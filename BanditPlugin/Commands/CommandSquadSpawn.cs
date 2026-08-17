using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/squadspawn" - puts a whole squad on the ground in front of you, already switched on.
    ///
    ///   /squadspawn                 the default type, down your sightline at its own distance
    ///   /squadspawn sniper          a type by name - see /squadspawn squads
    ///   /squadspawn sniper 300      the same, at a distance you name
    ///   /squadspawn rifle marker    wherever you have placed your map marker
    ///   /squadspawn 60              the default type at 60m, as before types existed
    ///   /squadspawn squads          list the types, who is in them and where they are placed
    ///
    /// The type is the whole point: "rifle" and "sniper" are not the same five men at different
    /// ranges, they are different men - a squad type names the kits it is built from, so it brings
    /// their weapons, their accuracy and their engagement ranges with it, and adds the things that
    /// only mean anything to a group: how far apart they stand and take cover, how long they hold a
    /// sighting between them, and how long they will sit without a shot before moving. See
    /// <see cref="BanditSquadType"/>.
    ///
    /// Placed far off on purpose - past that type's own TargetAcquireRange - so the squad spawns
    /// unaware and you walk in on it. A squad that appears on top of you skips the only part worth
    /// watching, which is the moment they notice. The distance is per type because "past their
    /// eyes" is 130m for a rifle section and 260m for a pair of marksmen.
    ///
    /// Unlike "/bandit", which spawns one inert bandit for you to order about a command at a time,
    /// a squad comes out fighting: weapons free, each class under its own standing orders. That is
    /// the point of it - the behaviour worth watching is what five of them do between themselves
    /// when they see you, and none of that happens to a squad that has to be switched on.
    ///
    /// What you should see when you walk into one: whoever spots you first reports it to the rest,
    /// so the ones who cannot see you react anyway. The machinegunner drops flat where it stands
    /// and opens up - and keeps firing at where you were after you break line of sight, for as long
    /// as anybody else can still see you. The riflemen, the marksman and the breacher go for cover,
    /// and they go for *different* cover, because each one claims its spot and the rest search
    /// around it.
    /// </summary>
    public class CommandSquadSpawn : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "squadspawn";
        public string Help => "Spawns a squad of a given type in formation down your sightline, or at your map marker.";
        public string Syntax => "[<type>] [<metres>|marker]";
        public List<string> Aliases => new List<string> { "spawnsquad", "squad" };
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;

            // Taken out of the words before anything else reads them, because everything else this
            // command takes is positional and a bare team name could not be told from a type or a
            // distance. "team:blue" can therefore sit anywhere in the line.
            command = BanditTeams.ExtractTeamArgument(command, out string requestedTeam);

            if (command.Length > 0 && IsListRequest(command[0]))
            {
                ReplySquads(caller, config);
                return;
            }

            // The first word is a type name unless it is one of the two things the command took
            // before types existed - a distance or "marker" - so "/squadspawn 60" still means what
            // it always did, and a type is never mistaken for one because neither parses as a name.
            int placementArgument = 0;
            string requestedType = config.DefaultSquad;
            if (command.Length > 0 && !BanditPlacement.IsMarkerRequest(command[0]) && !float.TryParse(command[0], out _))
            {
                requestedType = command[0];
                placementArgument = 1;
            }

            BanditSquadType type = config.FindSquad(requestedType);
            if (type == null)
            {
                UnturnedChat.Say(caller, $"No squad type called '{requestedType}'. Known types: "
                    + $"{string.Join(", ", config.SquadNames().ToArray())}. Try /squadspawn squads.", Color.red);
                return;
            }

            BanditSquadProfile profile = BanditSquadProfile.FromType(config, type);
            List<string> composition = profile.Members;
            if (composition == null || composition.Count == 0)
            {
                UnturnedChat.Say(caller, $"Squad type '{profile.TypeName}' has no members - nothing to spawn.", Color.red);
                return;
            }

            // The side it fights on: what the caller typed, else the type's own, else the default.
            // A name nobody recognises is refused rather than quietly spawning the squad onto the
            // default team - a squad on the wrong side is the one mistake here you find out about
            // by being shot by it.
            string teamName = !string.IsNullOrEmpty(requestedTeam) ? requestedTeam : profile.Team;
            BanditTeam team = BanditTeams.Find(config, teamName);
            if (team == null && !string.IsNullOrEmpty(requestedTeam))
            {
                UnturnedChat.Say(caller, $"No team called '{requestedTeam}'. Teams: "
                    + $"{string.Join(", ", BanditTeams.Names(config).ToArray())}.", Color.red);
                return;
            }

            // Where the squad goes: your map marker, a distance you name, or the type's own down
            // your sightline. Shared with /banditevent, which takes the same words - see
            // BanditPlacement.
            string placement = command.Length > placementArgument ? command[placementArgument] : null;
            if (!BanditPlacement.TryResolve(caller, placement, profile.SpawnDistance,
                out BanditPlacement.Result placed, out string placementError))
            {
                UnturnedChat.Say(caller, placementError, Color.red);
                if (placement != null && !BanditPlacement.IsMarkerRequest(placement))
                {
                    ReplyUsage(caller, config);
                }
                return;
            }

            BanditSquadSpawner.Result spawn = BanditSquadSpawner.Spawn(config, profile, team,
                placed.Centre, placed.Right, placed.Forward, placed.Facing);

            if (spawn.Squad == null)
            {
                UnturnedChat.Say(caller, "Failed to spawn any of the squad - see server console.", Color.red);
                return;
            }

            UnturnedChat.Say(caller, $"Squad {spawn.Squad.Id} '{profile.TypeName}' up {placed.Range:0}m "
                + (placed.UsedMarker ? "at your marker" : "that way")
                + $": {string.Join(", ", spawn.Spawned.ToArray())}"
                + (team != null ? $", team {team.Label}" : ", no team")
                + (profile.WeaponsFree ? ", weapons free." : ", holding fire."), Color.green);

            if (spawn.Unknown.Count > 0)
            {
                UnturnedChat.Say(caller, $"Skipped unknown kit(s): {string.Join(", ", spawn.Unknown.ToArray())}. "
                    + $"Check the '{profile.TypeName}' squad's Members against /bandit kits.", Color.yellow);
            }
        }

        private static bool IsListRequest(string argument)
        {
            return argument.Equals("squads", System.StringComparison.OrdinalIgnoreCase)
                || argument.Equals("types", System.StringComparison.OrdinalIgnoreCase)
                || argument.Equals("list", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The types, who is in each and where it puts them. The figures are read off the resolved
        /// profile rather than the type, so one that leaves a number at -1 to inherit reports the
        /// distance it will actually spawn at rather than a -1 nobody can act on. Same reason
        /// "/bandit kits" resolves through BanditProfile.
        /// </summary>
        private static void ReplySquads(IRocketPlayer caller, BanditConfiguration config)
        {
            List<string> names = config.SquadNames();
            if (names.Count == 0)
            {
                UnturnedChat.Say(caller, "No squad types configured.", Color.red);
                return;
            }

            UnturnedChat.Say(caller, $"Squad types: {string.Join(", ", names.ToArray())}. "
                + $"'/squadspawn' alone puts down '{config.DefaultSquad}'.", Color.white);

            foreach (string name in names)
            {
                BanditSquadProfile profile = BanditSquadProfile.FromType(config, config.FindSquad(name));
                string members = profile.Members.Count > 0
                    ? string.Join(", ", profile.Members.ToArray())
                    : "nobody";

                UnturnedChat.Say(caller, $"  {name}: {members} - {config.SquadCost(config.FindSquad(name)):0} pts, "
                    + $"team {profile.Team}, "
                    + $"{profile.SpawnDistance:0}m out, "
                    + $"{profile.Spacing:0}m apart, holds contact {profile.ContactMemorySeconds:0}s, "
                    + $"repositions after {profile.RepositionAfterNoShotSeconds:0.#}s"
                    + (profile.WeaponsFree ? string.Empty : ", holding fire"), Color.grey);
            }
        }

        private static void ReplyUsage(IRocketPlayer caller, BanditConfiguration config)
        {
            UnturnedChat.Say(caller, "Usage: /squadspawn  |  /squadspawn <type>  |  "
                + "/squadspawn <type> <metres>  |  /squadspawn <type> marker  |  "
                + "/squadspawn <type> team:<team>  |  /squadspawn squads. "
                + $"Types: {string.Join(", ", config.SquadNames().ToArray())}. "
                + $"Teams: {string.Join(", ", BanditTeams.Names(config).ToArray())}.", Color.yellow);
        }

    }
}
