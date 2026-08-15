using System.Collections.Generic;

namespace BanditPlugin
{
    /// <summary>
    /// One class of bandit: what it carries, and how it fights with it.
    ///
    /// A kit is picked by name - "/bandit mg" - and everything a bandit needs is resolved from it
    /// once at spawn (see <see cref="BanditProfile"/>), so two bandits standing next to each other
    /// can be running completely different numbers. Nothing here is read again afterwards; editing
    /// a kit affects the next bandit spawned, not the ones already out.
    ///
    /// Two different conventions live in here, and the difference is deliberate:
    ///
    ///   The numbers inherit. Negative means "use whatever the global setting says", which is the
    ///   same convention <see cref="BanditWeapon"/> already uses, so a kit only has to state the
    ///   handful of figures that actually make it a different class.
    ///
    ///   The switches do not. A class is defined by whether it takes cover or lies down, so those
    ///   are stated outright rather than inherited - which does mean that flipping the global
    ///   CoverByDefault has no effect on a bandit spawned from a kit. The globals still apply to a
    ///   bandit spawned with no kit at all.
    ///
    /// Per-weapon settings - hit chance, burst size - belong on the weapon inside
    /// <see cref="Loadout"/> rather than here, because they are already per-weapon and a bandit can
    /// be holding either of two.
    /// </summary>
    public class BanditKit
    {
        /// <summary>
        /// What "/bandit &lt;name&gt;" matches, case-insensitively. Also what the bandit is called in
        /// the player list, which is the quickest way to tell five of them apart in the field.
        /// </summary>
        public string Name = string.Empty;

        public BanditLoadout Loadout = new BanditLoadout();

        /// <summary>
        /// Furthest this class shoots. Worth keeping under the gun's own Range from its .dat, since
        /// past that the bullet stops before it arrives and the bandit is firing at nothing - which
        /// is why the breacher's shotgun is capped so much shorter than the rifles.
        /// </summary>
        public float FireRange = -1f;

        /// <summary>
        /// Furthest this class notices anyone at all. Separate from <see cref="FireRange"/> because
        /// spotting and shooting are different jobs: a marksman that acquires at 200m and fires at
        /// 180m tracks you across a valley before it can touch you, and a breacher has no business
        /// noticing either.
        /// </summary>
        public float TargetAcquireRange = -1f;

        /// <summary>Range this class tries to fight at, and scores cover positions against.</summary>
        public float PreferredEngagementRange = -1f;

        /// <summary>Gap between aimed shots, when this class is not firing bursts.</summary>
        public float FireIntervalSeconds = -1f;

        /// <summary>Gap between bursts, when it is.</summary>
        public float BurstIntervalSeconds = -1f;

        /// <summary>Range at which this class swaps to its sidearm. 0 keeps the primary out.</summary>
        public float SecondaryWeaponRange = -1f;

        /// <summary>
        /// Hold the trigger down and count rounds out, rather than one aimed shot per interval.
        ///
        /// Stated per class because the same setting means opposite things to a machinegun and a
        /// marksman rifle: it is what makes the Nykorev a machinegun at all, and on a semi-only
        /// gun like the Snayperskya it would force the asset onto automatic and turn a 65-damage
        /// sniper into a four-rounds-a-second one. Guns that rechamber - bolt actions, pump
        /// shotguns - are held to semi regardless; see BanditLoadoutApplier.SetFiremode.
        /// </summary>
        public bool BurstFire;

        /// <summary>Spawn this class holding fire, until /bandit shoot start.</summary>
        public bool HoldFire = true;

        /// <summary>Spawn this class already looking for cover when it is exposed.</summary>
        public bool Cover;

        /// <summary>Spawn this class already alternating hiding with stepping out to shoot.</summary>
        public bool Peek;

        /// <summary>
        /// Spawn this class lying down. It still stands up to move anywhere - see
        /// BanditBrain.ApplyProneOrder - so this is a firing stance rather than a way of life.
        /// </summary>
        public bool Prone;

        /// <summary>Walk at a target further away than <see cref="PreferredEngagementRange"/>.</summary>
        public bool AdvanceOnTarget;

        /// <summary>
        /// The four classes a squad is built from, with the weapons picked out of this server's own
        /// Bundles folder. Used both as the field initializer on
        /// <see cref="BanditConfiguration.Kits"/> and by LoadDefaults, so a config file written
        /// before kits existed gains them on the next load rather than coming up with none.
        /// </summary>
        public static List<BanditKit> BuildDefaults()
        {
            return new List<BanditKit>
            {
                // Nykorev: 200-round belt, automatic only, 11 damage a round. It wins a fight by
                // volume rather than by marksmanship, hence the long bursts and the poorest hit
                // chance of the four - a machinegun that lands every round is just a better rifle.
                new BanditKit
                {
                    Name = "mg",
                    FireRange = 120f,
                    TargetAcquireRange = 140f,
                    PreferredEngagementRange = 45f,
                    BurstFire = true,
                    BurstIntervalSeconds = 1.4f,
                    // Cover off on purpose: this class fights from the ground, and going prone is
                    // driven by contact rather than set at spawn - which is squad work, still to
                    // come. Until then /banditprone puts it down by hand.
                    Cover = false,
                    Peek = false,
                    Loadout = MilitaryForest(new BanditWeapon
                    {
                        Item = "126",
                        AimHitChance = 0.22f,
                        BurstMinRounds = 6,
                        BurstMaxRounds = 9
                    })
                },

                // Maplestrike: the ordinary soldier. Everything about this one is the middle of the
                // road, so it is the yardstick the other three read as fast, far or close against.
                new BanditKit
                {
                    Name = "rifleman",
                    FireRange = 100f,
                    TargetAcquireRange = 110f,
                    PreferredEngagementRange = 25f,
                    BurstFire = true,
                    BurstIntervalSeconds = 1.1f,
                    Cover = true,
                    Peek = true,
                    Loadout = MilitaryForest(new BanditWeapon
                    {
                        Item = "363",
                        AimHitChance = 0.35f,
                        BurstMinRounds = 3,
                        BurstMaxRounds = 4
                    })
                },

                // Snayperskya: semi-automatic, 65 damage, and deliberately NOT on burst fire. Its
                // asset has no automatic mode of its own, so BurstFire would force one on and hold
                // the trigger at Firerate 13 - roughly four sniper rounds a second. One slow aimed
                // shot at a time is the whole point of the class.
                new BanditKit
                {
                    Name = "marksman",
                    FireRange = 180f,
                    TargetAcquireRange = 200f,
                    PreferredEngagementRange = 70f,
                    FireIntervalSeconds = 1.6f,
                    BurstFire = false,
                    Cover = true,
                    Peek = true,
                    Loadout = MilitaryForest(new BanditWeapon
                    {
                        Item = "129",
                        AimHitChance = 0.55f
                    })
                },

                // Bluntforce: a pump shotgun, so vanilla's rechamber rule pins it to semi whatever
                // BurstFire says. FireRange is the number that matters here - the asset's own Range
                // is 35m and a pellet that never arrives does nothing at all, so this class has to
                // close, which is what AdvanceOnTarget is for.
                new BanditKit
                {
                    Name = "breacher",
                    FireRange = 30f,
                    TargetAcquireRange = 60f,
                    PreferredEngagementRange = 8f,
                    FireIntervalSeconds = 1f,
                    BurstFire = false,
                    Cover = true,
                    Peek = true,
                    AdvanceOnTarget = true,
                    Loadout = MilitaryForest(new BanditWeapon
                    {
                        Item = "112",
                        AimHitChance = 0.45f
                    })
                }
            };
        }

        /// <summary>
        /// The same military forest uniform for every class, so the only thing that reads
        /// differently in the field is the gun - and the name over their head. Armour is the
        /// biggest single lever on how long a bandit takes to kill, so keeping it identical across
        /// the four is what makes their behaviour comparable at all.
        /// </summary>
        private static BanditLoadout MilitaryForest(BanditWeapon primary)
        {
            return new BanditLoadout
            {
                PrimaryWeapon = primary,
                SecondaryWeapon = new BanditWeapon(),
                Hat = new BanditClothingItem { Item = "309" },
                Mask = new BanditClothingItem { Item = "434" },
                Vest = new BanditClothingItem { Item = "310" },
                Shirt = new BanditClothingItem { Item = "307" },
                Pants = new BanditClothingItem { Item = "308" },
                Backpack = new BanditClothingItem { Item = "811" },
                Glasses = new BanditClothingItem()
            };
        }
    }
}
