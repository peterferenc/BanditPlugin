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

        /// <summary>Spawns a bandit with no kit: the legacy loadout and the global figures.</summary>
        public static Player Spawn(Vector3 position, float angleDegrees, string displayName)
        {
            return Spawn(position, angleDegrees, displayName, null, null);
        }

        /// <summary>Spawns a bandit of a class onto whichever team the configuration defaults to.</summary>
        public static Player Spawn(Vector3 position, float angleDegrees, string displayName, BanditKit kit)
        {
            return Spawn(position, angleDegrees, displayName, kit, null);
        }

        /// <param name="kit">
        /// The class to spawn as, or null for the global configuration. Resolved into a
        /// <see cref="BanditProfile"/> here and then owned by that bandit, so editing the kit
        /// afterwards has no effect on it.
        /// </param>
        /// <param name="team">
        /// The side to put it on, or null for the configured default team. A team is a real in-game
        /// group, so this is what decides who the bandit shoots at and who it will not - see
        /// <see cref="BanditTeams"/>.
        /// </param>
        public static Player Spawn(Vector3 position, float angleDegrees, string displayName, BanditKit kit,
            BanditTeam team)
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

            // Never let the delete below reach a real person's savedata. The IDs generated here are
            // in universe 17, which Steam does not issue, so this can't trip today - but "can't"
            // rests entirely on FakeSteamIdBase, and the cost of that constant being edited into the
            // real range is silently wiping a player's character. Cheap to check, unrecoverable not to.
            if (IsRealIndividualAccount(fakeSteamId.m_SteamID))
            {
                Logger.LogError($"[Bandit] Refusing to spawn: generated bot SteamID {fakeSteamId.m_SteamID} decodes as a real individual Steam account, and spawning clears that ID's savedata. Check FakeSteamIdBase.");
                return null;
            }

            // Wipe anything left on disk under this synthetic SteamID before the player exists.
            //
            // The IDs come from a static counter that restarts at FakeSteamIdBase every time the
            // plugin loads, so bot #1 of this session is bot #1 of the last one - same SteamID, same
            // Servers/<id>/Players/<steamid>_0 folder. Player.InitializePlayer() then runs
            // PlayerInventory.load() and PlayerClothing.load(), which restore that folder's contents,
            // and the configured loadout is applied on top. Provider.kick saves the combined total on
            // the way out - Player.save() unconditionally saves clothing, inventory, life, skills and
            // quests, with no opt-out - so every spawn-then-clear cycle wrote back one more full set
            // of gear than the last. That is why bandits started dropping doubles, then triples.
            //
            // (Killing a bot instead of clearing it hides the problem rather than avoiding it:
            // PlayerInventory.save() deletes Inventory.dat outright when the player is dead and both
            // Lose_Weapons/Lose_Clothes are on, so only bots removed alive - /banditclear, or a
            // server shutdown - accumulate.)
            //
            // Deleting here beats deleting on despawn: it runs ahead of every load path, so a bot
            // starts from nothing no matter what a previous session left behind or how it ended.
            try
            {
                PlayerSavedata.deleteFolder(playerID);
            }
            catch (System.Exception e)
            {
                Logger.LogWarning($"[Bandit] Could not clear stale savedata for bot {fakeSteamId}: {e.Message}. It may spawn carrying gear from an earlier session.");
            }

            // Wrapped into 0-360 before halving, because the packed angle is a byte and the callers
            // hand over whatever the maths gave them. "eulerAngles.y + 180" reaches 540 and an
            // Atan2 facing goes negative; both then cast to a byte that has wrapped, which spawns
            // the bandit pointing somewhere unrelated to where it was told to face.
            float normalizedAngle = Mathf.Repeat(angleDegrees, 360f);
            byte angleByte = (byte)Mathf.RoundToInt(normalizedAngle / 2f);
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

            // The side it fights on, before anything else reads the bot: the group has to be set
            // before its first target scan, or it spends that scan deciding its own teammates are
            // targets. Done after the join has been broadcast, so every client already knows the
            // player the group state is about.
            //
            // A team that cannot be resolved leaves the bandit ungrouped rather than failing the
            // spawn - which is exactly how every bandit behaved before teams existed.
            BanditTeam resolvedTeam = team ?? BanditTeams.Default(config);
            if (resolvedTeam != null && !BanditTeams.Assign(steamPlayer.player, resolvedTeam))
            {
                Logger.LogWarning($"[Bandit] Could not put {displayName} on team '{resolvedTeam.Name}'. "
                    + "It will spawn on no team, and will treat every other team's bandits as friendly.");
            }

            // Everything the kit has a say in, folded against the global figures once, here. From
            // this point nothing downstream knows or cares whether a kit was involved.
            BanditProfile profile = kit != null
                ? BanditProfile.FromKit(config, kit)
                : BanditProfile.FromConfiguration(config);

            BanditLoadoutApplier.Result loadout = config.ApplyLoadout
                ? BanditLoadoutApplier.Apply(steamPlayer.player, profile.Loadout, profile.BurstFire)
                : default(BanditLoadoutApplier.Result);

            BanditBotController controller = steamPlayer.player.gameObject.AddComponent<BanditBotController>();
            controller.Self = steamPlayer.player;
            controller.SteamPlayerToKeepAlive = steamPlayer;
            controller.Profile = profile;
            controller.TurnSpeedDegreesPerSecond = config.TurnSpeedDegreesPerSecond;
            controller.ScanIntervalSeconds = config.ScanIntervalSeconds;
            controller.FireIntervalSeconds = profile.FireIntervalSeconds;
            controller.AimToleranceDegrees = config.AimToleranceDegrees;
            controller.FireRange = profile.FireRange;
            controller.TargetAcquireRange = profile.TargetAcquireRange;
            controller.SuppressiveFire = profile.SuppressiveFire;
            controller.DestroysCover = profile.DestroysCover;
            controller.SuppressionSeconds = config.SuppressionSeconds;
            controller.FriendlyFireClearanceRadius = config.FriendlyFireClearanceRadius;
            controller.HostileToUngrouped = config.HostileToUngrouped;
            controller.InfiniteAmmo = config.InfiniteAmmo;
            controller.HoldFire = profile.HoldFire;
            controller.HasPrimaryWeapon = loadout.HasPrimaryWeapon;
            controller.HasSecondaryWeapon = loadout.HasSecondaryWeapon;
            controller.SecondaryWeaponRange = profile.SecondaryWeaponRange;
            controller.PrimaryAimHitChance = ResolveHitChance(profile.Loadout?.PrimaryWeapon, config.AimHitChance);
            controller.SecondaryAimHitChance = ResolveHitChance(profile.Loadout?.SecondaryWeapon, config.AimHitChance);
            controller.BurstFire = profile.BurstFire;
            controller.PrimaryBurstMinRounds = ResolveBurstRounds(profile.Loadout?.PrimaryWeapon?.BurstMinRounds, config.BurstMinRounds);
            controller.PrimaryBurstMaxRounds = ResolveBurstRounds(profile.Loadout?.PrimaryWeapon?.BurstMaxRounds, config.BurstMaxRounds);
            controller.SecondaryBurstMinRounds = ResolveBurstRounds(profile.Loadout?.SecondaryWeapon?.BurstMinRounds, config.BurstMinRounds);
            controller.SecondaryBurstMaxRounds = ResolveBurstRounds(profile.Loadout?.SecondaryWeapon?.BurstMaxRounds, config.BurstMaxRounds);
            controller.BurstIntervalSeconds = profile.BurstIntervalSeconds;
            controller.BurstErrorRampPerRound = config.BurstErrorRampPerRound;
            controller.AimTargetRadius = config.AimTargetRadius;
            controller.AimTargetHalfHeight = config.AimTargetHalfHeight;
            controller.AimMaxErrorDegrees = config.AimMaxErrorDegrees;
            controller.CrouchedAimErrorMultiplier = config.CrouchedAimErrorMultiplier;
            controller.ProneAimErrorMultiplier = config.ProneAimErrorMultiplier;
            controller.AimWobbleIntervalSeconds = config.AimWobbleIntervalSeconds;
            controller.AimWobbleSmoothingSeconds = config.AimWobbleSmoothingSeconds;
            controller.RequireLineOfSight = config.RequireLineOfSight;

            // Now that the loadout has been applied, the gun is a real asset and its own Range can
            // be checked against what the kit told the bandit to shoot at.
            profile.WarnIfOutranged(ResolvePrimaryGunAsset(profile.Loadout));

            SpawnedBotSteamIds.Add(fakeSteamId.m_SteamID);
            LastSpawnedController = controller;

            AnnounceConnected(fakeSteamId);

            return steamPlayer.player;
        }

        /// <summary>
        /// Whether a SteamID64 could belong to a real person's account.
        ///
        /// The 64 bits pack universe (top 8), account type (next 4), instance (next 20) and account
        /// number (low 32). Real player accounts are universe 1 (Public) and type 1 (Individual),
        /// which is the whole of 76561197960265728..76561202255233023 - every account Steam can ever
        /// issue. Bot IDs are built in universe 17, a value Steam has no meaning for, so they sit far
        /// above that range and cannot alias into it however many are spawned.
        /// </summary>
        private static bool IsRealIndividualAccount(ulong steamId)
        {
            ulong universe = (steamId >> 56) & 0xFF;
            ulong accountType = (steamId >> 52) & 0xF;
            return universe == 1 && accountType == 1;
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

            LeaveTeam(steamPlayer);
            AnnounceDisconnected(steamPlayer.playerID.steamID);
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
        /// Tells the rest of the server a bot has joined, and later that it has left.
        ///
        /// The companion to <see cref="AttachRocketPlayerComponents"/>, and the same shape of bug.
        /// Rocket's own connect event is raised by a component's Start, so attaching the components
        /// covers it - but <see cref="Provider.onServerConnected"/> is raised by vanilla's real
        /// connection handshake, which a bot never goes through, so anything listening to *that*
        /// never learns the bot exists. uEssentials keeps its player registry off exactly that
        /// event, which is why every bandit death threw a NullReferenceException out of its
        /// GenericPlayerDeath handler: it looked the dying bot up, got nothing, and dereferenced it.
        /// Its join and leave messages worked the whole time because those read the name straight
        /// off the argument and never touch the registry, which is what made it look unrelated.
        ///
        /// Raising both halves is the honest signal - a player did arrive, and later did leave -
        /// and it is what makes a bot a first-class player to every plugin rather than only to
        /// Rocket. Each listener is allowed to throw without taking the spawn down with it: this is
        /// other people's code, and a bandit failing to spawn because some plugin dislikes a bot is
        /// a worse outcome than that plugin missing an event.
        /// </summary>
        private static void AnnounceConnected(Steamworks.CSteamID steamId)
        {
            try
            {
                Provider.onServerConnected?.Invoke(steamId);
            }
            catch (System.Exception e)
            {
                Rocket.Core.Logging.Logger.LogWarning("[Bandit] A plugin threw while being told a bot "
                    + $"connected: {e.Message}");
            }
        }

        private static void AnnounceDisconnected(Steamworks.CSteamID steamId)
        {
            try
            {
                Provider.onServerDisconnected?.Invoke(steamId);
            }
            catch (System.Exception e)
            {
                Rocket.Core.Logging.Logger.LogWarning("[Bandit] A plugin threw while being told a bot "
                    + $"disconnected: {e.Message}");
            }
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
        /// <summary>
        /// The primary weapon's asset, purely so the kit's FireRange can be sanity-checked against
        /// the gun's own Range. Resolution failures are the loadout applier's business and have
        /// already been logged by the time this runs, so anything unresolvable is simply null here.
        /// </summary>
        private static ItemGunAsset ResolvePrimaryGunAsset(BanditLoadout loadout)
        {
            string identifier = loadout?.PrimaryWeapon?.Item?.Trim();
            if (string.IsNullOrEmpty(identifier))
            {
                return null;
            }

            if (ushort.TryParse(identifier, out ushort legacyId))
            {
                return legacyId != 0 ? Assets.find(EAssetType.ITEM, legacyId) as ItemGunAsset : null;
            }

            return System.Guid.TryParse(identifier, out System.Guid guid) && guid != System.Guid.Empty
                ? Assets.find(guid) as ItemGunAsset
                : null;
        }

        private static float ResolveHitChance(BanditWeapon weapon, float fallback)
        {
            return weapon != null && weapon.AimHitChance >= 0f ? weapon.AimHitChance : fallback;
        }

        /// <summary>
        /// A weapon's own burst size if it sets one, otherwise the global figure. Takes the value
        /// rather than the weapon so one method covers both ends of the range; negative means the
        /// entry did not set it, and a missing weapon entry arrives here as null.
        /// </summary>
        private static int ResolveBurstRounds(int? weaponValue, int fallback)
        {
            return weaponValue.HasValue && weaponValue.Value >= 0 ? weaponValue.Value : fallback;
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

                LeaveTeam(client);
                AnnounceDisconnected(client.playerID.steamID);
                Provider.kick(client.playerID.steamID, "bandit despawned");
                removed++;
            }

            SpawnedBotSteamIds.Clear();
            return removed;
        }

        /// <summary>
        /// Takes a bot off its team on the way out.
        ///
        /// Not cosmetic: vanilla counts a group's members up in ServerAssignToGroup and only ever
        /// counts them back down in leaveGroup, and a kicked player does neither. Without this,
        /// every spawn-and-clear cycle leaves a team one member heavier than it really is, that
        /// count is written into Groups.dat, and a server with Max_Group_Members set eventually
        /// refuses to let anyone join a team that has nobody on it.
        /// </summary>
        private static void LeaveTeam(SteamPlayer steamPlayer)
        {
            try
            {
                BanditTeams.Leave(steamPlayer.player);
            }
            catch (System.Exception e)
            {
                Logger.LogWarning($"[Bandit] Could not take a despawning bandit off its team: {e.Message}");
            }
        }
    }
}
