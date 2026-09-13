# Backlog: Air and naval support tasking (track 4 of 4)

**Status**: not yet designed. Captured 2026-09-13 from the AI-behaviour brainstorm. Depends on
platoons-with-objectives (support is tasked *for* a platoon's objective).

## What the user asked for (verbatim intent)

- The same objective list that drives platoons also drives **strike sorties and ship positions**
  in support: a platoon moving on a village gets a CAS mission over it, a ship stationed off a
  coastal point, AWACS/air guard over the platoon's approach.
- Fully unified with the ground brain, not a separate air brain choosing its own targets.

## Reuse to read first

`Ai/CommanderEnemyCommanderAir.cs` (`TaskAirWing`, `GetStrikeTarget`, role composition),
`AirCommand/CommanderAirCommandService.cs` mission API (area, radius, mode, `AirMissionRecipe`
and auto-recreate from 2026-09-13), `Ai/CommanderEnemyCommanderGround.cs` `ReviewNaval`,
`Naval/CommanderNavalPurchaseService.cs`.

## Open questions for the design session

- Support ratio: one sortie per platoon, or per objective under contest?
- Does the player-side AI task the player's Air Command aircraft, or only ones it bought?
- Naval: station ships at coastal points only, or use them as floating AA for platoons near the
  shore?
