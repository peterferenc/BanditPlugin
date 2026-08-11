# BanditZombiePlugin

A RocketMod plugin for Unturned that spawns AI "bandit" bots: they appear as real player
characters, hold an Eaglefire, continuously turn to face the nearest player, and shoot at them.

Despite the project name (which started life as a zombie-based experiment), bots are **fake
players**, not zombies. See "Why not zombies?" below.

## Commands

| Command | Permission | Description |
|---|---|---|
| `/bandit` | `banditzombie.spawn` | Spawns a bandit where you're looking, facing you. |
| `/banditclear` (alias `/clearbandits`) | `banditzombie.spawn` | Removes all spawned bandits. |

Use `/banditclear` before disconnecting - bots occupy player slots, and their presence affects
the client-side exit timer.

## How it works

Four pieces:

- **`FakeTransportConnection`** - a no-op `ITransportConnection`, so `Provider.addPlayer` has a
  non-null connection to register for a client that doesn't exist.
- **`FakePlayerSpawner`** - reflects into `Provider.ClaimNetIdBlockForNewPlayer` / `addPlayer` /
  `Player.InitializePlayer` / `SendInitialPlayerState`, mirroring the sequence `Provider` uses for
  a real join. Then gives the bot an Eaglefire and attaches the controller.
- **`PlayerJoinBroadcaster`** - sends the `PlayerConnected` message and initial player state to
  every already-connected client. **`addPlayer` alone does no networking at all** (it only fires a
  local C# event), so without this the bot exists server-side but nobody's client renders it.
- **`BanditBotController`** - each tick, builds a `WalkingPlayerInputPacket` and enqueues it on the
  bot's own `PlayerInput.serversidePackets`. Vanilla `PlayerInput` then drives everything -
  `look.simulate` (rotation), `movement.simulate`, `equipment.simulate` (trigger), the animator,
  and the `clock`/`tock` cadence that paces firing.

## Non-obvious things this had to work around

Each of these was found by decompiling the server's real `Assembly-CSharp.dll`, and each one
produced a silent failure rather than an error:

1. **The server does not raycast bullets.** `UseableGun.ballistics()` applies damage from hit
   reports the *owning client* sends (`PlayerInput.sendRaycast(info, ERaycastInfoUsage.Gun)`);
   with no client, `if (!player.input.hasInputs()) break` discards every bullet. Symptom: the bot
   fires convincingly and damages nothing at all - not players, not trees. The controller now
   raycasts itself and injects an `InputInfo` into the packet's `serversideInputs`.
2. **Firing before the gun is equipped throws punches.** `PlayerEquipment.simulate` routes primary
   attacks to `simulate_PunchInput` while there's no valid useable, and ignores input until
   `IsEquipAnimationFinished`. Attack input is gated on both.
3. **`ServerEquip()` silently no-ops** if the player is momentarily `life.isDead` / `!canEquip`
   right after spawn - so some bots stood around holding nothing. It's now retried until it takes.
4. **`startPrimary()` refuses to fire while firemode is SAFETY.** Firemode lives in
   `equipment.state[11]` and ammo in `state[10]`, both read by `UseableGun.equip()`, so they must
   be set on the item *before* it is equipped.
5. **`analog = 0` means "walk backwards"**, not "stand still". Input is decoded as
   `((analog >> 4) & 0xF) - 1`, so neutral is `0x11`.
6. **`SteamPlayerID` overloads `operator==` without a null guard** - it dereferences both sides. A
   plain `steamPlayer.playerID == null` check throws `NullReferenceException`.
7. **A nested type that is `public` inside an `internal` type reports `IsPublic == false`.**
   `NetMessages.ClientWriteHandler` must be looked up with `BindingFlags.Public | NonPublic`;
   `NonPublic` alone silently returns null.
8. **Bots are kicked after ~30s** by `Provider.KickClientsWithBadConnection` unless
   `SteamPlayer.timeLastPacketWasReceivedFromClient` is refreshed, since they never receive packets.

## Why not zombies?

The original plan was a custom zombie. Two hard blockers killed it:

- Zombies are synced to a client **once**, when that client's map region first loads
  (`ZombieManager.onBoundUpdated` + a one-shot `regions[bound].isNetworked` flag). There is no
  vanilla RPC that introduces a *new* zombie ID to an already-loaded client - even
  `ReceiveZombieAlive` requires `id < regions[reference].zombies.Count` on the client's existing
  list. A zombie added mid-session is permanently invisible to anyone already there.
- `Zombie.rightHook` (the bone mega zombies parent their boulder to) is declared but **never
  assigned anywhere**, and has no `[SerializeField]`. There's no working attachment point for a
  persistent held item.

Players have neither problem: joins are broadcast unconditionally and are the most
heavily-exercised path in the game.

## Build

1. Copy `Directory.Build.props.example` to `Directory.Build.props` and set:
   - `UnturnedManagedPath` - the server's `Unturned_Headless_Data/Managed` folder.
   - `RocketModPath` - the folder with `Rocket.API.dll`, `Rocket.Core.dll`, `Rocket.Unturned.dll`.
2. `dotnet build BanditZombiePlugin.csproj -c Release`

## Install

1. Copy `bin/Release/BanditZombiePlugin.dll` into the server's `Rocket/Plugins/` folder.
2. Start once to generate `BanditZombiePlugin.configuration.xml`.
3. Grant `banditzombie.spawn` to a group in `Rocket/Permissions.config.xml`.

Developed against Unturned 3.26.3.8 with RocketModFix 4.23.1.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `GunAssetGuid` | Eaglefire's GUID | Resolved by GUID because `Assets.find(EAssetType.ITEM, name)` is case-sensitive and the asset is `Eaglefire`, not `EagleFire`. |
| `GunAssetLegacyId` | `4` | Fallback if the GUID doesn't resolve. |
| `MagazineCapacity` | `30` | Also the refill amount for `InfiniteAmmo`. |
| `FireIntervalSeconds` | `0.6` | One trigger pull per interval. |
| `AimToleranceDegrees` | `10` | Won't fire until aimed this close. |
| `FireRange` | `50` | See the range limitation below. |

## Known limitations

- **~50m effective range.** With Ballistics enabled the server only accepts a hit report within
  roughly `ballisticTravel * (steps + 1 + SAMPLES) + 4` (≈54m for the Eaglefire) of the bullet at
  report time. Longer range would mean delaying the hit report as the bullet travels.
- **Bots are stationary** - they turn and shoot but never move. Movement would require
  reproducing client-side movement prediction so `clientPosition` matches the server.
- **Bots consume player slots** and appear in the player list and server browser count.
- Reflection into private members means a game update can break this; failures name the specific
  member in the server log.

## Credits

Approach informed by [EvolutionPlugins/Dummy](https://github.com/EvolutionPlugins/Dummy) (MIT,
© 2022 DiFFoZ) - specifically driving bots via `serversidePackets` input packets and the
`timeLastPacketWasReceivedFromClient` keep-alive. No code was copied; that project has no weapon
support (`// todo: simulate useable`), so the equipping and firing here is original.
