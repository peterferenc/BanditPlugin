using System;
using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Unturned.Chat;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditevent" - buys a fight with a budget and puts it down where you are looking.
    ///
    ///   /banditevent 200              200 points of bandits down your sightline
    ///   /banditevent 500 marker       at your map marker instead
    ///   /banditevent 300 400          400m out that way
    ///   /banditevent 250 team:red     on a particular side
    ///   /banditevent 500 seed:1234    the same 500-point event again, exactly
    ///   /banditevent check            what the configuration would let it draw, and what is wrong with it
    ///
    /// The number is a cost, not a difficulty level. Everything that can be spawned carries a price
    /// - a rifleman is the unit, and everything else is quoted against him - so the budget is spent
    /// on whole squads and crewed vehicles until nothing else is affordable, and the change is spent
    /// on individual men who join the last squad. What that buys is entirely a matter of what is in
    /// the configuration file: add a kit, a squad type or a vehicle with a price on it and the draw
    /// picks it up with no code involved. See <see cref="BanditEventDraw"/> for the three rules.
    ///
    /// Two points about what you get for a number. First, the same budget does not buy the same
    /// event twice - the draw is random, weighted by what the configuration says is ordinary. Second
    /// it is not meant to: a larger budget unlocks *kinds* of thing as well as quantities of them,
    /// so a big event brings marksmen and armour rather than a great many riflemen. If you want one
    /// event back, take its seed out of the reply and hand it to seed:.
    /// </summary>
    public class CommandBanditEvent : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "banditevent";
        public string Help => "Spawns a random event of a given points cost - squads and crewed vehicles.";
        public string Syntax => "<cost> [<metres>|marker] [team:<team>] [seed:<n>]";
        public List<string> Aliases => new List<string> { "event", "bevent" };
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        /// <summary>
        /// How far to the side of a vehicle its crew is spawned, and how far apart from each other.
        /// Clear of the hull, so nobody spawns inside it, and inside the driver's own search radius.
        /// </summary>
        private const float CrewSpawnOffset = 4f;

        /// <summary>Things placed per ring around the event's centre before starting another.</summary>
        private const int PlacementsPerRing = 6;

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;

            command = BanditTeams.ExtractTeamArgument(command, out string requestedTeam);
            command = ExtractSeedArgument(command, out int? requestedSeed);

            if (command.Length > 0 && IsCheckRequest(command[0]))
            {
                BanditEventCheck.Reply(caller, config);
                return;
            }

            // The two sub-commands. Both are "/banditevent" rather than commands of their own
            // because a convoy is an event - it is drawn against the same budget, out of the same
            // kits, squads and vehicles - and its route is the one thing that has to be set up
            // before one can be run.
            if (command.Length > 0 && IsWaypointRequest(command[0]))
            {
                BanditConvoyCommands.Waypoints(caller, Rest(command));
                return;
            }

            if (command.Length > 0 && command[0].Equals("convoy", StringComparison.OrdinalIgnoreCase))
            {
                BanditConvoyCommands.Convoy(caller, Rest(command), requestedTeam, requestedSeed);
                return;
            }

            if (command.Length == 0 || !float.TryParse(command[0], out float budget) || budget <= 0f)
            {
                ReplyUsage(caller, config);
                return;
            }

            BanditTeam team = BanditTeams.Find(config, !string.IsNullOrEmpty(requestedTeam)
                ? requestedTeam
                : config.DefaultTeam);

            if (team == null && !string.IsNullOrEmpty(requestedTeam))
            {
                UnturnedChat.Say(caller, $"No team called '{requestedTeam}'. Teams: "
                    + $"{string.Join(", ", BanditTeams.Names(config).ToArray())}.", Color.red);
                return;
            }

            // The configured cap and nothing else. An earlier version also clamped this to the
            // server's free player slots, on the reasoning that a bandit is a real client and must
            // therefore occupy one - which is wrong in the way that matters. FakePlayerSpawner goes
            // in through Provider.addPlayer by reflection, and the maxPlayers test lives further up
            // in the connection-accept path that a reflected spawn never touches. Bandits are
            // genuinely not bounded by the slot count, and clamping to it only made large events
            // quietly smaller on a busy server.
            int banditCap = Mathf.Max(0, config.EventMaxBandits);
            if (banditCap < 1)
            {
                UnturnedChat.Say(caller, $"EventMaxBandits is {config.EventMaxBandits} - "
                    + "no bandits can be spawned until it is raised.", Color.red);
                return;
            }

            int seed = requestedSeed ?? Environment.TickCount;
            BanditEventPlan plan = BanditEventDraw.Draw(config, budget, seed, banditCap);

            // Where the whole event goes. The same words /squadspawn takes, meaning the same things,
            // because they run through the same resolver - see BanditPlacement.
            string placement = command.Length > 1 ? command[1] : null;
            if (!BanditPlacement.TryResolve(caller, placement, config.SquadSpawnDistance,
                out BanditPlacement.Result placed, out string placementError))
            {
                UnturnedChat.Say(caller, placementError, Color.red);
                return;
            }

            BanditEvent banditEvent = BanditEvent.Create(budget);
            Spawn(config, plan, banditEvent, team, placed);

            Reply(caller, config, plan, banditEvent, placed, team);
        }

        /// <summary>
        /// Puts the plan on the ground: each squad and each vehicle at its own place around the
        /// centre, and the leftover men alongside the last squad.
        /// </summary>
        private static void Spawn(BanditConfiguration config, BanditEventPlan plan, BanditEvent banditEvent,
            BanditTeam team, BanditPlacement.Result placed)
        {
            int placement = 0;

            // Kept as the loose men's destination. It has to be the last *infantry* squad and the
            // spot that squad actually went to: the event's squad list also collects a squad per
            // vehicle crew, so taking the last entry would attach the remainder to a truck's crew
            // and spawn them at the truck.
            BanditSquad lastInfantrySquad = null;
            Vector3 lastInfantrySpot = placed.Centre;

            foreach (BanditSquadType type in plan.Squads)
            {
                Vector3 spot = PlacementSpot(placed, config.EventSpread, placement++);
                BanditSquadProfile profile = BanditSquadProfile.FromType(config, type);

                BanditSquadSpawner.Result result = BanditSquadSpawner.Spawn(config, profile, team,
                    spot, placed.Right, placed.Forward, placed.Facing);

                if (result.Squad != null)
                {
                    banditEvent.Squads.Add(result.Squad);
                    lastInfantrySquad = result.Squad;
                    lastInfantrySpot = spot;
                }
            }

            foreach (BanditVehicleType type in plan.Vehicles)
            {
                SpawnRide(config, banditEvent, team, placed, type, PlacementSpot(placed, config.EventSpread, placement++));
            }

            SpawnLoose(config, plan, banditEvent, team, placed, placement, lastInfantrySquad, lastInfantrySpot);

            banditEvent.Spent = plan.Spent;
        }

        /// <summary>
        /// The remainder: individual bandits added to the event's last infantry squad, so they
        /// arrive as part of it rather than as a scattering of loners with no shared contact between
        /// them.
        ///
        /// With no squad to join - a budget that bought only vehicles, or the one-man floor - they
        /// get a squad of their own on the global figures, placed like anything else in the event.
        /// </summary>
        private static void SpawnLoose(BanditConfiguration config, BanditEventPlan plan, BanditEvent banditEvent,
            BanditTeam team, BanditPlacement.Result placed, int placement, BanditSquad squad, Vector3 squadSpot)
        {
            if (plan.Loose.Count == 0)
            {
                return;
            }

            Vector3 centre;
            if (squad != null)
            {
                // Alongside the squad they are joining, set back from it so they are not standing in
                // the formation's own slots.
                centre = squadSpot - placed.Forward * config.SquadSpacing;
            }
            else
            {
                squad = BanditSquad.Create(BanditSquadProfile.FromConfiguration(config));
                banditEvent.Squads.Add(squad);
                centre = PlacementSpot(placed, config.EventSpread, placement);
            }

            for (int i = 0; i < plan.Loose.Count; i++)
            {
                Vector3 slot = BanditPlacement.FormationSlot(centre, placed.Right, placed.Forward,
                    i, plan.Loose.Count, config.SquadSpacing);

                BanditSquadSpawner.SpawnMember(plan.Loose[i], team, slot, placed.Facing, squad, weaponsFree: true);
            }
        }

        /// <summary>
        /// Spawns one vehicle and the men who ride in it.
        ///
        /// The crew is spawned on the ground beside the vehicle and then *asked* for its seats
        /// rather than placed into them: vanilla will not seat anyone whose equip animation is still
        /// running, and a bandit one tick old is doing exactly that. See
        /// BanditBotController.RequestSeat, which keeps trying. A man who never gets in is left
        /// standing beside the vehicle, armed and in the squad, which is a perfectly serviceable
        /// outcome.
        /// </summary>
        internal static void SpawnRide(BanditConfiguration config, BanditEvent banditEvent, BanditTeam team,
            BanditPlacement.Result placed, BanditVehicleType type, Vector3 spot, int crewLimit = int.MaxValue)
        {
            InteractableVehicle vehicle = BanditVehicleSpawner.Spawn(type.Vehicle, spot, placed.Facing,
                out string vehicleError);

            if (vehicle == null)
            {
                Rocket.Core.Logging.Logger.LogWarning($"[Bandit] Event {banditEvent.Id}: could not spawn "
                    + $"'{type.Name}' - {vehicleError}. Check its Vehicle setting with /banditevent check.");
                return;
            }

            BanditEvent.Ride ride = new BanditEvent.Ride
            {
                Vehicle = vehicle,
                TypeName = type.Name,
                DriveAtCaller = type.DriveAtCaller,
                DriverDismounts = type.DriverDismounts,
                DismountRange = Mathf.Max(0f, type.DismountRange),
                EngageRange = Mathf.Max(0f, type.EngageRange),
                ContactTriggerRange = Mathf.Max(0f, type.ContactTriggerRange),
                // Where the caller was standing, not where they are now: the event was placed
                // relative to that spot, and a destination that chases a moving player would make
                // the arrival - and so the dismount - something that may never happen.
                Destination = placed.Origin
            };

            // One squad per vehicle, so its crew shares contact with each other the moment they are
            // out. Named after the vehicle rather than a squad type, since there isn't one.
            BanditSquadProfile profile = BanditSquadProfile.FromConfiguration(config);
            profile.TypeName = type.Name;
            BanditSquad squad = BanditSquad.Create(profile);

            List<BanditVehicleSeat> crew = type.Crew ?? new List<BanditVehicleSeat>();
            string turretSeats = BanditVehicleSpawner.DescribeTurretSeats(vehicle.asset);

            // Seats are filled in the order they are configured, and seat 0 - the driver - is
            // first, so a limit of one is a vehicle with nobody in it but the man driving it.
            for (int i = 0; i < crew.Count && i < crewLimit; i++)
            {
                BanditVehicleSeat seat = crew[i];
                BanditKit kit = seat != null ? config.FindKit(seat.Kit) : null;
                if (kit == null)
                {
                    continue;
                }

                Vector3 slot = BanditPlacement.SnapToGround(
                    spot + placed.Right * (CrewSpawnOffset + i * 1.5f));

                BanditBotController crewman = BanditSquadSpawner.SpawnMember(kit, team, slot, placed.Facing,
                    squad, weaponsFree: true);

                if (crewman == null)
                {
                    continue;
                }

                // A turret crewman needs to know it is one, so it tracks and engages from the seat.
                // Read off the asset rather than configured: the seat a turret is on is a fact about
                // the vehicle, and asking people to restate it correctly is asking for silent
                // passengers.
                bool isGunner = IsTurretSeat(vehicle.asset, seat.Seat);
                crewman.RequestSeat(vehicle, seat.Seat, isGunner);

                if (seat.Seat == BanditVehicleDriver.DriverSeat)
                {
                    ride.Driver = crewman;
                }
                else if (isGunner)
                {
                    // Never dismounted: this man is the reason an armed vehicle is worth more than
                    // the sum of the men inside it.
                    ride.Gunners.Add(crewman);
                }
                else
                {
                    ride.Riders.Add(crewman);
                }
            }

            if (ride.Driver == null && ride.DriveAtCaller)
            {
                // Nothing to drive it. Left as a static position rather than pretending, so the
                // director does not sit watching for an arrival that cannot happen.
                ride.DriveAtCaller = false;
                Rocket.Core.Logging.Logger.Log($"[Bandit] Event {banditEvent.Id}: '{type.Name}' has no seat 0 "
                    + "crewman, so it stays where it spawned."
                    + (turretSeats != null ? $" Its turret seats are {turretSeats}." : string.Empty));
            }

            banditEvent.Rides.Add(ride);

            // Only if anybody actually got aboard: a vehicle spawned empty, or one whose crew all
            // failed to spawn, would otherwise leave a memberless squad in the event's list for the
            // reply to count.
            if (squad.Members.Count > 0)
            {
                banditEvent.Squads.Add(squad);
            }
        }

        private static bool IsTurretSeat(VehicleAsset asset, byte seat)
        {
            if (asset?.turrets == null)
            {
                return false;
            }

            foreach (TurretInfo turret in asset.turrets)
            {
                if (turret != null && turret.seatIndex == seat)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Where the nth thing in an event goes: the first at the centre, the rest on rings around
        /// it.
        ///
        /// Spread out rather than stacked because an event is several separate problems arriving
        /// from slightly different places - two squads spawned on one spot are one crowd, which is
        /// both less interesting to fight and a reliable way to have them shoot each other in the
        /// back. The ring is turned a little on each lap so a second one does not line up behind the
        /// first.
        /// </summary>
        private static Vector3 PlacementSpot(BanditPlacement.Result placed, float spread, int index)
        {
            if (index == 0)
            {
                return placed.Centre;
            }

            int ring = 1 + (index - 1) / PlacementsPerRing;
            int positionInRing = (index - 1) % PlacementsPerRing;

            float degrees = positionInRing * (360f / PlacementsPerRing) + ring * 23f;
            float radians = degrees * Mathf.Deg2Rad;

            Vector3 offset = placed.Forward * Mathf.Cos(radians) + placed.Right * Mathf.Sin(radians);
            return BanditPlacement.SnapToGround(placed.Centre + offset * (spread * ring));
        }

        private static void Reply(IRocketPlayer caller, BanditConfiguration config, BanditEventPlan plan,
            BanditEvent banditEvent, BanditPlacement.Result placed, BanditTeam team)
        {
            int spawned = banditEvent.BanditCount;

            List<string> bought = new List<string>();
            foreach (BanditSquadType squad in plan.Squads)
            {
                bought.Add(squad.Name);
            }
            foreach (BanditVehicleType vehicle in plan.Vehicles)
            {
                bought.Add(vehicle.Name);
            }
            if (plan.Loose.Count > 0)
            {
                bought.Add($"+{plan.Loose.Count} loose");
            }

            UnturnedChat.Say(caller, $"Event {banditEvent.Id}: {plan.Spent:0} of {plan.Budget:0} pts, "
                + $"{spawned} bandit(s), {banditEvent.Rides.Count} vehicle(s), {placed.Range:0}m "
                + (placed.UsedMarker ? "at your marker" : "that way")
                + (team != null ? $", team {team.Label}" : ", no team") + ".", Color.green);

            UnturnedChat.Say(caller, $"  Drew: {(bought.Count > 0 ? string.Join(", ", bought.ToArray()) : "nothing")}. "
                + $"Seed {plan.Seed} - /banditevent {plan.Budget:0} seed:{plan.Seed} runs it again.", Color.grey);

            if (plan.Spent > plan.Budget)
            {
                // The floor deliberately overspends rather than reporting an empty event, and
                // "12 of 5 pts" reads as arithmetic gone wrong unless it says so.
                UnturnedChat.Say(caller, $"{plan.Budget:0} pts buys nothing at these prices - "
                    + "spawned one bandit as the minimum.", Color.grey);
            }
            else if (plan.LimitedByBanditCap)
            {
                UnturnedChat.Say(caller, $"Stopped at the bandit ceiling of {config.EventMaxBandits}, "
                    + $"not the budget - {plan.Unspent:0} pts unspent. Raise EventMaxBandits to spend it.",
                    Color.yellow);
            }
            else if (plan.Unspent >= SmallestCost(config) && plan.Unspent > 0f)
            {
                UnturnedChat.Say(caller, $"{plan.Unspent:0} pts unspent - nothing left was both affordable "
                    + "and allowed at this size. /banditevent check shows the floors.", Color.grey);
            }
        }

        /// <summary>The cheapest thing the configuration can buy, for deciding whether leftover
        /// budget is worth mentioning at all.</summary>
        private static float SmallestCost(BanditConfiguration config)
        {
            float cheapest = float.MaxValue;
            if (config.Kits != null)
            {
                foreach (BanditKit kit in config.Kits)
                {
                    float cost = BanditConfiguration.CostOf(kit);
                    if (cost > 0f && cost < cheapest)
                    {
                        cheapest = cost;
                    }
                }
            }

            return cheapest == float.MaxValue ? 0f : cheapest;
        }

        /// <summary>
        /// Pulls "seed:1234" out of the words, wherever it sits, the same way team: is pulled out -
        /// everything else this command takes is positional and a bare number could not be told from
        /// the budget or the distance.
        /// </summary>
        private static string[] ExtractSeedArgument(string[] command, out int? seed)
        {
            seed = null;
            if (command == null || command.Length == 0)
            {
                return command ?? new string[0];
            }

            List<string> remaining = new List<string>(command.Length);
            foreach (string word in command)
            {
                if (word != null && word.Length > 5
                    && word.StartsWith("seed", StringComparison.OrdinalIgnoreCase)
                    && (word[4] == ':' || word[4] == '=')
                    && int.TryParse(word.Substring(5), out int parsed))
                {
                    seed = parsed;
                    continue;
                }

                remaining.Add(word);
            }

            return remaining.ToArray();
        }

        private static bool IsWaypointRequest(string argument)
        {
            return argument.Equals("wp", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("waypoint", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("waypoints", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Everything after the sub-command word.</summary>
        private static string[] Rest(string[] command)
        {
            if (command.Length < 2)
            {
                return new string[0];
            }

            string[] rest = new string[command.Length - 1];
            Array.Copy(command, 1, rest, 0, rest.Length);
            return rest;
        }

        private static bool IsCheckRequest(string argument)
        {
            return argument.Equals("check", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("validate", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("list", StringComparison.OrdinalIgnoreCase);
        }

        private static void ReplyUsage(IRocketPlayer caller, BanditConfiguration config)
        {
            UnturnedChat.Say(caller, "Usage: /banditevent <cost>  |  /banditevent <cost> <metres>  |  "
                + "/banditevent <cost> marker  |  /banditevent <cost> team:<team> seed:<n>  |  "
                + "/banditevent check  |  /banditevent wp  |  /banditevent convoy <cost>.", Color.yellow);
            UnturnedChat.Say(caller, "The number is a points budget, not a difficulty. "
                + $"A '{config.DefaultKit}' costs {BanditConfiguration.CostOf(config.FindKit(config.DefaultKit)):0}; "
                + "/banditevent check prices everything.", Color.grey);
        }
    }
}
