using System.Collections.Generic;

namespace BanditPlugin
{
    /// <summary>
    /// One kind of squad: who is in it, and how the group as a whole fights.
    ///
    /// This is to "/squadspawn" what <see cref="BanditKit"/> is to "/bandit" - a named thing you can
    /// put on the ground without stating any of its details, so "/squadspawn sniper" and
    /// "/squadspawn rifle" produce two squads that behave differently under one configuration.
    ///
    /// The split between the two is deliberate and it is the whole reason this class is short: what
    /// a man carries and how far he shoots is a property of his class, not of the squad he happens
    /// to be in, so weapons, clothing, hit chance, engagement range and stance all live in the kits
    /// named by <see cref="Members"/>. What is left here is the handful of things that only mean
    /// anything to several bandits at once - how far apart they stand, how long they hold a sighting
    /// between them, how far apart they take cover - plus where the squad is placed.
    ///
    /// Which means a squad is given "different items" by naming different kits, not by restating
    /// them. A sniper squad is two marksmen and a rifleman precisely because the marksman kit is
    /// already the one with the Snayperskya and the 200m acquire range.
    ///
    /// The numbers here inherit: negative means "use the global Squad* setting", the same convention
    /// <see cref="BanditKit"/> uses, so a type only states the figures that actually make it a
    /// different squad. <see cref="WeaponsFree"/> is stated outright, for the same reason a kit's
    /// switches are - it is part of what the type is.
    ///
    /// Everything is resolved into a <see cref="BanditSquadProfile"/> once, when the squad is
    /// spawned. Editing a type affects the next squad, not the one already in the field.
    /// </summary>
    public class BanditSquadType
    {
        /// <summary>
        /// What "/squadspawn &lt;name&gt;" matches, case-insensitively.
        /// </summary>
        public string Name = string.Empty;

        /// <summary>
        /// The kits this squad is built from, in the order they are laid out from left to right.
        /// Any kit name from <see cref="BanditConfiguration.Kits"/> is valid, and repeats are how
        /// you get three riflemen. An unknown name is skipped with a warning rather than aborting
        /// the spawn, so one typo costs you a man rather than the squad.
        /// </summary>
        public List<string> Members = new List<string>();

        /// <summary>Metres between members as they are placed, and the depth of the wedge.</summary>
        public float Spacing = -1f;

        /// <summary>
        /// How far off "/squadspawn" puts this squad when you do not name a distance.
        ///
        /// Worth setting per type rather than leaving global, because the point of the default is to
        /// be past the squad's own eyes: you walk in on them and get to watch the moment they
        /// notice. That distance is a property of who is in the squad - a rifle section notices at
        /// 110m and a pair of marksmen at 200m, so one figure cannot be right for both.
        /// </summary>
        public float SpawnDistance = -1f;

        /// <summary>
        /// How long this squad keeps acting on a sighting after the last member loses sight of it.
        /// Longer suits a squad that is meant to hold and watch a piece of ground; shorter lets one
        /// go back to standing about sooner after you break contact.
        /// </summary>
        public float ContactMemorySeconds = -1f;

        /// <summary>
        /// Closest two of these members will deliberately take cover to each other. Wide spreads a
        /// squad along a ridge; narrow keeps it behind one wall, where one grenade is all of them.
        /// </summary>
        public float CoverSeparation = -1f;

        /// <summary>
        /// How long a member sits in contact without a shot before it gives up its cover and looks
        /// for an angle. This is the squad's patience, and it is the difference between a section
        /// that works its way round you and a pair of marksmen that stay in a hide you have not
        /// found yet. 0 leaves them where they are indefinitely.
        /// </summary>
        public float RepositionAfterNoShotSeconds = -1f;

        /// <summary>
        /// Spawn this squad weapons free and under its classes' standing orders, rather than inert.
        ///
        /// On for every default type: a squad exists to be watched fighting, and one that has to be
        /// switched on a command at a time is not a squad - that is what "/bandit" is for.
        /// </summary>
        public bool WeaponsFree = true;

        /// <summary>
        /// The squads a fresh configuration starts with, built from the default kits. Used both by
        /// LoadDefaults and by the backfill in BanditPlugin.Load, so a configuration written before
        /// squad types existed gains them on the next start rather than coming up with none.
        /// </summary>
        public static List<BanditSquadType> BuildDefaults()
        {
            return new List<BanditSquadType>
            {
                // One of each class - the squad "/squadspawn" always put down, kept as the default
                // because it is the only one that shows all four behaviours in the same fight: the
                // gunner suppressing, the marksman reaching, the breacher closing and the riflemen
                // working cover. Every figure inherits, so this is the global configuration exactly.
                new BanditSquadType
                {
                    Name = "basic",
                    Members = new List<string> { "rifleman", "rifleman", "mg", "marksman", "breacher" }
                },

                // A rifle section: four of the same class, so nothing in the fight is coming from a
                // specialist. Tighter and closer than the mixed squad in every respect - it has no
                // marksman to keep it honest at distance, so it is spawned just outside a rifleman's
                // own 110m acquire range, and its short patience is what makes it the type that
                // comes looking for you rather than settling in.
                new BanditSquadType
                {
                    Name = "rifle",
                    Members = new List<string> { "rifleman", "rifleman", "rifleman", "rifleman" },
                    Spacing = 4f,
                    SpawnDistance = 130f,
                    RepositionAfterNoShotSeconds = 3.5f
                },

                // Two marksmen and a rifleman for close security. The opposite squad in every
                // respect: it starts beyond 200m because that is where a marksman starts noticing,
                // it stands far enough apart that finding one does not give you the other, and its
                // long patience means a marksman that cannot get a shot stays in its hide instead of
                // repositioning into the open. The long contact memory is what keeps the pair
                // watching the spot you ducked into for a good while after you left it.
                new BanditSquadType
                {
                    Name = "sniper",
                    Members = new List<string> { "marksman", "marksman", "rifleman" },
                    Spacing = 12f,
                    SpawnDistance = 260f,
                    ContactMemorySeconds = 25f,
                    CoverSeparation = 10f,
                    RepositionAfterNoShotSeconds = 12f
                }
            };
        }
    }
}
