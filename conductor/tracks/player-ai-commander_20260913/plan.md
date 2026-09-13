# Plan: player-ai-commander

**Track**: `player-ai-commander_20260913` · **Spec**: `spec.md` (binding; this plan follows its
file:line references and records the two places it had to depart from the spec's literal wording,
with reasons) · **Executor**: one agent, tasks in order, `dotnet build GroundControlRts.csproj -c
Release` after every task (0 warnings, 0 errors). Do NOT run `build-and-install.ps1`, `build-dev.bat`
or `build-release.bat` (the game may be running). Do NOT commit or stage anything.

If a fresh shell lacks `NUCLEAR_OPTION_DIR`, set it to `I:\SteamLibrary\steamapps\common\Nuclear Option`.

The working tree already carries uncommitted changes from the `ui-scale-slider` track in
`Core/CommanderSettings.cs`, `Core/CommanderPlugin.cs`, `UI/CommanderOverlayUiSettings.cs`,
`UI/CommanderUiScale.cs` and `CHANGELOG.md`. Build on top of them; never revert or reformat them.

## Read first (Reuse rule 1)

- `Ai/CommanderEnemyCommanderService.cs` whole file (724 lines): `TickPersistent` 161-207 is the
  seam; `Review` 229-325; `PrepareDuel` 358; `RevealPlayerBase` 377; `LevelEconomy` 404;
  `PruneStates` 557; `ReadForce` 618; `CommanderState` 687-723; the class remarks 13-23.
- `Ai/CommanderEnemyCommanderAir.cs` 236-262 (`BuyAirframe` facing), 396-435 (`TaskAirWing`),
  437-471 (`GetStrikeTarget` and its remarks — read them, they explain why the base is remembered).
- `Ai/CommanderEnemyCommanderGround.cs` 53-131 (`ReviewRecon`, `EnsureReconPosts`), 183-221 (`ReviewNaval`).
- `Ai/CommanderEnemyCommanderDefence.cs` 1-30 (class remarks on `playerCommand: true`), 104-186
  (`NotifyUnitDamaged`, `ReviewDefences`, `ReviewDefence`), 294-370 (`PruneDefenders`,
  `ReleaseSurplusDefenders`, `RecruitDefenders`).
- `Ai/CommanderCaptureService.cs` 136-150 (tick gate), 506-523 (`ReviewEnemies`), 668-690
  (`CollectCaptureSquad`), 776-790 (`PruneDrives`).
- `Economy/CommanderEconomyServiceEnemy.cs` 48-59 (`ReviewEnemies`), and the gate that calls it in
  `Economy/CommanderEconomyService.cs` 441-447.
- `Units/CommanderMoveService.cs` 49-53 (the tables), 1005 (`IsStopped`), 1213-1226 (`TryGetOrder`),
  1374-1395 (`playerDestinations` and where it is written from), `Units/CommanderMovePatches.cs` 1-16.
- `Core/CommanderSettings.cs` 71 (`EnemyCommanderMode`), 101-121 (shortcut pairs), 176 (warm-up
  touch), 213-228 (`GetShortcut`).
- `UI/CommanderOverlayUiSettings.cs` 130-240 (`DrawGameplaySettings`), 419-474 (`DrawControlSettings`),
  476-492 (`DrawBinding`), 536-563 (`GetBinding`), 565-592 (`SetBinding`), 607-621 (`ResetActionBindings`).
- `UI/CommanderOverlayUiPanel.cs` 193-215 — the `(HOST ONLY)` label + `GUI.enabled` pattern to copy.
- `UI/CommanderOverlayUi.cs` 81-82, 225-226 (`moneyRect`, `enemyPlanRect`), 321 (HUD hit-test), 379-389.
- `UI/CommanderShortcutReference.cs` 91-95 (INTERFACE section).
- `Units/CommanderAlertService.cs` 164-167 (`NotifyCapture`: a toast with no unit anchor, not gated
  by `CombatAlerts`) and 252-270 (`Raise`).
- `Core/CommanderModeController.cs` 82 (registration line), `Core/CommanderPlugin.cs` 31-38.
- `Camera/CommanderCameraTuning.cs` 94-140 and `UI/CommanderUiScale.cs` `SelfCheck` — the
  `failures` / `Expect` / `ExpectTrue` shape to copy for the new self-check.
- `conductor/tracks/ui-scale-slider_20260913/plan.md` — the executor-notes style expected here.

## Design facts fixed by the spec (and two recorded departures)

- Setting `Gameplay/PlayerCommanderEnabled` (bool, default false); shortcut
  `Keybinds/TogglePlayerCommander` (default `KeyCode.None`).
- New service `Ai/CommanderPlayerCommanderService.cs`, Advanced tier, `ICommanderTickActive` (hotkey
  only) and `ICommanderResetSession`. It owns the shared helpers listed below. The `Enemy*` names
  stay so upstream merges stay clean.
- **Hostile** means "another HQ" — the mod's own convention (`CommanderGameAccess.IsFriendlyUnit`
  compares HQs and nothing else), so `ChooseOpponent` ranks every non-self HQ in
  `FactionRegistry.GetAllHQs()` with a non-null `faction`.
- **Departure 1 — `IsCommanded` carries the enemy switch too.** Spec §2 writes it as
  `!ReferenceEquals(hq, localHq) || PlayerCommanderEnabled`. That formula assumes the caller has
  already gated on `EnemyCommanderMode != OFF`, which every loop today does *outside* the loop
  (`CommanderEnemyCommanderService.cs:169`, `CommanderCaptureService.cs:143`,
  `CommanderEconomyService.cs:443`). With the enemy commander OFF and the player commander ON, those
  outer gates would stop the player's HQ ever being reviewed. So the outer gates become
  `AnyCommanderOn` (= enemy mode on **or** player switch on) and the inner skip becomes
  `IsCommanded(hq, localHq)` = for the local HQ `PlayerCommanderEnabled`, for any other HQ
  `EnemyCommanderService.EffectiveMode != ModeOff`. Truth table with the player switch OFF: local →
  false (skipped, as today); other → enemy mode on, which the old outer gate already guaranteed
  inside the loop → true (reviewed, as today). Identical behaviour.
- **Departure 2 — `ChooseOpponent` returns the local HQ for every non-local HQ.** Spec §3 says
  "richest hostile" for every HQ and then asserts this equals the local HQ "whenever there is one
  hostile side". On a three-faction mission with two hostile HQs that would flip an enemy's opponent
  from the player to the richer other enemy — a behaviour change with the switch OFF, which the
  spec forbids. So: `ChooseOpponent(hq, localHq)` = `localHq` when `hq` is not the local HQ (exactly
  what every call site passes today), else the richest other HQ (tie: first in registry order).
  The richest-pick core is a pure function over a funds list so the self-check can drive it.
- Co-command query: `CommanderMoveService.HasPlayerOrder(Unit)` = `orders.ContainsKey(unit) ||
  stoppedUnits.Contains(unit)`. `orders` is the route table every Commander-issued travel, attack,
  guard and capture order lives in (`CommanderMoveService.cs:51`); `stoppedUnits` is the STOP hold.
  `playerDestinations` is deliberately **not** consulted: it is filled by a Harmony postfix on
  `UnitCommand.ServerSetDestination` (`CommanderMovePatches.cs:9-14`) that cannot tell a player's
  click from the home guard's own `SetDestination(post, playerCommand: true)`, so counting it would
  make the guard release and re-recruit its own defenders every review. Both tables only ever hold
  units the local HQ owns (`ShouldAllowCommanderMove`), so for enemy HQs the query is always false
  and the switch-OFF path is untouched.
- Every log line that today starts `Enemy commander ({hq.faction.name}` goes through one
  `CommanderPlayerCommanderService.CommanderLabel(hq)` → `Player commander (<faction>)` for the
  local HQ, `Enemy commander (<faction>)` otherwise. Self-check failure messages are not per-HQ and
  keep their wording.
- Toast on toggle: `CommanderAlertService.Instance?.NotifyCapture("PLAYER COMMANDER ON")` — the one
  existing "match event with no unit" toast, not gated by `CombatAlerts`. No new `LogKind`.
- Constants get a `<summary>` saying what the number is and why (CLAUDE.md). Comments explain why.

## OFF = unchanged: the call-site ledger (the plan evaluator checks every row)

`P` = `CommanderSettings.PlayerCommanderEnabled`. Every row must resolve to today's value when `P` is false.

| # | Site today | Becomes | Why unchanged when P is false |
|---|---|---|---|
| 1 | `Ai/CommanderEnemyCommanderService.cs:169` `if (mode == ModeOff || localHq == null) return;` | `if (!CommanderPlayerCommanderService.AnyCommanderOn || localHq == null) return;` | `AnyCommanderOn` = `mode != ModeOff || P`; with P false it is `mode != ModeOff`. |
| 2 | `:186` `ForceRead playerForce = ReadForce(localHq);` (once, before the loop) | moved inside the loop: `FactionHQ opponent = ChooseOpponent(hq, localHq); ForceRead opponentForce = ReadForce(opponent);` | Departure 2: for every non-local `hq`, `opponent` is `localHq`. `ReadForce` is pure over the HQ's unit list; computing it per HQ instead of once yields the same struct. |
| 3 | `:190` `ReferenceEquals(hq, localHq)` skip | `!CommanderPlayerCommanderService.IsCommanded(hq, localHq)` | Departure 1 truth table. |
| 4 | `:195` `Review(hq, localHq, mode, playerForce)` | `Review(hq, localHq, opponent, mode, opponentForce)` | `opponent == localHq` for non-local `hq`. `localHq` stays a parameter because §4 needs `isLocal`. |
| 5 | `:202` `PruneStates(localHq)` — drops the local HQ's state | prune the local HQ only when `!IsCommanded(localHq, localHq)` | With P false `IsCommanded(local)` is false → pruned as today. |
| 6 | `:242` `LevelEconomy(hq, localHq)` / `:246` `PrepareDuel(hq)` | both inside `if (!isLocal)` where `isLocal = ReferenceEquals(hq, localHq)` | The local HQ is never reviewed with P false, so `isLocal` is always false in the loop. |
| 7 | `:253` `RevealPlayerBase(hq, localHq)` | `RevealPlayerBase(hq, opponent)` (parameter renamed `opponent`) | Row 2. |
| 8 | `:260` `ReviewPosture(hq, localHq, state, playerForce)` | `ReviewPosture(hq, opponent, state, opponentForce)` | Row 2. |
| 9 | `:293` `ReviewNaval(hq, localHq, state, …)` | `ReviewNaval(hq, opponent, state, …)`; `Ground.cs:211` `GetTerritoryCenter(localHq)` → `GetTerritoryCenter(opponent)` | Row 2. |
| 10 | `:335` `ReviewRecon(hq, localHq, state)` → `Ground.cs:77` `EnsureReconPosts(state, hq, localHq)` → `:112` `GetTerritoryCenter(localHq)` | parameter renamed `opponent` end to end | Row 2. |
| 11 | `:340` `TaskAirWing(hq, localHq, playerForce)` → `Air.cs:405` `GetStrikeTarget(localHq)` | `TaskAirWing(hq, state, opponent, opponentForce)` — `state` is a NEW parameter threaded from `ReviewPosture`, which already has it in scope (`Service.cs:333`) → inside, `GetStrikeTarget(state, opponent)` | Row 2, plus row 13 for the remembered base. Passing `state` changes no logic; it only gives `GetStrikeTarget` the per-HQ slot. |
| 12 | `Air.cs:246-249` `GetLocalHq()` then `GetStrikeTarget(localHq)` for launch facing | `FactionHQ? player = CommanderGameAccess.GetLocalHq(); GlobalPosition facing = player == null ? airbase.center.GlobalPosition() : GetStrikeTarget(state, ChooseOpponent(hq, player));` (the local is named `player`, not `localHq`, so the T5/T11 greps stay meaningful) | `BuyAirframe` only ever runs for HQs inside the review loop; for a non-local `hq`, `ChooseOpponent` returns the local HQ. |
| 13 | `Air.cs:75` `private Airbase? playerHomeBase;` — one field for the whole service | moves into `CommanderState` as `StrikeBase` (the opponent's remembered opening base, per commander) | Today every enemy commander shares one field and all of them target the player, so all of them remember the same first airbase; per-state each remembers the player's first airbase, picked by the same loop over the same list on the same tick. With the local HQ also commanded the shared field would be overwritten with the enemy's base every review and the "remembered once and kept" rule in the remarks would break — that is the only reason it moves. `ResetSession` clears `states`, which now clears it. |
| 14 | `Defence.cs:124` `ReferenceEquals(hq, localHq)` skip in `ReviewDefences` | `!IsCommanded(hq, localHq)` | Departure 1. |
| 15 | `Ai/CommanderCaptureService.cs:143` gate `EffectiveMode != ModeOff` | `CommanderPlayerCommanderService.AnyCommanderOn` | Row 1. |
| 16 | `:516` `!ReferenceEquals(hq, localHq)` | `IsCommanded(hq, localHq)` | Departure 1. |
| 17 | `:522` `PruneDrives(localHq)` | prune local only when `!IsCommanded(localHq, localHq)` | Row 5. |
| 18 | `Economy/CommanderEconomyService.cs:443` gate | `AnyCommanderOn` | Row 1. |
| 19 | `Economy/CommanderEconomyServiceEnemy.cs:53` skip | `!IsCommanded(hq, localHq)` | Departure 1. |
| 20 | `Defence.cs` `RecruitDefenders`, `PruneDefenders`, `Ground.cs` `ReviewRecon` loop, `CommanderCaptureService.CollectCaptureSquad` | each additionally skips / drops units where `CommanderMoveService.HasPlayerOrder(unit)` | Both tables behind `HasPlayerOrder` hold only local-HQ units, which are never reviewed with P false. |
| 21 | 23 log lines `$"Enemy commander ({hq.faction.name}…` (list in T8) | `$"{CommanderLabel(hq)}…` | `CommanderLabel` prints `Enemy commander (<name>)` for every non-local HQ. |
| 22 | `TotalPurchases++` at `Service.cs:320`, `Air.cs:257`, `Ground.cs:215` | `RecordPurchase(hq)` — local → `PlayerPurchases++`, else `TotalPurchases++` | Local HQ never reviewed with P false. |
| 23 | `Service.cs:196-206` `primary` / `StatusLine` | the `primary` pick skips the local HQ; a new `PlayerStatusLine` is set from the local state | Local HQ never in the loop with P false; `PlayerStatusLine` stays empty. |

Not changed (stays `localHq`, on purpose): `Ai/CommanderCaptureService.cs:160-171, 349-362` — the
player's own capture-order path, not the AI.

## Tasks

- [x] **T1 — Setting, keybind, Controls row, Shortcuts row.** `Core/CommanderSettings.cs`: add
  `PlayerCommanderEnabled` (`Get("Gameplay", "PlayerCommanderEnabled", false)` / `Set`) beside
  `EnemyCommanderMode` (line 71) and `TogglePlayerCommander` (`GetShortcut("TogglePlayerCommander",
  KeyCode.None, "Toggle the AI commander for your own faction.")`) after `MapBoxSelect` (line 121);
  touch `_ = PlayerCommanderEnabled;` after `_ = EnemyCommanderMode;` (line 176). Shortcuts are
  created by `GetShortcut` on first read, so no touch is needed for the bind.
  `UI/CommanderOverlayUiSettings.cs`: add `"toggle_player_commander"` to `GetBinding`, `SetBinding`
  and `ResetActionBindings` (reset to `KeyCode.None`); draw it in `DrawControlSettings` as the last
  COMMANDER ACTIONS row (`right, rowY + 384f`, label `"Player commander"`); grow the panel box from
  `480f` to `520f` and move both RESET buttons from `y + 432f` to `y + 472f`. Check by arithmetic:
  the tab starts at `y = 82` (or 162 with help open); 162 + 520 = 682 < 790.
  `UI/CommanderShortcutReference.cs`: in INTERFACE add
  `new Entry("Toggle player commander", Key(CommanderSettings.TogglePlayerCommander), "AI runs your faction; you keep command too.")`.
  Build.

- [x] **T2 — New service with the shared helpers.** Create `Ai/CommanderPlayerCommanderService.cs`,
  `internal sealed class CommanderPlayerCommanderService : ICommanderTickActive, ICommanderResetSession`,
  file-scoped namespace, a class `<summary>` saying what it is (the switch and the shared "who is
  commanded / who is the opponent" rules; the AI itself stays in `CommanderEnemyCommanderService`).
  Members:
  - `internal static bool AnyCommanderOn => CommanderEnemyCommanderService.EffectiveMode != CommanderEnemyCommanderService.ModeOff || CommanderSettings.PlayerCommanderEnabled;`
  - `internal static bool IsCommanded(bool isLocalHq, bool enemyCommanderOn, bool playerCommanderOn)` — pure: `isLocalHq ? playerCommanderOn : enemyCommanderOn`.
  - `internal static bool IsCommanded(FactionHQ hq, FactionHQ? localHq)` — feeds the live values into the pure one.
  - `internal static int PickRichestIndex(IReadOnlyList<float> funds, int selfIndex)` — pure: index of the largest value excluding `selfIndex`, first wins a tie, `-1` when nothing else exists.
  - `internal static FactionHQ ChooseOpponent(FactionHQ hq, FactionHQ localHq)` — Departure 2. Build the candidate list from `FactionRegistry.GetAllHQs()` (non-null, `faction != null`) into two reusable `static readonly List<>` buffers (no per-call allocation; the review runs every 30 s), call `PickRichestIndex`, and if it returns `-1` fall back to `hq` itself — document that the fallback means "solo mission, no opponent" and that `GetStrikeTarget`/`GetTerritoryCenter` on one's own HQ is harmless.
  - `internal static string CommanderLabel(FactionHQ hq)` — `ReferenceEquals(hq, CommanderGameAccess.GetLocalHq()) ? $"Player commander ({hq.faction.name})" : $"Enemy commander ({hq.faction.name})"`.
  - `internal void Toggle()` — flips `CommanderSettings.PlayerCommanderEnabled`, raises the toast via `CommanderAlertService.Instance?.NotifyCapture(on ? "PLAYER COMMANDER ON" : "PLAYER COMMANDER OFF")`, logs `Player commander switched ON/OFF for <faction or "no faction">`.
  - `TickActive()` — `if (CommanderShortcutInput.IsDown(CommanderSettings.TogglePlayerCommander)) Toggle();`
  - `ResetSession()` — nothing to clear today; keep the method with a one-line comment saying the switch persists across missions on purpose (spec: not persisted per mission means it is a plain setting, not mission state).
  Register in `Core/CommanderModeController.cs` right after line 82
  (`services.Register(new CommanderPlayerCommanderService(), CommanderTier.Advanced);`) with a
  comment: ticks after the enemy commander so a toggle this frame is seen by next frame's review.
  Build.

- [x] **T3 — Self-check (with fail-proof).** In the new file add `internal static void SelfCheck()`
  copying the `failures` / `Expect` / `ExpectTrue` / `LogDebug` passed / `LogError "Player commander
  self-check FAILED: …"` shape from `UI/CommanderUiScale.cs`. Cases:
  - `IsCommanded(true, false, false) == false` "local, both off"
  - `IsCommanded(true, true, false) == false` "local, enemy only" — the switch-OFF invariant
  - `IsCommanded(true, false, true) == true` "local, player only"
  - `IsCommanded(false, true, false) == true` "enemy, enemy on"
  - `IsCommanded(false, false, true) == false` "enemy, player only" — the player switch never commands a hostile HQ
  - `PickRichestIndex([10, 30, 20], 0) == 1` "richest hostile wins"
  - `PickRichestIndex([30, 10, 20], 0) == 2` "never itself"
  - `PickRichestIndex([10, 20, 20], 0) == 1` "tie goes to the first"
  - `PickRichestIndex([10], 0) == -1` "nobody else"
  Register `CommanderPlayerCommanderService.SelfCheck();` in `Core/CommanderPlugin.cs` after
  `CommanderEnemyCommanderService.SelfCheck();` (line 33). Build.
  Testing rule 5: record SHA256 of the new file; plant `isLocalHq ? playerCommanderOn : enemyCommanderOn`
  → `isLocalHq || enemyCommanderOn` in the pure `IsCommanded`; build (must still succeed — the
  compiler cannot see it); read the code path and name the cases that would fail ("local, both off",
  "local, enemy only"); restore; build; record SHA256 again, byte-identical. Write it all in the notes.

- [x] **T4 — Skips and gates (ledger rows 1, 3, 5, 14-19).** Apply exactly the rows listed. In
  `Defence.cs:104-110` update the `NotifyUnitDamaged` remark: `states` holds the local HQ only while
  the player commander is on, and then the player's own losses put *their* commander on the
  defensive, which is the point. Build. Then Grep `ReferenceEquals(hq, localHq)` and
  `ReferenceEquals(entry.Key, localHq)` across `Ai/` and `Economy/` and confirm the only survivors
  are the two prune conditions (now wrapped in `!IsCommanded`). (T6 adds one more legitimate hit,
  `isLocal` in `Review`; T11's final grep accounts for it.)

- [x] **T5 — Opponent pass-through (ledger rows 2, 4, 7-13).** Purely mechanical: rename the
  `localHq` parameter to `opponent` in `Review` (which keeps `localHq` as well, see row 4),
  `ReviewPosture`, `RevealPlayerBase`, `ReviewRecon`, `EnsureReconPosts`, `ReviewNaval`,
  `TaskAirWing`; `TaskAirWing` gains a `CommanderState state` parameter threaded from `ReviewPosture`
  (row 11); `GetStrikeTarget(FactionHQ localHq)` becomes `GetStrikeTarget(CommanderState state,
  FactionHQ opponent)` reading/writing `state.StrikeBase` (row 13; move the `<summary>` from the
  field onto the new `CommanderState` member and fix the remark's wording from "the player's" to
  "the opponent's opening airbase", keeping the rest of the remarks verbatim). Rename the
  `playerForce` parameters to `opponentForce` where they flow from row 2 (`Review`, `ReviewPosture`,
  `TaskAirWing`, `UpdatePlan`, `BuyAirframe`), and fix the `ReadForce` summary ("What the opponent is
  actually fielding…"). Do not touch `ChoosePlan`, `MatchesPlan` or any threshold. Build. Confirm with
  Grep that no `localHq` remains in `Ai/CommanderEnemyCommanderAir.cs` or `…Ground.cs`.

- [x] **T6 — Economy rules for the player HQ (row 6).** In `Review`: `bool isLocal = ReferenceEquals(hq, localHq);`
  and wrap `LevelEconomy` / `PrepareDuel` in `if (!isLocal)`. On the first review of the local HQ
  log one line through `CommanderLabel`: `… keeps the player's own economy: no head start, no fund
  reset.` Everything else in `Review` (build reserve, spend fraction, duel purchase count, air fund,
  naval share, capture unit precedence) runs unchanged for the local HQ — that is spec §4. Build.

- [x] **T7 — Co-command (row 20).** `Units/CommanderMoveService.cs`: add
  `internal bool HasPlayerOrder(Unit? unit) => unit != null && (orders.ContainsKey(unit) || stoppedUnits.Contains(unit));`
  next to `IsStopped` (line 1005), with the `<summary>` from the design facts above (why
  `playerDestinations` is excluded). No static wrapper: the four sites call
  `CommanderMoveService.Instance?.HasPlayerOrder(unit) == true` directly, so there is one definition.
  Sites:
  - `Defence.cs` `RecruitDefenders` candidate filter: add `&& CommanderMoveService.Instance?.HasPlayerOrder(unit) != true`.
  - `Defence.cs` `PruneDefenders`: a defender with a player order is dropped from `state.Defenders`
    **without** clearing `CommandedDestinationRef` (the player's own route set `commandedDestination`
    and owns it now) — comment says so. When the route completes, `orders` drops it and the next
    review may recruit it again, which is the spec's "not re-pinned until it arrives".
  - `Ground.cs` `ReviewRecon` issue loop: `continue` on a truck with a player order (comment: the
    player has borrowed this radar; the post stays, the truck goes back to it when the order ends).
  - `Ai/CommanderCaptureService.cs` `CollectCaptureSquad`: skip units with a player order, same
    reason as the `IsDefendingUnit` skip right above it. **Deviation from spec §5**, which names only
    `RecruitDefenders` and `ReviewRecon`; the capture squad re-issues `SetDestination` every 20 s to
    every capture-capable unit it owns, so without this row the AI would fight a player's manual
    order on any troop carrier — spec §5's first sentence. Record it in the notes.
  Build.

- [x] **T8 — One label helper, one purchase counter (rows 21-22).** Replace every
  `$"Enemy commander ({hq.faction.name}` (and `{hq.faction?.name}` in `Units/CommanderRepairService.cs:186`)
  with `$"{CommanderPlayerCommanderService.CommanderLabel(hq)}` — 23 sites:
  `Service.cs:322, 364, 414, 445`; `Air.cs:260, 387, 431, 505`; `Ground.cs:217`; `Defence.cs:151, 153`;
  `Capture.cs:547, 587`; `EconomyEnemy.cs:108, 141, 244, 266, 426, 449, 470, 484, 509`; `Repair.cs:186`.
  Keep the rest of each string byte-for-byte. Exactly one site has extra content inside the label's
  parentheses, `Service.cs:322` (`{hq.faction.name}, {GetPlanLabel(buyPlan)}`): give `CommanderLabel`
  an optional `string? detail = null` appended inside the parentheses as `, {detail}` so that line
  still reads `Enemy commander (Boscali, SPEARHEAD) bought …` exactly as before; the other 22 sites
  call it with one argument. Add `private void RecordPurchase(FactionHQ hq)` in
  `Service.cs` beside `TotalPurchases` and `internal int PlayerPurchases { get; private set; }`;
  replace the three `TotalPurchases++`; clear `PlayerPurchases` in `ResetSession`. Update the
  `TotalPurchases` summary: enemy commanders only. Build. Grep `"Enemy commander (` afterwards: the
  only hits must be in self-check messages and comments.

- [x] **T9 — Settings button, HUD row, host-only (spec §6, §9; row 23).**
  `Service.cs` `TickPersistent`: `primary` skips the local HQ; after the loop set
  `PlayerStatusLine` = `$"{GetPlanLabel(plan)}   {FundsLabel(localHq.factionFunds)}" + (Defending ? "   DEFENDING" : "")`
  when `states` holds the local HQ, else `string.Empty`; clear it in `ResetSession`.
  `UI/CommanderOverlayUi.cs`: add `playerPlanRect` (`enemyPlanRect.x`, `enemyPlanRect.yMax + 4f`, 250, 30),
  include it in the hit-test at line 321, draw `GUI.Box(playerPlanRect, $"YOU   {status}", CommanderUiTheme.Money)`
  when `PlayerStatusLine` is non-empty (it is only non-empty while the switch is on).
  `UI/CommanderOverlayUiSettings.cs` `DrawGameplaySettings` — the COMMAND box has to take one more
  32 px button and still fit under the settings window with the help overlay open
  (window 790 tall; content starts at `y = 162` then). Reflow: tighten the nine toggles from a 34 px
  to a 32 px pitch (`commandY + 40, 72, 104, …, 296`), ENEMY COMMANDER at `+334`, new
  PLAYER COMMANDER at `+372`, radius sliders at `+412` and `+450`, capture label `+488` / slider
  `+494`, COMMAND box height `512f` → `526f`, and SPAWN RESTRICTIONS box `92f` → `84f` with
  `commandY = y + 96f`. Check: 162 + 96 + 526 = 784 ≤ 790. Button text
  `$"PLAYER COMMANDER: {(on ? "ON" : "OFF")}" + (on ? $"   ({PlayerPurchases} bought)" : "")`,
  style `DangerButton` when on (mirror the ENEMY button), click → `Toggle()` on the service instance
  (add `internal static CommanderPlayerCommanderService? Instance` set in the constructor, as the
  other services do). Host-only: `FactionHQ? hq = CommanderGameAccess.GetLocalHq(); bool host = hq == null || hq.IsServer;`
  — when not host append `"   (HOST ONLY)"` and wrap the button in the `GUI.enabled` pattern from
  `UI/CommanderOverlayUiPanel.cs:193-215`. The AI loops already require `hq.IsServer`, so a client's
  stale `true` in the config does nothing. Build.

- [x] **T10 — CHANGELOG and README.** `CHANGELOG.md`: new bullet at the top of the first
  `### Added` under `## Unreleased` (currently line 222, above the UI-scale bullet), in the file's
  voice: what the player sees (Settings > Gameplay PLAYER COMMANDER button, hotkey in Controls, the
  YOU row under your funds, the toast), what it does (the same commander that runs the enemy runs
  your faction: builds, buys, defends, scouts, flies, captures; your own orders and purchases still
  work and the AI leaves any unit you have given an order alone until it arrives), what it does not
  do (no head start, no fund reset for you; off by default; host only). `README.md`: a short
  `### Player commander` subsection at the end of `## Enemy commander` (after line ~560) saying the
  same in two paragraphs, plus the config keys `Gameplay/PlayerCommanderEnabled` and
  `Keybinds/TogglePlayerCommander`.

- [x] **T11 — Final build, greps, notes.** `dotnet build GroundControlRts.csproj -c Release`; paste
  the last 5 lines below. Greps to paste: `localHq` in `Ai/CommanderEnemyCommanderAir.cs` and
  `Ai/CommanderEnemyCommanderGround.cs` (expect none), `"Enemy commander (` (expect only self-check
  strings and comments), `TotalPurchases++` (expect none), `ReferenceEquals(hq, localHq)` in `Ai/`
  and `Economy/` (expect only `isLocal` in `Review`). Tick every task, fill every note section.

## In-game acceptance (USER TO VERIFY IN GAME — not the executor, not the evaluator)

The spec's acceptance criteria verbatim, in `Ground Control Duel` as host, after
`.\build-and-install.ps1` with the game closed (or `build-dev.bat` for hot reload):

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

Planner notes for the user, not in the spec: (a) on the duel map the switch also hands *you* the
enemy's buildings as standing intel (`RevealPlayerBase` with you as the commander) — symmetric with
what the enemy gets, and what spec §3 asks for, but worth knowing; (b) `TaskAirWing` gives a mission
to every AI-piloted aircraft of yours that has none — an airframe you launched from the AIR window
and left idle will be tasked by the AI; one you gave a mission is left alone.

## Executor notes

### Final build (T11)

`dotnet build "I:\Dropbox (Personal)\Projects\RTS-Commander\GroundControlRts.csproj" -c Release`,
last 5 lines verbatim:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:00.51
```

A build was run after every one of T1-T10, plus the baseline build before T1 and the two builds the
T3 fail-proof needs; every one was `0 Warning(s)`, `0 Error(s)`. `NUCLEAR_OPTION_DIR` was exported
as `I:\SteamLibrary\steamapps\common\Nuclear Option` in each shell. Nothing was installed, staged or
committed.

T11 greps, verbatim results:

- `grep -rn 'localHq' Ai/CommanderEnemyCommanderAir.cs Ai/CommanderEnemyCommanderGround.cs` — no
  hits. (`playerForce` and `playerHomeBase` were checked the same way, also no hits.)
- `grep -rn '"Enemy commander (' --include=*.cs .` — no hits. Better than the plan's expectation of
  "self-check strings and comments only": the three self-check messages are
  `Enemy plan/air role/defence self-check FAILED`, none of which contains `Enemy commander (`, and
  the label itself is now assembled from the bare word in
  `Ai/CommanderPlayerCommanderService.cs:139`. Widening to `grep -rn 'Enemy commander'` returns that
  one line and nothing else — one definition, twenty-three callers.
- `grep -rn 'TotalPurchases++' --include=*.cs .` — one hit,
  `Ai/CommanderEnemyCommanderService.cs:131`, which is the body of the new `RecordPurchase` helper
  itself. All three original call sites (`Service.cs`, `Air.cs`, `Ground.cs`) are gone, which is what
  the row meant.
- `grep -rn 'ReferenceEquals(hq, localHq)' Ai Economy` — five hits, all legitimate:
  `CommanderEnemyCommanderService.cs:227` (the `primary` pick, ledger row 23 — see the deviation
  below), `:277` (`isLocal` in `Review`, ledger row 6), and three inside
  `CommanderPlayerCommanderService.cs` itself (lines 61, 65, 106 — the doc comment on `IsCommanded`,
  its one live use, and `ChooseOpponent`'s Departure-2 test). The old skips in `Defence.cs`,
  `CommanderCaptureService.cs` and `CommanderEconomyServiceEnemy.cs` are all gone.

### T3 fail-proof record (Testing rule 5)

Done, with the defect removed afterwards.

1. SHA256 of `Ai/CommanderPlayerCommanderService.cs` before planting:
   `47CB4234E38BAA57A9AA5AE535A52CD420DB332C40B07A22E1D2A7944BBBE901`.
2. Planted the defect the plan names: the pure `IsCommanded` body
   `isLocalHq ? playerCommanderOn : enemyCommanderOn` became `isLocalHq || enemyCommanderOn`. Build:
   succeeded, `0 Warning(s)`, `0 Error(s)` — the defect is invisible to the compiler, which is the
   whole reason the self-check exists.
3. Code path read with the defect in place. `SelfCheck` calls the pure overload five times and
   compares with `Expect(List<string>, string, bool, bool)`, which appends
   `"{name}: expected {expected}, got {actual}"` to `failures` on a mismatch; after the five cases
   `failures.Count == 0` is false, so the loop at the end logs each one through
   `CommanderPlugin.Log.LogError` as `Player commander self-check FAILED: …`. Case by case, with
   `isLocalHq || enemyCommanderOn` substituted:
   - **"local, both off"** — `true || false` = `true`, expected `false`. **FAILS.**
   - **"local, enemy only"** — `true || true` = `true`, expected `false`. **FAILS.** This is the
     switch-OFF invariant: with the defect in, an enemy commander running would also command the
     player's own HQ with `PlayerCommanderEnabled` false, which is exactly the OFF-is-unchanged
     property the whole ledger is built on.
   - "local, player only" — `true || false` = `true`, expected `true`. Passes.
   - "enemy, enemy on" — `false || true` = `true`, expected `true`. Passes.
   - "enemy, player only" — `false || false` = `false`, expected `false`. Passes.

   Two named failures, the two the plan predicted: `local, both off` and `local, enemy only`. The
   four `PickRichestIndex` cases are untouched by this defect and still pass. No game run was needed:
   every function on the path is pure and takes its inputs as parameters.
4. Restored `isLocalHq ? playerCommanderOn : enemyCommanderOn`. SHA256 after restore:
   `47CB4234E38BAA57A9AA5AE535A52CD420DB332C40B07A22E1D2A7944BBBE901` — byte-identical to step 1
   (`Get-FileHash … -Algorithm SHA256`). Rebuilt: `0 Warning(s)`, `0 Error(s)`.

### Fix cycle 1 (execution evaluation, 2026-09-13)

The independent execution evaluation returned FAIL with two defects; both were fixed by the loop
runner and re-verified.

1. **Stale YOU row.** `PlayerStatusLine` was only cleared by the review loop, which sits behind the
   `AnyCommanderOn` early return in `TickPersistent`. On a non-duel mission with the enemy commander
   OFF, switching the player commander OFF made `AnyCommanderOn` false, so the loop never ran again
   and the HUD kept the last `YOU <PLAN> <FUNDS>` row (spec acceptance criterion 5). Fix:
   `PlayerStatusLine = string.Empty;` inside that early-return block, with a comment. `StatusLine`
   is untouched there, and with the switch off the value on that path was already empty, so the
   OFF path is unchanged.
2. **Controls tab overlap.** The new "Player commander" binding row (`rowY + 384`, 30 tall, so its
   bottom is `y + 482`) overlapped the two RESET buttons at `y + 472` by 10 px. Fix: RESET buttons
   moved to `y + 492f`, panel box grown `520f` -> `540f`. Check: `162 + 540 = 702 <= 790`.

Build after the fixes: `0 Warning(s)`, `0 Error(s)`.

### Deviations from plan or spec

Every ledger row was applied exactly as written; none was skipped or altered. The two departures the
plan records (IsCommanded carrying the enemy switch, ChooseOpponent returning the local HQ for every
non-local HQ) and the T7 capture-squad skip are implemented as specified. Four further notes:

1. **A sixth `playerForce` → `opponentForce` rename, in `ChooseAirRole`.** T5 lists five functions
   (`Review`, `ReviewPosture`, `TaskAirWing`, `UpdatePlan`, `BuyAirframe`). `BuyAirframe` passes that
   same struct straight into `ChooseAirRole(FactionHQ, in ForceRead)`, which the list does not name,
   so a literal reading would have left one frame calling the identical value `playerForce` while its
   caller called it `opponentForce`. Renamed for consistency; it is the same mechanical rename with
   no behaviour attached, and nothing else in `Air.cs` reads the parameter.

2. **Ledger row 23 adds a second `ReferenceEquals(hq, localHq)`.** T4's aside expects T11's grep to
   find exactly one legitimate hit (`isLocal` in `Review`). Row 23's "the `primary` pick skips the
   local HQ" needs a second one, in `TickPersistent`, because the `primary` comparison is about funds
   and not about being commanded — `IsCommanded` cannot express it. Both hits are recorded in the
   grep results above. No other raw skip was introduced.

3. **Prose touched beyond the lines the plan names.** T5 explicitly asks for the `GetStrikeTarget`
   remark and the `ReadForce` summary; alongside those, `ReviewRecon`'s and `EnsureReconPosts`'
   summaries in `Ground.cs` had "the player's territory" changed to "the opponent's territory" and
   the local `playerCenter` renamed `opponentCenter`, because the parameter feeding them is now
   `opponent` end to end (row 10) and the old wording would have been false for the player's own
   commander. `RevealPlayerBase`'s summary was deliberately **left verbatim** — the plan only asked
   for the parameter rename there, and the planner's note (a) to the user already covers what that
   method does with the switch on.

4. **`CommanderRepairService.cs:186` used `{hq.faction?.name}`, the other twenty-two used
   `{hq.faction.name}`.** Both now call `CommanderLabel(hq)`, which dereferences `hq.faction.name`
   like the other twenty-two always did. That is safe: the only caller of
   `TrySendEnemyRepairCrew(hq)` is `CommanderEconomyService.ReviewEnemy`, reached only from a loop
   that already requires `hq.faction != null`. The `?.` was defensive, not load-bearing.

No concern was found with any ledger row, so nothing was left implemented-as-written-but-disputed.
