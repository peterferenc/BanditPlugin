using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin
{
    /// <summary>
    /// What one thing is estimated to be worth, and the working behind it.
    /// </summary>
    public sealed class BanditCostEstimate
    {
        public string Name = string.Empty;

        /// <summary>The suggested price, already scaled so the anchor kit lands on its anchor value.</summary>
        public float Suggested;

        /// <summary>What the configuration currently says, for comparison.</summary>
        public float Current;

        /// <summary>The terms that produced it, so a surprising number can be argued with.</summary>
        public string Working = string.Empty;

        /// <summary>Why it could not be priced, or null. Unpriceable things are never written back.</summary>
        public string Problem;

        public bool IsPriced => Problem == null;

        /// <summary>How far off the current price is, as a multiple. 1 means they agree.</summary>
        public float Ratio => Current > 0f && Suggested > 0f ? Suggested / Current : 0f;
    }

    /// <summary>
    /// Estimates what a kit or a vehicle ought to cost, from the game's own asset data and the
    /// plugin's own numbers.
    ///
    /// The model prices threat as three things multiplied together: how much damage a thing puts out
    /// per second, how long it takes to kill, and how far away it can start doing so. Reach earns its
    /// place as a separate term because a marksman and a breacher doing identical damage are not
    /// remotely equivalent - one of them is shooting at you for a hundred and fifty metres before the
    /// other can see you.
    ///
    /// The single most important thing about this model is what it takes the rate of fire from. It is
    /// NOT the gun's firerate: your bandits are paced by their own kit - burst size over
    /// BurstIntervalSeconds, or FireIntervalSeconds for a single-shot class - and the asset's
    /// firerate is only a ceiling on how fast rounds can leave inside a burst. Pricing a Snayperskya
    /// at its mechanical 3.6 rounds a second would value a marksman at six times what it actually
    /// delivers. Everything here therefore runs through <see cref="BanditProfile"/>, the same
    /// resolved figures the bandit itself fights with.
    ///
    /// Two things it deliberately cannot see, and they are the reason this produces a suggestion
    /// rather than a price:
    ///
    ///   Suppression. A machinegun's worth is that it pins people down and denies ground, and none
    ///   of that appears in damage per second. Priced on output alone the machinegunner comes out
    ///   *below* the rifleman - which is arithmetically correct and tactically wrong.
    ///
    ///   Everything a squad does. Patience, cover separation, how long a sighting is held between
    ///   members - all real, none of it attached to any asset.
    ///
    /// So the output is meant to be read, argued with, and then written into the configuration where
    /// it becomes an ordinary editable number. See <see cref="Commands.CommandBanditCost"/>.
    /// </summary>
    public static class BanditCostModel
    {
        /// <summary>
        /// Ticks of the shot clock per second: PlayerInput feeds SAMPLES (4) per packet at RATE
        /// (0.08s). UseableGun.tockShoot fires when clock - lastFire exceeds the gun's firerate, so
        /// the fastest a gun can cycle is 50 / (firerate + 1) rounds a second.
        ///
        /// Worth checking against something known rather than trusting the arithmetic: the
        /// Snayperskya's firerate of 13 gives 3.6, which is the "roughly four sniper rounds a second"
        /// already recorded in BanditKit's own notes.
        /// </summary>
        private const float ShotClockTicksPerSecond = 50f;

        /// <summary>
        /// A bandit's health before armour. Vanilla players have 100, and vehicle toughness is
        /// expressed against it so that a vehicle's soak comes out in men rather than in points.
        /// </summary>
        private const float BaseHealth = 100f;

        /// <summary>
        /// Prices every kit, scaled so the anchor kit lands exactly on its configured value.
        ///
        /// The scaling is the only reason these numbers mean anything. Raw threat is in units of
        /// damage-metres-per-second-per-armour and is not a quantity anybody has intuition for; what
        /// is useful is "this class is worth 2.6 riflemen". Anchoring to a kit you have already
        /// priced by hand turns the whole table into multiples of something you understand.
        /// </summary>
        public static List<BanditCostEstimate> EstimateKits(BanditConfiguration config)
        {
            List<BanditCostEstimate> estimates = new List<BanditCostEstimate>();
            Dictionary<string, float> threats = new Dictionary<string, float>();

            foreach (BanditKit kit in config.Kits ?? new List<BanditKit>())
            {
                if (kit == null || string.IsNullOrEmpty(kit.Name))
                {
                    continue;
                }

                BanditCostEstimate estimate = new BanditCostEstimate
                {
                    Name = kit.Name,
                    Current = kit.Cost
                };

                float threat = ThreatOf(config, kit, estimate);
                if (estimate.IsPriced)
                {
                    threats[kit.Name] = threat;
                }

                estimates.Add(estimate);
            }

            Scale(config, estimates, threats, AnchorThreat(config, threats));
            return estimates;
        }

        /// <summary>
        /// Prices every vehicle platform - the metal only, matching what
        /// <see cref="BanditVehicleType.Cost"/> means. Its crew is priced as kits and added by
        /// <see cref="BanditConfiguration.VehicleCost"/>, so counting them here would charge twice.
        ///
        /// A vehicle is worth what it soaks plus what it shoots. Soak is its health against a man's
        /// hundred, which is the honest comparison - a 2000-health truck genuinely does take twenty
        /// bandits' worth of shooting to destroy. Guns are its turrets, each priced by exactly the
        /// same output arithmetic as an infantry weapon, because a turret firing at you is a gun
        /// firing at you.
        /// </summary>
        public static List<BanditCostEstimate> EstimateVehicles(BanditConfiguration config)
        {
            List<BanditCostEstimate> estimates = new List<BanditCostEstimate>();
            Dictionary<string, float> threats = new Dictionary<string, float>();

            // Vehicles are scaled against the same anchor as the kits, so a tank priced at 20 really
            // does mean twenty riflemen. Working that out needs the kit threats, so they are
            // recomputed here rather than passed in - it is a handful of dictionary lookups on a
            // command nobody runs in a loop.
            Dictionary<string, float> kitThreats = new Dictionary<string, float>();
            foreach (BanditKit kit in config.Kits ?? new List<BanditKit>())
            {
                if (kit == null || string.IsNullOrEmpty(kit.Name))
                {
                    continue;
                }

                BanditCostEstimate scratch = new BanditCostEstimate { Name = kit.Name };
                float threat = ThreatOf(config, kit, scratch);
                if (scratch.IsPriced)
                {
                    kitThreats[kit.Name] = threat;
                }
            }

            float anchor = AnchorThreat(config, kitThreats);

            foreach (BanditVehicleType type in config.Vehicles ?? new List<BanditVehicleType>())
            {
                if (type == null || string.IsNullOrEmpty(type.Name))
                {
                    continue;
                }

                BanditCostEstimate estimate = new BanditCostEstimate
                {
                    Name = type.Name,
                    Current = type.Cost
                };

                VehicleAsset asset = BanditVehicleSpawner.Resolve(type.Vehicle, out string assetError);
                if (asset == null)
                {
                    estimate.Problem = assetError;
                    estimates.Add(estimate);
                    continue;
                }

                float soak = asset.health / BaseHealth;
                float guns = TurretOutput(config, asset, out int turretCount);

                // Soak and guns are in different units - one is "men of health", the other is raw
                // output - so the guns are converted through the anchor before they are added.
                float gunsInMen = anchor > 0f ? guns / anchor : 0f;
                float men = soak * Mathf.Max(0f, config.CostModelVehicleSoakWeight) + gunsInMen;

                estimate.Suggested = men * config.CostModelAnchorPoints;
                estimate.Working = $"{asset.health} hp = {soak:0.#} men"
                    + (turretCount > 0 ? $", {turretCount} turret(s) = {gunsInMen:0.#} men" : ", unarmed");

                estimates.Add(estimate);
            }

            return estimates;
        }

        /// <summary>
        /// How dangerous one bandit of this class is, in damage-metres per second per unit of
        /// incoming damage. An abstract quantity on purpose - only its ratio to another kit's is
        /// ever used.
        /// </summary>
        private static float ThreatOf(BanditConfiguration config, BanditKit kit, BanditCostEstimate estimate)
        {
            BanditProfile profile = BanditProfile.FromKit(config, kit);
            BanditLoadout loadout = profile.Loadout;

            if (loadout?.PrimaryWeapon == null || string.IsNullOrEmpty(loadout.PrimaryWeapon.Item))
            {
                estimate.Problem = "no primary weapon configured";
                return 0f;
            }

            ItemGunAsset gun = BanditLoadoutApplier.ResolveQuiet(loadout.PrimaryWeapon.Item) as ItemGunAsset;
            if (gun == null)
            {
                estimate.Problem = $"primary weapon '{loadout.PrimaryWeapon.Item}' is not a gun on this server";
                return 0f;
            }

            float damage = DamagePerRound(gun);
            float rounds = RoundsPerSecond(config, profile, loadout.PrimaryWeapon, gun);
            float hit = loadout.PrimaryWeapon.AimHitChance >= 0f
                ? loadout.PrimaryWeapon.AimHitChance
                : config.AimHitChance;

            // The shorter of the two, because both are real limits: past the gun's own Range the
            // bullet stops before it arrives, and past the kit's FireRange the bandit does not pull
            // the trigger at all.
            float range = Mathf.Min(profile.FireRange, gun.range);
            float reach = range / Mathf.Max(1f, config.CostModelReachBaseline);

            float armour = ArmourFactor(loadout);

            float output = damage * rounds * hit;
            float threat = output * reach * armour;

            // "rds/s" spelled out because it is rounds *per second*, not seconds per round, and a
            // burst class's figure looks slow enough to be mistaken for the latter: a rifleman
            // averages 2.3 rounds a second, firing 3-4 of them at 8.3 a second and then waiting.
            estimate.Working = $"{damage:0.#} dmg/shot x {rounds:0.##} rds/s x {hit:0.##} hit = {output:0.#} dps, "
                + $"{range:0}m reach, armour x{armour:0.##}";

            return threat;
        }

        /// <summary>
        /// Damage from one pull of the trigger, at the part of a body the bandit actually aims at.
        ///
        /// The base figure lives on the gun rather than on its ammunition - Player_Damage in the
        /// gun's own .dat. The magazine's own playerDamage is *not* part of this: that field is the
        /// blast damage of an explosive round, not a multiplier on the bullet, so folding it in
        /// would double-count for grenade launchers and do nothing for everything else.
        ///
        /// The spine multiplier is the one that applies, because the aim model puts its rounds on
        /// centre of mass rather than on heads; using the skull figure would price every class as
        /// though it only ever landed headshots.
        ///
        /// Pellets are what the magazine really contributes, and they are the difference between a
        /// shotgun being priced as a slow rifle and being priced as a shotgun - a Bluntforce loads
        /// six-pellet shells, so one trigger pull is six times its listed 40 damage. They are
        /// counted all-or-nothing against the hit roll, which flatters a shotgun slightly at range,
        /// where in truth some of the spread would miss.
        /// </summary>
        private static float DamagePerRound(ItemGunAsset gun)
        {
            PlayerDamageMultiplier multiplier = gun.playerDamageMultiplier;
            float spine = multiplier.spine > 0f ? multiplier.spine : 1f;

            // Whatever the gun spawns loaded with, which is what the bandits actually carry - the
            // loadout does not configure ammunition, and InfiniteAmmo refills to this magazine.
            ItemMagazineAsset magazine = gun.SelectDefaultMagazine();
            float pellets = magazine != null && magazine.pellets > 1 ? magazine.pellets : 1f;

            return multiplier.damage * spine * pellets;
        }

        /// <summary>
        /// How fast this class really shoots, which is a property of the kit far more than of the
        /// gun.
        ///
        /// A burst class fires its burst at the gun's mechanical rate and then waits out
        /// BurstIntervalSeconds, so its true average is the burst divided by the whole cycle
        /// including the pause. A single-shot class is paced entirely by FireIntervalSeconds. Either
        /// way the gun's own rate is a ceiling and almost never the answer.
        /// </summary>
        private static float RoundsPerSecond(BanditConfiguration config, BanditProfile profile,
            BanditWeapon weapon, ItemGunAsset gun)
        {
            float mechanical = ShotClockTicksPerSecond / (gun.firerate + 1f);

            if (!profile.BurstFire)
            {
                float interval = Mathf.Max(0.01f, profile.FireIntervalSeconds);
                return Mathf.Min(mechanical, 1f / interval);
            }

            int minRounds = weapon.BurstMinRounds >= 0 ? weapon.BurstMinRounds : config.BurstMinRounds;
            int maxRounds = weapon.BurstMaxRounds >= 0 ? weapon.BurstMaxRounds : config.BurstMaxRounds;
            float average = Mathf.Max(1f, (minRounds + maxRounds) * 0.5f);

            // The burst itself takes time at the gun's rate, and then the class waits. Both halves
            // count, or a machinegun firing nine-round bursts looks like it fires them instantly.
            float cycle = average / Mathf.Max(0.01f, mechanical) + Mathf.Max(0f, profile.BurstIntervalSeconds);
            return average / Mathf.Max(0.01f, cycle);
        }

        /// <summary>
        /// How much longer this loadout takes to kill than a bandit in shirtsleeves.
        ///
        /// Vanilla armour is a multiplier on incoming damage where lower is better, and only hats,
        /// vests, shirts and pants carry it - masks, glasses and backpacks never do. Condition
        /// matters as much as the item: DamageTool.getPlayerArmor interpolates a piece toward no
        /// protection as its quality falls, so a helmet at 0% stops nothing at all.
        ///
        /// Returned as effective health, so a bandit with no armour is exactly 1 and the term simply
        /// drops out of the arithmetic.
        /// </summary>
        private static float ArmourFactor(BanditLoadout loadout)
        {
            float multiplier = 1f;

            multiplier *= ArmourOf(loadout.Hat);
            multiplier *= ArmourOf(loadout.Vest);
            multiplier *= ArmourOf(loadout.Shirt);
            multiplier *= ArmourOf(loadout.Pants);

            return multiplier > 0f ? 1f / multiplier : 1f;
        }

        private static float ArmourOf(BanditClothingItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Item))
            {
                return 1f;
            }

            if (!(BanditLoadoutApplier.ResolveQuiet(item.Item) is ItemClothingAsset clothing))
            {
                return 1f;
            }

            // The same interpolation vanilla applies: armor + (1 - armor) * (1 - quality/100).
            float quality = Mathf.Clamp01(item.Quality / 100f);
            return clothing.armor + (1f - clothing.armor) * (1f - quality);
        }

        /// <summary>
        /// What a vehicle's turrets put out, priced exactly as an infantry weapon is.
        ///
        /// The hit chance used is the global one rather than any kit's, because whoever ends up in
        /// the seat is decided by the vehicle's crew list and could be any class - and a turret's
        /// worth should not swing on which kit happens to be sitting in it today.
        /// </summary>
        private static float TurretOutput(BanditConfiguration config, VehicleAsset asset, out int turretCount)
        {
            turretCount = 0;

            if (asset.turrets == null)
            {
                return 0f;
            }

            float total = 0f;
            foreach (TurretInfo turret in asset.turrets)
            {
                if (turret == null || turret.itemID == 0)
                {
                    continue;
                }

                if (!(Assets.find(EAssetType.ITEM, turret.itemID) is ItemGunAsset gun))
                {
                    continue;
                }

                turretCount++;

                float rounds = ShotClockTicksPerSecond / (gun.firerate + 1f);
                float reach = gun.range / Mathf.Max(1f, config.CostModelReachBaseline);
                total += DamagePerRound(gun) * rounds * config.AimHitChance * reach;
            }

            return total;
        }

        /// <summary>
        /// The threat the whole table is scaled against - the anchor kit's, or the cheapest priced
        /// kit if the anchor cannot be priced.
        /// </summary>
        private static float AnchorThreat(BanditConfiguration config, Dictionary<string, float> threats)
        {
            string anchorName = !string.IsNullOrEmpty(config.CostModelAnchorKit)
                ? config.CostModelAnchorKit
                : config.DefaultKit;

            foreach (KeyValuePair<string, float> entry in threats)
            {
                if (string.Equals(entry.Key, anchorName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }

            float lowest = 0f;
            foreach (KeyValuePair<string, float> entry in threats)
            {
                if (lowest <= 0f || entry.Value < lowest)
                {
                    lowest = entry.Value;
                }
            }

            return lowest;
        }

        private static void Scale(BanditConfiguration config, List<BanditCostEstimate> estimates,
            Dictionary<string, float> threats, float anchorThreat)
        {
            if (anchorThreat <= 0f)
            {
                foreach (BanditCostEstimate estimate in estimates)
                {
                    if (estimate.IsPriced)
                    {
                        estimate.Problem = "nothing in the configuration could be priced to scale against";
                    }
                }

                return;
            }

            foreach (BanditCostEstimate estimate in estimates)
            {
                if (estimate.IsPriced && threats.TryGetValue(estimate.Name, out float threat))
                {
                    estimate.Suggested = threat / anchorThreat * config.CostModelAnchorPoints;
                }
            }
        }
    }
}
