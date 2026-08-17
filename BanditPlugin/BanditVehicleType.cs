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
        /// Whether the driver gets out with everyone else once the trip is over.
        ///
        /// On, which is right for a transport. The first version kept the driver at the wheel on the
        /// reasoning that it still had a vehicle worth keeping - but watch it happen and the flaw is
        /// obvious: the trip is the only thing the driver was for. Once the truck has stopped there
        /// is no further order coming, so a driver left aboard is a man sitting in a parked car
        /// through the whole fight, and in something unarmed he cannot even shoot out of it.
        ///
        /// Off leaves him aboard, which is only worth doing for something armed enough that sitting
        /// in it beats standing next to it.
        ///
        /// Ignored entirely unless <see cref="DriveAtCaller"/> is on - a vehicle that holds position
        /// never unloads at all.
        /// </summary>
        public bool DriverDismounts = true;

        /// <summary>
        /// How near an enemy has to get before a waiting vehicle sets off, in metres, whether or not
        /// anybody has actually laid eyes on them.
        ///
        /// A vehicle does not drive the moment it spawns. It waits with its engine running until the
        /// event makes contact - either a squad sees someone, or somebody comes inside this range -
        /// and only then sets off. Without that hold, a transport spawned two hundred metres out
        /// simply drives at where you were standing while the infantry it spawned beside are still
        /// walking, so the event arrives in two halves and the first half is a lorry on its own.
        ///
        /// This range is the backstop rather than the main trigger. The main one is the squad's own
        /// shared contact, which is what makes the whole event move together the instant any part of
        /// it sees you. This exists because a bandit sitting inside a vehicle may have no line of
        /// sight out of it, so a vehicle spawned with no infantry alongside could otherwise wait for
        /// eyes it does not have. 0 turns it off and leaves the vehicle entirely dependent on
        /// somebody seeing you.
        /// </summary>
        public float ContactTriggerRange = 120f;

        /// <summary>
        /// How far short of the destination the crew gets out, in metres.
        ///
        /// The setting that makes a transport worth having. Driving all the way onto the target and
        /// only then opening the doors wastes the entire approach: the passengers can do nothing
        /// from inside an unarmed vehicle, so the trip is dead time and the fight begins with a
        /// truck parked on top of you and men appearing out of it. Stopping short turns the same
        /// vehicle into what it should be - something that closes the ground quickly and then puts
        /// a squad on its feet with an assault still to make.
        ///
        /// 60m by default: inside a rifleman's 110m acquire range, so they are in contact the moment
        /// they land, but with enough ground left that the last stretch is fought on foot.
        ///
        /// 0 drives the whole way and unloads on arrival, which is the old behaviour and is only
        /// really right for somewhere the vehicle itself needs to be.
        /// </summary>
        public float DismountRange = 60f;

        /// <summary>
        /// How close an *armed* vehicle closes before stopping to shoot, in metres. Ignored entirely
        /// by anything with no turret.
        ///
        /// The difference between the two endings a vehicle can have. Something unarmed is a taxi:
        /// it stops at <see cref="DismountRange"/>, everybody gets out, and the vehicle has done its
        /// whole job. Something with a turret is a weapon that happens to have wheels, and emptying
        /// it would be throwing the weapon away - so it keeps its driver and its gunners, holds at
        /// this range, and fights from there while only the men in the back dismount.
        ///
        /// It also keeps moving as the fight does: the destination is the freshest thing the event
        /// has seen, so a vehicle whose target has backed off closes the gap again rather than
        /// sitting where it first stopped. Far enough out that it is using its reach, near enough
        /// that it can see anything at all - which is why it is longer than the dismount range
        /// rather than shorter.
        /// </summary>
        public float EngageRange = 110f;

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
                    // Now advances rather than sitting where it spawned. Being armed does not mean
                    // being static - it means it never empties: it waits for contact, closes to
                    // EngageRange, and fights from the turret with its crew still aboard.
                    DriveAtCaller = true,
                    DriverDismounts = false,
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
