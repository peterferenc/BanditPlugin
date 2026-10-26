using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using BanditPlugin.Navigation;
using Rocket.API;
using Rocket.Unturned.Player;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/bandit debug ..." - the tools for finding out why a bandit did what it did.
    ///
    ///   status [target]        what each bandit thinks it is doing - everyone, or just the target
    ///   perf [reset]           server frame time over the last window
    ///   cost [apply]           what the cost model thinks everything is worth
    ///   cover [target]         make one bandit take cover from you now, and explain the choice
    ///   navlog on|off|route    vehicle navigation commentary, and the last planned route drawn
    ///   roads [route|show]     the road graph, a route over it, or the graph drawn around you
    ///   clear                  wipe every debug marker in the world
    /// </summary>
    public static class BanditDebugCommand
    {
        public static void Execute(IRocketPlayer caller, string[] args)
        {
            string tool = args.Length > 0 ? args[0].ToLowerInvariant() : null;
            string[] rest = Rest(args, 1);

            switch (tool)
            {
                case "status":
                    if (rest.Length == 0)
                    {
                        Status(caller, null);
                    }
                    else if (BanditTargets.TryResolve(rest, 0, out BanditTarget target, out _, out string error))
                    {
                        Status(caller, target);
                    }
                    else
                    {
                        Reply(caller, error, Color.red);
                    }
                    break;

                case "perf":
                    Perf(caller, rest);
                    break;

                case "cost":
                    BanditCostCommand.Execute(caller, rest);
                    break;

                case "cover":
                    Cover(caller, rest);
                    break;

                case "navlog":
                    BanditNavLogCommand.Execute(caller, rest);
                    break;

                case "roads":
                    BanditRoadsCommand.Execute(caller, rest);
                    break;

                case "clear":
                    BanditCoverDebug.Clear();
                    BanditRouteDebug.Clear();
                    Reply(caller, "Cover, route and road markers cleared.", Color.green);
                    break;

                default:
                    Reply(caller, "Usage: /bandit debug status [target]  |  perf [reset]  |  cost [apply]  |  "
                        + "cover [target]  |  navlog on|off|route [s]  |  roads [route|show [r] [s]]  |  clear",
                        Color.yellow);
                    break;
            }
        }

        /// <summary>
        /// What each bandit thinks it is doing - everyone, or the bandits of one target.
        ///
        /// Movement failures are silent by nature: the bot just stands there, and nothing is logged
        /// because nothing went wrong. This prints the four things that actually explain it - which
        /// state the brain is in, whether it has a destination, whether it is pathing or steering
        /// blind, and whether its feet are being given a direction at all.
        /// </summary>
        public static void Status(IRocketPlayer caller, BanditTarget target)
        {
            // Convoys first: a column reports as one thing doing one thing, and reading it off
            // twenty individual bandits is not possible - none of them knows the state the convoy
            // is in.
            foreach (BanditConvoy convoy in BanditConvoy.All)
            {
                if (target == null || target.Convoy == convoy)
                {
                    Reply(caller, convoy.Describe(), Color.cyan);
                }
            }

            List<BanditBotController> bandits = target != null
                ? target.Members()
                : FakePlayerSpawner.GetActiveControllers();

            if (bandits.Count == 0)
            {
                Reply(caller, target != null ? $"{target.Label} has nobody left on the map." : "No bandits spawned.",
                    Color.red);
                return;
            }

            if (target != null)
            {
                Reply(caller, $"{target.Label}: {bandits.Count} bandit(s).", Color.green);
            }

            foreach (BanditBotController bandit in bandits)
            {
                if (bandit.Brain == null)
                {
                    Reply(caller, $"#{bandit.Id}: brain never initialised.", Color.red);
                    continue;
                }

                BanditBrain brain = bandit.Brain;
                string currentTarget = bandit.CurrentTarget != null
                    ? bandit.CurrentTarget.channel.owner.playerID.characterName
                    : "none";

                string destination = brain.Navigator.HasDestination
                    ? $"{Vector3.Distance(bandit.Self.transform.position, brain.Navigator.Destination):0}m " +
                      (brain.Navigator.IsFollowingPath ? "(A*)" : "(steering)")
                    : "none";

                // The stance vanilla settled on, not the one the brain asked for. They can differ -
                // PlayerStance refuses to duck in shallow water, and a crouch input beats a prone
                // one - so this is the only honest answer to "did the stance order take?".
                string stance = bandit.Self.stance != null
                    ? bandit.Self.stance.stance.ToString()
                    : "unknown";

                string kit = bandit.Profile != null ? bandit.Profile.KitName : "default";

                // Read off the bandit's live group rather than what it was spawned with, so a team
                // changed underneath it shows the side it is actually fighting on.
                string team = BanditTeams.Describe(bandit.Self);
                // The type as well as the number: two squads on the ground are usually two different
                // types, and which one a bandit belongs to explains the figures it is fighting with.
                string squad = bandit.Squad != null
                    ? $" sq{bandit.Squad.Id} {bandit.Squad.TypeName}"
                    : string.Empty;

                StatusLine(caller,
                    $"#{bandit.Id} [{kit}{squad} team {team}]: {brain.State}{(brain.State == BanditBrain.BanditState.Engage ? (brain.HuntingAngle ? " (hunting an angle)" : " (closing in)") : string.Empty)}, target {currentTarget}, dest {destination}, " +
                    $"holding {bandit.EquippedWeaponName}{(bandit.IsBursting ? " (bursting)" : string.Empty)}" +
                    $"{(bandit.IsSuppressingFire ? " (suppressing)" : string.Empty)}, " +
                    $"stance {stance}, " +
                    $"moving {(brain.MoveDirection.sqrMagnitude > 0.0001f ? "yes" : "no")}, " +
                    $"orders [{(bandit.HoldFire ? "hold fire" : "weapons free")}, " +
                    $"cover {(brain.CoverEnabled ? "on" : "off")}, " +
                    $"peek {(brain.PeekEnabled ? "on" : "off")}, " +
                    $"stance {brain.StanceOrder}]" +
                    $"{(brain.PatrolEnabled ? ", patrolling" : string.Empty)}" +
                    $", fire: {bandit.DescribeFireBlock()}",
                    Color.white);
            }
        }

        /// <summary>
        /// A status line to the caller and, when that is a player, to the server log as well - chat
        /// truncates and scrolls away, and the log is what can be read back afterwards.
        /// </summary>
        private static void StatusLine(IRocketPlayer caller, string line, Color color)
        {
            Reply(caller, line, color);
            if (caller is UnturnedPlayer)
            {
                Rocket.Core.Logging.Logger.Log($"[Bandit] status: {line}");
            }
        }

        /// <summary>
        /// What the last window of server frames cost, and "reset" to start a new one.
        ///
        /// The measurement this is built for is a difference, not a reading: reset with no bandits
        /// down, wait a fixed time, read; spawn, reset, wait the same time, read again. A single
        /// absolute number says nothing, because a server that is idling and a server that is
        /// drowning both report whatever frame rate they were configured to target.
        ///
        /// Prints the bandit count alongside, so a line pasted into notes still says what it was
        /// measuring a week later.
        /// </summary>
        private static void Perf(IRocketPlayer caller, string[] args)
        {
            BanditPerfMonitor monitor = BanditPlugin.Instance != null ? BanditPlugin.Instance.Perf : null;
            if (monitor == null)
            {
                Reply(caller, "Performance monitor is not running.", Color.red);
                return;
            }

            if (args.Length > 0 && Is(args[0], "reset"))
            {
                monitor.Reset();
                Reply(caller, "Perf window reset. Let the scenario run, then /bandit debug perf.", Color.yellow);
                return;
            }

            BanditPerfReport report = monitor.Snapshot();
            if (report == null)
            {
                Reply(caller, "No frames recorded yet.", Color.red);
                return;
            }

            int bandits = FakePlayerSpawner.GetActiveControllers().Count;

            Reply(caller,
                $"[perf] {report.Frames} frames over {report.WindowSeconds:0.0}s, {bandits} bandits"
                + $"{(report.Truncated ? " (window full - showing the most recent frames only)" : string.Empty)}",
                Color.white);

            Reply(caller,
                $"[perf] frame ms: mean {report.MeanMs:0.00} ({report.MeanFps:0} fps), "
                + $"p50 {report.MedianMs:0.00}, p95 {report.P95Ms:0.00}, "
                + $"p99 {report.P99Ms:0.00}, max {report.MaxMs:0.0}",
                Color.white);

            // The line that usually matters. A mean pinned to the target frame rate hides a server
            // doing far too much work; the frames that blew through the target cannot hide.
            Reply(caller,
                $"[perf] hitches: {report.FramesOver33Ms} over 33ms ({report.HitchesPerMinute:0.0}/min), "
                + $"{report.FramesOver50Ms} over 50ms ({report.BadHitchesPerMinute:0.0}/min)",
                report.FramesOver50Ms > 0 ? Color.yellow : Color.white);

            Reply(caller,
                $"[perf] GC: gen0 {report.Gen0Collections} ({report.Gen0PerMinute:0.0}/min), "
                + $"gen1 {report.Gen1Collections}, gen2 {report.Gen2Collections}",
                Color.white);
        }

        /// <summary>
        /// Makes one bandit take cover from *you*, right now, and says what it found.
        ///
        /// Cover normally only triggers when the bot is exposed and its search timer is up, which
        /// makes it awkward to observe and impossible to debug: a search that finds nothing looks
        /// exactly like a search that never ran. This forces one and reports the outcome - which
        /// spot, what kind, how far - or, when it finds nothing, the tally of which test rejected
        /// the candidates.
        /// </summary>
        private static void Cover(IRocketPlayer caller, string[] args)
        {
            UnturnedPlayer callerPlayer = RequirePlayer(caller, "debug cover");
            if (callerPlayer == null)
            {
                return;
            }

            if (!BanditTargets.TryResolve(args, 0, out BanditTarget target, out _, out string error))
            {
                Reply(caller, error, Color.red);
                return;
            }

            List<BanditBotController> bandits = BanditOrders.Ready(caller, target);
            if (bandits.Count == 0)
            {
                return;
            }

            if (bandits.Count > 1)
            {
                Reply(caller, $"{target.Label} is {bandits.Count} bandits - the cover search explains one: "
                    + "/bandit debug cover <number> (/bandit list shows them).", Color.red);
                return;
            }

            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;
            BanditBotController bandit = bandits[0];
            Vector3 threatEye = BanditGeometry.EyeOf(callerPlayer.Player);

            List<BanditCoverCandidateReport> reports = new List<BanditCoverCandidateReport>();
            bool found = bandit.Brain.TryTakeCoverFrom(threatEye, out BanditCoverSearchStats stats, reports);

            Vector3? chosen = found ? bandit.Brain.CurrentCover.Position : (Vector3?)null;
            int drawn = BanditCoverDebug.Show(reports, chosen, config.CoverDebugSeconds, config.CoverDebugMaxMarkers);

            // Also to the server console: chat truncates, and the tally is the whole diagnostic.
            Rocket.Core.Logging.Logger.Log($"[Bandit] Cover search from {threatEye} for bandit #{bandit.Id} at "
                + $"{bandit.Self.transform.position}: {stats}"
                + (found ? $" -> chose {bandit.Brain.CurrentCover.Position}" : " -> nothing")
                + $" (drew {drawn} of {reports.Count} markers)");

            Reply(caller, stats.ToString(), Color.white);

            // Say so when the drawing is a sample. The tallies above always cover the whole search,
            // so without this a capped draw reads as a smaller search than actually ran.
            if (drawn < reports.Count)
            {
                Reply(caller,
                    $"Drawing {drawn} of {reports.Count} candidates ({BanditCoverDebug.SplatterCount(drawn)} decals) - "
                    + "all viable spots plus an even sample of the rejects. Raise CoverDebugMaxMarkers to see more.",
                    Color.grey);
            }

            Reply(caller, BanditCoverDebug.Legend + " /bandit debug clear removes them.", Color.grey);

            if (!found)
            {
                Reply(caller, "No cover found.", Color.yellow);
                return;
            }

            BanditCoverSpot spot = bandit.Brain.CurrentCover;
            float distance = Vector3.Distance(bandit.Self.transform.position, spot.Position);

            string kind = spot.RequiresCrouch
                ? "crouch cover (safe ducked, can shoot standing)"
                : spot.CanPeek
                    ? "hard cover (hidden; steps out to shoot)"
                    : "hard cover (hidden, no firing angle)";

            Reply(caller, $"Bandit #{bandit.Id} taking {kind} - {distance:0.#}m away.", Color.green);
        }
    }
}
