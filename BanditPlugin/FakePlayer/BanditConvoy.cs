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
            Cruise,
            Contact,
            Rallying,
            Arrived
        }

        /// <summary>
        /// How near the current route point a vehicle has to get before it is given the next one.
        ///
        /// Larger than the navigator's own arrival radius on purpose. Route points are eight metres
        /// apart, and a column that had to put its bumper on each one in turn would brake into every
        /// single one of them - the vehicle is handed the next point while it is still rolling at
        /// the current one, so a straight road is driven as a straight road.
        /// </summary>
        private const float AdvanceRadiusMetres = 14f;

        /// <summary>How near the final waypoint counts as arrived. A convoy stops in the area, not
        /// on the pixel.</summary>
        private const float ArriveRadiusMetres = 12f;

        /// <summary>
        /// The interval a following vehicle tries to keep, and the distance at which it stops rather
        /// than closes. Measured bumper to bumper - see <see cref="ClosestGapAhead"/> - so these are
        /// lengths of clear road, not distances between origins, and they mean the same thing for a
        /// tank as for a hatchback.
        /// </summary>
        private const float DesiredGapMetres = 14f;
        private const float MinimumGapMetres = 5f;

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

        /// <summary>How long the column waits at the start line for its crews to get aboard before
        /// leaving without the stragglers. See <see cref="HasFormedUp"/>.</summary>
        private const float FormUpTimeoutSeconds = 25f;

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
        private readonly List<Element> _elements = new List<Element>();
        private float _lastContactTime = float.MinValue;
        private float _rallyDeadline;

        /// <summary>Whether the column has finished loading and may leave. See <see cref="HasFormedUp"/>.</summary>
        private bool _hasFormedUp;
        private readonly float _formUpDeadline;

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
        }

        /// <summary>One vehicle in the column, and where it has got to.</summary>
        private sealed class Element
        {
            public BanditEvent.Ride Ride;

            /// <summary>Index into the route of the point this vehicle is currently driving at.</summary>
            public int Target;

            /// <summary>The point the driver was last actually told about, so a destination is
            /// re-issued when it changes and not every tick - re-issuing resets the navigator's
            /// stuck tracking, and doing that constantly would mean it could never detect a stall.</summary>
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
            _formUpDeadline = Time.time + FormUpTimeoutSeconds;
            _path = path;
            UsesRoads = usesRoads;

            foreach (RoutePoint point in path)
            {
                if (point.NodeIndex >= 0)
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
        /// Plans a route from where the column is standing, through every waypoint in order, and
        /// starts it moving.
        ///
        /// Returns null when there is no route worth driving - which in practice means fewer than
        /// two points came out of the planner - rather than spawning a convoy that reports having
        /// arrived on its first tick.
        /// </summary>
        public static BanditConvoy Create(BanditEvent banditEvent, List<BanditEvent.Ride> rides,
            Vector3 start, IReadOnlyList<Vector3> waypoints, bool useRoads, out string summary)
        {
            List<RoutePoint> path = BuildPath(start, waypoints, useRoads, out int roadLegs, out int directLegs);

            if (path.Count < 1)
            {
                summary = "the route came out empty";
                return null;
            }

            BanditConvoy convoy = new BanditConvoy(banditEvent, path, useRoads);

            foreach (BanditEvent.Ride ride in rides)
            {
                // The event's own director drives a ride at whoever it sees. A convoy's vehicles
                // are driven from here instead, and the two must not both be steering.
                ride.DriveAtCaller = false;
                convoy._elements.Add(new Element { Ride = ride });
            }

            summary = useRoads
                ? $"{roadLegs} leg(s) on road, {directLegs} direct"
                : $"{path.Count} point(s), roads off";

            return convoy;
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

                        path.Add(new RoutePoint { Position = roadNode.Position, NodeIndex = node });
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

                path.Add(new RoutePoint { Position = waypoint, NodeIndex = -1 });
                cursor = waypoint;
            }

            return path;
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

            return $"convoy {Id}: {State}, {running}/{_elements.Count} vehicle(s) running, "
                + $"{PointCount} route point(s)";
        }

        private void Tick()
        {
            if (State == ConvoyState.Arrived)
            {
                return;
            }

            // Before contact, deliberately. A column still loading is not in a fight it can do
            // anything about: ordering the riders out at that moment takes them out of the seats
            // they are in the middle of climbing into, which is both silly to watch and a deadlock -
            // the column then waits for men to be aboard who it has just told to get out. Somebody
            // shooting at a convoy on its start line still gets shot back at, by the gunners in the
            // turrets and by every crew squad, which are weapons free from the moment they spawn.
            if (!HasFormedUp())
            {
                return;
            }

            UpdateContact();

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

            if (allFinished)
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
        /// Whether the column is loaded and ready to leave.
        ///
        /// Crews are spawned on the ground beside their vehicle and have to walk round and climb in,
        /// and vanilla refuses to seat anyone whose equip animation is still running - so for the
        /// first second or two of a convoy's life some vehicles have a driver and some do not. Each
        /// vehicle used to be told to drive the moment its own driver sat down, which is why the
        /// head of the column left while the rest were still getting aboard.
        ///
        /// So nobody moves until everybody can. Bounded by a deadline, because a crewman who never
        /// makes it into his seat must not strand the whole convoy at the start line - after that
        /// the column leaves with whoever is aboard, which is the same bargain the rally has.
        /// </summary>
        private bool HasFormedUp()
        {
            if (_hasFormedUp)
            {
                return true;
            }

            if (Time.time >= _formUpDeadline)
            {
                _hasFormedUp = true;
                Logger.Log($"[Bandit] Convoy {Id} moving off without the crew that did not get aboard.");
                return true;
            }

            foreach (Element element in _elements)
            {
                if (element.Finished || element.Ride.Vehicle == null)
                {
                    continue;
                }

                BanditBotController driver = element.Ride.Driver;
                if (driver == null || driver.Self == null || (driver.Self.life != null && driver.Self.life.isDead))
                {
                    continue; // never going to arrive; not something to wait for
                }

                if (driver.Driver == null || !driver.Driver.IsSeated || driver.HasPendingSeat)
                {
                    return false;
                }

                // The riders too, or the column pulls away from men still walking round to the door -
                // but only the ones still trying. RequestSeat retries on its own, so a man who wants
                // a seat has one pending; a man with neither a seat nor a pending request is not
                // coming, and waiting the full deadline out for him holds up a column that is
                // otherwise ready to go.
                foreach (BanditBotController rider in element.Ride.Riders)
                {
                    if (rider == null || rider.Self == null
                        || (rider.Self.life != null && rider.Self.life.isDead))
                    {
                        continue;
                    }

                    if (rider.HasPendingSeat)
                    {
                        return false;
                    }
                }
            }

            _hasFormedUp = true;
            return true;
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

            // How far along the route it is. More than one point can fall inside the advance radius
            // on a tight bend, or after a skip, so this walks rather than steps.
            while (element.Target < _path.Count - 1
                && HorizontalDistance(vehiclePosition, TargetPosition(element, element.Target)) <= AdvanceRadiusMetres)
            {
                element.Target++;
            }

            bool atLastPoint = element.Target >= _path.Count - 1;
            Vector3 target = TargetPosition(element, element.Target);

            if (atLastPoint && HorizontalDistance(vehiclePosition, target) <= ArriveRadiusMetres)
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
                    Logger.Log($"[Bandit] Convoy {Id}: {ride.TypeName} could not get through - "
                        + "unloading where it stands.");
                    DismountElement(element);
                    vehicleDriver.StopDriving();
                    element.Finished = true;
                    return;
                }

                element.Target = Mathf.Min(element.Target + SkipPointsOnGiveUp, _path.Count - 1);
                element.IssuedTarget = -1;
                target = TargetPosition(element, element.Target);
            }

            if (element.IssuedTarget != element.Target || !vehicleDriver.HasDestination)
            {
                if (vehicleDriver.TrySetDestination(target, out string reason))
                {
                    element.IssuedTarget = element.Target;
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

            vehicleDriver.SpeedScale = ResolveSpeedScale(element, position, vehiclePosition);
        }

        /// <summary>
        /// How fast this vehicle may go: the column's own pace for whatever it is doing, held down
        /// further by the vehicle in front of it.
        ///
        /// Interval keeping is speed rather than steering on purpose. Everything in the column is
        /// driving the same route, so a follower that is too close does not need to go round the
        /// vehicle ahead - it needs to stop pushing into it, and a lorry that noses out to overtake
        /// on a bend is precisely the behaviour to avoid.
        /// </summary>
        private float ResolveSpeedScale(Element element, int position, Vector3 vehiclePosition)
        {
            float pace;
            switch (State)
            {
                case ConvoyState.Contact:
                    pace = ContactSpeedScale;
                    break;
                case ConvoyState.Rallying:
                    return 0f; // engine running, waiting for its men
                default:
                    pace = 1f;
                    break;
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

        /// <summary>
        /// Where on the route this particular vehicle should be. On a road that is its own lane -
        /// over to the right if it fits, down the middle if it does not - which is why the answer
        /// depends on the vehicle and not only on the point.
        /// </summary>
        private Vector3 TargetPosition(Element element, int index)
        {
            RoutePoint point = _path[index];
            if (point.NodeIndex < 0)
            {
                return point.Position;
            }

            float halfWidth = 1.2f;
            if (element.Ride.Driver != null && element.Ride.Driver.Driver != null
                && element.Ride.Driver.Driver.IsSeated)
            {
                halfWidth = Mathf.Max(0.5f, element.Ride.Driver.Driver.Footprint.HalfWidth);
            }

            return BanditRoadGraph.GetLanePosition(point.NodeIndex, TravelDirection(index), halfWidth);
        }

        /// <summary>Which way the route is running at a point, which is what decides which side of
        /// the road is the right-hand one.</summary>
        private Vector3 TravelDirection(int index)
        {
            if (index + 1 < _path.Count)
            {
                return _path[index + 1].Position - _path[index].Position;
            }

            if (index > 0)
            {
                return _path[index].Position - _path[index - 1].Position;
            }

            return Vector3.forward;
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
