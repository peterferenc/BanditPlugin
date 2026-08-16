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

        /// <summary>
        /// How high above a slot the ground probe starts, and how far down it looks. Generous in
        /// both directions because a slot 200m away is worked out from a flat bearing and can
        /// easily land well above or below the caller's own height.
        /// </summary>
        private const float GroundProbeHeight = 300f;
        private const float GroundProbeDepth = 1200f;

        /// <summary>Nearest a squad may be placed, so "/squadspawn 0" cannot drop one on your head.</summary>
        private const float MinimumSpawnDistance = 15f;

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
            if (command.Length > 0 && !IsMarkerRequest(command[0]) && !float.TryParse(command[0], out _))
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

            Player callerPlayer = ((UnturnedPlayer)caller).Player;
            Vector3 origin = callerPlayer.transform.position;

            // Where the squad goes: your map marker, a distance you name, or the type's own down
            // your sightline.
            bool useMarker = command.Length > placementArgument && IsMarkerRequest(command[placementArgument]);

            float distance = profile.SpawnDistance;
            if (!useMarker && command.Length > placementArgument)
            {
                if (!float.TryParse(command[placementArgument], out distance))
                {
                    ReplyUsage(caller, config);
                    return;
                }

                distance = Mathf.Max(distance, MinimumSpawnDistance);
            }

            // Taken from the aim transform, which is where the player is actually looking, and not
            // from the body transform - that only carries the yaw of the last input packet the
            // server processed for them, so a squad placed off it lands wherever the model happens
            // to be facing rather than down the caller's sightline. /bandit already spawns off the
            // aim for the same reason.
            //
            // Flattened, because the formation is laid out on the ground; a caller looking at their
            // boots would otherwise squash it into a point, hence the fallback.
            Vector3 forward = callerPlayer.look != null && callerPlayer.look.aim != null
                ? callerPlayer.look.aim.forward
                : callerPlayer.transform.forward;

            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = callerPlayer.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.0001f)
                {
                    forward = Vector3.forward;
                }
            }
            forward.Normalize();
            Vector3 right = new Vector3(forward.z, 0f, -forward.x);

            Vector3 centre;
            if (useMarker)
            {
                if (callerPlayer.quests == null || !callerPlayer.quests.isMarkerPlaced)
                {
                    UnturnedChat.Say(caller, "No map marker placed - put one down first, or use /squadspawn <metres>.", Color.red);
                    return;
                }

                centre = callerPlayer.quests.markerPosition;

                // A marker is a point on a flat map: its height is whatever the client happened to
                // send and means nothing on the ground. The bearing is recomputed from it too, so
                // the squad still faces the way you will be coming from.
                Vector3 fromMarker = origin - centre;
                fromMarker.y = 0f;
                if (fromMarker.sqrMagnitude > 0.0001f)
                {
                    forward = -fromMarker.normalized;
                    right = new Vector3(forward.z, 0f, -forward.x);
                }
            }
            else
            {
                centre = origin + forward * distance;
            }

            centre = SnapToGround(centre);
            if (float.IsNaN(centre.x) || float.IsNaN(centre.z))
            {
                UnturnedChat.Say(caller, "Could not work out where that is.", Color.red);
                return;
            }

            // Facing the caller: they are the reason the squad is here, and it saves a bandit
            // spending its first second turning round before it can see anything.
            float facing = Mathf.Atan2(-forward.x, -forward.z) * Mathf.Rad2Deg;

            BanditSquad squad = BanditSquad.Create(profile);
            List<string> spawned = new List<string>();
            List<string> unknown = new List<string>();

            for (int i = 0; i < composition.Count; i++)
            {
                string kitName = composition[i];
                BanditKit kit = config.FindKit(kitName);
                if (kit == null)
                {
                    unknown.Add(kitName);
                    continue;
                }

                Vector3 slot = FormationSlot(centre, right, forward, i, composition.Count, profile.Spacing);

                // The team in the name, not just the class: with two sides on the ground, which
                // side a man is on is the first thing you need to read off him through a scope.
                string displayName = team != null ? $"{team.Label} {kit.Name}" : $"Bandit {kit.Name}";
                Player bandit = FakePlayerSpawner.Spawn(slot, facing, displayName, kit, team);
                if (bandit == null)
                {
                    continue;
                }

                BanditBotController controller = FakePlayerSpawner.LastSpawnedController;
                if (controller == null)
                {
                    continue;
                }

                squad.Add(controller);

                if (profile.WeaponsFree)
                {
                    controller.HoldFire = false;
                }

                spawned.Add(kit.Name);
            }

            if (spawned.Count == 0)
            {
                UnturnedChat.Say(caller, "Failed to spawn any of the squad - see server console.", Color.red);
                return;
            }

            float placedRange = Vector3.Distance(origin, centre);
            UnturnedChat.Say(caller, $"Squad {squad.Id} '{profile.TypeName}' up {placedRange:0}m "
                + (useMarker ? "at your marker" : "that way")
                + $": {string.Join(", ", spawned.ToArray())}"
                + (team != null ? $", team {team.Label}" : ", no team")
                + (profile.WeaponsFree ? ", weapons free." : ", holding fire."), Color.green);

            if (unknown.Count > 0)
            {
                UnturnedChat.Say(caller, $"Skipped unknown kit(s): {string.Join(", ", unknown.ToArray())}. "
                    + $"Check the '{profile.TypeName}' squad's Members against /bandit kits.", Color.yellow);
            }
        }

        /// <summary>
        /// The two words the command took as a placement before squad types existed, kept working
        /// so "/squadspawn marker" is still a marker rather than a lookup for a squad called that.
        /// </summary>
        private static bool IsMarkerRequest(string argument)
        {
            return argument.Equals("marker", System.StringComparison.OrdinalIgnoreCase)
                || argument.Equals("map", System.StringComparison.OrdinalIgnoreCase);
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

                UnturnedChat.Say(caller, $"  {name}: {members} - team {profile.Team}, "
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

        /// <summary>
        /// Where one member stands: spread along the line abreast, with the flanks set back into a
        /// shallow wedge.
        ///
        /// The setback is not decoration. A squad on one straight line has every member in every
        /// other member's field of fire the moment they all turn to face the same threat, and the
        /// friendly-fire check in the controller would then spend the fight refusing to let the
        /// flanks shoot. Staggering the line by half the spacing per step out from the centre gives
        /// each of them an angle past the others.
        /// </summary>
        private static Vector3 FormationSlot(Vector3 centre, Vector3 right, Vector3 forward,
            int index, int count, float spacing)
        {
            float lateral = (index - (count - 1) * 0.5f) * spacing;
            float setback = Mathf.Abs(index - (count - 1) * 0.5f) * spacing * 0.5f;

            Vector3 slot = centre + right * lateral - forward * setback;
            return SnapToGround(slot);
        }

        /// <summary>
        /// Drops a formation slot onto whatever is under it. Without this a squad laid out across
        /// sloping ground spawns partly buried and partly in mid-air, and a bandit that starts
        /// inside the terrain never gets its feet under it.
        ///
        /// It probes from far overhead rather than from just above the slot, because a squad placed
        /// two hundred metres away is worked out from a flat bearing and its ground can be a long
        /// way above or below the caller's own.
        ///
        /// BLOCK_COLLISION is the mask the existing single spawn uses, so a slot landing on a roof
        /// or a rock puts the bandit on top of it rather than under it. A probe that hits nothing
        /// keeps the caller's own height, which is the best guess available.
        /// </summary>
        private static Vector3 SnapToGround(Vector3 slot)
        {
            Vector3 probeOrigin = slot + Vector3.up * GroundProbeHeight;
            if (Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit hit, GroundProbeDepth, RayMasks.BLOCK_COLLISION))
            {
                return hit.point;
            }

            return slot;
        }
    }
}
