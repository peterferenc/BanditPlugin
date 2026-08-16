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
        public string Help => "Drives the last spawned bandit's vehicle to the point you are looking at.";
        public string Syntax => "[stop]";
        public List<string> Aliases => new List<string> { "bvgoto" };
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditBotController bandit = FakePlayerSpawner.LastSpawnedController;
            if (bandit?.Driver == null)
            {
                UnturnedChat.Say(caller, "No bandit to command - spawn one with /bandit first.", Color.red);
                return;
            }

            if (command.Length > 0 && command[0].ToLowerInvariant() == "stop")
            {
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
    }
}
