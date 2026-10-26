# Testing

The plugin carries its own end-to-end tests. They run on a real server against real bandits -
the same spawner, brain, navigator and guns as everything else - and check what a person would
check by watching: did it get there, did it hide, did it shoot the right side.

Bandits are fake players, so no client is needed. Every test is bandits against bandits, which
means the whole suite runs on a headless server with nobody connected.

## The tests

| Test | What it checks |
|---|---|
| `goto` | One bandit walks 20m to a point, arrives within the time limit, and stays there - not stuck, not stopped short, not wandering off afterwards. Reports whether it used A* or steered. |
| `formation` | Three bandits walk 20m to the formation slots `/bandit goto` lays out for a group, all reach their slots, and finish more than a metre apart. |
| `cover` | A rifleman standing in the open, seen by an enemy 25m away, turns cover on: it has to pick a spot, reach it, and then be hidden from that enemy's own line-of-sight test - held for 3 seconds, not sampled once. Both sides hold fire. |
| `teams` | Two red riflemen and one blue, 30m apart, weapons free. Red and blue have to hit each other within 60s; no bandit may ever target a teammate or land a hit on one. Hits are read from the game's own damage event. |

Each test fails as soon as something is clearly wrong and says why - `stopped walking 14m short
of the destination (brain state Idle)`, `no hits between red and blue in 60s` with each bandit's
fire block, and so on. A test can never hang: every wait has a timeout, and the runner enforces
a hard limit per test on top.

Test bandits ignore players on no team, so you can stand in the arena and watch. A player who has
joined red or blue is fair game, though.

## The arena

Tests need somewhere to run. A test uses the arena point named after it, or the one called
`arena` if it has none, so one good spot serves the whole suite:

- open ground, roughly level, with **30m of clear line straight ahead** of where you face
- **something to hide behind** - rocks, trees, a wall - within about 30m, for `cover`
- not on a road a convoy uses, and not in water

Stand on it, face the way the tests should run, and record it:

```
/bandit test arena set              the point every test falls back to
/bandit test arena set cover        a separate spot for one test, if it needs one
/bandit test arena remove cover
```

Points are kept per map in `Rocket/Plugins/BanditPlugin/Tests/<map>.arena.txt`, as
`name x y z yaw` - hand-editable.

## Running them in game

```
/bandit clear all                   the field has to be empty
/bandit test                        lists the tests and this map's arena points
/bandit test run all                every test, at the arena
/bandit test run cover here         one test, where you are looking - for watching
/bandit test run goto,teams
/bandit test stop                   aborts, and cleans up the running test
/bandit test results                the last run again
```

Progress and results come back in chat, and go to the server log with a `[BanditTest]` prefix.

## Running them unattended

`tools/bandit-test.sh` does the whole loop: builds the plugin, deploys it, starts the server, runs
the tests, shuts the server down and exits with whether they passed.

```
tools/bandit-test.sh                all tests
tools/bandit-test.sh cover          one
tools/bandit-test.sh -n goto,teams  skip the build, test what is installed
```

It refuses to run while the server is up. A DLL replaced under a running server corrupts it
rather than updating it, and a restart is the only way to load new plugin code anyway. Each run
costs one server boot, about half a minute on top of the tests themselves.

Underneath, it sets two environment variables, which work just as well by hand:

| Variable | Effect |
|---|---|
| `BANDIT_TEST=all` | Once the level has loaded, run these tests - `all`, a name, or `name,name`. |
| `BANDIT_TEST_EXIT=1` | Shut the server down when they finish. |

Results are written to `Rocket/Plugins/BanditPlugin/Tests/results.json`: one entry per test, with
its outcome (`PASS`, `FAIL`, `ERROR`, `ABORTED`), duration, the arena point it used, the reason,
and everything it logged.

## What they can't see

- **Anything client-side.** Whether your client draws a bandit, or what the player list shows,
  is outside what the server can observe. Watch for it.
- **Exact outcomes.** Physics, asynchronous A* and aim spread mean the same test does not play out
  the same way twice. The checks are deliberately coarse - reached within 3m, hidden 80% of the
  time - so a pass is meaningful and a fail is worth looking at, but a single fail in a firefight
  test can still be bad luck. Run it again before chasing it.
- **The map.** A test is only as good as its arena. A `cover` failure that says no spot was found
  is about the arena first - `/bandit debug cover` on the spot shows what the search rejected.

## Writing a test

A test is a class deriving from `BanditTest` in `BanditPlugin/Testing/`, added to
`BanditTests.All`. `Run` is a coroutine that works through a `BanditTestContext`:

| Member | Use |
|---|---|
| `t.At(right, ahead)` | A point on the ground, relative to the arena and the way it faces. |
| `t.Spawn(kit, team, position, faceToward)` | A bandit owned by the test, and removed with it. |
| `yield return t.WaitForBrains()` | Before giving orders - the brain starts a frame after spawning. |
| `yield return t.WaitUntil(condition, seconds, what)` | Fails with `timed out ... waiting for <what>`. |
| `yield return t.Wait(seconds)` | Let the game run. |
| `t.Check(condition, reason)` | Fails with the reason if false. |
| `t.Always(() => violation or null)` | Checked every frame for the whole test. |
| `t.OnCleanup(action)` | Unhook whatever the test hooked into, however it ends. |
| `t.Log(line)`, `t.Summary` | What happened, for the log, the watcher and results.json. |

Throwing `BanditTestFailure` from anywhere fails the test. Any other exception is reported as
`ERROR`, because that is a bug in the test or the plugin rather than a behaviour that fell short.
