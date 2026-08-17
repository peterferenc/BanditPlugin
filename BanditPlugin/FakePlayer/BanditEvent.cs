using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.FakePlayer
{
    /// <summary>
    /// One "/banditevent": everything it put on the ground, and the one behaviour that needs
    /// watching afterwards.
    ///
    /// Squads look after themselves once spawned - that is what <see cref="BanditSquad"/> is for -
    /// so this exists for the two things a squad cannot answer.
    ///
    /// The first is cleanup. A spawned vehicle is a real networked vehicle: it survives every bot
    /// being kicked, and without something remembering that this event created it, "/banditclear"
    /// leaves a field of abandoned trucks behind. Bots are cleaned up by walking Provider.clients;
    /// vehicles have no such handle, so the list here is the only record.
    ///
    /// The second is the dismount, which is the entire point of a vehicle that carries infantry. A
    /// truckload of riflemen that drives at you and then sits there with everyone inside is not a
    /// threat - it is scenery with a windscreen. So a transport is watched until it arrives, and
    /// then emptied. See <see cref="Tick"/>.
    /// </summary>
    public sealed class BanditEvent
    {
        /// <summary>Every event still running, newest last.</summary>
        public static readonly List<BanditEvent> All = new List<BanditEvent>();

        private static int _nextId = 1;

        /// <summary>Shown in the reply and by /banditstatus, so two events can be told apart.</summary>
        public int Id { get; }

        /// <summary>What was typed, and what the draw actually managed to spend.</summary>
        public float Budget { get; }
        public float Spent { get; internal set; }

        /// <summary>The squads it drew, for reporting. They fight without any help from here.</summary>
        public readonly List<BanditSquad> Squads = new List<BanditSquad>();

        /// <summary>The vehicles it spawned, crewed or empty.</summary>
        public readonly List<Ride> Rides = new List<Ride>();

        /// <summary>
        /// One spawned vehicle and the men in it, plus where it has got to in the short life of a
        /// transport: waiting for its crew to climb aboard, driving, and then done with.
        /// </summary>
        public sealed class Ride
        {
            public InteractableVehicle Vehicle;
            public string TypeName = string.Empty;

            /// <summary>Whoever is meant to be in seat 0. Null for a vehicle spawned empty.</summary>
            public BanditBotController Driver;

            /// <summary>Everyone else aboard - the men who get out at the far end.</summary>
            public readonly List<BanditBotController> Passengers = new List<BanditBotController>();

            /// <summary>Whether this one drives at the caller and unloads, or holds where it is.</summary>
            public bool DriveAtCaller;

            /// <summary>Where it is going, if it is going anywhere.</summary>
            public Vector3 Destination;

            /// <summary>Set once the drive order has actually been accepted, so it is given once.</summary>
            public bool Driving;

            /// <summary>Set once the passengers are out, after which this ride is left alone.</summary>
            public bool Unloaded;
        }

        private BanditEvent(float budget)
        {
            Id = _nextId++;
            Budget = budget;
            All.Add(this);
            BanditEventDirector.Ensure();
        }

        public static BanditEvent Create(float budget)
        {
            return new BanditEvent(budget);
        }

        /// <summary>How many bandits this event put on the ground, across squads and vehicles.</summary>
        public int BanditCount
        {
            get
            {
                int total = 0;
                foreach (BanditSquad squad in Squads)
                {
                    total += squad.Members.Count;
                }

                return total;
            }
        }

        /// <summary>
        /// Forgets every event. Called by "/banditclear", which also kicks the bots and destroys the
        /// vehicles - the latter through BanditVehicleSpawner, which owns every vehicle this plugin
        /// put down rather than only the ones an event did.
        /// </summary>
        public static void ClearAll()
        {
            All.Clear();
        }

        /// <summary>
        /// Moves every transport along by one step: order the drive once the crew is aboard, and
        /// put the passengers out when it gets there.
        ///
        /// Deliberately driven by the driver's own navigator rather than by measuring the distance
        /// here. ConsumeArrived and ConsumeGaveUp are what the vehicle itself concluded about the
        /// trip, and "gave up" has to unload as surely as "arrived" does - a truck stuck against a
        /// rock two hundred metres out with four men sat inside it is the exact failure this whole
        /// mechanism exists to avoid.
        /// </summary>
        private void Tick()
        {
            foreach (Ride ride in Rides)
            {
                if (ride.Unloaded || !ride.DriveAtCaller)
                {
                    continue;
                }

                if (ride.Vehicle == null || ride.Vehicle.isDead || ride.Vehicle.isExploded)
                {
                    // Nothing left to unload from. Anyone who was inside was thrown out by vanilla
                    // when it blew up, and is now a bandit on foot like any other.
                    ride.Unloaded = true;
                    continue;
                }

                BanditBotController driver = ride.Driver;
                bool driverGone = driver == null || driver.Self == null
                    || (driver.Self.life != null && driver.Self.life.isDead);

                if (driverGone)
                {
                    // The truck is not going anywhere else. Better to put the passengers out here,
                    // wherever "here" is, than to leave a squad riding in a stationary vehicle for
                    // the rest of the round.
                    Unload(ride);
                    continue;
                }

                // Created in the controller's Start, which has not necessarily run yet: this
                // director can tick in the same frame the event spawned its crew. Nothing to do
                // until it exists.
                if (driver.Driver == null)
                {
                    continue;
                }

                if (!ride.Driving)
                {
                    // Not yet - the driver is still working its way into the seat. RequestSeat
                    // retries for several seconds, and TrySetDestination refuses from outside a
                    // vehicle, so the order is simply re-offered each tick until it is accepted.
                    if (!driver.Driver.IsSeated || driver.HasPendingSeat)
                    {
                        continue;
                    }

                    if (driver.Driver.TrySetDestination(ride.Destination, out string reason))
                    {
                        ride.Driving = true;
                    }
                    else
                    {
                        // Something that will not become true by waiting - a boat asked to drive
                        // overland, most often. Put the men out where they are and let them walk.
                        Logger.Log($"[Bandit] Event {Id}: {ride.TypeName} cannot drive to the caller "
                            + $"({reason}) - unloading where it stands.");
                        Unload(ride);
                    }

                    continue;
                }

                if (driver.Driver.ConsumeArrived() || driver.Driver.ConsumeGaveUp())
                {
                    Unload(ride);
                }
            }
        }

        /// <summary>
        /// Puts everyone but the driver out. The driver stays at the wheel: it still has a vehicle
        /// worth keeping, and a man who has just climbed out of one is a man who spent the fight
        /// getting out of it.
        /// </summary>
        private void Unload(Ride ride)
        {
            ride.Unloaded = true;

            foreach (BanditBotController passenger in ride.Passengers)
            {
                if (passenger == null || passenger.Self == null)
                {
                    continue;
                }

                if (passenger.Self.life != null && passenger.Self.life.isDead)
                {
                    continue;
                }

                passenger.Driver?.TryExit(out _);
            }
        }

        /// <summary>
        /// The one thing in this plugin that needs a heartbeat of its own.
        ///
        /// Every other behaviour hangs off a bandit, and a bandit is a MonoBehaviour on a real
        /// player object that vanilla is already ticking. An event is not attached to anything, and
        /// its transports have to be watched between the moment they are told to drive and the
        /// moment they arrive - so it gets one hidden object to tick from, created the first time an
        /// event is run and left alone afterwards.
        /// </summary>
        private sealed class BanditEventDirector : MonoBehaviour
        {
            private static BanditEventDirector _instance;

            public static void Ensure()
            {
                if (_instance != null)
                {
                    return;
                }

                GameObject host = new GameObject("BanditEventDirector");
                DontDestroyOnLoad(host);
                _instance = host.AddComponent<BanditEventDirector>();
            }

            private void Update()
            {
                for (int i = 0; i < All.Count; i++)
                {
                    All[i].Tick();
                }
            }
        }
    }
}
