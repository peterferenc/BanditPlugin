using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;

namespace BanditZombiePlugin.Commands
{
    /// <summary>
    /// "/banditclear" - removes every bot this plugin spawned, freeing the player slots they hold
    /// and letting you disconnect without the game treating them as other players nearby.
    /// </summary>
    public class CommandClearBandits : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "banditclear";
        public string Help => "Removes all spawned bandit bots.";
        public string Syntax => string.Empty;
        public List<string> Aliases => new List<string> { "clearbandits" };
        public List<string> Permissions => new List<string> { "banditzombie.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            int removed = FakePlayer.FakePlayerSpawner.RemoveAllBots();

            if (caller is Rocket.Unturned.Player.UnturnedPlayer)
            {
                UnturnedChat.Say(caller, removed > 0 ? $"Removed {removed} bandit(s)." : "No bandits to remove.", Color.green);
            }
            else
            {
                Rocket.Core.Logging.Logger.Log($"[BanditZombie] Removed {removed} bandit(s).");
            }
        }
    }
}
