using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/bandit clear [target]" - removes bandits, and whatever vehicles they came with.
    ///
    /// Like every other command, no target means the last thing spawned - the reason to reach for
    /// this is usually that the thing you just spawned is doing something you no longer want to
    /// watch, and there may well be something else running that you do. "/bandit clear all" is the
    /// big hammer: every bandit, every vehicle this plugin spawned, every squad, event and convoy.
    /// Use that one before disconnecting - bots occupy player slots, and their presence affects the
    /// client-side exit timer.
    /// </summary>
    public static class BanditClearCommand
    {
        public static void Execute(IRocketPlayer caller, string[] args)
        {
            if (!BanditTargets.TryResolve(args, 0, out BanditTarget target, out _, out string error))
            {
                Reply(caller, error, Color.red);
                return;
            }

            switch (target.Kind)
            {
                case BanditTargetKind.All:
                    ClearAll(caller);
                    break;

                case BanditTargetKind.Convoy:
                    BanditConvoy.Clear(target.Convoy, out string convoySummary);
                    Reply(caller, $"Cleared {convoySummary}.", Color.green);
                    break;

                case BanditTargetKind.Event:
                    // A convoy is an event underneath. Cleared as the convoy, or the column would
                    // be left steering vehicles that no longer exist.
                    BanditConvoy convoy = BanditTargets.ConvoyOf(target.Event);
                    if (convoy != null)
                    {
                        BanditConvoy.Clear(convoy, out string summary);
                        Reply(caller, $"Cleared {summary}.", Color.green);
                        break;
                    }

                    target.Event.Clear(out int men, out int vehicles);
                    Reply(caller, $"Cleared event {target.Event.Id}: {men} bandit(s) and {vehicles} vehicle(s).",
                        Color.green);
                    break;

                case BanditTargetKind.Vehicle:
                    ClearRide(caller, target.Ride);
                    break;

                default:
                    ClearBandits(caller, target);
                    break;
            }
        }

        /// <summary>
        /// Despawns the bandits a target covers. Their vehicles stay - a squad or a team does not
        /// own one - and so does any squad they leave empty, which the next prune drops.
        /// </summary>
        private static void ClearBandits(IRocketPlayer caller, BanditTarget target)
        {
            int removed = 0;
            foreach (BanditBotController bandit in target.Members())
            {
                removed += FakePlayerSpawner.Despawn(bandit) ? 1 : 0;
            }

            if (target.Kind == BanditTargetKind.Squad)
            {
                BanditSquad.All.Remove(target.Squad);
            }

            Reply(caller, $"Cleared {target.Label}: {removed} bandit(s).", Color.green);
        }

        /// <summary>One vehicle and everyone who belongs to it, wherever they are now.</summary>
        private static void ClearRide(IRocketPlayer caller, BanditEvent.Ride ride)
        {
            int removed = 0;
            foreach (BanditBotController crewman in BanditEvent.CrewOf(ride))
            {
                removed += FakePlayerSpawner.Despawn(crewman) ? 1 : 0;
            }

            ride.Finished = true;
            if (ride.Vehicle != null && !ride.Vehicle.isExploded)
            {
                VehicleManager.askVehicleDestroy(ride.Vehicle);
            }

            ride.Event.Rides.Remove(ride);

            // A vehicle spawned on its own is a one-vehicle event, and goes with it.
            if (ride.Event.Rides.Count == 0 && ride.Event.BanditCount == 0)
            {
                BanditEvent.All.Remove(ride.Event);
            }

            Reply(caller, $"Cleared vehicle {ride.Id} ({ride.TypeName}) and {removed} bandit(s).", Color.green);
        }

        private static void ClearAll(IRocketPlayer caller)
        {
            int removed = FakePlayerSpawner.RemoveAllBots();

            // The bots are kicked above, so every squad is now empty - dropping them here rather
            // than waiting for the next prune keeps a cleared field from leaving squad objects
            // holding references to players that no longer exist.
            BanditSquad.ClearAll();
            BanditEvent.ClearAll();
            BanditConvoy.ClearAll();
            BanditTargets.Forget();

            // Vehicles outlive their crews completely, so kicking the bots is not enough: without
            // this, an afternoon of events leaves the map covered in abandoned trucks. Only the ones
            // this plugin spawned are touched - anything a mapper placed stays put.
            int vehicles = BanditVehicleSpawner.DestroyAll();

            Reply(caller, removed > 0 || vehicles > 0
                ? $"Removed {removed} bandit(s) and {vehicles} spawned vehicle(s)."
                : "No bandits or spawned vehicles to remove.", Color.green);
        }
    }
}
