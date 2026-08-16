using System.Collections.Generic;
using System.Reflection;
using SDG.NetTransport;
using SDG.Unturned;
using Steamworks;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.FakePlayer
{
    /// <summary>
    /// Seats a player in a *named* vehicle seat.
    ///
    /// Vanilla's public server-side entry point - VehicleManager.ServerForcePassengerIntoVehicle -
    /// takes the first free seat and gives you no say in which one, so a bandit ordered into the
    /// gunner's seat lands in the driver's instead whenever that one happens to be empty. The RPC
    /// underneath it does carry a seat index; it is simply internal, so it is reached by reflection
    /// the same way the bots reach PlayerInput.serversidePackets.
    ///
    /// InvokeAndLoopback is what makes one call do the whole job: every remote client is told to
    /// render the bandit in that seat, and the server runs its own ReceiveEnterVehicle, which is
    /// what actually calls InteractableVehicle.addPlayer - parenting the player to the seat
    /// transform, equipping the seat's turret, and setting the stance to DRIVING or SITTING.
    ///
    /// The preconditions below are ServerForcePassengerIntoVehicle's own, restated because going
    /// around the front door means going around its checks too. Deliberately *not* restated is its
    /// lock check - there isn't one; forcing a passenger in ignores vehicle locks, and so does this.
    /// </summary>
    public static class VehicleSeatTool
    {
        private static readonly FieldInfo SendEnterVehicleField =
            typeof(VehicleManager).GetField("SendEnterVehicle", BindingFlags.NonPublic | BindingFlags.Static);

        private static object _sender;
        private static MethodInfo _invokeAndLoopback;
        private static bool _resolveAttempted;

        /// <summary>
        /// Whether seat-specific entry is available at all. False means the game version moved the
        /// RPC; callers should fall back to ServerForcePassengerIntoVehicle and accept its seat.
        /// </summary>
        public static bool IsAvailable => Resolve();

        public static bool TrySeat(Player player, InteractableVehicle vehicle, byte seatIndex, out string error)
        {
            if (player == null || player.life == null || player.life.isDead)
            {
                error = "the bandit is dead";
                return false;
            }

            if (player.movement == null || player.movement.getVehicle() != null)
            {
                error = "it is already in a vehicle";
                return false;
            }

            // Vanilla refuses mid-shot and mid-equip, and it is right to: addPlayer swaps the
            // useable out from under an animation that is still playing.
            if (player.equipment != null
                && (player.equipment.isBusy
                    || (player.equipment.HasValidUseable && !player.equipment.IsEquipAnimationFinished)))
            {
                error = "it is mid-shot or mid-equip - try again in a second";
                return false;
            }

            if (vehicle == null || vehicle.isDead || vehicle.isExploded)
            {
                error = "the vehicle is wrecked";
                return false;
            }

            if (!vehicle.isExitable)
            {
                error = "the vehicle has no safe exit point beside it";
                return false;
            }

            Passenger[] seats = vehicle.passengers;
            if (seats == null || seatIndex >= seats.Length || seats[seatIndex] == null)
            {
                error = $"the vehicle has no seat {seatIndex} (it has {(seats == null ? 0 : seats.Length)})";
                return false;
            }

            if (seats[seatIndex].player != null)
            {
                error = $"seat {seatIndex} is taken";
                return false;
            }

            if (!Resolve())
            {
                error = "the seat-entry RPC could not be reflected - game version may have changed";
                return false;
            }

            CSteamID steamId = player.channel.owner.playerID.steamID;
            _invokeAndLoopback.Invoke(_sender, new object[]
            {
                ENetReliability.Reliable,
                Provider.GatherRemoteClientConnections(),
                vehicle.instanceID,
                seatIndex,
                steamId
            });

            error = null;
            return true;
        }

        private static bool Resolve()
        {
            if (_resolveAttempted)
            {
                return _invokeAndLoopback != null;
            }
            _resolveAttempted = true;

            if (SendEnterVehicleField == null)
            {
                Logger.LogError("[Bandit] VehicleManager.SendEnterVehicle not found; bandits cannot pick a seat.");
                return false;
            }

            _sender = SendEnterVehicleField.GetValue(null);
            if (_sender == null)
            {
                Logger.LogError("[Bandit] VehicleManager.SendEnterVehicle was null; bandits cannot pick a seat.");
                return false;
            }

            // Bound to the List<ITransportConnection> overload by exact signature rather than by
            // name: ClientStaticMethod has five InvokeAndLoopback/Invoke overloads, and
            // GatherRemoteClientConnections returns a PooledTransportConnectionList, which is one.
            _invokeAndLoopback = _sender.GetType().GetMethod("InvokeAndLoopback", new[]
            {
                typeof(ENetReliability),
                typeof(List<ITransportConnection>),
                typeof(uint),
                typeof(byte),
                typeof(CSteamID)
            });

            if (_invokeAndLoopback == null)
            {
                Logger.LogError("[Bandit] SendEnterVehicle.InvokeAndLoopback(reliability, connections, uint, byte, CSteamID) "
                    + "not found; bandits cannot pick a seat.");
            }

            return _invokeAndLoopback != null;
        }
    }
}
