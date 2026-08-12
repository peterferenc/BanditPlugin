using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/bandit" - spawns a stationary fake-player bot a short distance in front of the caller
    /// that continuously turns to face whichever real player is nearest. No movement, no combat.
    /// </summary>
    public class CommandSpawnBandit : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "bandit";
        public string Help => "Spawns a stationary bandit bot that turns to face the nearest player.";
        public string Syntax => string.Empty;
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            UnturnedPlayer callerPlayer = (UnturnedPlayer)caller;
            Player unturnedPlayer = callerPlayer.Player;

            Vector3 origin = unturnedPlayer.look.aim.position;
            Vector3 direction = unturnedPlayer.look.aim.forward;

            Vector3 spawnPosition;
            if (Physics.Raycast(origin, direction, out RaycastHit hit, 50f, RayMasks.BLOCK_COLLISION))
            {
                spawnPosition = hit.point;
            }
            else
            {
                spawnPosition = unturnedPlayer.transform.position + unturnedPlayer.transform.forward * 3f;
            }

            // Face the bandit back toward whoever spawned it (LookAtNearestPlayer will immediately
            // override this once it starts scanning, but gives it a sane initial facing).
            float facingAngleDegrees = unturnedPlayer.transform.eulerAngles.y + 180f;

            Player bandit = FakePlayer.FakePlayerSpawner.Spawn(spawnPosition, facingAngleDegrees, "Bandit");
            if (bandit == null)
            {
                UnturnedChat.Say(caller, "Failed to spawn bandit - see server console for details.", Color.red);
                return;
            }

            UnturnedChat.Say(caller, "Bandit spawned.", Color.green);
        }
    }
}
