using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.BanditGeometry;

namespace BanditPlugin.Navigation
{
    /// <summary>
    /// Cover slots: the places round an object a body can stand pressed against it, worked out once
    /// per object and kept.
    ///
    /// Every shooter whose AI could be read does cover this way rather than searching the world
    /// afresh on each request - S.T.A.L.K.E.R. stores cover per nav vertex, Mafia III keeps 1m
    /// slots round every piece of cover, Reforger's cover points come with a facing. The search
    /// this replaces aimed one probe ray at each object from the threat and stood the bandit
    /// behind wherever it hit, so each object offered exactly one spot, on the line from the
    /// threat. Walk round a wall and the spot that would hide you - the other face, two metres
    /// away, or the inside of a sandbag's curve - was never a candidate at all.
    ///
    /// Slots are found by casting in at the object from all round it at chest height and standing
    /// off each surface the rays meet. They do not depend on where the threat is, which is what
    /// lets them be cached: which slot hides from a given threat is decided per query, against
    /// the slot's facing first and then the same silhouette test as every other candidate.
    /// </summary>
    public static partial class BanditCoverFinder
    {
        /// <summary>A place to stand against an object, and which way its surface faces.</summary>
        internal struct CoverSlot
        {
            /// <summary>The ground under the standing spot.</summary>
            public Vector3 Position;

            /// <summary>
            /// Flat, unit length, pointing away from the surface the slot stands against. A threat
            /// out in this direction is in front of the surface and sees the slot; one behind it
            /// is on the far side of the object.
            /// </summary>
            public Vector3 Normal;
        }

        private sealed class SlotEntry
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public CoverSlot[] Slots;
            public float LastUsed;
        }

        /// <summary>Roughly the spacing of the rays cast in round an object.</summary>
        private const float SlotSpacingMetres = 1f;
        private const int MinSlotRays = 8;
        private const int MaxSlotRays = 32;

        /// <summary>How far off the surface a slot stands: a body's radius and a little air.</summary>
        private const float SlotStandOff = 0.2f;

        /// <summary>How far a slot's ground may be above or below the base of its object.</summary>
        private const float SlotMaxDropMetres = 1.5f;

        /// <summary>Slots closer together than this are the same slot.</summary>
        private const float SlotDedupeMetres = 0.7f;

        /// <summary>Entries not used for this long are forgotten, so the cache follows the fighting.</summary>
        private const float SlotCacheForgetSeconds = 300f;
        private const int SlotCacheSweepSize = 2048;

        private static readonly Dictionary<int, SlotEntry> SlotCache = new Dictionary<int, SlotEntry>();
        private static readonly List<int> SlotCacheStale = new List<int>();
        private static readonly CoverSlot[] NoSlots = new CoverSlot[0];
        private static readonly List<CoverSlot> SlotScratch = new List<CoverSlot>();

        /// <summary>
        /// The slots round a collider, from the cache when it has not moved since they were made.
        /// Colliders that are not cover - a player, a bandit - get an empty list, cached the same
        /// way, so the check that rules them out runs once and not on every search.
        /// </summary>
        internal static CoverSlot[] SlotsOf(Collider collider)
        {
            int key = collider.GetInstanceID();
            Transform transform = collider.transform;

            if (SlotCache.TryGetValue(key, out SlotEntry entry)
                && (entry.Position - transform.position).sqrMagnitude < 0.01f
                && Quaternion.Angle(entry.Rotation, transform.rotation) < 1f)
            {
                entry.LastUsed = Time.time;
                return entry.Slots;
            }

            if (SlotCache.Count >= SlotCacheSweepSize)
            {
                ForgetStaleSlots();
            }

            entry = new SlotEntry
            {
                Position = transform.position,
                Rotation = transform.rotation,
                Slots = IsBody(collider) ? NoSlots : GenerateSlots(collider),
                LastUsed = Time.time
            };
            SlotCache[key] = entry;
            return entry.Slots;
        }

        /// <summary>How many objects have slots cached, for /bandit debug status.</summary>
        public static int CachedSlotObjects => SlotCache.Count;

        private static void ForgetStaleSlots()
        {
            SlotCacheStale.Clear();
            foreach (KeyValuePair<int, SlotEntry> pair in SlotCache)
            {
                if (Time.time - pair.Value.LastUsed > SlotCacheForgetSeconds)
                {
                    SlotCacheStale.Add(pair.Key);
                }
            }

            foreach (int key in SlotCacheStale)
            {
                SlotCache.Remove(key);
            }

            // Everything still in use and the cache still full: start again rather than grow
            // without bound. The next searches rebuild what they need.
            if (SlotCache.Count >= SlotCacheSweepSize)
            {
                SlotCache.Clear();
            }
        }

        /// <summary>A player or a bandit is not something to take cover behind - it walks off.</summary>
        private static bool IsBody(Collider collider)
        {
            return collider.GetComponentInParent<Player>() != null;
        }

        private static CoverSlot[] GenerateSlots(Collider collider)
        {
            Bounds bounds = collider.bounds;
            float extent = Mathf.Max(bounds.extents.x, bounds.extents.z);
            float radius = extent + 1.5f;
            float chest = bounds.min.y + Mathf.Min(1f, bounds.size.y * 0.5f);
            Vector3 centre = new Vector3(bounds.center.x, chest, bounds.center.z);

            int rays = Mathf.Clamp(Mathf.RoundToInt(2f * Mathf.PI * radius / SlotSpacingMetres), MinSlotRays, MaxSlotRays);
            SlotScratch.Clear();

            for (int i = 0; i < rays; i++)
            {
                Vector3 outward = Quaternion.Euler(0f, 360f / rays * i, 0f) * Vector3.forward;
                Vector3 origin = centre + outward * radius;

                // This collider only: the slot is about this object's surface. A neighbouring
                // piece in the way is not missed - its own slots cover it - and a slot that lands
                // inside a neighbour fails the standing-room test below.
                if (!collider.Raycast(new Ray(origin, -outward), out RaycastHit hit, radius + extent))
                {
                    continue;
                }

                Vector3 normal = Flatten(hit.normal);
                normal = normal.sqrMagnitude > 0.01f ? normal.normalized : outward;

                Vector3 standing = hit.point + normal * (PlayerStance.RADIUS + SlotStandOff);
                if (!TrySnapToGround(standing, out Vector3 ground) || !HasStandingRoom(ground))
                {
                    continue;
                }

                // The ground found has to be the ground the object stands on. Snapping down from
                // a point inside a bank or under the edge of a road can come out metres below -
                // the first slot to do it was 6.4m under a sandbag wall, out of anyone's sight
                // and anyone's reach, and passed as perfect standing cover. S.T.A.L.K.E.R.
                // throws out cover more than 3m off in height for the same reason.
                if (Mathf.Abs(ground.y - bounds.min.y) > SlotMaxDropMetres)
                {
                    continue;
                }

                bool duplicate = false;
                foreach (CoverSlot existing in SlotScratch)
                {
                    if (FlatDistance(existing.Position, ground) < SlotDedupeMetres)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    SlotScratch.Add(new CoverSlot { Position = ground, Normal = normal });
                }
            }

            return SlotScratch.Count > 0 ? SlotScratch.ToArray() : NoSlots;
        }

        /// <summary>
        /// Whether a slot's surface stands between it and a threat - the cheap test, before any
        /// raycast. Generous on purpose: it only has to throw out slots that face the threat
        /// outright, and the silhouette test decides the rest. Reforger takes cover valid up to 60
        /// degrees off its facing; this allows nearly 80, since a slot at a sandbag's end faces
        /// along the wall and still hides a body from a threat well round to the side.
        /// </summary>
        private static bool SlotFacesAway(CoverSlot slot, Vector3 threatEye)
        {
            Vector3 toThreat = Flatten(threatEye - slot.Position);
            return toThreat.sqrMagnitude < 0.0001f || Vector3.Dot(-slot.Normal, toThreat.normalized) > 0.2f;
        }
    }
}
