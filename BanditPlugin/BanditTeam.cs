using System.Collections.Generic;

namespace BanditPlugin
{
    /// <summary>
    /// One side of a fight: a name you can spawn a squad onto, and that a real player can join.
    ///
    /// A team is not a plugin-private tag. It is a real in-game group - the same thing vanilla
    /// creates when players form a group between themselves - so everything the game already does
    /// with groups comes free: teammates show each other's names in green, they appear on each
    /// other's map, and the server's Gameplay.Friendly_Fire setting decides whether they can hurt
    /// one another at all. See <see cref="BanditTeams"/> for how a name becomes a group.
    ///
    /// Which means "teams" is a server feature rather than a bandit one: a player types
    /// "/banditteam join red" and is on the same side as every red bandit, with no bot involved.
    /// </summary>
    public class BanditTeam
    {
        /// <summary>
        /// What "/banditteam join &lt;name&gt;" and "team:&lt;name&gt;" match, case-insensitively.
        /// Also what the group ID is derived from, so renaming a team makes a different group and
        /// leaves anyone who joined the old one behind on it.
        /// </summary>
        public string Name = string.Empty;

        /// <summary>
        /// The group's name in game, and the prefix on the bandit names - so a "red" bandit
        /// machinegunner is called "Red mg" and shows "Red" as its group. Empty falls back to
        /// <see cref="Name"/>.
        ///
        /// Worth having separately from the name purely so the default team can be typed as
        /// "bandits" but still produce the "Bandit mg" the bots were always called.
        /// </summary>
        public string DisplayName = string.Empty;

        /// <summary>
        /// What to show and to name bandits after: DisplayName, or the name it was given. Kept out
        /// of the file - it is derived from the two fields above, and a third element saying the
        /// same thing is a third element to keep in step.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public string Label => !string.IsNullOrEmpty(DisplayName) ? DisplayName : Name;

        /// <summary>
        /// The teams a fresh configuration starts with. "bandits" is the side every bandit joins
        /// when nothing says otherwise, which is what keeps a server that never touches teams
        /// behaving exactly as it did: one side of bandits, hostile to everyone not on it.
        ///
        /// "red" and "blue" exist to be spawned against each other -
        /// "/squadspawn rifle team:red" then "/squadspawn rifle team:blue" is a fight you can
        /// stand and watch.
        /// </summary>
        public static List<BanditTeam> BuildDefaults()
        {
            return new List<BanditTeam>
            {
                new BanditTeam { Name = "bandits", DisplayName = "Bandit" },
                new BanditTeam { Name = "red", DisplayName = "Red" },
                new BanditTeam { Name = "blue", DisplayName = "Blue" }
            };
        }
    }
}
