using System.Collections;
using System.Collections.Generic;
using BanditPlugin.Commands;
using BanditPlugin.FakePlayer;
using BanditPlugin.Navigation;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Testing
{
    /// <summary>
    /// The foundations, kit by kit: shooting, taking cover, and peeking out of it - on one bandit
    /// and on a squad.
    ///
    /// Every test builds its own cover out of sandbags rather than trusting whatever the arena
    /// happens to have lying about, so a result is about the bandit and not about the map. The
    /// enemy is the same in all of them: a target that stands still, never shoots and cannot die,
    /// so the only thing that changes during a test is what the bandit decides to do.
    /// </summary>
    internal static class BanditFoundation
    {
        /// <summary>Vanilla's sandbag: about a metre high, so one course is crouch cover.</summary>
        public const ushort SandbagId = 365;

        /// <summary>
        /// A wall of sandbags <paramref name="wide"/> long and <paramref name="high"/> courses tall,
        /// centred on <paramref name="centre"/> and facing <paramref name="threat"/>. Pieces are laid
        /// edge to edge by their measured size, so the wall has no gaps whatever the model's width.
        /// </summary>
        public static void BuildSandbagWall(BanditTestContext t, Vector3 centre, Vector3 threat, int wide, int high)
        {
            Vector3 toward = threat - centre;
            toward.y = 0f;
            Vector3 lateral = Vector3.Cross(Vector3.up, toward.normalized);

            // One piece first, to learn how wide and tall a sandbag is.
            t.PlaceBarricade(SandbagId, centre, threat, out Bounds first);
            float pieceWidth = Mathf.Max(0.5f, Mathf.Abs(lateral.x) * first.size.x + Mathf.Abs(lateral.z) * first.size.z);
            float pieceHeight = Mathf.Max(0.3f, first.max.y - centre.y);

            // A sandbag is an arc, so pieces laid by their full width leave a gap at the hips.
            // Overlapped a little, they close it.
            float spacing = pieceWidth * 0.75f;

            // Where each column stands, found before anything else goes down. Snapping to the
            // ground afterwards finds the sandbags already placed, and a layer stacked on top of
            // that floats a whole course too high.
            Vector3[] columns = new Vector3[wide];
            for (int i = 0; i < wide; i++)
            {
                // 0, +1, -1, +2, -2 ... out from the centre.
                int step = (i + 1) / 2 * (i % 2 == 1 ? 1 : -1);
                columns[i] = i == 0 ? centre : GroundUnder(centre + lateral * (step * spacing), centre.y);
            }

            for (int layer = 0; layer < high; layer++)
            {
                for (int i = 0; i < wide; i++)
                {
                    if (layer == 0 && i == 0)
                    {
                        continue; // the measuring piece is the middle of the bottom course
                    }

                    t.PlaceBarricade(SandbagId, columns[i] + Vector3.up * (layer * pieceHeight), threat, out _);
                }
            }

            t.Note($"sandbag wall {wide} wide, {high} high: about {wide * pieceWidth:0.0}m by {high * pieceHeight:0.00}m.");
        }

        /// <summary>
        /// Waits for a bandit to be standing in the cover it chose, and if it never is, fails with
        /// where it actually is - how far from the spot, still walking or not, in what state - so
        /// a timeout says why rather than only that.
        /// </summary>
        public static IEnumerator WaitInCover(BanditTestContext t, BanditBotController bandit, float seconds)
        {
            float deadline = Time.time + seconds;
            while (!(bandit.Brain.HasCover && !bandit.Brain.Navigator.HasDestination))
            {
                if (Time.time >= deadline)
                {
                    string where = bandit.Brain.HasCover
                        ? $"{BanditTests.FlatDistance(bandit.Self.transform.position, bandit.Brain.CurrentCover.Position):0.00}m "
                          + $"from its spot, {(bandit.Brain.Navigator.HasDestination ? "still walking" : "stopped")}, "
                          + $"{(bandit.Brain.IsPeeking ? "peeking" : "hiding")}"
                        : "with no cover chosen";
                    throw new BanditTestFailure($"never settled into cover in {seconds:0}s: {BanditTestContext.Name(bandit)} "
                        + $"is {where}, state {bandit.Brain.State}, stance {bandit.Self.stance.stance}, "
                        + $"{(bandit.CurrentTarget != null ? "sees the target" : "cannot see the target")}, "
                        + $"last dropped cover: {bandit.Brain.LastCoverDrop}");
                }

                yield return null;
            }
        }

        /// <summary>The ground under a point, ignoring anything a test has built there.</summary>
        private static Vector3 GroundUnder(Vector3 point, float fallbackY)
        {
            RaycastHit[] hits = Physics.RaycastAll(point + Vector3.up * 50f, Vector3.down, 100f,
                RayMasks.GROUND | RayMasks.GROUND2, QueryTriggerInteraction.Ignore);

            float best = float.MinValue;
            foreach (RaycastHit hit in hits)
            {
                best = Mathf.Max(best, hit.point.y);
            }

            point.y = best > float.MinValue ? best : fallbackY;
            return point;
        }

        /// <summary>
        /// Counts the hits each bandit lands on the target. Read off the damage request itself, before
        /// the target's immortality cancels it, so a hit is counted whether or not it was allowed.
        /// </summary>
        public static Dictionary<BanditBotController, int> CountHits(BanditTestContext t, BanditBotController target)
        {
            Dictionary<BanditBotController, int> hits = new Dictionary<BanditBotController, int>();

            DamageTool.DamagePlayerHandler counter = (ref DamagePlayerParameters parameters, ref bool shouldAllow) =>
            {
                if (target.Self == null || parameters.player != target.Self)
                {
                    return;
                }

                BanditBotController shooter = t.FindBySteamId(parameters.killer);
                if (shooter != null && shooter != target)
                {
                    hits.TryGetValue(shooter, out int count);
                    hits[shooter] = count + 1;
                }
            };

            DamageTool.damagePlayerRequested += counter;
            t.OnCleanup(() => DamageTool.damagePlayerRequested -= counter);
            return hits;
        }

        public static int Total(Dictionary<BanditBotController, int> hits)
        {
            int total = 0;
            foreach (int count in hits.Values)
            {
                total += count;
            }

            return total;
        }

        /// <summary>
        /// How far from the target a kit is tested at: half its firing range, so a miss is about
        /// aim and not about reach, and never closer than 15m.
        /// </summary>
        public static float TestDistance(BanditConfiguration config, string kit)
        {
            BanditProfile profile = BanditProfile.FromKit(config, config.FindKit(kit));
            return Mathf.Clamp(profile.FireRange * 0.5f, 15f, 80f);
        }
    }

    /// <summary>
    /// Watches bandits in cover against one target, frame by frame: whether the target can see
    /// them, how many times they picked a cover spot, and how far they strayed from it.
    /// </summary>
    internal sealed class CoverWatch
    {
        public sealed class Record
        {
            public BanditBotController Bandit;
            public int Picks;
            public Vector3? Spot;
            public int Frames;
            public int SeenFrames;
            public int PeekFrames;
            public int SeenWhileHiddenFrames;
            public float FurthestFromSpot;
            public bool Crouch;
            public bool HadCover;
            public bool Away;

            /// <summary>
            /// The longest stretch it sat in its cover - arrived, not peeking - while the target
            /// could see it: how long it took to notice its spot had stopped working.
            /// </summary>
            public float LongestSeenInCover;
            public float? SeenInCoverSince;
        }

        private readonly BanditBotController _target;
        public readonly List<Record> Records = new List<Record>();

        /// <summary>Start counting frames only once this is set - the walk to cover is not under test.</summary>
        public bool Counting;

        public CoverWatch(BanditTestContext t, BanditBotController target, IEnumerable<BanditBotController> bandits)
        {
            _target = target;
            foreach (BanditBotController bandit in bandits)
            {
                Records.Add(new Record { Bandit = bandit });
            }

            t.Always(() =>
            {
                foreach (Record record in Records)
                {
                    Update(t, record);
                }

                return null;
            });
        }

        private void Update(BanditTestContext t, Record record)
        {
            BanditBotController bandit = record.Bandit;
            if (bandit == null || bandit.Self == null || bandit.Brain == null || BanditCommand.IsDead(bandit))
            {
                return;
            }

            if (record.HadCover && !bandit.Brain.HasCover)
            {
                t.Note($"{BanditTestContext.Name(bandit)} dropped its cover - {bandit.Brain.LastCoverDrop}; "
                    + (record.Spot.HasValue
                        ? $"{BanditTests.FlatDistance(bandit.Self.transform.position, record.Spot.Value):0.00}m from the spot"
                        : string.Empty));
            }

            record.HadCover = bandit.Brain.HasCover;

            if (bandit.Brain.HasCover)
            {
                Vector3 spot = bandit.Brain.CurrentCover.Position;
                if (!record.Spot.HasValue || (record.Spot.Value - spot).sqrMagnitude > 0.25f)
                {
                    record.Spot = spot;
                    record.Picks++;
                    record.Crouch = bandit.Brain.CurrentCover.RequiresCrouch;
                    t.Note($"{BanditTestContext.Name(bandit)} cover pick {record.Picks}: "
                        + $"{BanditTests.FlatDistance(spot, _target.Self.transform.position):0.0}m from the target, "
                        + $"{BanditTests.FlatDistance(spot, bandit.Self.transform.position):0.0}m from the bandit, "
                        + (record.Crouch ? "crouch" : "standing")
                        + (bandit.Brain.CurrentCover.CanPeek ? ", can peek" : ", no peek"));
                }
            }

            if (!Counting)
            {
                return;
            }

            record.Frames++;
            bool seen = _target.HasLineOfSightTo(bandit.Self);
            bool peeking = bandit.Brain.IsPeeking;

            if (seen)
            {
                record.SeenFrames++;
            }

            if (peeking)
            {
                record.PeekFrames++;
            }
            else if (seen)
            {
                record.SeenWhileHiddenFrames++;
            }

            bool settled = bandit.Brain.HasCover && !peeking && !bandit.Brain.Navigator.HasDestination;
            if (settled && seen)
            {
                record.SeenInCoverSince = record.SeenInCoverSince ?? Time.time;
                record.LongestSeenInCover = Mathf.Max(record.LongestSeenInCover, Time.time - record.SeenInCoverSince.Value);
            }
            else
            {
                record.SeenInCoverSince = null;
            }

            if (record.Spot.HasValue)
            {
                float off = BanditTests.FlatDistance(bandit.Self.transform.position, record.Spot.Value);
                record.FurthestFromSpot = Mathf.Max(record.FurthestFromSpot, off);

                // Each time it leaves its spot, say what it was doing - where the navigator was
                // taking it, relative to the spot and to the peek position, and in what state.
                bool away = off > 0.6f;
                if (away && !record.Away)
                {
                    BanditNavigator nav = bandit.Brain.Navigator;
                    BanditCoverSpot cover = bandit.Brain.CurrentCover;
                    t.Note($"{BanditTestContext.Name(bandit)} left its spot ({off:0.0}m): state {bandit.Brain.State}, "
                        + (bandit.Brain.IsPeeking ? "peeking" : "hiding") + ", "
                        + (nav.HasDestination
                            ? $"walking to a point {BanditTests.FlatDistance(nav.Destination, cover.Position):0.0}m from the spot "
                              + $"and {BanditTests.FlatDistance(nav.Destination, cover.PeekPosition):0.0}m from the peek position"
                            : "not walking anywhere")
                        + $", peek position {BanditTests.FlatDistance(cover.PeekPosition, cover.Position):0.0}m from the spot, "
                        + $"moving {(bandit.Brain.MoveDirection.sqrMagnitude > 0.0001f ? "yes" : "no")}");
                }

                record.Away = away;
            }
        }

        public static float Fraction(int part, int whole) => whole > 0 ? (float)part / whole : 0f;

        public string Describe(Record record)
        {
            int hiddenFrames = record.Frames - record.PeekFrames;
            return $"{BanditTestContext.Name(record.Bandit)}: {record.Picks} cover pick(s), "
                + (record.Crouch ? "crouch" : "standing") + " cover, "
                + $"seen {Fraction(record.SeenWhileHiddenFrames, hiddenFrames):P0} of the time it meant to be hidden, "
                + $"peeking {Fraction(record.PeekFrames, record.Frames):P0}, "
                + $"strayed up to {record.FurthestFromSpot:0.0}m from its spot, "
                + $"longest seen while sitting in cover {record.LongestSeenInCover:0.0}s";
        }
    }

    /// <summary>
    /// One bandit of a kit, cover off, against the target at half its firing range in the open: it
    /// has to start hitting within a few seconds and keep hitting. When it does not, the reason is
    /// whatever its own fire-block report says - out of range, no line, friendly in the way.
    /// </summary>
    public sealed class ShootTest : BanditTest
    {
        private const float FirstHitSeconds = 15f;
        private const float KeepSeconds = 10f;
        private readonly string _kit;
        private readonly float? _distance;
        private readonly BanditStance? _stance;
        private readonly string _suffix;

        /// <param name="distance">Metres to the target; half the kit's firing range if not given.</param>
        /// <param name="stance">A stance to hold, overriding whatever the kit chooses on contact.</param>
        public ShootTest(string kit, float? distance = null, BanditStance? stance = null, string suffix = "")
        {
            _kit = kit;
            _distance = distance;
            _stance = stance;
            _suffix = suffix;
        }

        /// <summary>
        /// The server's own check on a hit report, done by hand: PlayerInput.getInput casts from the
        /// shooter's eye to the hit and back again on DAMAGE_SERVER, and throws the report away if
        /// either ray meets anything that is not the target. Whatever it meets is named here.
        /// </summary>
        private static string DescribeOcclusion(BanditBotController shooter, BanditBotController target)
        {
            Vector3 eye = shooter.Self.look.aim.position;
            Vector3 point = BanditGeometry.EyeOf(target.Self) - Vector3.up * 0.5f;
            Vector3 delta = point - eye;
            float length = delta.magnitude;
            Vector3 direction = delta / length;

            string Name(RaycastHit hit) => hit.transform == null
                ? "nothing"
                : $"'{hit.transform.name}' (tag {hit.transform.tag}, layer {LayerMask.LayerToName(hit.transform.gameObject.layer)}"
                  + $"{(hit.transform.IsChildOf(shooter.Self.transform) ? ", the SHOOTER's own" : hit.transform.IsChildOf(target.Self.transform) ? ", the target's" : string.Empty)}) "
                  + $"{hit.distance:0.00}m along";

            Physics.Raycast(new Ray(eye, direction), out RaycastHit forward, length - 0.025f, RayMasks.DAMAGE_SERVER);
            Physics.Raycast(new Ray(eye + direction * (length - 0.025f), -direction), out RaycastHit backward,
                length - 0.025f, RayMasks.DAMAGE_SERVER);

            return $"occlusion check from the eye at {eye.y - shooter.Self.transform.position.y:0.00}m "
                + $"(stance {shooter.Self.stance.stance}): forward ray meets {Name(forward)}; "
                + $"backward ray meets {Name(backward)}.";
        }

        public override string Name => $"shoot-{_kit}{_suffix}";
        public override string Description => $"a lone {_kit} in the open hits a target "
            + (_distance.HasValue ? $"{_distance:0}m away" : "at half its firing range")
            + (_stance.HasValue ? $", held {_stance.Value.ToString().ToLowerInvariant()}" : string.Empty);
        public override float TimeoutSeconds => FirstHitSeconds + KeepSeconds + 30f;

        public override IEnumerator Run(BanditTestContext t)
        {
            float distance = _distance ?? BanditFoundation.TestDistance(t.Config, _kit);
            BanditBotController target = BanditEngagement.SpawnTarget(t, t.At(0f, distance), t.At(0f, 0f));
            BanditBotController shooter = t.Spawn(_kit, "red", t.At(0f, 0f), t.At(0f, distance));
            Dictionary<BanditBotController, int> hits = BanditFoundation.CountHits(t, target);
            yield return t.WaitForBrains();

            BanditEngagement.Pacify(target);
            shooter.Brain.SetCoverEnabled(false);
            shooter.Brain.SetPeekEnabled(false);
            if (_stance.HasValue)
            {
                shooter.Brain.SetStanceOrder(_stance.Value);
            }

            shooter.HoldFire = false;

            t.Log($"{BanditTestContext.Name(shooter)} weapons free, target {distance:0}m away in the open"
                + (_stance.HasValue ? $", held {_stance.Value.ToString().ToLowerInvariant()}." : "."));

            float began = Time.time;
            while (BanditFoundation.Total(hits) == 0)
            {
                if (Time.time - began > FirstHitSeconds)
                {
                    t.Note(DescribeOcclusion(shooter, target));
                    t.Note($"hit reports: {shooter.HitReportsSent} sent, {shooter.HitReportsOnPlayers} on a player, "
                        + $"last other hit {shooter.LastNonPlayerHit}.");
                    throw new BanditTestFailure($"no hit in {FirstHitSeconds:0}s at {distance:0}m - "
                        + $"{shooter.RoundsFired} round(s) fired from {shooter.EquippedWeaponName}, "
                        + $"now {BanditTests.FlatDistance(shooter.Self.transform.position, target.Self.transform.position):0}m out, "
                        + $"stance {shooter.Self.stance.stance}, fire: {shooter.DescribeFireBlock()}");
                }

                yield return null;
            }

            float firstHit = Time.time - began;
            t.Log($"first hit after {firstHit:0.0}s.");

            yield return t.Wait(KeepSeconds);

            int total = BanditFoundation.Total(hits);
            int rounds = shooter.RoundsFired;
            float now = BanditTests.FlatDistance(shooter.Self.transform.position, target.Self.transform.position);
            t.Check(total >= 2, $"only {total} hit(s) from {rounds} round(s) fired, ending {now:0}m out - "
                + $"fire: {shooter.DescribeFireBlock()}");

            t.Summary = $"first hit at {firstHit:0.0}s, {total} hits from {rounds} rounds "
                + $"({CoverWatch.Fraction(total, rounds):P0}) at {distance:0}m with {shooter.EquippedWeaponName}";
        }
    }

    /// <summary>
    /// One bandit with a sandbag wall beside it, holding fire and not peeking, against a target in
    /// the open. It has to go behind the wall, be hidden there, and then stay - one cover pick for
    /// the whole test, because the target never moves and nothing else can spoil the spot.
    ///
    /// The wall's height decides what kind of cover it is: one course of sandbags only hides a
    /// crouching body, two hide one standing. Both are checked.
    /// </summary>
    public sealed class SandbagCoverTest : BanditTest
    {
        private const float ThreatDistance = 30f;
        private const float WatchSeconds = 15f;
        private readonly string _name;
        private readonly string _kit;
        private readonly int _wide;
        private readonly int _courses;

        public SandbagCoverTest(string name, string kit, int wide, int courses)
        {
            _name = name;
            _kit = kit;
            _wide = wide;
            _courses = courses;
        }

        /// <summary>
        /// One sandbag is 1.07m. A crouching player's eye is at 1.2, so a single course hides
        /// nobody, and the right answer is to refuse it - which is the bug this case exists for.
        /// </summary>
        private bool TooLow => _courses < 2;

        public override string Name => _name;
        public override string Description => TooLow
            ? $"a {_kit} refuses a {_wide}-wide, 1-high sandbag wall - it does not hide a crouching head"
            : $"a {_kit} hides behind a {_wide}-wide, {_courses}-high sandbag wall and stays there";
        public override float TimeoutSeconds => WatchSeconds + 60f;

        public override IEnumerator Run(BanditTestContext t)
        {
            Vector3 home = t.At(0f, 0f);
            Vector3 threatSpot = t.At(0f, ThreatDistance);

            // The wall a few metres to the side and a little forward - close enough to be the
            // obvious choice, far enough that taking it is a decision rather than an accident.
            Vector3 wall = t.At(4f, 3f);
            BanditFoundation.BuildSandbagWall(t, wall, threatSpot, _wide, _courses);

            BanditBotController target = BanditEngagement.SpawnTarget(t, threatSpot, home);
            BanditBotController bandit = t.Spawn(_kit, "red", home, threatSpot);
            yield return t.WaitForBrains();

            BanditEngagement.Pacify(target);
            bandit.HoldFire = true;
            bandit.Brain.SetPeekEnabled(false);
            bandit.Brain.SetCoverEnabled(false);

            yield return t.WaitUntil(() => target.HasLineOfSightTo(bandit.Self), 5f,
                "the target to see the bandit in the open");

            CoverWatch watch = new CoverWatch(t, target, new[] { bandit });
            bandit.Brain.SetCoverEnabled(true);
            t.Log($"{BanditTestContext.Name(bandit)} in the open, seen from {ThreatDistance:0}m; cover on, peek off.");

            if (TooLow)
            {
                // Give it the time it would take to choose and walk there, then make sure it did
                // not: any spot within a couple of metres of the wall is behind the wall.
                yield return t.Wait(10f);
                CoverWatch.Record tried = watch.Records[0];
                // At the wall's own level, too: the arena is on the lip of a road cutting, and a
                // spot two metres from the wall on the map but six below it, down on the road, is
                // hidden by the bank and nothing to do with the wall.
                bool usedWall = tried.Spot.HasValue && BanditTests.FlatDistance(tried.Spot.Value, wall) < 2.5f
                    && Mathf.Abs(tried.Spot.Value.y - wall.y) < 1.5f;
                if (usedWall)
                {
                    // What the target can actually see standing on that spot, and how the spot
                    // sits against the wall - a spot that passed as standing cover behind a wall
                    // shorter than a head is either lower than the wall's base or not where the
                    // wall is between it and the target.
                    Vector3 spot = tried.Spot.Value;
                    List<string> heights = new List<string>();
                    foreach (float height in new[] { 1.75f, 1.35f, 1f, 0.55f })
                    {
                        if (target.CanSeePoint(spot + Vector3.up * height))
                        {
                            heights.Add($"{height:0.00}m");
                        }
                    }

                    // Everything a ray straight down through the spot passes, every layer, top
                    // first - what the ground there really is, and what the snap could have hit.
                    List<string> column = new List<string>();
                    RaycastHit[] through = Physics.RaycastAll(spot + Vector3.up * 12f, Vector3.down, 24f, ~0,
                        QueryTriggerInteraction.Collide);
                    System.Array.Sort(through, (a, b) => b.point.y.CompareTo(a.point.y));
                    foreach (RaycastHit hit in through)
                    {
                        column.Add($"'{hit.transform.name}' layer {LayerMask.LayerToName(hit.transform.gameObject.layer)}"
                            + $"{(hit.collider.isTrigger ? " (trigger)" : string.Empty)} at {hit.point.y - wall.y:+0.00;-0.00}m");
                    }

                    t.Note("straight down through the spot: " + (column.Count > 0 ? string.Join("; ", column.ToArray()) : "nothing"));

                    Vector3 toTarget = target.Self.transform.position - spot;
                    Vector3 toWall = wall - spot;
                    toTarget.y = 0f;
                    toWall.y = 0f;
                    t.Note($"the spot it took: {BanditTests.FlatDistance(spot, wall):0.0}m from the wall's centre, "
                        + $"ground {spot.y - wall.y:+0.00;-0.00}m against the wall's base, wall "
                        + $"{Vector3.Angle(toTarget, toWall):0}° off the line to the target; the target sees it at "
                        + (heights.Count > 0 ? string.Join(", ", heights.ToArray()) : "no height")
                        + $"; cover {(bandit.Brain.HasCover ? $"held, {(bandit.Brain.CurrentCover.RequiresCrouch ? "crouch" : "standing")}" : $"dropped ({bandit.Brain.LastCoverDrop})")}");
                }

                t.Check(!usedWall, $"took the 1-high wall as {(tried.Crouch ? "crouch" : "standing")} cover - "
                    + "its head is above it either way");
                t.Summary = tried.Spot.HasValue
                    ? $"left the wall alone, chose cover {BanditTests.FlatDistance(tried.Spot.Value, wall):0}m from it"
                    : "left the wall alone, and found nothing better here";
                yield break;
            }

            yield return t.WaitUntil(() => bandit.Brain.HasCover, 15f, "the bandit to choose cover");
            yield return BanditFoundation.WaitInCover(t, bandit, 20f);
            t.Log("in cover.");

            watch.Counting = true;
            yield return t.Wait(WatchSeconds);

            CoverWatch.Record record = watch.Records[0];
            t.Note(watch.Describe(record));

            float seen = CoverWatch.Fraction(record.SeenWhileHiddenFrames, record.Frames - record.PeekFrames);
            t.Check(record.Picks == 1, $"picked cover {record.Picks} times against a target that never moved");
            t.Check(seen < 0.2f, $"visible {seen:P0} of the time in cover - the spot does not hide it");
            t.Check(record.FurthestFromSpot < 1.5f, $"wandered {record.FurthestFromSpot:0.0}m from its cover spot");
            t.Check(BanditTests.FlatDistance(record.Spot.Value, wall) < 2.5f,
                $"hid {BanditTests.FlatDistance(record.Spot.Value, wall):0.0}m from the wall rather than behind it");

            t.Summary = $"{(record.Crouch ? "crouch" : "standing")} cover, one pick, seen {seen:P0} in {WatchSeconds:0}s";
        }
    }

    /// <summary>
    /// The same wall, weapons free and peeking: the bandit has to alternate hiding and showing
    /// itself, hit the target from the peek, stay hidden while it is not peeking, and never give
    /// up the cover - the target is standing still, so there is never a reason to.
    /// </summary>
    public sealed class PeekTest : BanditTest
    {
        private const float ThreatDistance = 30f;
        private const float WatchSeconds = 25f;
        private readonly string _kit;
        private readonly int _wide;
        private readonly int _courses;
        private readonly string _suffix;

        public PeekTest(string kit, int wide, int courses, string suffix = "")
        {
            _kit = kit;
            _wide = wide;
            _courses = courses;
            _suffix = suffix;
        }

        public override string Name => $"peek-{_kit}{_suffix}";
        public override string Description => $"a {_kit} behind a {_wide}-wide, {_courses}-high sandbag wall peeks, hits, and keeps its cover";
        public override float TimeoutSeconds => WatchSeconds + 60f;

        public override IEnumerator Run(BanditTestContext t)
        {
            Vector3 home = t.At(0f, 0f);
            Vector3 threatSpot = t.At(0f, ThreatDistance);
            BanditFoundation.BuildSandbagWall(t, t.At(4f, 3f), threatSpot, _wide, _courses);

            BanditBotController target = BanditEngagement.SpawnTarget(t, threatSpot, home);
            BanditBotController bandit = t.Spawn(_kit, "red", home, threatSpot);
            Dictionary<BanditBotController, int> hits = BanditFoundation.CountHits(t, target);
            yield return t.WaitForBrains();

            BanditEngagement.Pacify(target);
            bandit.HoldFire = true;
            bandit.Brain.SetCoverEnabled(true);
            bandit.Brain.SetPeekEnabled(true);

            CoverWatch watch = new CoverWatch(t, target, new[] { bandit });
            t.Log($"{BanditTestContext.Name(bandit)} with a {_courses}-high wall beside it, target {ThreatDistance:0}m; cover and peek on.");

            yield return BanditFoundation.WaitInCover(t, bandit, 25f);

            bandit.HoldFire = false;
            int hitsBefore = BanditFoundation.Total(hits);
            watch.Counting = true;
            t.Log("in cover, weapons free.");

            yield return t.Wait(WatchSeconds);

            CoverWatch.Record record = watch.Records[0];
            t.Note(watch.Describe(record));

            int landed = BanditFoundation.Total(hits) - hitsBefore;
            float peeking = CoverWatch.Fraction(record.PeekFrames, record.Frames);
            float seenHidden = CoverWatch.Fraction(record.SeenWhileHiddenFrames, record.Frames - record.PeekFrames);

            t.Check(record.Picks == 1, $"picked cover {record.Picks} times against a target that never moved");
            t.Check(peeking > 0.1f, $"peeked only {peeking:P0} of the time - it is not coming out to shoot");
            t.Check(seenHidden < 0.2f, $"visible {seenHidden:P0} of the time it was meant to be hidden");
            t.Check(landed >= 1, $"no hits in {WatchSeconds:0}s of peeking - fire: {bandit.DescribeFireBlock()}");
            t.Check(record.FurthestFromSpot < 2.5f, $"wandered {record.FurthestFromSpot:0.0}m from its cover spot");

            t.Summary = $"{landed} hit(s) in {WatchSeconds:0}s, peeking {peeking:P0}, seen {seenHidden:P0} while hidden, one pick";
        }
    }

    /// <summary>
    /// A rifle squad with a sandbag wall in front of each member: everybody has to end up in their
    /// own cover, hidden, peeking, and the squad has to land hits - without anybody swapping spots
    /// or walking off, because the target never moves.
    /// </summary>
    public sealed class SquadCoverTest : BanditTest
    {
        private const float ThreatDistance = 35f;
        private const float WatchSeconds = 25f;

        public override string Name => "squad-cover";
        public override string Description => "a rifle squad each takes its own sandbag wall, peeks and hits, and holds";
        public override float TimeoutSeconds => WatchSeconds + 70f;

        public override IEnumerator Run(BanditTestContext t)
        {
            Vector3 home = t.At(0f, 0f);
            Vector3 threatSpot = t.At(0f, ThreatDistance);

            List<BanditBotController> squad = BanditEngagement.SpawnSquad(t, "rifle", "red", home, threatSpot);

            // A two-sandbag stack beside each member, the same layout the single-bandit tests use:
            // off to one side and a little forward, so every member starts in the open and seen,
            // and has a piece of cover of its own a few steps away. Built in front of them, the
            // walls hid the whole squad from the start and nobody had a reason to move.
            foreach (BanditBotController member in squad)
            {
                Vector3 at = member.Self.transform.position + t.Right * 2.5f + t.Forward * 3f;
                BanditFoundation.BuildSandbagWall(t, BanditPlacement.SnapToGround(at), threatSpot, 1, 2);
            }

            BanditBotController target = BanditEngagement.SpawnTarget(t, threatSpot, home);
            Dictionary<BanditBotController, int> hits = BanditFoundation.CountHits(t, target);
            yield return t.WaitForBrains();
            BanditEngagement.Pacify(target);

            foreach (BanditBotController member in squad)
            {
                member.HoldFire = true;
                member.Brain.SetCoverEnabled(true);
                member.Brain.SetPeekEnabled(true);
            }

            CoverWatch watch = new CoverWatch(t, target, squad);
            t.Log($"rifle squad of {squad.Count} with {squad.Count} walls, target {ThreatDistance:0}m.");

            foreach (BanditBotController member in squad)
            {
                yield return BanditFoundation.WaitInCover(t, member, 30f);
            }

            foreach (BanditBotController member in squad)
            {
                member.HoldFire = false;
            }

            watch.Counting = true;
            t.Log("all in cover, weapons free.");
            yield return t.Wait(WatchSeconds);

            List<string> problems = new List<string>();
            for (int i = 0; i < watch.Records.Count; i++)
            {
                CoverWatch.Record record = watch.Records[i];
                t.Note(watch.Describe(record));

                float seenHidden = CoverWatch.Fraction(record.SeenWhileHiddenFrames, record.Frames - record.PeekFrames);
                if (record.Picks > 1)
                {
                    problems.Add($"#{record.Bandit.Id} picked cover {record.Picks} times");
                }
                if (seenHidden >= 0.2f)
                {
                    problems.Add($"#{record.Bandit.Id} seen {seenHidden:P0} while hidden");
                }

                for (int j = 0; j < i; j++)
                {
                    CoverWatch.Record other = watch.Records[j];
                    if (record.Spot.HasValue && other.Spot.HasValue
                        && BanditTests.FlatDistance(record.Spot.Value, other.Spot.Value) < 1.5f)
                    {
                        problems.Add($"#{record.Bandit.Id} and #{other.Bandit.Id} share a spot");
                    }
                }
            }

            int total = BanditFoundation.Total(hits);
            if (total == 0)
            {
                problems.Add("the squad landed no hits");
            }

            t.Check(problems.Count == 0, string.Join("; ", problems.ToArray()));
            t.Summary = $"{squad.Count} in their own cover, {total} hit(s) in {WatchSeconds:0}s, nobody moved";
        }
    }
}
