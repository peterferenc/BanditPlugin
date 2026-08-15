using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

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
    }

    /// <summary>What happened to one candidate spot during a search.</summary>
    public enum BanditCoverOutcome
    {
        Chosen,
        Viable,
        NoGround,
        TooCloseToThreat,
        NoStandingRoom,
        StillVisible
    }

    /// <summary>One candidate and its verdict, for /banditcover to draw and log.</summary>
    public struct BanditCoverCandidateReport
    {
        public Vector3 Position;
        public BanditCoverOutcome Outcome;
    }

    /// <summary>
    /// Why a cover search came out the way it did. A failed search is otherwise invisible - the
    /// bot just stands there and nothing is logged, because nothing went wrong - so /banditcover
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
        public int Viable;

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
                   $"{Viable} viable"
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
    public static class BanditCoverFinder
    {
        /// <summary>Eye height used for the standing visibility test. Roughly PlayerLook.aim.</summary>
        private const float StandingEyeHeight = 1.65f;

        /// <summary>
        /// Eye height used for the peek test. Unlike the body samples this really is one point:
        /// peeking is about whether the bot's *aim origin* can see the target, and that is a
        /// single place - the same one its bullets leave from.
        /// </summary>
        private const float PeekEyeHeight = StandingEyeHeight;

        /// <summary>
        /// Half the shoulder width. The body samples are spread this far either side of centre,
        /// perpendicular to the threat, because that is the silhouette a shooter actually sees.
        /// </summary>
        private const float ShoulderHalfWidth = 0.22f;

        /// <summary>Heights sampled down the body when standing: eye, chest, waist, knees.</summary>
        private static readonly float[] StandingSampleHeights = { 1.65f, 1.35f, 1.0f, 0.55f };

        /// <summary>The same, crouched - the whole body is inside 1.2m (PlayerMovement.HEIGHT_CROUCH).</summary>
        private static readonly float[] CrouchSampleHeights = { 0.95f, 0.8f, 0.6f, 0.3f };

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
        private static readonly List<Vector3> Candidates = new List<Vector3>();

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
        /// Optional per-candidate verdicts, for /banditcover to draw in the world. Filled only
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
            best = default;
            stats = default;
            reports?.Clear();
            bool found = false;

            // The bar to beat. If the bot is already in cover this scores well and most candidates
            // are rejected; if it is standing in the open this evaluates to nothing and anything
            // valid wins.
            float incumbentScore = float.MinValue;
            if (TryEvaluate(selfPosition, selfPosition, threatEye, minimumThreatDistance,
                    preferredThreatDistance, preferHidden, out BanditCoverSpot incumbent, ref stats, out _))
            {
                incumbentScore = incumbent.Score;
                best = incumbent;
                found = true;
            }

            stats.ColliderBufferFull = CollectCandidates(selfPosition, threatEye, searchRadius, ringSamples);
            stats.Candidates = Candidates.Count;

            for (int i = 0; i < Candidates.Count; i++)
            {
                bool viable = TryEvaluate(Candidates[i], selfPosition, threatEye, minimumThreatDistance,
                    preferredThreatDistance, preferHidden, out BanditCoverSpot spot, ref stats,
                    out BanditCoverOutcome outcome);

                reports?.Add(new BanditCoverCandidateReport
                {
                    Position = viable ? spot.Position : Candidates[i],
                    Outcome = outcome
                });

                if (!viable)
                {
                    continue;
                }

                stats.Viable++;

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
                }
            }

            if (!found)
            {
                return false;
            }

            // Tighten the winner before committing to it. Scoring picks the best of what the
            // candidate generator happened to produce, and "best" is not the same as "actually
            // hidden" - the generator stands the bot a fixed distance behind whatever the probe
            // ray hit, which is a guess at where the shadow of that object is.
            RefineSpot(ref best, selfPosition, threatEye, minimumThreatDistance,
                preferredThreatDistance, preferHidden);

            // "The best spot is the one I'm already standing on" is not a move order.
            return (best.Position - selfPosition).sqrMagnitude > 1f;
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
            // Already hidden standing up - nothing to fix, and the peek cycle handles the rest.
            if (!spot.RequiresCrouch)
            {
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
        private static bool CollectCandidates(Vector3 selfPosition, Vector3 threatEye, float searchRadius, int ringSamples)
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
                    continue; // nothing solid between the threat and this landmark
                }

                Vector3 behind = Flatten(lineOfFire);
                if (behind.sqrMagnitude < 0.0001f)
                {
                    continue;
                }
                behind.Normalize();

                AddCandidate(occluder.point + behind * (PlayerStance.RADIUS + 0.5f), occluder.point.y);
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

            Candidates.Add(point);
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
            for (int sign = -1; sign <= 1; sign += 2)
            {
                Vector3 peek = ground + lateral * (PeekOffset * sign);
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
                return true;
            }

            ground = candidate;
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

        private static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return (a - b).magnitude;
        }
    }
}
