# BanditPlugin

A plugin I made for Unturned. It spawns AI enemies ("bandits") - they can shoot, take cover,
drive vehicles, man turrets, patrol, run convoys and much more.

Bandits are **fake players**, not zombies: they appear as real player characters, carry real
weapons and are driven by the same input packets a client sends. That is what lets them do
everything a player can. They do not consume player slots - a server full of bandits still lets
real players in - though they are visible in the in-game player list, which is drawn client-side
and cannot be changed from a server plugin. See
[Why not zombies?](https://github.com/peterferenc/BanditPlugin/wiki/Why-Not-Zombies).

Built as a RocketMod plugin, developed against Unturned 3.26.3.8 with RocketModFix 4.23.1.

## Features

**Combat**

- Spawn bandits one at a time (`/bandit`) or as a whole squad (`/squadspawn`).
- They acquire and shoot players and other bandits with real weapons - rifles, machineguns,
  marksman rifles, shotguns - with configurable accuracy that holds steady with range.
- They take cover automatically: cover is scored against the threat and their preferred
  engagement range, and they sprint to it when it is worth the run.
- They peek out of cover on a duck-and-pop cycle to get shots off, and duck back.
- Line of sight is respected - they will not target or shoot through walls, rocks or vehicles.

**Kits and squads**

- Four behaviour kits out of the box - `mg`, `rifleman`, `marksman`, `breacher` - each with its
  own weapon, acquire and fire ranges, accuracy and stance behaviour. Fully configurable, and you
  can add your own.
- Squad types (`basic`, `rifle`, `sniper` by default) name the kits they are built from, their
  spacing, spawn distance and group behaviour.
- Automatic cost calculation: every kit and vehicle is priced relative to a rifleman, so
  `/banditevent <points>` buys a whole fight - squads and crewed vehicles - against a budget.
  `/banditcost` prices everything from the game's own asset data. See
  [Points and cost](https://github.com/peterferenc/BanditPlugin/wiki/Points-and-Cost).

**Teams**

- Squads and players can be assigned to teams, which are real in-game groups. Bandits never shoot
  their own team and always shoot the others, so **bandit armies can fight each other**, or fight
  teams of players.
- `HostileToUngrouped` decides whether someone on no team is everyone's enemy, so you can walk
  through a bot war until you pick a side.

**Vehicles**

- Bandits can drive vehicles, and be sent to a destination by road or straight line.
- They man vehicle turrets and engage from them.
- They dismount when the fight arrives: transports unload their riders at the dismount range,
  armed vehicles hold and fight while the men in the back get out.
- Crews are part of the vehicle type, so a spawned vehicle comes with the men to fill it.

**Movement**

- Pathfinding through the map's own A* graph inside Nav volumes, direct steering outside them.
- Patrol routes recorded per map (`/banditwp`), with a fallback to the map's location nodes.
- Convoys: a column of crewed vehicles that drives a route, holds spacing, stops on contact,
  dismounts, fights, remounts and carries on.

## Quick start

```bash
# 1. Point the build at your server's assemblies
cp BanditPlugin/Directory.Build.props.example BanditPlugin/Directory.Build.props
$EDITOR BanditPlugin/Directory.Build.props   # UnturnedManagedPath, RocketModPath

# 2. Build
dotnet build BanditPlugin/BanditPlugin.csproj -c Release

# 3. Install - both files, they go in different folders
cp BanditPlugin/bin/Release/BanditPlugin.dll <server>/Rocket/Plugins/
cp BanditPlugin/bin/Release/0Harmony.dll     <server>/Rocket/Libraries/
```

`0Harmony.dll` is what keeps bandits from counting against the server's player slots. Without it
the plugin still loads and everything else works, but a server with more bandits than
`Max_Players` turns real players away as full - it logs an error saying so.

Start the server once to generate `BanditPlugin.configuration.xml`, then grant `bandit.spawn`
(and `bandit.team`) to a group in `Rocket/Permissions.config.xml`.

Then, in game:

```
/bandit                  spawns one where you are looking - it holds fire until told
/bandit shoot start      weapons free, for every bandit on the map
/bandit cover start      and they start using cover
/squadspawn rifle        spawns a squad, already fighting
/banditevent 200         spawns a whole fight bought for 200 points
/banditstatus            reports what every bandit is currently doing
/banditclear             removes them all
```

A bandit from `/bandit` does nothing until ordered, so you can switch on one behaviour at a time
and watch it. A squad, an event or a convoy comes out fighting.

Use `/banditclear` before disconnecting - bots occupy player slots, and their presence affects
the client-side exit timer.

## Documentation

The **[wiki](https://github.com/peterferenc/BanditPlugin/wiki)** is the detailed documentation -
every command, every configuration key, and how each subsystem actually works:

- [Commands](https://github.com/peterferenc/BanditPlugin/wiki/Commands)
- [Configuration](https://github.com/peterferenc/BanditPlugin/wiki/Configuration)
- [Points & cost](https://github.com/peterferenc/BanditPlugin/wiki/Points-and-Cost)
- [Build & install](https://github.com/peterferenc/BanditPlugin/wiki/Build-and-Install)
- [Architecture](https://github.com/peterferenc/BanditPlugin/wiki/Architecture)
- [Movement, cover & patrol](https://github.com/peterferenc/BanditPlugin/wiki/Movement-Cover-and-Patrol)
- [Vehicles](https://github.com/peterferenc/BanditPlugin/wiki/Vehicles) ·
  [Turrets](https://github.com/peterferenc/BanditPlugin/wiki/Turrets) ·
  [Convoys](https://github.com/peterferenc/BanditPlugin/wiki/Convoys)
- [Teams](https://github.com/peterferenc/BanditPlugin/wiki/Teams)
- [Engine workarounds](https://github.com/peterferenc/BanditPlugin/wiki/Engine-Workarounds) ·
  [Why not zombies?](https://github.com/peterferenc/BanditPlugin/wiki/Why-Not-Zombies) ·
  [Known limitations](https://github.com/peterferenc/BanditPlugin/wiki/Known-Limitations)

The wiki pages live in [BanditPlugin/wiki/](BanditPlugin/wiki/) and are pushed with
[wiki/publish.sh](BanditPlugin/wiki/publish.sh).

## Repository layout

| Path | What's in it |
|---|---|
| [BanditPlugin/](BanditPlugin/) | The plugin source, and its full [README](BanditPlugin/README.md). |
| [BanditPlugin/FakePlayer/](BanditPlugin/FakePlayer/) | Spawning the fake players, the bot brain and controller, squads, convoys, vehicles. |
| [BanditPlugin/Navigation/](BanditPlugin/Navigation/) | A* pathing, cover finding, road graph, waypoints. |
| [BanditPlugin/Commands/](BanditPlugin/Commands/) | Every chat command. |
| [BanditPlugin/wiki/](BanditPlugin/wiki/) | Wiki pages, published to the GitHub wiki. |
| [tools/](tools/) | Performance sampling script and captured measurements. |

## Known limitations

Effective shooting range is about 50m (a server-side ballistics check), line of sight is
eye-to-eye, and movement is quantised to 8 directions because that is all the input packet
carries. Bots consume player slots and show up in the player list. The full list, with the
reasons, is on the
[Known limitations](https://github.com/peterferenc/BanditPlugin/wiki/Known-Limitations) page.

## AI disclosure

This plugin was written with the help of AI coding assistants. The design decisions, the testing
against a live server and the final say on what went in are mine, but a good deal of the code and
of this documentation was AI-generated. Read it with that in mind, and please report anything
that looks wrong.

## Credits

Approach informed by [EvolutionPlugins/Dummy](https://github.com/EvolutionPlugins/Dummy) (MIT,
© 2022 DiFFoZ) - specifically driving bots via `serversidePackets` input packets and the
`timeLastPacketWasReceivedFromClient` keep-alive. No code was copied; that project has no weapon
support (`// todo: simulate useable`), so the equipping and firing here is original.

## License

[MIT](LICENSE).

## Disclaimer

This plugin does not modify or increase player count for servers. It is not intended for
"botting" or "server boosting". The plugin adheres to the
[Steam Online Conduct](https://store.steampowered.com/online_conduct/) and the
[Unturned Server Hosting Rules](https://docs.smartlydressedgames.com/en/stable/servers/server-hosting-rules.html).
