# Track: player-ai-commander

**Created**: 2026-09-13 · **Type**: feature

**Decided by user (2026-09-13)**: the AI takes over *everything the enemy AI does* for the
player's faction; the player can still give manual orders and purchases alongside it
(co-command); the switch is a PLAYER COMMANDER button in Settings > Gameplay next to ENEMY
COMMANDER, plus a remappable hotkey, plus a HUD label while on.

## Goal

Flip one switch and the player's own faction is run by the same commander AI that runs the
enemy: it earns, builds, buys, defends, scouts, flies and captures. Flip it off and the faction
goes quiet again, leaving whatever the AI bought and positioned in place. While it is on, the
player's own clicks still work.

## Problem (with evidence)

The commander AI exists but is hard-wired to "every faction except mine", and it treats the
local faction as *the opponent* in several places:

- Skips: `Ai/CommanderEnemyCommanderService.cs:190`, `Ai/CommanderEnemyCommanderDefence.cs:124`,
  `Ai/CommanderCaptureService.cs:516`, `Economy/CommanderEconomyServiceEnemy.cs:53` all
  `continue` on `ReferenceEquals(hq, localHq)`.
- Opponent-as-local assumptions: `ReadForce(localHq)` feeds `ChoosePlan`
  (`Ai/CommanderEnemyCommanderService.cs:180`); `GetStrikeTarget(localHq)` and
  `RevealPlayerBase(hq, localHq)` (`Ai/CommanderEnemyCommanderAir.cs:450`,
  `Ai/CommanderEnemyCommanderService.cs` around the `Review` method); recon posts face
  `GetTerritoryCenter(localHq)` (`Ai/CommanderEnemyCommanderGround.cs:112`); naval purchases
  aim at the local territory centre (`Ai/CommanderEnemyCommanderGround.cs:211`).
- `LevelEconomy(hq, localHq)` copies the *local* HQ's balance onto the enemy; for the player's
  own HQ this must be a no-op.
- The HUD status line (`UI/CommanderOverlayUi.cs:384`) reads only the enemy commander.
- Server guard: all of this is host-only already (`hq.IsServer`), which is correct for the
  player's HQ too.

## Existing code being reused (and why it fits)

`CommanderEnemyCommanderService` (+ `Air`, `Ground`, `Defence` partials),
`CommanderEconomyService.ReviewEnemy`, `CommanderCaptureService.ReviewEnemy`,
`CommanderRepairService.TrySendEnemyRepairCrew`. All are already per-HQ (`states` is keyed by
`FactionHQ`, `CommanderState` holds per-HQ funds/posts/defenders). The work is to parameterise
**which HQs are commanded** and **who each one's opponent is**, not to write a second AI.

Naming: this track renames nothing upstream-visible. The new concept "commanded HQ set" lives in
a new small service; the existing `Enemy*` names stay so upstream merges remain clean.

## Requirements

1. **Setting** `Gameplay/PlayerCommanderEnabled` (bool, default false) in `CommanderSettings`,
   plus keybind `Keybinds/TogglePlayerCommander` (default: none bound; shown in Controls and in
   the Shortcuts reference via `CommanderShortcutReference.Collect`).
2. **New service** `Ai/CommanderPlayerCommanderService.cs` (Advanced tier, `ICommanderTickActive`
   for the hotkey, `ICommanderResetSession`). Exposes `static bool IsCommanded(FactionHQ hq,
   FactionHQ? localHq)` = `!ReferenceEquals(hq, localHq) || PlayerCommanderEnabled`. Every skip
   listed above calls this instead of the raw `ReferenceEquals`. One definition, five callers.
3. **Opponent resolution**: a `static FactionHQ? ChooseOpponent(FactionHQ hq)` in the same
   service = the hostile HQ with the most funds (tie: first). Every place that today passes
   `localHq` *as the opponent* (`ReadForce`, `GetStrikeTarget`, `RevealPlayerBase`,
   `EnsureReconPosts`, `ReviewNaval`, `TaskAirWing`) receives `opponent` instead. For enemy HQs
   the opponent is the local HQ whenever there is one hostile side, so behaviour is unchanged
   with the switch off. **This is the risky part; keep the diff mechanical: rename parameter,
   pass through.**
4. **Economy for the player HQ**: `LevelEconomy` and `PrepareDuel` are skipped for the local HQ
   (no head start, no fund reset). The player HQ builds/upgrades through the existing
   `ReviewEnemy` path at the existing prices and targets (duel targets apply on the duel map).
5. **Co-command**: the AI must not fight the player's hand. Defence recruitment
   (`RecruitDefenders`) and recon pinning (`ReviewRecon`) skip any unit that has an active
   player-issued order (`CommanderMoveService` knows; expose a `HasPlayerOrder(Unit)` query,
   reusing whatever tracks routes today rather than a new list). Purchases and buildings are
   additive; the player's BUILD window keeps working.
6. **UI**: `PLAYER COMMANDER: OFF/ON (N bought)` button under the ENEMY COMMANDER button in
   Settings > Gameplay (`UI/CommanderOverlayUiSettings.cs:189-202` pattern). HUD status line
   gains a second row `YOU: <PLAN> <FUNDS>` while on (`UI/CommanderOverlayUi.cs:384`). Alert
   toast on toggle ("PLAYER COMMANDER ON/OFF") via `CommanderAlertService`.
7. **Log**: the existing `Enemy commander (<faction>)` log lines print `Player commander
   (<faction>)` for the local HQ — one helper `CommanderLabel(hq)` used by every line.
8. **Self-checks**: `IsCommanded` truth table; `ChooseOpponent` picks the richest hostile and
   never returns the HQ itself.
9. **Multiplayer**: host-only like the enemy commander; a pure client shows the button disabled
   with "(HOST ONLY)".

## Acceptance criteria (in `Ground Control Duel`, host)

- [ ] Switch OFF: `BepInEx\LogOutput.log` shows no `Player commander` lines; enemy behaviour
      unchanged versus the previous build (same first three log lines after mission start).
- [ ] Switch ON mid-match: within 30 s a `Player commander (<faction>) bought ...` line; within
      60 s a mine or radar is built near the player's base; home-guard vehicles move to ring
      posts; an airframe is launched once affordable.
- [ ] Give a manual move order to a defending vehicle: it obeys and is not re-pinned until it
      arrives.
- [ ] Buy a unit by hand while ON: both purchases appear, funds drop by both prices.
- [ ] Hotkey toggles the switch; toast appears; HUD row appears/disappears.
- [ ] Switch OFF: no further `Player commander` lines; units stay where they are.
- [ ] No `self-check FAILED` at load.

## Files in scope

`Ai/CommanderPlayerCommanderService.cs` (new), `Ai/CommanderEnemyCommanderService.cs`,
`Ai/CommanderEnemyCommanderAir.cs`, `Ai/CommanderEnemyCommanderGround.cs`,
`Ai/CommanderEnemyCommanderDefence.cs`, `Ai/CommanderCaptureService.cs`,
`Economy/CommanderEconomyServiceEnemy.cs`, `Units/CommanderMoveService.cs` (query only),
`Core/CommanderSettings.cs`, `Core/CommanderModeController.cs` (one Register),
`Core/CommanderPlugin.cs` (SelfCheck), `UI/CommanderOverlayUiSettings.cs`,
`UI/CommanderOverlayUi.cs`, `UI/CommanderShortcutReference.cs`, `CHANGELOG.md`, `README.md`.

## Out of scope

A different personality or difficulty for the player-side AI; AI issuing orders to units the
player has selected; spectator camera automation; persisting the switch per mission; strike
calls (see `Project_plan.md`).

## NEEDS USER (open)

- None. All four design questions were answered 2026-09-13 (see header).
