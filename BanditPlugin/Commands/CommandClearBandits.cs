using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
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
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            int removed = FakePlayer.FakePlayerSpawner.RemoveAllBots();

            // The bots are kicked above, so every squad is now empty - dropping them here rather
            // than waiting for the next prune keeps a cleared field from leaving squad objects
            // holding references to players that no longer exist.
            FakePlayer.BanditSquad.ClearAll();
            FakePlayer.BanditEvent.ClearAll();
            FakePlayer.BanditConvoy.ClearAll();

            // Vehicles outlive their crews completely, so kicking the bots is not enough: without
            // this, an afternoon of /banditevent leaves the map covered in abandoned trucks. Only
            // the ones this plugin spawned are touched - anything a mapper placed stays put.
            int vehicles = FakePlayer.BanditVehicleSpawner.DestroyAll();

            string report = removed > 0 || vehicles > 0
                ? $"Removed {removed} bandit(s) and {vehicles} spawned vehicle(s)."
                : "No bandits or spawned vehicles to remove.";

            Reply(caller, report, Color.green);
        }
    }
}
