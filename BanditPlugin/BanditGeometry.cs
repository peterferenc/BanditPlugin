using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin
{
    /// <summary>
    /// The handful of measurements every part of the plugin takes off a player, in one place.
    ///
    /// These are three-line helpers, which is exactly why they were copied rather than shared: the
    /// controller, the brain, the vehicle driver, both navigators and the cover finder each grew
    /// their own. The copies then drifted. "Where is a player's eye" was answered with a 1.5m
    /// fallback in four places and a 1.75m one in two, and the chest fraction was declared as a
    /// named constant twice - one of them carrying a comment saying it had to match the other - and
    /// written as a bare 0.7f a third time.
    ///
    /// Nothing here is interesting on its own. It is here so that there is one answer to each
    /// question rather than several that agree by luck.
    /// </summary>
    public static class BanditGeometry
    {
        /// <summary>
        /// Where PlayerLook puts the aim transform on someone standing, used only when
        /// <see cref="EyeOf"/> is handed a player whose look is not wired up yet.
        ///
        /// A live player always has one, so this is a degenerate path - it matters for the frame
        /// between a bandit being registered and Player.InitializePlayer finishing, and for nothing
        /// else. It is named rather than inlined so the fallback cannot quietly become a different
        /// height in each caller again.
        /// </summary>
        public const float StandingEyeHeight = 1.75f;

        /// <summary>
        /// Height PlayerLook puts the aim transform at when prone (HEIGHT_LOOK_PRONE), which is
        /// where a prone bandit's eyes and its bullets both are.
        /// </summary>
        public const float ProneEyeHeight = 0.35f;

        /// <summary>The same, crouched (HEIGHT_LOOK_CROUCH).</summary>
        public const float CrouchEyeHeight = 1.2f;

        /// <summary>
        /// The eye height assumed above a bare ground position - a remembered sighting, a squad's
        /// contact report, a cover sample - where there is no player to read a real aim transform
        /// off.
        ///
        /// Deliberately not <see cref="StandingEyeHeight"/>. It is an approximation of "roughly
        /// where a person's head would be" used to *score visibility*, and the cover finder and the
        /// brain have to agree on it or a bandit picks cover against a threat eye that the
        /// visibility test then places somewhere else.
        /// </summary>
        public const float VisibilityEyeHeight = 1.65f;

        /// <summary>
        /// How far from the feet towards the eyes the chest sits. See <see cref="AimPointOf"/>.
        /// </summary>
        public const float ChestHeightFraction = 0.7f;

        /// <summary>
        /// A player's own aim transform - their eye, and where their shots come from. Follows their
        /// stance, so this is 1.75m standing and 0.35m prone.
        /// </summary>
        public static Vector3 EyeOf(Player player)
        {
            return player.look != null && player.look.aim != null
                ? player.look.aim.position
                : player.transform.position + Vector3.up * StandingEyeHeight;
        }

        /// <summary>
        /// Where on a player a bot points: the chest, wherever that has ended up.
        ///
        /// Taken as a fraction of the way from the feet to that player's own aim transform, so it
        /// follows their stance for free - roughly 1.2m on someone standing, 0.85m crouched, 0.25m
        /// prone. A fixed offset off the ground cannot do that, and the one this replaced (a flat
        /// 1.5m) sailed a clear metre over anyone lying down.
        ///
        /// The chest rather than the eye because the aim error model draws a miss around this point
        /// with a half-height of AimTargetHalfHeight; centred on the eyes, half of that ellipse is
        /// over open air above the head and the measured hit rate comes out under the configured
        /// one.
        /// </summary>
        public static Vector3 AimPointOf(Player player)
        {
            return Vector3.Lerp(player.transform.position, EyeOf(player), ChestHeightFraction);
        }

        /// <summary>The same vector with its height thrown away.</summary>
        public static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        /// <summary>
        /// Distance ignoring height. Nearly every distance in the plugin is this one: a bandit two
        /// metres away up a staircase is next to you, and steering, spacing and arrival all want
        /// the map distance rather than the true one.
        /// </summary>
        public static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return (a - b).magnitude;
        }
    }
}
