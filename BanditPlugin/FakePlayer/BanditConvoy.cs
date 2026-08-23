using System.Collections.Generic;
using BanditPlugin.Navigation;
using SDG.Unturned;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.FakePlayer
{
    /// <summary>
    /// A column of crewed vehicles driving a route, fighting what it meets on the way, and picking
    /// its infantry back up afterwards.
    ///
    /// The difference between this and the vehicles a plain "/banditevent" spawns is what they are
    /// *for*. Those are an ambush: they sit until the event sees somebody, drive at them, and empty
    /// out on top of them - the destination is the enemy. A convoy has somewhere to be, and the
    /// enemy is an interruption. So the route comes first and everything else is an interruption of
    /// it, which is what the state machine below says:
    ///
    ///     Cruise    following the route at speed, holding interval
    ///     Contact   riders out and fighting, vehicles crawling and shooting, route still running
    ///     Rallying  threat gone, vehicles stopped, riders walking back to their seats
    ///     Arrived   at the last waypoint. They stop, and they stay.
    ///
    /// Movement itself is not reimplemented here. Each vehicle still drives with the same
    /// <see cref="BanditVehicleDriver"/> a "/banditvgoto" uses, and is simply handed the next point
    /// on the route as its destination - so the width sweep, the reverse-out-of-trouble logic, the
    /// ride-height calibration and both server validation clamps all keep working, and a convoy is
    /// exactly as good at getting round a fallen tree as a single vehicle is. What this adds on top
    /// is which point to hand it next, how fast to take it, and when to stop driving and fight.
    /// </summary>
    public sealed class BanditConvoy
    {
        /// <summary>Every convoy still running.</summary>
        public static readonly List<BanditConvoy> All = new List<BanditConvoy>();

        private static int _nextId = 1;

        public enum ConvoyState
        {
            /// <summary>Putting itself on the ground one vehicle at a time. See <see cref="TickForming"/>.</summary>
            Forming,
            Cruise,
            Contact,
            Rallying,
            Arrived
        }

        /// <summary>Which step of the leapfrog the column is on while it forms up.</summary>
        private enum FormPhase
        {
            /// <summary>Put the next vehicle down on the start line.</summary>
            Spawn,

            /// <summary>Wait for the crew that just spawned to climb aboard.</summary>
            Board,

            /// <summary>Roll the column forward far enough to clear the start line again.</summary>
            Advance,

            /// <summary>Stand still for a moment so the column settles before the next one lands.</summary>
            Hold,

            /// <summary>The last vehicle is down. Wait for the whole column - not just that one - to
            /// be crewed and at rest before anybody drives anywhere.</summary>
            Settle,

            /// <summary>Formed up and about to leave. The two-second pause before it does.</summary>
            Depart
        }

        /// <summary>
        /// Puts one vehicle on the ground and returns it, or null if it could not be spawned.
        ///
        /// A delegate rather than a list of vehicle types because everything a spawn needs - the
        /// team, the budget it came out of, the crew kits, the event it belongs to - lives in the
        /// command that drew it, and none of that is a convoy's business. The convoy's business is
        /// only *when*.
        /// </summary>
        public delegate BanditEvent.Ride VehicleFactory(Vector3 spot, float facing);


        /// <summary>
        /// How far ahead along the route a vehicle steers.
        ///
        /// Separate from which point it has *reached*, and that separation is the point. Route
        /// points come eight metres apart; aiming at the next one it has not yet reached means
        /// aiming at something that may be half a metre away, which is a full-lock steering input
        /// for no reason. Aiming a fixed distance up the road instead is how the road gets followed
        /// rather than connected dot to dot.
        /// </summary>
        /// <summary>
        /// Scaled with speed rather than fixed, and clamped at both ends.
        ///
        /// A fixed twelve metres is too far for a ninety-degree turn - the aim point is already
        /// round the corner while the vehicle is still short of it, so it starts the turn late, cuts
        /// the inside and runs wide - and too near for a straight, where a close target is what
        /// makes a vehicle chase small errors. Tying it to speed gets both: the vehicle slows for
        /// the corner (the clearance probe sees the far side of the junction), the aim point comes
        /// in with it, and the turn is driven tightly.
        /// </summary>
        private const float SteerLookaheadSeconds = 1f;
        private const float MinSteerLookaheadMetres = 8f;
        private const float MaxSteerLookaheadMetres = 14f;

        /// <summary>How near the moving steer carrot counts as reached - small, because it is a
        /// carrot and not the destination. See BanditRouteDrive for why a large one freezes the
        /// vehicle.</summary>
        private const float CarrotArriveRadiusMetres = 3f;

        /// <summary>How near the final waypoint counts as arrived. A convoy stops in the area, not
        /// on the pixel.</summary>
        private const float ArriveRadiusMetres = 12f;

        /// <summary>How near a route point counts as reached without having driven past it. See
        /// <see cref="HasPassed"/>.</summary>
        private const float PointReachedRadiusMetres = 5f;

        /// <summary>How far out the column starts slowing for its last waypoint, and the slowest it
        /// creeps while doing it.</summary>
        private const float ApproachSlowdownMetres = 30f;
        private const float ApproachMinimumScale = 0.18f;

        /// <summary>
        /// The interval a following vehicle tries to keep, and the distance at which it stops rather
        /// than closes. Measured bumper to bumper - see <see cref="ClosestGapAhead"/> - so these are
        /// lengths of clear road, not distances between origins, and they mean the same thing for a
        /// tank as for a hatchback.
        /// </summary>
        private const float DesiredGapMetres = 10f;
        private const float MinimumGapMetres = 5f;

        /// <summary>
        /// How far the vehicle behind may drop back before this one eases off for it, and the gap at
        /// which it is down to its slowest.
        ///
        /// A column only stays a column if the waiting is mutual. Followers were already braking for
        /// what was in front of them and nothing at all was looking backwards, so every evasion,
        /// three-point turn or slow squeeze past an obstruction was paid for entirely by the vehicle
        /// that made it - the head of the column drove on at full speed and the tail was still
        /// fighting an obstacle a quarter of a mile back. Taking the interval from behind as well
        /// closes that: the leader lifts off, the gap comes back, and the leader gets going again.
        ///
        /// It works vehicle to vehicle rather than head to tail, so it propagates - the second waits
        /// for the third, the first waits for the second - and a five-vehicle column is not forced to
        /// bunch into one interval's worth of road.
        /// </summary>
        private const float TailGapMetres = 10f;
        private const float TailStretchedGapMetres = 35f;

        /// <summary>
        /// The slowest a vehicle is held down to while waiting for the one behind it.
        ///
        /// Never zero, and that is the point of it: a column that stops to wait is a column parked on
        /// a road, which is the one thing a convoy must not be. It keeps rolling, slowly enough that
        /// the tail gains on it and fast enough that a vehicle which is never coming - burning,
        /// wedged, or crewless - costs the rest a slow mile rather than the trip.
        /// </summary>
        private const float TailWaitMinimumScale = 0.4f;

        /// <summary>How far to either side still counts as being in front. Wide enough to cover a
        /// vehicle in the same lane through a bend, narrow enough to ignore oncoming traffic.</summary>
        private const float LaneHalfWidthMetres = 3.5f;

        /// <summary>How fast the column moves while it is fighting, as a fraction of its cruise.
        /// Slow enough to shoot from and to let the dismounted infantry keep up, not a halt - a
        /// stopped convoy is a stationary target, which is the one thing a convoy must not be.</summary>
        private const float ContactSpeedScale = 0.3f;

        /// <summary>
        /// How near a hostile has to come to start a fight the crew cannot see coming.
        ///
        /// A bandit inside a hull often has no line of sight out of it, so contact normally arrives
        /// from the squads - whoever can actually see. This is the backstop for the case where the
        /// whole convoy is buttoned up and somebody steps out of the trees beside it.
        /// </summary>
        private const float ContactTriggerRadiusMetres = 140f;

        /// <summary>How long after the last sighting the convoy decides the fight is over.</summary>
        private const float ContactMemorySeconds = 12f;

        /// <summary>How near its vehicle a dismounted rider has to be before it asks for its seat
        /// back, and how long the column waits for stragglers before leaving without them.</summary>
        private const float RemountRadiusMetres = 6f;
        private const float RallyTimeoutSeconds = 60f;

        /// <summary>How far a vehicle has to have moved before the men walking back to it are told
        /// again where it is.</summary>
        private const float RallyReorderDistanceSquared = 9f;

        /// <summary>
        /// What a vehicle does when the navigator gives up: skip a few route points and try again,
        /// three times, then stop being a vehicle.
        ///
        /// Giving up means the way ahead does not fit, and on a road that usually means one
        /// obstruction rather than a route that was never drivable - so stepping over it and picking
        /// the road up again beyond is worth trying before writing the vehicle off. Bounded, because
        /// a convoy that skips its way to the destination through a cliff has not arrived anywhere.
        /// </summary>
        private const int SkipPointsOnGiveUp = 5;
        private const int MaxGiveUps = 3;

        /// <summary>How long the column waits for one vehicle's crew to climb aboard before it
        /// gives up on the stragglers and spawns the next. See <see cref="TickForming"/>.</summary>
        private const float BoardTimeoutSeconds = 20f;

        /// <summary>How long the leapfrog is allowed to spend rolling the column clear of the start
        /// line before it spawns the next vehicle anyway. A backstop for a column that has driven
        /// into something on its first ten metres, not a normal outcome.</summary>
        private const float AdvanceTimeoutSeconds = 20f;

        /// <summary>The pause the whole column takes between one vehicle joining and the next
        /// landing. Two seconds, as asked for: long enough that the vehicles have actually come to
        /// rest before something is dropped in behind them.</summary>
        private const float FormHoldSeconds = 2f;

        /// <summary>How fast the column moves while it is shuffling forward to make room. Slow, on
        /// purpose - this is a manoeuvre in a confined space with a vehicle about to appear in
        /// it.</summary>
        private const float FormUpSpeedScale = 0.35f;

        /// <summary>How long the column waits for the whole of itself to be crewed and stopped
        /// before it leaves anyway. See <see cref="IsColumnReady"/>.</summary>
        private const float SettleTimeoutSeconds = 15f;

        /// <summary>How slow counts as stopped. A vehicle that has just been dropped is still
        /// settling on its suspension, and a column that leaves during that is a column whose tail
        /// is bouncing.</summary>
        private const float AtRestMetresPerSecond = 0.5f;

        /// <summary>How much further than the bare spacing the column will roll while waiting for
        /// the start line to physically clear.</summary>
        private const float FormClearanceSlackMetres = 10f;

        /// <summary>How far from a road either end of a leg may be before it is driven direct.</summary>
        public const float RoadSnapDistanceMetres = 80f;

        public int Id { get; }
        public ConvoyState State { get; private set; } = ConvoyState.Cruise;

        /// <summary>The event that spawned it - the source of both the vehicles and the contact
        /// reports, since every crew is a squad of the event's.</summary>
        public BanditEvent Event { get; }

        /// <summary>Whether the route was planned along roads, and how much of it is on them.</summary>
        public bool UsesRoads { get; }
        public int RoadPointCount { get; }
        public int PointCount => _path.Count;

        private readonly List<RoutePoint> _path;

        /// <summary>How far along the route each point is, measured from the start. Fixed for the
        /// life of the convoy, and the yardstick the column keeps its interval against: how far
        /// apart two vehicles are *along the road* is not how far apart they are in a straight line,
        /// and on a hairpin the two answers differ by the whole of the bend.</summary>
        private readonly List<float> _alongRoute = new List<float>();

        private readonly List<Element> _elements = new List<Element>();
        private float _lastContactTime = float.MinValue;
        private float _rallyDeadline;

        /// <summary>The start line: where every vehicle in the column is put down, one after
        /// another, and the heading it is put down facing.</summary>
        private Vector3 _spawnPoint;
        private float _spawnFacing;
        private Vector3 _spawnTravel = Vector3.forward;

        /// <summary>The vehicles still to be spawned, in column order - the head of the column
        /// first, because the head is what drives forward to make room for the rest.</summary>
        private readonly Queue<VehicleFactory> _pending = new Queue<VehicleFactory>();

        /// <summary>How much room the column leaves on the start line for the next vehicle.</summary>
        private float _formSpacing = 20f;

        private FormPhase _formPhase = FormPhase.Spawn;
        private float _formPhaseDeadline;

        /// <summary>
        /// Men whose own vehicle is not going anywhere any more - it burned, or its driver was
        /// killed - and who are now walking. They ride on in somebody else's spare seat. See
        /// <see cref="TickOrphans"/>.
        /// </summary>
        private readonly List<BanditBotController> _orphans = new List<BanditBotController>();
        private readonly Dictionary<BanditBotController, Vector3> _orphanOrders =
            new Dictionary<BanditBotController, Vector3>();

        /// <summary>One point on the planned route. A road point knows which road node it came from,
        /// so each vehicle can pick its own lane through it; a waypoint the player recorded is
        /// driven straight at.</summary>
        private struct RoutePoint
        {
            public Vector3 Position;
            public int NodeIndex;

            /// <summary>What kind of point this is, purely so <see cref="BanditRouteDebug"/> can
            /// colour it: a road node, a point on a rounded corner, or a waypoint that was asked
            /// for.</summary>
            public BanditRouteDebug.MarkerKind Kind;
        }

        /// <summary>One vehicle in the column, and where it has got to.</summary>
        private sealed class Element
        {
            public BanditEvent.Ride Ride;

            /// <summary>Index into the route of the point this vehicle is currently driving at.</summary>
            public int Target;

            /// <summary>The route point last written to the nav log, so the commentary says
            /// something once per point rather than once per tick. The aim point itself is slid
            /// along every tick - see TickElement.</summary>
            public int IssuedTarget = -1;

            public bool Finished;

            /// <summary>Finished *because it got there*, as opposed to having been destroyed,
            /// abandoned or wedged. Only so the log tells the truth about what happened.</summary>
            public bool Arrived;

            public int GiveUps;

            /// <summary>Which seat each rider came out of, so it goes back into the same one.</summary>
            public readonly Dictionary<BanditBotController, byte> RiderSeats =
                new Dictionary<BanditBotController, byte>();

            /// <summary>
            /// Where each rider was last told to walk back to.
            ///
            /// Kept because BanditNavigator.SetDestination throws the current path away and forces
            /// a repath - so a "walk to the vehicle" order re-issued every tick would clear the
            /// path every tick and the man would stand still being ordered to move. The order is
            /// only repeated when the vehicle has actually moved out from under it.
            /// </summary>
            public readonly Dictionary<BanditBotController, Vector3> RallyOrders =
                new Dictionary<BanditBotController, Vector3>();
        }

        private BanditConvoy(BanditEvent banditEvent, List<RoutePoint> path, bool usesRoads)
        {
            Id = _nextId++;
            Event = banditEvent;
            State = ConvoyState.Forming;
            _path = path;
            UsesRoads = usesRoads;

            float along = 0f;
            for (int i = 0; i < path.Count; i++)
            {
                if (i > 0)
                {
                    along += HorizontalDistance(path[i - 1].Position, path[i].Position);
                }

                _alongRoute.Add(along);

                if (path[i].NodeIndex >= 0)
                {
                    RoadPointCount++;
                }
            }

            All.Add(this);
            BanditConvoyDirector.Ensure();
        }

        public static void ClearAll()
        {
            All.Clear();
        }

        /// <summary>
        /// Removes the most recently spawned convoy: its vehicles destroyed, its men despawned, and
        /// the column itself taken off the director's list so nothing keeps steering it.
        ///
        /// The last one rather than all of them, because the reason to reach for this is that the
        /// convoy you just spawned is doing something you did not want to watch any longer, and
        /// there may well be another one running that you do. /banditclear is still the big hammer.
        /// </summary>
        public static bool ClearLast(out string summary)
        {
            if (All.Count == 0)
            {
                summary = "no convoy is running";
                return false;
            }

            BanditConvoy convoy = All[All.Count - 1];
            All.RemoveAt(All.Count - 1);

            int vehicles = 0;
            int men = 0;

            foreach (Element element in convoy._elements)
            {
                // Everyone who belongs to this vehicle, wherever they currently are - in a seat, out
                // fighting, or walking back to it.
                foreach (BanditBotController rider in element.Ride.Riders)
                {
                    men += Remove(rider) ? 1 : 0;
                }

                foreach (BanditBotController gunner in element.Ride.Gunners)
                {
                    men += Remove(gunner) ? 1 : 0;
                }

                men += Remove(element.Ride.Driver) ? 1 : 0;

                InteractableVehicle vehicle = element.Ride.Vehicle;
                if (vehicle != null && !vehicle.isExploded)
                {
                    VehicleManager.askVehicleDestroy(vehicle);
                    vehicles++;
                }
            }

            // Men off a burnt-out vehicle are still this convoy's, even though their ride is gone.
            foreach (BanditBotController orphan in convoy._orphans)
            {
                men += Remove(orphan) ? 1 : 0;
            }

            convoy._elements.Clear();
            convoy._pending.Clear();
            convoy._orphans.Clear();
            convoy._orphanOrders.Clear();
            convoy.State = ConvoyState.Arrived;

            summary = $"convoy {convoy.Id}: {vehicles} vehicle(s) and {men} bandit(s) removed";
            return true;
        }

        /// <summary>Despawns one bandit, if it is still there to despawn.</summary>
        private static bool Remove(BanditBotController bandit)
        {
            if (bandit == null || bandit.Self == null)
            {
                return false;
            }

            SteamPlayer steamPlayer = bandit.SteamPlayerToKeepAlive;
            if (steamPlayer == null)
            {
                return false;
            }

            FakePlayerSpawner.DespawnBot(steamPlayer);
            return true;
        }

        /// <summary>
        /// Plans a route through every waypoint in order and starts a column forming up on the
        /// first one.
        ///
        /// Nothing is on the ground yet when this returns. The vehicles are handed over as factories
        /// and spawned one at a time from <see cref="TickForming"/>, which is the whole point: a
        /// column dropped all at once has to be laid out by guessing where each vehicle will fit,
        /// and the guess is what put lorries in living rooms. Spawning one, driving it forward, and
        /// then spawning the next into the space it has just physically vacated needs no guess at
        /// all - the ground is known to be clear because a vehicle was standing on it a moment ago.
        ///
        /// Returns null when there is no route worth driving, rather than spawning a convoy that
        /// reports having arrived on its first tick.
        /// </summary>
        public static BanditConvoy Create(BanditEvent banditEvent, IReadOnlyList<VehicleFactory> vehicles,
            Vector3 start, Vector3 travel, IReadOnlyList<Vector3> waypoints, bool useRoads, float spacing,
            out string summary)
        {
            List<RoutePoint> path = BuildPath(start, waypoints, useRoads, out int roadLegs, out int directLegs);

            if (path.Count < 1)
            {
                summary = "the route came out empty";
                return null;
            }

            BanditConvoy convoy = new BanditConvoy(banditEvent, path, useRoads);

            // Faced along the route that was actually planned, not along the straight line to the
            // next waypoint. Those are frequently not the same thing and occasionally opposite: the
            // road leaving the start point can run the other way before it turns, so the first
            // point of the road route sits *behind* a column faced at the waypoint. The vehicles
            // then found their destination a hundred and twenty degrees off the nose, decided it
            // was behind them, and reversed - which is the "they weren't even going in the right
            // direction" run, and the log has it at err=-56 REV from the first packet.
            Vector3 routed = FirstLegDirection(path, start);
            if (routed.sqrMagnitude > 0.0001f)
            {
                travel = routed;
            }

            travel.y = 0f;
            if (travel.sqrMagnitude < 0.0001f)
            {
                travel = Vector3.forward;
            }

            convoy._spawnPoint = start;
            convoy._spawnTravel = travel.normalized;
            convoy._spawnFacing = Mathf.Atan2(convoy._spawnTravel.x, convoy._spawnTravel.z) * Mathf.Rad2Deg;
            convoy._formSpacing = Mathf.Max(10f, spacing);

            foreach (VehicleFactory factory in vehicles)
            {
                if (factory != null)
                {
                    convoy._pending.Enqueue(factory);
                }
            }

            summary = useRoads
                ? $"{roadLegs} leg(s) on road, {directLegs} direct"
                : $"{path.Count} point(s), roads off";

            return convoy;
        }

        /// <summary>
        /// Which way the planned route actually leaves the start point.
        ///
        /// Taken from the first route point far enough away to have a meaningful direction, rather
        /// than from the very first one: road nodes are eight metres apart and the nearest of them
        /// is often more or less underneath the column, where the direction is noise.
        /// </summary>
        private static Vector3 FirstLegDirection(List<RoutePoint> path, Vector3 start)
        {
            const float MeaningfulDistance = 10f;

            foreach (RoutePoint point in path)
            {
                Vector3 offset = point.Position - start;
                offset.y = 0f;

                if (offset.sqrMagnitude >= MeaningfulDistance * MeaningfulDistance)
                {
                    return offset.normalized;
                }
            }

            return Vector3.zero;
        }

        /// <summary>
        /// Turns the recorded waypoints into a list of points a vehicle can be aimed at, one after
        /// another.
        ///
        /// Each leg is routed over the road graph when asked for and when both ends are near enough
        /// to a road; whatever the graph returns is followed by the waypoint itself, so the column
        /// leaves the road to actually visit the point rather than driving past the nearest layby to
        /// it. A leg that cannot be routed - no road at either end, or two places with no road
        /// between them at all - is driven direct, which is exactly what a convoy with roads turned
        /// off does everywhere.
        /// </summary>
        /// <summary>
        /// The planned route as bare points, for anything that wants to drive the same line a convoy
        /// would without being a convoy. See <see cref="BanditRouteDrive"/>.
        /// </summary>
        public static List<Vector3> PlanRoute(Vector3 start, IReadOnlyList<Vector3> waypoints,
            bool useRoads, out int roadLegs, out int directLegs)
        {
            List<RoutePoint> path = BuildPath(start, waypoints, useRoads, out roadLegs, out directLegs);
            List<Vector3> points = new List<Vector3>(path.Count);

            foreach (RoutePoint point in path)
            {
                points.Add(point.Position);
            }

            return points;
        }

        private static List<RoutePoint> BuildPath(Vector3 start, IReadOnlyList<Vector3> waypoints,
            bool useRoads, out int roadLegs, out int directLegs)
        {
            List<RoutePoint> path = new List<RoutePoint>();
            List<int> nodes = new List<int>();

            roadLegs = 0;
            directLegs = 0;

            Vector3 cursor = start;

            foreach (Vector3 waypoint in waypoints)
            {
                bool routed = false;
                string reason = null;

                if (useRoads && BanditRoadGraph.TryRoute(cursor, waypoint, RoadSnapDistanceMetres, nodes, out reason))
                {
                    foreach (int node in nodes)
                    {
                        BanditRoadGraph.RoadNode roadNode = BanditRoadGraph.Get(node);
                        if (roadNode == null)
                        {
                            continue;
                        }

                        // Consecutive legs share the junction they meet at, and a route that
                        // doubles back on its own last point makes a vehicle brake for nothing.
                        if (path.Count > 0 && (path[path.Count - 1].Position - roadNode.Position).sqrMagnitude < 4f)
                        {
                            continue;
                        }

                        path.Add(new RoutePoint
                        {
                            Position = roadNode.Position,
                            NodeIndex = node,
                            Kind = BanditRouteDebug.MarkerKind.RoadPoint
                        });
                    }

                    routed = true;
                    roadLegs++;
                }
                else if (useRoads)
                {
                    Logger.Log($"[Bandit] Convoy leg to ({waypoint.x:0}, {waypoint.z:0}) is off-road: {reason}.");
                }

                if (!routed)
                {
                    directLegs++;
                }

                path.Add(new RoutePoint
                {
                    Position = waypoint,
                    NodeIndex = -1,
                    Kind = BanditRouteDebug.MarkerKind.Waypoint
                });

                cursor = waypoint;
            }

            List<RoutePoint> rounded = RoundCorners(path);
            RecordForDebug(rounded);
            return rounded;
        }

        /// <summary>Keeps the finished plan where "/banditnavlog route" can draw it.</summary>
        private static void RecordForDebug(List<RoutePoint> path)
        {
            List<BanditRouteDebug.Marker> markers = new List<BanditRouteDebug.Marker>(path.Count);

            foreach (RoutePoint point in path)
            {
                markers.Add(new BanditRouteDebug.Marker { Position = point.Position, Kind = point.Kind });
            }

            BanditRouteDebug.SetPlan(markers);
        }

        /// <summary>How far back from a corner the turn starts, and how sharp a corner has to be
        /// before it is worth rounding at all.</summary>
        private const float CornerRadiusMetres = 10f;
        private const float CornerMinDegrees = 12f;

        /// <summary>How many points an arc is drawn with. Enough that the steering lookahead always
        /// has one to interpolate between, few enough not to bloat a cross-map route.</summary>
        private const int CornerSegments = 5;

        /// <summary>
        /// Replaces every sharp corner in the route with an arc through it.
        ///
        /// A road route is a list of node positions, and a junction is one node where two straight
        /// runs meet at a right angle. Driving that literally means aiming at the corner itself,
        /// which is not where the vehicle should ever be: it arrives pointing the wrong way, turns
        /// on the spot, and in the meantime its nose has cut across whatever is on the inside of the
        /// bend. On the Stratford crossroads that is the pavement, the kerb and a lamp post.
        ///
        /// The arc is a quadratic Bezier with the corner itself as the control point, starting and
        /// ending ten metres back along each straight. That curve leaves the first road tangentially,
        /// joins the second tangentially, and passes through the midpoint between the corner and the
        /// straight line joining its two ends - which is the line a driver actually takes through a
        /// junction, and it stays inside the carriageway the whole way round.
        /// </summary>
        private static List<RoutePoint> RoundCorners(List<RoutePoint> path)
        {
            if (path.Count < 3)
            {
                return path;
            }

            List<RoutePoint> rounded = new List<RoutePoint>(path.Count + 8) { path[0] };

            for (int i = 1; i < path.Count - 1; i++)
            {
                Vector3 corner = path[i].Position;
                Vector3 incoming = corner - path[i - 1].Position;
                Vector3 outgoing = path[i + 1].Position - corner;

                incoming.y = 0f;
                outgoing.y = 0f;

                if (incoming.sqrMagnitude < 0.01f || outgoing.sqrMagnitude < 0.01f)
                {
                    continue;
                }

                float turn = Vector3.Angle(incoming, outgoing);
                if (turn < CornerMinDegrees)
                {
                    rounded.Add(path[i]); // near enough straight; leave it alone
                    continue;
                }

                // Never more than half of either straight, or two arcs on a short link would
                // overlap and the route would double back through itself.
                float back = Mathf.Min(CornerRadiusMetres,
                    Mathf.Min(incoming.magnitude, outgoing.magnitude) * 0.5f);

                Vector3 start = corner - incoming.normalized * back;
                Vector3 end = corner + outgoing.normalized * back;

                for (int step = 0; step <= CornerSegments; step++)
                {
                    float t = (float)step / CornerSegments;
                    float inverse = 1f - t;

                    Vector3 point = inverse * inverse * start
                        + 2f * inverse * t * corner
                        + t * t * end;

                    // The arc belongs to the road it is turning on, so it keeps the corner's node
                    // index - only /banditroads and the "how much of this route is on tarmac" count
                    // read that.
                    rounded.Add(new RoutePoint
                    {
                        Position = point,
                        NodeIndex = path[i].NodeIndex,
                        Kind = BanditRouteDebug.MarkerKind.CornerArc
                    });
                }
            }

            rounded.Add(path[path.Count - 1]);
            return rounded;
        }

        /// <summary>What the convoy is doing, for the command that spawned it and for /banditstatus.</summary>
        public string Describe()
        {
            int running = 0;
            foreach (Element element in _elements)
            {
                if (!element.Finished)
                {
                    running++;
                }
            }

            return $"convoy {Id}: {State}, {running}/{_elements.Count} vehicle(s) running"
                + (_pending.Count > 0 ? $", {_pending.Count} still to spawn" : string.Empty)
                + $", {PointCount} route point(s)";
        }

        private void Tick()
        {
            if (State == ConvoyState.Arrived)
            {
                return;
            }

            bool forming = State == ConvoyState.Forming;

            if (forming)
            {
                // Contact is deliberately not read while the column is still arriving. A convoy
                // half of which does not exist yet cannot do anything useful about being shot at:
                // ordering the riders out takes them out of the seats they are in the middle of
                // climbing into, and the column then waits for men to be aboard who it has just
                // told to get out. Whoever is already here still shoots back - the gunners in the
                // turrets and every crew squad are weapons free from the moment they spawn.
                TickForming();
            }
            else
            {
                UpdateContact();
            }

            bool allFinished = true;

            for (int i = 0; i < _elements.Count; i++)
            {
                Element element = _elements[i];
                if (!element.Finished)
                {
                    TickElement(element, i);
                }

                allFinished &= element.Finished;
            }

            if (allFinished && !forming)
            {
                State = ConvoyState.Arrived;

                int arrived = 0;
                foreach (Element element in _elements)
                {
                    if (element.Arrived)
                    {
                        arrived++;
                    }
                }

                Logger.Log($"[Bandit] Convoy {Id} is done - {arrived} of {_elements.Count} vehicle(s) "
                    + "made it to the last waypoint.");
            }
        }

        /// <summary>
        /// Builds the column on the start line, one vehicle at a time, leapfrogging forward to make
        /// room for the next.
        ///
        /// The cycle is: put a vehicle down on the start line, wait for its crew to get in, roll the
        /// whole column forward until the start line is empty again, everybody stops for a couple of
        /// seconds, put the next one down. Repeat until there are none left, and then the convoy is
        /// a convoy and drives.
        ///
        /// This replaced laying the column out all at once, spaced back down a straight line drawn
        /// towards the next waypoint. That line is only the road for the first few metres - a route
        /// that starts on a bend puts the tail of the column through whatever is inside the corner,
        /// which on this map is somebody's house - and no amount of searching sideways for a clear
        /// slot fixes the underlying problem, which is that nothing knew where the vehicles would
        /// actually fit. Driving the first one out of the way and dropping the second where it stood
        /// does know: that ground was holding a vehicle a second ago.
        ///
        /// Every wait is bounded. A crewman who never makes it into his seat, or a head of column
        /// that has driven into a tree, must not leave the rest of the convoy unspawned forever.
        /// </summary>
        private void TickForming()
        {
            switch (_formPhase)
            {
                case FormPhase.Spawn:
                    SpawnNext();
                    break;

                case FormPhase.Board:
                    if (NewestElementAboard() || Time.time >= _formPhaseDeadline)
                    {
                        // The last one down does not go straight to the road. Everything spawned
                        // before it was waited for when it was that vehicle's turn, but "waited for"
                        // then meant its own crew - not that it had come to rest, and not that a
                        // straggler off an earlier vehicle had finished climbing in. Leaving on the
                        // strength of one driver sitting down is what left the tail of the column
                        // still arriving while the head drove off.
                        _formPhase = _pending.Count > 0 ? FormPhase.Advance : FormPhase.Settle;
                        _formPhaseDeadline = Time.time
                            + (_pending.Count > 0 ? AdvanceTimeoutSeconds : SettleTimeoutSeconds);
                    }

                    break;

                case FormPhase.Settle:
                    if (IsColumnReady() || Time.time >= _formPhaseDeadline)
                    {
                        _formPhase = FormPhase.Depart;
                        _formPhaseDeadline = Time.time + FormHoldSeconds;
                    }

                    break;

                case FormPhase.Depart:
                    if (Time.time >= _formPhaseDeadline)
                    {
                        State = ConvoyState.Cruise;
                        Logger.Log($"[Bandit] Convoy {Id} formed up - {_elements.Count} vehicle(s) "
                            + "on the road.");
                    }

                    break;

                case FormPhase.Advance:
                    if (IsStartLineClear() || Time.time >= _formPhaseDeadline)
                    {
                        _formPhase = FormPhase.Hold;
                        _formPhaseDeadline = Time.time + FormHoldSeconds;
                    }

                    break;

                case FormPhase.Hold:
                    if (Time.time >= _formPhaseDeadline)
                    {
                        _formPhase = FormPhase.Spawn;
                    }

                    break;
            }
        }

        /// <summary>Puts the next vehicle in the queue on the start line and starts waiting for its
        /// crew.</summary>
        private void SpawnNext()
        {
            while (_pending.Count > 0)
            {
                BanditEvent.Ride ride = _pending.Dequeue()(_spawnPoint, _spawnFacing);

                if (ride == null)
                {
                    continue; // could not be put on the ground; the spawner has already said why
                }

                // The event's own director drives a ride at whoever it sees. A convoy's vehicles
                // are driven from here instead, and the two must not both be steering.
                ride.DriveAtCaller = false;
                _elements.Add(new Element { Ride = ride });

                _formPhase = FormPhase.Board;
                _formPhaseDeadline = Time.time + BoardTimeoutSeconds;
                return;
            }

            // Every remaining factory failed. Whatever is already here is the convoy.
            State = ConvoyState.Cruise;
            Logger.Log($"[Bandit] Convoy {Id} formed up - {_elements.Count} vehicle(s) on the road.");
        }

        /// <summary>
        /// Whether the vehicle that landed last has everybody in it who is coming.
        ///
        /// Only the newest one, because this is the gate on spawning the *next* vehicle and the rest
        /// of the column was waited for when it was their turn. The gate on actually leaving is
        /// <see cref="IsColumnReady"/>, which asks about all of them.
        ///
        /// A crewman with a seat request still pending is still climbing in - RequestSeat retries on
        /// its own - and one with neither a seat nor a request is not coming at all, so waiting the
        /// deadline out for him would hold up a vehicle that is otherwise ready.
        /// </summary>
        private bool NewestElementAboard()
        {
            return _elements.Count == 0 || IsElementAboard(_elements[_elements.Count - 1]);
        }

        /// <summary>
        /// Whether the whole column is crewed, stopped and ready to be a convoy.
        ///
        /// The gate on leaving the start line, and it asks about every vehicle rather than the one
        /// that landed last. Being at rest is part of it: a vehicle is dropped from a height so it
        /// settles onto its own suspension rather than spawning half inside the road, and a column
        /// that pulls away during that has a tail that is still bouncing. Bounded by
        /// <see cref="SettleTimeoutSeconds"/>, because a vehicle wedged nose-down in a ditch is
        /// never going to report itself ready and the convoy still has somewhere to be.
        /// </summary>
        private bool IsColumnReady()
        {
            foreach (Element element in _elements)
            {
                if (element.Finished || element.Ride.Vehicle == null)
                {
                    continue;
                }

                if (!IsElementAboard(element))
                {
                    return false;
                }

                Rigidbody body = element.Ride.Vehicle.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic && body.velocity.magnitude > AtRestMetresPerSecond)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether one vehicle has everybody in it who is still coming.</summary>
        private static bool IsElementAboard(Element element)
        {
            if (element.Finished || element.Ride.Vehicle == null)
            {
                return true;
            }

            BanditBotController driver = element.Ride.Driver;
            bool driverComing = driver != null && driver.Self != null
                && (driver.Self.life == null || !driver.Self.life.isDead);

            if (driverComing && (driver.Driver == null || !driver.Driver.IsSeated || driver.HasPendingSeat))
            {
                return false;
            }

            foreach (BanditBotController rider in element.Ride.Riders)
            {
                if (rider != null && rider.Self != null
                    && (rider.Self.life == null || !rider.Self.life.isDead)
                    && rider.HasPendingSeat)
                {
                    return false;
                }
            }

            foreach (BanditBotController gunner in element.Ride.Gunners)
            {
                if (gunner != null && gunner.Self != null
                    && (gunner.Self.life == null || !gunner.Self.life.isDead)
                    && gunner.HasPendingSeat)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether the start line is far enough behind the column, and physically empty, to drop
        /// another vehicle on.
        ///
        /// Both halves are needed. The distance is what says the column has actually moved rather
        /// than sat revving; the box check is what says nothing is standing on the spot - which the
        /// distance alone would miss for a vehicle that pulled forward and then rolled back, or one
        /// whose crew is walking across the line.
        /// </summary>
        private bool IsStartLineClear()
        {
            float nearest = float.MaxValue;
            bool any = false;

            foreach (Element element in _elements)
            {
                InteractableVehicle vehicle = element.Ride.Vehicle;
                if (vehicle == null || vehicle.isDead || vehicle.isExploded)
                {
                    continue;
                }

                any = true;
                nearest = Mathf.Min(nearest, HorizontalDistance(vehicle.transform.position, _spawnPoint));
            }

            if (!any)
            {
                return true; // nothing left to get out of the way
            }

            if (nearest < _formSpacing)
            {
                return false;
            }

            // Rolled far enough. Past that, keep going only while something is still standing on the
            // spot, and not for ever - see FormClearanceSlackMetres.
            return nearest >= _formSpacing + FormClearanceSlackMetres
                || BanditPlacement.IsVehicleSlotClear(_spawnPoint, _spawnTravel);
        }

        /// <summary>
        /// Decides whether the column is in a fight, and moves it between cruising, fighting and
        /// picking its infantry back up.
        ///
        /// Contact comes from the squads first. Every crew is a squad of the event's, and so is
        /// every group of riders once they are out, so "anyone in this convoy can see somebody"
        /// is already answered by the squad that spotted them - including by men who are on foot
        /// beside a vehicle whose crew can see nothing but its own armour.
        /// </summary>
        private void UpdateContact()
        {
            if (HasFreshContact())
            {
                _lastContactTime = Time.time;

                if (State != ConvoyState.Contact)
                {
                    State = ConvoyState.Contact;
                    DismountRiders();
                }

                return;
            }

            switch (State)
            {
                case ConvoyState.Contact:
                    if (Time.time - _lastContactTime < ContactMemorySeconds)
                    {
                        return; // still fighting from memory - the target ducked, it is not over
                    }

                    State = ConvoyState.Rallying;
                    _rallyDeadline = Time.time + RallyTimeoutSeconds;
                    break;

                case ConvoyState.Rallying:
                    if (TickRally())
                    {
                        State = ConvoyState.Cruise;
                    }

                    break;
            }
        }

        private bool HasFreshContact()
        {
            foreach (BanditSquad squad in Event.Squads)
            {
                if (squad != null && squad.HasFreshContact)
                {
                    return true;
                }
            }

            // The buttoned-up case: nobody in the column can see out, so proximity stands in for
            // sight. Measured from the vehicles rather than the men, since the men may be anywhere.
            foreach (Element element in _elements)
            {
                BanditEvent.Ride ride = element.Ride;
                if (ride.Vehicle == null || ride.Driver == null || ride.Driver.Self == null)
                {
                    continue;
                }

                if (BanditEvent.NearestEnemyTo(ride.Driver.Self, ride.Vehicle.transform.position,
                        ContactTriggerRadiusMetres) != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Puts out everyone whose job is not driving or shooting from the vehicle. Gunners stay -
        /// a turret is worth more than another rifle on the ground - and so does the driver, because
        /// the column keeps moving while it fights and a driverless vehicle cannot.
        /// </summary>
        private void DismountRiders()
        {
            foreach (Element element in _elements)
            {
                if (element.Finished)
                {
                    continue;
                }

                foreach (BanditBotController rider in element.Ride.Riders)
                {
                    if (rider == null || rider.Self == null || rider.Self.life == null || rider.Self.life.isDead)
                    {
                        continue;
                    }

                    if (rider.Driver == null || !rider.Driver.IsSeated)
                    {
                        continue;
                    }

                    // Recorded before it gets out, because afterwards there is nothing to ask.
                    element.RiderSeats[rider] = rider.Self.movement.getSeat();
                    BanditEvent.Disembark(rider);
                }
            }
        }

        /// <summary>
        /// Walks the dismounted men back to their vehicles and puts them in. Returns true once the
        /// column is loaded again, or once it has waited long enough that whoever is left is not
        /// coming.
        ///
        /// A straggler is left behind rather than held for: it is armed, it is in a squad, and it
        /// will fight where it stands. A convoy that waits indefinitely for a man wedged behind a
        /// rock never moves again, which is a far worse outcome than one rifleman walking home.
        /// </summary>
        private bool TickRally()
        {
            bool everyoneAboard = true;

            foreach (Element element in _elements)
            {
                if (element.Finished || element.Ride.Vehicle == null)
                {
                    continue;
                }

                Vector3 vehiclePosition = element.Ride.Vehicle.transform.position;
                List<BanditBotController> boarded = null;

                foreach (KeyValuePair<BanditBotController, byte> entry in element.RiderSeats)
                {
                    BanditBotController rider = entry.Key;

                    bool gone = rider == null || rider.Self == null
                        || (rider.Self.life != null && rider.Self.life.isDead);

                    if (gone || (rider.Driver != null && rider.Driver.IsSeated))
                    {
                        (boarded ?? (boarded = new List<BanditBotController>())).Add(rider);
                        continue;
                    }

                    everyoneAboard = false;

                    if (rider.HasPendingSeat)
                    {
                        continue; // already climbing in - RequestSeat retries on its own
                    }

                    float distance = Vector3.Distance(rider.Self.transform.position, vehiclePosition);
                    if (distance > RemountRadiusMetres)
                    {
                        if (!element.RallyOrders.TryGetValue(rider, out Vector3 ordered)
                            || (ordered - vehiclePosition).sqrMagnitude > RallyReorderDistanceSquared)
                        {
                            element.RallyOrders[rider] = vehiclePosition;
                            rider.Brain?.GoTo(vehiclePosition);
                        }

                        continue;
                    }

                    element.RallyOrders.Remove(rider);
                    rider.Brain?.StopMoving();
                    rider.RequestSeat(element.Ride.Vehicle, entry.Value, false);
                }

                if (boarded != null)
                {
                    foreach (BanditBotController rider in boarded)
                    {
                        element.RiderSeats.Remove(rider);
                        element.RallyOrders.Remove(rider);
                    }
                }
            }

            // The men off a burnt-out vehicle are picked up in the same breath as everyone else
            // getting back into their own seats.
            everyoneAboard &= TickOrphans();

            if (everyoneAboard)
            {
                return true;
            }

            if (Time.time < _rallyDeadline)
            {
                return false;
            }

            Logger.Log($"[Bandit] Convoy {Id} moving off without the men who did not make it back.");
            foreach (Element element in _elements)
            {
                element.RiderSeats.Clear();
                element.RallyOrders.Clear();
            }

            _orphans.Clear();
            _orphanOrders.Clear();

            return true;
        }

        /// <summary>
        /// One vehicle's step: check it still exists, work out how far along the route it has got,
        /// hand the driver the next point, and decide how fast it is allowed to take it.
        /// </summary>
        private void TickElement(Element element, int position)
        {
            BanditEvent.Ride ride = element.Ride;

            if (ride.Vehicle == null || ride.Vehicle.isDead || ride.Vehicle.isExploded)
            {
                // Vanilla throws the occupants clear when a vehicle goes up. They are bandits on
                // foot in a squad now, which is a perfectly good thing to be - and if the column
                // still has a spare seat anywhere, they are getting back in it.
                foreach (BanditBotController survivor in ride.Riders)
                {
                    Orphan(survivor);
                }

                Orphan(ride.Driver);

                element.Finished = true;
                return;
            }

            BanditBotController driver = ride.Driver;
            bool driverGone = driver == null || driver.Self == null
                || (driver.Self.life != null && driver.Self.life.isDead);

            if (driverGone)
            {
                // Nothing will move this vehicle again, so nobody should be sitting in it waiting.
                DismountElement(element);
                element.Finished = true;
                return;
            }

            BanditVehicleDriver vehicleDriver = driver.Driver;
            if (vehicleDriver == null || !vehicleDriver.IsSeated || driver.HasPendingSeat)
            {
                return; // still getting aboard
            }

            if (vehicleDriver.IsSubmerged)
            {
                // Vanilla has drowned it and the engine will not restart in there, so it is out of
                // the column - and the column has to be told, or it waits for a vehicle that is
                // never going to arrive. Nobody is put out: a rider ordered into deep water drowns
                // as surely as the vehicle did.
                Logger.Log($"[Bandit] Convoy {Id}: {ride.TypeName} is underwater - leaving it behind.");
                vehicleDriver.StopDriving();
                element.Finished = true;
                return;
            }

            Vector3 vehiclePosition = ride.Vehicle.transform.position;

            // How far along the route it is - measured by which points it has actually driven past,
            // not by which ones are within fourteen metres of it.
            //
            // The radius test was corner cutting, and on this map that means leaving the road. Route
            // points are eight metres apart, so a fourteen-metre radius routinely swallowed two of
            // them at once; on a bend the two it swallowed were the two that described the bend, and
            // the vehicle drove the chord across the verge instead. Passing a point is a fact about
            // the geometry rather than a tolerance: the vehicle is past it when it is on the far
            // side of it, along the segment leading to the next one.
            while (element.Target < _path.Count - 1 && HasPassed(vehiclePosition, element.Target))
            {
                element.Target++;
            }

            bool atLastPoint = element.Target >= _path.Count - 1;
            Vector3 target = SteerTarget(element, vehiclePosition);
            float toEnd = HorizontalDistance(vehiclePosition, _path[_path.Count - 1].Position);

            // Near it, or past it. Overshooting used to leave a vehicle permanently travelling,
            // because arrival was only ever tested as proximity and a vehicle eighty metres beyond
            // the last waypoint is never near it again.
            if (atLastPoint && (toEnd <= ArriveRadiusMetres || HasOvershotEnd(vehiclePosition)))
            {
                vehicleDriver.StopDriving();
                vehicleDriver.SpeedScale = 1f;
                element.Arrived = true;
                element.Finished = true;
                return;
            }

            if (vehicleDriver.ConsumeGaveUp())
            {
                element.GiveUps++;

                if (element.GiveUps > MaxGiveUps)
                {
                    Logger.Log($"[Bandit] Convoy {Id}: {ride.TypeName} could not get through at route "
                        + $"point {element.Target}/{_path.Count - 1}, ({vehiclePosition.x:0}, "
                        + $"{vehiclePosition.z:0}) - unloading where it stands. Turn on /banditnavlog "
                        + "to find out why.");
                    DismountElement(element);
                    vehicleDriver.StopDriving();
                    element.Finished = true;
                    return;
                }

                Logger.Log($"[Bandit] Convoy {Id}: {ride.TypeName} gave up on route point "
                    + $"{element.Target}/{_path.Count - 1} (attempt {element.GiveUps} of {MaxGiveUps}) - "
                    + "skipping ahead.");

                element.Target = Mathf.Min(element.Target + SkipPointsOnGiveUp, _path.Count - 1);
                element.IssuedTarget = -1;
                target = SteerTarget(element, vehiclePosition);
            }

            // The carrot moves every tick, and only a trip that is not running is issued afresh.
            // Holding it still between route points is what made a vehicle brake in the middle of a
            // clear bend: the aim point stops being a carrot and becomes a place, the vehicle
            // reaches it, and the bearing to a place beside its own nose swings through a right
            // angle - which is the threshold for a three-point turn. See
            // BanditVehicleDriver.TryMoveDestination for why moving it is not the same call as
            // issuing it.
            if (!vehicleDriver.TryMoveDestination(target, CarrotArriveRadiusMetres))
            {
                if (vehicleDriver.TrySetDestination(target, CarrotArriveRadiusMetres, out string reason))
                {
                    element.IssuedTarget = -1;
                }
                else
                {
                    // Not something that becomes true by waiting - a boat given a road route, most
                    // often. Put the men out and stop pretending this vehicle is going anywhere.
                    Logger.Log($"[Bandit] Convoy {Id}: {ride.TypeName} cannot drive the route "
                        + $"({reason}) - unloading where it stands.");
                    DismountElement(element);
                    element.Finished = true;
                    return;
                }
            }

            BanditRouteDebug.CurrentTarget = target;

            if (element.IssuedTarget != element.Target)
            {
                element.IssuedTarget = element.Target;
                BanditNavLog.Write(vehicleDriver, $"convoy {Id}: point {element.Target}/{_path.Count - 1}, "
                    + $"aiming ({target.x:0}, {target.z:0})");
            }

            float scale = ResolveSpeedScale(element, position, vehiclePosition);

            // Braking for the end of the route. The driver is handed a rolling aim point well ahead
            // of it the whole way, so without this the last waypoint looks like any other and the
            // column arrives at cruising speed.
            if (atLastPoint)
            {
                scale = Mathf.Min(scale,
                    Mathf.Max(ApproachMinimumScale, Mathf.Clamp01(toEnd / ApproachSlowdownMetres)));
            }

            vehicleDriver.SpeedScale = scale;
        }

        /// <summary>
        /// How fast this vehicle may go: the column's own pace for whatever it is doing, held down
        /// by the vehicle in front of it and again by the one behind it.
        ///
        /// Interval keeping is speed rather than steering on purpose. Everything in the column is
        /// driving the same route, so a follower that is too close does not need to go round the
        /// vehicle ahead - it needs to stop pushing into it, and a lorry that noses out to overtake
        /// on a bend is precisely the behaviour to avoid.
        ///
        /// Looking backwards is the other half of the same idea. Whoever had to go round the fallen
        /// tree pays for it in time, and if nobody in front gives any of that time back the column
        /// arrives strung out over half a mile. See <see cref="TailWaitScale"/>.
        /// </summary>
        private float ResolveSpeedScale(Element element, int position, Vector3 vehiclePosition)
        {
            float pace;
            switch (State)
            {
                case ConvoyState.Forming:
                    // Only the leapfrog moves the column, and only at a shuffle. Every other moment
                    // of forming up is the column standing still with the engines running, which is
                    // the point: a vehicle is about to appear on the start line behind them.
                    if (_formPhase != FormPhase.Advance)
                    {
                        return 0f;
                    }

                    pace = FormUpSpeedScale;
                    break;
                case ConvoyState.Contact:
                    pace = ContactSpeedScale;
                    break;
                case ConvoyState.Rallying:
                    return 0f; // engine running, waiting for its men
                default:
                    pace = 1f;
                    break;
            }

            // Waiting for the vehicle behind. Only while cruising: under contact the column is
            // already crawling, and while forming up the leapfrog decides who moves.
            if (State == ConvoyState.Cruise)
            {
                pace *= TailWaitScale(element, position, vehiclePosition);
            }

            float closest = ClosestGapAhead(position, vehiclePosition);

            if (closest >= DesiredGapMetres)
            {
                return pace;
            }

            if (closest <= MinimumGapMetres)
            {
                return 0f;
            }

            return pace * Mathf.InverseLerp(MinimumGapMetres, DesiredGapMetres, closest);
        }

        /// <summary>
        /// How much this vehicle has to give away to the one behind it: one when the column is
        /// closed up, falling to <see cref="TailWaitMinimumScale"/> when the next vehicle back has
        /// been left a long way behind.
        ///
        /// "Behind" is by progress along the route rather than by position in the list or by where
        /// the vehicles are in space. A column is not in spawn order for long - anything that gives
        /// up and skips points, gets stuck, or is quicker away from a stop changes the order - and
        /// two vehicles on opposite sides of a hairpin are thirty metres apart in a straight line
        /// while being two hundred metres apart on the road they are both driving.
        /// </summary>
        private float TailWaitScale(Element element, int position, Vector3 vehiclePosition)
        {
            float self = RouteProgressMetres(element, vehiclePosition);
            float nearestBehind = float.MinValue;
            Element follower = null;

            for (int i = 0; i < _elements.Count; i++)
            {
                if (i == position)
                {
                    continue;
                }

                Element other = _elements[i];
                InteractableVehicle otherVehicle = other.Ride.Vehicle;

                if (other.Finished || other.Arrived || otherVehicle == null
                    || otherVehicle.isDead || otherVehicle.isExploded)
                {
                    continue; // not coming, so not something to hold the column for
                }

                float progress = RouteProgressMetres(other, otherVehicle.transform.position);
                if (progress >= self || progress <= nearestBehind)
                {
                    continue;
                }

                nearestBehind = progress;
                follower = other;
            }

            if (follower == null)
            {
                return 1f; // nothing behind - this is the tail
            }

            // Bumper to bumper, like every other interval in this class, so the number means the
            // same thing for a column of tanks as for a column of hatchbacks.
            float gap = self - nearestBehind - HalfLengthOf(element) - HalfLengthOf(follower);

            if (gap <= TailGapMetres)
            {
                return 1f;
            }

            return Mathf.Lerp(1f, TailWaitMinimumScale,
                Mathf.InverseLerp(TailGapMetres, TailStretchedGapMetres, gap));
        }

        /// <summary>
        /// How far along the route a vehicle has got, in metres of road.
        ///
        /// The point it is driving at gives the coarse answer and the projection onto the leg it is
        /// currently on gives the rest, so the figure moves smoothly rather than in eight-metre steps
        /// - which matters, because the difference between two of these is what decides whether the
        /// column is closed up or stretched out.
        /// </summary>
        private float RouteProgressMetres(Element element, Vector3 position)
        {
            int index = Mathf.Clamp(element.Target, 0, _path.Count - 1);
            float progress = _alongRoute[index];

            if (index >= _path.Count - 1)
            {
                return progress;
            }

            Vector3 leg = _path[index + 1].Position - _path[index].Position;
            leg.y = 0f;

            float length = leg.magnitude;
            if (length < 0.01f)
            {
                return progress;
            }

            Vector3 offset = position - _path[index].Position;
            offset.y = 0f;

            return progress + Mathf.Clamp(Vector3.Dot(offset, leg / length), -length, length);
        }

        /// <summary>
        /// How much clear road there is between this vehicle's nose and the back of whatever is in
        /// front of it.
        ///
        /// Two things here were wrong and together they let a tank drive into a lorry. The gap was
        /// measured centre to centre, so two seven-metre vehicles reported ten metres of daylight
        /// while their bumpers were touching - the interval was being kept between origins, not
        /// between vehicles. And only vehicles earlier in the column were considered, on the
        /// assumption that the column stays in the order it spawned in; once anything overtakes,
        /// swaps places after a rally, or is simply quicker away from a stop, the vehicle actually
        /// in front is the one nobody was looking at.
        ///
        /// So: every other running vehicle, whichever end of the column it belongs to, and the
        /// distance measured between bumpers using the footprints the drivers already measured off
        /// their own colliders. Only what is genuinely in front counts - a vehicle alongside on a
        /// wide road is not something to brake for, and one behind is its own driver's problem.
        /// </summary>
        private float ClosestGapAhead(int position, Vector3 vehiclePosition)
        {
            Element self = _elements[position];
            InteractableVehicle vehicle = self.Ride.Vehicle;
            if (vehicle == null)
            {
                return float.MaxValue;
            }

            Vector3 forward = vehicle.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                return float.MaxValue;
            }

            forward.Normalize();

            float selfHalfLength = HalfLengthOf(self);
            float closest = float.MaxValue;

            for (int i = 0; i < _elements.Count; i++)
            {
                if (i == position)
                {
                    continue;
                }

                Element other = _elements[i];
                InteractableVehicle otherVehicle = other.Ride.Vehicle;
                if (other.Finished || otherVehicle == null || otherVehicle.isDead || otherVehicle.isExploded)
                {
                    continue;
                }

                Vector3 offset = otherVehicle.transform.position - vehiclePosition;
                offset.y = 0f;

                float along = Vector3.Dot(offset, forward);
                if (along <= 0f)
                {
                    continue; // behind us
                }

                // Inside the lane rather than merely somewhere in front, so a vehicle passing the
                // other way on a wide road does not stop the column dead.
                float lateral = Vector3.Distance(offset, forward * along);
                if (lateral > LaneHalfWidthMetres)
                {
                    continue;
                }

                closest = Mathf.Min(closest, along - selfHalfLength - HalfLengthOf(other));
            }

            return closest;
        }

        /// <summary>Half the length of a vehicle, from the footprint its driver measured off the
        /// colliders. The fallback is car-sized, for a vehicle nobody is sitting in yet.</summary>
        private static float HalfLengthOf(Element element)
        {
            BanditVehicleDriver driver = element.Ride.Driver?.Driver;
            return driver != null && driver.IsSeated
                ? Mathf.Max(1.5f, driver.Footprint.HalfLength)
                : 3f;
        }

        /// <summary>Everyone out - riders and, since the vehicle is finished either way, the driver
        /// too unless there is still a gun for it to point.</summary>
        private void DismountElement(Element element)
        {
            foreach (BanditBotController rider in element.Ride.Riders)
            {
                BanditEvent.Disembark(rider);
                Orphan(rider);
            }

            if (!element.Ride.IsArmed && element.Ride.DriverDismounts)
            {
                BanditEvent.Disembark(element.Ride.Driver);
                Orphan(element.Ride.Driver);
            }

            element.RiderSeats.Clear();
            element.RallyOrders.Clear();
        }

        /// <summary>Remembers a man who has lost his ride, so the column can pick him up again.</summary>
        private void Orphan(BanditBotController bandit)
        {
            if (bandit != null && !_orphans.Contains(bandit))
            {
                _orphans.Add(bandit);
            }
        }

        /// <summary>
        /// Puts the men from a dead vehicle into the spare seats of a live one.
        ///
        /// A convoy is nearly always carrying empty seats - a crew is what the configuration says it
        /// is, and a Stryker has more room than that - so a burnt-out vehicle's survivors walking
        /// home while the column drives past with three seats free is simply waste. They are already
        /// armed, already in a squad, and already going the same way.
        ///
        /// Seat zero is never offered. A vehicle whose driver died is out of the convoy by then, and
        /// putting a rifleman behind the wheel of one would quietly resurrect a vehicle the column
        /// has already written off.
        /// </summary>
        private bool TickOrphans()
        {
            bool everyoneAboard = true;
            List<BanditBotController> collected = null;

            foreach (BanditBotController orphan in _orphans)
            {
                bool gone = orphan == null || orphan.Self == null
                    || (orphan.Self.life != null && orphan.Self.life.isDead);

                if (gone || (orphan.Driver != null && orphan.Driver.IsSeated))
                {
                    (collected ?? (collected = new List<BanditBotController>())).Add(orphan);
                    continue;
                }

                if (orphan.HasPendingSeat)
                {
                    everyoneAboard = false;
                    continue;
                }

                if (!TryFindSpareSeat(orphan, out InteractableVehicle vehicle, out byte seat))
                {
                    continue; // nowhere to put him; he walks, and the deadline will leave him
                }

                everyoneAboard = false;

                Vector3 where = vehicle.transform.position;
                float distance = Vector3.Distance(orphan.Self.transform.position, where);

                if (distance > RemountRadiusMetres)
                {
                    if (!_orphanOrders.TryGetValue(orphan, out Vector3 ordered)
                        || (ordered - where).sqrMagnitude > RallyReorderDistanceSquared)
                    {
                        _orphanOrders[orphan] = where;
                        orphan.Brain?.GoTo(where);
                    }

                    continue;
                }

                _orphanOrders.Remove(orphan);
                orphan.Brain?.StopMoving();
                orphan.RequestSeat(vehicle, seat, false);
            }

            if (collected != null)
            {
                foreach (BanditBotController orphan in collected)
                {
                    _orphans.Remove(orphan);
                    _orphanOrders.Remove(orphan);
                }
            }

            return everyoneAboard;
        }

        /// <summary>The nearest running vehicle in the column with a seat nobody is in.</summary>
        private bool TryFindSpareSeat(BanditBotController orphan, out InteractableVehicle vehicle, out byte seat)
        {
            vehicle = null;
            seat = 0;

            Vector3 from = orphan.Self.transform.position;
            float nearest = float.MaxValue;

            foreach (Element element in _elements)
            {
                InteractableVehicle candidate = element.Ride.Vehicle;
                if (element.Finished || candidate == null || candidate.isDead || candidate.isExploded)
                {
                    continue;
                }

                Passenger[] seats = candidate.passengers;
                if (seats == null)
                {
                    continue;
                }

                float distance = Vector3.Distance(from, candidate.transform.position);
                if (distance >= nearest)
                {
                    continue;
                }

                for (byte index = 1; index < seats.Length; index++)
                {
                    if (seats[index] != null && seats[index].player == null)
                    {
                        vehicle = candidate;
                        seat = index;
                        nearest = distance;
                        break;
                    }
                }
            }

            return vehicle != null;
        }

        /// <summary>Whether a vehicle is past the last waypoint rather than short of it, measured
        /// against the direction the route arrives from.</summary>
        private bool HasOvershotEnd(Vector3 position)
        {
            if (_path.Count < 2)
            {
                return false;
            }

            Vector3 destination = _path[_path.Count - 1].Position;
            Vector3 approach = destination - _path[_path.Count - 2].Position;
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

        /// <summary>
        /// Whether the vehicle is past a route point, i.e. on the far side of it along the segment
        /// running to the next one.
        ///
        /// The radius is only for the degenerate cases - two points on top of each other, and a
        /// vehicle that has come to rest exactly on a point without crossing the plane through it.
        /// Without one of those a column can sit on a point for ever waiting to pass it.
        /// </summary>
        private bool HasPassed(Vector3 position, int index)
        {
            Vector3 point = _path[index].Position;

            if (HorizontalDistance(position, point) <= PointReachedRadiusMetres)
            {
                return true;
            }

            Vector3 segment = _path[index + 1].Position - point;
            segment.y = 0f;
            if (segment.sqrMagnitude < 0.01f)
            {
                return true;
            }

            Vector3 offset = position - point;
            offset.y = 0f;

            return Vector3.Dot(offset, segment.normalized) > 0f;
        }

        /// <summary>
        /// The point on the route a vehicle should be steering at: a fixed distance up the road from
        /// where it is, interpolated along the polyline rather than snapped to a node.
        ///
        /// Interpolated because a node is a step and a step is a swerve. Walking the route until the
        /// nodes are far enough away and then aiming at the last one would hand the driver a target
        /// that jumps eight metres sideways every time the column advances, and the vehicle would
        /// steer at each jump. Sliding the aim point smoothly along the road means the steering
        /// input changes smoothly too.
        /// </summary>
        private Vector3 SteerTarget(Element element, Vector3 position)
        {
            BanditVehicleDriver driver = element.Ride.Driver?.Driver;
            float lookahead = Mathf.Clamp(
                (driver != null ? driver.Speed : 0f) * SteerLookaheadSeconds,
                MinSteerLookaheadMetres, MaxSteerLookaheadMetres);

            int start = Mathf.Clamp(element.Target, 0, _path.Count - 1);

            Vector3 previous = _path[start].Position;
            float previousDistance = HorizontalDistance(position, previous);

            if (previousDistance >= lookahead || start >= _path.Count - 1)
            {
                return previous;
            }

            for (int i = start + 1; i < _path.Count; i++)
            {
                Vector3 candidate = _path[i].Position;
                float distance = HorizontalDistance(position, candidate);

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

            return _path[_path.Count - 1].Position;
        }


        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        /// <summary>
        /// The convoy's heartbeat, for the same reason <see cref="BanditEvent"/> has one: a convoy
        /// is not attached to any game object vanilla is already ticking, and its vehicles have to
        /// be steered between the moment they set off and the moment they arrive.
        /// </summary>
        private sealed class BanditConvoyDirector : MonoBehaviour
        {
            private static BanditConvoyDirector _instance;

            public static void Ensure()
            {
                if (_instance != null)
                {
                    return;
                }

                GameObject host = new GameObject("BanditConvoyDirector");
                DontDestroyOnLoad(host);
                _instance = host.AddComponent<BanditConvoyDirector>();
            }

            private void Update()
            {
                for (int i = 0; i < All.Count; i++)
                {
                    All[i].Tick();
                }
            }
        }
    }
}
