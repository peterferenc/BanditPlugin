# Vehicles

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

## Which packet depends on the seat

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

## Driving somewhere, and not fitting

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

## Reverse

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

## Not running over your own squad

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

## The footprint has to be measured in local space

`Collider.bounds` is a **world-space AABB**, and folding its corners back into vehicle space
inflates the result by however much the vehicle happens to be rotated. A tank parked at 45° measured
about 40% too wide and, worse, reported an underside well below its real one - which the drive step
reads as ride height, so it drove half a metre in the air and glided over everything, while the
over-wide sweep made the navigation useless. It only showed on some vehicles because one parked
square to the world axes measures correctly.

Each collider's own local geometry is used instead (`BoxCollider.center/size`, `MeshCollider
.sharedMesh.bounds`, `WheelCollider.center/radius`, …) carried through its own transform into
vehicle space. On top of that, ride height is **calibrated from the vehicle itself** at the moment it
is given somewhere to go - the gap between its origin and the ground it is resting on - clamped
against the footprint figure so an already-floating vehicle cannot bake its float in.
