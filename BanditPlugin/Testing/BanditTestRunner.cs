using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BanditPlugin.Commands;
using BanditPlugin.FakePlayer;
using Rocket.API;
using SDG.Unturned;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.Testing
{
    public enum BanditTestOutcome
    {
        Pass,
        Fail,

        /// <summary>The test itself threw something other than a failure - a bug in the test or the plugin.</summary>
        Error,

        /// <summary>Stopped by "/bandit test stop" before it finished.</summary>
        Aborted
    }

    public sealed class BanditTestResult
    {
        public string Name;
        public BanditTestOutcome Outcome;
        public string Message;
        public float Seconds;
        public string ArenaPoint;
        public List<string> Lines = new List<string>();
    }

    /// <summary>
    /// Runs <see cref="BanditTest"/>s one after another on the live server and writes down what
    /// happened.
    ///
    /// The enumerators are stepped here by hand rather than handed to StartCoroutine, for three
    /// reasons: a throw has to become a failed test rather than a log line, the per-test timeout and
    /// invariants have to be checked every frame whatever the test is waiting on, and a test must be
    /// cleaned up however it ends. Nested waits are handled with an explicit stack, so a test yields
    /// a helper's IEnumerator exactly as it would in a Unity coroutine.
    ///
    /// Results go to the log, to whoever started the run, and to Tests/results.json - the last of
    /// which is what tools/bandit-test.sh reads after an unattended run. Setting BANDIT_TEST in the
    /// server's environment runs the named tests (or "all") once the level has loaded, and
    /// BANDIT_TEST_EXIT=1 shuts the server down afterwards.
    /// </summary>
    [DisallowMultipleComponent]
    public class BanditTestRunner : MonoBehaviour
    {
        /// <summary>The line an unattended run is finished on. tools/bandit-test.sh waits for it.</summary>
        public const string FinishedMarker = "[BanditTest] Finished:";

        /// <summary>
        /// How long after the level loads before an unattended run starts. The map is loaded well
        /// before the navmesh graphs and the plugin's road graph are ready to be asked anything.
        /// </summary>
        private const float AutorunSettleSeconds = 5f;

        /// <summary>A pause between tests, for kicked bandits to leave and their corpses with them.</summary>
        private const float BetweenTestsSeconds = 1.5f;

        public bool IsRunning { get; private set; }

        public List<BanditTestResult> LastResults { get; private set; } = new List<BanditTestResult>();

        private bool _stopRequested;

        private void Start()
        {
            string requested = Environment.GetEnvironmentVariable("BANDIT_TEST");
            if (!string.IsNullOrEmpty(requested))
            {
                bool exit = Environment.GetEnvironmentVariable("BANDIT_TEST_EXIT") == "1";
                StartCoroutine(Autorun(requested, exit));
            }
        }

        private IEnumerator Autorun(string requested, bool exit)
        {
            Logger.Log($"[BanditTest] BANDIT_TEST={requested} - running once the level has loaded"
                + (exit ? ", then shutting down." : "."));

            while (!Level.isLoaded)
            {
                yield return null;
            }

            float settleUntil = Time.realtimeSinceStartup + AutorunSettleSeconds;
            while (Time.realtimeSinceStartup < settleUntil)
            {
                yield return null;
            }

            if (!BanditTests.TryResolve(requested, out List<BanditTest> tests, out string error)
                || !TryStart(tests, null, null, Vector3.forward, out error))
            {
                Logger.LogError($"[BanditTest] {error}");
                WriteResults(new List<BanditTestResult>(), error);
                Logger.Log($"{FinishedMarker} 0 passed, 0 failed - {error}");
            }

            while (IsRunning)
            {
                yield return null;
            }

            if (exit)
            {
                Provider.shutdown(0, "Bandit tests finished");
            }
        }

        /// <summary>
        /// Starts a run. With <paramref name="here"/> every test runs at that point, facing
        /// <paramref name="hereForward"/>; otherwise each runs at its arena point on this map.
        /// </summary>
        public bool TryStart(List<BanditTest> tests, IRocketPlayer watcher, Vector3? here, Vector3 hereForward,
            out string error)
        {
            if (IsRunning)
            {
                error = "Tests are already running - /bandit test stop ends them.";
                return false;
            }

            // Tests count hits and targets among their own bandits, and anyone else's on the field
            // would be shooting at them. A clean field is the only way a result means something.
            int others = FakePlayerSpawner.GetActiveControllers().Count;
            if (others > 0)
            {
                error = $"{others} bandit(s) already on the map - /bandit clear all first, so they cannot interfere.";
                return false;
            }

            error = null;
            _stopRequested = false;
            IsRunning = true;
            StartCoroutine(RunAll(tests, watcher, here, hereForward));
            return true;
        }

        public void Stop()
        {
            _stopRequested = true;
        }

        /// <summary>Where this run's screenshots go - one folder per run, under Tests/shots.</summary>
        public static string ShotDirectory { get; private set; }

        private IEnumerator RunAll(List<BanditTest> tests, IRocketPlayer watcher, Vector3? here, Vector3 hereForward)
        {
            List<BanditTestResult> results = new List<BanditTestResult>();

            // Whoever started the run watches it rather than taking part: bandits do not see them,
            // and their screen is photographed at each step. Put back as it was afterwards.
            Player observer = (watcher as Rocket.Unturned.Player.UnturnedPlayer)?.Player;
            bool wasObserving = observer != null && BanditObservers.Set(observer, true);

            // And everyone else too, for as long as the run lasts - a player who joins halfway
            // through a test is watching it, not part of it.
            BanditObservers.EveryoneObserves = true;
            ShotDirectory = BanditTestArena.TestsDirectory != null
                ? Path.Combine(BanditTestArena.TestsDirectory, "shots", DateTime.Now.ToString("yyyyMMdd-HHmmss"))
                : null;
            Say(watcher, $"Running {tests.Count} test(s): {string.Join(", ", tests.ConvertAll(t => t.Name).ToArray())}.",
                Color.white);

            foreach (BanditTest test in tests)
            {
                if (_stopRequested)
                {
                    break;
                }

                BanditTestResult result = new BanditTestResult { Name = test.Name };
                results.Add(result);

                Vector3 anchor;
                Vector3 forward;
                if (here.HasValue)
                {
                    anchor = here.Value;
                    forward = hereForward;
                    result.ArenaPoint = "here";
                }
                else if (BanditTestArena.TryGet(test.Name, out BanditTestArena.Point point, out string pointName))
                {
                    anchor = point.Position;
                    forward = point.Forward;
                    result.ArenaPoint = pointName;
                }
                else
                {
                    result.Outcome = BanditTestOutcome.Fail;
                    result.Message = $"no arena on {BanditWaypointStoreMap()} - stand somewhere open with cover "
                        + "nearby and run /bandit test arena set, or run the test with 'here'";
                    Report(watcher, result);
                    continue;
                }

                BanditTestContext context = new BanditTestContext(test, anchor, forward, watcher);
                float started = Time.time;

                Stack<IEnumerator> stack = new Stack<IEnumerator>();
                stack.Push(test.Run(context));
                float deadline = Time.time + test.TimeoutSeconds;

                result.Outcome = BanditTestOutcome.Pass;
                while (stack.Count > 0)
                {
                    if (_stopRequested)
                    {
                        result.Outcome = BanditTestOutcome.Aborted;
                        result.Message = "stopped by /bandit test stop";
                        break;
                    }

                    if (Time.time >= deadline)
                    {
                        result.Outcome = BanditTestOutcome.Fail;
                        result.Message = $"took longer than the test's {test.TimeoutSeconds:0}s limit";
                        break;
                    }

                    string violation;
                    try
                    {
                        violation = context.FirstViolation();
                    }
                    catch (Exception e)
                    {
                        result.Outcome = BanditTestOutcome.Error;
                        result.Message = $"an invariant threw: {e}";
                        break;
                    }

                    if (violation != null)
                    {
                        result.Outcome = BanditTestOutcome.Fail;
                        result.Message = violation;
                        break;
                    }

                    IEnumerator top = stack.Peek();
                    bool more;
                    try
                    {
                        more = top.MoveNext();
                    }
                    catch (BanditTestFailure failure)
                    {
                        result.Outcome = BanditTestOutcome.Fail;
                        result.Message = failure.Message;
                        break;
                    }
                    catch (Exception e)
                    {
                        result.Outcome = BanditTestOutcome.Error;
                        result.Message = e.ToString();
                        break;
                    }

                    if (!more)
                    {
                        // Back to the caller on this same frame, the way a finished nested
                        // coroutine hands straight back to the one that yielded it.
                        stack.Pop();
                        continue;
                    }

                    if (top.Current is IEnumerator nested)
                    {
                        stack.Push(nested);
                        continue;
                    }

                    yield return null;
                }

                if (result.Outcome == BanditTestOutcome.Pass)
                {
                    result.Message = context.Summary ?? "passed";
                }

                result.Seconds = Time.time - started;
                context.Snapshot(result.Outcome.ToString().ToLowerInvariant());

                // The screenshot is taken by the watcher's client and takes a moment to come back;
                // cleaning up straight away photographs an empty field.
                float settle = Time.time + 1f;
                while (Time.time < settle)
                {
                    yield return null;
                }

                result.Lines = context.Lines;
                context.Cleanup();
                Report(watcher, result);

                float resume = Time.time + BetweenTestsSeconds;
                while (Time.time < resume)
                {
                    yield return null;
                }
            }

            BanditObservers.EveryoneObserves = false;
            if (observer != null && !wasObserving)
            {
                BanditObservers.Set(observer, false);
            }

            LastResults = results;
            int passed = results.FindAll(r => r.Outcome == BanditTestOutcome.Pass).Count;
            string summary = $"{passed} passed, {results.Count - passed} failed";
            WriteResults(results, null);

            Say(watcher, $"Tests finished: {summary}.", passed == results.Count ? Color.green : Color.red);
            Logger.Log($"{FinishedMarker} {summary}");
            IsRunning = false;
        }

        private static string BanditWaypointStoreMap()
        {
            return Navigation.BanditWaypointStore.CurrentMap ?? "this map";
        }

        private static void Report(IRocketPlayer watcher, BanditTestResult result)
        {
            string line = $"{Label(result.Outcome)} {result.Name} ({result.Seconds:0.0}s) - {result.Message}";
            Logger.Log($"[BanditTest] {line}");
            if (watcher is Rocket.Unturned.Player.UnturnedPlayer)
            {
                BanditCommand.Reply(watcher, line, result.Outcome == BanditTestOutcome.Pass ? Color.green : Color.red);
            }
        }

        private static void Say(IRocketPlayer watcher, string message, Color color)
        {
            if (watcher is Rocket.Unturned.Player.UnturnedPlayer)
            {
                BanditCommand.Reply(watcher, message, color);
            }
            else
            {
                Logger.Log($"[BanditTest] {message}");
            }
        }

        public static string Label(BanditTestOutcome outcome)
        {
            switch (outcome)
            {
                case BanditTestOutcome.Pass: return "PASS";
                case BanditTestOutcome.Fail: return "FAIL";
                case BanditTestOutcome.Error: return "ERROR";
                default: return "ABORTED";
            }
        }

        /// <summary>
        /// Tests/results.json, overwritten by every run. Written by hand rather than with a JSON
        /// library because the plugin ships as one DLL and this is the only JSON it writes.
        /// </summary>
        private static void WriteResults(List<BanditTestResult> results, string error)
        {
            string directory = BanditTestArena.TestsDirectory;
            if (directory == null)
            {
                return;
            }

            StringBuilder json = new StringBuilder();
            json.Append("{\n");
            json.Append("  \"map\": ").Append(Quote(Navigation.BanditWaypointStore.CurrentMap)).Append(",\n");
            json.Append("  \"finished\": ").Append(Quote(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            json.Append("  \"error\": ").Append(Quote(error)).Append(",\n");
            json.Append("  \"results\": [");

            for (int i = 0; i < results.Count; i++)
            {
                BanditTestResult r = results[i];
                json.Append(i == 0 ? "\n" : ",\n");
                json.Append("    {\"name\": ").Append(Quote(r.Name))
                    .Append(", \"outcome\": ").Append(Quote(Label(r.Outcome)))
                    .Append(", \"seconds\": ").Append(r.Seconds.ToString("0.0", CultureInfo.InvariantCulture))
                    .Append(", \"arena\": ").Append(Quote(r.ArenaPoint))
                    .Append(", \"message\": ").Append(Quote(r.Message))
                    .Append(", \"log\": [");
                for (int j = 0; j < r.Lines.Count; j++)
                {
                    json.Append(j == 0 ? string.Empty : ", ").Append(Quote(r.Lines[j]));
                }
                json.Append("]}");
            }

            json.Append(results.Count > 0 ? "\n  ]\n}\n" : "]\n}\n");

            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "results.json"), json.ToString());
            }
            catch (Exception e)
            {
                Logger.LogError($"[BanditTest] Could not write results: {e.Message}");
            }
        }

        private static string Quote(string text)
        {
            if (text == null)
            {
                return "null";
            }

            StringBuilder quoted = new StringBuilder("\"");
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': quoted.Append("\\\""); break;
                    case '\\': quoted.Append("\\\\"); break;
                    case '\n': quoted.Append("\\n"); break;
                    case '\r': quoted.Append("\\r"); break;
                    case '\t': quoted.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            quoted.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            quoted.Append(c);
                        }
                        break;
                }
            }

            return quoted.Append('"').ToString();
        }
    }
}
