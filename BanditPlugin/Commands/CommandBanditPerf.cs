using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditperf" - what the last window of server frames cost, and "/banditperf reset" to start
    /// a new one.
    ///
    /// The measurement this is built for is a difference, not a reading: reset with no bandits
    /// down, wait a fixed time, read; spawn, reset, wait the same time, read again. A single
    /// absolute number says nothing, because a server that is idling and a server that is drowning
    /// both report whatever frame rate they were configured to target.
    ///
    /// Prints the bandit count alongside, so a line pasted into notes still says what it was
    /// measuring a week later.
    /// </summary>
    public class CommandBanditPerf : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "banditperf";
        public string Help => "Reports server frame time over the last window; 'reset' starts a new one.";
        public string Syntax => "[reset]";
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditPerfMonitor monitor = BanditPlugin.Instance != null ? BanditPlugin.Instance.Perf : null;
            if (monitor == null)
            {
                Reply(caller, "Performance monitor is not running.", Color.red);
                return;
            }

            if (command.Length > 0 && command[0].Equals("reset", System.StringComparison.OrdinalIgnoreCase))
            {
                monitor.Reset();
                Reply(caller, "Perf window reset. Let the scenario run, then /banditperf.", Color.yellow);
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
