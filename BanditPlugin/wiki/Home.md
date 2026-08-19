# BanditPlugin

A RocketMod plugin for Unturned that spawns AI "bandit" bots: they appear as real player
characters, hold an Eaglefire, continuously turn to face the nearest player, and shoot at them.

The project started life as a zombie-based experiment, but bots are **fake players**, not
zombies. See [Why not zombies?](Why-Not-Zombies).

## Contents

**Using it**

- **[Commands](Commands)**
- **[Configuration](Configuration)**
- **[Build & install](Build-and-Install)**

**How it works**

- **[Architecture](Architecture)**
- **[Movement, cover & patrol](Movement-Cover-and-Patrol)**
- **[Vehicles](Vehicles)**
- **[Turrets](Turrets)**
- **[Convoys](Convoys)**
- **[Teams](Teams)**

**Background**

- **[Engine workarounds](Engine-Workarounds)**
- **[Why not zombies?](Why-Not-Zombies)**
- **[Known limitations](Known-Limitations)**

## Credits

Approach informed by [EvolutionPlugins/Dummy](https://github.com/EvolutionPlugins/Dummy) (MIT,
© 2022 DiFFoZ) - specifically driving bots via `serversidePackets` input packets and the
`timeLastPacketWasReceivedFromClient` keep-alive. No code was copied; that project has no weapon
support (`// todo: simulate useable`), so the equipping and firing here is original.
