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
    ///   /bandit               spawn one of the default class in front of you
    ///   /bandit mg            spawn a specific class - see /bandit kits
    ///   /bandit kits          list the classes and the ranges each fights at
    ///   /bandit stance prone  hold a stance - stand, crouch, prone, or free to choose again
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
        public string Help => "Spawns a bandit of a given class, or sets a standing order (cover/peek/shoot) for all of them.";
        public string Syntax => "[<kit>|kits|cover start|peek start|shoot start]";
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;

            // Lifted out first so "/bandit mg team:blue" is still a one-word kit spawn rather than
            // looking like one of the two-word orders below. See BanditTeams.ExtractTeamArgument.
            command = BanditTeams.ExtractTeamArgument(command, out string requestedTeam);

            BanditTeam team = BanditTeams.Find(config, requestedTeam);
            if (team == null && !string.IsNullOrEmpty(requestedTeam))
            {
                Reply(caller, $"No team called '{requestedTeam}'. Teams: "
                    + $"{string.Join(", ", BanditTeams.Names(config).ToArray())}.", Color.red);
                return;
            }

            if (command.Length == 0)
            {
                Spawn(caller, config.FindKit(config.DefaultKit), team, config);
                return;
            }

            if (command[0].Equals("kits", System.StringComparison.OrdinalIgnoreCase))
            {
                ReplyKits(caller, config);
                return;
            }

            // A single argument is a kit name. Checked before the order parsing below rather than
            // after, so a kit called "cover" would be unreachable rather than ambiguous - and the
            // orders keep their two-word shape, which is what keeps the two apart at all.
            if (command.Length == 1)
            {
                BanditKit kit = config.FindKit(command[0]);
                if (kit == null)
                {
                    Reply(caller, $"No kit called '{command[0]}'. Known kits: "
                        + $"{string.Join(", ", config.KitNames().ToArray())}.", Color.red);
                    return;
                }

                Spawn(caller, kit, team, config);
                return;
            }

            // Handled before the start/stop parsing below, because this one takes a stance name as
            // its second word rather than start or stop.
            if (command[0].Equals("stance", System.StringComparison.OrdinalIgnoreCase))
            {
                if (command.Length < 2 || !TryParseStance(command[1], out BanditStance stance))
                {
                    Reply(caller, "Usage: /bandit stance stand|crouch|prone|free", Color.yellow);
                    return;
                }

                ApplyToAll(caller, bandit => bandit.Brain.SetStanceOrder(stance),
                    stance == BanditStance.Free
                        ? "choosing their own stance again"
                        : $"holding {command[1].ToLowerInvariant()}");
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

        /// <summary>
        /// "free" is the way back to letting each class decide for itself - without it, ordering a
        /// squad to stand would permanently disable the machinegunner's own prone-on-contact.
        /// </summary>
        private static bool TryParseStance(string argument, out BanditStance stance)
        {
            switch (argument.ToLowerInvariant())
            {
                case "stand":
                case "standing":
                    stance = BanditStance.Stand;
                    return true;
                case "crouch":
                case "crouched":
                    stance = BanditStance.Crouch;
                    return true;
                case "prone":
                    stance = BanditStance.Prone;
                    return true;
                case "free":
                case "auto":
                    stance = BanditStance.Free;
                    return true;
                default:
                    stance = BanditStance.Free;
                    return false;
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

        private static void Spawn(IRocketPlayer caller, BanditKit kit, BanditTeam team, BanditConfiguration config)
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

            // The class and the side go in the name, because that is the one label visible from
            // across a field and through a scope - which is the only practical way to tell five of
            // them apart once a squad is on the ground, or to tell two sides apart once both are.
            BanditTeam spawnTeam = team ?? BanditTeams.Default(config);
            string kitName = kit != null && !string.IsNullOrEmpty(kit.Name) ? kit.Name : null;
            string prefix = spawnTeam != null ? spawnTeam.Label : "Bandit";
            string displayName = kitName != null ? $"{prefix} {kitName}" : prefix;

            Player bandit = FakePlayerSpawner.Spawn(spawnPosition, facingAngleDegrees, displayName, kit, spawnTeam);
            if (bandit == null)
            {
                Reply(caller, "Failed to spawn bandit - see server console for details.", Color.red);
                return;
            }

            BanditBotController controller = FakePlayerSpawner.LastSpawnedController;
            string orders = controller?.Brain != null
                ? DescribeStandingOrders(controller)
                : "no standing orders yet";

            Reply(caller, $"Spawned {displayName} on team {BanditTeams.Describe(bandit)} ({orders}).", Color.green);
        }

        /// <summary>
        /// What the kit already switched on, so it is obvious which of the usual commands still
        /// need giving by hand. A kit that turns nothing on behaves exactly as a bandit always did.
        /// </summary>
        private static string DescribeStandingOrders(BanditBotController bandit)
        {
            List<string> orders = new List<string>();
            if (!bandit.HoldFire)
            {
                orders.Add("weapons free");
            }
            if (bandit.Brain.CoverEnabled)
            {
                orders.Add("cover");
            }
            if (bandit.Brain.PeekEnabled)
            {
                orders.Add("peek");
            }
            if (bandit.Brain.ProneEnabled)
            {
                orders.Add("prone");
            }

            return orders.Count > 0
                ? string.Join(", ", orders.ToArray())
                : "holding fire, no orders";
        }

        private static void ReplyKits(IRocketPlayer caller, BanditConfiguration config)
        {
            List<string> names = config.KitNames();
            if (names.Count == 0)
            {
                Reply(caller, "No kits configured.", Color.red);
                return;
            }

            Reply(caller, $"Kits: {string.Join(", ", names.ToArray())}. "
                + $"'/bandit' alone spawns '{config.DefaultKit}'.", Color.white);

            foreach (string name in names)
            {
                // Resolved rather than read off the kit, so a kit that leaves a figure at -1 to
                // inherit the global one reports the number it will actually fight with.
                BanditProfile profile = BanditProfile.FromKit(config, config.FindKit(name));
                Reply(caller, $"  {name}: fires to {profile.FireRange:0}m, notices at {profile.TargetAcquireRange:0}m, "
                    + $"fights at {profile.PreferredEngagementRange:0}m"
                    + (profile.BurstFire ? ", bursts" : ", single shots")
                    + (profile.AdvanceOnTarget ? ", closes in" : string.Empty), Color.grey);
            }
        }

        private static void ReplyUsage(IRocketPlayer caller)
        {
            Reply(caller, "Usage: /bandit  |  /bandit <kit>  |  /bandit <kit> team:<team>  |  "
                + "/bandit kits  |  /bandit stance stand|crouch|prone|free  |  /bandit cover start|stop  "
                + "|  /bandit peek start|stop  |  /bandit shoot start|stop", Color.yellow);
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
