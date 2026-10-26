using System.Collections;
using System.Collections.Generic;
using BanditPlugin.Commands;
using BanditPlugin.FakePlayer;
using BanditPlugin.Navigation;
using UnityEngine;

namespace BanditPlugin.Testing
{
    /// <summary>
    /// Walks a test target through a list of points: back and forth on its own for as long as the
    /// test runs (<see cref="Start"/>), or one point at a time with stops in between, driven by the
    /// test (<see cref="WalkTo"/> and <see cref="Hold"/>).
    ///
    /// The target is an ordinary bandit on a /bandit goto, so it moves the way a player does -
    /// accelerating, turning corners, running or walking - rather than being teleported along.
    /// It never shoots and cannot die, the same as the target in every other foundation test.
    /// </summary>
    internal sealed class MovingTarget
    {
        private const float StuckSeconds = 6f;

        private readonly BanditBotController _target;
        private readonly Vector3[] _points;
        private readonly bool _run;
        private int _index;
        private int _step = 1;
        private Vector3 _last;
        private Vector3 _progressFrom;
        private float _progressTime;
        private float _movingSeconds;
        private bool _manual;
        private bool _holding;

        /// <summary>Legs walked so far - one per point reached.</summary>
        public int Legs;

        /// <summary>Metres walked while moving, for the average speed in the summary.</summary>
        public float Walked;

        public bool Moving { get; private set; }

        public float AverageSpeed => _movingSeconds > 0f ? Walked / _movingSeconds : 0f;

        /// <param name="points">The route; the target should be spawned on the first point.</param>
        /// <param name="run">Sprint between points rather than walk.</param>
        public MovingTarget(BanditTestContext t, BanditBotController target, Vector3[] points, bool run)
        {
            _target = target;
            _points = points;
            _run = run;
            t.Always(() => Tick());
        }

        public void Start()
        {
            _target.Brain.TravelSprintEnabled = _run;
            _last = _progressFrom = _target.Self.transform.position;
            _progressTime = Time.time;
            Moving = true;
            NextLeg();
        }

        /// <summary>
        /// Walks to one point and returns once it is there. A test that drives the target this way
        /// decides the route itself; the automatic back and forth stays off.
        /// </summary>
        public IEnumerator WalkTo(Vector3 point)
        {
            if (!Moving)
            {
                _target.Brain.TravelSprintEnabled = _run;
                _last = _target.Self.transform.position;
                Moving = true;
            }

            _manual = true;
            _holding = false;
            _progressFrom = _target.Self.transform.position;
            _progressTime = Time.time;
            _target.Brain.GoTo(point);

            while (_target.Brain.Navigator.HasDestination)
            {
                yield return null;
            }

            Legs++;
        }

        /// <summary>Stands still for a while - which a stuck check would otherwise call stuck.</summary>
        public IEnumerator Hold(float seconds)
        {
            _holding = true;
            float until = Time.time + seconds;
            while (Time.time < until)
            {
                yield return null;
            }

            _holding = false;
            _last = _progressFrom = _target.Self.transform.position;
            _progressTime = Time.time;
        }

        private void NextLeg()
        {
            if (_index + _step < 0 || _index + _step >= _points.Length)
            {
                _step = -_step;
            }

            _index += _step;
            _target.Brain.GoTo(_points[_index]);
        }

        private string Tick()
        {
            if (!Moving || _holding || _target.Self == null || _target.Brain == null || BanditCommand.IsDead(_target))
            {
                return null;
            }

            Vector3 position = _target.Self.transform.position;
            Walked += BanditTests.FlatDistance(position, _last);
            _movingSeconds += Time.deltaTime;
            _last = position;

            // Arriving clears the goto, which is the cue for the next leg.
            if (!_manual && !_target.Brain.Navigator.HasDestination)
            {
                Legs++;
                NextLeg();
            }

            // A target that has stopped is not a moving target, and a test that went on regardless
            // would report its result against the wrong thing.
            if (Vector3.Distance(position, _progressFrom) > 1f)
            {
                _progressFrom = position;
                _progressTime = Time.time;
            }
            else if (Time.time - _progressTime > StuckSeconds)
            {
                Vector3 goal = _manual ? _target.Brain.Navigator.Destination : _points[_index];
                return $"the moving target got stuck for {StuckSeconds:0}s, "
                    + $"{BanditTests.FlatDistance(position, goal):0.0}m short of its next point (state {_target.Brain.State})";
            }

            return null;
        }

        /// <summary>Points from <paramref name="from"/> to <paramref name="to"/> in a straight line.</summary>
        public static Vector3[] Line(Vector3 from, Vector3 to)
        {
            return new[] { BanditPlacement.SnapToGround(from), BanditPlacement.SnapToGround(to) };
        }

        /// <summary>
        /// Points on an arc of <paramref name="radius"/> round <paramref name="centre"/>, starting
        /// <paramref name="radius"/> metres along <paramref name="forward"/> and swinging
        /// <paramref name="degrees"/> round - positive to the left - one point every 10 degrees.
        /// </summary>
        public static Vector3[] Arc(Vector3 centre, Vector3 forward, float radius, float degrees)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(degrees) / 10f));
            Vector3[] points = new Vector3[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                Vector3 direction = Quaternion.AngleAxis(-degrees * i / steps, Vector3.up) * forward;
                points[i] = BanditPlacement.SnapToGround(centre + direction * radius);
            }

            return points;
        }

        public string Describe()
        {
            return $"target {(_run ? "ran" : "walked")} {Walked:0}m in {Legs} leg(s) at {AverageSpeed:0.0}m/s";
        }
    }

    /// <summary>
    /// The rifleman in the open against a target that will not hold still: crossing in front of
    /// it, walking or running, near or far, or coming straight at it. The aim has to keep up with
    /// a moving body - turn rate, aim point, the line-of-sight check, all of it done against
    /// where the target is now rather than where it was.
    /// </summary>
    public sealed class MovingShootTest : BanditTest
    {
        private const float FirstHitSeconds = 15f;
        private const float KeepSeconds = 15f;
        private readonly string _name;
        private readonly float _distance;
        private readonly float _halfWidth;
        private readonly bool _run;
        private readonly bool _approach;
        private readonly int _minHits;

        /// <param name="distance">Metres to the target's path, or to where an approach starts.</param>
        /// <param name="halfWidth">How far either side of centre the target crosses; for an approach, how far it closes.</param>
        /// <param name="approach">Walk straight at the bandit and back, rather than across.</param>
        public MovingShootTest(string name, float distance, float halfWidth, bool run, bool approach = false, int minHits = 3)
        {
            _name = name;
            _distance = distance;
            _halfWidth = halfWidth;
            _run = run;
            _approach = approach;
            _minHits = minHits;
        }

        public override string Name => _name;
        public override string Description => $"a lone rifleman in the open hits a target {(_run ? "running" : "walking")} "
            + (_approach
                ? $"at it from {_distance:0}m to {_distance - _halfWidth:0}m and back"
                : $"across in front of it {_distance:0}m away, {_halfWidth * 2f:0}m side to side");
        public override float TimeoutSeconds => FirstHitSeconds + KeepSeconds + 30f;

        public override IEnumerator Run(BanditTestContext t)
        {
            Vector3[] route = _approach
                ? MovingTarget.Line(t.At(0f, _distance), t.At(0f, _distance - _halfWidth))
                : MovingTarget.Line(t.At(-_halfWidth, _distance), t.At(_halfWidth, _distance));

            BanditBotController target = BanditEngagement.SpawnTarget(t, route[0], t.At(0f, 0f));
            BanditBotController shooter = t.Spawn("rifleman", "red", t.At(0f, 0f), t.At(0f, _distance));
            Dictionary<BanditBotController, int> hits = BanditFoundation.CountHits(t, target);
            MovingTarget mover = new MovingTarget(t, target, route, _run);
            yield return t.WaitForBrains();

            BanditEngagement.Pacify(target);
            shooter.Brain.SetCoverEnabled(false);
            shooter.Brain.SetPeekEnabled(false);
            mover.Start();
            shooter.HoldFire = false;

            t.Log($"{BanditTestContext.Name(shooter)} weapons free, target {(_run ? "running" : "walking")} "
                + (_approach ? "at it and back." : "across in front of it."));

            float began = Time.time;
            while (BanditFoundation.Total(hits) == 0)
            {
                if (Time.time - began > FirstHitSeconds)
                {
                    throw new BanditTestFailure($"no hit in {FirstHitSeconds:0}s - {shooter.RoundsFired} round(s) fired, "
                        + $"{shooter.HitReportsSent} hit report(s) sent, {shooter.HitReportsOnPlayers} on a player, "
                        + $"last other hit {shooter.LastNonPlayerHit}; target "
                        + $"{BanditTests.FlatDistance(shooter.Self.transform.position, target.Self.transform.position):0}m out; "
                        + $"{mover.Describe()}; fire: {shooter.DescribeFireBlock()}");
                }

                yield return null;
            }

            float firstHit = Time.time - began;
            t.Log($"first hit after {firstHit:0.0}s.");

            int roundsBefore = shooter.RoundsFired;
            int hitsBefore = BanditFoundation.Total(hits);
            yield return t.Wait(KeepSeconds);

            int landed = BanditFoundation.Total(hits) - hitsBefore;
            int rounds = shooter.RoundsFired - roundsBefore;
            t.Note($"{mover.Describe()}; {landed} hit(s) from {rounds} round(s) after the first.");
            t.Check(mover.Legs >= 1, $"the target never finished a leg - {mover.Describe()}");
            t.Check(landed >= _minHits, $"only {landed} hit(s) from {rounds} round(s) in {KeepSeconds:0}s after the first - "
                + $"fire: {shooter.DescribeFireBlock()}");

            t.Summary = $"first hit at {firstHit:0.0}s, then {landed} hits from {rounds} rounds "
                + $"({CoverWatch.Fraction(landed, rounds):P0}); {mover.Describe()}";
        }
    }

    /// <summary>
    /// The rifleman behind a sandbag wall while the target paces side to side in front of it.
    /// The wall hides the bandit from every point of the walk, so the enemy moving is not a reason
    /// to move: it should keep the one spot, stay hidden, and (with peek on) keep shooting from it.
    /// </summary>
    public sealed class MovingCoverTest : BanditTest
    {
        private const float ThreatDistance = 30f;
        private const float WatchSeconds = 30f;

        /// <summary>
        /// How long the bandit may sit in its cover being seen. The hidden check runs once a
        /// second and a re-search and a walk follow it, so a few seconds is a fair reaction.
        /// </summary>
        private const float ReactSeconds = 5f;

        private readonly string _name;
        private readonly bool _peek;

        public MovingCoverTest(string name, bool peek)
        {
            _name = name;
            _peek = peek;
        }

        public override string Name => _name;
        public override string Description => "a rifleman behind a 3-wide, 2-high sandbag wall keeps its cover "
            + $"against a target pacing in front of it{(_peek ? ", peeking and hitting" : string.Empty)}";
        public override float TimeoutSeconds => WatchSeconds + 70f;

        public override IEnumerator Run(BanditTestContext t)
        {
            Vector3 home = t.At(0f, 0f);
            Vector3 wall = t.At(4f, 3f);

            // 12m side to side, centred on the wall, so the wall is between them the whole way.
            Vector3[] route = MovingTarget.Line(t.At(-2f, ThreatDistance), t.At(10f, ThreatDistance));
            Vector3 threatSpot = t.At(4f, ThreatDistance);

            BanditFoundation.BuildSandbagWall(t, wall, threatSpot, 3, 2);

            BanditBotController target = BanditEngagement.SpawnTarget(t, route[0], home);
            BanditBotController bandit = t.Spawn("rifleman", "red", home, threatSpot);
            Dictionary<BanditBotController, int> hits = BanditFoundation.CountHits(t, target);
            MovingTarget mover = new MovingTarget(t, target, route, false);
            yield return t.WaitForBrains();

            BanditEngagement.Pacify(target);
            bandit.HoldFire = true;
            bandit.Brain.SetCoverEnabled(true);
            bandit.Brain.SetPeekEnabled(_peek);

            CoverWatch watch = new CoverWatch(t, target, new[] { bandit });
            t.Log($"{BanditTestContext.Name(bandit)} with a wall beside it, target {ThreatDistance:0}m; "
                + $"cover on, peek {(_peek ? "on" : "off")}.");

            yield return BanditFoundation.WaitInCover(t, bandit, 25f);
            CoverWatch.Record record = watch.Records[0];

            bandit.HoldFire = !_peek;
            int hitsBefore = BanditFoundation.Total(hits);
            mover.Start();
            watch.Counting = true;
            t.Log("in cover; the target starts pacing.");

            yield return t.Wait(WatchSeconds);

            t.Note(watch.Describe(record));
            t.Note(mover.Describe());

            int landed = BanditFoundation.Total(hits) - hitsBefore;
            float peeking = CoverWatch.Fraction(record.PeekFrames, record.Frames);
            float seenHidden = CoverWatch.Fraction(record.SeenWhileHiddenFrames, record.Frames - record.PeekFrames);

            t.Check(mover.Legs >= 1, $"the target never finished a leg - {mover.Describe()}");
            t.Check(record.LongestSeenInCover < ReactSeconds,
                $"sat in cover for {record.LongestSeenInCover:0.0}s while the target could see it - "
                + $"last dropped cover: {bandit.Brain.LastCoverDrop}");
            t.Check(record.Picks <= 2, $"picked cover {record.Picks} times against a target the wall hides it from all along");
            t.Check(seenHidden < 0.2f, $"visible {seenHidden:P0} of the time it was meant to be hidden");
            t.Check(record.FurthestFromSpot < 2.5f, $"wandered {record.FurthestFromSpot:0.0}m from its cover spot");

            if (_peek)
            {
                t.Check(peeking > 0.1f, $"peeked only {peeking:P0} of the time - it is not coming out to shoot");
                t.Check(landed >= 1, $"no hits in {WatchSeconds:0}s of peeking - fire: {bandit.DescribeFireBlock()}");
            }

            t.Summary = $"{record.Picks} pick(s), seen {seenHidden:P0} while hidden"
                + (_peek ? $", peeking {peeking:P0}, {landed} hit(s)" : string.Empty)
                + $"; {mover.Describe()}";
        }
    }

    /// <summary>
    /// The target walks round the bandit's wall - two full circles - and stops every quarter
    /// turn to look. Each quarter puts the threat on a new side, so the spot that hid the bandit
    /// at the last stop is in plain view at this one, and the only way to stay hidden is to keep
    /// moving round the wall ahead of the target.
    ///
    /// What is checked is the result, at the end of every stop: can the target see the bandit's
    /// head, its chest or its hips. Not the head alone, which is what a bandit's own eye-line
    /// test looks at - cover that hides a head over an exposed body is not working cover.
    /// </summary>
    public sealed class CircleCoverTest : BanditTest
    {
        private const float Radius = 25f;
        private const int Laps = 2;
        private const float StopSeconds = 2f;

        /// <summary>Points on the circle, one every 10 degrees; every ninth is a stop.</summary>
        private const int PointsPerLap = 36;
        private const int PointsPerStop = 9;

        public override string Name => "moving-cover-circle";
        public override string Description => $"a rifleman behind a 3-wide, 2-high sandbag wall stays hidden from a target "
            + $"walking {Laps} circles round it, {Radius:0}m out, and stopping to look every 90 degrees";
        public override float TimeoutSeconds => 200f;

        /// <summary>Which of head, chest and hips <paramref name="viewer"/> can see on <paramref name="seen"/>.</summary>
        private static List<string> VisibleParts(BanditBotController viewer, BanditBotController seen)
        {
            Vector3 feet = seen.Self.transform.position;
            Vector3 eye = seen.Self.look.aim.position;
            List<string> parts = new List<string>();

            if (viewer.CanSeePoint(eye))
            {
                parts.Add("head");
            }

            if (viewer.CanSeePoint(Vector3.Lerp(feet, eye, 0.7f)))
            {
                parts.Add("chest");
            }

            if (viewer.CanSeePoint(Vector3.Lerp(feet, eye, 0.45f)))
            {
                parts.Add("hips");
            }

            return parts;
        }

        /// <summary>Degrees round the wall, counted the way the target walks: 0 in front, 90 to the left.</summary>
        private static float Bearing(BanditTestContext t, Vector3 wall, Vector3 point)
        {
            Vector3 offset = point - wall;
            offset.y = 0f;
            float angle = Vector3.SignedAngle(t.Forward, offset, Vector3.up);
            return Mathf.Repeat(-angle, 360f);
        }

        /// <summary>
        /// A cover search run from where the bandit stands against where the target is now, without
        /// acting on it: what it would pick, and what became of the candidates by the wall. Says
        /// whether the wall offered no spot from this angle, or offered spots that were turned down.
        /// </summary>
        private static string DescribeWallSearch(BanditTestContext t, BanditBotController bandit,
            BanditBotController target, Vector3 wall)
        {
            List<BanditCoverCandidateReport> reports = new List<BanditCoverCandidateReport>();
            Vector3 threatEye = BanditGeometry.EyeOf(target.Self);
            bool moves = BanditCoverFinder.TryFindCover(bandit.Self.transform.position, threatEye,
                t.Config.CoverSearchRadius, t.Config.CoverRingSamples, t.Config.CoverMinimumThreatDistance,
                bandit.Profile?.PreferredEngagementRange ?? t.Config.PreferredEngagementRange, false,
                out BanditCoverSpot best, out BanditCoverSearchStats stats, reports);

            Dictionary<BanditCoverOutcome, int> byOutcome = new Dictionary<BanditCoverOutcome, int>();
            foreach (BanditCoverCandidateReport report in reports)
            {
                if (BanditTests.FlatDistance(report.Position, wall) < 9f)
                {
                    byOutcome.TryGetValue(report.Outcome, out int count);
                    byOutcome[report.Outcome] = count + 1;
                }
            }

            List<string> outcomes = new List<string>();
            foreach (KeyValuePair<BanditCoverOutcome, int> pair in byOutcome)
            {
                outcomes.Add($"{pair.Value} {pair.Key}");
            }

            string verdict = moves
                ? $"would move to a spot {BanditTests.FlatDistance(best.Position, wall):0.0}m from the wall, "
                  + $"{BanditTests.FlatDistance(best.Position, bandit.Self.transform.position):0.0}m away "
                  + $"(score {best.Score:0}, {(best.RequiresCrouch ? "crouch" : "standing")})"
                : best.Position != Vector3.zero
                    ? $"would stay where it is (score {best.Score:0})"
                    : "finds nowhere";

            return $"dry search: {verdict}; by the wall: "
                + (outcomes.Count > 0 ? string.Join(", ", outcomes.ToArray()) : "no candidates at all")
                + $"; {stats.Candidates} candidates, {stats.Viable} viable; {stats.Barricades} barricade(s) in range, "
                + $"{stats.BarricadeProbeMissed} missed by the probe, {stats.BarricadeNoGroundBehind} with no ground behind";
        }

        /// <summary>
        /// Every spot on a half-metre grid round the wall that would hide a body from the target
        /// right now - the ground truth the cover search is measured against. If there are none,
        /// being seen at this stop is the wall's fault, not the bandit's.
        /// </summary>
        private static string DescribeHidingSpots(BanditTestContext t, BanditBotController bandit,
            BanditBotController target, Vector3 wall)
        {
            const float Half = 9f;
            const float Step = 0.5f;
            Vector3 threatEye = BanditGeometry.EyeOf(target.Self);
            Vector3 self = bandit.Self.transform.position;
            int standing = 0;
            int crouching = 0;
            Vector3? nearest = null;

            for (float x = -Half; x <= Half; x += Step)
            {
                for (float z = -Half; z <= Half; z += Step)
                {
                    Vector3 point = wall + t.Right * x + t.Forward * z;
                    if (!BanditCoverFinder.IsHidingSpot(point, threatEye, true, out Vector3 ground))
                    {
                        continue;
                    }

                    crouching++;
                    if (BanditCoverFinder.IsHidingSpot(point, threatEye, false, out _))
                    {
                        standing++;
                    }

                    if (!nearest.HasValue || BanditTests.FlatDistance(ground, self) < BanditTests.FlatDistance(nearest.Value, self))
                    {
                        nearest = ground;
                    }
                }
            }

            return nearest.HasValue
                ? $"hiding spots within {Half:0}m of the wall: {standing} standing, {crouching} crouching; nearest "
                  + $"{BanditTests.FlatDistance(nearest.Value, self):0.0}m from the bandit, at {Bearing(t, wall, nearest.Value):0}° "
                  + $"round the wall {BanditTests.FlatDistance(nearest.Value, wall):0.0}m out"
                : $"no hiding spot anywhere within {Half:0}m of the wall";
        }

        public override IEnumerator Run(BanditTestContext t)
        {
            Vector3 home = t.At(0f, 0f);
            Vector3 wall = t.At(4f, 3f);
            Vector3[] circle = MovingTarget.Arc(wall, t.Forward, Radius, 360f);

            BanditFoundation.BuildSandbagWall(t, wall, circle[0], 3, 2);

            BanditBotController target = BanditEngagement.SpawnTarget(t, circle[0], home);
            BanditBotController bandit = t.Spawn("rifleman", "red", home, circle[0]);
            MovingTarget mover = new MovingTarget(t, target, circle, false);
            yield return t.WaitForBrains();

            BanditEngagement.Pacify(target);
            bandit.HoldFire = true;
            bandit.Brain.SetCoverEnabled(true);
            bandit.Brain.SetPeekEnabled(false);

            CoverWatch watch = new CoverWatch(t, target, new[] { bandit });
            t.Log($"{BanditTestContext.Name(bandit)} with a wall beside it, target {Radius:0}m out in front; "
                + "cover on, peek off.");

            yield return BanditFoundation.WaitInCover(t, bandit, 25f);
            watch.Counting = true;
            t.Log($"in cover; the target starts walking {Laps} circles round the wall.");

            List<string> failures = new List<string>();
            int stops = 0;

            for (int lap = 1; lap <= Laps; lap++)
            {
                for (int i = 1; i <= PointsPerLap; i++)
                {
                    yield return mover.WalkTo(circle[i % PointsPerLap]);

                    if (i % PointsPerStop != 0)
                    {
                        continue;
                    }

                    stops++;
                    yield return mover.Hold(StopSeconds);

                    int degrees = i * 360 / PointsPerLap;
                    List<string> parts = VisibleParts(target, bandit);
                    BanditBrain brain = bandit.Brain;
                    string doing = brain.HasCover
                        ? (brain.Navigator.HasDestination
                            ? $"walking to cover {BanditTests.FlatDistance(bandit.Self.transform.position, brain.CurrentCover.Position):0.0}m away"
                            : $"in cover, {BanditTests.FlatDistance(bandit.Self.transform.position, brain.CurrentCover.Position):0.0}m from its spot")
                        : $"without cover ({brain.LastCoverDrop})";
                    string line = $"lap {lap}, stop at {degrees}°: "
                        + (parts.Count == 0 ? "hidden" : $"SEEN - {string.Join(", ", parts.ToArray())}")
                        + $"; bandit {doing}, at {Bearing(t, wall, bandit.Self.transform.position):0}° round the wall "
                        + $"{BanditTests.FlatDistance(bandit.Self.transform.position, wall):0.0}m out, "
                        + $"stance {bandit.Self.stance.stance}, state {brain.State}";

                    t.Log(line);
                    t.Note(DescribeWallSearch(t, bandit, target, wall));
                    t.Note(DescribeHidingSpots(t, bandit, target, wall));
                    if (parts.Count > 0)
                    {
                        failures.Add($"lap {lap} at {degrees}° ({string.Join(", ", parts.ToArray())}; {doing})");
                    }
                }
            }

            CoverWatch.Record record = watch.Records[0];
            t.Note(watch.Describe(record));
            t.Note(mover.Describe());

            t.Check(failures.Count == 0, $"seen at {failures.Count} of {stops} stops: {string.Join("; ", failures.ToArray())}");
            t.Summary = $"hidden at all {stops} stops, {record.Picks} cover pick(s), "
                + $"longest seen while sitting in cover {record.LongestSeenInCover:0.0}s; {mover.Describe()}";
        }
    }
}
