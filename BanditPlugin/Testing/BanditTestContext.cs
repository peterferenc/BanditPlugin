using System;
using System.Collections;
using System.Collections.Generic;
using BanditPlugin.Commands;
using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using Action = System.Action;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.Testing
{
    /// <summary>
    /// What a running test works with: where it is, the bandits it has spawned, and the waits and
    /// checks it is written in.
    ///
    /// Positions are given relative to the arena - metres to the right of it and ahead of it - so
    /// one test runs unchanged wherever it is pointed, whether that is a recorded arena point or
    /// the spot somebody is looking at.
    /// </summary>
    public sealed class BanditTestContext
    {
        private readonly IRocketPlayer _watcher;
        private readonly List<Func<string>> _invariants = new List<Func<string>>();
        private readonly List<Action> _cleanups = new List<Action>();

        public BanditTestContext(BanditTest test, Vector3 anchor, Vector3 forward, IRocketPlayer watcher)
        {
            Test = test;
            Anchor = anchor;
            forward.y = 0f;
            Forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
            Right = Vector3.Cross(Vector3.up, Forward);
            _watcher = watcher;
            Config = BanditPlugin.Instance.Configuration.Instance;
        }

        public BanditTest Test { get; }
        public Vector3 Anchor { get; }
        public Vector3 Forward { get; }
        public Vector3 Right { get; }
        public BanditConfiguration Config { get; }

        /// <summary>Every bandit this test spawned, dead or alive, in the order it spawned them.</summary>
        public List<BanditBotController> Spawned { get; } = new List<BanditBotController>();

        /// <summary>What the test said along the way, kept with its result.</summary>
        public List<string> Lines { get; } = new List<string>();

        /// <summary>The closing remark on a pass - what was measured, not just that it worked.</summary>
        public string Summary { get; set; }

        /// <summary>A point on the ground, <paramref name="right"/> metres across and <paramref name="ahead"/> metres forward.</summary>
        public Vector3 At(float right, float ahead)
        {
            return BanditPlacement.SnapToGround(Anchor + Right * right + Forward * ahead);
        }

        /// <summary>
        /// Spawns a bandit for this test, facing <paramref name="faceToward"/>.
        ///
        /// It comes out the way /bandit spawn makes one - holding fire unless the configuration says
        /// otherwise, with its kit's standing orders - except that it ignores players on no team.
        /// Someone watching a test is usually standing in it, and a test that shoots its own audience
        /// tells you nothing about the bandits.
        ///
        /// The brain is created a frame later, in Start, so yield <see cref="WaitForBrains"/> before
        /// giving orders.
        /// </summary>
        public BanditBotController Spawn(string kitName, string teamName, Vector3 position, Vector3 faceToward)
        {
            BanditKit kit = Config.FindKit(kitName) ?? Config.FindKit(Config.DefaultKit);
            if (kit == null)
            {
                throw new BanditTestFailure($"no kit called '{kitName}', and no default kit either");
            }

            BanditTeam team = teamName != null ? BanditTeams.Find(Config, teamName) : BanditTeams.Default(Config);
            if (team == null)
            {
                throw new BanditTestFailure($"no team called '{teamName}' in the configuration - "
                    + $"teams are {string.Join(", ", BanditTeams.Names(Config).ToArray())}");
            }

            Vector3 facing = faceToward - position;
            facing.y = 0f;
            float yaw = facing.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(facing).eulerAngles.y : 0f;

            Player player = FakePlayerSpawner.Spawn(position, yaw, $"Test {team.Label} {kit.Name}", kit, team);
            BanditBotController bandit = player != null ? FakePlayerSpawner.LastSpawnedController : null;
            if (bandit == null)
            {
                throw new BanditTestFailure($"could not spawn a {kit.Name} - see the server log");
            }

            bandit.HostileToUngrouped = false;
            Spawned.Add(bandit);
            return bandit;
        }

        /// <summary>
        /// Builds a piece of cover for the test: a barricade by item ID, standing on the ground at
        /// <paramref name="position"/> with its broad face towards <paramref name="faceToward"/>.
        ///
        /// Which way a barricade model faces is a fact about how it was authored, so rather than
        /// keep a table of them this measures the result: if the piece came out end-on to the
        /// threat it is removed and put down again turned a quarter. The size it ended up is
        /// logged, because whether something is crouch cover or standing cover is its height.
        /// Removed again however the test ends.
        /// </summary>
        public Transform PlaceBarricade(ushort itemId, Vector3 position, Vector3 faceToward, out Bounds bounds)
        {
            ItemBarricadeAsset asset = Assets.find(EAssetType.ITEM, itemId) as ItemBarricadeAsset;
            if (asset == null)
            {
                throw new BanditTestFailure($"item {itemId} is not a barricade on this server");
            }

            Vector3 toward = faceToward - position;
            toward.y = 0f;
            toward = toward.sqrMagnitude > 0.0001f ? toward.normalized : Forward;
            Vector3 lateral = Vector3.Cross(Vector3.up, toward);
            float yaw = Quaternion.LookRotation(toward).eulerAngles.y;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                Quaternion rotation = BarricadeManager.getRotation(asset, 0f, yaw + attempt * 90f, 0f);
                Transform placed = BarricadeManager.dropNonPlantedBarricade(new Barricade(asset),
                    position + Vector3.up * asset.offset, rotation, 0UL, 0UL);

                if (placed == null)
                {
                    throw new BanditTestFailure($"could not place {asset.FriendlyName} at {position} - "
                        + "outside the map's barricade regions?");
                }

                bounds = MeasureColliders(placed);
                float width = Mathf.Abs(lateral.x) * bounds.size.x + Mathf.Abs(lateral.z) * bounds.size.z;
                float depth = Mathf.Abs(toward.x) * bounds.size.x + Mathf.Abs(toward.z) * bounds.size.z;

                if (width + 0.01f >= depth || attempt == 1)
                {
                    Transform keep = placed;
                    OnCleanup(() => DestroyBarricade(keep));
                    Note($"placed {asset.FriendlyName}: {width:0.0}m wide, {depth:0.0}m deep, "
                        + $"{bounds.max.y - position.y:0.00}m tall.");
                    return placed;
                }

                DestroyBarricade(placed);
            }

            throw new BanditTestFailure("unreachable");
        }

        /// <summary>
        /// The solid extent of a freshly placed barricade. Physics is synced first: a collider that
        /// was created this frame reports bounds from before its transform was applied - a sandbag
        /// once measured 470m across - and only solid colliders count, since a trigger volume is
        /// not something a bullet stops at.
        /// </summary>
        private static Bounds MeasureColliders(Transform root)
        {
            Physics.SyncTransforms();

            Bounds bounds = new Bounds(root.position, Vector3.zero);
            bool any = false;
            foreach (Collider collider in root.GetComponentsInChildren<Collider>())
            {
                if (collider == null || collider.isTrigger || !collider.enabled)
                {
                    continue;
                }

                if (!any)
                {
                    bounds = collider.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            return bounds;
        }

        private static void DestroyBarricade(Transform barricade)
        {
            if (barricade == null)
            {
                return;
            }

            BarricadeDrop drop = BarricadeManager.FindBarricadeByRootTransform(barricade);
            if (drop != null && BarricadeManager.tryGetRegion(barricade, out byte x, out byte y, out ushort plant, out _))
            {
                BarricadeManager.destroyBarricade(drop, x, y, plant);
            }
        }

        /// <summary>Waits for every spawned bandit to have its brain, i.e. to be able to take orders.</summary>
        public IEnumerator WaitForBrains()
        {
            return WaitUntil(() => Spawned.TrueForAll(b => b == null || b.Brain != null), 5f,
                "the bandits' brains to start");
        }

        /// <summary>Yields until <paramref name="condition"/> holds, and fails the test if it has not within the timeout.</summary>
        public IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds, string what)
        {
            float deadline = Time.time + timeoutSeconds;
            while (!condition())
            {
                if (Time.time >= deadline)
                {
                    throw new BanditTestFailure($"timed out after {timeoutSeconds:0}s waiting for {what}");
                }

                yield return null;
            }
        }

        /// <summary>Lets the game run for a while. Invariants are still checked every frame of it.</summary>
        public IEnumerator Wait(float seconds)
        {
            float until = Time.time + seconds;
            while (Time.time < until)
            {
                yield return null;
            }
        }

        /// <summary>Fails the test, with the reason, if the condition is false.</summary>
        public void Check(bool condition, string failure)
        {
            if (!condition)
            {
                throw new BanditTestFailure(failure);
            }
        }

        /// <summary>
        /// Something that must stay true for the whole test - "no bandit ever damages its own
        /// side". Return null while it holds, and the reason once it does not. The runner asks
        /// every frame, so a violation fails the test on the frame it happens, wherever the test
        /// itself happens to be waiting.
        /// </summary>
        public void Always(Func<string> invariant)
        {
            _invariants.Add(invariant);
        }

        /// <summary>Undoes something the test hooked into the game. Runs however the test ends.</summary>
        public void OnCleanup(Action cleanup)
        {
            _cleanups.Add(cleanup);
        }

        /// <summary>
        /// A line for the record only - the log and the result - without the chat message and the
        /// screenshot <see cref="Log"/> takes. For the running commentary a test keeps every time
        /// something changes, which would flood chat.
        /// </summary>
        public void Note(string message)
        {
            Lines.Add(message);
            Logger.Log($"[BanditTest] {Test.Name}: {message}");
        }

        public void Log(string message)
        {
            Lines.Add(message);
            Logger.Log($"[BanditTest] {Test.Name}: {message}");

            if (_watcher is UnturnedPlayer)
            {
                BanditCommand.Reply(_watcher, $"[test {Test.Name}] {message}", Color.grey);
            }

            Snapshot();
        }

        private int _shots;

        /// <summary>
        /// Photographs the watcher's screen, if a player is watching, into this run's shots folder,
        /// and notes the file against the test. Taken at every logged step and at the result, so a
        /// failure comes with pictures of the moments leading up to it.
        /// </summary>
        public void Snapshot(string label = null)
        {
            if (!(_watcher is UnturnedPlayer player) || BanditTestRunner.ShotDirectory == null)
            {
                return;
            }

            _shots++;
            string file = $"{Test.Name}-{_shots:00}{(label != null ? "-" + label : string.Empty)}.jpg";
            string path = System.IO.Path.Combine(BanditTestRunner.ShotDirectory, file);
            if (BanditObservers.Capture(player.Player, path))
            {
                Lines.Add($"screenshot {path}");
            }
        }

        /// <summary>Whose bullet this was, if it was one of this test's bandits.</summary>
        public BanditBotController FindBySteamId(CSteamID steamId)
        {
            foreach (BanditBotController bandit in Spawned)
            {
                if (bandit != null && bandit.Self != null
                    && bandit.Self.channel.owner.playerID.steamID == steamId)
                {
                    return bandit;
                }
            }

            return null;
        }

        /// <summary>A bandit's label in test output: its number, kit and team.</summary>
        public static string Name(BanditBotController bandit)
        {
            return bandit == null || bandit.Self == null
                ? "a removed bandit"
                : $"#{bandit.Id} {bandit.KitName} ({BanditTeams.Describe(bandit.Self)})";
        }

        internal string FirstViolation()
        {
            foreach (Func<string> invariant in _invariants)
            {
                string violation = invariant();
                if (violation != null)
                {
                    return violation;
                }
            }

            return null;
        }

        /// <summary>Removes everything the test spawned and unhooks everything it hooked.</summary>
        internal void Cleanup()
        {
            foreach (Action cleanup in _cleanups)
            {
                try
                {
                    cleanup();
                }
                catch (Exception e)
                {
                    Logger.LogError($"[BanditTest] {Test.Name}: cleanup threw: {e}");
                }
            }

            foreach (BanditBotController bandit in Spawned)
            {
                if (bandit != null)
                {
                    FakePlayerSpawner.Despawn(bandit);
                }
            }
        }
    }
}
