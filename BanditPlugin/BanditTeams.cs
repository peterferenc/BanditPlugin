using System.Collections.Generic;
using SDG.Unturned;
using Steamworks;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin
{
    /// <summary>
    /// Turns the team names in the configuration into real in-game groups, and answers the one
    /// question every bandit asks about everyone it can see: is that a friend or a target?
    ///
    /// Groups are vanilla's, not ours. A team's group ID is derived from its name rather than
    /// generated, which is what makes a team a durable thing: the same name is the same group
    /// across a restart, so a player who joined "red" last night is still red this morning (their
    /// group is saved with their character), and a squad spawned onto red today lines up with them.
    ///
    /// The account IDs are pushed into the top half of the range on purpose. Vanilla's own dynamic
    /// groups count up from 1 as players create them, so nothing derived here can collide with one
    /// however long the server runs.
    /// </summary>
    public static class BanditTeams
    {
        /// <summary>
        /// Which team each group ID belongs to, for reporting what side someone is on. Filled by
        /// <see cref="Ensure"/>, so it only ever holds teams that really exist as groups.
        /// </summary>
        private static readonly Dictionary<ulong, string> LabelsByGroup = new Dictionary<ulong, string>();

        /// <summary>
        /// Creates the group behind every configured team, and refreshes the name of one that was
        /// already there. Safe to call repeatedly - getOrAddGroup is a lookup when the group exists,
        /// which is the case for every team that survived into Groups.dat from the last session.
        ///
        /// Called once when the plugin loads and again whenever a team is looked up, because a
        /// group created before the level finished loading would not exist to join.
        /// </summary>
        public static void Ensure(BanditConfiguration config)
        {
            if (config?.Teams == null)
            {
                return;
            }

            foreach (BanditTeam team in config.Teams)
            {
                if (team == null || string.IsNullOrEmpty(team.Name))
                {
                    continue;
                }

                Ensure(team);
            }
        }

        /// <summary>
        /// The group behind one team, created if this is the first time it has been asked for.
        /// Returns null if the group system is not up yet, which is the only way this fails.
        /// </summary>
        public static GroupInfo Ensure(BanditTeam team)
        {
            if (team == null || string.IsNullOrEmpty(team.Name))
            {
                return null;
            }

            CSteamID groupId = GroupIdFor(team.Name);

            try
            {
                GroupInfo group = GroupManager.getOrAddGroup(groupId, team.Label, out bool wasCreated);
                if (group == null)
                {
                    return null;
                }

                // A team renamed in the configuration keeps its group - the ID comes from Name, not
                // DisplayName - so the label is pushed onto the existing group rather than ignored.
                if (!wasCreated && group.name != team.Label)
                {
                    group.name = team.Label;
                    GroupManager.sendGroupInfo(group);
                }

                LabelsByGroup[groupId.m_SteamID] = team.Label;
                return group;
            }
            catch (System.Exception e)
            {
                Logger.LogError($"[Bandit] Could not create the group for team '{team.Name}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// The team of that name with its group ready to join, or null. Case-insensitive, because
        /// these are typed into chat.
        /// </summary>
        public static BanditTeam Find(BanditConfiguration config, string name)
        {
            if (config?.Teams == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            foreach (BanditTeam team in config.Teams)
            {
                if (team != null && string.Equals(team.Name, name, System.StringComparison.OrdinalIgnoreCase))
                {
                    Ensure(team);
                    return team;
                }
            }

            return null;
        }

        /// <summary>
        /// The team a bandit joins when nothing names one: the configured default, or - if that
        /// name matches no team - none at all, which leaves the bandit ungrouped and behaving
        /// exactly as bandits did before teams existed.
        /// </summary>
        public static BanditTeam Default(BanditConfiguration config)
        {
            return Find(config, config?.DefaultTeam);
        }

        /// <summary>Every configured team name, for usage lines and "/banditteam list".</summary>
        public static List<string> Names(BanditConfiguration config)
        {
            List<string> names = new List<string>();
            if (config?.Teams == null)
            {
                return names;
            }

            foreach (BanditTeam team in config.Teams)
            {
                if (team != null && !string.IsNullOrEmpty(team.Name))
                {
                    names.Add(team.Name);
                }
            }

            return names;
        }

        /// <summary>
        /// Puts a player - bot or real - onto a team, taking them off whatever team they were on.
        ///
        /// The member limit is bypassed deliberately: Max_Group_Members is there to stop a player
        /// clan swallowing a server, and a team is meant to have a side's worth of people on it.
        /// Leaving first keeps the group's member count honest, since vanilla only decrements it
        /// through leaveGroup.
        /// </summary>
        public static bool Assign(Player player, BanditTeam team)
        {
            if (player?.quests == null)
            {
                return false;
            }

            GroupInfo group = Ensure(team);
            if (group == null)
            {
                return false;
            }

            if (player.quests.groupID == group.groupID)
            {
                return true;
            }

            Leave(player);
            return player.quests.ServerAssignToGroup(group.groupID, EPlayerGroupRank.MEMBER, bypassMemberLimit: true);
        }

        /// <summary>
        /// Takes a player off whatever team they are on. Forced, because the vanilla rules exist to
        /// stop a player abandoning their clan mid-raid and none of them mean anything to a bot -
        /// and a bandit being despawned has to leave whether or not it is allowed to.
        /// </summary>
        public static void Leave(Player player)
        {
            if (player?.quests == null || player.quests.groupID == CSteamID.Nil)
            {
                return;
            }

            player.quests.leaveGroup(force: true);
        }

        /// <summary>
        /// Whether <paramref name="self"/> should be shooting at <paramref name="other"/>.
        ///
        /// The whole rule, in order:
        ///   - same group, never. That covers two red bandits as much as it covers a red bandit and
        ///     the player who joined red.
        ///   - two bandits that are both on no team are one side, so a server that never configures
        ///     teams still gets bots that ignore each other.
        ///   - anyone on no team at all is fair game, unless HostileToUngrouped says otherwise. A
        ///     lone player who has joined nothing is the case this exists for.
        ///   - anything else is on a different side, and is a target.
        ///
        /// <paramref name="otherIsBandit"/> is passed rather than looked up because both callers
        /// already know it, and finding out costs a GetComponent on a path that runs per shot.
        /// </summary>
        public static bool IsHostile(Player self, Player other, bool otherIsBandit, bool hostileToUngrouped)
        {
            if (self?.quests == null || other?.quests == null)
            {
                return false;
            }

            CSteamID mine = self.quests.groupID;
            CSteamID theirs = other.quests.groupID;

            if (mine == theirs && (mine != CSteamID.Nil || otherIsBandit))
            {
                return false;
            }

            if (theirs == CSteamID.Nil)
            {
                return hostileToUngrouped;
            }

            return true;
        }

        /// <summary>
        /// What side someone is on, for status lines and chat: the team name if it is one of ours,
        /// the group's own name if the player is in a group they made themselves, otherwise "none".
        /// </summary>
        public static string Describe(Player player)
        {
            if (player?.quests == null || player.quests.groupID == CSteamID.Nil)
            {
                return "none";
            }

            CSteamID groupId = player.quests.groupID;
            if (LabelsByGroup.TryGetValue(groupId.m_SteamID, out string label))
            {
                return label;
            }

            GroupInfo group = GroupManager.getGroupInfo(groupId);
            return group != null && !string.IsNullOrEmpty(group.name) ? group.name : groupId.ToString();
        }

        /// <summary>
        /// Pulls a "team:&lt;name&gt;" (or "team=&lt;name&gt;") argument out of a command's words and
        /// hands back what is left.
        ///
        /// Keyed rather than positional on purpose: "/squadspawn" and "/bandit" both already read
        /// their arguments by position, and a bare team name in among them could not be told from a
        /// kit, a squad type or a distance. This form can sit anywhere and be recognised.
        /// </summary>
        public static string[] ExtractTeamArgument(string[] command, out string teamName)
        {
            teamName = null;
            if (command == null || command.Length == 0)
            {
                return command ?? new string[0];
            }

            List<string> remaining = new List<string>(command.Length);
            foreach (string word in command)
            {
                if (word != null && word.Length > 5
                    && word.StartsWith("team", System.StringComparison.OrdinalIgnoreCase)
                    && (word[4] == ':' || word[4] == '='))
                {
                    teamName = word.Substring(5);
                    continue;
                }

                remaining.Add(word);
            }

            return remaining.ToArray();
        }

        /// <summary>The group a team's members are in, whether or not that group exists yet.</summary>
        public static CSteamID GroupIdOf(BanditTeam team)
        {
            return team == null || string.IsNullOrEmpty(team.Name) ? CSteamID.Nil : GroupIdFor(team.Name);
        }

        /// <summary>
        /// A team name's group ID. FNV-1a over the lowercased name, landing in 0x40000000-0x7FFFFFFF
        /// so it is clear of the account IDs vanilla hands out to player-made groups (which start at
        /// 1) and clear of the top of the range, which the save code adds one to.
        ///
        /// Same universe and account type vanilla uses for a dynamic group, so a client treats one
        /// of these exactly like a group formed in game - which is the entire point.
        /// </summary>
        private static CSteamID GroupIdFor(string name)
        {
            uint hash = 2166136261u;
            foreach (char c in name.ToLowerInvariant())
            {
                hash ^= c;
                hash *= 16777619u;
            }

            uint accountId = 0x40000000u | (hash & 0x3FFFFFFFu);
            return new CSteamID(new AccountID_t(accountId), EUniverse.k_EUniversePublic,
                EAccountType.k_EAccountTypeConsoleUser);
        }
    }
}
