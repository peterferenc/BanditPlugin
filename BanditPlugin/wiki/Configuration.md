# Configuration

| Key | Default | Notes |
|---|---|---|
| `GunAssetGuid` | Eaglefire's GUID | Resolved by GUID because `Assets.find(EAssetType.ITEM, name)` is case-sensitive and the asset is `Eaglefire`, not `EagleFire`. |
| `GunAssetLegacyId` | `4` | Fallback if the GUID doesn't resolve. |
| `MagazineCapacity` | `30` | Also the refill amount for `InfiniteAmmo`. |
| `FireIntervalSeconds` | `0.6` | One trigger pull per interval. |
| `AimToleranceDegrees` | `10` | Won't fire until aimed this close. |
| `FireRange` | `50` | See [Known limitations](Known-Limitations). |
| `AimHitChance` | `0.3` | Roughly the fraction of shots that land. `1` restores perfect aim. |
| `AimTargetRadius` / `AimTargetHalfHeight` | `0.35` / `0.8` | Ellipse standing in for the player's hitboxes, used to solve for the aim error. |
| `AimMaxErrorDegrees` | `8` | Caps the error, so the bot doesn't flail at point-blank range. |
| `AimWobbleIntervalSeconds` | `0.35` | How often the aim re-drifts between shots. |
| `AimWobbleSmoothingSeconds` | `0.15` | Larger is a lazier sway; `0` snaps. |
| `RequireLineOfSight` | `true` | Bot won't target or shoot through walls, rocks or vehicles. |
| `MovementEnabled` | `true` | Master switch. Off makes bandits the stationary turrets they used to be. |
| `ArriveRadius` | `2` | How close counts as arrived. |
| `RepathIntervalSeconds` | `2.5` | How often a moving bot asks A* for a fresh path. |
| `NavmeshSnapDistance` | `3` | Beyond this from the navmesh the bot steers directly instead of pathing. |
| `AllowSprint` / `AllowJumping` | `true` | Vanilla still gates sprint on stamina and aiming. |
| `AdvanceOnTarget` | `false` | Walk toward a target further away than `PreferredEngagementRange`. A chase behaviour - off by default, and only applies when the bot has no order or patrol. |
| `InvestigateEnabled` | `false` | After losing contact, go and look at the last known position. Also off by default: it makes the bot leave its post. |
| `DespawnSecondsAfterDeath` | `5` | How long a killed bandit lies there before removal. Negative leaves the body for `/banditclear`. |
| `PreferredEngagementRange` | `25` | Range the bot fights at, and scores cover against. |
| `CoverEnabled` | `true` | Seek cover when seen or shot at. |
| `CoverSearchRadius` | `18` | Radius searched for cover. |
| `CoverSearchIntervalSeconds` | `3` | Minimum gap between searches - each is a burst of raycasts. |
| `CoverRingSamples` | `12` | Blind samples per ring, on top of nearby colliders. |
| `CoverMinimumThreatDistance` | `8` | Cover nearer the threat than this is ignored. |
| `CoverHideSeconds` / `CoverPeekSeconds` | `2.5` / `2` | The duck-and-pop cycle. |
| `Teams` | `bandits`, `red`, `blue` | The sides bandits and players can fight on. Each is a real in-game group; `DisplayName` is what shows in game and prefixes the bandits' names. |
| `DefaultTeam` | `bandits` | Team a bandit joins when neither its squad type nor the command names one. A name matching no team leaves bandits ungrouped, exactly as they were before teams existed. |
| `HostileToUngrouped` | `true` | Whether someone on no team is a target for every bandit. Off makes bandits fight only rival teams, so you can walk through a bot war until you pick a side. |
| `PatrolByDefault` | `false` | Start newly spawned bandits patrolling. |
| `PatrolWaypointDwellSeconds` | `3` | Loiter time at each waypoint. |
| `PatrolLoop` | `true` | Return to the first waypoint after the last. |
| `PatrolUseLocationNodesWhenNoWaypoints` | `true` | Fall back to the map's location nodes. |
| `ConvoyVehicleCap` | `6` | Most vehicles one convoy may draw. Separate from `EventVehicleCap`, which exists to keep an ordinary event's budget going mostly into men - a convoy is nothing but vehicles. |
| `ConvoySpacing` | `20` | Metres between vehicles at spawn, and the interval they hold on the move. |

## Accuracy

The bot's aim error is drawn in **metres at the target's range** and then converted to an angle, so
the hit rate holds steady with distance instead of the bot being lethal up close and hopeless far
out. Each axis' standard deviation is scaled by that axis' half-extent, which puts the miss (in
target-widths) on a circular unit Gaussian, so `P(hit) = 1 - exp(-1 / 2s²)` and `AimHitChance` is
met at `s = 1 / sqrt(-2 ln(1 - p))`. A fresh sample is drawn the instant the trigger is pulled, so
shots are independent; the drift between shots is just cosmetic sway.

Measured against the ellipse, that lands on 30% from about 10m out. Closer in, the
`AimMaxErrorDegrees` clamp takes over and the bot gets deadlier - ~43% at 5m, ~54% at 3m.
