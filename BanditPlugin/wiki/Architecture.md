# How it works

Four pieces:

- **`FakeTransportConnection`** - a no-op `ITransportConnection`, so `Provider.addPlayer` has a
  non-null connection to register for a client that doesn't exist.
- **`FakePlayerSpawner`** - reflects into `Provider.ClaimNetIdBlockForNewPlayer` / `addPlayer` /
  `Player.InitializePlayer` / `SendInitialPlayerState`, mirroring the sequence `Provider` uses for
  a real join. Then gives the bot an Eaglefire and attaches the controller.
- **`PlayerJoinBroadcaster`** - sends the `PlayerConnected` message and initial player state to
  every already-connected client. **`addPlayer` alone does no networking at all** (it only fires a
  local C# event), so without this the bot exists server-side but nobody's client renders it.
- **`BanditBotController`** - each tick, builds a `WalkingPlayerInputPacket` and enqueues it on the
  bot's own `PlayerInput.serversidePackets`. Vanilla `PlayerInput` then drives everything -
  `look.simulate` (rotation), `movement.simulate`, `equipment.simulate` (trigger), the animator,
  and the `clock`/`tock` cadence that paces firing.

and, for movement:

- **`BanditBrain`** - the state machine (Idle / Travel / Investigate / Engage / TakeCover). It
  publishes a desired *world* direction plus stance flags and never touches packets itself.

  **Combat does not steer the feet.** Taking cover is the single tactical override; everything
  else a bandit does while fighting - aiming, firing, tracking - happens in the controller and
  leaves movement alone. So a patrol or a `/banditgoto` keeps running through a firefight instead
  of being suspended by it. Closing on a target and investigating a last known position are both
  opt-in config, off by default, because each one makes the bot abandon what it was told to do.
- **`BanditNavigator`** - turns "go here" into a direction, via A* where there is a navmesh and
  whisker steering where there isn't.
- **`BanditPathFollower`** - one A* route, from asking for it to walking off the end of it. Shared
  by the on-foot navigator and the vehicle one, which need the same asynchronous request, the same
  guard against a stale path landing after the destination changed, and the same corner-following -
  and differ only in how far off the navmesh they will snap.
- **`BanditCoverFinder`** - generates and scores positions that break line of sight from a threat.
- **`BanditWaypointStore`** - per-map patrol routes in `Waypoints/<map>.txt`.

and, for convoys:

- **`BanditRoadGraph`** - the map's roads sampled into a routable network, since the game ships the
  splines but no connectivity between them.
- **`BanditConvoyRoute`** - per-map convoy routes in `Waypoints/<map>.convoy.txt`, kept apart from
  the patrol route because the two are nothing like each other.
- **`BanditConvoy`** - the column: which route point each vehicle drives at next, how fast, and what
  happens when it is shot at.
