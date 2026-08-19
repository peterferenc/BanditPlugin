using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditgoto" - sends the most recently spawned bandit to wherever the caller is looking,
    /// picked the same way /bandit picks its spawn point.
    ///
    /// The bandit walks there under its own steam: A* inside nav volumes, direct steering outside
    /// them, and it will still break off to fight anyone it sees on the way.
    /// </summary>
    public class CommandBanditGoto : IRocketCommand
    {
        /// <summary>Longer than /bandit's 50m - this is a "go over there" order, not a spawn.</summary>
        private const float MaxTargetDistance = 512f;

        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "banditgoto";
        public string Help => "Sends the last spawned bandit to the point you are looking at.";
        public string Syntax => string.Empty;
        public List<string> Aliases => new List<string> { "bgoto" };
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditBotController bandit = FakePlayerSpawner.LastSpawnedController;
            if (bandit == null)
            {
                UnturnedChat.Say(caller, NoBandit, Color.red);
                return;
            }

            if (bandit.Brain == null)
            {
                UnturnedChat.Say(caller, "That bandit never finished initialising - see the server console.", Color.red);
                return;
            }

            // A seated bandit has no feet to steer, so this order would be accepted and then do
            // nothing at all - which is exactly how it looked before this check existed.
            if (bandit.Driver != null && bandit.Driver.IsSeated)
            {
                UnturnedChat.Say(caller, "That bandit is in a vehicle - use /banditvgoto to drive it, "
                    + "or /banditv exit first.", Color.red);
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

            bandit.Brain.GoTo(hit.point);

            float distance = Vector3.Distance(bandit.Self.transform.position, hit.point);
            UnturnedChat.Say(caller, $"Bandit moving out - {distance:0}m.", Color.green);
        }
    }
}
