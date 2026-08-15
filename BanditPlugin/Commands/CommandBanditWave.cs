using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditwave" - the last spawned bandit turns to face you, puts its weapon away, waves, and
    /// arms itself again.
    ///
    /// The holstering is not decoration: vanilla drops any gesture request from a player with
    /// something in their hands, so this is the only sequence that produces a wave a real player
    /// could have produced. The bandit stands still for it and picks up whatever it was doing -
    /// patrol, cover, a /banditgoto order - the moment it has the weapon back.
    /// </summary>
    public class CommandBanditWave : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "banditwave";
        public string Help => "Makes the last spawned bandit holster its weapon, wave at you, then re-arm.";
        public string Syntax => string.Empty;
        public List<string> Aliases => new List<string> { "bwave" };
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditBotController bandit = FakePlayerSpawner.LastSpawnedController;
            if (bandit == null)
            {
                UnturnedChat.Say(caller, "No bandit to command - spawn one with /bandit first.", Color.red);
                return;
            }

            if (bandit.Self.life != null && bandit.Self.life.isDead)
            {
                UnturnedChat.Say(caller, "That bandit is dead.", Color.red);
                return;
            }

            Player callerPlayer = ((UnturnedPlayer)caller).Player;
            if (!bandit.TryPlayGesture(EPlayerGesture.WAVE, callerPlayer))
            {
                UnturnedChat.Say(caller, "That bandit is already mid-gesture.", Color.yellow);
                return;
            }

            UnturnedChat.Say(caller, "Bandit waving.", Color.green);
        }
    }
}
