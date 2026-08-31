using System.Reflection;
using HarmonyLib;
using SDG.Unturned;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.FakePlayer
{
    /// <summary>
    /// Stops bandits from eating the server's player slots.
    ///
    /// A bandit is a real entry in <see cref="Provider.clients"/> - that is the whole point of
    /// spawning it as a player rather than a zombie - and vanilla decides whether the server is
    /// full by counting that list. So eleven bandits on an eight-slot server make it full for
    /// real people. Decompiling the whole of Assembly-CSharp finds FOUR places that gate on
    /// capacity, and a fix has to cover all of them - patching only the first two gets a player
    /// through the queue and then rejected at authentication with "this server's player list and
    /// queue are now full", which is how this was found:
    ///
    ///   Provider.hasRoomForNewConnection            clients.Count >= maxPlayers
    ///       - the transport layer, deciding whether to accept the socket at all
    ///   Provider.verifyNextPlayerInQueue            clients.Count &lt; maxPlayers
    ///       - promoting the front of the queue into a slot
    ///   ServerMessageHandler_ReadyToConnect         clients.Count + 1 > maxPlayers
    ///                                                 &amp;&amp; pending.Count + 1 > queueSize
    ///   ServerMessageHandler_Authenticate           clients.Count + 1 > maxPlayers
    ///       - rejects with ESteamRejection.SERVER_FULL
    ///
    /// Rather than reimplement four different conditions - two of which live inside long packet
    /// handlers that cannot sensibly be rewritten in a prefix - each of those methods is wrapped
    /// so that for the duration of the call, and only for that duration, Provider.maxPlayers reads
    /// as the configured maximum plus the number of bandits connected. Vanilla's own logic then
    /// reaches the right answer on its own, and keeps doing so if a future version changes how it
    /// asks the question.
    ///
    /// The inflated figure is written straight to Provider's private _maxPlayers field, NOT
    /// through the public setter, because the setter pushes it to SteamGameServer.SetMaxPlayerCount
    /// - which would advertise a capacity the server does not have, and would do it every frame.
    /// Nothing inside the four wrapped methods sends maxPlayers to a client: the verify packet has
    /// an empty payload and Provider.accept never reads it (checked against the decompile). The
    /// connect response that DOES carry it, and that clients compare against the browser
    /// advertisement before hard-disconnecting on a mismatch, is written by a different message
    /// handler entirely, so it always sees the honest number.
    /// </summary>
    public static class BanditSlotPatches
    {
        private const string HarmonyId = "com.peterferenc.banditplugin.slots";

        private static readonly FieldInfo MaxPlayersField =
            typeof(Provider).GetField("_maxPlayers", BindingFlags.NonPublic | BindingFlags.Static);

        private static Harmony _harmony;

        /// <summary>
        /// Nesting depth of the wrapped calls, so that one patched method calling another - which
        /// vanilla does, RemoveClient runs verifyNextPlayerInQueue - inflates once and restores
        /// once instead of stacking bandit counts on top of each other.
        /// </summary>
        private static int _depth;

        private static bool _isInflated;
        private static byte _honestMaxPlayers;
        private static bool _warnedAboutClamping;

        public static void Apply()
        {
            if (_harmony != null)
            {
                return;
            }

            if (MaxPlayersField == null)
            {
                Logger.LogError("[Bandit] Could not find Provider._maxPlayers, so bandits will keep "
                    + "taking up player slots and a server full of them will refuse real players. "
                    + "The game version may have renamed it.");
                return;
            }

            MethodBase[] targets =
            {
                AccessTools.PropertyGetter(typeof(Provider), "hasRoomForNewConnection"),
                AccessTools.Method(typeof(Provider), "verifyNextPlayerInQueue"),
                // Both handlers are internal classes, so they cannot be named with typeof() here.
                AccessTools.Method(AccessTools.TypeByName("SDG.Unturned.ServerMessageHandler_ReadyToConnect"), "ReadMessage"),
                AccessTools.Method(AccessTools.TypeByName("SDG.Unturned.ServerMessageHandler_Authenticate"), "ReadMessage")
            };

            string[] names =
            {
                "Provider.hasRoomForNewConnection",
                "Provider.verifyNextPlayerInQueue",
                "ServerMessageHandler_ReadyToConnect.ReadMessage",
                "ServerMessageHandler_Authenticate.ReadMessage"
            };

            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] == null)
                {
                    Logger.LogError($"[Bandit] Could not find {names[i]} to patch, so bandits will "
                        + "keep taking up player slots. The game version may have changed internal "
                        + "names or signatures.");
                    return;
                }
            }

            try
            {
                HarmonyMethod prefix = new HarmonyMethod(
                    AccessTools.Method(typeof(BanditSlotPatches), nameof(InflateMaxPlayersPrefix)));
                HarmonyMethod finalizer = new HarmonyMethod(
                    AccessTools.Method(typeof(BanditSlotPatches), nameof(RestoreMaxPlayersFinalizer)));

                Harmony harmony = new Harmony(HarmonyId);
                foreach (MethodBase target in targets)
                {
                    // A finalizer rather than a postfix: a postfix does not run when the original
                    // throws, and the original here is a packet handler parsing attacker-supplied
                    // bytes. Leaving maxPlayers inflated after one bad packet would advertise the
                    // wrong capacity for the rest of the server's life.
                    harmony.Patch(target, prefix: prefix, finalizer: finalizer);
                }

                _harmony = harmony;
                Logger.Log("[Bandit] Bandits no longer count towards the server's player slots.");
            }
            catch (System.Exception e)
            {
                // Most likely 0Harmony.dll is not in Rocket's Libraries folder. Not fatal - every
                // other part of the plugin works without this - so say what is wrong and carry on.
                Logger.LogError("[Bandit] Could not patch the player-slot checks, so bandits will "
                    + "keep taking up player slots. Check that 0Harmony.dll is in Rocket/Libraries/. "
                    + e);
            }
        }

        public static void Remove()
        {
            if (_harmony == null)
            {
                return;
            }

            try
            {
                _harmony.UnpatchAll(HarmonyId);
            }
            catch (System.Exception e)
            {
                Logger.LogError($"[Bandit] Could not remove the player-slot patches: {e}");
            }
            finally
            {
                // Whatever happened to the patches, the game must not be left advertising an
                // inflated capacity because an unpatch landed between a prefix and its finalizer.
                if (_isInflated)
                {
                    MaxPlayersField.SetValue(null, _honestMaxPlayers);
                    _isInflated = false;
                }

                _depth = 0;
                _harmony = null;
            }
        }

        /// <summary>
        /// Makes Provider.maxPlayers read as "configured maximum + connected bandits" for the
        /// duration of one wrapped call.
        /// </summary>
        private static void InflateMaxPlayersPrefix()
        {
            if (_depth++ > 0)
            {
                return; // an outer wrapped call has already inflated it
            }

            int bots = CountBotClients();
            if (bots < 1)
            {
                return; // no bandits about: vanilla runs against the honest figure, untouched
            }

            _honestMaxPlayers = Provider.maxPlayers;

            int inflated = _honestMaxPlayers + bots;
            if (inflated > byte.MaxValue)
            {
                // maxPlayers is a byte, so this cannot represent the true figure. Clamping keeps
                // the slot count as high as it can go rather than wrapping around to a tiny number
                // and locking everyone out, which is what an unchecked cast would do.
                inflated = byte.MaxValue;
                if (!_warnedAboutClamping)
                {
                    _warnedAboutClamping = true;
                    Logger.LogWarning($"[Bandit] {bots} bandits plus {_honestMaxPlayers} player slots "
                        + "is more than the 255 the game can represent, so some slots stay occupied. "
                        + "Spawn fewer bandits if real players cannot get in.");
                }
            }

            MaxPlayersField.SetValue(null, (byte)inflated);
            _isInflated = true;
        }

        private static void RestoreMaxPlayersFinalizer()
        {
            if (_depth > 0)
            {
                _depth--;
            }

            if (_depth > 0 || !_isInflated)
            {
                return;
            }

            MaxPlayersField.SetValue(null, _honestMaxPlayers);
            _isInflated = false;
        }

        /// <summary>
        /// How many of the connected clients are bandits.
        ///
        /// Walks Provider.clients rather than just reading SpawnedBotSteamIds.Count, because that
        /// set only tracks the bots this plugin removed itself - an admin /kick, or anything else
        /// that takes a bot out from under us, would leave it counting a bot that is no longer
        /// connected. Over-counting is the dangerous direction: it would raise the cap past the
        /// configured maximum and let real players in beyond it. Counting the list is
        /// self-correcting and costs a walk of at most a few dozen entries.
        /// </summary>
        private static int CountBotClients()
        {
            int count = 0;
            foreach (SteamPlayer client in Provider.clients)
            {
                // ReferenceEquals, not ==: SteamPlayerID overloads operator== without a null guard,
                // so "client.playerID == null" dereferences and throws.
                if (client == null || ReferenceEquals(client.playerID, null))
                {
                    continue;
                }

                if (FakePlayerSpawner.SpawnedBotSteamIds.Contains(client.playerID.steamID.m_SteamID))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
