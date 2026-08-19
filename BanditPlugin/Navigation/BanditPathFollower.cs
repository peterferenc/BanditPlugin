using System.Collections.Generic;
using Pathfinding;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.BanditGeometry;

namespace BanditPlugin.Navigation
{
    /// <summary>
    /// One A* route, from asking for it to walking off the end of it.
    ///
    /// The server is already running the A* Pathfinding Project - the game uses it for zombies -
    /// so a route is a matter of borrowing a Seeker off the bandit and handing it two points.
    /// Doing that correctly is not: the request is asynchronous, the returned Path is pooled and
    /// recycled the instant the callback returns, a destination can change while a request is in
    /// flight, and outside a Nav volume there is no navmesh to path on at all.
    ///
    /// Both navigators - the one that walks a bandit and the one that drives a vehicle - had solved
    /// all of that, separately and identically. The two copies of this code were the same to the
    /// character, and the vehicle one had lost every comment explaining why any of it was there.
    /// The only real difference is how far off the navmesh each is willing to snap, which is a
    /// number, so it is a field here rather than a second class.
    ///
    /// The owner keeps the destination; this keeps the route to it.
    /// </summary>
    public sealed class BanditPathFollower
    {
        /// <summary>How often a moving bot asks A* for a fresh path.</summary>
        public float RepathIntervalSeconds = 2.5f;

        /// <summary>
        /// How far a point may be from the navmesh and still snap onto it. Beyond it there is no
        /// route to ask for and the owner steers directly instead.
        /// </summary>
        public float NavmeshSnapDistance = 3f;

        /// <summary>How close to a corner counts as reaching it and moving on to the next.</summary>
        public float CornerArriveRadius = 1.5f;

        /// <summary>
        /// True when there is a route to follow, false when there is not - which is the common case
        /// out in the countryside, where the owner steers straight at the destination instead.
        /// </summary>
        public bool HasPath => _hasPath;

        /// <summary>
        /// How long a callback may go missing before the request is written off. The Seeker always
        /// calls back, but a path cancelled or pooled during a level change might not, and a
        /// request believed to be in flight forever would wedge pathing for good.
        /// </summary>
        private const float PathRequestTimeoutSeconds = 5f;

        private readonly Player _self;
        private readonly OnPathDelegate _onPathComplete;
        private readonly List<Vector3> _corners = new List<Vector3>();

        private Seeker _seeker;
        private bool _seekerUnavailable;
        private bool _hasPath;
        private int _cornerIndex;
        private bool _pathPending;
        private float _pathRequestedTime;
        private float _nextRepathTime;

        // Bumped whenever the destination changes. A path request in flight when that happens
        // still calls back, and without this its corners - computed for the old destination -
        // would be installed as the current route.
        private int _pathGeneration;
        private int _pendingPathGeneration = -1;

        public BanditPathFollower(Player self)
        {
            _self = self;
            _onPathComplete = OnPathComplete;
        }

        /// <summary>
        /// Throws the route away and asks for a fresh one on the next tick, for a new destination.
        /// </summary>
        public void Restart()
        {
            _pathGeneration++;
            DropRoute();
        }

        /// <summary>
        /// Throws the route away and asks for a fresh one on the next tick, for the *same*
        /// destination - the vehicle navigator does this when a direction has been proven not to
        /// work and it wants A* to find another way round.
        ///
        /// Deliberately does not bump the generation, matching what it did before this was lifted
        /// out: a request already in flight still installs its corners when it lands.
        /// </summary>
        public void DropRoute()
        {
            _hasPath = false;
            _corners.Clear();
            _cornerIndex = 0;
            _nextRepathTime = 0f;
        }

        /// <summary>
        /// Gives up on the trip: forgets the route and cancels anything in flight, so a callback
        /// arriving afterwards cannot reinstate it.
        /// </summary>
        public void Abandon()
        {
            _pathGeneration++;
            _hasPath = false;
            _corners.Clear();

            if (_seeker != null && _pathPending)
            {
                _seeker.CancelCurrentPathRequest();
                _pathPending = false;
            }
        }

        /// <summary>
        /// How far there still is to travel: the remaining corners summed end to end, or the
        /// straight line when there is no route.
        ///
        /// The distinction matters for anything deciding "is this trip long enough to be worth
        /// sprinting". Straight-line distance badly understates a route around a building, which is
        /// exactly the case where a bandit crossing open ground wants to be running.
        /// </summary>
        public float RemainingDistance(Vector3 position, Vector3 destination)
        {
            if (!_hasPath || _corners.Count == 0 || _cornerIndex >= _corners.Count)
            {
                return FlatDistance(position, destination);
            }

            float total = FlatDistance(position, _corners[_cornerIndex]);
            for (int i = _cornerIndex; i < _corners.Count - 1; i++)
            {
                total += FlatDistance(_corners[i], _corners[i + 1]);
            }
            return total;
        }

        /// <summary>
        /// The point to steer at: the next corner not yet reached, or the destination itself when
        /// there is no route. Advances past any corners already behind us.
        /// </summary>
        public Vector3 SteerTarget(Vector3 position, Vector3 destination)
        {
            if (!_hasPath || _corners.Count == 0)
            {
                return destination;
            }

            while (_cornerIndex < _corners.Count - 1
                && FlatDistance(position, _corners[_cornerIndex]) < CornerArriveRadius)
            {
                _cornerIndex++;
            }
            return _corners[_cornerIndex];
        }

        /// <summary>
        /// Requests a fresh path if one is due and both ends are actually on a navmesh. When they
        /// are not, <see cref="HasPath"/> stays false and the owner steers straight at the
        /// destination instead.
        /// </summary>
        public void Refresh(Vector3 position, Vector3 destination)
        {
            if (_pathPending)
            {
                if (Time.time - _pathRequestedTime > PathRequestTimeoutSeconds)
                {
                    _pathPending = false;
                }
                return;
            }

            if (Time.time < _nextRepathTime)
            {
                return;
            }
            _nextRepathTime = Time.time + RepathIntervalSeconds;

            if (AstarPath.active == null || AstarPath.active.isScanning)
            {
                _hasPath = false;
                return;
            }

            if (!TrySnapToNavmesh(position, out Vector3 start)
                || !TrySnapToNavmesh(destination, out Vector3 end))
            {
                _hasPath = false;
                _corners.Clear();
                return;
            }

            if (!EnsureSeeker())
            {
                _hasPath = false;
                return;
            }

            _pathPending = true;
            _pathRequestedTime = Time.time;
            _pendingPathGeneration = _pathGeneration;
            _seeker.StartPath(ABPath.Construct(start, end), _onPathComplete);
        }

        private void OnPathComplete(Path path)
        {
            _pathPending = false;

            if (_pendingPathGeneration != _pathGeneration)
            {
                return; // computed for a destination we have since abandoned
            }

            _corners.Clear();
            _cornerIndex = 0;

            // The path is pooled and recycled after this callback returns, so the corners have to
            // be copied out rather than referenced.
            if (path == null || path.error || path.vectorPath == null || path.vectorPath.Count == 0)
            {
                _hasPath = false;
                return;
            }

            for (int i = 0; i < path.vectorPath.Count; i++)
            {
                _corners.Add(path.vectorPath[i]);
            }
            _hasPath = true;
        }

        /// <summary>
        /// The Seeker on the bandit, adding one if it has none.
        ///
        /// A bandit on foot and the same bandit in a vehicle share it, which is safe because they
        /// never run at once - a seated bandit's brain is not ticked - so one path request is in
        /// flight at a time.
        /// </summary>
        private bool EnsureSeeker()
        {
            if (_seeker != null)
            {
                return true;
            }
            if (_seekerUnavailable || _self == null)
            {
                return false;
            }

            GameObject gameObject = _self.gameObject;
            _seeker = gameObject.GetComponent<Seeker>();
            if (_seeker == null)
            {
                _seeker = gameObject.AddComponent<Seeker>();
                _seeker.drawGizmos = false;

                // Raw A* corners run node centre to node centre, which on a recast graph zig-zags
                // badly. The funnel modifier string-pulls them into the straight line a player
                // would actually walk - the same modifier vanilla puts on zombies.
                gameObject.AddComponent<FunnelModifier>();
            }

            _seekerUnavailable = _seeker == null;
            return _seeker != null;
        }

        private bool TrySnapToNavmesh(Vector3 point, out Vector3 snapped)
        {
            snapped = point;

            NNInfo info = AstarPath.active.GetNearest(point, NNConstraint.Walkable);
            if (info.node == null)
            {
                return false;
            }

            snapped = info.position;
            return FlatDistance(snapped, point) <= NavmeshSnapDistance
                && Mathf.Abs(snapped.y - point.y) <= NavmeshSnapDistance + 2f;
        }
    }
}
