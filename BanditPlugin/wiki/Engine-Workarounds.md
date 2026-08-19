# Non-obvious things this had to work around

Each of these was found by decompiling the server's real `Assembly-CSharp.dll`, and each one
produced a silent failure rather than an error:

1. **The server does not raycast bullets.** `UseableGun.ballistics()` applies damage from hit
   reports the *owning client* sends (`PlayerInput.sendRaycast(info, ERaycastInfoUsage.Gun)`);
   with no client, `if (!player.input.hasInputs()) break` discards every bullet. Symptom: the bot
   fires convincingly and damages nothing at all - not players, not trees. The controller now
   raycasts itself and injects an `InputInfo` into the packet's `serversideInputs`.
2. **Firing before the gun is equipped throws punches.** `PlayerEquipment.simulate` routes primary
   attacks to `simulate_PunchInput` while there's no valid useable, and ignores input until
   `IsEquipAnimationFinished`. Attack input is gated on both.
3. **`ServerEquip()` silently no-ops** if the player is momentarily `life.isDead` / `!canEquip`
   right after spawn - so some bots stood around holding nothing. It's now retried until it takes.
4. **`startPrimary()` refuses to fire while firemode is SAFETY.** Firemode lives in
   `equipment.state[11]` and ammo in `state[10]`, both read by `UseableGun.equip()`, so they must
   be set on the item *before* it is equipped.
5. **`analog = 0` means "walk backwards"**, not "stand still". Input is decoded as
   `((analog >> 4) & 0xF) - 1`, so neutral is `0x11`.
6. **`SteamPlayerID` overloads `operator==` without a null guard** - it dereferences both sides. A
   plain `steamPlayer.playerID == null` check throws `NullReferenceException`.
7. **A nested type that is `public` inside an `internal` type reports `IsPublic == false`.**
   `NetMessages.ClientWriteHandler` must be looked up with `BindingFlags.Public | NonPublic`;
   `NonPublic` alone silently returns null.
8. **Bots are kicked after ~30s** by `Provider.KickClientsWithBadConnection` unless
   `SteamPlayer.timeLastPacketWasReceivedFromClient` is refreshed, since they never receive packets.
9. **RocketMod's global event handlers assume every player is one it set up.** Rocket subscribes to
   statics like `PlayerLife.OnTellHealth_Global`, which fire for *every* player, then opens each
   handler with an unguarded `GetComponent<UnturnedPlayerEvents>()`. Rocket only attaches that
   component from its `Provider.onServerConnected` hook, which a bot never triggers - so every time
   a bot was shot, starved or grew thirsty the log filled with `NullReferenceException`, and the
   throw unwound back through `PlayerLife.doDamage` into `UseableGun.ballistics`, skipping whatever
   vanilla did after telling health. Fixed by attaching the same three components Rocket does
   (`UnturnedPlayerFeatures`, `UnturnedPlayerMovement`, `UnturnedPlayerEvents`). Note this also
   makes a bot spawn raise Rocket's `OnPlayerConnected` for other plugins.
