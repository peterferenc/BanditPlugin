using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// Shared body of "/banditshoot" and "/banditstop".
    ///
    /// Deliberately not a base class the commands inherit: Rocket registers commands by scanning
    /// the assembly for IRocketCommand implementations and calling Activator.CreateInstance on
    /// each one that has a parameterless constructor. An abstract command class only escapes that
    /// because its implicit constructor is protected rather than public - too subtle to rely on,
    /// when the cost of getting it wrong is every later command failing to register.
    /// </summary>
    internal static class BanditFireControl
    {
        /// <summary>
        /// Applies to every live bandit rather than the last one spawned, because fire control is
        /// a standing order for the field: you want the whole lot to stop while you walk around
        /// testing movement, not one of them.
        /// </summary>
        internal static void Apply(IRocketPlayer caller, bool holdFire)
        {
            List<BanditBotController> bandits = FakePlayerSpawner.GetActiveControllers();
            if (bandits.Count == 0)
            {
                Reply(caller, "No bandits spawned.", Color.red);
                return;
            }

            foreach (BanditBotController bandit in bandits)
            {
                bandit.HoldFire = holdFire;
            }

            Reply(caller, holdFire
                ? $"{bandits.Count} bandit(s) holding fire."
                : $"{bandits.Count} bandit(s) weapons free.", Color.green);
        }

        private static void Reply(IRocketPlayer caller, string message, Color color)
        {
            if (caller is Rocket.Unturned.Player.UnturnedPlayer)
            {
                UnturnedChat.Say(caller, message, color);
            }
            else
            {
                Rocket.Core.Logging.Logger.Log($"[Bandit] {message}");
            }
        }
    }

    /// <summary>"/banditshoot" - lets every bandit open fire again.</summary>
    public class CommandBanditShoot : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "banditshoot";
        public string Help => "Lets all bandits shoot again.";
        public string Syntax => string.Empty;
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditFireControl.Apply(caller, holdFire: false);
        }
    }

    /// <summary>
    /// "/banditstop" - every bandit stops shooting. They still track you and still take cover;
    /// only the trigger is disabled, along with aim-down-sights (vanilla PlayerStance refuses to
    /// sprint while aiming, so a bot told to hold fire can actually run somewhere).
    /// </summary>
    public class CommandBanditStop : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "banditstop";
        public string Help => "Makes all bandits hold fire. They still move and track you.";
        public string Syntax => string.Empty;
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditFireControl.Apply(caller, holdFire: true);
        }
    }
}
