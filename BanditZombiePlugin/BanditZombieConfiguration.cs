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
        }
    }
}
