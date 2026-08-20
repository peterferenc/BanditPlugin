using System;
using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using BanditPlugin.Navigation;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// The two halves of "/banditevent convoy": recording the route, and running a column along it.
    ///
    /// Kept beside <see cref="CommandBanditEvent"/> rather than inside it because they are a
    /// different job - one takes a budget and puts a fight on the ground, these take a budget and
    /// send it somewhere - but they are the same command word, since a convoy is drawn from the
    /// same kits, squads and vehicles as everything else an event buys.
    /// </summary>
    internal static class BanditConvoyCommands
    {
        /// <summary>
        /// "/banditevent wp ..." - the route this map's convoys drive.
        ///
        /// Waypoints are set where you stand or where your map marker is, which are the two places
        /// anyone actually knows the coordinates of. Removal is by the number the list prints rather
        /// than by proximity, because a convoy route runs across the map and its points are usually
        /// nowhere near whoever is editing it.
        /// </summary>
        public static void Waypoints(IRocketPlayer caller, string[] args)
        {
            Player player = ((UnturnedPlayer)caller).Player;
            string action = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

            switch (action)
            {
                case "set":
                case "add":
                    SetWaypoint(caller, player, args);
                    break;

                case "remove":
                case "delete":
                    RemoveWaypoint(caller, args);
                    break;

                case "clear":
                    int cleared = BanditConvoyRoute.Clear();
                    UnturnedChat.Say(caller, $"Cleared {cleared} convoy waypoint(s).", Color.green);
                    break;

                default:
                    List(caller, player);
                    break;
            }
        }

        private static void SetWaypoint(IRocketPlayer caller, Player player, string[] args)
        {
            Vector3 point;

            if (args.Length > 1 && BanditPlacement.IsMarkerRequest(args[1]))
            {
                if (player.quests == null || !player.quests.isMarkerPlaced)
                {
                    UnturnedChat.Say(caller, "No map marker placed.", Color.red);
                    return;
                }

                // A marker is a click on the map, so the height it carries is whatever the client
                // sent rather than the ground under it. Everything downstream - the road snap, the
                // arrival test - compares heights, so it is put on the terrain here.
                point = player.quests.markerPosition;
                point.y = LevelGround.getHeight(point);
            }
            else
            {
                point = player.transform.position;
            }

            int count = BanditConvoyRoute.Add(point);
            UnturnedChat.Say(caller, $"Convoy waypoint {count} set at ({point.x:0}, {point.z:0}).", Color.green);

            if (count == 1)
            {
                UnturnedChat.Say(caller, "One more at least - a convoy spawns at the first waypoint "
                    + "and drives to the last.", Color.grey);
            }
        }

        private static void RemoveWaypoint(IRocketPlayer caller, string[] args)
        {
            if (args.Length < 2 || !int.TryParse(args[1], out int index))
            {
                UnturnedChat.Say(caller, "Which one? /banditevent wp remove <number from the list>.", Color.red);
                return;
            }

            if (!BanditConvoyRoute.RemoveAt(index))
            {
                UnturnedChat.Say(caller, $"There is no convoy waypoint {index}.", Color.red);
                return;
            }

            UnturnedChat.Say(caller, $"Removed convoy waypoint {index}.", Color.green);
        }

        private static void List(IRocketPlayer caller, Player player)
        {
            IReadOnlyList<Vector3> route = BanditConvoyRoute.Current;

            if (route.Count == 0)
            {
                UnturnedChat.Say(caller, "No convoy route on this map. /banditevent wp set records one "
                    + "where you stand, /banditevent wp set marker at your map marker.", Color.yellow);
                return;
            }

            UnturnedChat.Say(caller, $"{route.Count} convoy waypoint(s) on {BanditConvoyRoute.CurrentMap}"
                + (route.Count < 2 ? " - one more is needed to run a convoy:" : ":"), Color.green);

            for (int i = 0; i < route.Count; i++)
            {
                Vector3 point = route[i];
                float distance = Vector3.Distance(player.transform.position, point);
                UnturnedChat.Say(caller, $"  {i + 1}. ({point.x:0}, {point.y:0}, {point.z:0}) - {distance:0}m away",
                    Color.white);
            }
        }

        /// <summary>
        /// "/banditevent convoy &lt;cost&gt; [useRoads:true]" - buys a column of crewed vehicles and
        /// sends it down the route.
        ///
        /// The budget works exactly as it does for an ordinary event, and is drawn from the same
        /// configuration; what differs is that every point of it goes on vehicles and the men riding
        /// in them. No foot squads: a squad spawned beside the road would be left behind by the
        /// first leg.
        /// </summary>
        public static void Convoy(IRocketPlayer caller, string[] args, string requestedTeam, int? requestedSeed)
        {
            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;
            Player player = ((UnturnedPlayer)caller).Player;

            args = ExtractUseRoads(args, out bool useRoads);
            args = ExtractCount(args, "vehicles", out int? vehicleLimit);
            args = ExtractCount(args, "crew", out int? crewLimit);

            if (args.Length == 0 || !float.TryParse(args[0], out float budget) || budget <= 0f)
            {
                UnturnedChat.Say(caller, "Usage: /banditevent convoy <cost> [vehicles:<n>] [crew:<n>] "
                    + "[useRoads:false] [team:<team>] [seed:<n>].", Color.yellow);
                UnturnedChat.Say(caller, "vehicles:1 crew:1 is the one-vehicle, one-bandit convoy - "
                    + "the shape to test a route with, since nothing is following anything.", Color.grey);
                UnturnedChat.Say(caller, "The route comes from /banditevent wp - the column spawns at "
                    + "the first waypoint and drives through the rest.", Color.grey);
                return;
            }

            IReadOnlyList<Vector3> route = BanditConvoyRoute.Current;
            if (route.Count < 2)
            {
                UnturnedChat.Say(caller, $"A convoy needs at least two waypoints; this map has {route.Count}. "
                    + "Set them with /banditevent wp set.", Color.red);
                return;
            }

            BanditTeam team = BanditTeams.Find(config, !string.IsNullOrEmpty(requestedTeam)
                ? requestedTeam
                : config.DefaultTeam);

            if (team == null && !string.IsNullOrEmpty(requestedTeam))
            {
                UnturnedChat.Say(caller, $"No team called '{requestedTeam}'.", Color.red);
                return;
            }

            int banditCap = Mathf.Max(0, config.EventMaxBandits);
            if (banditCap < 1)
            {
                UnturnedChat.Say(caller, $"EventMaxBandits is {config.EventMaxBandits} - no bandits "
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
                UnturnedChat.Say(caller, $"{budget:0} pts buys no vehicle at these prices. "
                    + "/banditevent check prices them, and MinEventCost is what gates the big ones.",
                    Color.red);
                return;
            }

            Spawn(caller, config, player, plan, team, route, useRoads, seed, crewLimit ?? int.MaxValue);
        }

        /// <summary>
        /// Puts the column on the ground at the first waypoint, nose-to-tail along the first leg,
        /// and hands it to <see cref="BanditConvoy"/> to drive.
        ///
        /// Spawning in column rather than on the ring an event uses is the whole difference in
        /// layout: these vehicles are going somewhere together, in an order, and dropping them in a
        /// circle would have them spend the first hundred metres sorting themselves out - or, worse,
        /// driving through each other while they do.
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

            float facing = Mathf.Atan2(travel.x, travel.z) * Mathf.Rad2Deg;
            Vector3 right = Vector3.Cross(Vector3.up, travel);

            BanditEvent banditEvent = BanditEvent.Create(plan.Budget);
            List<BanditEvent.Ride> rides = new List<BanditEvent.Ride>();

            for (int i = 0; i < plan.Vehicles.Count; i++)
            {
                // Back down the route, so the head of the column is on the first waypoint and the
                // tail is behind it rather than in front.
                Vector3 slot = BanditPlacement.SnapToGround(start - travel * (i * config.ConvoySpacing));

                BanditPlacement.Result placed = new BanditPlacement.Result
                {
                    Origin = player.transform.position,
                    Centre = slot,
                    Forward = travel,
                    Right = right,
                    Facing = facing,
                    UsedMarker = false
                };

                int before = banditEvent.Rides.Count;
                CommandBanditEvent.SpawnRide(config, banditEvent, team, placed, plan.Vehicles[i], slot,
                    crewLimit);

                for (int added = before; added < banditEvent.Rides.Count; added++)
                {
                    rides.Add(banditEvent.Rides[added]);
                }
            }

            banditEvent.Spent = plan.Spent;

            if (rides.Count == 0)
            {
                UnturnedChat.Say(caller, "Nothing could be spawned - check the vehicle names with "
                    + "/banditevent check.", Color.red);
                return;
            }

            // The first waypoint is where they are standing, so the route they drive is everything
            // after it.
            List<Vector3> legs = new List<Vector3>(route.Count - 1);
            for (int i = 1; i < route.Count; i++)
            {
                legs.Add(route[i]);
            }

            BanditConvoy convoy = BanditConvoy.Create(banditEvent, rides, start, legs, useRoads, out string summary);

            if (convoy == null)
            {
                UnturnedChat.Say(caller, $"Could not plan the route: {summary}. The vehicles are "
                    + "spawned and holding.", Color.red);
                return;
            }

            UnturnedChat.Say(caller, $"Convoy {convoy.Id}: {plan.Spent:0} of {plan.Budget:0} pts, "
                + $"{rides.Count} vehicle(s), {banditEvent.BanditCount} bandit(s), "
                + $"{route.Count} waypoint(s)"
                + (team != null ? $", team {team.Label}" : ", no team") + ".", Color.green);

            UnturnedChat.Say(caller, $"  Route: {summary}, {convoy.PointCount} point(s) "
                + $"({convoy.RoadPointCount} on road). Seed {seed}.", Color.grey);
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
