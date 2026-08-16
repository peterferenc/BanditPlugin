using System.Collections.Generic;

namespace BanditPlugin
{
    /// <summary>
    /// One squad's settings, resolved. Every "-1 means inherit" in a <see cref="BanditSquadType"/>
    /// has been folded against the global configuration by the time this exists, so the spawn
    /// command and <see cref="FakePlayer.BanditSquad"/> read plain final values and neither has to
    /// know a type was involved.
    ///
    /// The squad equivalent of <see cref="BanditProfile"/>, and resolved at the same moment for the
    /// same reason: a squad owns these figures for life, so two squads of different types can be in
    /// the field at once under one configuration, and editing a type does nothing to either.
    /// </summary>
    public sealed class BanditSquadProfile
    {
        /// <summary>The type this came from, or "squad" when one was spawned without a type.</summary>
        public string TypeName = "squad";

        /// <summary>The kit names to place, left to right. See <see cref="BanditSquadType.Members"/>.</summary>
        public List<string> Members = new List<string>();

        public float Spacing;
        public float SpawnDistance;
        public float ContactMemorySeconds;
        public float CoverSeparation;
        public float RepositionAfterNoShotSeconds;
        public bool WeaponsFree;

        /// <summary>
        /// The global squad settings with no members. Only reached if something spawns a squad
        /// without a type at all; the command never does, since it resolves a name first.
        /// </summary>
        public static BanditSquadProfile FromConfiguration(BanditConfiguration config)
        {
            return new BanditSquadProfile
            {
                TypeName = "squad",
                Members = new List<string>(),
                Spacing = config.SquadSpacing,
                SpawnDistance = config.SquadSpawnDistance,
                ContactMemorySeconds = config.SquadContactMemorySeconds,
                CoverSeparation = config.SquadCoverSeparation,
                RepositionAfterNoShotSeconds = config.RepositionAfterNoShotSeconds,
                WeaponsFree = true
            };
        }

        /// <summary>
        /// Folds a squad type over the global configuration. Numbers fall back when the type leaves
        /// them negative; <see cref="BanditSquadType.WeaponsFree"/> is taken outright. See
        /// <see cref="BanditSquadType"/>.
        /// </summary>
        public static BanditSquadProfile FromType(BanditConfiguration config, BanditSquadType type)
        {
            if (type == null)
            {
                return FromConfiguration(config);
            }

            return new BanditSquadProfile
            {
                TypeName = string.IsNullOrEmpty(type.Name) ? "unnamed" : type.Name,
                Members = type.Members ?? new List<string>(),
                Spacing = Inherit(type.Spacing, config.SquadSpacing),
                SpawnDistance = Inherit(type.SpawnDistance, config.SquadSpawnDistance),
                ContactMemorySeconds = Inherit(type.ContactMemorySeconds, config.SquadContactMemorySeconds),
                CoverSeparation = Inherit(type.CoverSeparation, config.SquadCoverSeparation),
                RepositionAfterNoShotSeconds =
                    Inherit(type.RepositionAfterNoShotSeconds, config.RepositionAfterNoShotSeconds),
                WeaponsFree = type.WeaponsFree
            };
        }

        /// <summary>
        /// A type's own figure, or the global one when it did not set it. Strictly negative rather
        /// than "falsy", because 0 is a real value here - RepositionAfterNoShotSeconds 0 means
        /// "never reposition", which is not the same as "inherit". Same rule as BanditProfile.
        /// </summary>
        private static float Inherit(float typeValue, float fallback)
        {
            return typeValue >= 0f ? typeValue : fallback;
        }
    }
}
