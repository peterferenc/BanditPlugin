using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/bandit info kits|squads|vehicles|event" - what the configuration offers to spawn.
    ///
    /// The names each spawn command takes, and what they mean once resolved: a kit's figures with
    /// the global ones folded in, a squad type's members and distances, a vehicle's asset and crew.
    /// "event" is the full price list an event draws from, with everything wrong with it.
    /// </summary>
    public static class BanditInfoCommand
    {
        public static void Execute(IRocketPlayer caller, string[] args)
        {
            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;
            string topic = args.Length > 0 ? args[0].ToLowerInvariant() : null;

            switch (topic)
            {
                case "kits":
                    BanditSpawnCommand.ReplyKits(caller, config);
                    break;

                case "squads":
                    BanditSquadSpawnCommand.ReplySquads(caller, config);
                    break;

                case "vehicles":
                    ReplyVehicles(caller, config);
                    break;

                case "event":
                    BanditEventCheck.Reply(caller, config);
                    break;

                default:
                    Reply(caller, "Usage: /bandit info kits|squads|vehicles|event", Color.yellow);
                    break;
            }
        }

        /// <summary>
        /// The configured vehicle types, the asset each resolves to on this map, and who rides
        /// where. The turret seats are read off the asset, because a crewman configured into a seat
        /// with no gun on it is a passenger, and there is no way to see that from in game.
        /// </summary>
        private static void ReplyVehicles(IRocketPlayer caller, BanditConfiguration config)
        {
            List<string> names = config.VehicleNames();
            if (names.Count == 0)
            {
                Reply(caller, "No vehicles configured.", Color.red);
                return;
            }

            Reply(caller, $"Vehicles: {string.Join(", ", names.ToArray())}. "
                + "/bandit vehicle spawn also takes a raw ID or GUID.", Color.white);

            foreach (string name in names)
            {
                BanditVehicleType type = config.FindVehicle(name);
                VehicleAsset asset = BanditVehicleSpawner.Resolve(type.Vehicle, out string error);
                string turrets = BanditVehicleSpawner.DescribeTurretSeats(asset);

                List<string> crew = new List<string>();
                foreach (BanditVehicleSeat seat in type.Crew ?? new List<BanditVehicleSeat>())
                {
                    if (seat != null)
                    {
                        crew.Add($"seat {seat.Seat} {seat.Kit}");
                    }
                }

                Reply(caller, $"  {name}: "
                    + (asset != null ? asset.FriendlyName : $"UNRESOLVED ({error})")
                    + $", {config.VehicleCost(type):0.#} pts with crew"
                    + $", crew {(crew.Count > 0 ? string.Join(", ", crew.ToArray()) : "none")}"
                    + (turrets != null ? $", turret seat(s) {turrets}" : ", no turret"), Color.grey);
            }
        }
    }
}
