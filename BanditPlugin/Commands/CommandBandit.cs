using System.Collections.Generic;
using Rocket.API;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/bandit" - the plugin's one command. Everything it does is a word after it:
    ///
    ///   /bandit spawn | squad spawn | event spawn | convoy spawn | vehicle spawn
    ///   /bandit [target] shoot|cover|peek|stance|patrol|goto|wave|seat|drive ...
    ///   /bandit list | clear [target] | route ... | team ... | info ... | debug ... | test ... | help [topic]
    ///
    /// A target is a bandit's number, "squad 2", "event 1", "convoy 1", "vehicle 4", "team red" or
    /// "all"; with none, an order goes to the last thing spawned. See <see cref="BanditTargets"/>.
    ///
    /// One command rather than one per job, so there is one word to remember and nothing to collide
    /// with another plugin's commands. Rocket checks permissions per command, so the split between
    /// bandit.spawn and bandit.team is made here instead.
    /// </summary>
    public class CommandBandit : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "bandit";
        public string Help => "Spawns and commands AI bandits. /bandit help lists everything.";
        public string Syntax => "spawn|squad|event|convoy|vehicle|<target> <order>|list|clear|route|team|info|debug|test|help";
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { SpawnPermission, TeamPermission };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            args = args ?? new string[0];

            if (args.Length == 0)
            {
                ReplyHelp(caller, null);
                return;
            }

            string first = args[0].ToLowerInvariant();
            string second = args.Length > 1 ? args[1].ToLowerInvariant() : null;

            if (first == "help")
            {
                ReplyHelp(caller, second);
                return;
            }

            // Team management is the one part of /bandit a player may have without being allowed to
            // spawn anything. "/bandit team red shoot on" is an order instead, and falls through.
            if (first == "team" && (second == null || BanditTeamCommand.IsSubcommand(second)))
            {
                if (RequirePermission(caller, TeamPermission))
                {
                    BanditTeamCommand.Execute(caller, Rest(args, 1));
                }
                return;
            }

            if (!RequirePermission(caller, SpawnPermission))
            {
                return;
            }

            if (second == "spawn")
            {
                switch (first)
                {
                    case "squad":
                        BanditSquadSpawnCommand.Execute(caller, Rest(args, 2));
                        return;
                    case "event":
                        BanditEventSpawnCommand.Execute(caller, Rest(args, 2));
                        return;
                    case "convoy":
                        BanditConvoySpawnCommand.Execute(caller, Rest(args, 2));
                        return;
                    case "vehicle":
                        BanditVehicleSpawnCommand.Execute(caller, Rest(args, 2));
                        return;
                }
            }

            switch (first)
            {
                case "spawn":
                    BanditSpawnCommand.Execute(caller, Rest(args, 1));
                    return;
                case "list":
                    BanditListCommand.Execute(caller);
                    return;
                case "clear":
                    BanditClearCommand.Execute(caller, Rest(args, 1));
                    return;
                case "route":
                    BanditRouteCommand.Execute(caller, Rest(args, 1));
                    return;
                case "info":
                    BanditInfoCommand.Execute(caller, Rest(args, 1));
                    return;
                case "debug":
                    BanditDebugCommand.Execute(caller, Rest(args, 1));
                    return;
                case "test":
                    BanditTestCommand.Execute(caller, Rest(args, 1));
                    return;
                case "observe":
                    Observe(caller, Rest(args, 1));
                    return;
            }

            ExecuteOrder(caller, args);
        }

        /// <summary>
        /// "/bandit observe on|off" - bandits stop seeing you, so you can stand in a fight and
        /// watch it. See <see cref="BanditObservers"/>.
        /// </summary>
        private static void Observe(IRocketPlayer caller, string[] args)
        {
            Rocket.Unturned.Player.UnturnedPlayer player = RequirePlayer(caller, "observe");
            if (player == null)
            {
                return;
            }

            if (args.Length == 0)
            {
                Reply(caller, BanditObservers.IsObserver(player.Player)
                    ? "You are observing - bandits do not see you. /bandit observe off."
                    : "You are not observing. /bandit observe on hides you from bandits.", Color.white);
                return;
            }

            if (!TryParseOnOff(args[0], out bool on))
            {
                Reply(caller, "Usage: /bandit observe on|off", Color.yellow);
                return;
            }

            BanditObservers.Set(player.Player, on);
            Reply(caller, on
                ? "Observing: bandits no longer see you or target you. Stray rounds still hurt."
                : "No longer observing - bandits see you again.", Color.green);
        }

        /// <summary>
        /// "[target] order ..." - or a target on its own, which reports on it.
        /// </summary>
        private static void ExecuteOrder(IRocketPlayer caller, string[] args)
        {
            if (!BanditTargets.TryParse(args, 0, out BanditTarget target, out int consumed, out string error))
            {
                Reply(caller, error, Color.red);
                return;
            }

            if (consumed == 0 && !BanditOrders.IsOrder(args[0]))
            {
                Reply(caller, $"Unknown command '{args[0]}'. /bandit help lists them.", Color.red);
                return;
            }

            if (consumed > 0 && consumed == args.Length)
            {
                BanditDebugCommand.Status(caller, target);
                return;
            }

            string order = args[consumed];
            if (!BanditOrders.IsOrder(order))
            {
                Reply(caller, $"Unknown order '{order}'. Orders: {string.Join(", ", BanditOrders.Words)}.", Color.red);
                return;
            }

            if (target == null && !BanditTargets.TryLast(out target, out error))
            {
                Reply(caller, error, Color.red);
                return;
            }

            BanditOrders.Execute(caller, target, order, Rest(args, consumed + 1));
        }

        private static void ReplyHelp(IRocketPlayer caller, string topic)
        {
            switch (topic)
            {
                case "spawn":
                    Say(caller,
                        "/bandit spawn [<kit>] [team:<t>] - one bandit where you look, holding fire until ordered.",
                        "/bandit squad spawn [<type>] [<metres>|marker] [team:<t>] - a squad, already fighting.",
                        "/bandit event spawn <cost> [<metres>|marker] [team:<t>] [seed:<n>] - squads and vehicles bought with points.",
                        "/bandit convoy spawn <cost> [vehicles:<n>] [crew:<n>] [noroads] [team:<t>] [seed:<n>] - a column down the convoy route.",
                        "/bandit vehicle spawn <type|id|GUID> [<metres>|marker] [team:<t>] - one crewed vehicle.",
                        "/bandit info kits|squads|vehicles|event lists what each one takes.");
                    break;

                case "orders":
                case "order":
                    Say(caller,
                        "/bandit [target] shoot|cover|peek|patrol on|off",
                        "/bandit [target] stance stand|crouch|prone|free",
                        "/bandit [target] goto [marker] - walk there; a group goes in formation.",
                        "/bandit [target] wave",
                        "/bandit [target] seat driver|gunner[n]|exit - one bandit at a time, except exit.",
                        "/bandit [target] drive [marker|route [noroads]|stop] - whoever in the target is at a wheel.",
                        "No target means the last thing spawned. /bandit help targets.");
                    break;

                case "targets":
                case "target":
                    Say(caller,
                        "A target goes before the order: 7 (bandit #7), squad 2, event 1, convoy 1, vehicle 4, "
                        + "team red, all, last.",
                        "With no target, an order goes to the last thing spawned.",
                        "/bandit <target> alone reports on it. /bandit list shows every number.");
                    break;

                case "route":
                    Say(caller,
                        "/bandit route patrol|convoy add [marker] - a point where you stand, or at your marker.",
                        "/bandit route patrol|convoy remove [<n>] - by list number, or the nearest within 10m.",
                        "/bandit route patrol|convoy list|clear. /bandit route alone lists both.");
                    break;

                case "team":
                    Say(caller,
                        "/bandit team - your team. /bandit team list|join <t>|leave|set <player> <t>.",
                        "Bandits never shoot their own team, and always shoot the others.");
                    break;

                case "debug":
                    Say(caller,
                        "/bandit debug status [target]  |  perf [reset]  |  cost [apply]  |  cover [target]",
                        "/bandit debug navlog on|off|route [s]  |  roads [route|show [r] [s]]  |  clear");
                    break;

                case "test":
                    Say(caller,
                        "/bandit test [list] - the end-to-end tests, and this map's arena points.",
                        "/bandit test run all|<name>[,<name>] [here] - run them at the arena, or where you look.",
                        "/bandit test stop | results  |  arena set [<name>] | remove <name>",
                        "Needs an empty field - /bandit clear all first.");
                    break;

                case "clear":
                    Say(caller,
                        "/bandit clear [target] - removes the target and its vehicles; no target is the last thing spawned.",
                        "/bandit clear all - every bandit and every vehicle this plugin spawned.");
                    break;

                default:
                    Say(caller,
                        "/bandit spawn | squad spawn | event spawn | convoy spawn | vehicle spawn",
                        "/bandit [target] shoot|cover|peek|stance|patrol|goto|wave|seat|drive ...",
                        "/bandit list | clear [target] | route | team | info | debug | test | observe on|off",
                        "/bandit help spawn|orders|targets|route|team|debug|test|clear for the details.");
                    break;
            }
        }

        private static void Say(IRocketPlayer caller, params string[] lines)
        {
            foreach (string line in lines)
            {
                Reply(caller, line, Color.white);
            }
        }
    }
}
