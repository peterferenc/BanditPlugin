using System.Collections.Generic;
using BanditPlugin.Testing;
using Rocket.API;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/bandit test ..." - the end-to-end tests, run on this server against real bandits.
    ///
    ///   /bandit test [list]                  the tests, and this map's arena points
    ///   /bandit test run all|&lt;name&gt;[,&lt;name&gt;] [here]
    ///   /bandit test stop | results
    ///   /bandit test arena set [&lt;name&gt;] | remove &lt;name&gt;
    ///
    /// "here" runs at the point you are looking at, facing the way you are facing, which is how a
    /// test is watched. Without it each test runs at its arena point, which is what an unattended
    /// run uses - see <see cref="BanditTestRunner"/>.
    /// </summary>
    public static class BanditTestCommand
    {
        private const float MaxHereDistance = 100f;

        public static void Execute(IRocketPlayer caller, string[] args)
        {
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            BanditTestRunner runner = BanditPlugin.Instance.Tests;

            switch (verb)
            {
                case "list":
                    List(caller);
                    return;

                case "run":
                    Run(caller, runner, Rest(args, 1));
                    return;

                case "stop":
                    if (!runner.IsRunning)
                    {
                        Reply(caller, "No tests running.", Color.yellow);
                        return;
                    }

                    runner.Stop();
                    Reply(caller, "Stopping after this frame - the running test is cleaned up and reported as aborted.",
                        Color.yellow);
                    return;

                case "results":
                    Results(caller, runner);
                    return;

                case "arena":
                    Arena(caller, Rest(args, 1));
                    return;

                default:
                    Reply(caller, "Usage: /bandit test list | run all|<name> [here] | stop | results | arena set|remove",
                        Color.yellow);
                    return;
            }
        }

        private static void List(IRocketPlayer caller)
        {
            foreach (BanditTest test in BanditTests.All)
            {
                Reply(caller, $"  {test.Name}: {test.Description}", Color.white);
            }

            Dictionary<string, BanditTestArena.Point> points = BanditTestArena.Load();
            if (points.Count == 0)
            {
                Reply(caller, "No arena on this map yet - stand somewhere open with some cover nearby and run "
                    + "/bandit test arena set. Or add 'here' to a run.", Color.yellow);
                return;
            }

            List<string> names = new List<string>(points.Keys);
            Reply(caller, $"Arena points: {string.Join(", ", names.ToArray())}.", Color.grey);
        }

        private static void Run(IRocketPlayer caller, BanditTestRunner runner, string[] args)
        {
            bool here = false;
            string request = null;
            foreach (string arg in args)
            {
                if (Is(arg, "here"))
                {
                    here = true;
                }
                else
                {
                    request = request == null ? arg : request + "," + arg;
                }
            }

            if (!BanditTests.TryResolve(request ?? "all", out List<BanditTest> tests, out string error))
            {
                Reply(caller, error, Color.red);
                return;
            }

            Vector3? anchor = null;
            Vector3 forward = Vector3.forward;
            if (here)
            {
                UnturnedPlayer player = RequirePlayer(caller, "'here'");
                if (player == null)
                {
                    return;
                }

                Player self = player.Player;
                if (!Physics.Raycast(self.look.aim.position, self.look.aim.forward, out RaycastHit hit,
                        MaxHereDistance, RayMasks.BLOCK_COLLISION))
                {
                    Reply(caller, $"Look at a point on the ground within {MaxHereDistance:0}m to run the tests there.",
                        Color.red);
                    return;
                }

                anchor = hit.point;
                forward = self.transform.forward;
            }

            if (!runner.TryStart(tests, caller, anchor, forward, out error))
            {
                Reply(caller, error, Color.red);
            }
        }

        private static void Results(IRocketPlayer caller, BanditTestRunner runner)
        {
            if (runner.LastResults.Count == 0)
            {
                Reply(caller, runner.IsRunning ? "Still running - nothing finished yet." : "No tests have run yet.",
                    Color.yellow);
                return;
            }

            foreach (BanditTestResult result in runner.LastResults)
            {
                Reply(caller, $"{BanditTestRunner.Label(result.Outcome)} {result.Name} ({result.Seconds:0.0}s) - "
                    + result.Message, result.Outcome == BanditTestOutcome.Pass ? Color.green : Color.red);
            }
        }

        private static void Arena(IRocketPlayer caller, string[] args)
        {
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : null;
            string name = args.Length > 1 ? args[1].ToLowerInvariant() : BanditTestArena.DefaultPoint;

            switch (verb)
            {
                case "set":
                    UnturnedPlayer player = RequirePlayer(caller, "arena set");
                    if (player == null)
                    {
                        return;
                    }

                    Vector3 position = player.Player.transform.position;
                    float yaw = player.Player.transform.eulerAngles.y;
                    if (!BanditTestArena.Set(name, position, yaw))
                    {
                        Reply(caller, "Could not save the arena point - see the server log.", Color.red);
                        return;
                    }

                    Reply(caller, $"Arena point '{name}' set here, facing the way you are. Tests spawn on this spot "
                        + "and use the ground up to 30m ahead.", Color.green);
                    return;

                case "remove":
                    if (args.Length < 2)
                    {
                        Reply(caller, "Usage: /bandit test arena remove <name>", Color.yellow);
                        return;
                    }

                    Reply(caller, BanditTestArena.Remove(name)
                        ? $"Removed arena point '{name}'."
                        : $"No arena point called '{name}'.", Color.green);
                    return;

                default:
                    Reply(caller, "Usage: /bandit test arena set [<name>] | remove <name>. A test uses the point "
                        + "named after it, or 'arena'.", Color.yellow);
                    return;
            }
        }
    }
}
