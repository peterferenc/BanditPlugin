# Turrets

## Firing a turret

A turret seat auto-equips its gun (`equipment.turretEquipServer`), so the trigger is the same
`equipment.simulate` path the bandits already use on foot. What differs is the aim and the reports.

**The aim angles are local to the turret, not to the seat.** `PlayerLook.simulate` rotates the gun
with `turretYaw.localRotation = rotationYaw * Euler(0, yaw, 0)`, so the barrel's zero is the yaw
pivot's parent times the base rotation it was built with - while the player's own aim transform
hangs off the *seat*. Those agree only while the seat faces the same way as the turret mount, which
is true of a hull gun and false of plenty of second and third turrets. Solving in seat space there
pointed the barrel somewhere else entirely while the rounds still went to the target: gun facing
away, bullets landing. Angles are now solved in the turret's own frame and the shot is traced along
that same frame, so what is aimed and what is fired agree.

The exception is a projectile turret with `useAimCamera` off: vanilla spawns rockets along
`player.look.aim.forward`, which stays in seat space whatever the barrel does, so for those the seat
frame is what the round will actually follow and is what gets aimed.

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

Firing respects the same `/bandit shoot start|stop` standing order as on foot - so with
`HoldFireByDefault` on, a fresh gunner tracks but holds its fire until told otherwise - and it will
not shoot with one of its own within 1.5m of the firing line.

`gunner` is F2, `gunner2` is F3, `gunner3` is F4, and so on: the number is the seat *key*, not a
count of turrets, so a command can be checked by pressing the key yourself. That is how you find out
which seat a modded vehicle's second turret is really on.

## Shooting the cover away

A target behind a tree, a fence or a parked car used to mean no shot at all, because the fire gate
asks whether the traced round reaches them. Now, if what it meets instead is **breakable** and is
within 12m of the target - the thing they are actually hiding behind, rather than every tree on the
way - the bandit shoots that instead.

Breakable means trees (`Resource`), player builds (`Barricade`, `Structure`), other vehicles, and
world objects with an `InteractableObjectRubble`. Terrain, rock and buildings deliberately do not
qualify: they take no damage, so a bandit shooting at them would hold a permanent clear-to-fire on
something it can never remove. A vehicle with our own side aboard doesn't qualify either.

Vehicle turrets do this **unconditionally** - a tank has no business waiting politely for someone to
step out from behind a sapling. On foot it is per-kit via `DestroysCover`, off by default: a
rifleman putting rounds into a trunk achieves nothing but noise, so this is for grenadiers and
anything firing explosives. `/banditstatus` reports `clearing cover`.

## Reloads are served, not skipped

`InfiniteAmmo` used to refill the magazine the instant it ran dry, which gave every bandit an
endless belt with no pause in it. Wrong everywhere, absurd on a vehicle: a tank cannon with a
one-round magazine and a six-second `Reload_Time` fired at the burst cadence, several times faster
than the gun can physically manage.

The magazine is still refilled - a bot has no client to play a reload animation - but only after the
gun's own `Reload_Time` has elapsed, which is the same figure vanilla measures its own reload
against (the animation length only wins where an animation exists, which on a dedicated server it
never does; a gun that declares none gets 2.5s). The bandit holds its fire for the duration instead
of dry-firing through it, on foot and in a turret alike, and `/banditstatus` reports
`reloading (4.2s)`.

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
