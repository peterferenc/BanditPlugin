using System;
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
    /// "/bandit" - spawns a bandit, and with a subcommand sets a standing order for every live one.
    ///
    ///   /bandit               spawn one in front of you
    ///   /bandit cover start   look for cover and move to it, re-finding it as the threat moves
    ///   /bandit cover stop    stop where you are and stay there
    ///   /bandit peek start    once in cover, alternate hiding with stepping out to shoot
    ///   /bandit peek stop     stay down
    ///   /bandit shoot start   weapons free
    ///   /bandit shoot stop    hold fire
    ///
    /// A spawned bandit does none of these until told: it stands, tracks whoever it can see, and
    /// waits. That makes each behaviour something you switch on and watch in isolation, which is
    /// the whole point of a test bot.
    ///
    /// The orders apply to every live bandit rather than the last one spawned, because they are
    /// orders for the field: you want the whole lot to hold fire while you walk around testing
    /// movement, not one of them.
    /// </summary>
    public class CommandSpawnBandit : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "bandit";
        public string Help => "Spawns a bandit, or sets a standing order (cover/peek/shoot) for all of them.";
        public string Syntax => "[cover|peek|shoot] [start|stop]";
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            if (command.Length == 0)
            {
                Spawn(caller);
                return;
            }

            if (command.Length < 2 || !TryParseStartStop(command[1], out bool start))
            {
                ReplyUsage(caller);
                return;
            }

            switch (command[0].ToLowerInvariant())
            {
                case "cover":
                    ApplyToAll(caller, bandit => bandit.Brain.SetCoverEnabled(start),
                        start ? "looking for cover" : "holding position");
                    break;

                case "peek":
                    ApplyToAll(caller, bandit => bandit.Brain.SetPeekEnabled(start),
                        start ? "peeking from cover" : "staying down in cover");
                    break;

                case "shoot":
                    // Inverted: "shoot start" is weapons free, which is HoldFire off.
                    ApplyToAll(caller, bandit => bandit.HoldFire = !start,
                        start ? "weapons free" : "holding fire");
                    break;

                default:
                    ReplyUsage(caller);
                    break;
            }
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

        /// <summary>
        /// Runs an order against every live bandit. Skips any whose brain never initialised rather
        /// than throwing halfway through the list and leaving the rest on the old order.
        /// </summary>
        private static void ApplyToAll(IRocketPlayer caller, Action<BanditBotController> order, string description)
        {
            List<BanditBotController> bandits = FakePlayerSpawner.GetActiveControllers();
            if (bandits.Count == 0)
            {
                Reply(caller, "No bandits spawned.", Color.red);
                return;
            }

            int applied = 0;
            foreach (BanditBotController bandit in bandits)
            {
                if (bandit.Brain == null)
                {
                    continue;
                }

                order(bandit);
                applied++;
            }

            if (applied == 0)
            {
                Reply(caller, "No bandits ready to take orders yet - try again in a moment.", Color.red);
                return;
            }

            Reply(caller, $"{applied} bandit(s) {description}.", Color.green);
        }

        private static void Spawn(IRocketPlayer caller)
        {
            if (!(caller is UnturnedPlayer callerPlayer))
            {
                Reply(caller, "Spawning a bandit places it in front of you, so it has to be run in-game.", Color.red);
                return;
            }

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

            // Face the bandit back toward whoever spawned it. Its target scan will override this as
            // soon as it sees someone, but it gives it a sane initial facing.
            float facingAngleDegrees = unturnedPlayer.transform.eulerAngles.y + 180f;

            Player bandit = FakePlayerSpawner.Spawn(spawnPosition, facingAngleDegrees, "Bandit");
            if (bandit == null)
            {
                Reply(caller, "Failed to spawn bandit - see server console for details.", Color.red);
                return;
            }

            Reply(caller, "Bandit spawned. It will stand and watch until told otherwise - "
                + "/bandit shoot start, /bandit cover start, /bandit peek start.", Color.green);
        }

        private static void ReplyUsage(IRocketPlayer caller)
        {
            Reply(caller, "Usage: /bandit  |  /bandit cover start|stop  |  /bandit peek start|stop  "
                + "|  /bandit shoot start|stop", Color.yellow);
        }

        private static void Reply(IRocketPlayer caller, string message, Color color)
        {
            if (caller is UnturnedPlayer)
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
