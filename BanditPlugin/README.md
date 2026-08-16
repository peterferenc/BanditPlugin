# BanditPlugin

A RocketMod plugin for Unturned that spawns AI "bandit" bots: they appear as real player
characters, hold an Eaglefire, continuously turn to face the nearest player, and shoot at them.

The project started life as a zombie-based experiment, but bots are **fake players**, not
zombies. See "Why not zombies?" below.

## Commands

| Command | Permission | Description |
|---|---|---|
| `/bandit` | `bandit.spawn` | Spawns a bandit where you're looking, facing you. |
| `/banditgoto` (alias `/bgoto`) | `bandit.spawn` | Sends the last spawned bandit to the point you're looking at (up to 512m). |
| `/banditpatrol [on\|off]` | `bandit.spawn` | Starts/stops **all** bandits patrolling this map's waypoints. No argument toggles. |
| `/banditwp add\|remove\|clear\|list` | `bandit.spawn` | Records this map's patrol route at your feet. |
| `/banditcover` | `bandit.spawn` | Makes the last spawned bandit take cover from you now, and reports what it found. |
| `/banditstop` | `bandit.spawn` | All bandits hold fire. They still move and track you. |
| `/banditshoot` | `bandit.spawn` | Weapons free again. |
| `/banditv drive\|gunner\|exit` (alias `/bv`) | `bandit.spawn` | Puts the last spawned bandit in the nearest vehicle's driver seat (holds station) or F2 seat (tracks the nearest player), or gets it out. |
| `/banditvgoto [stop]` (alias `/bvgoto`) | `bandit.spawn` | Drives that bandit's vehicle to the point you are looking at. |
| `/banditstatus` | `bandit.spawn` | What each bandit is doing - state, target, destination, A* or steering. |
| `/banditclear` (alias `/clearbandits`) | `bandit.spawn` | Removes all spawned bandits. |

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

and, for movement:

- **`BanditBrain`** - the state machine (Idle / Travel / Investigate / Engage / TakeCover). It
  publishes a desired *world* direction plus stance flags and never touches packets itself.

  **Combat does not steer the feet.** Taking cover is the single tactical override; everything
  else a bandit does while fighting - aiming, firing, tracking - happens in the controller and
  leaves movement alone. So a patrol or a `/banditgoto` keeps running through a firefight instead
  of being suspended by it. Closing on a target and investigating a last known position are both
  opt-in config, off by default, because each one makes the bot abandon what it was told to do.
- **`BanditNavigator`** - turns "go here" into a direction, via A* where there is a navmesh and
  whisker steering where there isn't.
- **`BanditCoverFinder`** - generates and scores positions that break line of sight from a threat.
- **`BanditWaypointStore`** - per-map patrol routes in `Waypoints/<map>.txt`.

## Movement, cover and patrol

### Movement is two more bytes on the packet

No positions are ever written. The server decodes movement out of the same packet the bot was
already sending and simulates it with the real `CharacterController`:

```csharp
int input_x = ((analog >> 4) & 0xF) - 1;   // -1 / 0 / 1
int input_y = (analog & 0xF) - 1;
player.movement.simulate(sim, recov, input_x, input_y, 0f, 0f, keys[0] /*jump*/, keys[5] /*sprint*/, RATE);
```

so gravity, slopes (`Max_Walkable_Slope`), step-up, stairs, per-surface friction, stamina, water
and region updates all come free, and position replicates to other clients exactly as a real
player's does. `keys` is a bitfield of `1 << index`: bit0 jump, bit3 crouch, bit5 sprint.

**Movement is body-relative, and the body yaw *is* the aim yaw.** `PlayerLook.simulate` assigns
`transform.localRotation` from the packet's yaw (both components live on the player's root
GameObject), and `PlayerMovement` then does `transform.rotation * move.normalized * speed`. So a
desired world direction has to be un-rotated by the yaw *that same packet carries* and quantised
onto the eight compass points. The upshot is that an engaged bandit strafes and backpedals with
its gun still on the target, and only turns its body when it has nobody to shoot at.

### Pathfinding: the server is already running A*

Unturned ships `AstarPathfindingProject.dll`, and `UnturnedPathfinding_ASPFP` creates a live
`AstarPath` singleton with one `RecastGraph` per Nav volume - that's what zombies path on
(`Seeker` + `FunnelModifier` + `LegacyAIPathNoRedist`). The navigator borrows the Seeker and the
funnel modifier but **not** the AIPath: AIPath drives `transform` directly, which would fight
`PlayerMovement` and desync the position other clients see. Only the corner list is used.

The navmesh only exists *inside* Nav volumes, i.e. the towns where zombies spawn, so a bandit on
the roads between them is off-mesh most of the time. Each repath checks whether both endpoints
snap onto a graph (within `NavmeshSnapDistance`, because AstarPath's own
`maxNearestNodeDistance` is 100m and would happily snap onto the next town's graph); if not, the
bot steers directly, with capsule-cast whiskers fanning out to either side of a blocked path,
a jump for obstacles that are only blocking low down, and vanilla-zombie-style stuck detection
that sidesteps and eventually gives up so the brain can pick another goal.

### Cover

Candidates come from the landmarks actually near the bot - trees, rocks, vehicles, walls,
barricades - plus a blind ring of samples for terrain folds and building corners no collider
points at.

For each landmark the spot is found by **raycasting from the threat's eye at the landmark** and
standing just behind wherever that ray first hits. The obvious alternative - `bounds.center` plus
the bounding box's half-extent - is wrong in exactly the case cover most obviously has to handle:
a tree's bounds are its *canopy*, three to five metres across, so the spot lands that far behind a
trunk 0.3m wide, where the trunk no longer occludes anything. Every tree then fails the
"still visible when crouched" test and is discarded, and the bot stands in the open having found
"no cover" with a tree right in front of it. Each survivor is scored on the same `BLOCK_SENTRY`
visibility test the bot's own line of sight uses, so "in cover" means exactly "can't be shot":

| Result | Meaning |
|---|---|
| visible when crouched | not cover, rejected |
| hidden crouched, visible standing | **crouch cover** - the prize: duck to be safe, stand to shoot |
| hidden either way | hard cover - safe but silent, unless a lateral step gives a firing angle |

The bot's current position is scored with the same function and used as the bar to beat, so one
already tucked behind a rock doesn't keep sprinting between equally good rocks. A hurt bandit
flips the weighting and hides properly instead of looking for a firing position.

Once there it alternates hiding and exposing itself (`CoverHideSeconds` / `CoverPeekSeconds`) -
crouching and standing in crouch cover, stepping out to the verified flank in hard cover. Note the
bot **keeps fighting while it has no visible target**: crouching is what breaks line of sight, and
the controller only acquires players it can see, so cover runs on memory for a few seconds rather
than the bot standing up and wandering off mid-firefight.

Getting shot by someone it never saw is handled through `DamageTool.damagePlayerRequested`, whose
`direction` points along the bullet - so the bot knows roughly where to hide from, and where to go
looking afterwards.

### Patrol

`/banditwp add` records waypoints at your feet into `Waypoints/<map>.txt` (plain `x y z` lines,
hand-editable). With no recorded route, patrol falls back to the map's `LocationDevkitNode`s - the
named places official maps mark in the editor. Bandits start at whichever waypoint is nearest,
loiter on arrival, and break off to fight anything they see on the way, resuming afterwards.

## Vehicles

`/banditv drive` seats the last spawned bandit in the driver seat of the nearest vehicle within
50m and holds it there. `/banditv exit` gets it out.

**The driver is the physics.** A server never simulates a driven vehicle: `PlayerInput` branches on
the packet type, and a `DrivingPlayerInputPacket` carries a position and a rotation where the
walking one carries an analog byte. `InteractableVehicle.simulate()` ends in
`rootRigidbody.MovePosition(point)` / `MoveRotation(angle)`, and `updatePhysics()` makes the
rigidbody kinematic the moment seat 0 is occupied. So the bot doesn't press W - whatever its packets
say the vehicle's pose is, is where the vehicle is.

With no destination that is all it does: holding station means echoing the vehicle's own transform
straight back at it every packet with the four motion values zeroed. The delta is nothing, so it
sits. Nothing shoots from a vehicle yet.

### Which packet depends on the seat

A driver's stance is `DRIVING` and only a `DrivingPlayerInputPacket` reaches
`InteractableVehicle.simulate`. Every other seat's stance is `SITTING`, and it is the **walking**
packet's `SITTING` branch that calls `PlayerMovement.ServerUpdateTurretAim()` - the one thing that
replicates where a gunner is pointing. Send a driving packet from a turret seat and the turret never
moves for anyone watching. So `/banditv gunner` sends walking packets carrying nothing but a look
angle, and `/banditv drive` sends driving packets carrying a pose.

Those look angles are **seat-local**. `PlayerLook` assigns yaw to the seat's own local rotation and
clamps it - a driver to ±160°, a turret seat to the asset's `yawMin`/`yawMax` and
`pitchMin`/`pitchMax` - so the gunner converts the direction to its target into the seat transform's
space before sending it, and slews at a fixed rate rather than snapping. There is deliberately no
line-of-sight test on that target: from inside a vehicle the first thing a ray hits is usually the
vehicle, and a gunner that dropped its target whenever the hull came between them would read as
broken tracking.

### Driving somewhere, and not fitting

`/banditvgoto` routes with the same A* the bandits walk on, then does something the navmesh cannot
help with. That mesh was baked for a walking zombie; it says nothing about whether a 3m-wide truck
fits through a gap it will happily route a person through. So before any heading is driven, a plate
the width **and height** of the vehicle is swept along it (`Physics.BoxCast`, self-hits filtered),
and a fan of ±20/40/60/80° is tried when the direct line is blocked. Terrain is deliberately left
out of that sweep - it would read every upslope as a wall - and handled by a separate climb test
that refuses anything over 35°.

Two things about that sweep were wrong at first and are worth recording, because both let the
vehicle drive through solid objects:

- **`RayMasks.CLIP` was missing.** Unturned objects carry their collision on separate clip volumes
  rather than on the visible mesh, and vanilla's own `BLOCK_COLLISION` includes that layer. A fence,
  railing or barrier can therefore be completely solid to the game and completely invisible to a
  sweep that only looks at `LARGE`/`MEDIUM`. This is why thin things were being driven through.
  `SMALL` is still left out and stays out - it is not in `BLOCK_COLLISION` either, so it stops
  nothing in vanilla, and a truck should drive through a bush.
- **The probe was a thin band at bumper height**, so anything whose collision sat above or below
  that one slice was missed. It is now the body's full height, starting 35cm above the underside so
  the road ahead isn't mistaken for a wall.

A collider the sweep starts *inside* now counts as blocking, too. Forgiving those meant a vehicle
pressed against something reported every direction as clear and kept pushing; the way out is
reverse, and the reverse sweep is the one place overlap is still forgiven.

When nothing in the fan fits, the vehicle **stops** rather than grinding into the gap, and
`/banditstatus` reports `blocked 40m out`. That is a real outcome, not a failure: the vehicle is the
size it is, and a bandit that gets as close as it can and says so beats one wedged in a doorway
still pushing.

The step itself is clamped to both validation gates every packet, and is taken from the pose last
*reported* rather than from `vehicle.transform`. `MovePosition` on a kinematic body lands at the
next physics step, so the transform can be a packet behind what the server has already accepted into
`InteractableVehicle.real` - stepping from it would hand the server a delta of up to two steps and
trip the very check the step size exists to respect. The two are resynced whenever they drift
further apart than a step can explain, which is also how a bandit recovers from a rejected packet.

### Reverse

Two different reasons to back up, and they are not the same behaviour:

- **The destination is behind and close.** Swinging a lorry round to cover ten metres takes longer
  than reversing, so past 110° of heading error and within 18m it goes tail-first. Further than that
  it turns around, because nobody reverses two hundred metres. The choice is sticky - without that
  it flips gear every time the heading error crosses the threshold and never goes anywhere.
- **It has stopped making progress.** Stuck means exactly one thing: the vehicle was supposed to be
  getting closer to where it was sent and, for 2.5s, hasn't. It is deliberately *not* "is something
  in front of me" - the navigator already sweeps for that and drives round it, and most of what it
  finds is a fact about the route rather than a failure of it. Distance to the destination is the
  only measure that catches every real case: grinding along a fence at full speed, circling a rock
  the fan keeps deflecting off, or sitting still because nothing fits all look identical from here
  and all want the same answer.

  That answer is a 1.6s reverse **plus a ban on the direction that failed** (50° arc, 7s) and a
  forced repath, so the route that comes back goes a different way instead of taking another run at
  the same gap. Bounded to three attempts per trip, because reversing counts as movement and would
  otherwise keep the trip alive forever; after that it gives up and says so. A route that suddenly
  gets 15m longer is treated as a new route rather than a failure, so repathing round a building
  doesn't read as being stuck.

The clearance sweep doesn't care which way the vehicle faces - it sweeps a *world* direction through
a box the body's width - so a heading the navigator already approved is as safe to reverse along as
to drive along. Only the wedged case picks its own direction, and that one gets swept before it's
taken.

### Not running over your own squad

The vehicle does not brake and does not steer round friendly infantry: a bandit isn't in any of the
masks the width sweep uses, and giving way to your own side would mean a vehicle that can never move
through its own squad. **The bandit moves instead.**

While driving, any squadmate inside the lane - the vehicle's own width plus a margin, from its nose
to about two seconds' driving ahead - is given `BanditBrain.OrderEvade` toward the nearer side. That
order outranks everything: cover, patrol, the fight it is in, a `/banditgoto`, even `MovementEnabled`
being off. A bandit lying prone in cover stands up and sprints out of the lane, then goes back to
what it was doing about a second later, because the order is short and re-issued every packet only
while it is still in the way. Squadmates riding in a vehicle are skipped, and so is anyone more than
3m above or below - a bandit on a roof over the lane is in no danger.

Two loose bandits both have a null squad, which puts them in the same "ours" pool, so this works when
testing with `/bandit` as well as with `/squadspawn`.

### Firing a turret

A turret seat auto-equips its gun (`equipment.turretEquipServer`), so the trigger is the same
`equipment.simulate` path the bandits already use on foot. What differs is the aim and the reports:

- The muzzle line is computed from the angles **this packet is about to carry**, converted out of
  seat space, rather than read off `player.look.aim` - which still holds the previous packet's aim,
  because the packet hasn't been simulated yet.
- **Hitscan turrets** need hit reports injected, exactly like a rifle: the server raycasts nothing
  itself, so an unreported round damages nothing. One report per round that could leave the barrel
  during the packet.
- **Projectile turrets** (rocket pods, cannon) need none - `fire()` spawns the projectile server-side
  along the seat's own aim. Sending reports for one would be ignored.
- The trigger is **latched down** through a burst (1.2s on, 1.4s off) rather than pulsed, because
  vanilla sets `equipment.isBusy` for 150ms per shot and a re-pulled trigger caps at about four
  rounds a second whatever the gun is.
- It fires only when **the round actually connects** - the shot is traced first, and the trigger
  goes down only if that trace reaches the target (or, for an explosive round, lands within 2.5m of
  them). An angular tolerance cannot do this job: vanilla clamps a seat's aim to the turret's own
  `pitchMin`/`pitchMax`, so a gun that cannot depress far enough sits at its limit with the target
  well inside any tolerance and every round sailing overhead. Since it is the same ray
  `AttachHitReports` traces, passing it also guarantees the report is a hit on the target rather
  than on the bandit's own hull - which it would otherwise be, because a tank's seat puts the
  bandit's head *inside* its own armour. The muzzle now comes from the seat's turret aim transform
  rather than that head.

Firing respects the same `/banditstop` / `/banditshoot` standing order as on foot - so with
`HoldFireByDefault` on, a fresh gunner tracks but holds its fire until told otherwise - and it will
not shoot with one of its own within 1.5m of the firing line.

Ground vehicles only for now. Boats and aircraft are refused with a reason rather than half-driven:
every step snaps the vehicle onto the ground beneath it, which under a boat is the seabed and under a
helicopter is where it is meant not to be. Holding station already works for all of them.

Fuel and battery are topped up while a bandit occupies a vehicle (`VehicleInfiniteFuel`,
`VehicleInfiniteBattery`; health deliberately is not). The fuel one is not just convenience - vanilla
tightens a car's anti-teleport delta from the asset's own figure to half a metre per packet once the
tank is empty, so a bandit that runs dry mid-drive stops being able to move at any sensible speed.

Two gates decide whether the server believes a driving packet (off LAN, with `ForceTrustClient`
unset): the horizontal step must be within `asset.sqrDelta`, and the vertical speed within
`asset.validSpeedUp`/`validSpeedDown`. Failing either starts a *recovery* - the vehicle is snapped
back and further packets ignored for a few seconds. Neither can fire while the reported position is
the one the vehicle already holds, which makes this the safe first step before anything moves.

Getting in is `VehicleManager.ServerForcePassengerIntoVehicle`, vanilla's own server-side seating
call: it broadcasts `SendEnterVehicle` so every client renders the bandit in the seat, and skips the
lock and line-of-sight checks a real player's request goes through - so a bandit will happily drive
a locked vehicle. What it will not do is let you name a seat; it takes the first free one, which is
why the driver seat is confirmed empty before the call rather than after. Trains are excluded: they
replicate a road position packed into all three channels of the position vector, and the packing is
an `internal` method.

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
9. **RocketMod's global event handlers assume every player is one it set up.** Rocket subscribes to
   statics like `PlayerLife.OnTellHealth_Global`, which fire for *every* player, then opens each
   handler with an unguarded `GetComponent<UnturnedPlayerEvents>()`. Rocket only attaches that
   component from its `Provider.onServerConnected` hook, which a bot never triggers - so every time
   a bot was shot, starved or grew thirsty the log filled with `NullReferenceException`, and the
   throw unwound back through `PlayerLife.doDamage` into `UseableGun.ballistics`, skipping whatever
   vanilla did after telling health. Fixed by attaching the same three components Rocket does
   (`UnturnedPlayerFeatures`, `UnturnedPlayerMovement`, `UnturnedPlayerEvents`). Note this also
   makes a bot spawn raise Rocket's `OnPlayerConnected` for other plugins.

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
2. `dotnet build BanditPlugin.csproj -c Release`

Pathfinding references three more assemblies out of the same Managed folder:
`AstarPathfindingProject.dll`, plus `PackageTools.dll` and `Drawing.dll` for the base classes
`Seeker` inherits from (`VersionedMonoBehaviour` and `MonoBehaviourGizmos`). All three ship with
the server and are already loaded by the game, so nothing extra is deployed.

## Install

1. Copy `bin/Release/BanditPlugin.dll` into the server's `Rocket/Plugins/` folder.
2. Start once to generate `BanditPlugin.configuration.xml`.
3. Grant `bandit.spawn` to a group in `Rocket/Permissions.config.xml`.

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
| `AimHitChance` | `0.3` | Roughly the fraction of shots that land. `1` restores perfect aim. |
| `AimTargetRadius` / `AimTargetHalfHeight` | `0.35` / `0.8` | Ellipse standing in for the player's hitboxes, used to solve for the aim error. |
| `AimMaxErrorDegrees` | `8` | Caps the error, so the bot doesn't flail at point-blank range. |
| `AimWobbleIntervalSeconds` | `0.35` | How often the aim re-drifts between shots. |
| `AimWobbleSmoothingSeconds` | `0.15` | Larger is a lazier sway; `0` snaps. |
| `RequireLineOfSight` | `true` | Bot won't target or shoot through walls, rocks or vehicles. |
| `MovementEnabled` | `true` | Master switch. Off makes bandits the stationary turrets they used to be. |
| `ArriveRadius` | `2` | How close counts as arrived. |
| `RepathIntervalSeconds` | `2.5` | How often a moving bot asks A* for a fresh path. |
| `NavmeshSnapDistance` | `3` | Beyond this from the navmesh the bot steers directly instead of pathing. |
| `AllowSprint` / `AllowJumping` | `true` | Vanilla still gates sprint on stamina and aiming. |
| `AdvanceOnTarget` | `false` | Walk toward a target further away than `PreferredEngagementRange`. A chase behaviour - off by default, and only applies when the bot has no order or patrol. |
| `InvestigateEnabled` | `false` | After losing contact, go and look at the last known position. Also off by default: it makes the bot leave its post. |
| `DespawnSecondsAfterDeath` | `5` | How long a killed bandit lies there before removal. Negative leaves the body for `/banditclear`. |
| `PreferredEngagementRange` | `25` | Range the bot fights at, and scores cover against. |
| `CoverEnabled` | `true` | Seek cover when seen or shot at. |
| `CoverSearchRadius` | `18` | Radius searched for cover. |
| `CoverSearchIntervalSeconds` | `3` | Minimum gap between searches - each is a burst of raycasts. |
| `CoverRingSamples` | `12` | Blind samples per ring, on top of nearby colliders. |
| `CoverMinimumThreatDistance` | `8` | Cover nearer the threat than this is ignored. |
| `CoverHideSeconds` / `CoverPeekSeconds` | `2.5` / `2` | The duck-and-pop cycle. |
| `PatrolByDefault` | `false` | Start newly spawned bandits patrolling. |
| `PatrolWaypointDwellSeconds` | `3` | Loiter time at each waypoint. |
| `PatrolLoop` | `true` | Return to the first waypoint after the last. |
| `PatrolUseLocationNodesWhenNoWaypoints` | `true` | Fall back to the map's location nodes. |

### Accuracy

The bot's aim error is drawn in **metres at the target's range** and then converted to an angle, so
the hit rate holds steady with distance instead of the bot being lethal up close and hopeless far
out. Each axis' standard deviation is scaled by that axis' half-extent, which puts the miss (in
target-widths) on a circular unit Gaussian, so `P(hit) = 1 - exp(-1 / 2s²)` and `AimHitChance` is
met at `s = 1 / sqrt(-2 ln(1 - p))`. A fresh sample is drawn the instant the trigger is pulled, so
shots are independent; the drift between shots is just cosmetic sway.

Measured against the ellipse, that lands on 30% from about 10m out. Closer in, the
`AimMaxErrorDegrees` clamp takes over and the bot gets deadlier - ~43% at 5m, ~54% at 3m.

## Known limitations

- **~50m effective range.** With Ballistics enabled the server only accepts a hit report within
  roughly `ballisticTravel * (steps + 1 + SAMPLES) + 4` (≈54m for the Eaglefire) of the bullet at
  report time. Longer range would mean delaying the hit report as the bullet travels.
- **Line of sight is eye-to-eye.** A target whose head is behind cover reads as hidden even if a
  leg is exposed, and vice versa - the bot won't shoot at a sliver of a player it can technically
  see. Mirrors what vanilla sentry guns do.
- **Movement is 8-way.** `input_x`/`input_y` are only ever -1, 0 or 1, so a walking direction is
  quantised into 45° sectors. It's invisible while travelling (the body turns onto the line of
  travel) but a strafing bandit in combat moves in 45° steps.
- **Pathfinding only exists inside Nav volumes.** Outside them the bot steers directly, so it can
  be led into a dead end that whiskers and stuck-sidestepping can't reason its way out of; it
  gives up on the goal rather than grinding.
- **Bots consume player slots** and appear in the player list and server browser count.
- Reflection into private members means a game update can break this; failures name the specific
  member in the server log.

## Credits

Approach informed by [EvolutionPlugins/Dummy](https://github.com/EvolutionPlugins/Dummy) (MIT,
© 2022 DiFFoZ) - specifically driving bots via `serversidePackets` input packets and the
`timeLastPacketWasReceivedFromClient` keep-alive. No code was copied; that project has no weapon
support (`// todo: simulate useable`), so the equipping and firing here is original.
