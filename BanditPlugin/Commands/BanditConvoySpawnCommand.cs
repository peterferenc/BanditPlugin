using System;
using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using BanditPlugin.Navigation;
using Rocket.API;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/bandit convoy spawn" - a column of crewed vehicles driving this map's convoy route.
    ///
    /// Kept beside <see cref="BanditEventSpawnCommand"/> rather than inside it because they are a
    /// different job - one takes a budget and puts a fight on the ground, this takes a budget and
    /// sends it somewhere - but a convoy is still an event underneath, drawn from the same kits,
    /// squads and vehicles. The route itself is recorded with "/bandit route convoy".
    /// </summary>
    internal static class BanditConvoySpawnCommand
    {
        /// <summary>
        /// "/bandit convoy spawn &lt;cost&gt; [noroads]" - buys a column of crewed vehicles and
        /// sends it down the route.
        ///
        /// The budget works exactly as it does for an ordinary event, and is drawn from the same
        /// configuration; what differs is that every point of it goes on vehicles and the men riding
        /// in them. No foot squads: a squad spawned beside the road would be left behind by the
        /// first leg.
        /// </summary>
        public static void Execute(IRocketPlayer caller, string[] args)
        {
            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;
            UnturnedPlayer callerPlayer = RequirePlayer(caller, "Spawning a convoy");
            if (callerPlayer == null)
            {
                return;
            }

            Player player = callerPlayer.Player;

            args = BanditTeams.ExtractTeamArgument(args, out string requestedTeam);
            args = BanditEventSpawnCommand.ExtractSeedArgument(args, out int? requestedSeed);
            args = ExtractUseRoads(args, out bool useRoads);
            args = ExtractCount(args, "vehicles", out int? vehicleLimit);
            args = ExtractCount(args, "crew", out int? crewLimit);

            if (args.Length == 0 || !float.TryParse(args[0], out float budget) || budget <= 0f)
            {
                Reply(caller, "Usage: /bandit convoy spawn <cost> [vehicles:<n>] [crew:<n>] "
                    + "[noroads] [team:<team>] [seed:<n>].", Color.yellow);
                Reply(caller, "vehicles:1 crew:1 is the one-vehicle, one-bandit convoy - "
                    + "the shape to test a route with, since nothing is following anything.", Color.grey);
                Reply(caller, "The route comes from /bandit route convoy - the column spawns at "
                    + "the first waypoint and drives through the rest.", Color.grey);
                return;
            }

            IReadOnlyList<Vector3> route = BanditConvoyRoute.Current;
            if (route.Count < 2)
            {
                Reply(caller, $"A convoy needs at least two waypoints; this map has {route.Count}. "
                    + "Record them with /bandit route convoy add.", Color.red);
                return;
            }

            BanditTeam team = BanditTeams.Find(config, !string.IsNullOrEmpty(requestedTeam)
                ? requestedTeam
                : config.DefaultTeam);

            if (team == null && !string.IsNullOrEmpty(requestedTeam))
            {
                Reply(caller, $"No team called '{requestedTeam}'.", Color.red);
                return;
            }

            int banditCap = Mathf.Max(0, config.EventMaxBandits);
            if (banditCap < 1)
            {
                Reply(caller, $"EventMaxBandits is {config.EventMaxBandits} - no bandits "
                    + "can be spawned until it is raised.", Color.red);
                return;
            }

            int seed = requestedSeed ?? Environment.TickCount;
            int vehicleCap = vehicleLimit.HasValue
                ? Mathf.Clamp(vehicleLimit.Value, 1, config.ConvoyVehicleCap)
                : config.ConvoyVehicleCap;

            BanditEventPlan plan = BanditEventDraw.DrawConvoy(config, budget, seed, banditCap, vehicleCap);

            if (plan.Vehicles.Count == 0)
            {
                // Deliberately not softened into "spawned one bandit anyway" the way an event is.
                // A convoy with no vehicle in it is not a smaller convoy, it is a different thing.
                Reply(caller, $"{budget:0} pts buys no vehicle at these prices. "
                    + "/bandit info event prices them, and MinEventCost is what gates the big ones.",
                    Color.red);
                return;
            }

            Spawn(caller, config, player, plan, team, route, useRoads, seed, crewLimit ?? int.MaxValue);
        }

        /// <summary>
        /// Works out where the column starts, what order it drives in, and hands the whole thing to
        /// <see cref="BanditConvoy"/> to put on the ground one vehicle at a time.
        ///
        /// Nothing is spawned here any more. It used to lay the entire column out at once, spaced
        /// back down a straight line drawn at the next waypoint, and search sideways whenever a slot
        /// was occupied - and the vehicles still ended up in houses, because that line stops being
        /// the road the moment the route bends. The convoy spawns them itself now, leapfrogging: one
        /// vehicle, driven forward, next vehicle onto the ground it just left. See
        /// BanditConvoy.TickForming.
        /// </summary>
        private static void Spawn(IRocketPlayer caller, BanditConfiguration config, Player player,
            BanditEventPlan plan, BanditTeam team, IReadOnlyList<Vector3> route, bool useRoads, int seed,
            int crewLimit)
        {
            Vector3 start = route[0];

            Vector3 travel = route[1] - route[0];
            travel.y = 0f;
            travel = travel.sqrMagnitude > 0.0001f ? travel.normalized : Vector3.forward;

            // Started on the tarmac rather than beside it when there is any: the first thing the
            // column does is drive, and a lorry that spawns in the treeline has to fight its way out
            // of it before the route even begins.
            if (useRoads && BanditRoadGraph.TryGetNearest(start, BanditConvoy.RoadSnapDistanceMetres,
                    out int startNode, out float _))
            {
                BanditRoadGraph.RoadNode node = BanditRoadGraph.Get(startNode);
                if (node != null)
                {
                    start = node.Position;
                }
            }

            start = ResolveSpawnSlot(start, travel, useRoads);

            float facing = Mathf.Atan2(travel.x, travel.z) * Mathf.Rad2Deg;
            Vector3 right = Vector3.Cross(Vector3.up, travel);
            Vector3 origin = player.transform.position;

            BanditEvent banditEvent = BanditEvent.Create(plan.Budget);
            List<BanditVehicleType> order = OrderColumn(plan.Vehicles, crewLimit);
            List<BanditConvoy.VehicleFactory> factories =
                new List<BanditConvoy.VehicleFactory>(order.Count);

            foreach (BanditVehicleType type in order)
            {
                // Captured rather than spawned: the convoy decides when each of these runs, and the
                // spot it passes in is the start line as it stands at that moment.
                BanditVehicleType captured = type;

                factories.Add((spot, spawnFacing) =>
                {
                    BanditPlacement.Result placed = new BanditPlacement.Result
                    {
                        Origin = origin,
                        Centre = spot,
                        Forward = travel,
                        Right = right,
                        Facing = spawnFacing,
                        UsedMarker = false
                    };

                    return BanditEventSpawnCommand.SpawnRide(config, banditEvent, team, placed, captured, spot,
                        crewLimit);
                });
            }

            banditEvent.Spent = plan.Spent;

            // The first waypoint is where they start, so the route they drive is everything after it.
            List<Vector3> legs = new List<Vector3>(route.Count - 1);
            for (int i = 1; i < route.Count; i++)
            {
                legs.Add(route[i]);
            }

            BanditConvoy convoy = BanditConvoy.Create(banditEvent, factories, start, travel, legs, useRoads,
                config.ConvoySpacing, out string summary);

            if (convoy == null)
            {
                Reply(caller, $"Could not plan the route: {summary}. Nothing was spawned.",
                    Color.red);
                return;
            }

            BanditTargets.Remember(BanditTarget.Of(convoy));

            Reply(caller, $"Convoy {convoy.Id}: {plan.Spent:0} of {plan.Budget:0} pts, "
                + $"{factories.Count} vehicle(s), {route.Count} waypoint(s)"
                + (team != null ? $", team {team.Label}" : ", no team") + ".", Color.green);

            Reply(caller, $"  Forming up at ({start.x:0}, {start.z:0}) - one vehicle at a "
                + "time, each pulling forward to make room for the next.", Color.grey);

            Reply(caller, $"  Route: {summary}, {convoy.PointCount} point(s) "
                + $"({convoy.RoadPointCount} on road). Seed {seed}.", Color.grey);
        }

        /// <summary>
        /// The order the column drives in: guns at the ends, soft skin in the middle.
        ///
        /// One armed vehicle leads. Two, and the second one brings up the rear. More than two, and
        /// only one stays at the back - everything else goes to the front, because the head of a
        /// column is where it meets whatever it is going to meet. The unarmed vehicles are what is
        /// being escorted, so they ride between.
        ///
        /// This is also the order they spawn in, head first, which is what makes the leapfrog work:
        /// the vehicle that drives away to make room is the one that should be in front anyway.
        /// </summary>
        private static List<BanditVehicleType> OrderColumn(List<BanditVehicleType> vehicles, int crewLimit)
        {
            List<BanditVehicleType> armed = new List<BanditVehicleType>();
            List<BanditVehicleType> unarmed = new List<BanditVehicleType>();

            foreach (BanditVehicleType type in vehicles)
            {
                (IsArmed(type, crewLimit) ? armed : unarmed).Add(type);
            }

            List<BanditVehicleType> column = new List<BanditVehicleType>(vehicles.Count);

            // Everything armed except the last one leads; with a single gun there is no last one to
            // hold back, so it simply leads.
            int lead = armed.Count >= 2 ? armed.Count - 1 : armed.Count;

            for (int i = 0; i < lead; i++)
            {
                column.Add(armed[i]);
            }

            column.AddRange(unarmed);

            for (int i = lead; i < armed.Count; i++)
            {
                column.Add(armed[i]);
            }

            return column;
        }

        /// <summary>
        /// Whether this vehicle type will actually arrive with a gun manned.
        ///
        /// Both halves matter: a vehicle with turrets and nobody configured into one of them is a
        /// taxi, and a crew list naming a turret seat the vehicle does not have is a passenger. The
        /// asset is the authority on which seats carry guns, so the two are checked against each
        /// other rather than either being taken on trust. crewLimit is honoured because "crew:1" is
        /// a real thing to type and it means every vehicle turns up with only its driver.
        /// </summary>
        private static bool IsArmed(BanditVehicleType type, int crewLimit)
        {
            if (type?.Crew == null)
            {
                return false;
            }

            VehicleAsset asset = BanditVehicleSpawner.Resolve(type.Vehicle, out string _);
            if (asset == null)
            {
                return false;
            }

            for (int i = 0; i < type.Crew.Count && i < crewLimit; i++)
            {
                BanditVehicleSeat seat = type.Crew[i];
                if (seat != null && BanditEventSpawnCommand.IsTurretSeat(asset, seat.Seat))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Pulls "useRoads:false" out of the words, wherever it sits, and defaults it to on.
        ///
        /// Named rather than positional for the same reason team: and seed: are - everything else
        /// this command takes is a number, and a bare "false" among them would be anybody's guess.
        /// Off is worth having: it is how you find out whether a convoy that took a strange line was
        /// routed badly or driven badly.
        /// </summary>
        /// <summary>
        /// Where a vehicle in the column can actually be put down.
        ///
        /// Spacing the column back along a straight line is right for the first few metres and wrong
        /// after that: the line runs towards the *next waypoint*, the road does not, and a route that
        /// starts on a bend puts the tail of the column through whatever is inside the corner. Which
        /// on PEI is a house - vehicles were spawning in living rooms.
        ///
        /// So the straight line only proposes a distance back down the column, and the road decides
        /// where that is. Snapping each slot onto the nearest road node keeps the column on the
        /// tarmac, which is flat, clear and the thing it is about to drive along anyway. Then the
        /// slot is checked for whatever is standing in it regardless, because a road with a parked
        /// car or a fence post on it is still no place to put a lorry, and the search steps further
        /// back and then sideways until it finds room.
        /// </summary>
        private static Vector3 ResolveSpawnSlot(Vector3 desired, Vector3 travel, bool useRoads)
        {
            Vector3 right = Vector3.Cross(Vector3.up, travel);

            for (int attempt = 0; attempt < SpawnSlotAttempts; attempt++)
            {
                // Each attempt gives up a little more ground backwards, and alternates to either
                // side of the line, so the column stays a column rather than fanning out.
                Vector3 candidate = desired
                    - travel * (attempt * SpawnSlotBackStepMetres)
                    + right * (attempt % 3 == 1 ? SpawnSlotSideStepMetres
                        : attempt % 3 == 2 ? -SpawnSlotSideStepMetres : 0f);

                if (useRoads && BanditRoadGraph.TryGetNearest(candidate, SpawnSlotRoadSnapMetres,
                        out int node, out float _))
                {
                    BanditRoadGraph.RoadNode roadNode = BanditRoadGraph.Get(node);
                    if (roadNode != null)
                    {
                        candidate = roadNode.Position;
                    }
                }

                candidate = BanditPlacement.SnapToGround(candidate);

                if (BanditPlacement.IsVehicleSlotClear(candidate, travel))
                {
                    return candidate;
                }
            }

            // Nothing clear anywhere along the search. Put it where it was asked for - a vehicle
            // wedged in something is still better than no vehicle, and the driver will reverse out.
            return BanditPlacement.SnapToGround(desired);
        }

        /// <summary>How hard to look for a clear spawn slot, and how far each step moves.</summary>
        private const int SpawnSlotAttempts = 8;
        private const float SpawnSlotBackStepMetres = 4f;
        private const float SpawnSlotSideStepMetres = 3f;
        private const float SpawnSlotRoadSnapMetres = 20f;

        /// <summary>
        /// Pulls "&lt;name&gt;:&lt;number&gt;" out of the words, wherever it sits.
        ///
        /// Both of the two that use it exist for testing rather than for play. A convoy is a column,
        /// and a column is several things happening at once - routing, interval keeping, contact,
        /// remounting - so when one of them is wrong the first useful question is which. "vehicles:1
        /// crew:1" answers it by taking the other three away: one vehicle with nobody in it but the
        /// driver is a route being driven and nothing else.
        /// </summary>
        private static string[] ExtractCount(string[] args, string name, out int? count)
        {
            count = null;

            if (args == null || args.Length == 0)
            {
                return args ?? new string[0];
            }

            List<string> remaining = new List<string>(args.Length);

            foreach (string word in args)
            {
                if (word != null && word.Length > name.Length + 1
                    && word.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                    && (word[name.Length] == ':' || word[name.Length] == '=')
                    && int.TryParse(word.Substring(name.Length + 1), out int value))
                {
                    count = Mathf.Max(1, value);
                    continue;
                }

                remaining.Add(word);
            }

            return remaining.ToArray();
        }

        private static string[] ExtractUseRoads(string[] args, out bool useRoads)
        {
            useRoads = true;

            if (args == null || args.Length == 0)
            {
                return args ?? new string[0];
            }

            List<string> remaining = new List<string>(args.Length);

            foreach (string word in args)
            {
                if (word != null && word.Length > 9
                    && word.StartsWith("useroads", StringComparison.OrdinalIgnoreCase)
                    && (word[8] == ':' || word[8] == '='))
                {
                    string value = word.Substring(9);
                    useRoads = !(value.Equals("false", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                        || value == "0");
                    continue;
                }

                if (word != null && (word.Equals("noroads", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("offroad", StringComparison.OrdinalIgnoreCase)))
                {
                    useRoads = false;
                    continue;
                }

                remaining.Add(word);
            }

            return remaining.ToArray();
        }
    }
}
