using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditteam" - which side of the fight you are on.
    ///
    ///   /banditteam                what team you are on
    ///   /banditteam list           every team, and how many are on it
    ///   /banditteam join red       put yourself on a team
    ///   /banditteam leave          off every team
    ///   /banditteam Bob red        put someone else on a team
    ///
    /// A team is a real in-game group, so joining one is not a plugin bookkeeping entry: your name
    /// goes green for your teammates, you appear on their map, and on a server left at the vanilla
    /// default (Gameplay.Friendly_Fire off) you and they cannot damage each other at all. Bandits
    /// spawned onto that team read it the same way - they will not shoot at you, and they will
    /// shoot at the team next door.
    ///
    /// Which is what makes this a server command rather than a bandit one. Two players can join
    /// "red" and "blue" and fight each other with no bot on the map at all.
    /// </summary>
    public class CommandBanditTeam : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "banditteam";
        public string Help => "Shows, joins or leaves a team - the side bandits and players fight on.";
        public string Syntax => "[list|join <team>|leave|<player> <team>]";
        public List<string> Aliases => new List<string> { "team", "teams" };
        public List<string> Permissions => new List<string> { "bandit.team" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;

            if (command.Length == 0)
            {
                ReplyOwnTeam(caller);
                return;
            }

            string first = command[0];

            if (first.Equals("list", System.StringComparison.OrdinalIgnoreCase)
                || first.Equals("teams", System.StringComparison.OrdinalIgnoreCase))
            {
                ReplyTeams(caller, config);
                return;
            }

            if (first.Equals("leave", System.StringComparison.OrdinalIgnoreCase))
            {
                if (!(caller is UnturnedPlayer callerPlayer))
                {
                    Reply(caller, "Leaving a team is something a player does - run it in game.", Color.red);
                    return;
                }

                BanditTeams.Leave(callerPlayer.Player);
                Reply(caller, "You are on no team. Every bandit is now hostile to you.", Color.yellow);
                return;
            }

            if (first.Equals("join", System.StringComparison.OrdinalIgnoreCase))
            {
                if (command.Length < 2)
                {
                    ReplyUsage(caller, config);
                    return;
                }

                if (!(caller is UnturnedPlayer joiner))
                {
                    Reply(caller, "Joining a team is something a player does - run it in game, or "
                        + "use /banditteam <player> <team>.", Color.red);
                    return;
                }

                JoinTeam(caller, joiner.Player, joiner.CharacterName, command[1], config);
                return;
            }

            // "/banditteam <player> <team>" - the two-word form is what tells it apart from the
            // subcommands above, all of which are one word or start with a keyword.
            if (command.Length >= 2)
            {
                UnturnedPlayer target = UnturnedPlayer.FromName(first);
                if (target == null)
                {
                    Reply(caller, $"No player called '{first}' is on the server. "
                        + "Did you mean /banditteam join <team>?", Color.red);
                    return;
                }

                JoinTeam(caller, target.Player, target.CharacterName, command[1], config);
                return;
            }

            ReplyUsage(caller, config);
        }

        /// <summary>
        /// Puts one player on a team and says so. Shared by the self and the by-name forms, so
        /// putting somebody else on a team reports exactly what joining one does.
        /// </summary>
        private static void JoinTeam(IRocketPlayer caller, Player player, string playerName,
            string teamName, BanditConfiguration config)
        {
            BanditTeam team = BanditTeams.Find(config, teamName);
            if (team == null)
            {
                Reply(caller, $"No team called '{teamName}'. Teams: "
                    + $"{string.Join(", ", BanditTeams.Names(config).ToArray())}.", Color.red);
                return;
            }

            if (!BanditTeams.Assign(player, team))
            {
                Reply(caller, $"Could not put {playerName} on '{team.Label}' - see server console.", Color.red);
                return;
            }

            Reply(caller, $"{playerName} is on {team.Label}. Bandits on that team will hold their fire; "
                + "every other team's will not.", Color.green);

            // Told to the player as well when somebody else moved them, because being switched to a
            // side without noticing is the kind of thing you find out about by being shot.
            if (player.channel?.owner != null
                && caller.Id != player.channel.owner.playerID.steamID.ToString())
            {
                UnturnedChat.Say(UnturnedPlayer.FromPlayer(player), $"You are now on team {team.Label}.", Color.green);
            }
        }

        private static void ReplyOwnTeam(IRocketPlayer caller)
        {
            if (!(caller is UnturnedPlayer callerPlayer))
            {
                Reply(caller, "The console is on no team. Use /banditteam list.", Color.white);
                return;
            }

            string team = BanditTeams.Describe(callerPlayer.Player);
            Reply(caller, team == "none"
                ? "You are on no team - every bandit treats you as a target. /banditteam join <team>."
                : $"You are on {team}.", Color.white);
        }

        /// <summary>
        /// Every team and who is on it, counted off the live players rather than the group's own
        /// member figure - that one includes anyone who joined and logged off, which is not what
        /// "who is on this team right now" means.
        /// </summary>
        private static void ReplyTeams(IRocketPlayer caller, BanditConfiguration config)
        {
            List<string> names = BanditTeams.Names(config);
            if (names.Count == 0)
            {
                Reply(caller, "No teams configured.", Color.red);
                return;
            }

            Reply(caller, $"Teams: {string.Join(", ", names.ToArray())}. "
                + $"Bandits spawn onto '{config.DefaultTeam}' unless told otherwise.", Color.white);

            foreach (string name in names)
            {
                BanditTeam team = BanditTeams.Find(config, name);
                if (team == null)
                {
                    continue;
                }

                Steamworks.CSteamID groupId = BanditTeams.GroupIdOf(team);
                int bots = 0;
                int players = 0;
                foreach (SteamPlayer client in Provider.clients)
                {
                    if (client?.player?.quests == null || client.player.quests.groupID != groupId)
                    {
                        continue;
                    }

                    if (client.player.GetComponent<FakePlayer.BanditBotController>() != null)
                    {
                        bots++;
                    }
                    else
                    {
                        players++;
                    }
                }

                Reply(caller, $"  {name} ({team.Label}): {bots} bandit(s), {players} player(s).", Color.grey);
            }
        }

        private static void ReplyUsage(IRocketPlayer caller, BanditConfiguration config)
        {
            Reply(caller, "Usage: /banditteam  |  /banditteam list  |  /banditteam join <team>  |  "
                + "/banditteam leave  |  /banditteam <player> <team>. "
                + $"Teams: {string.Join(", ", BanditTeams.Names(config).ToArray())}.", Color.yellow);
        }
    }
}
