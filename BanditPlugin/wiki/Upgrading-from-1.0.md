# Upgrading from 1.0

Version 1.0 had eighteen separate commands, each with its own aliases. They are now one
command, `/bandit`, and the old names no longer exist - a script, bind or habit that types
`/squadspawn` or `/bv` gets "unknown command". This page maps each old command to its new form.
[Commands](Commands) has the full reference.

Nothing else needs changing. Permissions are the same (`bandit.spawn`, and `bandit.team` for
team management), and an existing configuration file loads as it is - the new cover settings
are added with their defaults the first time the server starts.

## What behaves differently

- **No aliases.** `/bv`, `/bgoto`, `/event`, `/team`, `/clearbandits` and the rest are gone.
  Short aliases collided with other plugins.
- **Orders go to a target.** In 1.0 an order reached the last spawned *bandit*. Now it reaches
  the last spawned *thing*: after `/bandit squad spawn`, `/bandit shoot off` holds the whole
  squad's fire. Name a target to reach anything else - `7`, `squad 2`, `event 1`, `convoy 1`,
  `vehicle 4`, `team red` or `all`. `/bandit list` shows the numbers.
- **Switches are `on` or `off`.** `start`/`stop` became `on`/`off`, and nothing toggles any
  more: `/banditpatrol` and `/banditprone` with no argument used to flip, and the new forms
  need you to say which.
- **`/bandit vehicle spawn` brings its own crew.** `/banditv <id>` put the last spawned bandit
  in a new vehicle. Now the vehicle arrives crewed, as a one-vehicle event. To put a bandit you
  already have into a vehicle, use `/bandit seat driver`.
- **`/bandit clear` removes the last thing spawned**, not everything. `/bandit clear all` is
  the old `/banditclear`.
- **Patrol and convoy routes share their words.** `/bandit route patrol ...` and
  `/bandit route convoy ...` both take `add [marker]`, `remove [<n>]`, `list` and `clear`. A
  patrol waypoint can now be removed by number, and placed at your map marker.

## Old command to new

### Spawning

| 1.0 | Now |
|---|---|
| `/bandit [<kit>]` | `/bandit spawn [<kit>]` |
| `/squadspawn [<type>] [<metres>\|marker]` | `/bandit squad spawn [<type>] [<metres>\|marker]` |
| `/banditevent <cost> ...` | `/bandit event spawn <cost> ...` |
| `/banditevent convoy <cost> ... [useRoads:false]` | `/bandit convoy spawn <cost> ... [noroads]` |
| `/banditv <id\|GUID\|name>` | `/bandit vehicle spawn <type\|id\|GUID>` - crewed, see above |

### Orders

| 1.0 | Now |
|---|---|
| `/bandit shoot start\|stop` | `/bandit [target] shoot on\|off` |
| `/bandit cover start\|stop` | `/bandit [target] cover on\|off` |
| `/bandit peek start\|stop` | `/bandit [target] peek on\|off` |
| `/bandit stance ...` | `/bandit [target] stance stand\|crouch\|prone\|free` |
| `/banditprone [start\|stop]` | `/bandit [target] stance prone`, and `stance free` to undo it |
| `/banditpatrol [on\|off]` | `/bandit [target] patrol on\|off` |
| `/banditgoto` | `/bandit [target] goto [marker]` |
| `/banditwave` | `/bandit [target] wave` |

### Vehicles

| 1.0 | Now |
|---|---|
| `/banditv drive` | `/bandit [target] seat driver` |
| `/banditv gunner[2\|3\|…]` | `/bandit [target] seat gunner[2\|3\|…]` |
| `/banditv exit` | `/bandit [target] seat exit` |
| `/banditvgoto` | `/bandit [target] drive` |
| `/banditvgoto marker` | `/bandit [target] drive marker` |
| `/banditvgoto wp [noroads]` | `/bandit [target] drive route [noroads]` |
| `/banditvgoto stop` | `/bandit [target] drive stop` |

### Routes

| 1.0 | Now |
|---|---|
| `/banditwp [list]` | `/bandit route patrol list` |
| `/banditwp add` | `/bandit route patrol add [marker]` |
| `/banditwp remove` | `/bandit route patrol remove [<n>]` |
| `/banditwp clear` | `/bandit route patrol clear` |
| `/banditevent wp [list]` | `/bandit route convoy list` |
| `/banditevent wp set [marker]` | `/bandit route convoy add [marker]` |
| `/banditevent wp remove <n>` | `/bandit route convoy remove [<n>]` |
| `/banditevent wp clear` | `/bandit route convoy clear` |

`/bandit route` on its own lists both routes.

### Teams

| 1.0 | Now |
|---|---|
| `/banditteam` | `/bandit team` |
| `/banditteam list` | `/bandit team list` |
| `/banditteam join <team>` | `/bandit team join <team>` |
| `/banditteam leave` | `/bandit team leave` |
| `/banditteam <player> <team>` | `/bandit team set <player> <team>` |

### Information and diagnostics

| 1.0 | Now |
|---|---|
| `/bandit kits` | `/bandit info kits` |
| `/squadspawn squads` | `/bandit info squads` |
| `/banditevent check` | `/bandit info event` |
| - | `/bandit info vehicles` (new) |
| - | `/bandit list` (new) |
| `/banditstatus` | `/bandit debug status [target]` |
| `/banditperf [reset]` | `/bandit debug perf [reset]` |
| `/banditcost [apply]` | `/bandit debug cost [apply]` |
| `/banditcover` | `/bandit debug cover [target]` |
| `/banditnavlog on\|off` | `/bandit debug navlog on\|off` |
| `/banditnavlog route [seconds]` | `/bandit debug navlog route [seconds]` |
| `/banditroads` | `/bandit debug roads` |
| `/banditroads route` | `/bandit debug roads route` |
| `/banditroads show [radius] [seconds]` | `/bandit debug roads show [radius] [seconds]` |
| `/banditcover clear`, `/banditnavlog clear`, `/banditroads clear` | `/bandit debug clear` - all markers at once |

### Clearing

| 1.0 | Now |
|---|---|
| `/banditclear` | `/bandit clear all` |
| `/banditevent convoy clear` | `/bandit clear convoy <n>`, or `/bandit clear` if it was the last thing spawned |
| - | `/bandit clear [target]` - any one bandit, squad, event, convoy, vehicle or team |

### New in this version

| Command | What it is |
|---|---|
| `/bandit observe on\|off` | Bandits stop seeing you, so you can stand in a fight and watch it. |
| `/bandit test ...` | End-to-end tests on the live server. See [Testing](Testing). |
| `/bandit help [topic]` | The command list in game. |
