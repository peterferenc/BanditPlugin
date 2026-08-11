using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SDG.NetPak;
using SDG.NetTransport;
using SDG.Unturned;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditZombiePlugin.FakePlayer
{
    /// <summary>
    /// Replicates the two broadcast steps that Provider's real remote-player join path
    /// (Provider.accept(SteamPlayerID, ...) around the "addPlayer" call) does and that
    /// Provider.addPlayer() alone does NOT do: telling every other already-connected client
    /// that a new player exists (WriteConnectedMessage/PlayerConnected), and sending that new
    /// player's own clothing/appearance state out to them (SendInitialPlayerState with the
    /// connections-list overload). Confirmed via decompiling the real Assembly-CSharp.dll that
    /// skipping these is exactly why a bot spawned via addPlayer() alone is invisible to anyone
    /// already connected - addPlayer() itself only fires a local C# event
    /// (Provider.broadcastEnemyConnected), it does not touch the network at all.
    ///
    /// NetMessages, its ClientWriteHandler delegate, and both of these methods are all
    /// private/internal, so this is reflection end to end. NetPakWriter itself is public, which
    /// is what makes constructing a compatible delegate for ClientWriteHandler possible at all.
    /// </summary>
    public static class PlayerJoinBroadcaster
    {
        private static readonly MethodInfo WriteConnectedMessageMethod =
            typeof(Provider).GetMethod("WriteConnectedMessage", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly MethodInfo GatherRemoteClientConnectionsMatchingPredicateMethod =
            typeof(Provider).GetMethod("GatherRemoteClientConnectionsMatchingPredicate", BindingFlags.Public | BindingFlags.Static);

        private static readonly MethodInfo SendInitialPlayerStateToConnectionsMethod = typeof(Player)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "SendInitialPlayerState"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(List<ITransportConnection>));

        private static readonly System.Type NetMessagesType =
            typeof(Provider).Assembly.GetType("SDG.Unturned.NetMessages");

        // NOTE: must pass BOTH Public and NonPublic here. ClientWriteHandler is declared *public*
        // inside NetMessages, which is itself internal - so Type.IsPublic is false (it reports on
        // the whole visibility chain) while IsNestedPublic is true. Looking it up with only
        // BindingFlags.NonPublic silently returns null.
        private static readonly System.Type ClientWriteHandlerType =
            NetMessagesType?.GetNestedType("ClientWriteHandler", BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly MethodInfo SendMessageToClientMethod = NetMessagesType?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "SendMessageToClient" && m.GetParameters().Length == 4);

        /// <summary>
        /// Names whichever reflected members failed to resolve, so a future game update that
        /// renames one of them produces an actionable log line instead of a vague one.
        /// </summary>
        private static string DescribeMissingMembers()
        {
            List<string> missing = new List<string>();
            if (WriteConnectedMessageMethod == null) missing.Add("Provider.WriteConnectedMessage");
            if (GatherRemoteClientConnectionsMatchingPredicateMethod == null) missing.Add("Provider.GatherRemoteClientConnectionsMatchingPredicate");
            if (SendInitialPlayerStateToConnectionsMethod == null) missing.Add("Player.SendInitialPlayerState(List<ITransportConnection>)");
            if (NetMessagesType == null) missing.Add("SDG.Unturned.NetMessages");
            if (ClientWriteHandlerType == null) missing.Add("NetMessages.ClientWriteHandler");
            if (SendMessageToClientMethod == null) missing.Add("NetMessages.SendMessageToClient");
            return string.Join(", ", missing);
        }

        public static void AnnounceNewPlayerToExistingClients(SteamPlayer newBot)
        {
            string missingMembers = DescribeMissingMembers();
            if (missingMembers.Length > 0)
            {
                Logger.LogError($"[BanditZombie] Could not reflect the player-join broadcast members needed to make the bot visible: {missingMembers}. The game version may have changed internal names/signatures.");
                return;
            }

            foreach (SteamPlayer existingClient in Provider.clients)
            {
                if (existingClient == newBot)
                {
                    continue;
                }

                object callback = CreateWriteConnectedMessageDelegate(newBot, existingClient);
                SendMessageToClientMethod.Invoke(null, new object[]
                {
                    EClientMessage.PlayerConnected,
                    ENetReliability.Reliable,
                    existingClient.transportConnection,
                    callback
                });
            }

            object remoteConnections = GatherRemoteClientConnectionsMatchingPredicateMethod.Invoke(
                null,
                new object[] { (System.Predicate<SteamPlayer>)(p => p != newBot) });

            SendInitialPlayerStateToConnectionsMethod.Invoke(newBot.player, new object[] { remoteConnections });
        }

        private static object CreateWriteConnectedMessageDelegate(SteamPlayer aboutPlayer, SteamPlayer forPlayer)
        {
            WriteConnectedMessageCallback callbackTarget = new WriteConnectedMessageCallback(aboutPlayer, forPlayer);
            MethodInfo callbackMethod = typeof(WriteConnectedMessageCallback).GetMethod(nameof(WriteConnectedMessageCallback.Invoke));
            return System.Delegate.CreateDelegate(ClientWriteHandlerType, callbackTarget, callbackMethod);
        }

        private sealed class WriteConnectedMessageCallback
        {
            private readonly SteamPlayer _aboutPlayer;
            private readonly SteamPlayer _forPlayer;

            public WriteConnectedMessageCallback(SteamPlayer aboutPlayer, SteamPlayer forPlayer)
            {
                _aboutPlayer = aboutPlayer;
                _forPlayer = forPlayer;
            }

            public void Invoke(NetPakWriter writer)
            {
                WriteConnectedMessageMethod.Invoke(null, new object[] { writer, _aboutPlayer, _forPlayer });
            }
        }
    }
}
