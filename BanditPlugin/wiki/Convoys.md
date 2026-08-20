# Convoys

`/banditevent convoy 300` buys three hundred points of crewed vehicles, spawns them nose-to-tail at
the first waypoint of `/banditevent wp`, and drives them through the rest. No foot squads are drawn:
a squad spawned beside the road would be left behind by the first leg. At the last waypoint they
stop and stay.

The difference from the vehicles an ordinary event spawns is what they are *for*. Those are an
ambush - they sit until the event sees somebody, drive at them and empty out on top of them, so the
destination **is** the enemy. A convoy has somewhere to be, and the enemy is an interruption.

## Roads are level data, and the server has all of it

`Level.init` calls `LevelRoads.load()` unconditionally, so a headless server holds every road on the
map: `Environment/Paths.dat` read into a list of `Road`, each a cubic Bezier spline over its joints.
None of the evaluation needs a mesh or a renderer -

```csharp
road.getPosition(index, t)    // a point on the spline
road.getVelocity(index, t)    // the tangent, i.e. which way the road runs
road.getLengthEstimate(i)     // how long that segment is
```

What the game has no notion of is a road *network*. Roads are independent splines with no
connectivity, no junctions and no lane data - where two cross is implicit geometry and nothing more.
`BanditRoadGraph` recovers the rest: every road sampled every 8m into linked nodes, then samples
from *different* roads within the sum of their half-widths linked into junctions, then the gaps
between what that leaves bridged, then A* over the result. Sampling walks segment by segment rather
than over the whole spline, because `t` is not arc
length - `getPosition(t)` divides it evenly between joints, so a long straight and a tight bend get
the same share of it, which is also why vanilla's own `updateSamples` walks segments.

Three things that are easy to get wrong here, and all of them silent:

- **The two width fields mean different things.** A modern `RoadAsset.Width` is the *full* flat
  width - `Road.buildMesh` lays its vertices at `Width * 0.5f` either side of the spline - while the
  legacy `RoadMaterial.width` is already a half-width, which is why the game added a `HalfWidth`
  property to say so. Read the legacy one as a full width and the right-hand lane is in the ditch.
  Outside the flat top the mesh tapers into the terrain over another `Depth` metres; that skirt is
  shoulder, not road, and is not counted.
- **`Cross(forward, up)` points left.** It is the order vanilla's own mesh builder uses, so it is the
  one you reach for - and it would put the column into oncoming traffic. Right is `Cross(up, forward)`.
- **Train tracks are roads too.** They are ordinary `Road`s with an entry in the level's
  `configData.Trains`, and their samples look exactly like a highway's. They are excluded, because
  routing a lorry down a railway looks precisely as wrong as it sounds.

### The gaps are the whole game

Junction detection alone does not produce a connected map, and the shortfall is not marginal. PEI's
twenty-three roads come to 780 nodes and exactly **twenty** junction links, which leaves **fourteen
disconnected islands of road**. A\* between two of them cannot degrade gracefully - there is no
route - so `TryRoute` failed and the leg was driven as a straight line. That is the whole of the
"convoy ignores the roads" bug: the routing never ran.

The gaps are real ground rather than missing data. A map maker draws each road as its own spline and
nothing in the editor makes one end *at* another, so the tarmac genuinely stops and the next stretch
starts a hundred metres later with drivable land in between. PEI's two halves are 167m apart at the
closest point their splines approach each other, and every convoy crossing the island needs that
crossing.

So `BridgeGaps` links them, Kruskal over the loose ends - a node with one neighbour is where a
spline stops - shortest crossing first, one per pair of islands. One crossing, because a missing
stretch of road is one road; anything more just adds the router's own shortcuts to a network that is
supposed to describe roads.

What keeps that honest is not the distance limit, it is the ground test every crossing has to pass.
The other thing on the far side of a gap in a coastal map's road network is **a bay**, and the
spline data cannot tell the two apart - both are two road ends with nothing between them. So the
line is sampled every 8m and rejected if it crosses more than a metre of water (a ford is somewhere
vehicles do drive) or ground steeper than 30 degrees. The surface it tests is the terrain, or road
decking above it: a bridge here is a road whose joints are flagged `ignoreTerrain`, so its deck is a
road mesh on the ENVIRONMENT layer. Objects are deliberately not probed - a gap that happens to pass
over a warehouse roof is not a road.

On PEI that bridges eleven gaps and refuses five, four of them across water and one up a cliff, and
takes the network from fourteen pieces to three. The route the convoy could not plan at all comes
out as 1501m along the network - of which 170m is open ground - against the 846m straight line it
used to drive instead. `/banditroads` reports both halves: how many gaps the map needed to be
routable, and how much of a given route crosses them.

Road classification comes free: the same width-and-surface test the game uses to draw the map chart
sorts roads into highway, road, street and path, and each costs a little more per metre than the
last. So a convoy prefers the motorway to the farm track running beside it without anything being
hand-tagged, and still takes the track when it saves a kilometre. `/banditroads route` prints the
mix.

Roads are on layer 19 (`ENVIRONMENT`), which in the whole of `Assembly-CSharp` **nothing else uses** -
only `Road` assigns it. That makes a downward ray with `RayMasks.ENVIRONMENT` a clean "am I on a
road?" test, and it is already why a convoy can cross a bridge: `VehicleTerrain.GroundMask` includes
that layer, so road decking is a surface a vehicle rests on rather than a hole it drives into.

## The column drives itself with the driver it already had

Nothing about movement is reimplemented. Each vehicle is driven by the same `BanditVehicleDriver`
that `/banditvgoto` uses and is simply handed the next route point as a destination - so the width
sweep, the reverse-out-of-trouble logic, the ride-height calibration and both server validation
clamps keep working, and a convoy is exactly as good at getting round a fallen tree as a single
vehicle is.

Two things sit on top of that:

- **Points are handed over early.** The advance radius is wider than the navigator's own arrival
  radius, so a vehicle gets the next point while it is still rolling at the current one. Waiting to
  put its bumper on each point in turn would make it brake into every one of them, eight metres
  apart, all the way across the map.
- **A destination is only re-issued when it changes.** `SetDestination` throws the current path away
  and resets the stall tracking, so re-issuing every tick would mean the navigator could never
  detect being stuck. Since the target only advances when the vehicle has got closer, a genuinely
  stuck vehicle stops advancing and its stall detection fires normally.

### Water is not ground

Nothing in the drive step notices water. It snaps the vehicle onto whatever is beneath it, and
beneath water that is the seabed - so a convoy that reached the coast drove into the sea and kept
going along the bottom of it. The ground sample is not wrong, it is answering a different question.

So the climb test answers this one too: the surface a step ahead is refused if it is under more than
a metre of water, using the map's own water volumes rather than a sea level of ours, which is what
makes a lake above sea level work as well as the sea does. A bridge deck is above the water and on
the ground mask, so crossing one is unaffected.

Behind that sits a backstop, because a vehicle can be in the water without having driven there -
pushed, spawned badly, or already in it when the refusal came along. `IsSubmerged` is vanilla's own
`isUnderwater`, the same test it uses to cut the engine, and a driver that finds itself submerged
stops rather than motoring along the bottom. The convoy drops that vehicle from the column: nobody
is put out of it, since a rider ordered into deep water drowns as surely as the vehicle did, and a
column that waits for it never moves again.

Each vehicle picks its own lane through a road point: over to the right if half the carriageway can
hold its width plus a margin, down the middle if it cannot. That is measured against the footprint
the driver already measures from the vehicle's colliders, so a bike and a tank on the same road make
different choices.

Interval keeping is **speed, not steering**. Everything is driving the same route, so a follower
that is too close does not need to go round the vehicle in front - it needs to stop pushing into it,
and a lorry nosing out to overtake on a bend is exactly the behaviour to avoid. A `SpeedScale` on
the driver scales cruise between a crawl and normal; zero means "stay put with the engine running",
which is not `StopDriving` - the destination survives, so it moves off again when the scale comes
back up.

## Contact, and getting going again

    Cruise     following the route at speed, holding interval
    Contact    riders out and fighting, vehicles crawling and shooting, route still running
    Rallying   threat gone, vehicles stopped, riders walking back to their seats
    Arrived    at the last waypoint - they stop, and they stay

Contact comes from the squads first. Every crew is a squad of the event's, and so is every group of
riders once they are out, so "anyone here can see somebody" is already answered by whoever spotted
them. Underneath it is a proximity trigger measured from the vehicles, for the buttoned-up case: a
bandit inside a hull frequently has no line of sight out of it, and without that backstop a column
could be walked up to and never notice.

On contact the riders get out and fight as any bandit does - cover, prone, the lot. Drivers and
gunners stay: a turret is worth more than another rifle on the ground, and the column keeps rolling
at about a third of its speed while it fights, because a stopped convoy is a stationary target.

When nothing has been seen for twelve seconds the vehicles stop and the men walk back. A rider is
ordered to the vehicle, then asks for **the seat it came out of** once it is within a few metres -
recorded on the way out, since afterwards there is nothing to ask. Two details make that work:
`RequestSeat` retries on its own, because vanilla refuses to seat anyone whose equip animation is
still running; and the walk order is only re-issued when the vehicle has actually moved, because
`BanditNavigator.SetDestination` clears the path, and an order repeated every tick means a man being
told to move so often he never takes a step.

A straggler is left behind after a minute. It is armed and in a squad and will fight where it
stands, which beats a convoy that never moves again because one rifleman is wedged behind a rock.

## When it does not fit

The navigator giving up means the way ahead is too tight, which on a road usually means one
obstruction rather than a route that was never drivable. So the vehicle skips five route points and
picks the road up beyond, three times; after that it unloads where it stands and stops being a
vehicle. Bounded, because a convoy that skips its way to the destination through a cliff has not
arrived anywhere.

A vehicle whose driver is killed unloads too, rather than leaving a truckload of riflemen sitting in
something that will never move again. One that is destroyed needs no help - vanilla throws the
occupants clear, and they are bandits on foot in a squad like any other.
