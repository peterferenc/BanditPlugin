# Teams

A team is not a tag this plugin keeps to itself - it is a real in-game **group**, the same thing
vanilla makes when players form one between themselves. `BanditTeams` derives a group ID from the
team's name (FNV-1a, pushed into `0x40000000`-`0x7FFFFFFF` so it can never collide with the IDs
vanilla hands out to player-made groups, which count up from 1) and creates it through
`GroupManager.getOrAddGroup`. Bandits are put on one at spawn with
`PlayerQuests.ServerAssignToGroup`, players with `/banditteam join`.

Deriving the ID from the name rather than generating one is what makes a team durable: the same
name is the same group after a restart, and a player's group is saved with their character
(`PlayerQuests.save` writes it), so somebody who joined `red` last night is still red tonight and
lines up with a squad spawned onto red today.

Everything the game already does with groups therefore comes free:

- teammates see each other's names in green and appear on each other's map,
- with the vanilla default `Gameplay.Friendly_Fire = false`, teammates **cannot damage each other
  at all** - `DamageTool.isPlayerAllowedToDamagePlayer` blocks it before the plugin is involved,
- players can be on a team with no bot on the map at all, which is what makes this a server
  feature rather than a bandit one.

The targeting rule is one method, `BanditTeams.IsHostile`, shared by the on-foot scan, the
friendly-fire clearance and the vehicle turret:

1. same group is never a target - that covers two red bandits as much as it covers a red bandit and
   the player who joined red;
2. two bandits both on *no* team are one side, so a server that never configures teams keeps bots
   that ignore each other;
3. anyone on no team is fair game, unless `HostileToUngrouped` is off;
4. anything else is another side, and is a target.

Only friends block a shot. A rival team's bandit standing in the line of fire is a second target
rather than a reason to hold fire, and a driver will run one over while still steering around its
own - otherwise two sides would politely wait for clear lanes instead of fighting.

One thing worth knowing if you extend this: vanilla counts a group's members **up** in
`ServerAssignToGroup` and only ever counts them back **down** in `leaveGroup`, and a kicked player
does neither. Despawning bandits therefore leave their team explicitly (`FakePlayerSpawner.LeaveTeam`)
before the kick, or every spawn-and-clear cycle would leave the team a member heavier than it is,
write that into `Groups.dat`, and eventually make a server with `Max_Group_Members` refuse to let
anyone join a team nobody is on.
