using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.BanditGeometry;

namespace BanditPlugin.Navigation
{
    /// <summary>A position that breaks line of sight from a particular threat.</summary>
    public struct BanditCoverSpot
    {
        public Vector3 Position;

        /// <summary>
        /// True when crouching here hides the bot but standing exposes it. This is the *best* kind
        /// of cover: the bot can duck to be safe and stand up to shoot, with no repositioning.
        /// False means hard cover - hidden either way - which is safer but silent unless the bot
        /// steps out to <see cref="PeekPosition"/>.
        /// </summary>
        public bool RequiresCrouch;

        /// <summary>True when a step to the side yields a firing angle on the threat.</summary>
        public bool CanPeek;

        public Vector3 PeekPosition;

        public float Score;

        /// <summary>
        /// Whether the spot still hides the bandit a hand's breadth either side of it. A spot
        /// without that margin - behind a pole or a trunk - only works stood on exactly, so it is
        /// walked to more precisely. Set on the chosen spot only.
        /// </summary>
        public bool HasMargin;
    }

    /// <summary>What happened to one candidate spot during a search.</summary>
    public enum BanditCoverOutcome
    {
        Chosen,
        Viable,
        NoGround,
        TooCloseToThreat,
        NoStandingRoom,
        StillVisible,

        /// <summary>A squadmate got there first, or is close enough that this would bunch them up.</summary>
        TakenBySquadmate,

        /// <summary>A cover slot on the threat's side of its object - in front of the cover, not behind it.</summary>
        FacesThreat,

        /// <summary>Getting there means running toward the threat, or across its front.</summary>
        TowardThreat,

        /// <summary>Hidden from where the threat is, but not from where it is heading.</summary>
        ExposedSoon
    }

    /// <summary>One candidate and its verdict, for /bandit debug cover to draw and log.</summary>
    public struct BanditCoverCandidateReport
    {
        public Vector3 Position;
        public BanditCoverOutcome Outcome;
    }

    /// <summary>
    /// Why a cover search came out the way it did. A failed search is otherwise invisible - the
    /// bot just stands there and nothing is logged, because nothing went wrong - so /bandit debug cover
    /// reports these counts and turns "cover doesn't work" into a specific rejected test.
    /// </summary>
    public struct BanditCoverSearchStats
    {
        public int Candidates;
        public int RejectedNoGround;
        public int RejectedTooCloseToThreat;
        public int RejectedNoStandingRoom;
        public int RejectedNotCover;
        public int RejectedNotBetter;

        /// <summary>Rejected because a squadmate had already claimed that spot or one next to it.</summary>
        public int RejectedTakenBySquadmate;

        public int Viable;

        /// <summary>Of the candidates, how many were cover slots - see BanditCoverSlots.</summary>
        public int Slots;
        public int RejectedFacesThreat;
        public int RejectedTowardThreat;
        public int RejectedExposedSoon;

        /// <summary>Candidates the budget ran out before - ranked too low to be worth the raycasts.</summary>
        public int NotEvaluated;

        /// <summary>Barricades in range - placed cover, the kind a test builds - and what became of them.</summary>
        public int Barricades;

        /// <summary>Barricades whose probe ray from the threat met nothing, so they offered no spot.</summary>
        public int BarricadeProbeMissed;

        /// <summary>Barricades the ray met, whose stepping back never got off the top of cover.</summary>
        public int BarricadeNoGroundBehind;

        /// <summary>
        /// True when the overlap query filled its collider buffer, i.e. there were more solid
        /// things within the search radius than the search could look at. Worth surfacing rather
        /// than swallowing: OverlapSphereNonAlloc returns an arbitrary subset once it is full - not
        /// the nearest ones - so a truncated search in a dense town is quietly sampling the wrong
        /// half of the map around it, which looks identical to "the search just didn't find much".
        /// </summary>
        public bool ColliderBufferFull;

        public override string ToString()
        {
            return $"{Candidates} candidates: {RejectedNoGround} no ground, " +
                   $"{RejectedTooCloseToThreat} too close to threat, {RejectedNoStandingRoom} no room, " +
                   $"{RejectedNotCover} still visible, {RejectedNotBetter} not better than here, " +
                   $"{RejectedTakenBySquadmate} taken by squadmates, {RejectedFacesThreat} facing the threat, " +
                   $"{RejectedTowardThreat} toward the threat, {RejectedExposedSoon} exposed soon, " +
                   $"{NotEvaluated} not evaluated, {Viable} viable ({Slots} of the candidates were slots)"
                   + (ColliderBufferFull ? " (collider buffer full - reduce CoverSearchRadius)" : string.Empty);
        }
    }

    /// <summary>
    /// Finds somewhere to stand where a given threat cannot see you.
    ///
    /// Candidates come from two sources, because they catch different things:
    ///   - the colliders actually near the bot (trees, rocks, vehicles, walls, barricades), taking
    ///     the point on the far side of each from the threat - this is the "get behind that tree"
    ///     case, and it aims at real geometry rather than hoping a sample lands behind it;
    ///   - a ring of blind samples, which catches terrain folds, ditches and building corners that
    ///     no single collider points at.
    ///
    /// Every survivor is then scored on the same two raycasts vanilla sentries use for visibility,
    /// so "in cover" here means exactly what "can't be shot" means everywhere else in the plugin.
    /// </summary>
    public static partial class BanditCoverFinder
    {
        /// <summary>
        /// Eye height used for the peek test. Unlike the body samples this really is one point:
        /// peeking is about whether the bot's *aim origin* can see the target, and that is a
        /// single place - the same one its bullets leave from: vanilla's standing look height
        /// (PlayerLook.HEIGHT_LOOK_STAND), which is also where a bandit standing up out of crouch
        /// cover shoots from.
        /// </summary>
        private const float PeekEyeHeight = StandingEyeHeight;

        /// <summary>
        /// Half the shoulder width. The body samples are spread this far either side of centre,
        /// perpendicular to the threat, because that is the silhouette a shooter actually sees.
        /// </summary>
        private const float ShoulderHalfWidth = 0.22f;

        /// <summary>
        /// Heights sampled down the body when standing: the top of the head, chest, waist, knees.
        ///
        /// The top sample is the head, not somewhere near it. Vanilla puts a standing player's eye
        /// at 1.75m (PlayerLook.HEIGHT_LOOK_STAND) and the head goes on above that; these used to
        /// stop at 1.65, so a wall a little short of eye level passed as standing cover with the
        /// bandit's whole head showing over it - and the enemy's line of sight, which is taken eye
        /// to eye, saw it every time.
        /// </summary>
        private static readonly float[] StandingSampleHeights = { 1.85f, 1.35f, 1.0f, 0.55f };

        /// <summary>
        /// The same, crouched. A crouching eye is at 1.2m (PlayerLook.HEIGHT_LOOK_CROUCH), so the
        /// head reaches about 1.3. These used to top out at 0.95, which is why a single sandbag -
        /// 1.07m - was taken as crouch cover by bandits whose heads were plainly above it. It takes
        /// two sandbags to hide a crouching man.
        /// </summary>
        private static readonly float[] CrouchSampleHeights = { 1.3f, 0.95f, 0.6f, 0.3f };

        /// <summary>Offsets tried when nudging a chosen spot deeper into cover, nearest first.</summary>
        private static readonly float[] RefinementDistances = { 0.4f, 0.8f, 1.2f };

        /// <summary>
        /// Offsets tried when the bot is hit *while hidden* and wants to pull further in. Longer
        /// than the refinement steps: the spot already passed the body test, so if shots are still
        /// landing the margin needs to grow by more than a shuffle.
        /// </summary>
        private static readonly float[] PullDeeperDistances = { 0.5f, 1f, 1.5f, 2f, 2.5f };

        /// <summary>How far to the side a "peek" step is.</summary>
        private const float PeekOffset = 0.9f;

        /// <summary>
        /// Further steps tried, nearest first, when the short one is still behind the cover. Only
        /// for cover that hides the bandit standing - crouch cover peeks by standing up. A tree
        /// trunk is cleared in under a metre, but the middle of a wall is not: behind a sandbag
        /// wall seven metres long the 0.9m step was still in its shadow, the spot was marked "no
        /// peek", and the bandit sat out the fight in perfect, useless cover.
        /// </summary>
        private static readonly float[] WidePeekOffsets = { 1.5f, 2.25f, 3f, 3.75f };

        /// <summary>Candidates rounded onto a grid this size are treated as the same spot.</summary>
        private const float DedupeGridSize = 1.5f;

        /// <summary>Colliders bigger than this are terrain/landscape, not cover to hide behind.</summary>
        private const float MaxCoverColliderSize = 40f;

        /// <summary>A collider shorter than this can't hide a crouching player.</summary>
        private const float MinCoverColliderHeight = 0.9f;

        /// <summary>How far the standing-room capsule is lifted off the floor. See HasStandingRoom.</summary>
        private const float GroundClearance = 0.15f;

        /// <summary>
        /// Overlap results for one search. Sized against CoverSearchRadius rather than left at the
        /// 48 it was when that radius was 18m: OverlapSphereNonAlloc stops at the buffer length and
        /// gives back whatever it found first, which is not distance-sorted, so a buffer too small
        /// for the radius throws away real cover in exactly the cluttered places cover matters.
        /// </summary>
        private static readonly Collider[] ColliderBuffer = new Collider[128];
        private static readonly HashSet<long> SeenCells = new HashSet<long>();
        private static readonly List<Candidate> Candidates = new List<Candidate>();

        /// <summary>One place the search might send a bandit, before any raycast has been spent on it.</summary>
        private struct Candidate
        {
            public Vector3 Position;

            /// <summary>A cover slot, with the facing of the surface it stands against.</summary>
            public bool IsSlot;
            public Vector3 Normal;

            /// <summary>The score as far as it can be known without a raycast; sets the order they are tried in.</summary>
            public float CheapScore;
        }

        private static readonly Comparison<Candidate> ByCheapScore = (a, b) => b.CheapScore.CompareTo(a.CheapScore);

        /// <summary>
        /// The raycast budget for one search. Candidates are tried best-first by their cheap score,
        /// each with a quick early-out visibility test, and only the ones that pass go on to the
        /// full evaluation - peek positions and all. With slots round every object in range there
        /// can be hundreds of candidates, and the old way of fully evaluating every one would cost
        /// tens of thousands of rays. Crysis 2's tactical point system and Mafia III both work in
        /// this order: cheap filters, then a sort, then raycasts from the top down.
        /// </summary>
        private const int QuickCheckBudget = 80;
        private const int FullEvaluationBudget = 12;
        private const int EnoughViable = 5;

        /// <summary>
        /// Looks for a better place to be than where the bot currently is.
        ///
        /// The bot's own position is scored with the same function and used as the bar to beat, so
        /// a bandit already tucked behind a rock doesn't keep sprinting between equally good rocks.
        /// </summary>
        /// <param name="selfPosition">Feet position of the bot.</param>
        /// <param name="threatEye">Eye position of whoever is shooting at it.</param>
        /// <param name="preferHidden">
        /// Set when the bot is hurt: hard cover outscores peekable cover, i.e. it hides properly
        /// instead of looking for a firing position.
        /// </param>
        public static bool TryFindCover(
            Vector3 selfPosition,
            Vector3 threatEye,
            float searchRadius,
            int ringSamples,
            float minimumThreatDistance,
            float preferredThreatDistance,
            bool preferHidden,
            out BanditCoverSpot best)
        {
            return TryFindCover(selfPosition, threatEye, searchRadius, ringSamples, minimumThreatDistance,
                preferredThreatDistance, preferHidden, out best, out _);
        }

        /// <inheritdoc cref="TryFindCover(Vector3,Vector3,float,int,float,float,bool,out BanditCoverSpot)"/>
        public static bool TryFindCover(
            Vector3 selfPosition,
            Vector3 threatEye,
            float searchRadius,
            int ringSamples,
            float minimumThreatDistance,
            float preferredThreatDistance,
            bool preferHidden,
            out BanditCoverSpot best,
            out BanditCoverSearchStats stats)
        {
            return TryFindCover(selfPosition, threatEye, searchRadius, ringSamples, minimumThreatDistance,
                preferredThreatDistance, preferHidden, out best, out stats, null);
        }

        /// <inheritdoc cref="TryFindCover(Vector3,Vector3,float,int,float,float,bool,out BanditCoverSpot)"/>
        /// <param name="reports">
        /// Optional per-candidate verdicts, for /bandit debug cover to draw in the world. Filled only
        /// when non-null, so the search this runs for a live bot allocates nothing extra.
        /// </param>
        public static bool TryFindCover(
            Vector3 selfPosition,
            Vector3 threatEye,
            float searchRadius,
            int ringSamples,
            float minimumThreatDistance,
            float preferredThreatDistance,
            bool preferHidden,
            out BanditCoverSpot best,
            out BanditCoverSearchStats stats,
            List<BanditCoverCandidateReport> reports)
        {
            return TryFindCover(selfPosition, threatEye, searchRadius, ringSamples, minimumThreatDistance,
                preferredThreatDistance, preferHidden, out best, out stats, reports, null, 0f);
        }

        /// <inheritdoc cref="TryFindCover(Vector3,Vector3,float,int,float,float,bool,out BanditCoverSpot)"/>
        /// <param name="occupiedSpots">
        /// Cover other members of the same squad have already committed to. Candidates within
        /// <paramref name="minSeparation"/> of one are rejected outright, which is what stops a
        /// squad stacking onto a single rock: this search is deterministic, so bandits standing
        /// near each other and facing the same threat otherwise all pick the same coordinate.
        /// Null or an empty list restores the plain single-bandit search.
        /// </param>
        /// <param name="minSeparation">Metres of clearance to keep from each occupied spot.</param>
        /// <param name="predictedEye">
        /// Where the threat's eye will be shortly, if it is moving. A spot has to hide the bandit
        /// from there as well as from where the threat is now, so cover is chosen ahead of a
        /// threat walking round it rather than given up after it has. Null, or a point within a
        /// metre of <paramref name="threatEye"/>, when the threat is standing still.
        /// </param>
        public static bool TryFindCover(
            Vector3 selfPosition,
            Vector3 threatEye,
            float searchRadius,
            int ringSamples,
            float minimumThreatDistance,
            float preferredThreatDistance,
            bool preferHidden,
            out BanditCoverSpot best,
            out BanditCoverSearchStats stats,
            List<BanditCoverCandidateReport> reports,
            IList<Vector3> occupiedSpots,
            float minSeparation,
            float maxAdvance = float.PositiveInfinity,
            Vector3? predictedEye = null)
        {
            best = default;
            stats = default;
            reports?.Clear();
            bool found = false;
            bool predicting = predictedEye.HasValue && FlatDistance(predictedEye.Value, threatEye) > 1f;

            // How near the threat a candidate may be: no closer than maxAdvance in from where the
            // bandit stands now. See BanditConfiguration.CoverMaxAdvance.
            float closestAllowed = FlatDistance(selfPosition, threatEye) - maxAdvance;

            // The bar to beat. If the bot is already in cover this scores well and most candidates
            // are rejected; if it is standing in the open this evaluates to nothing and anything
            // valid wins.
            //
            // Held to the separation rule as well, so a bandit whose mate has since claimed the
            // spot it is standing on stops treating that spot as good enough and goes elsewhere -
            // otherwise the first one there would keep everyone else's search from ever winning.
            // And to the prediction: where the bandit stands is no bar to beat if the threat is
            // about to see round it.
            float incumbentScore = float.MinValue;
            bool bestIsIncumbent = false;
            if (!IsTaken(selfPosition, occupiedSpots, minSeparation)
                && TryEvaluate(selfPosition, selfPosition, threatEye, minimumThreatDistance,
                    preferredThreatDistance, preferHidden, out BanditCoverSpot incumbent, ref stats, out _)
                && !(predicting && IsAnyOfBodyVisible(predictedEye.Value, incumbent.Position)))
            {
                incumbentScore = incumbent.Score;
                best = incumbent;
                found = true;
                bestIsIncumbent = true;
            }

            stats.ColliderBufferFull = CollectCandidates(selfPosition, threatEye, searchRadius, ringSamples, ref stats);
            stats.Candidates = Candidates.Count;

            // The cheap pass: everything that can be decided without a raycast, and a provisional
            // score for the rest - the distance and range terms of the real score, which are most
            // of what separates one hiding place from another.
            Ranked.Clear();
            for (int i = 0; i < Candidates.Count; i++)
            {
                Candidate candidate = Candidates[i];
                BanditCoverOutcome? rejected = null;

                // Before the evaluation rather than after: this test is two subtractions against a
                // handful of claims, and the one it skips ends in a dozen raycasts.
                if (IsTaken(candidate.Position, occupiedSpots, minSeparation))
                {
                    stats.RejectedTakenBySquadmate++;
                    rejected = BanditCoverOutcome.TakenBySquadmate;
                }
                else if (FlatDistance(candidate.Position, threatEye) < closestAllowed)
                {
                    stats.RejectedTooCloseToThreat++;
                    rejected = BanditCoverOutcome.TooCloseToThreat;
                }
                else if (candidate.IsSlot && !SlotFacesAway(new CoverSlot { Position = candidate.Position, Normal = candidate.Normal }, threatEye))
                {
                    stats.RejectedFacesThreat++;
                    rejected = BanditCoverOutcome.FacesThreat;
                }
                else if (RunsPastThreat(selfPosition, candidate.Position, threatEye))
                {
                    stats.RejectedTowardThreat++;
                    rejected = BanditCoverOutcome.TowardThreat;
                }

                if (rejected.HasValue)
                {
                    reports?.Add(new BanditCoverCandidateReport { Position = candidate.Position, Outcome = rejected.Value });
                    continue;
                }

                candidate.CheapScore = -FlatDistance(selfPosition, candidate.Position) * 2f
                    - Mathf.Abs(FlatDistance(candidate.Position, threatEye) - preferredThreatDistance) * 0.5f
                    + Mathf.Max(0f, 8f - DistanceFromLine(candidate.Position, selfPosition, threatEye));
                Ranked.Add(candidate);
            }

            Ranked.Sort(ByCheapScore);

            // The expensive pass, best first: a quick early-out silhouette test, from where the
            // threat is and from where it is heading, then the full evaluation on what survives.
            int quickChecks = 0;
            int fullEvaluations = 0;
            int viable = 0;
            for (int i = 0; i < Ranked.Count; i++)
            {
                if (quickChecks >= QuickCheckBudget || fullEvaluations >= FullEvaluationBudget || viable >= EnoughViable)
                {
                    stats.NotEvaluated = Ranked.Count - i;
                    break;
                }

                Vector3 position = Ranked[i].Position;
                quickChecks++;

                if (!TrySnapToGround(position, out Vector3 ground))
                {
                    stats.RejectedNoGround++;
                    reports?.Add(new BanditCoverCandidateReport { Position = position, Outcome = BanditCoverOutcome.NoGround });
                    continue;
                }

                if (IsAnyOfBodyVisible(threatEye, ground))
                {
                    stats.RejectedNotCover++;
                    reports?.Add(new BanditCoverCandidateReport { Position = ground, Outcome = BanditCoverOutcome.StillVisible });
                    continue;
                }

                if (predicting && IsAnyOfBodyVisible(predictedEye.Value, ground))
                {
                    stats.RejectedExposedSoon++;
                    reports?.Add(new BanditCoverCandidateReport { Position = ground, Outcome = BanditCoverOutcome.ExposedSoon });
                    continue;
                }

                fullEvaluations++;
                bool isViable = TryEvaluate(ground, selfPosition, threatEye, minimumThreatDistance,
                    preferredThreatDistance, preferHidden, out BanditCoverSpot spot, ref stats,
                    out BanditCoverOutcome outcome);

                reports?.Add(new BanditCoverCandidateReport
                {
                    Position = isViable ? spot.Position : ground,
                    Outcome = outcome
                });

                if (!isViable)
                {
                    continue;
                }

                stats.Viable++;
                viable++;

                // Margin, not just ">": swapping cover costs a walk across open ground, so it has
                // to be a clear improvement to be worth it.
                if (found && spot.Score <= incumbentScore + 5f)
                {
                    stats.RejectedNotBetter++;
                    continue;
                }

                if (!found || spot.Score > best.Score)
                {
                    best = spot;
                    found = true;
                    bestIsIncumbent = false;
                }
            }

            if (!found)
            {
                return false;
            }

            // Tighten the winner before committing to it. Scoring picks the best of what the
            // candidate generator happened to produce, and "best" is not the same as "actually
            // hidden" - the generator stands the bot a fixed distance off whatever it found, which
            // is a guess at where the shadow of that object is.
            RefineSpot(ref best, selfPosition, threatEye, minimumThreatDistance,
                preferredThreatDistance, preferHidden);

            best.HasMargin = best.RequiresCrouch
                ? IsBodyHidden(threatEye, best.Position, crouched: true, out _)
                : HasMargin(best.Position, threatEye);

            // "The best spot is the one I'm already standing on" is not a move order. A candidate
            // a few tenths away is, though - judging that by distance alone left a bandit standing
            // 0.3m off a spot that hid it, visible, refusing to step onto it.
            return !bestIsIncumbent && (best.Position - selfPosition).sqrMagnitude > 0.01f;
        }

        private static readonly List<Candidate> Ranked = new List<Candidate>();

        /// <summary>
        /// Whether walking straight from the bandit to a spot would take it toward the threat or
        /// across its front - closer to the threat on the way than at either end, by a margin.
        /// The Source engine's cover search never expands a node nearer the threat than it is
        /// itself, S.T.A.L.K.E.R. rejects cover with the enemy "on the way", and Crysis drops any
        /// point the enemy reaches first: cover you have to run past the gun to reach is not cover.
        /// </summary>
        private static bool RunsPastThreat(Vector3 selfPosition, Vector3 spot, Vector3 threatEye)
        {
            float onTheWay = DistanceFromLine(threatEye, selfPosition, spot);
            float ends = Mathf.Min(FlatDistance(selfPosition, threatEye), FlatDistance(spot, threatEye));
            return onTheWay < ends - RunsPastThreatMarginMetres;
        }

        private const float RunsPastThreatMarginMetres = 2f;

        /// <summary>
        /// Whether a crouching body at a spot is hidden from a threat - the early-out form of the
        /// silhouette test, for the brain's frequent checks on the spot it already holds.
        /// </summary>
        public static bool IsHiddenFrom(Vector3 ground, Vector3 threatEye)
        {
            return !IsAnyOfBodyVisible(threatEye, ground);
        }

        /// <summary>
        /// Whether a spot is close enough to one a squadmate has claimed to count as the same
        /// piece of cover. Flat distance, because two bandits either side of a bank are still
        /// standing on top of each other as far as a shooter is concerned.
        /// </summary>
        private static bool IsTaken(Vector3 position, IList<Vector3> occupiedSpots, float minSeparation)
        {
            if (occupiedSpots == null || minSeparation <= 0f)
            {
                return false;
            }

            float thresholdSq = minSeparation * minSeparation;
            for (int i = 0; i < occupiedSpots.Count; i++)
            {
                Vector3 offset = occupiedSpots[i] - position;
                offset.y = 0f;
                if (offset.sqrMagnitude < thresholdSq)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a spot still hides a player from a threat. Cheap enough to re-run every second,
        /// which is what keeps a bot from sitting behind a rock the shooter has since walked around.
        ///
        /// <paramref name="crouched"/> must match the stance the bot is actually holding. Testing
        /// the crouching silhouette for a bot that is standing up is not a conservative
        /// approximation - it is the wrong question, and it passes long after the bot's head and
        /// chest have come into view over whatever is hiding its legs.
        /// </summary>
        public static bool IsCoveredFrom(Vector3 ground, Vector3 threatEye, bool crouched)
        {
            return IsBodyHidden(threatEye, ground, crouched, out _);
        }

        /// <summary>
        /// Whether a bandit in this cover still has a shot at the threat from it: standing up, for
        /// cover that only hides a crouch, or stepping out to its peek position for cover that hides
        /// it standing. The same eye-height ray the search used to decide the spot could peek.
        /// A spot chosen with no firing angle at all - pure hiding, for a hurt bandit - has none to
        /// lose and always answers true.
        /// </summary>
        public static bool CanFireFrom(BanditCoverSpot spot, Vector3 threatEye)
        {
            if (spot.RequiresCrouch)
            {
                return IsVisible(threatEye, spot.Position + Vector3.up * PeekEyeHeight);
            }

            if (spot.CanPeek)
            {
                return IsVisible(threatEye, spot.PeekPosition + Vector3.up * PeekEyeHeight);
            }

            return true;
        }

        /// <summary>
        /// Nudges a chosen spot until the whole body is hidden, or gives up and keeps the best it
        /// managed.
        ///
        /// A spot only has to hide a *crouching* body to be accepted, which is the honest minimum -
        /// but it leaves the bot ducking behind a trunk with its shoulder out the side, which is
        /// what "it thinks it's covered but it ain't" looks like from the other end of the rifle.
        /// Here the spot is walked outwards in a ring of small steps and re-tested standing, taking
        /// the first offset that hides the bot completely on its feet.
        ///
        /// Steps run nearest-first and are tried away from the threat before sideways, so the
        /// correction is the smallest one that works - deeper into the shadow, or shuffled along
        /// the wall - rather than a jump to somewhere unrelated. The umbra behind an object widens
        /// with distance from it, so backing off is a genuine fix and not just a heuristic.
        /// </summary>
        private static void RefineSpot(
            ref BanditCoverSpot spot,
            Vector3 selfPosition,
            Vector3 threatEye,
            float minimumThreatDistance,
            float preferredThreatDistance,
            bool preferHidden)
        {
            // Hidden standing up. What can still be wrong is the margin: a spot that only hides a
            // body standing exactly on it is not one a bandit can use, because it never stands
            // exactly on it - it arrives a few tenths off, sways, steps back from a peek - and the
            // once-a-second check then finds it visible and throws the cover away.
            if (!spot.RequiresCrouch)
            {
                if (!HasMargin(spot.Position, threatEye))
                {
                    TryFindMargin(ref spot, selfPosition, threatEye, minimumThreatDistance,
                        preferredThreatDistance, preferHidden);
                }

                return;
            }

            if (TryStepDeeper(spot.Position, selfPosition, threatEye, RefinementDistances,
                    requirePeek: spot.CanPeek, minimumThreatDistance, preferredThreatDistance,
                    preferHidden, out BanditCoverSpot tightened))
            {
                spot = tightened;
            }
        }

        /// <summary>
        /// Pulls further into cover after being shot while hidden.
        ///
        /// A spot that passed the body test and is still taking rounds means the model is wrong
        /// somewhere it cannot see: the shooter has shifted to an angle the once-a-second
        /// revalidation hasn't caught yet, or the twelve-sample silhouette has a gap the bullet
        /// found - an arm past the shoulder line, or the space between two sample heights. Rather
        /// than reason about which, the answer is the same: increase the margin. Backing away
        /// widens the umbra behind whatever is doing the hiding.
        ///
        /// Tries to keep a firing angle first and settles for pure concealment if it can't - being
        /// shot is the one situation where hiding properly beats being able to shoot back.
        /// </summary>
        public static bool TryPullDeeper(
            BanditCoverSpot current,
            Vector3 threatEye,
            float minimumThreatDistance,
            float preferredThreatDistance,
            out BanditCoverSpot deeper)
        {
            if (TryStepDeeper(current.Position, current.Position, threatEye, PullDeeperDistances,
                    requirePeek: true, minimumThreatDistance, preferredThreatDistance,
                    preferHidden: true, out deeper))
            {
                return true;
            }

            return TryStepDeeper(current.Position, current.Position, threatEye, PullDeeperDistances,
                requirePeek: false, minimumThreatDistance, preferredThreatDistance,
                preferHidden: true, out deeper);
        }

        /// <summary>
        /// Walks outward from a spot in a ring of steps and returns the first offset that hides the
        /// bot completely *standing up*, never moving closer to the threat.
        ///
        /// Ordered nearest-first, and away from the threat before sideways, so the accepted
        /// correction is the smallest one that works - deeper into the shadow, or shuffled along
        /// the cover face - rather than a jump to somewhere unrelated.
        /// </summary>
        private static bool TryStepDeeper(
            Vector3 origin,
            Vector3 selfPosition,
            Vector3 threatEye,
            float[] distances,
            bool requirePeek,
            float minimumThreatDistance,
            float preferredThreatDistance,
            bool preferHidden,
            out BanditCoverSpot result)
        {
            result = default;

            Vector3 toThreat = Flatten(threatEye - origin);
            if (toThreat.sqrMagnitude < 0.0001f)
            {
                return false;
            }
            float originThreatDistance = toThreat.magnitude;
            toThreat.Normalize();

            Vector3 away = -toThreat;
            Vector3 lateral = Vector3.Cross(Vector3.up, toThreat);

            Vector3[] directions =
            {
                away,
                (away + lateral).normalized,
                (away - lateral).normalized,
                lateral,
                -lateral
            };

            BanditCoverSearchStats scratch = default;

            foreach (float distance in distances)
            {
                foreach (Vector3 direction in directions)
                {
                    Vector3 probe = origin + direction * distance;

                    if (FlatDistance(probe, threatEye) < originThreatDistance - 0.05f)
                    {
                        continue; // never "hide" by walking towards the gun
                    }

                    if (!TryEvaluate(probe, selfPosition, threatEye, minimumThreatDistance,
                            preferredThreatDistance, preferHidden, out BanditCoverSpot candidate,
                            ref scratch, out _))
                    {
                        continue;
                    }

                    // Standing exposure is the thing being fixed; a spot that only hides a crouch
                    // is no improvement on the one already being stood in.
                    if (candidate.RequiresCrouch)
                    {
                        continue;
                    }

                    if (requirePeek && !candidate.CanPeek)
                    {
                        continue;
                    }

                    result = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Returns true when the collider buffer filled, i.e. results were dropped.</summary>
        private static bool CollectCandidates(Vector3 selfPosition, Vector3 threatEye, float searchRadius, int ringSamples,
            ref BanditCoverSearchStats stats)
        {
            Candidates.Clear();
            SeenCells.Clear();

            int colliderCount = Physics.OverlapSphereNonAlloc(selfPosition, searchRadius, ColliderBuffer,
                RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore);
            bool bufferFull = colliderCount >= ColliderBuffer.Length;

            for (int i = 0; i < colliderCount; i++)
            {
                Collider collider = ColliderBuffer[i];
                if (collider == null)
                {
                    continue;
                }

                Bounds bounds = collider.bounds;
                if (bounds.size.y < MinCoverColliderHeight)
                {
                    continue; // too low to hide behind even crouched
                }
                if (bounds.size.x > MaxCoverColliderSize || bounds.size.z > MaxCoverColliderSize)
                {
                    continue; // terrain or a whole building shell - its "far side" is meaningless
                }

                // Probe for the surface the threat can actually see, at chest height, and stand
                // just behind THAT.
                //
                // The obvious version - bounds.center plus the bounding box's half-extent - is
                // what made this whole thing fail on the case it most obviously should handle. A
                // tree's bounds are its canopy, three to five metres across, so the spot landed
                // that far behind a trunk 0.3m wide, where the trunk stops occluding anything.
                // Every tree then failed the "still visible crouched" test and was thrown away.
                bool barricade = IsBarricade(collider);
                if (barricade)
                {
                    stats.Barricades++;
                }

                // Every slot round the object, whichever side the threat is on - which side hides
                // is the search's question, answered per slot. See BanditCoverSlots.
                foreach (CoverSlot slot in SlotsOf(collider))
                {
                    if (FlatDistance(slot.Position, selfPosition) <= searchRadius)
                    {
                        Candidates.Add(new Candidate { Position = slot.Position, IsSlot = true, Normal = slot.Normal });
                        stats.Slots++;
                    }
                }

                Vector3 aimPoint = new Vector3(
                    bounds.center.x,
                    Mathf.Min(bounds.min.y + 1f, bounds.max.y - 0.05f),
                    bounds.center.z);

                Vector3 toLandmark = aimPoint - threatEye;
                float landmarkDistance = toLandmark.magnitude;
                if (landmarkDistance < 0.05f)
                {
                    continue;
                }
                Vector3 lineOfFire = toLandmark / landmarkDistance;

                // A hit on some *other* collider first is fine, even preferable - it is a real
                // occluder on that bearing, which is all cover is.
                if (!Physics.Raycast(threatEye, lineOfFire, out RaycastHit occluder, landmarkDistance + 2f,
                        RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
                {
                    if (barricade)
                    {
                        stats.BarricadeProbeMissed++;
                    }

                    continue; // nothing solid between the threat and this landmark
                }

                Vector3 behind = Flatten(lineOfFire);
                if (behind.sqrMagnitude < 0.0001f)
                {
                    continue;
                }
                behind.Normalize();

                // Just behind the face the threat sees - but "just behind" has to be measured
                // against how deep the thing is. A fixed step back puts the spot on top of anything
                // deeper than a metre: a sandbag is an arc nearly two metres front to back, and a
                // bandit standing on it is the most visible thing on the field. So step further
                // back until the ground under the spot is ground, not the cover itself.
                //
                // And once it has had to step over cover, further still, to the first spot that
                // fits a body and hides it. A wall seen end-on is the case: past the near end the
                // steps find pockets of ground between the sandbags too tight to stand in, then
                // the tapered far end, which hides a head but not shoulders - and the shadow that
                // does hide a body starts a few metres beyond it. Stopping at the first ground
                // offered only those, and the wall was thrown away from the one angle a bandit
                // most needs to move round it. Cover that never had to be stepped over keeps the
                // first spot, so a pole costs no more than it did; the faces of an object the probe
                // does not point at come from its slots.
                Vector3? fallback = null;
                bool steppedOverCover = false;
                for (int step = 0; step < CoverDepthSteps; step++)
                {
                    Vector3 spot = occluder.point + behind * (PlayerStance.RADIUS + 0.5f + step * CoverDepthStepMetres);
                    if (IsOnTopOfCover(spot))
                    {
                        steppedOverCover = true;
                        continue;
                    }

                    if (!steppedOverCover)
                    {
                        fallback = spot;
                        break;
                    }

                    fallback = fallback ?? spot;
                    if (TrySnapToGround(spot, out Vector3 ground) && HasStandingRoom(ground)
                        && !IsAnyOfBodyVisible(threatEye, ground))
                    {
                        fallback = spot;
                        break;
                    }
                }

                if (fallback.HasValue)
                {
                    AddCandidate(fallback.Value, occluder.point.y);
                }
                else if (barricade)
                {
                    stats.BarricadeNoGroundBehind++;
                }
            }

            for (int i = 0; i < ringSamples; i++)
            {
                float angle = 360f / ringSamples * i;
                Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                AddCandidate(selfPosition + direction * (searchRadius * 0.45f), selfPosition.y);
                AddCandidate(selfPosition + direction * (searchRadius * 0.9f), selfPosition.y);
            }

            return bufferFull;
        }

        private static void AddCandidate(Vector3 point, float height)
        {
            point.y = height;

            long cell = ((long)Mathf.RoundToInt(point.x / DedupeGridSize) << 32)
                ^ (uint)Mathf.RoundToInt(point.z / DedupeGridSize);
            if (!SeenCells.Add(cell))
            {
                return;
            }

            Candidates.Add(new Candidate { Position = point });
        }

        private static bool TryEvaluate(
            Vector3 candidate,
            Vector3 selfPosition,
            Vector3 threatEye,
            float minimumThreatDistance,
            float preferredThreatDistance,
            bool preferHidden,
            out BanditCoverSpot spot,
            ref BanditCoverSearchStats stats,
            out BanditCoverOutcome outcome)
        {
            spot = default;

            if (!TrySnapToGround(candidate, out Vector3 ground))
            {
                stats.RejectedNoGround++;
                outcome = BanditCoverOutcome.NoGround;
                return false;
            }

            float threatDistance = FlatDistance(ground, threatEye);
            if (threatDistance < minimumThreatDistance)
            {
                stats.RejectedTooCloseToThreat++;
                outcome = BanditCoverOutcome.TooCloseToThreat;
                return false; // don't "take cover" by walking into the target's lap
            }

            if (!HasStandingRoom(ground))
            {
                stats.RejectedNoStandingRoom++;
                outcome = BanditCoverOutcome.NoStandingRoom;
                return false;
            }

            // Any part of a crouching body still visible means this isn't cover at all.
            if (!IsBodyHidden(threatEye, ground, crouched: true, out _))
            {
                stats.RejectedNotCover++;
                outcome = BanditCoverOutcome.StillVisible;
                return false;
            }

            outcome = BanditCoverOutcome.Viable;

            bool exposedWhenStanding = !IsBodyHidden(threatEye, ground, crouched: false, out _);

            Vector3 toThreat = Flatten(threatEye - ground).normalized;
            Vector3 lateral = Vector3.Cross(Vector3.up, toThreat);
            bool canPeek = false;
            Vector3 peekPosition = ground;
            int offsets = exposedWhenStanding ? 1 : 1 + WidePeekOffsets.Length;
            for (int o = 0; o < offsets && !canPeek; o++)
            {
                float offset = o == 0 ? PeekOffset : WidePeekOffsets[o - 1];
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Vector3 peek = ground + lateral * (offset * sign);
                    if (!TrySnapToGround(peek, out Vector3 peekGround) || !HasStandingRoom(peekGround))
                    {
                        continue;
                    }
                    if (IsVisible(threatEye, peekGround + Vector3.up * PeekEyeHeight))
                    {
                        canPeek = true;
                        peekPosition = peekGround;
                        break;
                    }
                }
            }

            float score;
            if (preferHidden)
            {
                // Hurt: being unshootable beats being able to shoot back.
                score = exposedWhenStanding ? 30f : 80f;
            }
            else
            {
                // Duck-and-pop cover is the prize - safe crouched, lethal standing, no walking.
                score = exposedWhenStanding ? 60f : 30f;
                if (canPeek)
                {
                    score += 25f;
                }
            }

            score -= FlatDistance(selfPosition, ground) * 2f;
            score -= Mathf.Abs(threatDistance - preferredThreatDistance) * 0.5f;

            // Prefer the thing you are already half behind - the tree between you and the shooter -
            // over an equally good rock off to the flank. Measured against the line from the bot to
            // the threat, so it rewards being on that line without rewarding closing the distance
            // (the two terms above already push back on that).
            score += Mathf.Max(0f, 8f - DistanceFromLine(ground, selfPosition, threatEye));

            spot = new BanditCoverSpot
            {
                Position = ground,
                RequiresCrouch = exposedWhenStanding,
                CanPeek = canPeek,
                PeekPosition = peekPosition,
                Score = score
            };
            return true;
        }

        private static bool TrySnapToGround(Vector3 candidate, out Vector3 ground)
        {
            // Start above the candidate so a point generated inside a slope still finds the
            // surface, and include GROUND2 which BLOCK_COLLISION leaves out.
            Vector3 origin = candidate + Vector3.up * 3f;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 12f,
                    RayMasks.BLOCK_COLLISION | RayMasks.GROUND2, QueryTriggerInteraction.Ignore))
            {
                ground = hit.point;

                // The top of a barricade is not somewhere to take cover - it is the cover, and
                // standing on it puts the bandit above it in full view.
                return !IsBarricade(hit.collider);
            }

            ground = candidate;
            return false;
        }

        /// <summary>How far either side of a standing spot the body must also be hidden.</summary>
        private const float CoverMarginMetres = 0.35f;

        /// <summary>Whether a standing body is hidden at a spot and a hand's breadth either side of it.</summary>
        private static bool HasMargin(Vector3 ground, Vector3 threatEye)
        {
            Vector3 toThreat = Flatten(threatEye - ground);
            Vector3 lateral = toThreat.sqrMagnitude > 0.0001f
                ? Vector3.Cross(Vector3.up, toThreat.normalized) * CoverMarginMetres
                : Vector3.right * CoverMarginMetres;

            return IsBodyHidden(threatEye, ground + lateral, crouched: false, out _)
                && IsBodyHidden(threatEye, ground - lateral, crouched: false, out _);
        }

        /// <summary>
        /// Moves a standing spot to the nearest one around it that keeps its margin - deeper into
        /// the shadow first, then along the cover - without giving up a peek it had. Leaves it
        /// where it was if nothing nearby does better.
        /// </summary>
        private static void TryFindMargin(ref BanditCoverSpot spot, Vector3 selfPosition, Vector3 threatEye,
            float minimumThreatDistance, float preferredThreatDistance, bool preferHidden)
        {
            Vector3 toThreat = Flatten(threatEye - spot.Position);
            if (toThreat.sqrMagnitude < 0.0001f)
            {
                return;
            }

            toThreat.Normalize();
            Vector3 away = -toThreat;
            Vector3 lateral = Vector3.Cross(Vector3.up, toThreat);
            Vector3[] directions = { away, (away + lateral).normalized, (away - lateral).normalized, lateral, -lateral };

            BanditCoverSearchStats scratch = default;
            foreach (float distance in RefinementDistances)
            {
                foreach (Vector3 direction in directions)
                {
                    if (!TryEvaluate(spot.Position + direction * distance, selfPosition, threatEye,
                            minimumThreatDistance, preferredThreatDistance, preferHidden,
                            out BanditCoverSpot candidate, ref scratch, out _))
                    {
                        continue;
                    }

                    if (candidate.RequiresCrouch || (spot.CanPeek && !candidate.CanPeek))
                    {
                        continue;
                    }

                    if (HasMargin(candidate.Position, threatEye))
                    {
                        spot = candidate;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// How many times, and how far each time, a candidate is stepped back off the top of a
        /// deep piece of cover. Enough to get behind something ten metres deep - which a wall is,
        /// seen end-on: the probe hits the near end, and the only shadow is past the far one.
        /// Three metres was enough for a sandbag seen from the front and left a 3-wide wall seen
        /// along its length with no candidate at all. Stepping stops at the first patch of real
        /// ground, so thin cover costs no more than it did.
        /// </summary>
        private const int CoverDepthSteps = 20;
        private const float CoverDepthStepMetres = 0.5f;

        /// <summary>Whether the ground under a point is the top of a barricade rather than ground.</summary>
        private static bool IsOnTopOfCover(Vector3 point)
        {
            return Physics.Raycast(point + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 12f,
                    RayMasks.BLOCK_COLLISION | RayMasks.GROUND2, QueryTriggerInteraction.Ignore)
                && IsBarricade(hit.collider);
        }

        /// <summary>Whether a collider belongs to a placed barricade - its root carries the tag.</summary>
        private static bool IsBarricade(Collider collider)
        {
            for (Transform t = collider != null ? collider.transform : null; t != null; t = t.parent)
            {
                if (t.CompareTag("Barricade"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a player-sized capsule fits at a spot.
        ///
        /// The bottom sphere is lifted clear of the floor rather than sat on it. A sphere of
        /// radius r centred exactly r above the ground is *tangent* to it, and tangency reads as
        /// an overlap often enough that a ground-hugging capsule reports "no room" on flat open
        /// terrain - which rejects every candidate everywhere and makes cover look broken rather
        /// than picky. The lift is well under the CharacterController's step offset, so nothing
        /// the bot could not walk onto is let through.
        /// </summary>
        private static bool HasStandingRoom(Vector3 ground)
        {
            float radius = PlayerStance.RADIUS * 0.9f;
            return !Physics.CheckCapsule(
                ground + Vector3.up * (radius + GroundClearance),
                ground + Vector3.up * (PlayerMovement.HEIGHT_STAND - radius),
                radius,
                RayMasks.BLOCK_COLLISION,
                QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Whether any part of a player standing (or crouching) at <paramref name="ground"/> can be
        /// seen from <paramref name="threatEye"/>.
        ///
        /// This is the difference between cover that works and cover that only looks like it does.
        /// Testing a single eye-height point says "hidden" for a spot where the shoulders, chest
        /// and knees are all hanging out in the open - the bot ducks behind a trunk narrower than
        /// it is, is quite happy, and gets shot in the arm. So the silhouette is sampled instead:
        /// four heights down the body, each at the centre and both shoulders, spread perpendicular
        /// to the threat because that is the width a shooter actually sees.
        ///
        /// Every sample must be blocked. <paramref name="exposedSamples"/> reports how many were
        /// not, which is what the refinement pass uses to tell "nearly hidden" from "in the open".
        /// </summary>
        private static bool IsBodyHidden(Vector3 threatEye, Vector3 ground, bool crouched, out int exposedSamples)
        {
            exposedSamples = 0;

            Vector3 toThreat = Flatten(threatEye - ground);
            Vector3 lateral = toThreat.sqrMagnitude > 0.0001f
                ? Vector3.Cross(Vector3.up, toThreat.normalized) * ShoulderHalfWidth
                : Vector3.right * ShoulderHalfWidth;

            float[] heights = crouched ? CrouchSampleHeights : StandingSampleHeights;

            foreach (float height in heights)
            {
                Vector3 centre = ground + Vector3.up * height;
                if (IsVisible(threatEye, centre)
                    || IsVisible(threatEye, centre + lateral)
                    || IsVisible(threatEye, centre - lateral))
                {
                    exposedSamples++;
                }
            }

            return exposedSamples == 0;
        }

        /// <summary>
        /// <see cref="IsBodyHidden"/> for a crouching body, inverted, stopping at the first sample
        /// that shows - so a spot in the open costs one ray rather than twelve. For the candidate
        /// generator, which may try many spots per piece of cover.
        /// </summary>
        private static bool IsAnyOfBodyVisible(Vector3 threatEye, Vector3 ground)
        {
            Vector3 toThreat = Flatten(threatEye - ground);
            Vector3 lateral = toThreat.sqrMagnitude > 0.0001f
                ? Vector3.Cross(Vector3.up, toThreat.normalized) * ShoulderHalfWidth
                : Vector3.right * ShoulderHalfWidth;

            foreach (float height in CrouchSampleHeights)
            {
                Vector3 centre = ground + Vector3.up * height;
                if (IsVisible(threatEye, centre)
                    || IsVisible(threatEye, centre + lateral)
                    || IsVisible(threatEye, centre - lateral))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// One ray, BLOCK_SENTRY - the same mask BanditBotController uses for its own line of
        /// sight, so "cover" here means the same thing as "can't shoot me" there.
        /// </summary>
        private static bool IsVisible(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance < 0.05f)
            {
                return true;
            }

            return !Physics.Raycast(new Ray(from, delta / distance), distance - 0.025f,
                RayMasks.BLOCK_SENTRY, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Whether a body fits at a point and is hidden there from a threat - the ground, room and
        /// silhouette tests a candidate has to pass, without the scoring. For tests that sweep an
        /// area to learn whether any hiding spot exists, independently of what the candidate
        /// generator happened to offer.
        /// </summary>
        public static bool IsHidingSpot(Vector3 point, Vector3 threatEye, bool crouched, out Vector3 ground)
        {
            return TrySnapToGround(point, out ground)
                && HasStandingRoom(ground)
                && IsBodyHidden(threatEye, ground, crouched, out _);
        }

        /// <summary>Distance from a point to the segment between two others, on the XZ plane.</summary>
        private static float DistanceFromLine(Vector3 point, Vector3 lineStart, Vector3 lineEnd)
        {
            Vector3 p = Flatten(point);
            Vector3 a = Flatten(lineStart);
            Vector3 b = Flatten(lineEnd);

            Vector3 ab = b - a;
            float lengthSq = ab.sqrMagnitude;
            if (lengthSq < 0.0001f)
            {
                return (p - a).magnitude;
            }

            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / lengthSq);
            return (p - (a + ab * t)).magnitude;
        }
    }
}
