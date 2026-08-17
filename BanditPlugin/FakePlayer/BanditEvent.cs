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
        /// One spawned vehicle and the men in it, plus where it has got to in its short life:
        /// waiting for contact, driving, and then either emptied or dug in and shooting.
        ///
        /// Which of those two endings it gets is decided by whether anybody aboard is in a turret.
        /// An unarmed vehicle is a taxi and its whole worth is the delivery, so it stops and empties.
        /// An armed one is a weapon that happens to have wheels, and emptying it would be throwing
        /// the weapon away - so it keeps its driver and its gunners, holds at a distance it can
        /// shoot from, and only the men riding in the back get out.
        /// </summary>
        public sealed class Ride
        {
            public InteractableVehicle Vehicle;
            public string TypeName = string.Empty;

            /// <summary>Whoever is meant to be in seat 0. Null for a vehicle spawned empty.</summary>
            public BanditBotController Driver;

            /// <summary>Crew in a turret seat. These fight from the vehicle and never get out.</summary>
            public readonly List<BanditBotController> Gunners = new List<BanditBotController>();

            /// <summary>Everyone else aboard - the men who get out at the far end.</summary>
            public readonly List<BanditBotController> Riders = new List<BanditBotController>();

            /// <summary>Whether this one drives at contact, or simply holds where it spawned.</summary>
            public bool DriveAtCaller;

            /// <summary>Whether the driver gets out too. Only ever consulted for an unarmed vehicle;
            /// an armed one needs its driver to keep the gun pointed somewhere useful.</summary>
            public bool DriverDismounts = true;

            /// <summary>How far short of the contact the riders get out.</summary>
            public float DismountRange;

            /// <summary>How close an armed vehicle closes before stopping to shoot.</summary>
            public float EngageRange;

            /// <summary>How near an enemy must come to start a waiting vehicle with nobody's eyes on
            /// them. See <see cref="BanditVehicleType.ContactTriggerRange"/>.</summary>
            public float ContactTriggerRange;

            /// <summary>Where it is heading - the freshest contact the event has, not a fixed point.</summary>
            public Vector3 Destination;

            /// <summary>Set once something has been seen and the vehicle is allowed to move at all.</summary>
            public bool Released;

            /// <summary>Set once a drive order has been accepted, so it is not re-issued every tick.</summary>
            public bool Driving;

            /// <summary>Set once the riders are out. The vehicle may still be fighting.</summary>
            public bool Unloaded;

            /// <summary>Set once there is nothing further to do with this ride at all.</summary>
            public bool Finished;

            /// <summary>
            /// Whether anybody is still alive in a turret. Checked rather than cached because a
            /// gunner dying turns the vehicle from a weapon back into a taxi, and the driver should
            /// then get out and fight rather than sit in a hull with a dead gun.
            /// </summary>
            public bool IsArmed
            {
                get
                {
                    foreach (BanditBotController gunner in Gunners)
                    {
                        if (gunner != null && gunner.Self != null
                            && (gunner.Self.life == null || !gunner.Self.life.isDead))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }
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
        /// Moves every vehicle along by one step: hold until the event sees something, drive at it,
        /// then either empty out or settle down and shoot.
        ///
        /// The hold is the important part and it was learned by watching one without it. A vehicle
        /// that sets off the moment it spawns drives at where you were standing while the infantry
        /// it spawned beside are still on foot two hundred metres back - so the event arrives in two
        /// halves, and the first half is a lorry by itself. Waiting for contact keeps it together:
        /// the instant any part of the event sees you, all of it starts moving at once.
        ///
        /// Arrival is measured here rather than left to the navigator, whose idea of arriving is
        /// having its bumper on the point. That is exactly the behaviour worth avoiding - it wastes
        /// the entire approach and puts the fight on top of the player.
        /// </summary>
        private void Tick()
        {
            foreach (Ride ride in Rides)
            {
                if (ride.Finished || !ride.DriveAtCaller)
                {
                    continue;
                }

                if (ride.Vehicle == null || ride.Vehicle.isDead || ride.Vehicle.isExploded)
                {
                    // Anyone inside was thrown clear by vanilla when it went up, and is now a bandit
                    // on foot like any other.
                    ride.Finished = true;
                    continue;
                }

                BanditBotController driver = ride.Driver;
                bool driverGone = driver == null || driver.Self == null
                    || (driver.Self.life != null && driver.Self.life.isDead);

                if (driverGone)
                {
                    // Not going anywhere else. Better to put the riders out here, wherever here is,
                    // than to leave them sitting in a stationary vehicle for the rest of the round.
                    UnloadRiders(ride);
                    ride.Finished = true;
                    continue;
                }

                // Created in the controller's Start, which has not necessarily run yet - this
                // director can tick in the same frame the crew was spawned.
                if (driver.Driver == null)
                {
                    continue;
                }

                // Still climbing aboard. RequestSeat retries for several seconds, and a destination
                // cannot be given from outside a vehicle anyway.
                if (!driver.Driver.IsSeated || driver.HasPendingSeat)
                {
                    continue;
                }

                if (!TryUpdateTarget(ride, driver.Self))
                {
                    continue; // nothing seen yet - sit still with the engine running
                }

                Vector3 offset = ride.Vehicle.transform.position - ride.Destination;
                offset.y = 0f;
                float distance = offset.magnitude;

                // The riders get out once they are near enough to be useful on foot, whatever the
                // vehicle does next.
                if (!ride.Unloaded && distance <= ride.DismountRange)
                {
                    UnloadRiders(ride);
                }

                // An armed vehicle keeps its driver and its gunners and holds at a range it can
                // shoot from; an unarmed one has done its whole job the moment the riders are out.
                float stopAt = ride.IsArmed ? ride.EngageRange : ride.DismountRange;

                if (distance <= stopAt)
                {
                    driver.Driver.StopDriving();
                    ride.Driving = false;

                    if (!ride.IsArmed)
                    {
                        UnloadRiders(ride);
                        if (ride.DriverDismounts)
                        {
                            Disembark(ride.Driver);
                        }

                        ride.Finished = true;
                    }

                    continue;
                }

                // Further off than it wants to be - close the gap. Re-issued whenever the contact
                // moves far enough to matter, which is what lets an armed vehicle follow someone
                // who has backed away instead of sitting where it first stopped.
                if (!ride.Driving)
                {
                    if (driver.Driver.TrySetDestination(ride.Destination, out string reason))
                    {
                        ride.Driving = true;
                    }
                    else
                    {
                        // Not something that becomes true by waiting - a boat asked to cross a
                        // field, most often. Put the men out and let them walk.
                        Logger.Log($"[Bandit] Event {Id}: {ride.TypeName} cannot drive to contact "
                            + $"({reason}) - unloading where it stands.");
                        UnloadRiders(ride);
                        ride.Finished = true;
                        continue;
                    }
                }

                if (driver.Driver.ConsumeGaveUp())
                {
                    // Stuck. Everyone out here rather than riding a wedged vehicle indefinitely.
                    UnloadRiders(ride);
                    if (!ride.IsArmed && ride.DriverDismounts)
                    {
                        Disembark(ride.Driver);
                    }

                    ride.Finished = true;
                }
                else if (driver.Driver.ConsumeArrived())
                {
                    ride.Driving = false;
                }
            }
        }

        /// <summary>
        /// Where this event currently thinks the enemy is, and whether it has any business moving
        /// yet.
        ///
        /// Contact is taken from the whole event rather than from the vehicle's own crew, because a
        /// bandit sitting inside a hull may have no line of sight out of it - the infantry standing
        /// in the open are the ones who can actually see. That also gives the behaviour worth
        /// having: whoever spots you starts everything moving, vehicles included.
        ///
        /// The proximity trigger underneath it is the backstop for a vehicle spawned with no
        /// infantry alongside, which would otherwise wait forever on eyes it does not have.
        /// </summary>
        private bool TryUpdateTarget(Ride ride, Player crewman)
        {
            foreach (BanditSquad squad in Squads)
            {
                if (squad.HasFreshContact)
                {
                    ride.Destination = squad.ContactPosition;
                    ride.Released = true;
                    return true;
                }
            }

            if (ride.ContactTriggerRange > 0f)
            {
                Player near = NearestEnemyTo(crewman, ride.Vehicle.transform.position, ride.ContactTriggerRange);
                if (near != null)
                {
                    ride.Destination = near.transform.position;
                    ride.Released = true;
                    return true;
                }
            }

            // Once it has been woken it stays awake, heading for the last place anything was seen -
            // otherwise a vehicle would stop dead every time the squad lost sight of you.
            return ride.Released;
        }

        /// <summary>
        /// The nearest live player this event would shoot at, within a radius. No line-of-sight test:
        /// this is deliberately "somebody has come close enough to notice", not "somebody is visible",
        /// since the whole reason it exists is that the crew cannot see out.
        /// </summary>
        private static Player NearestEnemyTo(Player self, Vector3 position, float radius)
        {
            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;

            Player nearest = null;
            float nearestSquared = radius * radius;

            foreach (SteamPlayer client in Provider.clients)
            {
                if (client?.player == null || ReferenceEquals(client.playerID, null))
                {
                    continue;
                }

                Player candidate = client.player;
                if (candidate.life == null || candidate.life.isDead)
                {
                    continue;
                }

                // The driver is the yardstick for which side this vehicle is on. It has to be a
                // real player rather than null: IsHostile answers "no" to a null self, so passing
                // one would have made this trigger quietly never fire.
                float distanceSquared = (candidate.transform.position - position).sqrMagnitude;
                if (distanceSquared >= nearestSquared)
                {
                    continue;
                }

                if (FakePlayerSpawner.SpawnedBotSteamIds.Contains(client.playerID.steamID.m_SteamID))
                {
                    continue; // never wake a vehicle up over one of our own
                }

                if (!BanditTeams.IsHostile(self, candidate, otherIsBandit: false, config.HostileToUngrouped))
                {
                    continue;
                }

                nearestSquared = distanceSquared;
                nearest = candidate;
            }

            return nearest;
        }

        /// <summary>
        /// Puts the men riding in the back out. Gunners are never included - they are the reason the
        /// vehicle is worth anything - and the driver is handled by the caller, since whether it
        /// leaves depends on whether there is still a gun to drive.
        /// </summary>
        private void UnloadRiders(Ride ride)
        {
            if (ride.Unloaded)
            {
                return;
            }

            ride.Unloaded = true;

            foreach (BanditBotController rider in ride.Riders)
            {
                Disembark(rider);
            }
        }

        private static void Disembark(BanditBotController crewman)
        {
            if (crewman == null || crewman.Self == null)
            {
                return;
            }

            if (crewman.Self.life != null && crewman.Self.life.isDead)
            {
                return;
            }

            // Stop holding the vehicle still first. A driver that climbs out with a destination
            // still set would go on feeding driving packets to a seat it no longer occupies, and
            // the men who just got out are standing at the bumper.
            crewman.Driver?.StopDriving();
            crewman.Driver?.TryExit(out _);
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
