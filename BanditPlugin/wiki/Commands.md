# Commands

| Command | Permission | Description |
|---|---|---|
| `/bandit [<kit>] [team:<team>]` | `bandit.spawn` | Spawns a bandit where you're looking, facing you. |
| `/banditevent <cost> [<metres>\|marker] [team:<team>] [seed:<n>]` (aliases `/event`, `/bevent`) | `bandit.spawn` | Buys a whole fight against a points budget - squads and crewed vehicles. `/banditevent check` prices everything the configuration can draw. |
| `/banditevent wp set [marker]\|list\|remove <n>\|clear` | `bandit.spawn` | This map's **convoy route**, at your feet or your map marker. Separate from `/banditwp`, which is the patrol route. |
| `/banditevent convoy <cost> [vehicles:<n>] [crew:<n>] [useRoads:false] [team:<team>] [seed:<n>]` | `bandit.spawn` | Spends the whole budget on crewed vehicles and drives them along the convoy route. Needs at least two waypoints. `vehicles:1 crew:1` is the one-vehicle, driver-only column - a route being driven and nothing else, which is the shape to test a route with. |
| `/banditroads [route]` (alias `/broads`) | `bandit.spawn` | What the road graph found on this map, how many gaps between roads it had to bridge to be routable, and what a route from you to your map marker costs. |
| `/squadspawn [<type>] [<metres>\|marker] [team:<team>]` (aliases `/spawnsquad`, `/squad`) | `bandit.spawn` | Puts a whole squad down fighting - `basic`, `rifle` or `sniper` by default. Each type names the kits it is built from and its own spacing, spawn distance and group behaviour. `/squadspawn squads` lists them. |
| `/banditteam [list\|join <team>\|leave\|<player> <team>]` (aliases `/team`, `/teams`) | `bandit.team` | Which side someone is on. Bandits never shoot their own team and always shoot the others. |
| `/banditgoto` (alias `/bgoto`) | `bandit.spawn` | Sends the last spawned bandit to the point you're looking at (up to 512m). |
| `/banditpatrol [on\|off]` | `bandit.spawn` | Starts/stops **all** bandits patrolling this map's waypoints. No argument toggles. |
| `/banditwp add\|remove\|clear\|list` | `bandit.spawn` | Records this map's patrol route at your feet. |
| `/banditcover` | `bandit.spawn` | Makes the last spawned bandit take cover from you now, and reports what it found. |
| `/banditstop` | `bandit.spawn` | All bandits hold fire. They still move and track you. |
| `/banditshoot` | `bandit.spawn` | Weapons free again. |
| `/banditv drive\|gunner[2\|3\|…]\|exit` (alias `/bv`) | `bandit.spawn` | Puts the last spawned bandit in the nearest vehicle's driver seat (holds station) or a gun seat - `gunner` is F2, `gunner2` is F3, `gunner3` is F4 - where it tracks and engages the nearest player. Or gets it out. |
| `/banditvgoto [stop]` (alias `/bvgoto`) | `bandit.spawn` | Drives that bandit's vehicle to the point you are looking at. |
| `/banditstatus` | `bandit.spawn` | What each bandit is doing - state, target, destination, A* or steering. |
| `/banditclear` (alias `/clearbandits`) | `bandit.spawn` | Removes all spawned bandits. |

Use `/banditclear` before disconnecting - bots occupy player slots, and their presence affects
the client-side exit timer.
