using Rocket.Unturned.Player;
using Rocket.API;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin
{
    /// <summary>
    /// Where a spawn command puts things: down the caller's sightline at some distance, or at their
    /// map marker.
    ///
    /// Lifted out of "/squadspawn" so "/banditevent" can take the same words and mean the same
    /// thing by them. Both commands are asked to place something relative to a player, and a second
    /// copy of this logic is a second thing to keep in step - the marker's meaningless height, the
    /// aim transform rather than the body transform, the ground probe reaching far enough for a
    /// point two hundred metres away. All of those are decisions that took finding out once.
    /// </summary>
    public static class BanditPlacement
    {
        /// <summary>
        /// How high above a point the ground probe starts, and how far down it looks. Generous in
        /// both directions because a point 200m away is worked out from a flat bearing and can
        /// easily land well above or below the caller's own height.
        /// </summary>
        private const float GroundProbeHeight = 300f;
        private const float GroundProbeDepth = 1200f;

        /// <summary>Nearest anything may be placed, so "&lt;command&gt; 0" cannot drop one on your head.</summary>
        public const float MinimumSpawnDistance = 15f;

        /// <summary>
        /// A resolved placement: where the thing goes, and which way it faces.
        /// </summary>
        public struct Result
        {
            /// <summary>Where the caller was standing when they asked.</summary>
            public Vector3 Origin;

            /// <summary>The ground-snapped centre of whatever is being placed.</summary>
            public Vector3 Centre;

            /// <summary>
            /// The flat bearing from the caller to the centre. Formations are laid out across it and
            /// set back along it; see <see cref="FormationSlot"/>.
            /// </summary>
            public Vector3 Forward;

            /// <summary>The flat bearing at right angles to it, for laying a line out sideways.</summary>
            public Vector3 Right;

            /// <summary>
            /// Compass heading, in degrees, for a spawned thing to face the caller. They are the
            /// reason it is here, and it saves a bandit spending its first second turning round.
            /// </summary>
            public float Facing;

            /// <summary>Whether the caller asked for their map marker rather than a distance.</summary>
            public bool UsedMarker;

            /// <summary>How far the centre ended up from the caller, for the reply.</summary>
            public float Range => Vector3.Distance(Origin, Centre);
        }

        /// <summary>Whether a word means "at my map marker" rather than a distance.</summary>
        public static bool IsMarkerRequest(string argument)
        {
            return argument.Equals("marker", System.StringComparison.OrdinalIgnoreCase)
                || argument.Equals("map", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Works out where a command's subject goes.
        /// </summary>
        /// <param name="placementArgument">
        /// The word the caller gave as a placement - a distance in metres or "marker" - or null for
        /// the caller's own default distance.
        /// </param>
        /// <param name="defaultDistance">Where to put it down the sightline when no distance was given.</param>
        /// <param name="error">Why it could not be placed, or null on success.</param>
        public static bool TryResolve(IRocketPlayer caller, string placementArgument, float defaultDistance,
            out Result result, out string error)
        {
            result = default;
            error = null;

            Player callerPlayer = ((UnturnedPlayer)caller).Player;
            Vector3 origin = callerPlayer.transform.position;

            bool useMarker = !string.IsNullOrEmpty(placementArgument) && IsMarkerRequest(placementArgument);

            float distance = defaultDistance;
            if (!useMarker && !string.IsNullOrEmpty(placementArgument))
            {
                if (!float.TryParse(placementArgument, out distance))
                {
                    error = $"'{placementArgument}' is neither a distance in metres nor 'marker'.";
                    return false;
                }

                distance = Mathf.Max(distance, MinimumSpawnDistance);
            }

            // Taken from the aim transform, which is where the player is actually looking, and not
            // from the body transform - that only carries the yaw of the last input packet the
            // server processed for them, so anything placed off it lands wherever the model happens
            // to be facing rather than down the caller's sightline.
            //
            // Flattened, because formations are laid out on the ground; a caller looking at their
            // boots would otherwise squash one into a point, hence the fallback.
            Vector3 forward = callerPlayer.look != null && callerPlayer.look.aim != null
                ? callerPlayer.look.aim.forward
                : callerPlayer.transform.forward;

            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = callerPlayer.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.0001f)
                {
                    forward = Vector3.forward;
                }
            }
            forward.Normalize();

            Vector3 centre;
            if (useMarker)
            {
                if (callerPlayer.quests == null || !callerPlayer.quests.isMarkerPlaced)
                {
                    error = "No map marker placed - put one down first, or give a distance in metres.";
                    return false;
                }

                centre = callerPlayer.quests.markerPosition;

                // A marker is a point on a flat map: its height is whatever the client happened to
                // send and means nothing on the ground. The bearing is recomputed from it too, so
                // what spawns still faces the way you will be coming from.
                Vector3 fromMarker = origin - centre;
                fromMarker.y = 0f;
                if (fromMarker.sqrMagnitude > 0.0001f)
                {
                    forward = -fromMarker.normalized;
                }
            }
            else
            {
                centre = origin + forward * distance;
            }

            centre = SnapToGround(centre);
            if (float.IsNaN(centre.x) || float.IsNaN(centre.z))
            {
                error = "Could not work out where that is.";
                return false;
            }

            result = new Result
            {
                Origin = origin,
                Centre = centre,
                Forward = forward,
                Right = new Vector3(forward.z, 0f, -forward.x),
                Facing = Mathf.Atan2(-forward.x, -forward.z) * Mathf.Rad2Deg,
                UsedMarker = useMarker
            };

            return true;
        }

        /// <summary>
        /// Where one member of a formation stands: spread along the line abreast, with the flanks
        /// set back into a shallow wedge.
        ///
        /// The setback is not decoration. A squad on one straight line has every member in every
        /// other member's field of fire the moment they all turn to face the same threat, and the
        /// friendly-fire check in the controller would then spend the fight refusing to let the
        /// flanks shoot. Staggering the line by half the spacing per step out from the centre gives
        /// each of them an angle past the others.
        /// </summary>
        public static Vector3 FormationSlot(Vector3 centre, Vector3 right, Vector3 forward,
            int index, int count, float spacing)
        {
            float lateral = (index - (count - 1) * 0.5f) * spacing;
            float setback = Mathf.Abs(index - (count - 1) * 0.5f) * spacing * 0.5f;

            return SnapToGround(centre + right * lateral - forward * setback);
        }

        /// <summary>
        /// Drops a point onto whatever is under it. Without this a formation laid out across sloping
        /// ground spawns partly buried and partly in mid-air, and a bandit that starts inside the
        /// terrain never gets its feet under it.
        ///
        /// It probes from far overhead rather than from just above the point, because something
        /// placed two hundred metres away is worked out from a flat bearing and its ground can be a
        /// long way above or below the caller's own.
        ///
        /// BLOCK_COLLISION is the mask the single spawn uses, so a point landing on a roof or a rock
        /// puts the bandit on top of it rather than under it. A probe that hits nothing keeps the
        /// height it was given, which is the best guess available.
        /// </summary>
        /// <summary>
        /// Whether a vehicle-sized box at this spot is free of buildings, fences, trees and anything
        /// already parked there.
        ///
        /// Sized for something the width of a lorry rather than for the particular vehicle, because
        /// every caller asks this *before* it has one: it is the test for whether a spawn is worth
        /// attempting at all. Erring large is the safe direction - a slot rejected for a truck is
        /// still a fine slot for a quad, and the alternative error puts a lorry in a wall.
        /// </summary>
        public static bool IsVehicleSlotClear(Vector3 spot, Vector3 travel)
        {
            const int Mask = RayMasks.LARGE | RayMasks.MEDIUM | RayMasks.SMALL | RayMasks.RESOURCE
                | RayMasks.STRUCTURE | RayMasks.BARRICADE | RayMasks.VEHICLE;

            Vector3 halfExtents = new Vector3(1.6f, 1f, 3.4f);
            Quaternion orientation = travel.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(travel, Vector3.up)
                : Quaternion.identity;

            return !Physics.CheckBox(spot + Vector3.up * (halfExtents.y + 0.3f), halfExtents,
                orientation, Mask, QueryTriggerInteraction.Ignore);
        }

        public static Vector3 SnapToGround(Vector3 point)
        {
            Vector3 probeOrigin = point + Vector3.up * GroundProbeHeight;
            if (Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit hit, GroundProbeDepth, RayMasks.BLOCK_COLLISION))
            {
                return hit.point;
            }

            return point;
        }
    }
}
