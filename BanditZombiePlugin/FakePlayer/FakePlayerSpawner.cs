using System.Linq;
using System.Reflection;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditZombiePlugin.FakePlayer
{
    /// <summary>
    /// Spawns a bot as a real Player entity (not a Zombie) using a fabricated, never-connected
    /// ITransportConnection. Mirrors the exact sequence Provider.cs uses to spawn the host's own
    /// local player in singleplayer/listen-server mode (Provider.onLevelLoaded, around the
    /// "isClient" branch) - confirmed via decompiling the real Assembly-CSharp.dll, not guessed
    /// from possibly-mismatched GitHub source. That reference sequence is:
    ///   ClaimNetIdBlockForNewPlayer() -> addPlayer(...) -> player.InitializePlayer() ->
    ///   player.SendInitialPlayerState(steamPlayer)
    /// All of these are internal/private, so every call here goes through reflection.
    ///
    /// This was chosen over a custom-skinned Zombie after live testing proved zombies added
    /// mid-session are permanently invisible to any client that already loaded that map region -
    /// there is no vanilla RPC that can introduce a new zombie ID to an already-loaded client (see
    /// git history / conversation for the full investigation). Players, by contrast, are broadcast
    /// to every already-connected client unconditionally via Provider.broadcastEnemyConnected(),
    /// which is exercised by every real player join and has none of that staleness problem.
    /// </summary>
    public static class FakePlayerSpawner
    {
        private static readonly MethodInfo AllocPlayerChannelIdMethod =
            typeof(Provider).GetMethod("allocPlayerChannelId", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly MethodInfo ClaimNetIdBlockMethod =
            typeof(Provider).GetMethod("ClaimNetIdBlockForNewPlayer", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly MethodInfo AddPlayerMethod = typeof(Provider)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "addPlayer" && m.GetParameters().Length == 30);

        private static readonly MethodInfo InitializePlayerMethod =
            typeof(Player).GetMethod("InitializePlayer", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo SendInitialPlayerStateMethod = typeof(Player)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "SendInitialPlayerState"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(SteamPlayer));

        // Synthetic Steam64 IDs for bots, deliberately far from the real individual-account range
        // to avoid any chance of colliding with a genuine connected player's SteamID.
        private const ulong FakeSteamIdBase = 0x1100001000000001UL;
        private static ulong _nextFakeSteamId = FakeSteamIdBase;

        /// <summary>SteamIDs of every bot this plugin has spawned, so /banditclear can remove them.
        /// Tracked as raw ulongs rather than SteamPlayer references to avoid SteamPlayerID's
        /// null-unsafe operator== (see RemoveAllBots).</summary>
        public static readonly System.Collections.Generic.HashSet<ulong> SpawnedBotSteamIds =
            new System.Collections.Generic.HashSet<ulong>();

        private const byte AmmoStateIndex = 10;     // PlayerEquipment.state[10] == rounds in magazine
        private const byte FiremodeStateIndex = 11; // PlayerEquipment.state[11] == EFiremode

        public static Player Spawn(Vector3 position, float angleDegrees, string displayName)
        {
            System.Collections.Generic.List<string> missing = new System.Collections.Generic.List<string>();
            if (AllocPlayerChannelIdMethod == null) missing.Add("Provider.allocPlayerChannelId");
            if (ClaimNetIdBlockMethod == null) missing.Add("Provider.ClaimNetIdBlockForNewPlayer");
            if (AddPlayerMethod == null) missing.Add("Provider.addPlayer");
            if (InitializePlayerMethod == null) missing.Add("Player.InitializePlayer");
            if (SendInitialPlayerStateMethod == null) missing.Add("Player.SendInitialPlayerState(SteamPlayer)");
            if (missing.Count > 0)
            {
                Logger.LogError($"[BanditZombie] Could not reflect Provider/Player members needed to spawn a fake player: {string.Join(", ", missing)}. The game version may have changed internal method names/signatures.");
                return null;
            }

            CSteamID fakeSteamId = new CSteamID(_nextFakeSteamId++);
            SteamPlayerID playerID = new SteamPlayerID(
                fakeSteamId,
                newCharacterID: 0,
                newPlayerName: displayName,
                newCharacterName: displayName,
                newNickName: displayName,
                newGroup: CSteamID.Nil);

            byte angleByte = (byte)Mathf.RoundToInt(angleDegrees / 2f);
            object netId = ClaimNetIdBlockMethod.Invoke(null, null);
            object channel = AllocPlayerChannelIdMethod.Invoke(null, null);

            object[] addPlayerArgs =
            {
                new FakeTransportConnection(),
                netId,
                playerID,
                position,
                angleByte,
                /* isPro */ false,
                /* isAdmin */ false,
                channel,
                /* face */ (byte)0,
                /* hair */ (byte)0,
                /* beard */ (byte)0,
                /* skin */ Color.white,
                /* color */ Color.white,
                /* markerColor */ Color.white,
                /* beardColor */ Color.black,
                /* hand */ false,
                /* shirtItem */ 0,
                /* pantsItem */ 0,
                /* hatItem */ 0,
                /* backpackItem */ 0,
                /* vestItem */ 0,
                /* maskItem */ 0,
                /* glassesItem */ 0,
                /* skinItems */ new int[0],
                /* skinTags */ new string[0],
                /* skinDynamicProps */ new string[0],
                /* skillset */ EPlayerSkillset.NONE,
                /* language */ "en",
                /* lobbyID */ CSteamID.Nil,
                /* clientPlatform */ EClientPlatform.Windows
            };

            SteamPlayer steamPlayer;
            try
            {
                steamPlayer = (SteamPlayer)AddPlayerMethod.Invoke(null, addPlayerArgs);
            }
            catch (TargetInvocationException e)
            {
                Logger.LogError($"[BanditZombie] Provider.addPlayer threw while spawning fake player: {e.InnerException}");
                return null;
            }

            if (steamPlayer?.player == null)
            {
                Logger.LogError("[BanditZombie] Provider.addPlayer did not return a usable SteamPlayer/Player.");
                return null;
            }

            InitializePlayerMethod.Invoke(steamPlayer.player, null);
            SendInitialPlayerStateMethod.Invoke(steamPlayer.player, new object[] { steamPlayer });

            // addPlayer() alone never touches the network - it only fires a local C# event. This
            // is what actually tells every other already-connected client that the bot exists and
            // sends them its appearance data. Without it the bot is a fully functional server-side
            // Player that nobody else's client ever renders.
            PlayerJoinBroadcaster.AnnounceNewPlayerToExistingClients(steamPlayer);

            AttachRocketPlayerComponents(steamPlayer.player);

            BanditZombieConfiguration config = BanditZombiePlugin.Instance.Configuration.Instance;
            if (config.GiveGun)
            {
                GiveAndEquipGun(steamPlayer.player, config);
            }

            BanditBotController controller = steamPlayer.player.gameObject.AddComponent<BanditBotController>();
            controller.Self = steamPlayer.player;
            controller.SteamPlayerToKeepAlive = steamPlayer;
            controller.TurnSpeedDegreesPerSecond = config.TurnSpeedDegreesPerSecond;
            controller.ScanIntervalSeconds = config.ScanIntervalSeconds;
            controller.FireIntervalSeconds = config.FireIntervalSeconds;
            controller.AimToleranceDegrees = config.AimToleranceDegrees;
            controller.FireRange = config.FireRange;
            controller.InfiniteAmmo = config.InfiniteAmmo;
            controller.AimHitChance = config.AimHitChance;
            controller.AimTargetRadius = config.AimTargetRadius;
            controller.AimTargetHalfHeight = config.AimTargetHalfHeight;
            controller.AimMaxErrorDegrees = config.AimMaxErrorDegrees;
            controller.AimWobbleIntervalSeconds = config.AimWobbleIntervalSeconds;
            controller.AimWobbleSmoothingSeconds = config.AimWobbleSmoothingSeconds;
            controller.RequireLineOfSight = config.RequireLineOfSight;

            SpawnedBotSteamIds.Add(fakeSteamId.m_SteamID);
            return steamPlayer.player;
        }

        /// <summary>
        /// Gives the bot the three per-player components RocketMod attaches to everyone who joins.
        ///
        /// Rocket hooks Provider.onServerConnected and does exactly this - TryAddComponent of
        /// UnturnedPlayerFeatures, UnturnedPlayerMovement and UnturnedPlayerEvents - but a bot is
        /// never accepted through Provider, so that hook never fires for one. Meanwhile Rocket
        /// subscribes its handlers to the *global* statics (PlayerLife.OnTellHealth_Global and
        /// friends), which fire for every player alive on the server, bot or not. Those handlers
        /// open with an unguarded GetComponent&lt;UnturnedPlayerEvents&gt;(), so every time a bot
        /// got shot, starved or grew thirsty the server logged a NullReferenceException out of
        /// Rocket - and, because the throw unwound back through PlayerLife.doDamage into
        /// UseableGun.ballistics, whatever vanilla meant to do after telling health was skipped.
        ///
        /// Attaching the components makes those lookups succeed and the bot a first-class Rocket
        /// player. Side effect, and the reason this is worth knowing about: UnturnedPlayerEvents'
        /// own Start() raises Rocket's OnPlayerConnected for the bot, so other plugins will see a
        /// bot spawn as a player join.
        /// </summary>
        private static void AttachRocketPlayerComponents(Player player)
        {
            try
            {
                GameObject gameObject = player.gameObject;

                if (gameObject.GetComponent<Rocket.Unturned.Player.UnturnedPlayerFeatures>() == null)
                {
                    gameObject.AddComponent<Rocket.Unturned.Player.UnturnedPlayerFeatures>();
                }

                if (gameObject.GetComponent<Rocket.Unturned.UnturnedPlayerMovement>() == null)
                {
                    gameObject.AddComponent<Rocket.Unturned.UnturnedPlayerMovement>();
                }

                if (gameObject.GetComponent<Rocket.Unturned.Events.UnturnedPlayerEvents>() == null)
                {
                    gameObject.AddComponent<Rocket.Unturned.Events.UnturnedPlayerEvents>();
                }
            }
            catch (System.Exception e)
            {
                // Not fatal - the bot works without these, it just makes Rocket's global event
                // handlers throw on it. Better a spammy log than no bot.
                Logger.LogError($"[BanditZombie] Could not attach RocketMod's player components to the bot; expect NullReferenceExceptions from Rocket's event handlers. {e}");
            }
        }

        /// <summary>
        /// PlayerInventory.forceAddItem(item, auto: true) routes a primary-slot weapon through
        /// tryAddItemEquip, which calls PlayerEquipment.ServerEquip - and ServerEquip replicates to
        /// everyone via SendEquip.InvokeAndLoopback(... GatherRemoteClientConnections() ...). So the
        /// gun becomes visible in the bot's hands to other players with no manual networking.
        /// (Verified by decompiling PlayerInventory/PlayerEquipment from the real Assembly-CSharp.dll.
        /// Note the Dummy project has no equip support at all - "todo: simulate useable" - so this
        /// path is not borrowed from it.)
        /// </summary>
        private static void GiveAndEquipGun(Player player, BanditZombieConfiguration config)
        {
            ItemAsset gunAsset = null;

            if (System.Guid.TryParse(config.GunAssetGuid, out System.Guid gunGuid) && gunGuid != System.Guid.Empty)
            {
                gunAsset = Assets.find(gunGuid) as ItemAsset;
            }

            if (gunAsset == null && config.GunAssetLegacyId != 0)
            {
                gunAsset = Assets.find(EAssetType.ITEM, config.GunAssetLegacyId) as ItemAsset;
            }

            if (gunAsset == null)
            {
                Logger.LogError($"[BanditZombie] Could not resolve a gun asset from GUID '{config.GunAssetGuid}' or legacy ID {config.GunAssetLegacyId}; bot will spawn empty-handed.");
                return;
            }

            Item gun = new Item(gunAsset.id, true);

            // UseableGun.equip() caches ammo from state[10] and firemode from state[11], so both
            // have to be right BEFORE the item is equipped. Firemode especially: startPrimary()
            // refuses to fire while it's SAFETY (0), which is a plausible default - so set it
            // explicitly. Doing it here (rather than simulating a firemode-toggle keypress) is
            // deterministic, and the state bytes ride along to every client in the SendEquip call.
            if (gun.state != null && gun.state.Length > FiremodeStateIndex)
            {
                gun.state[AmmoStateIndex] = config.MagazineCapacity;
                gun.state[FiremodeStateIndex] = (byte)EFiremode.SEMI;
            }

            player.inventory.forceAddItem(gun, true);
        }

        /// <summary>
        /// Removes every bot this plugin spawned. Uses the public Provider.kick(), which is how the
        /// Dummy project removes its dummies too - a server-side kick sidesteps the client-side exit
        /// timer entirely rather than going through a disconnect request.
        /// </summary>
        public static int RemoveAllBots()
        {
            int removed = 0;

            // Match by SteamID rather than by SteamPlayer reference. SteamPlayerID overloads
            // operator== WITHOUT a null guard (it dereferences both sides to compare .steamID), so
            // an innocent-looking "steamPlayer.playerID == null" throws NullReferenceException -
            // which is exactly what broke the first version of this command.
            foreach (SteamPlayer client in Provider.clients.ToArray())
            {
                if (client == null || ReferenceEquals(client.playerID, null))
                {
                    continue;
                }

                ulong steamId = client.playerID.steamID.m_SteamID;
                if (!SpawnedBotSteamIds.Contains(steamId))
                {
                    continue;
                }

                Provider.kick(client.playerID.steamID, "bandit despawned");
                removed++;
            }

            SpawnedBotSteamIds.Clear();
            return removed;
        }
    }
}
