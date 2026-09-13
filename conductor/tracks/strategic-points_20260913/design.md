# Design: Strategic points (resource sites, control points, base income)

**Date**: 2026-09-13 · **Track**: strategic-points_20260913 · **Approved by user**: 2026-09-13

First of four tracks that replace "mine spam next to base, convoy rush down one road" with a
map-driven economy and take-and-hold tactics. Later tracks (platoons with objectives, truck
logistics, air/naval support tasking) all read the point list this track creates.

## Problem

- The enemy commander sites mines 150–350 m from any building it owns
  (`Economy/CommanderEconomyServiceEnemy.cs`, `TryPickEnemyBuildSite`), so mines cluster at the
  base. A mine pays for itself in 12.5 min and then stacks upgrades; nothing on the map is worth
  fighting over except airbases.
- The mod never routes attacking units. Bought vehicles are handed to the game's convoy AI, which
  drives at the nearest known enemy along the road network (`Ai/CommanderEnemyCommanderService.cs`
  class remarks). Hence one stream down one road.
- The AI's decisions are visible only in `BepInEx\LogOutput.log`, and the BepInEx console is off in
  the developer's config.

## Decisions taken (user, 2026-09-13)

1. Variety is **map-driven**: geography (resource sites, villages, hilltops, bases) sets the
   options. Personalities and deeper adaptation are not in scope.
2. Resource sites = **existing industrial buildings + generated fill** so every map has a spread.
3. A site pays only when someone has **built a mine on it** ("build on it, defend it").
4. Territory income = **per base held** plus **small income from civilian infrastructure** held by
   presence, to reward platoon-level take-and-hold.
5. Control points pay **only while garrisoned**. Nobody present, or fewer than the minimum
   vehicles, = neutral, pays nobody. Driving through takes nothing.
6. Offensive doctrine (platoons + air/naval support) and truck logistics are later tracks.
7. The user needs to **see what the AI is thinking**: an in-game log window.

## Section 1 — Discovering points (once per mission, host only)

Runs once the strategic height map is ready (`Terrain/CommanderStrategicHeightMap.cs`, 20 m per
pixel). Produces one list of `StrategicPoint { Kind, Position, Radius, Owner, IncomePerMinute,
Garrison counts }`.

- **Resource sites**: every building whose `BuildingDefinition.buildingType` is industry (FAC) or
  storage/refinery class becomes a site. Fill: an ~8 km grid over the map; any cell without a site
  and with buildable flat ground gets one generated site (flat, off-road, near a road, via the
  existing `CommanderBuildPreview.IsSiteAllowed` rule with the base-radius check disabled).
  Minimum spacing ~2 km between sites; newer one dropped on conflict.
- **Villages**: civilian buildings (CIV) clustered by proximity (≤ 400 m; was 300). Cluster of ≥ 3 (was 4) → control
  point at the centroid, radius 400 m.
- **Hilltops**: sample the height map on a 1 km grid; a sample that is the highest within 1.5 km and
  ≥ 8 m (was 60, then 30, then 15) above the mean of that ring — and the sample is climbed to the actual summit first (steepest ascent on the height map) → control point, radius 300 m. Thresholds lowered 2026-09-13 after the duel map yielded 0 villages and 0 hilltops at the originals, then again 2026-09-13 alongside the three kinds below. Skip inside an airbase or within
  1 km of a village.
- **Outposts** (added 2026-09-13): a civilian cluster too small to qualify as a village (1 or 2
  buildings at the default `VillageMinBuildings = 3`) becomes an outpost at the cluster's centroid
  instead of being dropped, radius 300 m — a farmstead or hamlet, still worth a platoon's time on
  otherwise empty farmland.
- **Crossroads** (added 2026-09-13): built from the level's road network
  (`NetworkSceneSingleton<LevelInfo>.roadNetwork`, never the sea-lane network). Every road's two
  endpoints merge into a junction node within `RoadJunctionMergeMeters` (60 m); a node's degree is
  the number of road endpoints merged into it plus the number of other roads whose segment passes
  within the same distance without ending there. Degree ≥ `CrossroadsMinRoads` (3) → control point
  at the node, radius 300 m.
- **Roadside** (added 2026-09-13): walking each road (≥ 2 points; skips the sea-lane network), a
  point every `RoadsideSpacingMeters` (6 km) of arc length along it becomes a candidate, radius
  250 m — skipped within 1 km of a crossroads candidate (already the point of interest there) or
  inside an airbase.
- **Bases**: every airbase, kind Base. Ownership read live from the game, never stored.
- **Caps**: ≤ 30 resource sites and, separately, ≤ 120 control points (villages, hilltops, outposts,
  crossroads, roadside points; raised from 60 to 120 on 2026-09-13 when three more kinds joined the
  same allowance) per map — one shared cap let the sites starve the control points (found
  2026-09-13); no two within 800 m (was 1500 m, lowered 2026-09-13 for the same reason as the cap);
  none within 2 km of an airbase centre. Selection runs as three staged, spacing-aware passes
  sharing the one cap: villages + crossroads + outposts, then hilltops (seeded with stage one's
  survivors), then roadside points (seeded with both). Logged once as a table (kind, position,
  distance to nearest base).
- All numbers are config defaults in a new `Points` section of `CommanderSettings`.

## Section 2 — Owning and earning

- **Resource sites** are owned by the mine on them. Mines can be built **only at a site**: the
  player's ghost snaps to the nearest free site within reach and refuses elsewhere; the AI's mine
  step picks the nearest free reachable site. Build reach for mines = within `BuildRadiusKm` of a
  held base **or a currently garrisoned control point**. Mine destroyed → site free. Mine income and
  upgrades unchanged (`GoldMineIncomePerMinute × level`).
- **Control points** are held by presence. Every 5 s (scaled clock) count ground vehicles inside the
  ring per faction. One faction alone with ≥ `MinGarrison` (default 2) for `HoldSeconds` (default 60,
  cumulative, reset to zero when the garrison next arrives after a lapse) → held by that faction.
  Held pays each income tick **only while the condition still holds**. Below minimum or empty →
  neutral on the next check. Both factions present → contested, frozen, pays nobody. Aircraft do not
  count; any ground vehicle (including mobile AA) does.
- **Income tick** shares the mines' 15 s scheduler tick in `CommanderEconomyService.PayIncome`.
  Defaults per minute: base 30, village 10, hilltop 5. Kill bounties (`killReward`) untouched.
- Server only (`hq.IsServer`), same as mine income. Points reset on scene change; owners are not
  persisted across a mission reload (same stance as mine levels).

## Section 3 — What the AI does with points (this track)

Two existing decisions change; nothing else in the brain.

- **Build step**: in `GetEnemyBuildReserve` / `TryBuildEnemyEconomy`, "want a mine" = "want a mine at
  the nearest free site within reach." No reachable site → skip the mine target this review and
  save for the next item, instead of piling mines at home. Mine targets (2, duel 4) unchanged.
- **Garrison step**: new partial `Ai/CommanderEnemyCommanderGarrison.cs` on the defence clock
  (10 s). For each control point within ~12 km of a held base, nearest first, want `MinGarrison + 1`
  idle ground vehicles; recruit the way the home guard does (never a player-ordered unit —
  `CommanderMoveService.HasPlayerOrder` — never a home-guard or recon vehicle), drive them to the
  ring, leave them. Wiped garrison refilled next review if spare. Cap 3 garrisoned points per
  commander so the home guard is never starved.
- Player-side AI gets both for free (same code, gated by `CommanderPlayerCommanderService`).
- Enemy attackers still use the game's convoy AI in this track (interim; platoons are track 2).

## Section 4 — Seeing it

- **COMMANDER LOG window**: new CMD panel button beside ORDER OF BATTLE. One tab per faction (yours,
  your AI if on, each enemy). Header: funds, income/min by source (bases, villages, hilltops,
  mines), current plan, reserve target. Body: last 200 decisions with game-time stamps. Mechanism:
  one `CommanderAiLog.Note(hq, text)` that every existing AI log line flows through (extending the
  `CommanderLabel` helper from the player-commander track), writing to the BepInEx log as now and to
  a per-faction ring buffer. No second set of strings.
- **Points on the tactical map and in the world**: a filled dot per point (shapes were dropped 2026-09-13: rotated bars skewed under the UI-scale matrix); kind is in the label. Originally diamond = site, square = village, triangle =
  hilltop; colour by owner; striped when contested; label shows garrison `1/2` or mine level.
  Reuses `Units/CommanderMarkerView` / `CommanderWorldMarkerRenderer` / `Map/CommanderMapRouteRenderer`
  patterns. Clicking a point selects it like a building and shows the readout in the selection bar.
- **Settings > Gameplay** gains a Points block: minimum garrison, hold seconds, four income rates
  (sliders). Discovery spacing numbers are config-file-only.
- **BepInEx console** switched on in the developer's `BepInEx.cfg` during verification.

## Out of scope (later tracks)

Platoon composition and objectives; approach/route choice; air and naval support tasking; truck
logistics with money-on-arrival; personalities; persistence of point ownership across reloads.

## Verification

Self-checks: discovery spacing invariants on a synthetic point list; hold state machine (neutral →
held after HoldSeconds, → neutral on lapse, contested freezes, minimum garrison respected); income
table sums. In game (host, Ground Control Duel and one stock Escalation map): points appear on the
map with expected kinds; a mine cannot be placed off-site; a two-vehicle platoon takes a village
after 60 s and income rises; driving one vehicle away drops it to neutral; the AI builds its first
mine on a site rather than at its base; the COMMANDER LOG shows the AI's purchases and garrison
orders live.
