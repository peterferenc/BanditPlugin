namespace BanditPlugin
{
    /// <summary>
    /// One clothing item in the loadout.
    ///
    /// <see cref="Item"/> takes either form of Unturned item identifier and works out which it is
    /// given: a legacy numeric ID like "309", or a GUID like
    /// "09c22f9a82d349a99b96e41cd3b49788" (with or without dashes). The two can never be confused
    /// for each other - an ID is at most five digits and a GUID is thirty-two hex characters - so
    /// one field is enough. Leave it blank and the slot stays empty.
    ///
    /// IDs are the easier thing to type and are stable for vanilla items. They are only unique
    /// within vanilla, though: two workshop mods routinely ship items on the same ID, and whichever
    /// loads last wins - so for anything from the workshop, prefer the GUID.
    ///
    /// Look either one up in the server's own Bundles folder rather than a wiki, since curated and
    /// workshop content differs per install: every item's .dat file opens with its GUID and ID, e.g.
    /// Bundles/Items/Hats/Military_Helmet_Forest/Military_Helmet_Forest.dat.
    /// </summary>
    public class BanditClothingItem
    {
        public string Item = string.Empty;

        /// <summary>
        /// Condition, 0-100. This is not cosmetic: DamageTool.getPlayerArmor() interpolates an
        /// item's armour rating toward "no protection at all" as its quality falls, by
        /// armor + (1 - armor) * (1 - quality/100) - so a 0% helmet stops nothing. Only hats,
        /// vests, shirts and pants carry armour; masks, glasses and backpacks never do.
        /// </summary>
        public byte Quality = 100;
    }

    /// <summary>
    /// A gun for one of the two equipment slots. <see cref="Item"/> takes an ID or a GUID, exactly
    /// as <see cref="BanditClothingItem"/> does.
    ///
    /// Ammunition is not configured here - the magazine the asset itself spawns with is used, and
    /// InfiniteAmmo refills to that magazine's real capacity, so a sidearm is topped up to its
    /// seven rounds and a rifle to its thirty.
    /// </summary>
    public class BanditWeapon
    {
        public string Item = string.Empty;

        /// <summary>
        /// Overrides <see cref="BanditConfiguration.AimHitChance"/> while this weapon is in the
        /// bot's hands, so a sidearm can be made scrappier than the rifle without touching the rest
        /// of the aim model. Negative (the default) inherits the global value.
        /// </summary>
        public float AimHitChance = -1f;
    }

    /// <summary>
    /// What every bandit spawns wearing and carrying.
    ///
    /// Weapons go into the two real equipment slots, so which one an asset is allowed in is decided
    /// by the asset's own Slot line, not by this config: a "Slot Primary" rifle cannot be a
    /// secondary, while a "Slot Secondary" pistol fits either. A weapon that will not fit the slot
    /// it is configured for is logged and dropped into the bot's bag instead.
    ///
    /// IDs from this server's Bundles folder, for copy-pasting:
    ///   Guns      4 Eaglefire      363 Maplestrike      97 Colt (fits either slot)
    ///   Hats      309 Military_Helmet_Forest
    ///   Masks     812 Balaclava_Bandit
    ///   Vests     310 Military_Vest_Forest
    ///   Shirts    307 Military_Top_Forest
    ///   Pants     308 Military_Bottom_Forest
    ///   Backpacks 811 Backpack_Bandit
    ///   Glasses   334 Nightvision_Military
    ///
    /// Only the rifle is filled in by default. Armour changes how long a bandit takes to kill, so
    /// kitting one out in military gear is left as a deliberate choice rather than something that
    /// happens the first time the plugin regenerates its config.
    /// </summary>
    public class BanditLoadout
    {
        public BanditWeapon PrimaryWeapon = new BanditWeapon
        {
            Item = "4" // Eaglefire; its GUID, b03d581a5c1a490f995f8deba57b0f17, goes in the same field
        };

        public BanditWeapon SecondaryWeapon = new BanditWeapon();

        public BanditClothingItem Hat = new BanditClothingItem();

        public BanditClothingItem Mask = new BanditClothingItem();

        public BanditClothingItem Vest = new BanditClothingItem();

        public BanditClothingItem Shirt = new BanditClothingItem();

        public BanditClothingItem Pants = new BanditClothingItem();

        public BanditClothingItem Backpack = new BanditClothingItem();

        public BanditClothingItem Glasses = new BanditClothingItem();
    }
}
