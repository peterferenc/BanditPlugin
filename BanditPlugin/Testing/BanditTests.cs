using System.Collections;
using System.Collections.Generic;
using BanditPlugin.Commands;
using BanditPlugin.FakePlayer;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Testing
{
    /// <summary>
    /// Every test the plugin ships, in the order "all" runs them - cheapest and most fundamental
    /// first, so a broken walk is reported as that rather than as a cover test that never got there.
    /// </summary>
    public static class BanditTests
    {
        public static readonly List<BanditTest> All = new List<BanditTest>
        {
            new GotoTest(),
            new FormationTest(),
            new CoverTest(),
            new TeamsTest(),

            // The foundations, kit by kit: shooting, cover, peeking - one bandit, then a squad.
            new ShootTest("rifleman"),
            new ShootTest("mg"),
            new ShootTest("marksman"),
            new ShootTest("breacher"),

            // The MG never hits and the rest do: these pull its differences apart. Stance - it
            // crouches on contact where the rifleman stands - and distance, because a hit report
            // sent with the shot is only accepted within about 54m of a bullet that has just left
            // the barrel (UseableGun.ballistics with Ballistics on, Ballistic_Travel 10).
            new ShootTest("mg", 60f, BanditStance.Stand, "-stand"),
            new ShootTest("mg", 40f, null, "-40"),
            new ShootTest("rifleman", 50f, BanditStance.Crouch, "-crouch"),
            new ShootTest("rifleman", 50f, BanditStance.Prone, "-prone"),
            new ShootTest("rifleman", 80f, BanditStance.Stand, "-80"),
            new SandbagCoverTest("cover-low", "rifleman", 3, 1),
            new SandbagCoverTest("cover-high", "rifleman", 3, 2),
            new SandbagCoverTest("cover-stack", "rifleman", 1, 2),
            new PeekTest("rifleman", 3, 2),
            new PeekTest("rifleman", 1, 2, "-stack"),
            new SquadCoverTest(),

            // The same, against a target that moves: across in front, walking and running, near
            // and far, straight at the bandit; pacing in front of its cover, and circling it.
            new MovingShootTest("moving-shoot-walk", 40f, 10f, false),
            new MovingShootTest("moving-shoot-run", 40f, 12f, true),
            new MovingShootTest("moving-shoot-close", 15f, 8f, true),
            new MovingShootTest("moving-shoot-run-80", 80f, 12f, true, minHits: 2),
            new MovingShootTest("moving-shoot-approach", 60f, 40f, true, approach: true),
            new MovingCoverTest("moving-cover", false),
            new MovingCoverTest("moving-peek", true),
            new CircleCoverTest(),

            new HoldTest(),
            new SquadHoldTest(),
            new SquadFarTest(),
            new EventTest()
        };

        public static BanditTest Find(string name)
        {
            return All.Find(t => t.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>"all", one name, or several separated by commas.</summary>
        public static bool TryResolve(string request, out List<BanditTest> tests, out string error)
        {
            tests = new List<BanditTest>();
            error = null;

            if (string.IsNullOrEmpty(request) || request.Equals("all", System.StringComparison.OrdinalIgnoreCase))
            {
                tests.AddRange(All);
                return true;
            }

            foreach (string name in request.Split(','))
            {
                // "shoot*" is every test whose name starts with "shoot".
                if (name.Trim().EndsWith("*"))
                {
                    string prefix = name.Trim().TrimEnd('*');
                    List<BanditTest> matches = All.FindAll(t => t.Name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase));
                    if (matches.Count == 0)
                    {
                        error = $"No test starts with '{prefix}'.";
                        return false;
                    }

                    tests.AddRange(matches);
                    continue;
                }

                BanditTest test = Find(name.Trim());
                if (test == null)
                {
                    error = $"No test called '{name.Trim()}'. Tests: {string.Join(", ", All.ConvertAll(t => t.Name).ToArray())}.";
                    return false;
                }

                tests.Add(test);
            }

            return true;
        }

        internal static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        internal static bool SameSide(BanditBotController a, Player b)
        {
            return a.Self.quests != null && b.quests != null && a.Self.quests.groupID == b.quests.groupID;
        }
    }

    /// <summary>
    /// "/bandit goto" for one bandit: it walks 20m to a point, gets there in reasonable time, and
    /// stays put once it has. The last part matters as much as the first - a bandit that overshoots
    /// or wanders off after arriving looks fine in a log that only records the arrival.
    /// </summary>
    public sealed class GotoTest : BanditTest
    {
        private const float Distance = 20f;
        private const float StuckSeconds = 8f;

        public override string Name => "goto";
        public override string Description => $"one bandit walks {Distance:0}m to a point and stops there";
        public override float TimeoutSeconds => 75f;

        public override IEnumerator Run(BanditTestContext t)
        {
            Vector3 start = t.At(0f, 0f);
            Vector3 destination = t.At(0f, Distance);

            BanditBotController bandit = t.Spawn("rifleman", null, start, destination);
            yield return t.WaitForBrains();

            // Nothing here to take cover from, but a walk test should not depend on that.
            bandit.Brain.SetCoverEnabled(false);
            bandit.Brain.GoTo(destination);
            t.Log($"{BanditTestContext.Name(bandit)} sent {BanditTests.FlatDistance(start, destination):0}m ahead.");

            float arrive = t.Config.ArriveRadius + 1f;
            float began = Time.time;
            bool usedPath = false;
            Vector3 progressFrom = bandit.Self.transform.position;
            float progressTime = Time.time;

            // Fails as soon as the bandit is clearly not going to get there - stopped short, or
            // making no progress - rather than waiting out the whole timeout to say so.
            while (BanditTests.FlatDistance(bandit.Self.transform.position, destination) > arrive)
            {
                Vector3 position = bandit.Self.transform.position;
                float remaining = BanditTests.FlatDistance(position, destination);

                t.Check(!BanditCommand.IsDead(bandit), $"died {remaining:0}m short of the destination");
                t.Check(bandit.Brain.Navigator.HasDestination,
                    $"stopped walking {remaining:0}m short of the destination (brain state {bandit.Brain.State})");
                t.Check(Time.time - began < 45f, $"still {remaining:0}m away after 45s");

                usedPath |= bandit.Brain.Navigator.IsFollowingPath;

                if (Vector3.Distance(position, progressFrom) > 1f)
                {
                    progressFrom = position;
                    progressTime = Time.time;
                }
                else if (Time.time - progressTime > StuckSeconds)
                {
                    throw new BanditTestFailure($"stuck for {StuckSeconds:0}s, {remaining:0}m from the destination "
                        + $"(navigator {(bandit.Brain.Navigator.IsFollowingPath ? "on A*" : "steering")})");
                }

                yield return null;
            }

            float tookSeconds = Time.time - began;

            Vector3 arrivedAt = bandit.Self.transform.position;
            yield return t.Wait(2f);
            float drift = BanditTests.FlatDistance(bandit.Self.transform.position, arrivedAt);
            t.Check(drift < 1.5f, $"kept moving after it arrived - {drift:0.0}m in the next 2s");

            t.Summary = $"arrived in {tookSeconds:0.0}s by {(usedPath ? "A*" : "steering")}, then held still";
        }
    }

    /// <summary>
    /// "/bandit squad goto" for three: each walks to its own slot of the formation the goto order
    /// lays out, and they finish spread out rather than piled onto one point.
    /// </summary>
    public sealed class FormationTest : BanditTest
    {
        private const int Count = 3;
        private const float Distance = 20f;
        private const float ArriveSeconds = 50f;

        public override string Name => "formation";
        public override string Description => $"{Count} bandits walk {Distance:0}m to a formation and end up spread out";
        public override float TimeoutSeconds => 75f;

        public override IEnumerator Run(BanditTestContext t)
        {
            Vector3 centre = t.At(0f, Distance);
            float spacing = t.Config.SquadSpacing;

            List<BanditBotController> bandits = new List<BanditBotController>();
            for (int i = 0; i < Count; i++)
            {
                bandits.Add(t.Spawn("rifleman", null, t.At((i - (Count - 1) * 0.5f) * spacing, 0f), centre));
            }

            yield return t.WaitForBrains();

            // The same slots /bandit goto hands a group, so this is testing what the order does.
            List<Vector3> slots = new List<Vector3>();
            for (int i = 0; i < Count; i++)
            {
                Vector3 slot = BanditPlacement.FormationSlot(centre, t.Right, t.Forward, i, Count, spacing);
                slots.Add(slot);
                bandits[i].Brain.SetCoverEnabled(false);
                bandits[i].Brain.GoTo(slot);
            }

            t.Log($"{Count} bandits sent {Distance:0}m ahead, {spacing:0.#}m apart.");

            float arrive = t.Config.ArriveRadius + 1f;
            float began = Time.time;
            while (true)
            {
                List<string> away = new List<string>();
                for (int i = 0; i < Count; i++)
                {
                    float remaining = BanditTests.FlatDistance(bandits[i].Self.transform.position, slots[i]);
                    if (remaining > arrive)
                    {
                        away.Add($"#{bandits[i].Id} {remaining:0}m from its slot");
                    }
                }

                if (away.Count == 0)
                {
                    break;
                }

                t.Check(Time.time - began < ArriveSeconds,
                    $"not all in formation after {ArriveSeconds:0}s: {string.Join(", ", away.ToArray())}");
                yield return null;
            }

            yield return t.Wait(1f);

            float closest = float.MaxValue;
            for (int i = 0; i < Count; i++)
            {
                for (int j = i + 1; j < Count; j++)
                {
                    closest = Mathf.Min(closest, BanditTests.FlatDistance(
                        bandits[i].Self.transform.position, bandits[j].Self.transform.position));
                }
            }

            t.Check(closest > 1f, $"two bandits ended {closest:0.0}m apart - standing on each other");
            t.Summary = $"all {Count} in their slots after {Time.time - began - 1f:0.0}s, closest pair {closest:0.0}m apart";
        }
    }

    /// <summary>
    /// Cover from a threat that can see you: a bandit with cover on, facing an enemy across open
    /// ground, finds a spot, gets to it, and the enemy can no longer see it once it is there.
    ///
    /// Both sides hold fire, so nobody dies before the answer is in, and the enemy stands still so
    /// the cover it is hiding from does not move. The check at the end is the enemy's own line of
    /// sight test - the same one that decides whether it can shoot - not the cover finder agreeing
    /// with itself.
    /// </summary>
    public sealed class CoverTest : BanditTest
    {
        private const float ThreatDistance = 25f;
        private const float HoldSeconds = 3f;

        public override string Name => "cover";
        public override string Description => $"a bandit seen by an enemy {ThreatDistance:0}m away takes cover and breaks line of sight";
        public override float TimeoutSeconds => 90f;

        public override IEnumerator Run(BanditTestContext t)
        {
            Vector3 hidePosition = t.At(0f, 0f);
            Vector3 threatPosition = t.At(0f, ThreatDistance);

            BanditBotController hider = t.Spawn("rifleman", "red", hidePosition, threatPosition);
            BanditBotController threat = t.Spawn("rifleman", "blue", threatPosition, hidePosition);
            yield return t.WaitForBrains();

            hider.HoldFire = true;
            hider.Brain.SetCoverEnabled(false);
            hider.Brain.SetPeekEnabled(false);

            threat.HoldFire = true;
            threat.Brain.SetCoverEnabled(false);
            threat.Brain.SetStanceOrder(BanditStance.Stand);

            // Otherwise the test proves nothing: a bandit that was never seen is "in cover" already.
            yield return t.WaitUntil(() => threat.HasLineOfSightTo(hider.Self), 5f,
                $"the enemy to see the hider - the arena needs a clear {ThreatDistance:0}m line ahead of it");

            hider.Brain.SetCoverEnabled(true);
            t.Log($"{BanditTestContext.Name(hider)} in the open, seen by {BanditTestContext.Name(threat)} "
                + $"{ThreatDistance:0}m away; cover on.");

            yield return t.WaitUntil(() => hider.Brain.HasCover, 15f,
                $"a cover spot within {t.Config.CoverSearchRadius:0}m - '/bandit {hider.Id} cover on' and "
                + "/bandit debug cover show why the search finds nothing here");

            Vector3 spot = hider.Brain.CurrentCover.Position;
            t.Log($"chose cover {BanditTests.FlatDistance(hidePosition, spot):0.0}m away"
                + (hider.Brain.CurrentCover.RequiresCrouch ? ", crouching." : ", standing."));

            // Who it is hiding from, and whether the spot is anywhere near the line to the enemy
            // it is meant to be hiding from - a spot off to one side cannot be in the way of it.
            Player hidingFrom = hider.CurrentTarget;
            Vector3 threatEye = BanditGeometry.EyeOf(threat.Self);
            t.Log($"hiding from {(hidingFrom != null ? hidingFrom.channel.owner.playerID.characterName : "nobody it can see")}; "
                + $"enemy eye at height {threatEye.y - threat.Self.transform.position.y:0.00}m; "
                + $"spot is {BanditTests.FlatDistance(spot, threatEye):0.0}m from the enemy, "
                + $"{OffLine(spot, hidePosition, threatPosition):0.0}m off the line between them.");

            float began = Time.time;
            yield return t.WaitUntil(() => hider.Brain.HasCover && !hider.Brain.Navigator.HasDestination, 30f,
                "the hider to reach its cover");
            t.Log($"in cover after {Time.time - began:0.0}s.");

            float sightDeadline = Time.time + 5f;
            while (threat.HasLineOfSightTo(hider.Self))
            {
                if (Time.time >= sightDeadline)
                {
                    t.Log(DescribeSight(threat, hider, spot));
                    throw new BanditTestFailure("the enemy never lost sight of the hider once it was in cover");
                }

                yield return null;
            }

            // Held, not sampled once, because a bandit that settles and then stands straight back
            // up is exactly the bug a single check would miss. Peek is off, so it should stay down.
            int frames = 0;
            int seen = 0;
            float holdUntil = Time.time + HoldSeconds;
            while (Time.time < holdUntil)
            {
                frames++;
                seen += threat.HasLineOfSightTo(hider.Self) ? 1 : 0;
                yield return null;
            }

            float seenFraction = frames > 0 ? (float)seen / frames : 0f;
            t.Check(seenFraction < 0.2f, $"visible to the enemy {seenFraction:P0} of the {HoldSeconds:0}s after "
                + "reaching cover - the spot does not hide it");

            t.Summary = $"hid {BanditTests.FlatDistance(hidePosition, spot):0.0}m away in {Time.time - began - HoldSeconds:0.0}s, "
                + $"unseen {1f - seenFraction:P0} of the next {HoldSeconds:0}s";
        }

        /// <summary>Flat distance from a point to the segment between two others.</summary>
        private static float OffLine(Vector3 point, Vector3 from, Vector3 to)
        {
            Vector2 p = new Vector2(point.x, point.z);
            Vector2 a = new Vector2(from.x, from.z);
            Vector2 b = new Vector2(to.x, to.z);
            Vector2 ab = b - a;
            float along = ab.sqrMagnitude > 0.0001f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector2.Distance(p, a + ab * along);
        }

        /// <summary>
        /// The enemy's sight line to the hider, as the cover search would test it: eye to eye, the
        /// same mask, and what - if anything - is in the way.
        /// </summary>
        private static string DescribeSight(BanditBotController threat, BanditBotController hider, Vector3 spot)
        {
            Vector3 from = BanditGeometry.EyeOf(threat.Self);
            Vector3 to = BanditGeometry.EyeOf(hider.Self);
            Vector3 delta = to - from;
            string hit = Physics.Raycast(new Ray(from, delta.normalized), out RaycastHit info, delta.magnitude - 0.025f,
                    RayMasks.BLOCK_SENTRY, QueryTriggerInteraction.Ignore)
                ? $"blocked by '{info.collider.name}' {info.distance:0.0}m out"
                : "clear all the way";

            return $"hider stands {BanditTests.FlatDistance(hider.Self.transform.position, spot):0.0}m from its spot, "
                + $"stance {hider.Self.stance.stance}, eye {to.y - hider.Self.transform.position.y:0.00}m up; "
                + $"enemy eye to hider eye {delta.magnitude:0.0}m: {hit}; "
                + $"hider targeting {(hider.CurrentTarget != null ? hider.CurrentTarget.channel.owner.playerID.characterName : "nobody")}.";
        }
    }

    /// <summary>
    /// Teams in a real firefight: two red riflemen and a blue one, all weapons free. Red and blue
    /// must shoot each other, and red must never target or hit red.
    ///
    /// Hits are read off DamageTool's own damage event, which is where every bullet that lands on a
    /// player ends up - the same hook the plugin uses to tell a bandit it has been shot.
    /// </summary>
    public sealed class TeamsTest : BanditTest
    {
        private const float Separation = 30f;
        private const float FirstHitSeconds = 60f;
        private const float WatchSeconds = 10f;

        public override string Name => "teams";
        public override string Description => "red and blue shoot each other, red never targets or hits red";
        public override float TimeoutSeconds => 100f;

        public override IEnumerator Run(BanditTestContext t)
        {
            Vector3 blueSpot = t.At(0f, Separation);
            BanditBotController redLeft = t.Spawn("rifleman", "red", t.At(-2f, 0f), blueSpot);
            BanditBotController redRight = t.Spawn("rifleman", "red", t.At(2f, 0f), blueSpot);
            BanditBotController blue = t.Spawn("rifleman", "blue", blueSpot, t.At(0f, 0f));
            yield return t.WaitForBrains();

            int crossHits = 0;
            bool redTargetedBlue = false;
            bool blueTargetedRed = false;
            string friendlyFire = null;

            DamageTool.DamagePlayerHandler onDamage = (ref DamagePlayerParameters parameters, ref bool shouldAllow) =>
            {
                if (!shouldAllow || parameters.player == null)
                {
                    return;
                }

                BanditBotController shooter = t.FindBySteamId(parameters.killer);
                BanditBotController victim = parameters.player.GetComponent<BanditBotController>();
                if (shooter == null || victim == null || !t.Spawned.Contains(victim) || shooter == victim)
                {
                    return;
                }

                if (BanditTests.SameSide(shooter, victim.Self))
                {
                    friendlyFire = friendlyFire
                        ?? $"{BanditTestContext.Name(shooter)} hit its teammate {BanditTestContext.Name(victim)}";
                }
                else
                {
                    crossHits++;
                }
            };

            DamageTool.damagePlayerRequested += onDamage;
            t.OnCleanup(() => DamageTool.damagePlayerRequested -= onDamage);

            t.Always(() => friendlyFire);
            t.Always(() =>
            {
                foreach (BanditBotController bandit in t.Spawned)
                {
                    if (bandit == null || bandit.Self == null || BanditCommand.IsDead(bandit))
                    {
                        continue;
                    }

                    Player target = bandit.CurrentTarget;
                    if (target == null)
                    {
                        continue;
                    }

                    if (BanditTests.SameSide(bandit, target))
                    {
                        return $"{BanditTestContext.Name(bandit)} targeted its teammate "
                            + target.channel.owner.playerID.characterName;
                    }

                    if (bandit == blue)
                    {
                        blueTargetedRed = true;
                    }
                    else
                    {
                        redTargetedBlue = true;
                    }
                }

                return null;
            });

            foreach (BanditBotController bandit in t.Spawned)
            {
                bandit.Brain.SetCoverEnabled(false);
                bandit.Brain.SetStanceOrder(BanditStance.Stand);
                bandit.HoldFire = false;
            }

            t.Log($"red pair and blue {Separation:0}m apart, weapons free.");

            float began = Time.time;
            while (crossHits == 0)
            {
                if (Time.time - began > FirstHitSeconds)
                {
                    List<string> blocks = new List<string>();
                    foreach (BanditBotController bandit in t.Spawned)
                    {
                        blocks.Add($"#{bandit.Id}: {bandit.DescribeFireBlock()}");
                    }

                    throw new BanditTestFailure($"no hits between red and blue in {FirstHitSeconds:0}s "
                        + $"(red targeted blue: {redTargetedBlue}, blue targeted red: {blueTargetedRed}). "
                        + string.Join("; ", blocks.ToArray()));
                }

                yield return null;
            }

            t.Log($"first hit after {Time.time - began:0.0}s; watching for friendly fire for {WatchSeconds:0}s.");

            // Keep watching for friendly fire while the fight runs, but there is nothing more to
            // learn once a whole side is down.
            float watchUntil = Time.time + WatchSeconds;
            while (Time.time < watchUntil
                   && !(BanditCommand.IsDead(blue)
                        || (BanditCommand.IsDead(redLeft) && BanditCommand.IsDead(redRight))))
            {
                yield return null;
            }

            t.Check(redTargetedBlue, "red never targeted blue");
            t.Check(blueTargetedRed, "blue never targeted red");

            t.Summary = $"{crossHits} hit(s) between the teams, none on a teammate; both sides engaged";
        }
    }
}
