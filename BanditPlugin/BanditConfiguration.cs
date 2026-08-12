using Rocket.API;

namespace BanditPlugin
{
    public class BanditConfiguration : IRocketPluginConfiguration
    {
        /// <summary>
        /// GUID of the item the bot spawns holding. Defaults to the Eaglefire, read straight out of
        /// Bundles/Items/Guns/Eaglefire/Eaglefire.dat on this server install.
        /// A GUID is used rather than a name because Assets.find(EAssetType.ITEM, name) is
        /// case-sensitive and the asset is actually named "Eaglefire", not "EagleFire".
        /// </summary>
        public string GunAssetGuid = "b03d581a5c1a490f995f8deba57b0f17";

        /// <summary>Legacy numeric ID fallback, used only if the GUID above doesn't resolve.</summary>
        public ushort GunAssetLegacyId = 4;

        public bool GiveGun = true;

        /// <summary>Rounds loaded, and refilled to when InfiniteAmmo is on. Eaglefire's default
        /// magazine (Military_30) holds 30.</summary>
        public byte MagazineCapacity = 30;

        public bool InfiniteAmmo = true;

        public float TurnSpeedDegreesPerSecond = 180f;
        public float ScanIntervalSeconds = 0.5f;
        public float FireIntervalSeconds = 0.6f;
        public float AimToleranceDegrees = 10f;
        public float FireRange = 50f;

        /// <summary>
        /// Fraction of shots whose line passes within AimTargetRadius of the target's chest, i.e.
        /// roughly the bot's hit rate. The aim error needed for this is derived from the distance
        /// to the target, so the bot is no more accurate up close than it is far away in hitbox
        /// terms. 1 restores the old perfect aimbot.
        /// </summary>
        public float AimHitChance = 0.3f;

        /// <summary>
        /// Half-width in metres of the area treated as "on target" when solving for the aim error
        /// above - roughly half a torso.
        /// </summary>
        public float AimTargetRadius = 0.35f;

        /// <summary>
        /// Half-height of that same area. Bigger than the width because a shot that drifts low off
        /// the chest still finds a stomach or a leg, where the same drift sideways finds only air.
        /// Together these two are an ellipse standing in for the player's hitboxes, so they are an
        /// approximation: if the measured hit rate comes out off, tune AimHitChance to taste.
        /// </summary>
        public float AimTargetHalfHeight = 0.8f;

        /// <summary>
        /// Hard cap on the aim error, so that at point blank range - where a torso-width miss is a
        /// huge angle - the bot doesn't visibly flail sideways.
        /// </summary>
        public float AimMaxErrorDegrees = 8f;

        /// <summary>How often a fresh aim error is drawn while the bot is holding aim between shots.</summary>
        public float AimWobbleIntervalSeconds = 0.35f;

        /// <summary>
        /// Time constant for drifting toward a newly drawn aim error. Larger is a slower, lazier
        /// sway; 0 makes the aim snap between samples.
        /// </summary>
        public float AimWobbleSmoothingSeconds = 0.15f;

        /// <summary>
        /// Require an unobstructed line from the bot's eye to the target's before targeting or
        /// firing, so the bot cannot see or shoot through walls, rocks or vehicles.
        /// </summary>
        public bool RequireLineOfSight = true;

        /// <summary>
        /// Master switch for the whole movement/patrol/cover layer. Off makes bandits the
        /// stationary turrets they used to be.
        /// </summary>
        public bool MovementEnabled = true;

        /// <summary>How close, horizontally, counts as having reached a destination.</summary>
        public float ArriveRadius = 2f;

        /// <summary>How often a moving bot asks A* for a fresh path.</summary>
        public float RepathIntervalSeconds = 2.5f;

        /// <summary>
        /// How far a point may be from the navmesh and still be pathed to. AstarPath's own
        /// maxNearestNodeDistance is 100m, which would snap a bot standing in a field onto the
        /// graph of the next town; beyond this distance the bot steers directly instead.
        /// </summary>
        public float NavmeshSnapDistance = 3f;

        /// <summary>Allow requesting sprint while travelling. Vanilla still gates it on stamina.</summary>
        public bool AllowSprint = true;

        /// <summary>Allow jumping over low obstacles and when wedged.</summary>
        public bool AllowJumping = true;

        /// <summary>
        /// Walk toward a target further away than PreferredEngagementRange. Off by default: a
        /// bandit that closes on whoever it sees is a chase behaviour, and it overrides whatever
        /// the bot was doing. Only applies when it has no order or patrol to follow.
        /// </summary>
        public bool AdvanceOnTarget = false;

        /// <summary>
        /// After losing contact, go and look at the last place the target was seen, or back along
        /// the bullet when shot by someone unseen. Off by default for the same reason as
        /// AdvanceOnTarget - it makes the bot leave its post.
        /// </summary>
        public bool InvestigateEnabled = false;

        /// <summary>
        /// Seconds a killed bandit lies there before being removed. A bot has no client to press
        /// respawn, so without this its corpse holds a player slot forever. Negative disables the
        /// cleanup and leaves the body until /banditclear.
        /// </summary>
        public float DespawnSecondsAfterDeath = 5f;

        /// <summary>Range the bot tries to fight at, and scores cover positions against.</summary>
        public float PreferredEngagementRange = 25f;

        /// <summary>Look for cover when a target can see the bot, or when it's being shot.</summary>
        public bool CoverEnabled = true;

        /// <summary>Radius searched for cover around the bot.</summary>
        public float CoverSearchRadius = 18f;

        /// <summary>Minimum gap between cover searches - each one costs a burst of raycasts.</summary>
        public float CoverSearchIntervalSeconds = 3f;

        /// <summary>Blind samples per ring when searching for cover, on top of nearby colliders.</summary>
        public int CoverRingSamples = 12;

        /// <summary>
        /// Cover closer to the threat than this is ignored, so the bot doesn't "take cover" by
        /// walking into someone's lap. Kept low: the tree between you and a shooter ten metres
        /// away is legitimate cover, and a larger value silently rejects every candidate in a
        /// close-range fight.
        /// </summary>
        public float CoverMinimumThreatDistance = 3f;

        /// <summary>How long the bot stays hidden between peeks.</summary>
        public float CoverHideSeconds = 2.5f;

        /// <summary>How long the bot exposes itself to shoot before ducking back.</summary>
        public float CoverPeekSeconds = 2f;

        /// <summary>Start newly spawned bandits patrolling immediately.</summary>
        public bool PatrolByDefault = false;

        /// <summary>How long a bandit loiters at a waypoint before moving on.</summary>
        public float PatrolWaypointDwellSeconds = 3f;

        /// <summary>Return to the first waypoint after the last, rather than stopping.</summary>
        public bool PatrolLoop = true;

        /// <summary>
        /// When a map has no recorded waypoints, patrol between its LocationNodes - the named
        /// places official maps mark in the level editor. Those are town centres rather than
        /// verified walkable points, so a route recorded with /banditwp is always better.
        /// </summary>
        public bool PatrolUseLocationNodesWhenNoWaypoints = true;

        public void LoadDefaults()
        {
            GunAssetGuid = "b03d581a5c1a490f995f8deba57b0f17";
            GunAssetLegacyId = 4;
            GiveGun = true;
            MagazineCapacity = 30;
            InfiniteAmmo = true;
            TurnSpeedDegreesPerSecond = 180f;
            ScanIntervalSeconds = 0.5f;
            FireIntervalSeconds = 0.6f;
            AimToleranceDegrees = 10f;
            FireRange = 50f;
            AimHitChance = 0.3f;
            AimTargetRadius = 0.35f;
            AimTargetHalfHeight = 0.8f;
            AimMaxErrorDegrees = 8f;
            AimWobbleIntervalSeconds = 0.35f;
            AimWobbleSmoothingSeconds = 0.15f;
            RequireLineOfSight = true;

            MovementEnabled = true;
            ArriveRadius = 2f;
            RepathIntervalSeconds = 2.5f;
            NavmeshSnapDistance = 3f;
            AllowSprint = true;
            AllowJumping = true;

            AdvanceOnTarget = false;
            InvestigateEnabled = false;
            DespawnSecondsAfterDeath = 5f;
            PreferredEngagementRange = 25f;

            CoverEnabled = true;
            CoverSearchRadius = 18f;
            CoverSearchIntervalSeconds = 3f;
            CoverRingSamples = 12;
            CoverMinimumThreatDistance = 3f;
            CoverHideSeconds = 2.5f;
            CoverPeekSeconds = 2f;

            PatrolByDefault = false;
            PatrolWaypointDwellSeconds = 3f;
            PatrolLoop = true;
            PatrolUseLocationNodesWhenNoWaypoints = true;
        }
    }
}
