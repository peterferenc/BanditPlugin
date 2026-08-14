using System.Linq;
using System.Reflection;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.FakePlayer
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
                Logger.LogError($"[Bandit] Could not reflect Provider/Player members needed to spawn a fake player: {string.Join(", ", missing)}. The game version may have changed internal method names/signatures.");
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
                Logger.LogError($"[Bandit] Provider.addPlayer threw while spawning fake player: {e.InnerException}");
                return null;
            }

            if (steamPlayer?.player == null)
            {
                Logger.LogError("[Bandit] Provider.addPlayer did not return a usable SteamPlayer/Player.");
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

            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;
            BanditLoadoutApplier.Result loadout = config.ApplyLoadout
                ? BanditLoadoutApplier.Apply(steamPlayer.player, config.Loadout)
                : default(BanditLoadoutApplier.Result);

            BanditBotController controller = steamPlayer.player.gameObject.AddComponent<BanditBotController>();
            controller.Self = steamPlayer.player;
            controller.SteamPlayerToKeepAlive = steamPlayer;
            controller.TurnSpeedDegreesPerSecond = config.TurnSpeedDegreesPerSecond;
            controller.ScanIntervalSeconds = config.ScanIntervalSeconds;
            controller.FireIntervalSeconds = config.FireIntervalSeconds;
            controller.AimToleranceDegrees = config.AimToleranceDegrees;
            controller.FireRange = config.FireRange;
            controller.InfiniteAmmo = config.InfiniteAmmo;
            controller.HasPrimaryWeapon = loadout.HasPrimaryWeapon;
            controller.HasSecondaryWeapon = loadout.HasSecondaryWeapon;
            controller.SecondaryWeaponRange = config.SecondaryWeaponRange;
            controller.PrimaryAimHitChance = ResolveHitChance(config.Loadout?.PrimaryWeapon, config.AimHitChance);
            controller.SecondaryAimHitChance = ResolveHitChance(config.Loadout?.SecondaryWeapon, config.AimHitChance);
            controller.AimTargetRadius = config.AimTargetRadius;
            controller.AimTargetHalfHeight = config.AimTargetHalfHeight;
            controller.AimMaxErrorDegrees = config.AimMaxErrorDegrees;
            controller.AimWobbleIntervalSeconds = config.AimWobbleIntervalSeconds;
            controller.AimWobbleSmoothingSeconds = config.AimWobbleSmoothingSeconds;
            controller.RequireLineOfSight = config.RequireLineOfSight;

            SpawnedBotSteamIds.Add(fakeSteamId.m_SteamID);
            LastSpawnedController = controller;
            return steamPlayer.player;
        }

        /// <summary>
        /// The bot most recently spawned, or null if it has since been removed or killed. Commands
        /// that act on "the bandit" use this. Validated on read rather than cleared on despawn,
        /// because a bot can also leave via a kick or a server-side death we don't hook.
        /// </summary>
        public static BanditBotController LastSpawnedController
        {
            get => _lastSpawnedController != null && _lastSpawnedController.Self != null
                ? _lastSpawnedController
                : null;
            private set => _lastSpawnedController = value;
        }

        private static BanditBotController _lastSpawnedController;

        /// <summary>
        /// Removes one bot - used when a killed bandit's despawn timer runs out. Same kick path as
        /// RemoveAllBots, but also drops the SteamID so the bot stops being counted as live.
        /// </summary>
        public static void DespawnBot(SteamPlayer steamPlayer)
        {
            if (steamPlayer == null || ReferenceEquals(steamPlayer.playerID, null))
            {
                return;
            }

            ulong steamId = steamPlayer.playerID.steamID.m_SteamID;
            if (!SpawnedBotSteamIds.Remove(steamId))
            {
                return; // not ours, or already removed
            }

            Provider.kick(steamPlayer.playerID.steamID, "bandit killed");
        }

        /// <summary>
        /// Every live bot's controller. Walks Provider.clients rather than keeping a list, so a bot
        /// that was kicked or otherwise removed can't linger as a stale reference.
        /// </summary>
        public static System.Collections.Generic.List<BanditBotController> GetActiveControllers()
        {
            System.Collections.Generic.List<BanditBotController> controllers =
                new System.Collections.Generic.List<BanditBotController>();

            foreach (SteamPlayer client in Provider.clients)
            {
                if (client?.player == null || ReferenceEquals(client.playerID, null))
                {
                    continue;
                }

                if (!SpawnedBotSteamIds.Contains(client.playerID.steamID.m_SteamID))
                {
                    continue;
                }

                BanditBotController controller = client.player.GetComponent<BanditBotController>();
                if (controller != null)
                {
                    controllers.Add(controller);
                }
            }

            return controllers;
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
                Logger.LogError($"[Bandit] Could not attach RocketMod's player components to the bot; expect NullReferenceExceptions from Rocket's event handlers. {e}");
            }
        }

        /// <summary>
        /// A weapon's own hit chance if it sets one, otherwise the global figure. Kept here rather
        /// than in the loadout applier because it is the only part of a loadout entry the bot's
        /// combat code cares about, and the controller wants it resolved once at spawn.
        /// </summary>
        private static float ResolveHitChance(BanditWeapon weapon, float fallback)
        {
            return weapon != null && weapon.AimHitChance >= 0f ? weapon.AimHitChance : fallback;
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
