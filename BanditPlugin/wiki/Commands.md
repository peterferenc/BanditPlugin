# Commands

Every command takes the `bandit.spawn` permission except `/banditteam`, which takes
`bandit.team`. Grant both in `Rocket/Permissions.config.xml`.

## Conventions

Three things are worth knowing before the tables.

**Bandits always turn to face the nearest enemy they can see.** "Nearest" is measured inside that
kit's `TargetAcquireRange`, and since [teams](Teams) exist, an enemy is another team's bandit as
readily as it is a player. Whoever a bandit is already fighting keeps the slot unless somebody
else is meaningfully closer, so a bandit does not flick between two targets standing at the same
distance. Facing is not shooting: a bandit tracks you whether or not it has weapons free.

**A bandit spawned by `/bandit` does nothing until told.** It stands, tracks whoever it can see,
and waits - so you can switch on one behaviour at a time and watch it. A squad from
`/squadspawn`, and everything an event or convoy spawns, comes out fighting instead.

**Only the group commands take a distance or a map marker.** `/squadspawn` and `/banditevent`
place things at a distance you name, or at your map marker. `/bandit` does not: it puts one
bandit where you are looking, which is what you want when the bandit is a metre away from the
thing you are testing.

### Shared arguments

These mean the same thing wherever they appear, and `team:` and `seed:` may sit anywhere in the
line - they are lifted out before the positional words are read.

| Argument | Meaning |
|---|---|
| `team:<team>` | The side it fights on. `team=blue` works too. An unknown name is refused rather than quietly spawning onto the default team. Default teams: `bandits`, `red`, `blue`. |
| `seed:<n>` | Reproduces one random draw exactly. The reply prints the seed it used, so an interesting event can be run again. |
| `<metres>` | How far down your sightline to place things. Clamped to a minimum of 15m. |
| `marker` | Place at your map marker instead. `map` is accepted as well. |
| `start`\|`stop` | Switches an order on or off. Some commands toggle when given neither. |

### Running from the server console

Most commands work from the console. These need a player, because they read where you are
standing or looking: `/squadspawn`, `/banditevent`, `/banditgoto`, `/banditvgoto`,
`/banditcover`, `/banditwave`, `/banditwp`, `/banditroads`. `/bandit` runs from the console for
its orders and for `kits`, but spawning needs a sightline, so it has to be in-game.

## Spawning

| Command | Description |
|---|---|
| `/bandit [<kit>] [team:<team>]` | Spawns one bandit of a kit where you are looking, facing you. Up to 50m down your sightline; if that hits nothing, 3m in front of you. Without a kit it spawns the `DefaultKit`. It holds fire and takes no cover until ordered. |
| `/bandit kits` | Lists the kits and, for each, the range it fires to, notices at and fights at. |
| `/squadspawn [<type>] [<metres>\|marker] [team:<team>]`<br>(aliases `/spawnsquad`, `/squad`) | Spawns a squad in formation down your sightline, at a distance you name or at your map marker. Default distance is that type's own, deliberately past its eyes so the squad spawns unaware and you walk in on it. Comes out fighting. |
| `/squadspawn squads` | Lists the squad types, the kits each is built from, and where each places itself. `types` and `list` do the same. |

Default kits are `rifleman`, `mg`, `marksman` and `breacher`; default squad types are `basic`
(five men, mixed), `rifle` (four riflemen, 130m) and `sniper` (two marksmen and a rifleman,
260m).

```
/bandit                          one rifleman where you are looking, holding fire
/bandit mg                       one machinegunner instead
/bandit marksman team:red        one marksman on the red team
/bandit kits                     what each kit's ranges are

/squadspawn                      a 'basic' squad at its own distance, already fighting
/squadspawn rifle                four riflemen at 130m
/squadspawn sniper 300           three men at 300m down your sightline
/squadspawn rifle marker         four riflemen at your map marker
/squadspawn rifle marker team:blue
/squadspawn squads               what each type is made of
```

## Standing orders

These apply to **every live bandit**, not the last one spawned - they are orders for the field.
A bandit spawned afterwards starts on the configured defaults again (`HoldFireByDefault`,
`CoverByDefault`, `PeekByDefault`).

| Command | Description |
|---|---|
| `/bandit shoot start\|stop` | Weapons free, or hold fire. Holding fire, they still move and still track you. |
| `/bandit cover start\|stop` | Look for cover and move to it, re-finding it as the threat moves - or stop where they are and stay there. |
| `/bandit peek start\|stop` | Once in cover, alternate hiding with stepping out to shoot - or stay down. |
| `/bandit stance stand\|crouch\|prone\|free` | Hold a stance. `free` hands the choice back to each kit, so the machinegunner goes prone on contact again. `auto` is accepted for `free`. |
| `/banditpatrol [on\|off]` | Start or stop patrolling this map's waypoints. No argument toggles. Refused if the map has no waypoints and no location-node fallback. |

```
/bandit shoot start              weapons free, everyone
/bandit cover start              and they start using cover
/bandit peek start               and peeking out of it to shoot
/bandit stance prone             everyone down
/bandit stance free              each kit decides for itself again
/bandit shoot stop               ceasefire - they still track you
/banditpatrol on                 everyone walks the patrol route
```

The usual order to switch things on, one at a time, is `shoot` then `cover` then `peek`: each
one is easier to watch once the one before it is working.

## One bandit at a time

These act on the **last spawned** bandit - things you try on one bot and watch.

| Command | Description |
|---|---|
| `/banditgoto` (alias `/bgoto`) | Sends it to the point you are looking at, up to 512m. |
| `/banditcover [clear]` | Makes it take cover from *you* right now, and reports what it found - which spot, what kind, how far, or the tally of which test rejected every candidate. Draws markers in the world; `clear` removes them. |
| `/banditprone [start\|stop]` (alias `/bprone`) | Lie down or stand up; no argument toggles. It keeps its patrol, cover order and destination, and crawls them. |
| `/banditwave` (alias `/bwave`) | Holster the weapon, wave at you, re-arm. |

```
/banditgoto                      walk to whatever you are looking at
/banditcover                     take cover from me, and explain the choice
/banditcover clear               wipe the markers it drew
/banditprone                     toggle prone
/banditprone start               go prone and stay there
/banditwave                      prove it is a real player character
```

## Vehicle commands

| Command | Description |
|---|---|
| `/banditv <vehicle id, GUID or name>`<br>(alias `/bv`) | Spawns that vehicle in front of the last spawned bandit, with the bandit already driving it. Takes a legacy numeric ID, a GUID, or the name of an entry in the plugin's `Vehicles` list (`offroader`, `armored`, `tank` by default). |
| `/banditv drive` | Puts it in the nearest vehicle's driver seat, where it holds station. |
| `/banditv gunner[2\|3\|…]` | Puts it in a gun seat, where it tracks and engages the nearest enemy. `gunner` is F2, `gunner2` is F3, `gunner3` is F4. |
| `/banditv exit` | Gets it out. |
| `/banditvgoto` (alias `/bvgoto`) | Drives its vehicle to the point you are looking at, up to 512m, steering directly. |
| `/banditvgoto marker` | Drives to your map marker over the **road graph**, the way a convoy does. The one-hop version of a convoy, and the quickest way to find out whether a stretch of road drives at all. |
| `/banditvgoto wp [noroads]` | Drives the whole convoy route (or the patrol route if no convoy route exists). `noroads` makes it drive straight lines instead; `offroad` and `useRoads:false` mean the same. `route` is accepted for `wp`. |
| `/banditvgoto stop` | Halts it where it is. |

A vehicle name is only checked after the subcommands, so a configured vehicle called `drive`
would be unreachable - but nothing else can shadow one.

```
/banditv tank                    spawn the configured tank under the last bandit
/banditv 96                      spawn by legacy ID instead
/banditv drive                   put it in the nearest driver seat
/banditv gunner                  or the first gun seat (F2)
/banditv gunner2                 the second (F3)
/banditv exit                    out

/banditvgoto                     drive at what I am looking at
/banditvgoto marker              drive to my marker, following roads
/banditvgoto wp                  drive the recorded convoy route
/banditvgoto wp noroads          drive it in straight lines, to compare
/banditvgoto stop                hold station
```

## Events and convoys

An event buys a whole fight against a points budget - squads and crewed vehicles. The number is
a cost, not a difficulty: see
[Points and cost](Points-and-Cost).

| Command | Description |
|---|---|
| `/banditevent <cost> [<metres>\|marker] [team:<team>] [seed:<n>]`<br>(aliases `/event`, `/bevent`) | Spawns an event down your sightline or at your map marker. |
| `/banditevent check` | Prices everything the configuration could draw, and reports what is wrong with anything it cannot. `validate` and `list` do the same. |
| `/banditevent convoy <cost> [vehicles:<n>] [crew:<n>] [useRoads:false] [team:<team>] [seed:<n>]` | Spawns a convoy: vehicles full of bandits, bought against the budget, at the first convoy waypoint and driving the rest of the route. Needs at least two waypoints from `/banditevent wp`. |
| `/banditevent convoy clear` | Removes the last convoy spawned - vehicles destroyed, men despawned, including any walking after losing their ride. Leaves any other convoy running. |
| `/banditcost [apply]`<br>(aliases `/bcost`, `/costs`) | Suggests a points cost for every kit and vehicle from the game's own asset data, with the working shown. `apply` writes the suggestions into the configuration - read the report first. |

`vehicles:1 crew:1` is the one-vehicle, driver-only column: a route being driven and nothing
else, which is the shape to test a route with, since nothing is following anything.

```
/banditevent 200                 a fight worth 200 points, down your sightline
/banditevent 200 marker          the same at your map marker
/banditevent 500 300 team:red    500 points of red, 300m out
/banditevent 200 seed:1234       reproduce that exact draw
/banditevent check               price everything, and name anything broken

/banditevent convoy 300          a column worth 300 points down the route
/banditevent convoy 300 vehicles:1 crew:1     one vehicle, one driver - route testing
/banditevent convoy 300 useRoads:false        straight lines instead of the road graph
/banditevent convoy clear        remove the last convoy

/banditcost                      what the model thinks everything is worth
/banditcost apply                write those numbers into the configuration
```

## Routes

Two separate routes per map, and they are **not** the same list: a patrol route is walked, a
convoy route is driven.

| Command | Description |
|---|---|
| `/banditwp list` (alias `/banditwaypoint`) | This map's **patrol** route. No argument does the same. Reports the location-node fallback if nothing is recorded. |
| `/banditwp add` | Records a patrol waypoint at your feet. |
| `/banditwp remove` | Removes the nearest patrol waypoint within 10m. `delete` does the same. |
| `/banditwp clear` | Removes them all. |
| `/banditevent wp list` | This map's **convoy** route. No argument does the same. `waypoint` and `waypoints` are accepted for `wp`. |
| `/banditevent wp set [marker]` | Records a convoy waypoint at your feet, or at your map marker. `add` does the same. |
| `/banditevent wp remove <n>` | Removes one by the number the list prints - not by proximity, since a convoy route runs across the map. |
| `/banditevent wp clear` | Removes them all. |
| `/banditroads` (alias `/broads`) | What the road graph found on this map: node count, how many gaps between roads it had to bridge to make the network routable, and which node you are standing nearest. |
| `/banditroads route` | Costs a route from you to your map marker - metres on road against metres direct, the mix of road types, and how much of it crosses bridged gaps. |

A convoy spawns at the first waypoint and drives through the rest, so it needs at least two.

```
/banditwp add                    record a patrol point where you stand
/banditwp list                   what the patrol route looks like
/banditwp remove                 drop the nearest one
/banditpatrol on                 walk it

/banditevent wp set              record a convoy point where you stand
/banditevent wp set marker       or at your map marker
/banditevent wp list             numbered, with distances from you
/banditevent wp remove 3         drop the third
/banditevent wp clear            start over

/banditroads                     is there a road graph on this map at all
/banditroads route               what a route to my marker actually costs
```

## Team commands

| Command | Description |
|---|---|
| `/banditteam`<br>(aliases `/team`, `/teams`, permission `bandit.team`) | Reports which team you are on. |
| `/banditteam list` | Shows the teams. `teams` does the same. |
| `/banditteam join <team>` | Puts you on one. |
| `/banditteam leave` | Takes you off. Every bandit is then hostile to you, if `HostileToUngrouped` is on. |
| `/banditteam <player> <team>` | Puts somebody else on one. |

Teams are real in-game groups. Bandits never shoot their own team and always shoot the others,
so bandit armies can fight each other. See [Teams](Teams).

```
/banditteam list                 what sides exist
/banditteam join blue            pick one
/banditteam                      which am I on
/banditteam Peter red            put a named player on red
/banditteam leave                back to being everyone's enemy

# a two-sided battle to walk through:
/squadspawn rifle 150 team:red
/squadspawn rifle 150 team:blue
```

## Diagnostics

| Command | Description |
|---|---|
| `/banditstatus` | What every bandit is doing - state, target, destination, stance, seat, and whether it is on an A* path or steering directly. |
| `/banditperf [reset]` | Server frame time over the last window. `reset` starts a new one, so a scenario can be measured against a baseline. |
| `/banditnavlog on\|off` (alias `/bnavlog`) | Vehicle navigation commentary in the server log: every driving bandit reports twice a second, plus stalls, refusals and gives-up as they happen. `true`/`1` and `false`/`0` work too. |
| `/banditnavlog route [seconds]` | Paints the **last planned** route on the ground - not one re-planned now. Blue road point, yellow corner arc, red waypoint, green where it is steering now. Default 60s, capped at 600. `show` does the same. |
| `/banditnavlog clear` | Wipes those markers. |
| `/banditroads show [radius] [seconds]` | Paints the road graph around you - what the plugin *believes* a road is, which is not the same as what you can see. Blue centre line, red junction, green the node nearest you. Radius defaults to 120m and caps at 400; seconds default to 60 and cap at 600. |
| `/banditroads clear` | Wipes those markers. |

```
/banditperf reset                start a clean measurement window
/banditevent 400                 ...run the scenario...
/banditperf                      what it cost

/banditnavlog on                 narrate the driving into the server log
/banditvgoto marker              send it somewhere
/banditnavlog route              draw the route it planned
/banditnavlog route 300          leave the markers up for five minutes
/banditnavlog clear

/banditroads show                paint the road graph 120m around me
/banditroads show 400 120        the widest view, for two minutes
/banditroads clear
```

If a convoy drives somewhere strange, it is nearly always a routing answer rather than a driving
one - and routing happens before anything is spawned. `/banditroads route` and
`/banditnavlog route` are how you look at it.

## Housekeeping

| Command | Description |
|---|---|
| `/banditclear` (alias `/clearbandits`) | Removes every spawned bandit. The big hammer - `/banditevent convoy clear` is the narrow one. |

Use `/banditclear` before disconnecting - bots occupy player slots, and their presence affects
the client-side exit timer.

## Worked examples

**Watching one bandit, one behaviour at a time.** The reason `/bandit` spawns something inert.

```
/bandit rifleman                 one man, holding fire, tracking you
/banditgoto                      send it to where you are looking
/banditprone                     watch it crawl there instead
/banditprone stop
/bandit shoot start              now it fights
/bandit cover start              now it uses cover
/banditcover                     and explain the spot it picked
/banditclear
```

**A fight to walk into.**

```
/squadspawn rifle 200            four riflemen, 200m out, unaware
                                 ...walk in on them...
/banditstatus                    what each of them thought was happening
/banditclear
```

**Testing a convoy route on a new map.** Do it with one vehicle before spending points on a
column.

```
/banditroads                     does this map have a usable road graph
/banditevent wp clear
/banditevent wp set              stand at the start
                                 ...drive to the next point...
/banditevent wp set
/banditevent wp list             at least two, in the right order
/banditnavlog on
/banditevent convoy 100 vehicles:1 crew:1
/banditnavlog route              look at the line it planned
                                 ...if it is wrong, /banditroads show around the bad stretch...
/banditevent convoy clear
/banditevent convoy 400          now the real column
```

**Measuring what a scenario costs the server.**

```
/banditperf reset
/banditevent 400 marker
                                 ...let it run...
/banditperf
/banditclear
/banditperf reset                and a clean baseline to compare against
```

## Alias index

| Command | Aliases |
|---|---|
| `/bandit` | - |
| `/squadspawn` | `/spawnsquad`, `/squad` |
| `/banditevent` | `/event`, `/bevent` |
| `/banditcost` | `/bcost`, `/costs` |
| `/banditgoto` | `/bgoto` |
| `/banditcover` | - |
| `/banditprone` | `/bprone` |
| `/banditwave` | `/bwave` |
| `/banditv` | `/bv` |
| `/banditvgoto` | `/bvgoto` |
| `/banditwp` | `/banditwaypoint` |
| `/banditroads` | `/broads` |
| `/banditnavlog` | `/bnavlog` |
| `/banditpatrol` | - |
| `/banditteam` | `/team`, `/teams` |
| `/banditstatus` | - |
| `/banditperf` | - |
| `/banditclear` | `/clearbandits` |
