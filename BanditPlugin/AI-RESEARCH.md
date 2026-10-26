# Soldier AI research (2026-10-24)

What other games and mods do for walking, shooting soldier NPCs, and what the bandits should take
from it. Gathered from three sources: the AI source code of open-source games and mods, Reddit
(player and modder discussion), and published industry write-ups (Game AI Pro, GDC). Where a
point was inferred rather than read in code or text, it says so.

## The plan, in order

### Phase 1 - cover that holds up against a moving enemy

The `moving-cover-circle` test fails because the cover finder rebuilds its candidates from scratch
on every search, from a probe ray per object. It misses spots a couple of metres away, such as the
far face of a wall, and it churns between spots while the enemy moves. Every shipped game read
does it differently:

1. **Cover points per object, tagged with the directions they protect from.** Worked out once,
   the first time a bandit looks for cover near an object, and cached until the object changes
   (a barricade destroyed). A query becomes a lookup by threat bearing, plus one or two confirming
   raycasts on the best few.
   - Mafia III: 1m cover slots with a 32-bit protection mask from short rays round each slot.
   - S.T.A.L.K.E.R.: high and low cover per nav vertex in 4 directions.
   - Reforger: each cover point has a facing, valid while the threat is within 60 degrees of it.
2. **Pessimistic and predictive.** A spot must protect against the threat's bearing a sector
   either side, and against where the threat will be in about 1.5-2s.
   - Killzone stores the farthest attack distance per sector, pessimistically, which it calls "a
     limited ability to anticipate threat movement".
   - Quake 3 pre-aims at the first point on the enemy's route the bot can see.
   - The 1.5-2s horizon is our own adaptation [inferred].
3. **Keep the spot; re-score it rather than searching again.**
   - Re-score the current spot cheaply several times a second.
   - Switch only if a new spot is clearly better (about 10-20%), and after a minimum time in the
     spot.
   - Leave when the threat has walked round it (Reforger's 60 degrees, then fall back to cover
     within 20m).
   - Never leave cover without a next spot to go to (Reforger).
   - If the destination stops working mid-run, stop and fight from where you are, because
     changing target mid-run "looked erratic" (Mafia III).
   - Sources: S.T.A.L.K.E.R. `update_best_cover_actuality`; SAIN (Tarkov) uses a 6s minimum, a
     10s cooldown, never moves while suppressed, and only moves to better cover.
4. **Don't run toward or past the enemy to reach cover.**
   - Source engine: never expand a node closer than 1.5x its distance to the threat.
   - S.T.A.L.K.E.R.: "threat on the way", within 30 degrees of the enemy direction.
   - Crysis: drop points the enemy can reach before you.
5. **Danger-marked spots.**
   - Hit while in a spot: avoid it for about 120s, recovering gradually; this is squad-wide
     (S.T.A.L.K.E.R.).
   - If the enemy has been aiming at you for 2s or more, the spot is no longer good (F.E.A.R.).
     That's cheap here, because players' look direction is known.

### Phase 2 - squad fire and manoeuvre

- **Attack slots.**
  - Only one or two bandits shoot at a time. The rest move or hold, aimed at the last known
    position. Whoever has a shot may take it (Source Combine, The Last of Us
    "OpportunisticShooter").
  - When cover has to change, one suppressor fires while everyone else moves (F.E.A.R.).
  - Alternatively, alternating halves: one suppresses while the other bounds, swapping every
    11-19s (LAMBS).
- **Flank the enemy's exposed side.**
  - Sweep round the enemy for the sector where it has least cover and take cover there
    (S.T.A.L.K.E.R.).
  - Fewer than half the squad flank at once.
  - This replaces the fixed 10m sidestep.
- **Suppression aimed where the enemy will shoot from.** Aim at its likely firing positions,
  scored from its point of view, not at the wall in front of it (Killzone, LAMBS).
- **Tactical point queries** as one engine for cover, advance, flank, retreat and search (Crysis
  2 TPS).
  - A query generates points, filters them cheaply, scores them, then raycasts from the best down.
  - "Directness" = (dist(me,goal) - dist(point,goal)) / dist(me,point). A value of at least 0.5
    is an advance, about 0 is a flank, at most -0.5 is a retreat.

### Phase 3 - fair perception and aim (the loudest player complaint)

The loudest complaint in every subreddit checked was about the AI cheating at perception and aim,
not about tactics: seeing through bushes, laser headshots at range, instant 180-degree turns.

- **Reaction delay, and aim that tightens with time on target** (Quake 3, Mafia III).
- **Accuracy multipliers:** worse at range, against moving targets, when the target reverses
  direction, and very close up.
- **Hit tokens** (Game AI Pro 3 ch. 33):
  - Each target has one "hit" token on a timer.
  - The delay is longer if the target is crouched, in cover or facing away, and shorter if it's
    close or running at the shooter.
  - Everyone else misses visibly: over the top of the cover, the ground at the peek side, past
    the head.
- **Vegetation and darkness block spotting.** Contact shared within the squad carries some
  position error.
- **Threat accumulator** (Reforger numbers):
  - Inputs: +0.10 per impact or fly-by, a little per gunshot by distance, explosions up to
    +0.2, +0.3 while bleeding.
  - It decays about 10% a second.
  - Levels: Safe / Vigilant (>0.05) / Alerted (>0.33) / Threatened (>0.66).
  - It drives stance, hide/expose timing and how long a bound lasts. Suppressed in cover: hide
    3.5-7s, show 1.7-2.5s.

### Phase 4 - morale, search, punishing mistakes

- **Morale.**
  - Strength comparison: ours plus enemies killed, against theirs plus friends lost (Days Gone,
    S.T.A.L.K.E.R.).
  - Worried: retreat in halves. Confident: advance in halves, then flank.
  - Players explicitly want enemies who run or surrender rather than fight to the last.
- **Search.**
  - Keep knowing the target's position for 2-3s after losing sight.
  - Check the last known position with 2-3 bandits at most.
  - Then search spots hidden from the estimated position, extrapolated along the target's
    heading.
  - Shared suspect positions, struck off when seen.
  - "Snuck away": if the target is more than 5m from where they think it is, send one bandit to
    look. This cut The Last of Us' combat loop from about 2 minutes to about 30s.
- **Rush when the target is vulnerable:** reloading, healing, badly hurt (SAIN). Players single
  this out as what makes bots feel smart.
- **Callouts.** "If the player didn't see it, it didn't happen." Barks like "flanking",
  "reloading" and "grenade!" make tactics readable (F.E.A.R.).
- **Grenades:** few, delayed, with a warning shout, never several on one player (Gray Zone
  Warfare 0.5 patch notes).

## Sources

### Code read
- **S.T.A.L.K.E.R.** - OpenXRay, github.com/OpenXRay/xray-16, `src/xrGame/`. Files:
  `cover_manager.cpp`, `cover_evaluators.cpp`, `ai/stalker/ai_stalker_cover.cpp`,
  `agent_location_manager.cpp`, `agent_member_manager.cpp`, `stalker_combat_actions.cpp`,
  `ai/stalker/ai_stalker_fire.cpp`. Covers:
  - static cover in 4 directions;
  - the scoring evaluator: penalty for walkable ground toward the enemy, weapon range bands,
    threat-on-the-way;
  - keeping the current cover with hysteresis;
  - danger locations;
  - squad claims within 5m;
  - the combat sequence TakeCover -> LookOut -> HoldPosition -> Detour -> Search;
  - morale.
- **Arma Reforger** - github.com/BohemiaInteractive/Arma-Reforger-Script-Diff,
  `scripts/Game/AI/`. Files: `SCR_AICombatMoveLogic_Attack.c`,
  `SCR_AICalculateCoverQueryProps_CombatMove.c`, `SCR_AIThreatSystem.c`,
  `SCR_AISectorThreatFilter.c`. Covers:
  - the cover cone query and the 60-degree facing test;
  - time-boxed bounds;
  - the threat accumulator.
- **Source SDK 2013** - `ai_tacticalservices.cpp`, `npc_combine.cpp`. **Half-Life 1** -
  `hgrunt.cpp`. Covers squad attack slots, the cover breadth-first search with the
  1.5x-distance rule, lateral cover, and flank positions.
- **F.E.A.R. SDK 1.08** - github.com/xfw5/Fear-SDK-1.08, `AIActivityAdvanceCover.cpp`,
  `AINodeValidators.cpp`, `AIActivitySearch.cpp`. Covers:
  - one suppressor while the others move;
  - node validators: threat aiming at me, damaged, path blocked, threat field of view;
  - two-man search.
- **LAMBS Danger** - github.com/nk3nny/LambsDanger, `addons/main/functions/`. Covers:
  - the cover finder;
  - hiding;
  - suppression target choice;
  - group flank with alternating teams;
  - group memory for building clearing;
  - weighted random tactic choice.
- **SAIN** (Tarkov SPT, C#), 4.1.0. Covers cover generated from colliders, the rules against
  churning, rushing a vulnerable target, and squad push/suppress. Its design rule: bots get no
  information a player couldn't have.
- **Quake 3** - `ai_dmq3.c` `BotAimAtEnemy`, `be_ai_move.c` `BotPredictVisiblePosition`. Covers
  the reaction delay, accuracy multipliers and predicting where the enemy reappears.
- **VCOM AI** - little of use: it relies on Arma's built-in cover.

### Published write-ups
- **Game AI Pro**, all free at gameaipro.com:
  - Crysis 2 tactical point system, GAP1 ch. 26
  - Killzone 3 bots, GAP1 ch. 29
  - tactical pathfinding, GAP1 ch. 27
  - The Last of Us, GAP2 ch. 34
  - search, GAP2 ch. 27
  - accuracy, GAP3 ch. 33
  - Mafia III, GAP Online 2021 ch. 16
  - Days Gone, GAP Online 2021 ch. 12
  - spatial query hysteresis, GAP Online 2021 ch. 5
- **Killzone, GDC Europe 2005**: guerrilla-games.com/media/News/Files/gdce05_killzone_ai.pdf
- **F.E.A.R., GDC 2006** (Orkin): pages.cs.wisc.edu/~dyer/cs540/handouts/gdc2006_orkin_jeff_fear.pdf
- Halo 2 and van der Sterren's terrain reasoning were read through summaries only.

### Reddit
Threads are opening posts only, because comment threads couldn't be fetched. Treat them as
player opinion.
- **Perception cheating:**
  - r/GrayZoneWarfare 1xdoq3t, 1s7zqlb
  - r/EscapefromTarkov 9vgt5m, 1i3iln6
  - r/arma o9j1n5
- **What reads as smart:**
  - r/Marathon 1rnl9dj
  - r/truegaming kgyayf, 4au22v
  - r/groundbranch 1uzprw9
  - r/arma 5lnf8n
- **Developer posts:**
  - Gray Zone Warfare 0.5 AI fixes: r/GrayZoneWarfare 1xbisev
  - HAIO: r/arma 1ujjsrz
  - TEKILLA stress system: r/arma 1xb99y2
