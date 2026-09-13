# Backlog: Platoons with objectives (track 2 of 4)

**Status**: not yet designed. Captured 2026-09-13 from the AI-behaviour brainstorm. Depends on
`strategic-points_20260913` (the point list is the objective list).

## What the user asked for (verbatim intent)

- Replace "buy vehicles, let the game's convoy AI drive them at the nearest enemy down one road".
- The AI groups bought vehicles into fixed **platoons** (example given: 2 tanks, 2 APCs, 2 AA
  vehicles). Each platoon gets **one objective**: a control point, a resource site, or an enemy
  base. It drives there via a chosen approach, sits, and holds.
- The mod routes them; the game's convoy AI is bypassed for commanded platoons.
- Same brain for the player-side AI commander.
- Focus on small **take-and-hold** tactics: a platoon going out to sit on a hilltop or in a village.
- Approaches should not be road-only: weight hilltops with sight lines, flanking routes, not just
  the highway.

## Reuse to read first

`Ai/CommanderEnemyCommanderDefence.cs` (home guard recruit/pin pattern), the garrison partial
added by the points track, `Units/CommanderMoveService.cs` (route issuing, formations,
`HasPlayerOrder`), `Terrain/CommanderTerrainFlightPlanner.cs` and the strategic height map for
approach scoring, `Ai/CommanderCaptureService.cs` (squad + hold point).

## Open questions for the design session

- Platoon composition table: fixed recipe per plan (AirDefence/FireSupport/Spearhead/ReconScreen)
  or one universal recipe?
- How many platoons a commander fields at once, and the reserve it keeps home.
- Approach choice: cheapest scoring that avoids the last N approaches used against the same
  objective, or a proper threat map?
- When does a platoon abandon an objective (losses, out of ammo, higher-value point freed)?
