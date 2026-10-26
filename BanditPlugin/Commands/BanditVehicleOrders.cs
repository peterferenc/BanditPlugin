using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using BanditPlugin.Navigation;
using Rocket.API;
using Rocket.Unturned.Player;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// The vehicle orders: getting bandits in and out of seats, and driving.
    ///
    ///   seat driver           climb into the driver seat of the nearest vehicle and hold it there
    ///   seat gunner[n]        climb into a gun seat - gunner is F2, gunner2 is F3 - and engage
    ///   seat exit             get out
    ///   drive [marker]        drive to where you are looking, or over the roads to your marker
    ///   drive route [noroads] drive the convoy route (or the patrol route if there is none)
    ///   drive stop            hold station
    ///
    /// Seating is one bandit at a time, because "the nearest vehicle's driver seat" has one
    /// answer and a squad asking for it at once would fight over it. Getting out and driving work
    /// on any target: "drive" goes to whoever in it is at a wheel, so "/bandit vehicle 4 drive
    /// marker" and "/bandit squad 3 drive marker" both reach the driver.
    /// </summary>
    public static class BanditVehicleOrders
    {
        public static void Seat(IRocketPlayer caller, BanditTarget target, string[] args)
        {
            if (args.Length == 0)
            {
                Reply(caller, "Usage: /bandit [target] seat driver|gunner[n]|exit", Color.yellow);
                return;
            }

            string seatWord = args[0].ToLowerInvariant();

            List<BanditBotController> bandits = BanditOrders.Ready(caller, target);
            bandits.RemoveAll(bandit => bandit.Driver == null);
            if (bandits.Count == 0)
            {
                return;
            }

            if (seatWord == "exit")
            {
                int exited = 0;
                string last = null;
                foreach (BanditBotController bandit in bandits)
                {
                    if (bandit.Driver.IsSeated && bandit.Driver.TryExit(out string reason))
                    {
                        exited++;
                        last = reason;
                    }
                }

                Reply(caller, exited > 0
                    ? $"{target.Label}: {exited} bandit(s) out" + (exited == 1 ? $" of {last}." : ".")
                    : $"Nobody in {target.Label} is in a vehicle.", exited > 0 ? Color.green : Color.yellow);
                return;
            }

            if (bandits.Count > 1)
            {
                Reply(caller, $"{target.Label} is {bandits.Count} bandits - seat one at a time, by number: "
                    + "/bandit <number> seat ... (/bandit list shows them).", Color.red);
                return;
            }

            BanditBotController one = bandits[0];

            if (seatWord == "driver" || seatWord == "drive")
            {
                if (!one.Driver.TryDrive(out string reason))
                {
                    Reply(caller, $"Bandit #{one.Id} stayed on foot: {reason}.", Color.red);
                    return;
                }

                // The seat change is applied by vanilla on the bandit's next input packet, so the
                // reply is about the order; /bandit debug status prints the seat it ended up in.
                Reply(caller, $"Bandit #{one.Id} taking the wheel of {reason}, and holding it there. "
                    + "'drive' sends it somewhere.", Color.green);
                return;
            }

            if (seatWord.StartsWith("gun"))
            {
                if (!TryParseGunnerSeat(seatWord, args, out byte seat))
                {
                    Reply(caller, "Gunner seats are gunner (F2), gunner2 (F3), gunner3 (F4) and so on.", Color.yellow);
                    return;
                }

                if (!one.Driver.TryGun(seat, out string reason))
                {
                    Reply(caller, $"Bandit #{one.Id} stayed on foot: {reason}.", Color.red);
                    return;
                }

                // The seat is whatever that vehicle's Nth seat happens to be. In anything with a
                // turret there it is the gun; in a plain car it is a passenger seat.
                Reply(caller, $"Bandit #{one.Id} into the F{seat + 1} seat of {reason}, tracking the nearest enemy.",
                    Color.green);
                return;
            }

            Reply(caller, "Usage: /bandit [target] seat driver|gunner[n]|exit", Color.yellow);
        }

        /// <summary>
        /// Reads which gun seat was asked for out of "gunner", "gunner2", "gun3" or "gunner 2".
        ///
        /// The number is the seat key, not the seat index counted from the guns: gunner2 is F3, is
        /// seat 2. Keeping those the same means the command can be checked against the vehicle by
        /// pressing the key yourself, which is how you find out which seat a modded vehicle's second
        /// turret is actually on.
        /// </summary>
        private static bool TryParseGunnerSeat(string subcommand, string[] command, out byte seat)
        {
            seat = 0;

            int digits = 0;
            bool hasDigits = false;
            for (int i = 0; i < subcommand.Length; i++)
            {
                if (!char.IsDigit(subcommand[i]))
                {
                    continue;
                }

                digits = digits * 10 + (subcommand[i] - '0');
                hasDigits = true;
            }

            if (!hasDigits && command.Length > 1 && !int.TryParse(command[1], out digits))
            {
                return false;
            }

            if (!hasDigits && command.Length <= 1)
            {
                digits = 1; // plain "gunner" is the first gun seat, F2
            }

            if (digits < 1 || digits > byte.MaxValue - 1)
            {
                return false;
            }

            seat = (byte)digits;
            return true;
        }

        /// <summary>
        /// Drives every vehicle in the target that has a bandit at the wheel.
        ///
        /// The plain form aims at whatever you are looking at and steers at it, width-sweeping each
        /// heading so a four-metre truck does not try a one-metre gate - "blocked 40m out" is a real
        /// and expected outcome. "marker" and "route" instead plan over the road graph the way a
        /// convoy does, which makes this the one-hop version of a convoy and the quickest way to
        /// find out whether a stretch of road drives at all.
        /// </summary>
        public static void Drive(IRocketPlayer caller, BanditTarget target, string[] args)
        {
            List<BanditBotController> bandits = BanditOrders.Ready(caller, target);
            if (bandits.Count == 0)
            {
                return;
            }

            string word = args.Length > 0 ? args[0].ToLowerInvariant() : null;

            if (word == "stop")
            {
                int stopped = 0;
                foreach (BanditBotController bandit in bandits)
                {
                    if (bandit.Driver == null)
                    {
                        continue;
                    }

                    BanditRouteDrive.Stop(bandit);
                    bandit.Driver.StopDriving();
                    stopped += bandit.Driver.IsDriver ? 1 : 0;
                }

                Reply(caller, $"{target.Label}: {stopped} vehicle(s) holding station.", Color.green);
                return;
            }

            List<BanditBotController> drivers = new List<BanditBotController>();
            int scripted = 0;
            foreach (BanditBotController bandit in bandits)
            {
                if (bandit.Driver == null || !bandit.Driver.IsDriver)
                {
                    continue;
                }

                // A vehicle an event or a convoy spawned is being driven by that event already, and
                // the two would fight over the wheel. A convoy's are left alone - the column is the
                // point of a convoy - and an event's are taken off its script and handed over.
                BanditEvent.Ride ride = BanditTargets.RideDrivenBy(bandit);
                if (ride != null && BanditTargets.ConvoyOf(ride.Event) != null)
                {
                    scripted++;
                    continue;
                }

                if (ride != null)
                {
                    ride.Finished = true;
                }

                drivers.Add(bandit);
            }

            if (drivers.Count == 0)
            {
                Reply(caller, scripted > 0
                    ? $"{target.Label} is driving in a convoy, which steers itself. Clear it to take over."
                    : $"Nobody in {target.Label} is at a wheel - /bandit <number> seat driver first.", Color.red);
                return;
            }

            if (word == "route" || word == "wp")
            {
                bool convoyRoute = BanditConvoyRoute.Current.Count > 0;
                DriveRoute(caller, target, drivers,
                    convoyRoute ? BanditConvoyRoute.Current : BanditWaypointStore.Current,
                    convoyRoute ? "the convoy route" : "the patrol route", args);
                return;
            }

            UnturnedPlayer player = RequirePlayer(caller, "drive");
            if (player == null)
            {
                return;
            }

            if (word != null && BanditPlacement.IsMarkerRequest(word))
            {
                if (BanditOrders.TryMarker(caller, player.Player, out Vector3 marker))
                {
                    DriveRoute(caller, target, drivers, new[] { marker }, "your map marker", args);
                }
                return;
            }

            if (!BanditOrders.TryPickPoint(caller, player.Player, args, out Vector3 point))
            {
                return;
            }

            int driving = 0;
            string refusal = null;
            foreach (BanditBotController driver in drivers)
            {
                if (driver.Driver.TrySetDestination(point, out string reason))
                {
                    driving++;
                }
                else
                {
                    refusal = reason;
                }
            }

            if (driving == 0)
            {
                Reply(caller, $"{target.Label} staying put: {refusal}.", Color.red);
                return;
            }

            // Worth saying out loud, because it is the number that decides whether the trip is
            // plausible at all. A bandit can walk through a 1m gate; the thing it is now driving
            // may be four metres wide.
            BanditVehicleFootprint footprint = drivers[0].Driver.Footprint;
            float distance = Vector3.Distance(drivers[0].Self.transform.position, point);

            Reply(caller, $"{target.Label}: {driving} vehicle(s) driving out - {distance:0}m, "
                + $"{footprint.HalfWidth * 2f:0.0}m wide by {footprint.HalfLength * 2f:0.0}m long. "
                + "/bandit debug status for progress.", Color.green);
        }

        /// <summary>
        /// Sends each driver along a route over the roads, with the same planner and driver a convoy
        /// uses - so a line it takes badly here is a line a column would take badly too.
        /// </summary>
        /// <param name="source">Where the points came from, so the reply says which list was used -
        /// there are two, and picking the wrong one silently is how "route does not work" happens.</param>
        private static void DriveRoute(IRocketPlayer caller, BanditTarget target, List<BanditBotController> drivers,
            IReadOnlyList<Vector3> route, string source, string[] args)
        {
            if (route == null || route.Count < 1)
            {
                Reply(caller, "No route on this map. /bandit route convoy add records one where you stand.",
                    Color.red);
                return;
            }

            bool useRoads = true;
            for (int i = 1; i < args.Length; i++)
            {
                string word = args[i].ToLowerInvariant();
                if (word == "noroads" || word == "offroad" || word == "useroads:false")
                {
                    useRoads = false;
                }
            }

            int driving = 0;
            string summary = null;
            foreach (BanditBotController driver in drivers)
            {
                if (BanditRouteDrive.Start(driver, route, useRoads, out string planned))
                {
                    driving++;
                }

                summary = planned;
            }

            if (driving == 0)
            {
                Reply(caller, $"Cannot drive the route: {summary}.", Color.red);
                return;
            }

            BanditVehicleFootprint footprint = drivers[0].Driver.Footprint;

            Reply(caller, $"{target.Label}: {driving} vehicle(s) driving {route.Count} point(s) from {source} - "
                + $"{summary}. {footprint.HalfWidth * 2f:0.0}m wide by {footprint.HalfLength * 2f:0.0}m long.",
                Color.green);

            Reply(caller, "/bandit debug navlog on for the commentary, 'drive stop' to end it.", Color.grey);
        }
    }
}
