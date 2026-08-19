# Commands

Everything takes the `bandit.spawn` permission except `/banditteam`, which takes `bandit.team`.

Three things are worth knowing before the table.

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

## Spawning

| Command | Description |
|---|---|
| `/bandit [<kit>] [team:<team>]` | Spawns a bandit of a kit where you are looking, facing you. Up to 50m down your sightline; if that hits nothing, 3m in front of you. Without a kit it spawns the `DefaultKit`. It holds fire and takes no cover until ordered. |
| `/bandit kits` | Lists the kits and, for each, the range it fires to, notices at and fights at. |
| `/squadspawn [<type>] [<metres>\|marker] [team:<team>]`<br>(aliases `/spawnsquad`, `/squad`) | Spawns a squad of a type, in formation down your sightline, at a distance you name or at your map marker. Default distance is that type's own, deliberately past its eyes so the squad spawns unaware and you walk in on it. Comes out fighting. |
| `/squadspawn squads` | Lists the squad types, the kits each is built from, and where each places itself. |
| `/banditevent <cost> [<metres>\|marker] [team:<team>] [seed:<n>]`<br>(aliases `/event`, `/bevent`) | Spawns a whole fight bought against a points budget - squads and crewed vehicles - down your sightline or at your map marker. The number is a cost, not a difficulty: see [Points and cost](Points-and-Cost). `seed:` reproduces one event exactly; the reply prints the seed it used. |
| `/banditevent convoy <cost> [useRoads:false] [team:<team>] [seed:<n>]` | Spawns a convoy: vehicles full of bandits, bought against the budget, at the first convoy waypoint and driving the rest of the route. Needs at least two waypoints from `/banditevent wp`. `useRoads:false` makes it drive straight lines instead of following the road graph. |
| `/banditv <vehicle id, GUID or name>`<br>(alias `/bv`) | Spawns that vehicle in front of the last spawned bandit, with the bandit already driving it. Takes a legacy numeric ID, a GUID, or the name of an entry in the plugin's `Vehicles` list. |
| `/banditevent check` | Prices everything the configuration could draw, and reports what is wrong with anything it cannot. |
| `/banditcost [apply]`<br>(aliases `/bcost`, `/costs`) | Suggests a points cost for every kit and vehicle from the game's own asset data, with the working shown. `apply` writes the suggestions into the configuration. See [Points and cost](Points-and-Cost). |

## Standing orders

These apply to **every live bandit**, not the last one spawned - they are orders for the field.
A bandit spawned afterwards starts on the configured defaults again (`HoldFireByDefault`,
`CoverByDefault`, `PeekByDefault`).

| Command | Description |
|---|---|
| `/bandit shoot start\|stop` | Orders every bandit to weapons free, or to hold fire. Holding fire, they still move and still track you. |
| `/bandit cover start\|stop` | Orders every bandit to look for cover and move to it, re-finding it as the threat moves - or to stop where it is and stay there. |
| `/bandit peek start\|stop` | Orders every bandit, once in cover, to alternate hiding with stepping out to shoot - or to stay down. |
| `/bandit stance stand\|crouch\|prone\|free` | Orders every bandit to hold a stance. `free` hands the choice back to each kit, so the machinegunner goes prone on contact again. |
| `/banditpatrol [on\|off]` | Orders every bandit to start or stop patrolling this map's waypoints. No argument toggles. |

## One bandit at a time

These act on the **last spawned** bandit - things you try on one bot and watch.

| Command | Description |
|---|---|
| `/banditgoto` (alias `/bgoto`) | Sends it to the point you are looking at, up to 512m. |
| `/banditcover [clear]` | Makes it take cover from *you* right now, and reports what it found - which spot, what kind, how far, or the tally of which test rejected every candidate. Draws markers in the world; `clear` removes them. |
| `/banditprone [start\|stop]` (alias `/bprone`) | Makes it lie down or stand up; no argument toggles. It keeps its patrol, cover order and destination, and crawls them. |
| `/banditv drive\|gunner[2\|3\|…]\|exit` (alias `/bv`) | Puts it in the nearest vehicle's driver seat, where it holds station, or a gun seat, where it tracks and engages the nearest enemy - `gunner` is F2, `gunner2` is F3, `gunner3` is F4. `exit` gets it out. |
| `/banditvgoto [stop]` (alias `/bvgoto`) | Drives its vehicle to the point you are looking at. `stop` halts it. |
| `/banditwave` (alias `/bwave`) | Makes it holster its weapon, wave at you, and re-arm. |

## Routes

Two separate routes per map, and they are not the same list.

| Command | Description |
|---|---|
| `/banditwp add\|remove\|clear\|list` (alias `/banditwaypoint`) | Edits this map's **patrol** route, recorded at your feet. What `/banditpatrol` walks. |
| `/banditevent wp set [marker]\|list\|remove <n>\|clear` | Edits this map's **convoy** route, at your feet or your map marker. What `/banditevent convoy` drives. Removal is by the number the list prints. |
| `/banditroads [route]` (alias `/broads`) | Reports what the road graph found on this map, and what a route from you to your map marker costs. |

## Teams

| Command | Description |
|---|---|
| `/banditteam [list\|join <team>\|leave\|<player> <team>]`<br>(aliases `/team`, `/teams`, permission `bandit.team`) | Shows the teams, puts you or another player on one, or takes you off. Bandits never shoot their own team and always shoot the others. See [Teams](Teams). |

## Housekeeping

| Command | Description |
|---|---|
| `/banditstatus` | Reports what every bandit is doing - state, target, destination, stance, seat, and whether it is on an A* path or steering directly. |
| `/banditperf [reset]` | Reports server frame time over the last window. `reset` starts a new one. |
| `/banditclear` (alias `/clearbandits`) | Removes every spawned bandit. |

Use `/banditclear` before disconnecting - bots occupy player slots, and their presence affects
the client-side exit timer.
