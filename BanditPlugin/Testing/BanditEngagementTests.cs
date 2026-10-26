using System.Collections;
using System.Collections.Generic;
using BanditPlugin.Commands;
using BanditPlugin.FakePlayer;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Testing
{
    /// <summary>
    /// Engagement distance, from one bandit up to a whole event: who walks at the enemy, how far,
    /// and why.
    ///
    /// Each test puts a target on the field that cannot die and never shoots back, so the fight
    /// runs for as long as the test wants and nothing ends early because somebody was killed. Then
    /// it watches. Every bandit's state change is noted with how far it is from the target at that
    /// moment, and its closest approach is kept along with the state that brought it there - so a
    /// failure says not only "the rifleman ended up 12m from the enemy" but "it was hunting an
    /// angle when it did".
    ///
    /// Only a kit with AdvanceOnTarget is meant to close in. Everybody else should fight from
    /// roughly where they are - moving to cover, or a short push for an angle, but not a charge.
    /// </summary>
    internal static class BanditEngagement
    {
        /// <summary>
        /// A blue rifleman standing in the open that never fires, never moves and cannot be hurt.
        /// Something to be seen, not something to fight.
        /// </summary>
        public static BanditBotController SpawnTarget(BanditTestContext t, Vector3 position, Vector3 faceToward)
        {
            BanditBotController target = t.Spawn("rifleman", "blue", position, faceToward);

            DamageTool.DamagePlayerHandler immortal = (ref DamagePlayerParameters parameters, ref bool shouldAllow) =>
            {
                if (target.Self != null && parameters.player == target.Self)
                {
                    shouldAllow = false;
                }
            };

            DamageTool.damagePlayerRequested += immortal;
            t.OnCleanup(() => DamageTool.damagePlayerRequested -= immortal);
            return target;
        }

        /// <summary>Puts the target into its do-nothing state. Call once its brain exists.</summary>
        public static void Pacify(BanditBotController target)
        {
            target.HoldFire = true;
            target.Brain.SetCoverEnabled(false);
            target.Brain.SetPeekEnabled(false);
            target.Brain.SetPatrol(false);
            target.Brain.SetStanceOrder(BanditStance.Stand);
        }

        /// <summary>
        /// Spawns a squad of a configured type for a test, on a team, facing the target, and hands
        /// its members to the test so they are cleaned up with everything else.
        /// </summary>
        public static List<BanditBotController> SpawnSquad(BanditTestContext t, string type, string team,
            Vector3 centre, Vector3 faceToward)
        {
            BanditSquadType squadType = t.Config.FindSquad(type);
            t.Check(squadType != null, $"no squad type called '{type}' in the configuration");

            Vector3 forward = faceToward - centre;
            forward.y = 0f;
            forward = forward.normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            float facing = Quaternion.LookRotation(forward).eulerAngles.y;

            BanditSquadSpawner.Result result = BanditSquadSpawner.Spawn(t.Config,
                BanditSquadProfile.FromType(t.Config, squadType), BanditTeams.Find(t.Config, team),
                centre, right, forward, facing);

            t.Check(result.Squad != null, $"the '{type}' squad did not spawn - see the server log");

            List<BanditBotController> members = new List<BanditBotController>(result.Squad.Members);
            foreach (BanditBotController member in members)
            {
                member.HostileToUngrouped = false;
                t.Spawned.Add(member);
            }

            return members;
        }
    }

    /// <summary>
    /// Watches a set of bandits against one target: notes every state change, and keeps each
    /// bandit's closest approach and what it was doing at the time.
    /// </summary>
    internal sealed class ApproachTracker
    {
        private sealed class Track
        {
            public BanditBotController Bandit;
            public float Start;
            public float Closest = float.MaxValue;
            public string AtClosest = "?";
            public string LastState;
            public Vector3? LastCover;
            public int CoverMoves;
        }

        private readonly List<Track> _tracks = new List<Track>();

        /// <summary>
        /// Whether any of them ever saw the target. A run in which nobody did proved nothing about
        /// how they fight, however far they stayed away.
        /// </summary>
        public bool AnyContact { get; private set; }
        private readonly BanditTestContext _t;
        private readonly BanditBotController _target;

        public ApproachTracker(BanditTestContext t, BanditBotController target, IEnumerable<BanditBotController> bandits)
        {
            _t = t;
            _target = target;

            foreach (BanditBotController bandit in bandits)
            {
                _tracks.Add(new Track { Bandit = bandit, Start = Distance(bandit) });
            }

            // Run every frame by the test runner, whatever the test is waiting on. Never fails the
            // test itself - it only keeps the record.
            t.Always(() =>
            {
                Update();
                return null;
            });
        }

        private float Distance(BanditBotController bandit)
        {
            return BanditTests.FlatDistance(bandit.Self.transform.position, _target.Self.transform.position);
        }

        private static string Describe(BanditBotController bandit)
        {
            if (BanditCommand.IsDead(bandit))
            {
                return "dead";
            }

            if (bandit.Brain == null)
            {
                return "no brain";
            }

            BanditBrain.BanditState state = bandit.Brain.State;
            string reason = state == BanditBrain.BanditState.Engage
                ? (bandit.Brain.HuntingAngle ? " (hunting an angle)" : " (closing in)")
                : string.Empty;
            string seat = bandit.Driver != null && bandit.Driver.IsSeated ? ", seated" : string.Empty;
            string sees = bandit.CurrentTarget != null ? ", sees the target" : string.Empty;

            return $"{state}{reason}{seat}{sees}";
        }

        private void Update()
        {
            foreach (Track track in _tracks)
            {
                if (track.Bandit == null || track.Bandit.Self == null)
                {
                    continue;
                }

                float distance = Distance(track.Bandit);
                string state = Describe(track.Bandit);
                AnyContact |= track.Bandit.CurrentTarget != null;

                if (distance < track.Closest)
                {
                    track.Closest = distance;
                    track.AtClosest = state;
                }

                if (state != track.LastState)
                {
                    track.LastState = state;
                    _t.Note($"{BanditTestContext.Name(track.Bandit)} {distance:0}m from the target: {state}");
                }

                // Every cover pick, including the ones made on the same tick the last was dropped -
                // those never show up as a state change, and a run of them is how a bandit walks
                // across a field without ever leaving TakeCover.
                if (track.Bandit.Brain != null && track.Bandit.Brain.HasCover)
                {
                    Vector3 spot = track.Bandit.Brain.CurrentCover.Position;
                    if (!track.LastCover.HasValue || (track.LastCover.Value - spot).sqrMagnitude > 0.25f)
                    {
                        track.LastCover = spot;
                        track.CoverMoves++;
                        _t.Note($"{BanditTestContext.Name(track.Bandit)} cover #{track.CoverMoves}: "
                            + $"{BanditTests.FlatDistance(spot, _target.Self.transform.position):0}m from the target, "
                            + $"{BanditTests.FlatDistance(spot, track.Bandit.Self.transform.position):0}m from the bandit"
                            + (track.Bandit.Brain.CurrentCover.RequiresCrouch ? ", crouch" : ", standing"));
                    }
                }
            }
        }

        /// <summary>
        /// Logs each bandit's start, closest approach and the state at it, and returns the ones
        /// that came closer than <paramref name="limit"/> allows - an empty list is a pass.
        /// Kits that close in by design are reported but never counted.
        /// </summary>
        public List<string> Report(System.Func<BanditBotController, float, float> limit)
        {
            List<string> offenders = new List<string>();

            foreach (Track track in _tracks)
            {
                if (track.Bandit == null)
                {
                    continue;
                }

                bool advances = track.Bandit.Profile != null && track.Bandit.Profile.AdvanceOnTarget;
                float allowed = limit(track.Bandit, track.Start);
                string line = $"{BanditTestContext.Name(track.Bandit)}: started {track.Start:0}m out, "
                    + $"closest {track.Closest:0}m ({track.AtClosest}), {track.CoverMoves} cover pick(s)"
                    + (advances ? " - closes in by design" : $", allowed {allowed:0}m");

                _t.Note(line);

                if (!advances && track.Closest < allowed)
                {
                    offenders.Add($"{BanditTestContext.Name(track.Bandit)} came to {track.Closest:0}m "
                        + $"from {track.Start:0}m ({track.AtClosest})");
                }
            }

            return offenders;
        }
    }

    /// <summary>
    /// One rifleman, weapons free, against a target 60m off in the open - well inside its firing
    /// range. It has a shot, so it has no reason to walk anywhere but into cover.
    /// </summary>
    public sealed class HoldTest : BanditTest
    {
        private const float Distance = 60f;
        private const float WatchSeconds = 20f;

        public override string Name => "hold";
        public override string Description => $"a lone rifleman with a shot {Distance:0}m away fights from where it is";
        public override float TimeoutSeconds => WatchSeconds + 30f;

        public override IEnumerator Run(BanditTestContext t)
        {
            BanditBotController target = BanditEngagement.SpawnTarget(t, t.At(0f, Distance), t.At(0f, 0f));
            BanditBotController rifleman = t.Spawn("rifleman", "red", t.At(0f, 0f), t.At(0f, Distance));
            yield return t.WaitForBrains();

            BanditEngagement.Pacify(target);
            rifleman.HoldFire = false;

            ApproachTracker tracker = new ApproachTracker(t, target, new[] { rifleman });
            t.Log($"{BanditTestContext.Name(rifleman)} weapons free, target {Distance:0}m away; watching {WatchSeconds:0}s.");

            yield return t.Wait(WatchSeconds);

            List<string> offenders = tracker.Report((bandit, start) => start - 15f);
            t.Check(tracker.AnyContact, "the rifleman never saw the target");
            t.Check(offenders.Count == 0, "closed in: " + string.Join("; ", offenders.ToArray()));
            t.Summary = "held its distance";
        }
    }

    /// <summary>
    /// A rifle squad 80m from the target - every member in range. Shared contact, cover claims and
    /// the dry-spell push all come into play, but with a shot available none of them should
    /// carry anyone far.
    /// </summary>
    public sealed class SquadHoldTest : BanditTest
    {
        private const float Distance = 80f;
        private const float WatchSeconds = 25f;

        public override string Name => "squad-hold";
        public override string Description => $"a rifle squad {Distance:0}m from the enemy, all in range, holds its ground";
        public override float TimeoutSeconds => WatchSeconds + 30f;

        public override IEnumerator Run(BanditTestContext t)
        {
            BanditBotController target = BanditEngagement.SpawnTarget(t, t.At(0f, Distance), t.At(0f, 0f));
            List<BanditBotController> squad = BanditEngagement.SpawnSquad(t, "rifle", "red", t.At(0f, 0f), t.At(0f, Distance));
            yield return t.WaitForBrains();

            BanditEngagement.Pacify(target);

            ApproachTracker tracker = new ApproachTracker(t, target, squad);
            t.Log($"rifle squad of {squad.Count}, {Distance:0}m from the target; watching {WatchSeconds:0}s.");

            yield return t.Wait(WatchSeconds);

            List<string> offenders = tracker.Report((bandit, start) => start - 20f);
            t.Check(tracker.AnyContact, "nobody in the squad ever saw the target");
            t.Check(offenders.Count == 0, "closed in: " + string.Join("; ", offenders.ToArray()));
            t.Summary = $"all {squad.Count} held their ground";
        }
    }

    /// <summary>
    /// A basic squad 130m out: the machinegunner and the marksman can see that far, the riflemen
    /// cannot, and the squad shares what it sees. The riflemen are in contact with no shot of their
    /// own - the situation an event 120m away puts them in.
    /// </summary>
    public sealed class SquadFarTest : BanditTest
    {
        private const float Distance = 130f;
        private const float WatchSeconds = 30f;

        public override string Name => "squad-far";
        public override string Description => $"a basic squad {Distance:0}m out - riflemen in contact but out of range - does not charge";
        public override float TimeoutSeconds => WatchSeconds + 30f;

        public override IEnumerator Run(BanditTestContext t)
        {
            BanditBotController target = BanditEngagement.SpawnTarget(t, t.At(0f, Distance), t.At(0f, 0f));
            List<BanditBotController> squad = BanditEngagement.SpawnSquad(t, "basic", "red", t.At(0f, 0f), t.At(0f, Distance));
            yield return t.WaitForBrains();

            BanditEngagement.Pacify(target);

            ApproachTracker tracker = new ApproachTracker(t, target, squad);
            t.Log($"basic squad of {squad.Count}, {Distance:0}m from the target; watching {WatchSeconds:0}s.");

            yield return t.Wait(WatchSeconds);

            // Closing to its own firing range is reasonable for a bandit that has none. Going any
            // further than that is a charge.
            List<string> offenders = tracker.Report((bandit, start) =>
                Mathf.Min(start - 20f, bandit.FireRange * 0.75f));
            t.Check(tracker.AnyContact, "nobody in the squad ever saw the target");
            t.Check(offenders.Count == 0, "charged: " + string.Join("; ", offenders.ToArray()));
            t.Summary = $"none of the {squad.Count} charged";
        }
    }

    /// <summary>
    /// A real event, exactly as /bandit event spawn puts one down, 120m from the target - the case
    /// that was seen in game with riflemen ending up at arm's length. Fixed seed, so every run
    /// draws the same squads and vehicles.
    /// </summary>
    public sealed class EventTest : BanditTest
    {
        private const float Distance = 100f;
        private const float Budget = 200f;
        private const int Seed = 1234;
        private const float WatchSeconds = 40f;
        private const float MinimumDistance = 40f;

        public override string Name => "event";
        public override string Description => $"a {Budget:0}-point event {Distance:0}m out: only breachers come within {MinimumDistance:0}m";
        public override float TimeoutSeconds => WatchSeconds + 40f;

        public override IEnumerator Run(BanditTestContext t)
        {
            Vector3 origin = t.At(0f, 0f);
            BanditBotController target = BanditEngagement.SpawnTarget(t, origin, t.At(0f, Distance));
            yield return t.WaitForBrains();
            BanditEngagement.Pacify(target);

            BanditEventPlan plan = BanditEventDraw.Draw(t.Config, Budget, Seed, Mathf.Max(1, t.Config.EventMaxBandits));
            BanditEvent banditEvent = BanditEvent.Create(Budget);
            t.OnCleanup(() => banditEvent.Clear(out _, out _));

            Vector3 forward = t.Forward;
            BanditPlacement.Result placed = new BanditPlacement.Result
            {
                Origin = origin,
                Centre = t.At(0f, Distance),
                Forward = forward,
                Right = new Vector3(forward.z, 0f, -forward.x),
                Facing = Mathf.Atan2(-forward.x, -forward.z) * Mathf.Rad2Deg,
                UsedMarker = false
            };

            BanditEventSpawnCommand.Spawn(t.Config, plan, banditEvent, BanditTeams.Find(t.Config, "red"), placed);

            List<BanditBotController> bandits = new List<BanditBotController>();
            foreach (BanditSquad squad in banditEvent.Squads)
            {
                foreach (BanditBotController member in squad.Members)
                {
                    member.HostileToUngrouped = false;
                    bandits.Add(member);
                }
            }

            t.Check(bandits.Count > 0, "the event spawned nobody - see the server log");
            yield return t.WaitUntil(() => bandits.TrueForAll(b => b == null || b.Brain != null), 5f,
                "the event's bandits' brains to start");

            ApproachTracker tracker = new ApproachTracker(t, target, bandits);
            t.Log($"event {banditEvent.Id}: {BanditEventSpawnCommand.DescribeContents(banditEvent)}; "
                + $"{bandits.Count} bandit(s), {Distance:0}m from the target, seed {Seed}; watching {WatchSeconds:0}s.");

            yield return t.Wait(WatchSeconds);

            List<string> offenders = tracker.Report((bandit, start) => MinimumDistance);
            t.Check(tracker.AnyContact, "no bandit ever saw the target, so this run says nothing - "
                + "the arena needs a clear line out to " + Distance.ToString("0") + "m");
            t.Check(offenders.Count == 0, $"came within {MinimumDistance:0}m: " + string.Join("; ", offenders.ToArray()));
            t.Summary = $"none of the {bandits.Count} non-breachers came within {MinimumDistance:0}m";
        }
    }
}
