using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.FakePlayer
{
    /// <summary>
    /// Puts one squad on the ground in formation, switched on.
    ///
    /// Lifted out of "/squadspawn" when "/banditevent" needed to do the same thing several times
    /// over. Everything the two commands share is here - the formation, the team prefix on the
    /// name, the weapons-free flag - so an event's squads are the same squads /squadspawn produces
    /// rather than a second implementation that drifts.
    /// </summary>
    public static class BanditSquadSpawner
    {
        /// <summary>What came of a spawn, so the caller can report it.</summary>
        public struct Result
        {
            /// <summary>The squad, or null when not one member could be spawned.</summary>
            public BanditSquad Squad;

            /// <summary>The kit names actually put on the ground, in order.</summary>
            public List<string> Spawned;

            /// <summary>Members naming a kit that does not exist. Skipped, not fatal.</summary>
            public List<string> Unknown;
        }

        /// <summary>
        /// Spawns <paramref name="profile"/>'s members abreast around <paramref name="centre"/>.
        ///
        /// An unknown kit costs a man rather than the squad, and a member that fails to spawn is
        /// passed over silently - the server console already carries the reason from the spawner,
        /// and a half-strength squad is still worth watching.
        /// </summary>
        public static Result Spawn(BanditConfiguration config, BanditSquadProfile profile, BanditTeam team,
            Vector3 centre, Vector3 right, Vector3 forward, float facing)
        {
            Result result = new Result
            {
                Spawned = new List<string>(),
                Unknown = new List<string>()
            };

            List<string> composition = profile.Members;
            if (composition == null || composition.Count == 0)
            {
                return result;
            }

            BanditSquad squad = BanditSquad.Create(profile);

            for (int i = 0; i < composition.Count; i++)
            {
                BanditKit kit = config.FindKit(composition[i]);
                if (kit == null)
                {
                    result.Unknown.Add(composition[i]);
                    continue;
                }

                Vector3 slot = BanditPlacement.FormationSlot(centre, right, forward, i, composition.Count,
                    profile.Spacing);

                if (SpawnMember(kit, team, slot, facing, squad, profile.WeaponsFree) != null)
                {
                    result.Spawned.Add(kit.Name);
                }
            }

            result.Squad = result.Spawned.Count > 0 ? squad : null;
            return result;
        }

        /// <summary>
        /// Spawns one bandit into an existing squad - the event's remainder spend, and a vehicle's
        /// crew, both of which add men to a squad one at a time rather than laying out a formation.
        /// </summary>
        public static BanditBotController SpawnMember(BanditKit kit, BanditTeam team, Vector3 position,
            float facing, BanditSquad squad, bool weaponsFree)
        {
            // The team in the name, not just the class: with two sides on the ground, which side a
            // man is on is the first thing you need to read off him through a scope.
            string displayName = team != null ? $"{team.Label} {kit.Name}" : $"Bandit {kit.Name}";

            Player bandit = FakePlayerSpawner.Spawn(position, facing, displayName, kit, team);
            if (bandit == null)
            {
                return null;
            }

            BanditBotController controller = FakePlayerSpawner.LastSpawnedController;
            if (controller == null)
            {
                return null;
            }

            squad?.Add(controller);

            if (weaponsFree)
            {
                controller.HoldFire = false;
            }

            return controller;
        }
    }
}
