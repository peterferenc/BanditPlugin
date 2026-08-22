using System.Collections.Generic;
using BanditPlugin.Navigation;
using SDG.Unturned;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.FakePlayer
{
    /// <summary>
    /// One bandit, one vehicle, one route - and nothing else running.
    ///
    /// The test harness a convoy is not. A column is several mechanisms at once: interval keeping,
    /// contact, dismounting, rallying, the leapfrog spawn, and route following underneath all of
    /// them. When it drives badly the first useful question is which of those is wrong, and the only
    /// way to ask it is to take the other five away. This drives the same route through the same
    /// <see cref="BanditVehicleDriver"/> and the same <see cref="BanditVehicleNavigator"/> that a
    /// convoy uses, and does nothing else at all - so anything it gets wrong is the driving.
    ///
    /// It also fixes what a convoy makes hard to see: with one vehicle there is nothing in front to
    /// brake for, nothing behind to be pushed by, and no crew to wait for, so the log is one
    /// vehicle's story rather than three interleaved.
    /// </summary>
    public sealed class BanditRouteDrive
    {
        /// <summary>Every drive still running. One per bandit; ordering a second replaces the
        /// first.</summary>
        private static readonly List<BanditRouteDrive> All = new List<BanditRouteDrive>();

        /// <summary>How near a route point counts as reached without having driven past it, and how
        /// near the last one counts as arrived. Both match the convoy's, so the two follow the same
        /// line.</summary>
        private const float PointReachedRadiusMetres = 5f;
        private const float ArriveRadiusMetres = 12f;

        /// <summary>
        /// How far out the vehicle starts slowing for the last point, and the slowest it is allowed
        /// to creep while doing it.
        ///
        /// Nothing was braking for the destination at all. The driver is handed a rolling aim point
        /// twelve metres up the road and told to go, so approaching the end looked exactly like
        /// approaching anywhere else - it arrived at thirteen metres a second and went straight
        /// past. Then it could never recover, because arriving was tested purely as "am I within
        /// twelve metres of the last point", which is false forever once you are eighty metres
        /// beyond it.
        /// </summary>
        private const float ApproachSlowdownMetres = 30f;
        private const float ApproachMinimumScale = 0.18f;

        private const float SteerLookaheadSeconds = 1f;
        private const float MinSteerLookaheadMetres = 6f;
        private const float MaxSteerLookaheadMetres = 14f;

        /// <summary>How many route points to step over when the navigator gives up, and how many
        /// times to allow that before the trip is called off.</summary>
        private const int SkipPointsOnGiveUp = 5;
        private const int MaxGiveUps = 3;

        private readonly BanditBotController _bandit;
        private readonly List<Vector3> _path;

        private int _target;
        private int _issued = -1;
        private int _giveUps;

        public bool Finished { get; private set; }

        private BanditRouteDrive(BanditBotController bandit, List<Vector3> path)
        {
            _bandit = bandit;
            _path = path;
        }

        /// <summary>
        /// Sends a seated bandit along a route through the given waypoints.
        ///
        /// Returns false with a sentence rather than throwing, because every reason this can fail is
        /// something the person typing the command needs to read: no waypoints recorded, the bandit
        /// is not driving anything, or the route could not be planned.
        /// </summary>
        public static bool Start(BanditBotController bandit, IReadOnlyList<Vector3> waypoints,
            bool useRoads, out string summary)
        {
            if (bandit == null || bandit.Driver == null || !bandit.Driver.IsSeated)
            {
                summary = "that bandit is not in a vehicle";
                return false;
            }

            InteractableVehicle vehicle = bandit.Driver.Vehicle;
            if (vehicle == null)
            {
                summary = "that bandit is not in a vehicle";
                return false;
            }

            List<Vector3> path = BanditConvoy.PlanRoute(vehicle.transform.position, waypoints, useRoads,
                out int roadLegs, out int directLegs);

            if (path.Count < 1)
            {
                summary = "the route came out empty";
                return false;
            }

            Stop(bandit);

            BanditRouteDrive drive = new BanditRouteDrive(bandit, path);
            All.Add(drive);
            BanditRouteDriveDirector.Ensure();

            summary = useRoads
                ? $"{path.Count} point(s), {roadLegs} leg(s) on road, {directLegs} direct"
                : $"{path.Count} point(s), roads off";

            return true;
        }

        /// <summary>Ends whatever this bandit was driving, and says whether there was anything to
        /// end.</summary>
        public static bool Stop(BanditBotController bandit)
        {
            for (int i = All.Count - 1; i >= 0; i--)
            {
                if (All[i]._bandit != bandit)
                {
                    continue;
                }

                All[i].Finished = true;
                All[i]._bandit?.Driver?.StopDriving();
                All.RemoveAt(i);
                return true;
            }

            return false;
        }

        public static int StopAll()
        {
            int stopped = All.Count;

            foreach (BanditRouteDrive drive in All)
            {
                drive.Finished = true;
                drive._bandit?.Driver?.StopDriving();
            }

            All.Clear();
            return stopped;
        }

        /// <summary>How far along it has got, for the command that started it.</summary>
        public static string Describe(BanditBotController bandit)
        {
            foreach (BanditRouteDrive drive in All)
            {
                if (drive._bandit == bandit)
                {
                    return $"route point {drive._target + 1}/{drive._path.Count}"
                        + (drive._giveUps > 0 ? $", {drive._giveUps} give-up(s)" : string.Empty);
                }
            }

            return null;
        }

        private void Tick()
        {
            BanditVehicleDriver driver = _bandit?.Driver;

            if (driver == null || !driver.IsSeated)
            {
                Finished = true;
                return;
            }

            InteractableVehicle vehicle = driver.Vehicle;
            if (vehicle == null || vehicle.isDead || vehicle.isExploded)
            {
                Finished = true;
                return;
            }

            Vector3 position = vehicle.transform.position;

            while (_target < _path.Count - 1 && HasPassed(position, _target))
            {
                _target++;
            }

            bool atLast = _target >= _path.Count - 1;
            Vector3 destination = _path[_path.Count - 1];
            float toEnd = Flat(position, destination);

            // Arrived by being near it, or by having driven past it. The second half matters: a
            // vehicle that overshoots is *closer to done* than one still approaching, and treating
            // it as still travelling sent it away down the road looking for a point behind it.
            if (atLast && (toEnd <= ArriveRadiusMetres || HasOvershot(position)))
            {
                driver.StopDriving();
                Finished = true;
                Logger.Log($"[Bandit] Route drive finished - {_path.Count} point(s), "
                    + $"{_giveUps} give-up(s), stopped {toEnd:0.0}m from the destination.");
                return;
            }

            if (driver.ConsumeGaveUp())
            {
                _giveUps++;

                if (_giveUps > MaxGiveUps)
                {
                    Logger.Log($"[Bandit] Route drive could not get through at point {_target}/"
                        + $"{_path.Count - 1}, ({position.x:0}, {position.z:0}) - stopping.");
                    driver.StopDriving();
                    Finished = true;
                    return;
                }

                BanditNavLog.Write(driver, $"route: gave up at point {_target}/{_path.Count - 1} "
                    + $"(attempt {_giveUps} of {MaxGiveUps}) - skipping ahead");

                _target = Mathf.Min(_target + SkipPointsOnGiveUp, _path.Count - 1);
                _issued = -1;
            }

            Vector3 aim = SteerTarget(driver, position);

            if (_issued != _target || !driver.HasDestination)
            {
                if (!driver.TrySetDestination(aim, out string reason))
                {
                    Logger.Log($"[Bandit] Route drive cannot drive the route ({reason}) - stopping.");
                    Finished = true;
                    return;
                }

                _issued = _target;
                BanditRouteDebug.CurrentTarget = aim;
                BanditNavLog.Write(driver, $"route: point {_target}/{_path.Count - 1}, "
                    + $"aiming ({aim.x:0}, {aim.z:0})");
            }

            // Braking for the end of the trip. Everywhere else full speed is right, because the
            // aim point is always well ahead; the last point is the one place where it is not.
            driver.SpeedScale = atLast
                ? Mathf.Max(ApproachMinimumScale, Mathf.Clamp01(toEnd / ApproachSlowdownMetres))
                : 1f;
        }

        /// <summary>
        /// Whether the vehicle is past the destination rather than short of it.
        ///
        /// Measured against the direction the route arrives from, so "past" means what it means to
        /// a driver: the destination is behind you now. Bounded, because a vehicle that is two
        /// hundred metres beyond it went somewhere else entirely and should still be trying.
        /// </summary>
        private bool HasOvershot(Vector3 position)
        {
            if (_path.Count < 2)
            {
                return false;
            }

            Vector3 destination = _path[_path.Count - 1];
            Vector3 approach = destination - _path[_path.Count - 2];
            approach.y = 0f;

            if (approach.sqrMagnitude < 0.01f)
            {
                return false;
            }

            Vector3 offset = position - destination;
            offset.y = 0f;

            return Vector3.Dot(offset, approach.normalized) > 0f
                && offset.magnitude <= ArriveRadiusMetres * 2f;
        }

        /// <summary>Whether the vehicle is on the far side of a route point, along the segment
        /// running to the next one. The same test the convoy uses.</summary>
        private bool HasPassed(Vector3 position, int index)
        {
            Vector3 point = _path[index];

            if (Flat(position, point) <= PointReachedRadiusMetres)
            {
                return true;
            }

            Vector3 segment = _path[index + 1] - point;
            segment.y = 0f;
            if (segment.sqrMagnitude < 0.01f)
            {
                return true;
            }

            Vector3 offset = position - point;
            offset.y = 0f;

            return Vector3.Dot(offset, segment.normalized) > 0f;
        }

        /// <summary>The point a fixed time up the road, interpolated along the polyline. Again the
        /// convoy's own, so what this harness proves carries over to a column.</summary>
        private Vector3 SteerTarget(BanditVehicleDriver driver, Vector3 position)
        {
            float lookahead = Mathf.Clamp(driver.Speed * SteerLookaheadSeconds,
                MinSteerLookaheadMetres, MaxSteerLookaheadMetres);

            int start = Mathf.Clamp(_target, 0, _path.Count - 1);
            Vector3 previous = _path[start];
            float previousDistance = Flat(position, previous);

            if (previousDistance >= lookahead || start >= _path.Count - 1)
            {
                return previous;
            }

            for (int i = start + 1; i < _path.Count; i++)
            {
                Vector3 candidate = _path[i];
                float distance = Flat(position, candidate);

                if (distance >= lookahead)
                {
                    float span = distance - previousDistance;
                    float t = span > 0.01f
                        ? Mathf.Clamp01((lookahead - previousDistance) / span)
                        : 1f;

                    return Vector3.Lerp(previous, candidate, t);
                }

                previous = candidate;
                previousDistance = distance;
            }

            return _path[_path.Count - 1];
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        /// <summary>The heartbeat, for the same reason a convoy has one: nothing here is attached to
        /// a game object vanilla is already ticking.</summary>
        private sealed class BanditRouteDriveDirector : MonoBehaviour
        {
            private static BanditRouteDriveDirector _instance;

            public static void Ensure()
            {
                if (_instance != null)
                {
                    return;
                }

                GameObject host = new GameObject("BanditRouteDriveDirector");
                DontDestroyOnLoad(host);
                _instance = host.AddComponent<BanditRouteDriveDirector>();
            }

            private void Update()
            {
                for (int i = All.Count - 1; i >= 0; i--)
                {
                    All[i].Tick();

                    if (All[i].Finished)
                    {
                        All.RemoveAt(i);
                    }
                }
            }
        }
    }
}
