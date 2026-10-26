using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/bandit vehicle spawn &lt;type&gt; [&lt;metres&gt;|marker] [team:&lt;team&gt;]" - one
    /// crewed vehicle, the way an event puts one down.
    ///
    /// A type from the configured Vehicles list arrives with that type's crew and behaves exactly
    /// as it would in an event: it waits for contact, then drives at it and either unloads or digs
    /// in and fights. Anything else is taken as a legacy ID or GUID, which is how a vehicle gets
    /// tried before it goes in the configuration - it arrives with a default-kit driver and a
    /// gunner in every turret seat the asset has, and holds where it spawned, ready for orders.
    ///
    /// Underneath it is a one-vehicle event, so it is cleared, listed and ordered about like one -
    /// but it is remembered as the vehicle, so the orders after it go to its crew.
    /// </summary>
    public static class BanditVehicleSpawnCommand
    {
        public static void Execute(IRocketPlayer caller, string[] args)
        {
            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;

            if (RequirePlayer(caller, "Spawning a vehicle") == null)
            {
                return;
            }

            args = BanditTeams.ExtractTeamArgument(args, out string requestedTeam);

            if (args.Length == 0)
            {
                Reply(caller, "Usage: /bandit vehicle spawn <type|id|GUID> [<metres>|marker] [team:<team>]. "
                    + $"Types: {string.Join(", ", config.VehicleNames().ToArray())}.", Color.yellow);
                return;
            }

            if (!TryResolveType(config, args[0], out BanditVehicleType type, out string error))
            {
                Reply(caller, error, Color.red);
                return;
            }

            BanditTeam team = BanditTeams.Find(config, !string.IsNullOrEmpty(requestedTeam)
                ? requestedTeam
                : config.DefaultTeam);

            if (team == null && !string.IsNullOrEmpty(requestedTeam))
            {
                Reply(caller, $"No team called '{requestedTeam}'. Teams: "
                    + $"{string.Join(", ", BanditTeams.Names(config).ToArray())}.", Color.red);
                return;
            }

            string placement = args.Length > 1 ? args[1] : null;
            float distance = Mathf.Max(BanditPlacement.MinimumSpawnDistance, config.VehicleSpawnDistance);
            if (!BanditPlacement.TryResolve(caller, placement, distance,
                    out BanditPlacement.Result placed, out string placementError))
            {
                Reply(caller, placementError, Color.red);
                return;
            }

            BanditEvent banditEvent = BanditEvent.Create(config.VehicleCost(type));
            BanditEvent.Ride ride = BanditEventSpawnCommand.SpawnRide(config, banditEvent, team, placed, type,
                placed.Centre);

            if (ride == null)
            {
                BanditEvent.All.Remove(banditEvent);
                Reply(caller, $"Could not spawn '{type.Name}' - see the server console.", Color.red);
                return;
            }

            BanditTargets.Remember(BanditTarget.Of(ride));

            string turrets = BanditVehicleSpawner.DescribeTurretSeats(ride.Vehicle.asset);
            Reply(caller, $"Vehicle {ride.Id} ({type.Name}): {ride.Vehicle.asset.FriendlyName} {placed.Range:0}m "
                + (placed.UsedMarker ? "at your marker" : "that way")
                + $", crew {DescribeMembers(BanditEvent.CrewOf(ride))}"
                + (team != null ? $", team {team.Label}" : ", no team")
                + (turrets != null ? $", turret seat(s) {turrets}." : ", no turret."), Color.green);

            Reply(caller, ride.DriveAtCaller
                ? "  Waits for contact, then drives at it. 'drive' takes it off that script and gives you the wheel."
                : "  Holds where it spawned. 'drive' sends it somewhere.", Color.grey);
        }

        /// <summary>
        /// A configured type by name, or - for anything else that names a vehicle asset - a
        /// stand-in type with a driver and a gunner per turret seat, so a vehicle can be tried
        /// before anybody has written its crew down.
        /// </summary>
        private static bool TryResolveType(BanditConfiguration config, string requested,
            out BanditVehicleType type, out string error)
        {
            type = config.FindVehicle(requested);
            error = null;
            if (type != null)
            {
                return true;
            }

            VehicleAsset asset = BanditVehicleSpawner.Resolve(requested, out string resolveError);
            if (asset == null)
            {
                error = $"Cannot spawn '{requested}': {resolveError}. "
                    + $"Configured vehicles: {string.Join(", ", config.VehicleNames().ToArray())}.";
                return false;
            }

            List<BanditVehicleSeat> crew = new List<BanditVehicleSeat>
            {
                new BanditVehicleSeat { Seat = BanditVehicleDriver.DriverSeat, Kit = config.DefaultKit }
            };

            if (asset.turrets != null)
            {
                foreach (TurretInfo turret in asset.turrets)
                {
                    if (turret != null && turret.seatIndex != BanditVehicleDriver.DriverSeat)
                    {
                        crew.Add(new BanditVehicleSeat { Seat = turret.seatIndex, Kit = config.DefaultKit });
                    }
                }
            }

            type = new BanditVehicleType
            {
                Name = asset.FriendlyName,
                Vehicle = requested,
                Crew = crew,
                DriveAtCaller = false
            };
            return true;
        }
    }
}
