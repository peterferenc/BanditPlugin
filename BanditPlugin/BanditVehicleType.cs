using System.Collections.Generic;

namespace BanditPlugin
{
    /// <summary>
    /// One seat of a vehicle and the class that rides in it.
    ///
    /// The seat is the index a player reaches with the function keys, not a count of the crew: seat
    /// 0 is the driver (F1), seat 1 is F2, and so on. Keeping those the same means a crew that came
    /// out wrong can be diagnosed by sitting in the vehicle yourself and pressing the keys, which is
    /// the only practical way to find out where a modded vehicle's second turret actually is.
    /// </summary>
    public class BanditVehicleSeat
    {
        /// <summary>Which seat, numbered as the F-keys are. 0 is the driver.</summary>
        public byte Seat;

        /// <summary>The kit riding in it - any name from <see cref="BanditConfiguration.Kits"/>.</summary>
        public string Kit = string.Empty;
    }

    /// <summary>
    /// One kind of vehicle an event can put on the ground, already crewed.
    ///
    /// This is to vehicles what <see cref="BanditSquadType"/> is to infantry: a named thing with a
    /// price, a floor and a crew, so the draw can spend a budget on it without anything in the code
    /// knowing what an Offroader is.
    ///
    /// The vehicle itself is named by <see cref="Vehicle"/>, which takes either form vanilla uses -
    /// a GUID or a legacy numeric ID. Both are needed rather than one: modern vanilla assets carry
    /// only a GUID (the Offroader has no numeric ID at all), while plenty of older and workshop
    /// content is still addressed by number.
    ///
    /// What a crew actually does once seated is decided by the seat it is in, not by configuration.
    /// A driver holds the vehicle where it is; anyone in a turret tracks and engages on their own.
    /// The one choice worth making per type is <see cref="DriveAtCaller"/>.
    /// </summary>
    public class BanditVehicleType
    {
        /// <summary>
        /// What "/banditv &lt;name&gt;" matches and what the event reports, case-insensitively.
        /// Purely a label for this entry - it has nothing to do with the vehicle's own name.
        /// </summary>
        public string Name = string.Empty;

        /// <summary>
        /// Which vehicle to spawn: a GUID as it appears in the asset's .dat
        /// ("e0f8b4b23249483a9cd5c1402e2170fb"), or a legacy numeric ID ("119"). Dashes in a GUID
        /// are optional. See <see cref="FakePlayer.BanditVehicleSpawner"/> for the resolution.
        /// </summary>
        public string Vehicle = string.Empty;

        /// <summary>
        /// What the platform is worth, before its crew. The men in it are drawn and paid for
        /// separately out of the same budget, so what you set here is the cost of the metal alone.
        ///
        /// Split that way on purpose: a vehicle priced as one lump hides how much of an event it
        /// really consumed, and the first thing you want to know when a 300-point event felt thin is
        /// whether the tank ate it. The draw still checks platform *and* crew against what is left,
        /// so a vehicle is never spawned with a crew the budget could not cover.
        /// </summary>
        public float Cost = 30f;

        /// <summary>
        /// The smallest event budget this vehicle may be drawn into. The same setting as
        /// <see cref="BanditSquadType.MinEventCost"/> and it matters more here: nothing turns a
        /// modest event into an unwinnable one faster than affording a tank.
        /// </summary>
        public float MinEventCost = 100f;

        /// <summary>How often this is drawn relative to the other eligible vehicles.</summary>
        public float Weight = 1f;

        /// <summary>
        /// Who is in it, and where. An entry naming a seat the vehicle does not have is skipped with
        /// a warning rather than failing the spawn - a crew list written for a six-seat truck and
        /// pointed at a quad should still produce a driven quad.
        ///
        /// Leave it empty and the vehicle is spawned with nobody in it, which is occasionally what
        /// you want: an event can include a wreck to take cover behind.
        /// </summary>
        public List<BanditVehicleSeat> Crew = new List<BanditVehicleSeat>();

        /// <summary>
        /// Drive at whoever triggered the event as soon as the crew is aboard, and put the
        /// passengers out on arrival.
        ///
        /// Off, a crewed vehicle is a firing position: it sits where it spawned, and anyone in a
        /// turret engages from it. That suits something armed and nothing else - a truckload of
        /// riflemen who never get out is not a threat, it is scenery.
        ///
        /// On, it is transport, and the point is the dismount: the vehicle drives to where the
        /// caller was standing, and everyone who is not the driver gets out and fights on foot. The
        /// driver stays at the wheel. See <see cref="FakePlayer.BanditEvent"/>, which watches for
        /// the arrival.
        /// </summary>
        public bool DriveAtCaller = true;

        /// <summary>
        /// The vehicles a fresh configuration starts with, priced against the rifleman's 10.
        ///
        /// All three are vanilla assets addressed by GUID, so they resolve on any map that loads
        /// core content. A server running a vehicle pack adds entries here and the draw picks them
        /// up with no code change - which is the whole point of the list being configuration.
        /// </summary>
        public static List<BanditVehicleType> BuildDefaults()
        {
            return new List<BanditVehicleType>
            {
                // A truckload of infantry: the cheapest way for an event to arrive somewhere rather
                // than already be there. Worth three riflemen as metal, and everything interesting
                // about it happens when it stops - see DriveAtCaller.
                new BanditVehicleType
                {
                    Name = "offroader",
                    Vehicle = "e0f8b4b23249483a9cd5c1402e2170fb",
                    Cost = 30f,
                    MinEventCost = 120f,
                    Weight = 3f,
                    DriveAtCaller = true,
                    Crew = new List<BanditVehicleSeat>
                    {
                        new BanditVehicleSeat { Seat = 0, Kit = "rifleman" },
                        new BanditVehicleSeat { Seat = 1, Kit = "rifleman" },
                        new BanditVehicleSeat { Seat = 2, Kit = "breacher" }
                    }
                },

                // The same idea with armour on it and a heavier team inside, so it survives being
                // shot at on the way in. Twice the platform cost and a much higher floor.
                new BanditVehicleType
                {
                    Name = "armored",
                    Vehicle = "48087f6ed2084186a92347962eb72f4b",
                    Cost = 60f,
                    MinEventCost = 250f,
                    Weight = 2f,
                    DriveAtCaller = true,
                    Crew = new List<BanditVehicleSeat>
                    {
                        new BanditVehicleSeat { Seat = 0, Kit = "rifleman" },
                        new BanditVehicleSeat { Seat = 1, Kit = "mg" },
                        new BanditVehicleSeat { Seat = 2, Kit = "rifleman" }
                    }
                },

                // The one default with a turret - seat 1, per the asset's own Turret_0_Seat_Index -
                // so it is the only one where a gunner does anything from inside. Priced at twenty
                // riflemen and locked to events large enough that it is one element of a fight
                // rather than the entire answer to it.
                new BanditVehicleType
                {
                    Name = "tank",
                    Vehicle = "20044da6daee4a12bba1b35eaa4c61e0",
                    Cost = 200f,
                    MinEventCost = 500f,
                    Weight = 1f,
                    // Left where it spawns: it is armed, so it is a firing position rather than a
                    // taxi, and its gunner engages from the turret without going anywhere.
                    DriveAtCaller = false,
                    Crew = new List<BanditVehicleSeat>
                    {
                        new BanditVehicleSeat { Seat = 0, Kit = "rifleman" },
                        new BanditVehicleSeat { Seat = 1, Kit = "mg" }
                    }
                }
            };
        }
    }
}
