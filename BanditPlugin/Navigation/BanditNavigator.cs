using System.Collections.Generic;
using Pathfinding;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Navigation
{
    /// <summary>
    /// Turns "go to this world point" into a world-space direction the bot should walk in.
    ///
    /// Two layers, because neither one is enough on its own:
    ///
    /// 1. A* Pathfinding Project. The server really does run it - Unturned ships
    ///    AstarPathfindingProject.dll and UnturnedPathfinding_ASPFP.OnGameLevelInstantiated()
    ///    creates a live AstarPath singleton, one RecastGraph per Nav volume, which is what
    ///    zombies path on (Seeker + FunnelModifier + LegacyAIPathNoRedist). We borrow the same
    ///    Seeker + FunnelModifier but deliberately NOT the AIPath: AIPath drives transform
    ///    directly, which would fight PlayerMovement's CharacterController and desync the
    ///    position other clients see. We only want the corner list.
    /// 2. Direct steering with capsule-cast whiskers. Necessary because the navmesh only exists
    ///    *inside* Nav volumes - the towns where zombies spawn. A bandit walking the roads
    ///    between them is off-mesh most of the time, and A* would have nothing to say.
    ///
    /// Which layer is used is decided per repath by whether both endpoints snap onto a graph.
    /// </summary>
    public sealed class BanditNavigator
    {
        /// <summary>How close (horizontally) counts as having reached the destination.</summary>
        public float ArriveRadius = 2f;

        public float RepathIntervalSeconds = 2.5f;

        /// <summary>
        /// How far a point may be from the navmesh and still be considered "on" it. AstarPath's
        /// own maxNearestNodeDistance is 100m, which would happily snap a bandit standing in a
        /// field onto a graph in the next town, so we apply our own much tighter limit.
        /// </summary>
        public float NavmeshSnapDistance = 3f;

        public bool AllowJumping = true;

        /// <summary>Set while a path is being followed; consumed by the controller as keys[0].</summary>
        public bool WantsJump { get; private set; }

        /// <summary>World-space unit vector on the XZ plane, or zero when there is nothing to do.</summary>
        public Vector3 DesiredDirection { get; private set; }

        public Vector3 Destination { get; private set; }
        public bool HasDestination { get; private set; }

        /// <summary>Latched true on arrival; the brain reads and clears it.</summary>
        public bool HasArrived { get; private set; }

        /// <summary>Latched true when the bot has been wedged against something for too long.</summary>
        public bool HasGivenUp { get; private set; }

        /// <summary>
        /// True when the bot is following an A* path, false when it is steering directly because
        /// one end of the trip is off the navmesh. Reported by /banditstatus - which of the two is
        /// running explains most odd movement.
        /// </summary>
        public bool IsFollowingPath => _hasPath;

        private const float StepHeight = 0.5f;
        private const float JumpableObstacleHeight = 1f;
        private const float ObstacleProbeDistance = 1.6f;
        private const float CornerArriveRadius = 1.5f;

        // Vertical slack when deciding "am I there yet". Generous, because a navmesh corner or a
        // commanded destination can easily sit a floor above or below the point we can stand on.
        private const float ArriveHeightTolerance = 5f;

        private static readonly float[] AvoidanceAngles = { 35f, 65f, 90f };

        private readonly Player _self;
        private readonly Transform _transform;
        private readonly OnPathDelegate _onPathComplete;
        private readonly List<Vector3> _corners = new List<Vector3>();

        private float _activeArriveRadius;
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

        private int _avoidSign;
        private float _avoidSignExpiry;

        private Vector3 _lastStuckSamplePosition;
        private float _nextStuckSampleTime;
        private int _stuckSamples;
        private float _sidestepUntil;
        private int _sidestepSign = 1;

        public BanditNavigator(Player self)
        {
            _self = self;
            _transform = self.transform;
            _onPathComplete = OnPathComplete;
            _lastStuckSamplePosition = _transform.position;
        }

        /// <summary>
        /// <paramref name="arriveRadiusOverride"/> exists for short deliberate hops - stepping out
        /// from behind a tree to peek is under a metre, which the default two-metre arrive radius
        /// would report as "already there" before the bot took a single step.
        /// </summary>
        public void SetDestination(Vector3 destination, float? arriveRadiusOverride = null)
        {
            _activeArriveRadius = arriveRadiusOverride ?? ArriveRadius;
            Destination = destination;
            HasDestination = true;
            HasArrived = false;
            HasGivenUp = false;
            _pathGeneration++;
            _hasPath = false;
            _corners.Clear();
            _cornerIndex = 0;
            _nextRepathTime = 0f;
            _stuckSamples = 0;
            _sidestepUntil = 0f;
            _lastStuckSamplePosition = _transform.position;
            _nextStuckSampleTime = Time.time + 0.5f;
        }

        public void Stop()
        {
            HasDestination = false;
            _pathGeneration++;
            _hasPath = false;
            _corners.Clear();
            DesiredDirection = Vector3.zero;
            WantsJump = false;

            if (_seeker != null && _pathPending)
            {
                _seeker.CancelCurrentPathRequest();
                _pathPending = false;
            }
        }

        public bool ConsumeArrived()
        {
            bool arrived = HasArrived;
            HasArrived = false;
            return arrived;
        }

        public bool ConsumeGaveUp()
        {
            bool gaveUp = HasGivenUp;
            HasGivenUp = false;
            return gaveUp;
        }

        public void Tick(float deltaTime)
        {
            WantsJump = false;
            DesiredDirection = Vector3.zero;

            if (!HasDestination || _self == null || _self.life == null || _self.life.isDead)
            {
                return;
            }

            Vector3 position = _transform.position;

            if (FlatDistance(position, Destination) <= _activeArriveRadius
                && Mathf.Abs(position.y - Destination.y) <= ArriveHeightTolerance)
            {
                HasArrived = true;
                Stop();
                return;
            }

            RefreshPath(position);

            Vector3 steerTarget = Destination;
            if (_hasPath && _corners.Count > 0)
            {
                while (_cornerIndex < _corners.Count - 1
                    && FlatDistance(position, _corners[_cornerIndex]) < CornerArriveRadius)
                {
                    _cornerIndex++;
                }
                steerTarget = _corners[_cornerIndex];
            }

            Vector3 desired = Flatten(steerTarget - position);
            if (desired.sqrMagnitude < 0.0001f)
            {
                desired = Flatten(Destination - position);
            }
            if (desired.sqrMagnitude < 0.0001f)
            {
                return;
            }
            desired.Normalize();

            UpdateStuckDetection(position);

            // A sidestep overrides avoidance entirely: we already know the "sensible" direction is
            // where we got wedged, so probing around it again just re-picks the same wall.
            if (Time.time < _sidestepUntil)
            {
                DesiredDirection = Quaternion.Euler(0f, 70f * _sidestepSign, 0f) * desired;
                return;
            }

            DesiredDirection = AvoidObstacles(position, desired);
        }

        /// <summary>
        /// Requests a fresh A* path if one is due and both ends are actually on a navmesh.
        /// When they are not - the common case out in the countryside - _hasPath stays false and
        /// Tick steers straight at the destination instead.
        /// </summary>
        private void RefreshPath(Vector3 position)
        {
            if (_pathPending)
            {
                // A callback that never arrives would wedge pathing forever; the Seeker always
                // calls back, but a cancelled/pooled path during a level change might not.
                if (Time.time - _pathRequestedTime > 5f)
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

            if (!TrySnapToNavmesh(position, out Vector3 start) || !TrySnapToNavmesh(Destination, out Vector3 end))
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

        private bool EnsureSeeker()
        {
            if (_seeker != null)
            {
                return true;
            }
            if (_seekerUnavailable)
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

        /// <summary>
        /// Whisker steering: if the way ahead is blocked, fan out to either side until something
        /// is clear. The chosen side is remembered briefly so the bot hugs its way around an
        /// obstacle instead of oscillating between two equally-good detours in front of it.
        /// </summary>
        private Vector3 AvoidObstacles(Vector3 position, Vector3 desired)
        {
            if (IsDirectionClear(position, desired, StepHeight))
            {
                if (Time.time > _avoidSignExpiry)
                {
                    _avoidSign = 0;
                }
                return desired;
            }

            // Blocked low but clear higher up means a fence, a log or a rock lip - jumpable.
            if (AllowJumping && IsDirectionClear(position, desired, JumpableObstacleHeight))
            {
                WantsJump = true;
                return desired;
            }

            int firstSign = _avoidSign != 0 && Time.time <= _avoidSignExpiry ? _avoidSign : 1;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int sign = attempt == 0 ? firstSign : -firstSign;
                foreach (float angle in AvoidanceAngles)
                {
                    Vector3 candidate = Quaternion.Euler(0f, angle * sign, 0f) * desired;
                    if (IsDirectionClear(position, candidate, StepHeight))
                    {
                        _avoidSign = sign;
                        _avoidSignExpiry = Time.time + 1.5f;
                        return candidate;
                    }
                }
            }

            // Boxed in on every side. Keep pushing; the stuck detector will sidestep shortly.
            return desired;
        }

        /// <summary>
        /// Sweeps the player's own capsule along a direction. The bottom of the capsule starts at
        /// <paramref name="floorClearance"/> so that kerbs and stairs the CharacterController will
        /// step over on its own don't read as walls.
        /// </summary>
        private bool IsDirectionClear(Vector3 position, Vector3 direction, float floorClearance)
        {
            float radius = PlayerStance.RADIUS * 0.9f;
            Vector3 bottom = position + Vector3.up * (floorClearance + radius);
            Vector3 top = position + Vector3.up * (PlayerMovement.HEIGHT_STAND - radius);
            if (top.y <= bottom.y)
            {
                top = bottom + Vector3.up * 0.05f;
            }

            // The player layer is not in BLOCK_COLLISION, so the bot's own controller - and any
            // other player - is invisible to this sweep. Bodies shouldn't stop a bandit walking.
            return !Physics.CapsuleCast(bottom, top, radius, direction.normalized, ObstacleProbeDistance,
                RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Same idea as vanilla Zombie.isStuck: if we asked to move and the body didn't, something
        /// the whiskers can't see is in the way (a doorway edge, a slope, another player). Sidestep
        /// for a moment, and after enough failed samples give up so the brain can pick a new goal.
        /// </summary>
        private void UpdateStuckDetection(Vector3 position)
        {
            if (Time.time < _nextStuckSampleTime)
            {
                return;
            }
            _nextStuckSampleTime = Time.time + 0.5f;

            float moved = FlatDistance(position, _lastStuckSamplePosition);
            _lastStuckSamplePosition = position;

            if (moved > 0.2f)
            {
                _stuckSamples = 0;
                return;
            }

            _stuckSamples++;

            if (_stuckSamples == 2)
            {
                _sidestepSign = UnityEngine.Random.value < 0.5f ? -1 : 1;
                _sidestepUntil = Time.time + 0.8f;
                WantsJump = AllowJumping;
            }
            else if (_stuckSamples >= 6)
            {
                HasGivenUp = true;
                Stop();
            }
        }

        private static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return (a - b).magnitude;
        }
    }
}
