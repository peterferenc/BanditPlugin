# Commands

Everything is one command, `/bandit`, followed by what to do. Spawning, orders, routes and
diagnostics take the `bandit.spawn` permission; `/bandit team ...` takes `bandit.team`. Grant
both in `Rocket/Permissions.config.xml`. `/bandit help` lists the commands in game, and
`/bandit help spawn|orders|targets|route|team|debug|test|clear` gives the details.

```
/bandit spawn | squad spawn | event spawn | convoy spawn | vehicle spawn
/bandit [target] shoot|cover|peek|stance|patrol|goto|wave|seat|drive ...
/bandit list | clear [target] | route ... | team ... | info ... | debug ... | test ... | help [topic]
```

## Conventions

**Orders go to a target, and no target means the last thing spawned.** Spawn something, then
tell it what to do: `/bandit squad spawn rifle` then `/bandit shoot off` holds that squad's
fire, and nobody else's. To talk to anything else, name it before the order:

| Target | Means |
|---|---|
| `7` | Bandit #7. Every bandit's number is in its name - "Red rifleman #7" - so it can be read off through a scope. |
| `squad 2` | Squad 2, from `/bandit squad spawn` or part of an event. |
| `event 1` | Every bandit in event 1. |
| `convoy 1` | Every bandit in convoy 1. |
| `vehicle 4` | The crew of vehicle 4, wherever they are now - in it, or out fighting. |
| `team red` | Every bandit on a team. |
| `all` | Every bandit on the map. |
| `last` | The last thing spawned - the same as giving no target. |

`/bandit list` shows every number. A target on its own, like `/bandit squad 2`, reports what
everyone in it is doing.

**A bandit spawned by `/bandit spawn` does nothing until told.** It stands, tracks whoever it
can see, and waits - so you can switch on one behaviour at a time and watch it. A squad from
`/bandit squad spawn`, and everything an event or convoy spawns, comes out fighting instead.

**Bandits always turn to face the nearest enemy they can see.** "Nearest" is measured inside
that kit's `TargetAcquireRange`, and since [teams](Teams) exist, an enemy is another team's
bandit as readily as it is a player. Whoever a bandit is already fighting keeps the slot unless
somebody else is meaningfully closer, so a bandit does not flick between two targets standing at
the same distance. Facing is not shooting: a bandit tracks you whether or not it has weapons free.

**Switches are `on` or `off`, and nothing toggles.** "Toggle" means nothing to a squad that is
half one way and half the other, so a bare `/bandit shoot` is a usage error rather than a guess.

## Shared arguments

These mean the same thing wherever they appear, and `team:` and `seed:` may sit anywhere in the
line - they are lifted out before the positional words are read.

| Argument | Meaning |
|---|---|
| `team:<team>` | The side it fights on. `team=blue` works too. An unknown name is refused rather than quietly spawning onto the default team. Default teams: `bandits`, `red`, `blue`. |
| `seed:<n>` | Reproduces one random draw exactly. The reply prints the seed it used, so an interesting event can be run again. |
| `<metres>` | How far down your sightline to place things. Clamped to a minimum of 15m. |
| `marker` | Place at, or go to, your map marker instead. `map` is accepted as well. |

## Running from the server console

Most commands work from the console. Anything that reads where you are standing or looking
needs a player: every `spawn`, `goto`, `wave`, `drive` to a point or your marker,
`route ... add`, `debug cover` and `debug roads`. Orders by target, `drive route`, `list`,
`clear`, `info` and the rest of `debug` work from either.

## Spawning

| Command | Description |
|---|---|
| `/bandit spawn [<kit>] [team:<team>]` | One bandit of a kit where you are looking, facing you. Up to 50m down your sightline; if that hits nothing, 3m in front of you. Without a kit it spawns the `DefaultKit`. It holds fire and takes no cover until ordered. |
| `/bandit squad spawn [<type>] [<metres>\|marker] [team:<team>]` | A squad in formation down your sightline, at a distance you name or at your map marker. Default distance is that type's own, deliberately past its eyes so the squad spawns unaware and you walk in on it. Comes out fighting. |
| `/bandit event spawn <cost> [<metres>\|marker] [team:<team>] [seed:<n>]` | A whole fight bought against a points budget - several squads and crewed vehicles. The number is a cost, not a difficulty: see [Points and cost](Points-and-Cost). |
| `/bandit convoy spawn <cost> [vehicles:<n>] [crew:<n>] [noroads] [team:<team>] [seed:<n>]` | Vehicles full of bandits, bought against the budget, at the first convoy waypoint and driving the rest of the route. Needs at least two waypoints from `/bandit route convoy add`. `noroads` drives straight lines instead of the road graph. |
| `/bandit vehicle spawn <type\|id\|GUID> [<metres>\|marker] [team:<team>]` | One crewed vehicle. A type from the `Vehicles` list (`offroader`, `armored`, `tank` by default) arrives with its configured crew and behaves as it would in an event - waits for contact, then drives at it. A raw legacy ID or GUID arrives with a `DefaultKit` driver and a gunner in every turret seat, and holds where it spawned. |

An event is several squads and vehicles, and each vehicle's crew is a squad of its own - so
`/bandit event 1 shoot off` reaches all of them, and `/bandit squad 4 shoot off` only one.

`vehicles:1 crew:1` is the one-vehicle, driver-only convoy: a route being driven and nothing
else, which is the shape to test a route with, since nothing is following anything.

```
/bandit spawn                          one rifleman where you are looking, holding fire
/bandit spawn mg                       one machinegunner instead
/bandit spawn marksman team:red        one marksman on the red team

/bandit squad spawn                    a 'basic' squad at its own distance, already fighting
/bandit squad spawn rifle              four riflemen at 130m
/bandit squad spawn sniper 300         three men at 300m down your sightline
/bandit squad spawn rifle marker team:blue

/bandit event spawn 200                a fight worth 200 points, down your sightline
/bandit event spawn 500 300 team:red   500 points of red, 300m out
/bandit event spawn 200 seed:1234      reproduce that exact draw

/bandit convoy spawn 300               a column worth 300 points down the route
/bandit convoy spawn 300 vehicles:1 crew:1     one vehicle, one driver - route testing

/bandit vehicle spawn tank             the configured tank, crewed
/bandit vehicle spawn 96 marker        a raw vehicle ID at your marker, driver and gunners only
```

## Orders

| Command | Description |
|---|---|
| `/bandit [target] shoot on\|off` | Weapons free, or hold fire. Holding fire, they still move and still track you. |
| `/bandit [target] cover on\|off` | Look for cover and move to it, re-finding it as the threat moves - or stop where they are and stay there. |
| `/bandit [target] peek on\|off` | Once in cover, alternate hiding with stepping out to shoot - or stay down. |
| `/bandit [target] stance stand\|crouch\|prone\|free` | Hold a stance. `free` hands the choice back to each kit, so the machinegunner goes prone on contact again. A bandit keeps its patrol, cover order and destination, and crawls them. |
| `/bandit [target] patrol on\|off` | Walk this map's patrol route. Refused if the map has no waypoints and no location-node fallback. |
| `/bandit [target] goto [marker]` | Walk to the point you are looking at, up to 512m, or to your map marker. One bandit goes to the point; a group spreads into a formation across it, facing the way you were looking. Anyone in a vehicle stays put. |
| `/bandit [target] wave` | Holster the weapon, wave at you, re-arm. |

A bandit spawned afterwards starts on the configured defaults again (`HoldFireByDefault`,
`CoverByDefault`, `PeekByDefault`). The usual order to switch things on, one at a time, is
`shoot` then `cover` then `peek`: each one is easier to watch once the one before it is working.

```
/bandit spawn                    one rifleman, holding fire
/bandit shoot on                 now it fights
/bandit cover on                 and uses cover
/bandit peek on                  and peeks out of it to shoot
/bandit stance prone             down
/bandit stance free              its kit decides again
/bandit goto                     walk to whatever I am looking at

/bandit squad 2 goto marker      squad 2 to my marker, in formation
/bandit team red shoot off       ceasefire for the red side
/bandit all patrol on            everyone walks the patrol route
/bandit 7 wave                   bandit #7 waves
```

## Vehicle orders

| Command | Description |
|---|---|
| `/bandit [target] seat driver` | Into the nearest vehicle's driver seat, where it holds station. One bandit at a time. |
| `/bandit [target] seat gunner[2\|3\|…]` | Into a gun seat, where it tracks and engages the nearest enemy. `gunner` is F2, `gunner2` is F3, `gunner3` is F4. One bandit at a time. |
| `/bandit [target] seat exit` | Out. Works on any target - everyone in it gets out. |
| `/bandit [target] drive` | Drives to the point you are looking at, up to 512m, steering directly. Goes to whoever in the target is at a wheel, so `/bandit vehicle 4 drive` and `/bandit squad 3 drive` both reach the driver. |
| `/bandit [target] drive marker` | Drives to your map marker over the **road graph**, the way a convoy does. The one-hop version of a convoy, and the quickest way to find out whether a stretch of road drives at all. |
| `/bandit [target] drive route [noroads]` | Drives the whole convoy route (or the patrol route if no convoy route exists). `noroads` makes it drive straight lines instead. |
| `/bandit [target] drive stop` | Halts it where it is. |

A vehicle an event spawned is driving to contact on its own; `drive` takes it off that script
and hands you the wheel. A convoy's vehicles are left alone - the column steers itself - so clear
the convoy to take one over.

```
/bandit spawn                    a bandit to put in something
/bandit seat driver              into the nearest driver seat
/bandit drive                    drive at what I am looking at
/bandit drive marker             drive to my marker, following roads
/bandit drive route noroads      drive the convoy route in straight lines, to compare
/bandit drive stop               hold station
/bandit seat exit                out

/bandit vehicle spawn tank       a crewed tank - now the last thing spawned
/bandit drive marker             so this drives the tank
```

## Routes

Two separate routes per map, and they are **not** the same list: a patrol route is walked, a
convoy route is driven. Both take the same words.

| Command | Description |
|---|---|
| `/bandit route` | Lists both routes. |
| `/bandit route patrol\|convoy add [marker]` | Records a waypoint at your feet, or at your map marker. |
| `/bandit route patrol\|convoy remove [<n>]` | Removes a waypoint by the number the list prints, or with no number the nearest within 10m. |
| `/bandit route patrol\|convoy list` | The route, numbered, with distances from you. Reports the location-node fallback for a patrol route with nothing recorded. |
| `/bandit route patrol\|convoy clear` | Removes them all. |

A convoy spawns at the first waypoint and drives through the rest, so it needs at least two.

```
/bandit route patrol add         record a patrol point where you stand
/bandit route patrol list        what the patrol route looks like
/bandit route patrol remove      drop the nearest one
/bandit all patrol on            walk it

/bandit route convoy add         record a convoy point where you stand
/bandit route convoy add marker  or at your map marker
/bandit route convoy remove 3    drop the third
/bandit route convoy clear       start over
```

## Team commands

| Command | Description |
|---|---|
| `/bandit team` | Reports which team you are on. Permission `bandit.team`. |
| `/bandit team list` | Shows the teams and how many are on each. |
| `/bandit team join <team>` | Puts you on one. |
| `/bandit team leave` | Takes you off. Every bandit is then hostile to you, if `HostileToUngrouped` is on. |
| `/bandit team set <player> <team>` | Puts somebody else on one. |

Teams are real in-game groups. Bandits never shoot their own team and always shoot the others,
so bandit armies can fight each other. See [Teams](Teams). `/bandit team red ...` with an order
after it is not a team command - it is an order to every bandit on red.

```
/bandit team list                what sides exist
/bandit team join blue           pick one
/bandit team set Peter red       put a named player on red
/bandit team leave               back to being everyone's enemy

# a two-sided battle to walk through:
/bandit squad spawn rifle 150 team:red
/bandit squad spawn rifle 150 team:blue
```

## Information

| Command | Description |
|---|---|
| `/bandit list` | Everything on the map by number: events and convoys, the squads in them, the bandits in those, and who is on their own. Also says what the last thing spawned is. |
| `/bandit info kits` | The kits and, for each, the range it fires to, notices at and fights at. |
| `/bandit info squads` | The squad types, the kits each is built from, and where each places itself. |
| `/bandit info vehicles` | The vehicle types, the asset each resolves to on this map, its crew and its turret seats. |
| `/bandit info event` | Prices everything an event could draw, and reports what is wrong with anything it cannot. |

Default kits are `rifleman`, `mg`, `marksman` and `breacher`; default squad types are `basic`
(five men, mixed), `rifle` (four riflemen, 130m) and `sniper` (two marksmen and a rifleman,
260m).

## Diagnostics

| Command | Description |
|---|---|
| `/bandit debug status [target]` | What every bandit - or every bandit in the target - is doing: state, target, destination, stance, seat, and whether it is on an A* path or steering directly. |
| `/bandit debug perf [reset]` | Server frame time over the last window. `reset` starts a new one, so a scenario can be measured against a baseline. |
| `/bandit debug cost [apply]` | Suggests a points cost for every kit and vehicle from the game's own asset data, with the working shown. `apply` writes the suggestions into the configuration - read the report first. |
| `/bandit debug cover [target]` | Makes one bandit take cover from *you* right now, and reports what it found - which spot, what kind, how far, or the tally of which test rejected every candidate. Draws markers in the world. |
| `/bandit debug navlog on\|off` | Vehicle navigation commentary in the server log: every driving bandit reports twice a second, plus stalls, refusals and gives-up as they happen. |
| `/bandit debug navlog route [seconds]` | Paints the **last planned** route on the ground - not one re-planned now. Blue road point, yellow corner arc, red waypoint, green where it is steering now. Default 60s, capped at 600. |
| `/bandit debug roads` | What the road graph found on this map: node count, how many gaps between roads it had to bridge, and which node you are standing nearest. |
| `/bandit debug roads route` | Costs a route from you to your map marker - metres on road against metres direct, the mix of road types, and how much of it crosses bridged gaps. |
| `/bandit debug roads show [radius] [seconds]` | Paints the road graph around you - what the plugin *believes* a road is. Blue centre line, red junction, green the node nearest you. Radius defaults to 120m and caps at 400; seconds default to 60 and cap at 600. |
| `/bandit debug clear` | Wipes every debug marker: cover, route and road. |

If a convoy drives somewhere strange, it is nearly always a routing answer rather than a driving
one - and routing happens before anything is spawned. `/bandit debug roads route` and
`/bandit debug navlog route` are how you look at it.

## Testing

End-to-end tests, run on this server against real bandits. See [Testing](Testing) for what each
test checks, how to pick an arena, and running them unattended with `tools/bandit-test.sh`.

| Command | Description |
|---|---|
| `/bandit test [list]` | The tests, and this map's arena points. |
| `/bandit test run all\|<name>[,<name>] [here]` | Runs the tests at this map's arena, or with `here` at the point you are looking at. Needs an empty field - `/bandit clear all` first. |
| `/bandit test stop` | Aborts the run; the test in progress is cleaned up and reported as aborted. |
| `/bandit test results` | The last run's results again. |
| `/bandit test arena set [<name>]` | Records where you stand, facing the way you face, as an arena point. Unnamed is `arena`, which every test falls back to. |
| `/bandit test arena remove <name>` | Removes one. |

## Housekeeping

| Command | Description |
|---|---|
| `/bandit clear [target]` | Removes the target, and the vehicles it came with. No target is the last thing spawned. A squad or team leaves its vehicles behind; an event, convoy or vehicle takes them with it. |
| `/bandit clear all` | Every bandit, and every vehicle this plugin spawned. The big hammer. |

Use `/bandit clear all` before disconnecting - bots occupy player slots, and their presence
affects the client-side exit timer.

## Worked examples

**Watching one bandit, one behaviour at a time.** The reason `/bandit spawn` spawns something
inert.

```
/bandit spawn rifleman           one man, holding fire, tracking you
/bandit goto                     send it to where you are looking
/bandit stance prone             watch it crawl there instead
/bandit stance free
/bandit shoot on                 now it fights
/bandit cover on                 now it uses cover
/bandit debug cover              and explain the spot it picked
/bandit clear
```

**A fight to walk into.**

```
/bandit squad spawn rifle 200    four riflemen, 200m out, unaware
                                 ...walk in on them...
/bandit squad 1                  what each of them thought was happening
/bandit clear all
```

**Testing a convoy route on a new map.** Do it with one vehicle before spending points on a
column.

```
/bandit debug roads              does this map have a usable road graph
/bandit route convoy clear
/bandit route convoy add         stand at the start
                                 ...drive to the next point...
/bandit route convoy add
/bandit route convoy list        at least two, in the right order
/bandit debug navlog on
/bandit convoy spawn 100 vehicles:1 crew:1
/bandit debug navlog route       look at the line it planned
                                 ...if it is wrong, /bandit debug roads show around the bad stretch...
/bandit clear                    the convoy, as the last thing spawned
/bandit convoy spawn 400         now the real column
```

**Measuring what a scenario costs the server.**

```
/bandit debug perf reset
/bandit event spawn 400 marker
                                 ...let it run...
/bandit debug perf
/bandit clear all
/bandit debug perf reset         and a clean baseline to compare against
```
