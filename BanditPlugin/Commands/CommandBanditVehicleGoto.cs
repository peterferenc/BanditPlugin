using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using BanditPlugin.Navigation;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditvgoto" - drives the last spawned bandit's vehicle to wherever the caller is looking,
    /// picked the same way /banditgoto picks its point.
    ///
    /// The on-foot /banditgoto does nothing from a seat, and cannot: it steers feet, and a seated
    /// bandit has none. This is its counterpart, and the difference is not only which body moves.
    /// A bandit walks a route the navmesh was baked for; a vehicle has to fit down it, so every
    /// heading is width-swept before it is driven and the trip stops when nothing fits rather than
    /// grinding into the gap. "Blocked 40m out" is a real and expected outcome here.
    ///
    /// "/banditvgoto stop" abandons the trip and leaves the vehicle where it is.
    /// </summary>
    public class CommandBanditVehicleGoto : IRocketCommand
    {
        /// <summary>Same reach as /banditgoto - this is a "drive over there" order.</summary>
        private const float MaxTargetDistance = 512f;

        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "banditvgoto";
        public string Help => "Drives the last spawned bandit's vehicle to the point you are looking at; "
            + "'marker' routes over the roads to your map marker, 'wp' drives the recorded route.";
        public string Syntax => "[marker|wp [noroads]|stop]";
        public List<string> Aliases => new List<string> { "bvgoto" };
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditBotController bandit = FakePlayerSpawner.LastSpawnedController;
            if (bandit?.Driver == null)
            {
                UnturnedChat.Say(caller, NoBandit, Color.red);
                return;
            }

            if (command.Length > 0 && command[0].ToLowerInvariant() == "stop")
            {
                BanditRouteDrive.Stop(bandit);
                bandit.Driver.StopDriving();
                UnturnedChat.Say(caller, "Bandit holding station.", Color.green);
                return;
            }

            if (!bandit.Driver.IsSeated)
            {
                UnturnedChat.Say(caller, "That bandit is on foot - put it in a vehicle with /banditv drive, "
                    + "or send it walking with /banditgoto.", Color.red);
                return;
            }

            if (command.Length > 0 && (command[0].ToLowerInvariant() == "wp"
                || command[0].ToLowerInvariant() == "route"))
            {
                DriveRoute(caller, bandit, BanditConvoyRoute.Current.Count > 0
                        ? BanditConvoyRoute.Current
                        : BanditWaypointStore.Current,
                    BanditConvoyRoute.Current.Count > 0 ? "/banditevent wp" : "/banditwp",
                    command);
                return;
            }

            if (command.Length > 0 && BanditPlacement.IsMarkerRequest(command[0]))
            {
                DriveToMarker(caller, bandit, command);
                return;
            }

            Player callerPlayer = ((UnturnedPlayer)caller).Player;
            Vector3 origin = callerPlayer.look.aim.position;
            Vector3 direction = callerPlayer.look.aim.forward;

            if (!Physics.Raycast(origin, direction, out RaycastHit hit, MaxTargetDistance, RayMasks.BLOCK_COLLISION))
            {
                UnturnedChat.Say(caller, "Look at a point on the ground within 512m.", Color.red);
                return;
            }

            if (!bandit.Driver.TrySetDestination(hit.point, out string reason))
            {
                UnturnedChat.Say(caller, $"Bandit staying put: {reason}.", Color.red);
                return;
            }

            // Worth saying out loud, because it is the number that decides whether the trip is
            // plausible at all. A bandit can walk through a 1m gate; the thing it is now driving
            // may be four metres wide, and /banditstatus will report "blocked" rather than
            // "arrived" if the route has nothing that size in it.
            BanditVehicleFootprint footprint = bandit.Driver.Footprint;
            float distance = Vector3.Distance(bandit.Self.transform.position, hit.point);

            UnturnedChat.Say(caller,
                $"Bandit driving out - {distance:0}m, "
                + $"{footprint.HalfWidth * 2f:0.0}m wide by {footprint.HalfLength * 2f:0.0}m long. "
                + "/banditstatus for progress.",
                Color.green);
        }

        /// <summary>
        /// "/banditvgoto marker" - drives to the map marker the way a convoy would, over the road
        /// graph, rather than straight at it.
        ///
        /// The distinction is the whole reason this exists. The plain form of this command aims at
        /// whatever you are looking at and steers at it; between towns there is no navmesh, so that
        /// is a straight line across the fields with obstacle dodging bolted on. This plans the same
        /// road route a convoy plans and follows it the same way, which makes it the one-hop version
        /// of a convoy and the quickest way to find out whether a stretch of road drives at all.
        /// </summary>
        private static void DriveToMarker(IRocketPlayer caller, BanditBotController bandit, string[] command)
        {
            Player player = ((UnturnedPlayer)caller).Player;

            if (player.quests == null || !player.quests.isMarkerPlaced)
            {
                UnturnedChat.Say(caller, "No map marker placed.", Color.red);
                return;
            }

            // A marker is a click on the map, so the height it carries is whatever the client sent
            // rather than the ground under it, and the road snap compares heights.
            Vector3 point = player.quests.markerPosition;
            point.y = LevelGround.getHeight(point);

            DriveRoute(caller, bandit, new[] { point }, "your map marker", command);
        }

        /// <summary>
        /// Sends the bandit along a route on its own, over the roads.
        ///
        /// Deliberately the same route planner and the same driver a convoy uses - the point of this
        /// is to be a convoy with everything except the driving removed, so a line it takes badly
        /// here is a line a column would take badly too, and one it takes well narrows the problem
        /// to the column.
        /// </summary>
        /// <param name="route">The points to drive through, in order.</param>
        /// <param name="source">Where they came from, so the reply says which list was used - there
        /// are two, and picking the wrong one silently is how "wp does not work" happens.</param>
        private static void DriveRoute(IRocketPlayer caller, BanditBotController bandit,
            IReadOnlyList<Vector3> route, string source, string[] command)
        {
            if (route == null || route.Count < 1)
            {
                UnturnedChat.Say(caller, "No waypoints on this map. /banditevent wp set records a "
                    + "convoy route where you stand, /banditwp records a patrol route.", Color.red);
                return;
            }

            bool useRoads = true;
            for (int i = 1; i < command.Length; i++)
            {
                string word = command[i].ToLowerInvariant();
                if (word == "noroads" || word == "offroad" || word == "useroads:false")
                {
                    useRoads = false;
                }
            }

            if (!BanditRouteDrive.Start(bandit, route, useRoads, out string summary))
            {
                UnturnedChat.Say(caller, $"Cannot drive the route: {summary}.", Color.red);
                return;
            }

            BanditVehicleFootprint footprint = bandit.Driver.Footprint;

            UnturnedChat.Say(caller, $"Driving {route.Count} point(s) from {source} - {summary}. "
                + $"{footprint.HalfWidth * 2f:0.0}m wide by {footprint.HalfLength * 2f:0.0}m long.",
                Color.green);

            UnturnedChat.Say(caller, "/banditnavlog on for the commentary, /banditvgoto stop to end it.",
                Color.grey);
        }
    }
}
