using System.Collections.Generic;

namespace BanditPlugin
{
    /// <summary>
    /// A stance, either as something a class adopts on contact or as an order given in the field.
    ///
    /// <see cref="Free"/> is the important one: it means "nobody has an opinion, work it out for
    /// yourself" - so a kit set to Free lets cover decide, and an *order* of Free hands the choice
    /// back to the class after you have overridden it.
    /// </summary>
    public enum BanditStance
    {
        Free,
        Stand,
        Crouch,
        Prone
    }

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
        /// What one of these is worth when "/banditevent &lt;cost&gt;" is spending its budget, and the
        /// figure every squad's own cost is summed from.
        ///
        /// The scale is set by whatever you make the rifleman: it is the ordinary soldier in every
        /// other respect, so it is the unit here too, and the rest of the classes are priced as
        /// multiples of one. Nothing reads this as an absolute - halving every number in the file
        /// and halving what you type on the command line gives identical events - so the only thing
        /// that matters is what a marksman is worth *relative to* a rifleman.
        ///
        /// Must be greater than zero, and the draw refuses to consider anything that is not. A class
        /// costing nothing stays affordable no matter how much has already been spent, so the loop
        /// buying it would never make progress and never end; see BanditEventDraw.
        /// </summary>
        public float Cost = 10f;

        /// <summary>
        /// The smallest event budget this class may appear in at all.
        ///
        /// This is the setting that stops a big event from simply being a lot of riflemen. Cost
        /// alone says what is affordable, not what belongs: with everything unlocked from the
        /// start, a 400-point event rolls forty riflemen about as readily as it rolls marksmen and
        /// a tank. Gating the specialists behind a floor is what makes a large event a different
        /// *kind* of fight rather than the same fight with more men in it.
        ///
        /// 0 means always available, which is right for the class you want to see at every size.
        /// </summary>
        public float MinEventCost;

        /// <summary>
        /// How often this class is picked relative to the others, once it is affordable and
        /// unlocked. 1 is ordinary, 0 excludes it from events entirely while leaving it spawnable
        /// by name with "/bandit &lt;kit&gt;".
        /// </summary>
        public float Weight = 1f;

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
        /// The stance this class drops into when the squad makes contact, and comes out of when it
        /// is over. This is the machinegunner's posture: it does not go looking for cover, it gets
        /// low where it stands and puts rounds down. It still stands up to move anywhere, so
        /// repositioning is never a crawl.
        ///
        /// Crouch is the useful setting and Prone is the extreme one. Lying down puts the eye at
        /// 0.35m, and that is where both the line-of-sight test and the bullet start - so on any
        /// ground that is not billiard-flat a prone gunner spends the fight looking into a rise it
        /// cannot shoot over. Crouching sits at 1.2m, which clears low cover and clutter while
        /// still being a markedly smaller target than standing.
        ///
        /// Whatever is set here is only what the class does when nobody has told it otherwise:
        /// "/bandit stance ..." overrides it outright, and "/bandit stance free" hands it back.
        /// </summary>
        public BanditStance ContactStance = BanditStance.Free;

        /// <summary>
        /// Keep firing at where the enemy was last seen after losing sight of them, rather than
        /// standing there waiting to see them again.
        ///
        /// Suppression is the reason a machinegun is worth having in a squad at all: it is aimed at
        /// a place rather than a person, so it continues while the target is behind cover and it
        /// works off the squad's shared contact - which means the gunner keeps firing at a position
        /// somebody else can still see, from behind a wall it cannot. Off for the classes that
        /// shoot at people, where hosing a bush the target has left is simply a waste.
        /// </summary>
        public bool SuppressiveFire;

        /// <summary>
        /// Shoot the cover rather than waiting for the target to leave it.
        ///
        /// With this off - which is the default, and right for a rifleman or a marksman - a bandit
        /// with no clear line simply holds its fire, because putting rounds into a tree trunk
        /// accomplishes nothing but noise. With it on, a target hiding behind something breakable
        /// gets that something shot out from in front of them: trees, player builds, vehicles and
        /// breakable objects all come down, and terrain and buildings still do not.
        ///
        /// Meant for classes whose weapon can actually do it - a grenadier, or anything firing
        /// explosives. Vehicle turrets do this regardless of the kit, on the grounds that a tank
        /// with a clear shot at the tree someone is behind has no business waiting politely.
        /// </summary>
        public bool DestroysCover;

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
                    // Twice a rifleman and held back until an event is worth a support weapon: one
                    // gun that never stops firing changes a fight more than two more rifles do.
                    Cost = 22f,
                    MinEventCost = 60f,
                    FireRange = 120f,
                    TargetAcquireRange = 140f,
                    PreferredEngagementRange = 45f,
                    BurstFire = true,
                    BurstIntervalSeconds = 1.4f,
                    // Cover off on purpose: this class does not go looking for a rock, it gets low
                    // where it stands and fires. The stance and the suppression are both driven by
                    // contact rather than set at spawn, so it walks upright until there is
                    // something to shoot at.
                    Cover = false,
                    Peek = false,
                    ContactStance = BanditStance.Crouch,
                    SuppressiveFire = true,
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
                    // The unit every other price is quoted in, available at any size and drawn
                    // several times as often as the specialists - most of an event should be these.
                    Cost = 10f,
                    Weight = 3f,
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
                    // The dearest of the four and the latest to unlock. A marksman is the class that
                    // makes an event feel different rather than bigger - it denies ground long
                    // before anything else in the draw can reach you - so it is deliberately not
                    // something a modest budget can stumble into.
                    Cost = 26f,
                    MinEventCost = 100f,
                    FireRange = 180f,
                    TargetAcquireRange = 200f,
                    PreferredEngagementRange = 70f,
                    FireIntervalSeconds = 1.6f,
                    BurstFire = false,
                    Cover = true,
                    Peek = true,
                    // The highest hit chance of the four by a wide margin. A marksman that misses is
                    // just a slow rifleman - the class only reads as one at all because a shot from
                    // it is expected to land. The aim model puts that accuracy on the chest rather
                    // than the head: it draws the miss around AimPointOf(), which is centre of
                    // mass, so raising this concentrates rounds on the torso instead of producing
                    // headshots.
                    Loadout = MilitaryForest(new BanditWeapon
                    {
                        Item = "129",
                        AimHitChance = 0.85f
                    })
                },

                // Bluntforce: a pump shotgun, so vanilla's rechamber rule pins it to semi whatever
                // BurstFire says. FireRange is the number that matters here - the asset's own Range
                // is 35m and a pellet that never arrives does nothing at all, so this class has to
                // close, which is what AdvanceOnTarget is for.
                new BanditKit
                {
                    Name = "breacher",
                    // Barely above a rifleman and unlocked from the start: it only reaches 30m, so
                    // it is dangerous when it arrives and free to ignore when it does not.
                    Cost = 14f,
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
