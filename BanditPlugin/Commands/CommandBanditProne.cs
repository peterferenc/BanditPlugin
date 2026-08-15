using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Unturned.Chat;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditprone" - drops the last spawned bandit to the ground, or stands it back up.
    ///
    ///   /banditprone         toggle
    ///   /banditprone start   lie down
    ///   /banditprone stop    stand up
    ///
    /// Acts on the last spawned bandit rather than every one of them, like /banditwave and
    /// /banditcover: this is a stance you try on one bot and watch, not an order for the field.
    ///
    /// Lying down is deliberately nothing more than a stance here. The bandit keeps its patrol,
    /// its cover order and any /banditgoto it was given, and crawls them - which is the only way
    /// to see whether prone movement works at all before the machinegunner's behaviour depends
    /// on it.
    /// </summary>
    public class CommandBanditProne : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "banditprone";
        public string Help => "Makes the last spawned bandit lie down, stand up, or toggle.";
        public string Syntax => "[start|stop]";
        public List<string> Aliases => new List<string> { "bprone" };
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditBotController bandit = FakePlayerSpawner.LastSpawnedController;
            if (bandit?.Brain == null)
            {
                Reply(caller, "No bandit to command - spawn one with /bandit first.", Color.red);
                return;
            }

            if (bandit.Self.life != null && bandit.Self.life.isDead)
            {
                Reply(caller, "That bandit is dead.", Color.red);
                return;
            }

            bool prone;
            if (command.Length == 0)
            {
                prone = !bandit.Brain.ProneEnabled;
            }
            else if (!TryParseStartStop(command[0], out prone))
            {
                Reply(caller, "Usage: /banditprone [start|stop]", Color.yellow);
                return;
            }

            bandit.Brain.SetProneEnabled(prone);

            // The stance itself changes on the next input packet, up to PlayerInput.RATE away, so
            // reading player.stance back here would still show the old one. Report the order given,
            // and point at the one case vanilla can refuse it outright - /banditstatus prints the
            // stance the bot really ended up in.
            string note = bandit.Self.stance != null && bandit.Self.stance.stance == EPlayerStance.SWIM
                ? " It is in water, though, and vanilla forces a swimming player upright - expect it to stay standing."
                : string.Empty;

            Reply(caller, (prone ? "Bandit going prone." : "Bandit standing up.") + note, Color.green);
        }

        private static bool TryParseStartStop(string argument, out bool start)
        {
            switch (argument.ToLowerInvariant())
            {
                case "start":
                    start = true;
                    return true;
                case "stop":
                    start = false;
                    return true;
                default:
                    start = false;
                    return false;
            }
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
}
