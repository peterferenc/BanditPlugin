using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Unturned.Chat;
using SDG.Unturned;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin
{
    /// <summary>
    /// Reads the event configuration back and says what is wrong with it.
    ///
    /// Worth its own file because of what "everything is configurable" costs: when the draw is
    /// driven entirely by prices, floors and weights in an XML file, a typo does not produce an
    /// error - it produces a strange event. A kit priced at 0 is unbuyable, a weight of 0 is a squad
    /// type that silently never appears, a GUID off by one character is a vehicle that never spawns,
    /// and a squad larger than the bandit ceiling is content that cannot be drawn at any budget. All
    /// four look identical from in game: an event that felt thin.
    ///
    /// A high MinEventCost is deliberately *not* on that list. The budget is whatever somebody
    /// types, so there is no floor a bigger number cannot reach - the only genuine ceiling is on
    /// men, not on points.
    ///
    /// So "/banditevent check" prices everything the draw can see and lists what cannot be drawn and
    /// why. The structural half of the same pass runs at load and goes to the console.
    /// </summary>
    public static class BanditEventCheck
    {
        /// <summary>
        /// The full report, in chat: what everything costs, and every problem found.
        ///
        /// Run from in game rather than only at load because half of it cannot be answered any
        /// earlier - whether a vehicle GUID resolves depends on which map is running, and that is
        /// exactly the mistake most worth catching.
        /// </summary>
        public static void Reply(IRocketPlayer caller, BanditConfiguration config)
        {
            List<string> problems = new List<string>();
            Collect(config, problems, checkAssets: true);

            UnturnedChat.Say(caller, "Kits - what a man costs, the smallest event he appears in, "
                + "and how often he is drawn:", Color.white);
            foreach (BanditKit kit in config.Kits ?? new List<BanditKit>())
            {
                if (kit == null || string.IsNullOrEmpty(kit.Name))
                {
                    continue;
                }

                UnturnedChat.Say(caller, $"  {kit.Name}: {kit.Cost:0.#} pts"
                    + (kit.MinEventCost > 0f ? $", from {kit.MinEventCost:0} up" : ", any size")
                    + $", weight {kit.Weight:0.#}"
                    + (kit.Weight <= 0f ? " (never drawn)" : string.Empty), Color.grey);
            }

            UnturnedChat.Say(caller, "Squads - cost is the sum of the members, so it follows them:", Color.white);
            foreach (BanditSquadType type in config.Squads ?? new List<BanditSquadType>())
            {
                if (type == null || string.IsNullOrEmpty(type.Name))
                {
                    continue;
                }

                UnturnedChat.Say(caller, $"  {type.Name}: {config.SquadCost(type):0.#} pts"
                    + (type.MinEventCost > 0f ? $", from {type.MinEventCost:0} up" : ", any size")
                    + $", weight {type.Weight:0.#}"
                    + (type.Weight <= 0f ? " (never drawn)" : string.Empty), Color.grey);
            }

            UnturnedChat.Say(caller, "Vehicles - platform pts + crew, and where the turrets are:", Color.white);
            foreach (BanditVehicleType type in config.Vehicles ?? new List<BanditVehicleType>())
            {
                if (type == null || string.IsNullOrEmpty(type.Name))
                {
                    continue;
                }

                VehicleAsset asset = BanditVehicleSpawner.Resolve(type.Vehicle, out string assetError);
                string turrets = BanditVehicleSpawner.DescribeTurretSeats(asset);

                UnturnedChat.Say(caller, $"  {type.Name}: {type.Cost:0.#} + crew = "
                    + $"{config.VehicleCost(type):0.#} pts, from {type.MinEventCost:0} up, "
                    + $"weight {type.Weight:0.#} - "
                    + (asset != null ? asset.FriendlyName : $"UNRESOLVED ({assetError})")
                    + (turrets != null ? $", turret seat(s) {turrets}" : ", no turret")
                    + (type.DriveAtCaller ? ", drives in and unloads" : ", holds position"), Color.grey);
            }

            UnturnedChat.Say(caller, $"Limits: at most {config.EventVehicleCap} vehicle(s) and "
                + $"{config.EventMaxBandits} bandit(s) per event, {config.EventSpread:0}m apart. "
                + $"{Provider.maxPlayers - Provider.clients.Count} player slot(s) free right now.", Color.white);

            if (problems.Count == 0)
            {
                UnturnedChat.Say(caller, "No problems found.", Color.green);
                return;
            }

            UnturnedChat.Say(caller, $"{problems.Count} problem(s):", Color.yellow);
            foreach (string problem in problems)
            {
                UnturnedChat.Say(caller, $"  {problem}", Color.yellow);
            }
        }

        /// <summary>
        /// The half of the report that can be answered without a map loaded, logged once at start so
        /// a broken configuration is noticed before anybody types a command.
        ///
        /// Vehicle assets are deliberately not resolved here. Whether a GUID is present depends on
        /// which map and which workshop content the server ended up loading, and warning about that
        /// before it has happened would be crying wolf every start.
        /// </summary>
        public static void LogProblems(BanditConfiguration config)
        {
            List<string> problems = new List<string>();
            Collect(config, problems, checkAssets: false);

            foreach (string problem in problems)
            {
                Logger.LogWarning($"[Bandit] Event configuration: {problem}");
            }

            if (problems.Count > 0)
            {
                Logger.LogWarning($"[Bandit] {problems.Count} event configuration problem(s) - "
                    + "run /banditevent check in game for the full report, including vehicle assets.");
            }
        }

        private static void Collect(BanditConfiguration config, List<string> problems, bool checkAssets)
        {
            foreach (BanditKit kit in config.Kits ?? new List<BanditKit>())
            {
                if (kit == null || string.IsNullOrEmpty(kit.Name))
                {
                    continue;
                }

                // The one that would hang the draw rather than merely disappoint it: a free man can
                // be bought out of any budget, however much of it has already been spent. The draw
                // refuses to consider anything priced at zero for exactly that reason, so the effect
                // here is only a kit that never appears - but it is worth saying which of the two
                // mistakes was made, because "0 means free" and "0 means excluded" read the same in
                // the file.
                if (kit.Cost <= 0f)
                {
                    problems.Add($"kit '{kit.Name}' costs {kit.Cost:0.#} - must be above 0, so it is "
                        + "excluded from events entirely. A free kit could be bought without limit.");
                }
            }

            foreach (BanditSquadType type in config.Squads ?? new List<BanditSquadType>())
            {
                if (type == null || string.IsNullOrEmpty(type.Name))
                {
                    continue;
                }

                if (type.Members == null || type.Members.Count == 0)
                {
                    problems.Add($"squad '{type.Name}' has no members.");
                    continue;
                }

                foreach (string member in type.Members)
                {
                    if (config.FindKit(member) == null)
                    {
                        problems.Add($"squad '{type.Name}' names kit '{member}', which does not exist - "
                            + "that member is skipped at spawn.");
                    }
                }

                if (config.SquadCost(type) <= 0f)
                {
                    problems.Add($"squad '{type.Name}' costs nothing, because none of its members "
                        + "resolve to a priced kit - it can never be drawn.");
                }
            }

            foreach (BanditVehicleType type in config.Vehicles ?? new List<BanditVehicleType>())
            {
                if (type == null || string.IsNullOrEmpty(type.Name))
                {
                    continue;
                }

                if (type.Cost <= 0f)
                {
                    problems.Add($"vehicle '{type.Name}' costs {type.Cost:0.#} - must be above 0, "
                        + "so it can never be drawn.");
                }

                bool hasDriver = false;
                foreach (BanditVehicleSeat seat in type.Crew ?? new List<BanditVehicleSeat>())
                {
                    if (seat == null)
                    {
                        continue;
                    }

                    if (config.FindKit(seat.Kit) == null)
                    {
                        problems.Add($"vehicle '{type.Name}' puts kit '{seat.Kit}' in seat {seat.Seat}, "
                            + "which does not exist - that seat stays empty.");
                    }

                    hasDriver |= seat.Seat == FakePlayer.BanditVehicleDriver.DriverSeat;
                }

                if (type.DriveAtCaller && !hasDriver)
                {
                    problems.Add($"vehicle '{type.Name}' is set to drive in, but has nobody in seat 0 - "
                        + "it will hold position instead.");
                }

                if (checkAssets && BanditVehicleSpawner.Resolve(type.Vehicle, out string assetError) == null)
                {
                    problems.Add($"vehicle '{type.Name}': {assetError}.");
                }
            }

            // A high floor is never a problem in itself - the budget is whatever somebody types, so
            // any floor can be reached by typing a bigger number. What genuinely cannot be drawn is
            // something that will not fit under the bandit ceiling however large the budget gets,
            // because that limit is on men rather than on points.
            if (config.EventMaxBandits > 0)
            {
                foreach (BanditSquadType type in config.Squads ?? new List<BanditSquadType>())
                {
                    if (type == null || string.IsNullOrEmpty(type.Name) || type.Members == null)
                    {
                        continue;
                    }

                    if (type.Members.Count > config.EventMaxBandits)
                    {
                        problems.Add($"squad '{type.Name}' has {type.Members.Count} members but "
                            + $"EventMaxBandits is {config.EventMaxBandits} - it can never be drawn, "
                            + "at any budget.");
                    }
                }

                foreach (BanditVehicleType type in config.Vehicles ?? new List<BanditVehicleType>())
                {
                    if (type?.Crew == null || string.IsNullOrEmpty(type.Name))
                    {
                        continue;
                    }

                    if (type.Crew.Count > config.EventMaxBandits)
                    {
                        problems.Add($"vehicle '{type.Name}' needs {type.Crew.Count} crew but "
                            + $"EventMaxBandits is {config.EventMaxBandits} - it can never be drawn.");
                    }
                }
            }
        }
    }
}
