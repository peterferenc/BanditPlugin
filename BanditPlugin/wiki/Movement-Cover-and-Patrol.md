# Movement, cover and patrol

## Movement is two more bytes on the packet

No positions are ever written. The server decodes movement out of the same packet the bot was
already sending and simulates it with the real `CharacterController`:

```csharp
int input_x = ((analog >> 4) & 0xF) - 1;   // -1 / 0 / 1
int input_y = (analog & 0xF) - 1;
player.movement.simulate(sim, recov, input_x, input_y, 0f, 0f, keys[0] /*jump*/, keys[5] /*sprint*/, RATE);
```

so gravity, slopes (`Max_Walkable_Slope`), step-up, stairs, per-surface friction, stamina, water
and region updates all come free, and position replicates to other clients exactly as a real
player's does. `keys` is a bitfield of `1 << index`: bit0 jump, bit3 crouch, bit5 sprint.

**Movement is body-relative, and the body yaw *is* the aim yaw.** `PlayerLook.simulate` assigns
`transform.localRotation` from the packet's yaw (both components live on the player's root
GameObject), and `PlayerMovement` then does `transform.rotation * move.normalized * speed`. So a
desired world direction has to be un-rotated by the yaw *that same packet carries* and quantised
onto the eight compass points. The upshot is that an engaged bandit strafes and backpedals with
its gun still on the target, and only turns its body when it has nobody to shoot at.

## Pathfinding: the server is already running A*

Unturned ships `AstarPathfindingProject.dll`, and `UnturnedPathfinding_ASPFP` creates a live
`AstarPath` singleton with one `RecastGraph` per Nav volume - that's what zombies path on
(`Seeker` + `FunnelModifier` + `LegacyAIPathNoRedist`). The navigator borrows the Seeker and the
funnel modifier but **not** the AIPath: AIPath drives `transform` directly, which would fight
`PlayerMovement` and desync the position other clients see. Only the corner list is used.

The navmesh only exists *inside* Nav volumes, i.e. the towns where zombies spawn, so a bandit on
the roads between them is off-mesh most of the time. Each repath checks whether both endpoints
snap onto a graph (within `NavmeshSnapDistance`, because AstarPath's own
`maxNearestNodeDistance` is 100m and would happily snap onto the next town's graph); if not, the
bot steers directly, with capsule-cast whiskers fanning out to either side of a blocked path,
a jump for obstacles that are only blocking low down, and vanilla-zombie-style stuck detection
that sidesteps and eventually gives up so the brain can pick another goal.

## Cover

Candidates come from the landmarks actually near the bot - trees, rocks, vehicles, walls,
barricades - plus a blind ring of samples for terrain folds and building corners no collider
points at.

For each landmark the spot is found by **raycasting from the threat's eye at the landmark** and
standing just behind wherever that ray first hits. The obvious alternative - `bounds.center` plus
the bounding box's half-extent - is wrong in exactly the case cover most obviously has to handle:
a tree's bounds are its *canopy*, three to five metres across, so the spot lands that far behind a
trunk 0.3m wide, where the trunk no longer occludes anything. Every tree then fails the
"still visible when crouched" test and is discarded, and the bot stands in the open having found
"no cover" with a tree right in front of it. Each survivor is scored on the same `BLOCK_SENTRY`
visibility test the bot's own line of sight uses, so "in cover" means exactly "can't be shot":

| Result | Meaning |
|---|---|
| visible when crouched | not cover, rejected |
| hidden crouched, visible standing | **crouch cover** - the prize: duck to be safe, stand to shoot |
| hidden either way | hard cover - safe but silent, unless a lateral step gives a firing angle |

The bot's current position is scored with the same function and used as the bar to beat, so one
already tucked behind a rock doesn't keep sprinting between equally good rocks. A hurt bandit
flips the weighting and hides properly instead of looking for a firing position.

Once there it alternates hiding and exposing itself (`CoverHideSeconds` / `CoverPeekSeconds`) -
crouching and standing in crouch cover, stepping out to the verified flank in hard cover. Note the
bot **keeps fighting while it has no visible target**: crouching is what breaks line of sight, and
the controller only acquires players it can see, so cover runs on memory for a few seconds rather
than the bot standing up and wandering off mid-firefight.

Getting shot by someone it never saw is handled through `DamageTool.damagePlayerRequested`, whose
`direction` points along the bullet - so the bot knows roughly where to hide from, and where to go
looking afterwards.

## Patrol

`/banditwp add` records waypoints at your feet into `Waypoints/<map>.txt` (plain `x y z` lines,
hand-editable). With no recorded route, patrol falls back to the map's `LocationDevkitNode`s - the
named places official maps mark in the editor. Bandits start at whichever waypoint is nearest,
loiter on arrival, and break off to fight anything they see on the way, resuming afterwards.
