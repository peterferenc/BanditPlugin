namespace BanditPlugin
{
    /// <summary>
    /// One bandit's settings, resolved. Every "-1 means inherit" in a <see cref="BanditKit"/> has
    /// been folded against the global configuration by the time this exists, so the controller and
    /// the brain read plain final values and neither has to know a kit was involved.
    ///
    /// Resolved once, at spawn, and then owned by that bandit for life. That is what lets a
    /// machinegunner and a marksman stand side by side under one configuration - and it is also why
    /// editing a kit does nothing to the bandits already in the field.
    /// </summary>
    public sealed class BanditProfile
    {
        /// <summary>The kit this came from, or "default" for a bandit spawned without one.</summary>
        public string KitName = "default";

        public BanditLoadout Loadout;

        public float FireRange;
        public float TargetAcquireRange;
        public float PreferredEngagementRange;
        public float FireIntervalSeconds;
        public float BurstIntervalSeconds;
        public float SecondaryWeaponRange;
        public bool BurstFire;

        public bool HoldFire;
        public bool Cover;
        public bool Peek;
        public bool Prone;
        public bool AdvanceOnTarget;

        /// <summary>
        /// What a bandit spawned with no kit gets: the global configuration exactly as it was
        /// before kits existed, including the legacy top-level Loadout. Keeping this path means an
        /// untouched config still spawns the bandit it always did.
        /// </summary>
        public static BanditProfile FromConfiguration(BanditConfiguration config)
        {
            return new BanditProfile
            {
                KitName = "default",
                Loadout = config.Loadout,
                FireRange = config.FireRange,
                TargetAcquireRange = config.TargetAcquireRange,
                PreferredEngagementRange = config.PreferredEngagementRange,
                FireIntervalSeconds = config.FireIntervalSeconds,
                BurstIntervalSeconds = config.BurstIntervalSeconds,
                SecondaryWeaponRange = config.SecondaryWeaponRange,
                BurstFire = config.BurstFire,
                HoldFire = config.HoldFireByDefault,
                Cover = config.CoverByDefault,
                Peek = config.PeekByDefault,
                Prone = false,
                AdvanceOnTarget = config.AdvanceOnTarget
            };
        }

        /// <summary>
        /// Folds a kit over the global configuration. Numbers fall back when the kit leaves them
        /// negative; the switches are taken from the kit outright, because whether a class takes
        /// cover is part of what makes it that class. See <see cref="BanditKit"/>.
        /// </summary>
        public static BanditProfile FromKit(BanditConfiguration config, BanditKit kit)
        {
            if (kit == null)
            {
                return FromConfiguration(config);
            }

            return new BanditProfile
            {
                KitName = string.IsNullOrEmpty(kit.Name) ? "unnamed" : kit.Name,
                Loadout = kit.Loadout ?? config.Loadout,
                FireRange = Inherit(kit.FireRange, config.FireRange),
                TargetAcquireRange = Inherit(kit.TargetAcquireRange, config.TargetAcquireRange),
                PreferredEngagementRange = Inherit(kit.PreferredEngagementRange, config.PreferredEngagementRange),
                FireIntervalSeconds = Inherit(kit.FireIntervalSeconds, config.FireIntervalSeconds),
                BurstIntervalSeconds = Inherit(kit.BurstIntervalSeconds, config.BurstIntervalSeconds),
                SecondaryWeaponRange = Inherit(kit.SecondaryWeaponRange, config.SecondaryWeaponRange),
                BurstFire = kit.BurstFire,
                HoldFire = kit.HoldFire,
                Cover = kit.Cover,
                Peek = kit.Peek,
                Prone = kit.Prone,
                AdvanceOnTarget = kit.AdvanceOnTarget
            };
        }

        /// <summary>
        /// A kit's own figure, or the global one when it did not set it. Zero is a real value on
        /// several of these - SecondaryWeaponRange 0 means "never swap" - so the test has to be
        /// strictly negative rather than "falsy".
        /// </summary>
        private static float Inherit(float kitValue, float fallback)
        {
            return kitValue >= 0f ? kitValue : fallback;
        }

        /// <summary>
        /// Warns about the one mistake a hand-written kit makes that nothing else catches: a
        /// FireRange past the gun's own Range, where the bandit pulls the trigger, the bullet stops
        /// short, and absolutely nothing happens. Called once per spawn, after the loadout has been
        /// applied and the asset is known.
        /// </summary>
        public void WarnIfOutranged(SDG.Unturned.ItemGunAsset gun)
        {
            if (gun == null || gun.range <= 0f || FireRange <= gun.range)
            {
                return;
            }

            Rocket.Core.Logging.Logger.LogWarning(
                $"[Bandit] Kit '{KitName}' fires out to {FireRange:0}m but '{gun.FriendlyName}' only reaches "
                + $"{gun.range:0}m, so shots past that do nothing at all. Lower the kit's FireRange.");
        }
    }
}
