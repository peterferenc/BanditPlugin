using System.Collections.Generic;
using BanditPlugin.Navigation;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditroads" - what the road graph thinks this map looks like, and whether it can get from
    /// here to there.
    ///
    /// A convoy that drives somewhere strange is nearly always a routing answer rather than a
    /// driving one, and the routing happens before anything is spawned. This makes it inspectable
    /// on its own: how many nodes came out of the map's roads, which one you are standing on, and
    /// what a route to your map marker actually costs. Run it once on a new map before wondering
    /// why a convoy took the scenic route.
    /// </summary>
    public class CommandBanditRoads : IRocketCommand
    {
        /// <summary>
        /// How far from a road either end of a route may be. Generous, because the interesting
        /// failure is "these two places are not connected by road", not "you were standing in a
        /// field" - the convoy drives the last stretch off-road either way.
        /// </summary>
        private const float SnapDistanceMetres = 80f;

        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "banditroads";
        public string Help => "Reports the road graph, and routes from you to your map marker.";
        public string Syntax => "[route]";
        public List<string> Aliases => new List<string> { "broads" };
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            Player player = ((UnturnedPlayer)caller).Player;
            Vector3 position = player.transform.position;

            BanditRoadGraph.EnsureBuilt();

            if (!BanditRoadGraph.IsAvailable)
            {
                UnturnedChat.Say(caller, "No road graph on this map - convoys would drive straight "
                    + "lines. Check the server log for how many roads were found.", Color.red);
                return;
            }

            UnturnedChat.Say(caller, $"Road graph: {BanditRoadGraph.NodeCount} node(s) on "
                + $"{(Level.info != null ? Level.info.name : "?")}.", Color.green);

            // A map's roads are separate splines that stop short of each other, so the graph has to
            // join them up to be routable at all. Worth reporting: a route that crosses a lot of
            // them is a route over a network the map maker never quite finished, and the long ones
            // are the first thing to look at when a convoy takes a line across a field.
            IReadOnlyList<BanditRoadGraph.GapCandidate> gaps = BanditRoadGraph.BridgedGaps;
            if (gaps.Count > 0)
            {
                float longest = 0f;
                foreach (BanditRoadGraph.GapCandidate gap in gaps)
                {
                    longest = Mathf.Max(longest, gap.Distance);
                }

                UnturnedChat.Say(caller, $"  {gaps.Count} gap(s) between roads bridged to connect it, "
                    + $"longest {longest:0}m.", Color.white);
            }

            if (!BanditRoadGraph.TryGetNearest(position, SnapDistanceMetres, out int nearest, out float distance))
            {
                UnturnedChat.Say(caller, $"No road within {SnapDistanceMetres:0}m of you.", Color.yellow);
            }
            else
            {
                BanditRoadGraph.RoadNode node = BanditRoadGraph.Get(nearest);
                UnturnedChat.Say(caller, $"Nearest road: {distance:0.0}m away, {node.Chart}, "
                    + $"{node.HalfWidth * 2f:0.0}m wide (road {node.RoadIndex}), "
                    + $"{node.Links.Count} link(s).", Color.white);
            }

            bool wantsRoute = command.Length > 0 && command[0].ToLowerInvariant() == "route";
            if (!wantsRoute)
            {
                UnturnedChat.Say(caller, "Place a map marker and run \"/banditroads route\" to test a route.", Color.gray);
                return;
            }

            if (player.quests == null || !player.quests.isMarkerPlaced)
            {
                UnturnedChat.Say(caller, "No map marker placed.", Color.red);
                return;
            }

            // The marker is a map click, so its height is whatever the client sent rather than the
            // ground under it. Everything downstream compares heights, so it is put on the terrain.
            Vector3 marker = player.quests.markerPosition;
            marker.y = LevelGround.getHeight(marker);

            List<int> route = new List<int>();
            if (!BanditRoadGraph.TryRoute(position, marker, SnapDistanceMetres, route, out string reason))
            {
                UnturnedChat.Say(caller, $"No road route: {reason}.", Color.red);
                return;
            }

            Describe(caller, route, position, marker);
        }

        /// <summary>
        /// Reports the route as the two numbers that matter - how far round the roads it is against
        /// how far it would be in a straight line - plus the mix of road types, which is what says
        /// whether the chart penalties are pulling a convoy onto sensible roads or not.
        /// </summary>
        private static void Describe(IRocketPlayer caller, List<int> route, Vector3 from, Vector3 to)
        {
            float roadLength = 0f;
            float gapLength = 0f;
            int gapCount = 0;
            Dictionary<EObjectChart, float> byChart = new Dictionary<EObjectChart, float>();

            for (int i = 1; i < route.Count; i++)
            {
                BanditRoadGraph.RoadNode previous = BanditRoadGraph.Get(route[i - 1]);
                BanditRoadGraph.RoadNode node = BanditRoadGraph.Get(route[i]);
                float step = Vector3.Distance(previous.Position, node.Position);

                if (BanditRoadGraph.IsBridgedGap(route[i - 1], route[i]))
                {
                    // Counted apart from the road mileage: this stretch is open ground the router
                    // believes is drivable, not a road anybody drew.
                    gapLength += step;
                    gapCount++;
                    continue;
                }

                roadLength += step;
                byChart.TryGetValue(node.Chart, out float existing);
                byChart[node.Chart] = existing + step;
            }

            BanditRoadGraph.RoadNode first = BanditRoadGraph.Get(route[0]);
            BanditRoadGraph.RoadNode last = BanditRoadGraph.Get(route[route.Count - 1]);

            float offRoad = Vector3.Distance(from, first.Position) + Vector3.Distance(last.Position, to);
            float direct = Vector3.Distance(from, to);

            UnturnedChat.Say(caller, $"Route: {route.Count} node(s), {roadLength:0}m on road "
                + $"+ {offRoad:0}m off it, against {direct:0}m direct.", Color.green);

            if (gapCount > 0)
            {
                UnturnedChat.Say(caller, $"  Crosses {gapCount} gap(s) between roads, {gapLength:0}m "
                    + "of open ground in total.", Color.white);
            }

            foreach (KeyValuePair<EObjectChart, float> entry in byChart)
            {
                UnturnedChat.Say(caller, $"  {entry.Key}: {entry.Value:0}m", Color.white);
            }
        }
    }
}
