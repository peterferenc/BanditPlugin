using Rocket.API;

namespace BanditZombiePlugin
{
    public class BanditZombieConfiguration : IRocketPluginConfiguration
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
        }
    }
}
