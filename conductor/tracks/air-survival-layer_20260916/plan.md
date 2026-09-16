# Air Survival Layer Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use orchestrator-supaconductor:executing-plans to implement this plan task-by-task.

**Goal:** Give commanded aircraft one coherent sense of self-preservation in four layers: the game's pilot keeps its bravery and break-off; a per-aircraft check sends an airframe home when it is out of ammo or low on fuel; the sortie posture also holds a sortie that faces a tracked air-defence belt with no sweep in; the attrition brake thins the sky; the duplicate clocks fold into the two helpers that already exist.

**Architecture:** Design in `conductor/tracks/air-survival-layer_20260916/design.md` (read §2–§5 first) and the audit in `conductor/designs/2026-09-16-air-self-preservation-audit.md`. New partial-class file `AirCommand/CommanderAirCommandSurvival.cs` on `CommanderAirCommandService`, ticked from the existing 2 s mission-prune clock. Everything else edits existing rules in place. No new Harmony patch, no new service.

**Tech Stack:** C# / net472 / BepInEx 5 / Harmony. Build ONCE at the end (Task 15) with `$env:NUCLEAR_OPTION_DIR = "I:\SteamLibrary\steamapps\common\Nuclear Option"; .\build-and-install.ps1 -Dev` — the developer is PLAYING right now and every hot reload wipes the commander's memory, so do not install after each task; use `dotnet build` (no install) to check compilation as you go. Verification: `SelfCheck()` cases logged at load; `BepInEx\LogOutput.log`.

**Rules that apply:** `.claude/CLAUDE.md` Reuse and Testing rules. Move code, never paraphrase it; one definition, two callers; every threshold gets an `Expect` self-check beside the existing ones; constants at class level with a `<summary>` saying what the number means and why; settings with `Get`/`Set` and a warm-up touch; comments record the user decision (2026-09-16) and the evidence in the file's voice. Never commit. Preserve each file's BOM and CRLF.

---

## Tasks

### Task 1 [x]: Settings
**Files:** `Core/CommanderSettings.cs` (Operations section, near `AirPostureRingMeters`; warm-up list).
Add `AirSurvivalFuelFraction` (float, 0.25: below this share of fuel a commanded airframe goes home; the game's own pilot lands at 0.20, so the commander acts first and the sortie frees the slot cleanly), `AirBeltHoldRadiusMeters` (float, 20000), `PackageFormUpStandoffMeters` (float, 20000: the package form-up standoff, split off the posture ring so one setting stops doing two jobs). Remove `AirSelfDefenceRadiusMeters` and its warm-up touch (Task 6 removes its readers). Verify: `dotnet build` errors only where the removed setting is still read — fix in Task 6.

### Task 2 [x]: Bravery refusal restored (pure + self-checks)
**Files:** `AirCommand/CommanderAirCommandPilotHooks.cs` (beside `TargetOutsideSelfDefence`, ~:273, and the end of the candidate loop in `ChooseMissionTarget` where `bestTarget`/`bestStation`/`bestOpportunity` are chosen); self-checks in `Operations/CommanderOperationsAirPosture.cs` `CheckAirPosture` or a new `CheckSurvival` block in `Operations/CommanderOperationsAirWing.cs` (register it where `CheckAirPosture` is registered).
Add `internal static bool TargetIsTooDangerous(float opportunity, float bravery, float hqThreat, float rangeMeters, float weaponMaxRangeMeters)` transcribing the game's `ChooseHQTarget` refusal (decompiled `/tmp/no-decomp/Assembly-CSharp.decompiled.cs:724`): `opportunity * bravery * 2f < 0.35f && hqThreat > opportunity * bravery * 2f && rangeMeters > weaponMaxRangeMeters * 2f`. After the loop, if `bestTarget != null && !ReferenceEquals(bestTarget, mission.ForcedTarget) && TargetIsTooDangerous(bestOpportunity, aircraft.bravery, hq.GetAircraftThreat(bestTarget.persistentID), bestRange, bestStation.WeaponInfo.targetRequirements.maxRange)` → drop the target (return the no-target result with `outOfAmmo` as computed). Keep `bestRange` alongside the existing bests. Self-checks: a weak, threatened, far target is refused; the same target inside 2× weapon range is taken; a strong opportunity (≥ 0.175/bravery) is taken; bravery 1.0 halves the refusal band.

### Task 3 [x]: Destination clamp respects break-off
**Files:** `AirCommand/CommanderAirCommandPatches.cs` (add `AttackModeField = AccessTools.Field(typeof(AIPilotCombatModes), "attackMode")` beside `DestinationField`), `AirCommand/CommanderAirCommandPilotHooks.cs` (`ConstrainMissionDestination`).
The game's `AttackMode` enum is private; read the field as `int` via `Convert.ToInt32(AttackModeField.GetValue(state))` and compare with the ordinals `BreakOffAttack = 1`, `RetreatStandoff = 2` (enum order in the decompiled source at `:12316`; record the ordinals in a `<summary>` with the caveat that a game update re-ordering the enum must be re-checked). Return early when either. Pure twin `PilotIsDisengaging(int attackMode)` with a self-check for 1, 2, and 0/3.

### Task 4 [x]: Survival file and the one out-of-ammo definition
**Files:** Create `AirCommand/CommanderAirCommandSurvival.cs` (`internal sealed partial class CommanderAirCommandService`, namespace `GroundControlRts`, header remark in the style of `Operations/CommanderOperationsAirPosture.cs`). Modify `AirCommand/CommanderAirCommandService.cs:558` (`IsWinchester`) and `AirCommand/CommanderAirCommandPilotHooks.cs` (`TryChooseMissionTarget`, the `result.outOfAmmo && mission.RouteIndex >= mission.Route.Count` branch; `MissionIsOutOfAmmo`).
MOVE `IsWinchester` into the new file (pointer comment at the old site). Make the chooser's out-of-ammo decision call the same predicate family: `MissionIsOutOfAmmo` stays pure; `IsWinchester` is its runtime reader. Nothing else changes behaviour yet.

### Task 5 [x]: Survival tick — ammo and fuel
**Files:** `AirCommand/CommanderAirCommandSurvival.cs`; `AirCommand/CommanderAirCommandService.cs:201-207` (call `TickSurvival()` after `ProcessReturningMissions()`).
`TickSurvival()`: for each `(aircraft, mission)` in `missions` where `aircraft` alive, `aircraft.Player == null`, `!mission.Returning`, `mission.LandingBase == null`, and the mission is commander-issued (find how the operations side marks its missions — `state.CommanderAirframes` lives in `CommanderOperationsService`; expose `CommanderOperationsService.IsCommanderAirframe(aircraft)` or reuse an existing accessor; player-tasked Air Command missions are EXEMPT by design):
- `IsWinchester(aircraft) && mission.RouteIndex >= mission.Route.Count` → `RequestReturnToBase(aircraft)`, log `"{label} goes home: out of ammo."` via `CommanderAiLog.Note(hq, …)`.
- `FuelBelow(aircraft.GetFuelLevel(), CommanderSettings.AirSurvivalFuelFraction)` (pure: fraction ≤ 0 = off; exactly on the fraction counts) → return, log `"{label} goes home: fuel at {level:P0}."`.
- Pilot already in `AIPilotLandingState` (any pilot of `aircraft.pilots` whose `currentState is AIPilotLandingState`) while the mission is not returning → `mission.Returning = true` (the same field the RTB path sets — read `RequestReturnToBase` to set exactly what it sets), log `"{label}: the pilot is landing for fuel; the sortie frees its slot."`.
One line per airframe per reason (a `HashSet<Aircraft>` of reported airframes cleared on `ResetSession`). Self-checks for `FuelBelow` boundaries.

### Task 6 [x]: Self-defence leash without the radius setting
**Files:** `AirCommand/CommanderAirCommandPilotHooks.cs` (`SelfDefenceEngages`, `TargetOutsideSelfDefence`, the loop), `AirCommand/CommanderAirCommandTypes.cs` (`AirMission.SelfDefenceRadiusMeters` → `bool SelfDefenceOnly`), `Operations/CommanderOperationsAirPosture.cs` (`ApplyPosture`, `ClearPosture`, self-checks), `Operations/CommanderOperationsAirRadarWatch.cs`/`AirCommand/CommanderAirCommandOrders.cs` (any other writer of the radius).
The leash becomes: while `SelfDefenceOnly`, a non-commanded target is engaged only if `SelfDefenceEngages(range, closingSpeed, weaponMaxRange)` = closing at ≥ `SelfDefenceClosingSpeedMetersPerSecond` AND within the weapon's max range, OR within `SelfDefenceCloseMeters` (a class constant, 6000, "inside this anything is engaged whatever it is doing — a hostile this close has already chosen the fight"). The setting is gone; the constant keeps the number with its reason. Update the self-checks (they currently pass the radius).

### Task 7 [x]: Split the form-up standoff off the posture ring
**Files:** `Operations/CommanderOperationsAirPackages.cs` (`TryFindFormUpPoint`: `float standoff = CommanderSettings.AirPostureRingMeters;` → `PackageFormUpStandoffMeters`), CHANGELOG wording.
Self-check: `PackageFormUpStandoffMeters > 0`.

### Task 8 [x]: Belt-ahead hold — pure rule
**Files:** `Operations/CommanderOperationsAirPosture.cs`.
`internal static bool BeltHoldsSortie(int airDefenceObserved, bool sweepIn)` → `airDefenceObserved > 0 && !sweepIn`. Self-checks: belt + no sweep holds; belt + sweep does not; no belt does not.

### Task 9 [x]: Belt-ahead hold — runtime
**Files:** `Operations/CommanderOperationsAirPosture.cs` (`WatchAirPosture`), `Operations/CommanderOperationsAirWing.cs` (`CommanderAirSortie`: add `HoldReason` enum field `{ None, Outnumbered, Belt }` replacing the implicit "falling back = outnumbered"; keep `FallingBack` as `HoldReason != None`), `Operations/CommanderOperationsAirAwacs.cs` (reconcile copies `HoldReason`).
In the tick, before the outnumbered test: `airDefence = CountObservedAirDefence(hq, sortie.Center)` within `AirBeltHoldRadiusMeters` (read `CountObservedAirDefence` at `RadarWatch.cs:638` — parameterise its radius if it is fixed); `sweepIn` = any sortie of `state.AirSorties` with `Kind == Arad && GoneIn` or `Kind == Strike && GoneIn` whose `Center` is within the ring. `BeltHoldsSortie` → enter the hold with the existing `BeginFallback` mechanics (fallback point, stamps, `InContact`), `HoldReason = Belt`, `AradWanted = Mathf.Max(AradWanted, HardArad)`, log once `"{Label}: holds — air defence over {objective} ({airDefence} sites) and no sweep in; asks for a sweep."`. Clear when `!BeltHoldsSortie` (log `"{Label}: sweep in; goes in."` or `"…: the belt is gone; goes in."`). The give-up clock applies as for outnumbered. Skip CAP-only sorties with no strike element (a patrol over a belt is the SAM's problem, not ours — document). Marker text: `Falling back (…)` → for `Belt`, `Holding for sweep`.

### Task 10 [x] (REPLACED 2026-09-16): Attrition brake escalates but never holds
**Replaces the original Task 10 below, on a user decision taken mid-execution.** The brake this task
was going to hang "thin the sky" on has been deleted, so the thinning is dropped entirely: there is no
transition into a hold to hang it on any more. See DECISION-039b and design §4 Layer 4.
Delete the hold (`record.Held`, `AttritionHoldsBuy` and its denial line, `AttritionQuietMinutes`, the
quiet release, `AttritionHoldLifted`, `AttritionBraking`), delete the airborne-ceiling halving
(`AttritionCeiling` and its use in `EffectiveAirborneCeiling`), delete the home-CAP baseline exemption
that only worked around the hold, and KEEP the tier escalation whole. "Already flying the best it can
afford" becomes a no-op logged once per window.

### Task 10 (ORIGINAL, withdrawn): Brake thins the sky
**Files:** `Ai/CommanderEnemyCommanderAttrition.cs` (where the hold first begins — the ledger's escalate/hold transition), `Operations/CommanderOperationsAirAwacs.cs` (`ReleaseBoundAirframes`), a small static entry `CommanderOperationsService.NotifyAttritionHold(FactionHQ hq)`.
On the transition INTO a hold (once per window): the operations side releases every sortie that is past `CooldownUntil`, not `InContact`, not a lift cover (`IsLiftCoverLabel`), not the strike package, through `ReleaseBoundAirframes` for Caps and Cas, logging `"attrition: thins the sky — releases {n} quiet sorties."`. Pure `SortieThinnable(inContact, liftCover, strike, cooldownLive)` with self-checks.

### Task 11 [x]: Clocks merged — waits
**Files:** `Operations/CommanderOperationsInsertion.cs:1465` (insertion stall uses `OperationsInsertionStallTimeoutSeconds`), the strike maximum (find `StrikeMax`/"strike … abandoned after" in `Operations/CommanderOperationsAirIdle.cs` / `AirPackages.cs`).
Both call `LiftWaitedTooLong(seconds, minutes)` (convert the stall's seconds setting once at the call). Behaviour-neutral; move the existing boundary self-checks to name the helper.

### Task 12 [-]: Clocks merged — loss cooldowns
**Files:** `Operations/CommanderOperationsInsertion.cs:1246` (`OperationsHeliInsertionCooldownMinutes`), `Operations/CommanderOperationsFob.cs:64` (`FobPointCooldownMinutes`), the strike-point cooldown in `AirPackages.cs`/`AirIdle.cs`.
Each becomes a call to `SortieLossCooldownMinutes(...)` (`Operations/CommanderOperationsAirWing.cs:1256`) parameterised so the numbers are unchanged. If the helper's shape does not fit a caller without forking, STOP and note it in the task result rather than paraphrase — behaviour-neutral or nothing.

### Task 13 [x]: Winchester callers and recovery sweep
**Files:** `AirCommand/CommanderAirCommandRecovery.cs`, `AirCommand/CommanderAirCommandLanding.cs`.
Where the recovery sweep skips aircraft in a landing state that are not `Returning` (audit gap 2), confirm Task 5's landing-state sync makes them visible; if the sweep has its own "is it ours to recover" test, make it read `mission.Returning` only. Note the finding.

### Task 14 [x]: CHANGELOG and decision doc
**Files:** `CHANGELOG.md` `## Unreleased` (one paragraph per layer, naming settings and log lines), `conductor/decision-log.md` DECISION-039 already written — append any deviation.

### Task 15 [x]: Build, install once, verify
`dotnet build` clean throughout; then the ONE hot install. Wait ~15 s; confirm a new `Ground Control (RTS) 0.7.6.0 loaded` line with no `self-check FAILED` and no exception. Watch ~10 min for: `goes home: out of ammo`, `goes home: fuel at`, `the pilot is landing for fuel`, `holds — air defence over`, `sweep in; goes in`, `attrition: thins the sky`. Report which were and were not observed. If the game is not running, say so.

## DAG
1 → 6, 7. 2, 3 independent. 4 → 5. 8 → 9 (9 also needs 6 for the stamps). 10 independent. 11, 12, 13 independent. 14, 15 last.

## Review
One independent review of the full diff (second-opinion `default` panel), one evaluation against design §6, at most two fix cycles.

---

## Execution record

`[x]` done as written, `[-]` stopped under the task's own escape hatch. One executor, 2026-09-16.

### Deviations

**Task 12 — STOPPED under its own escape hatch ("behaviour-neutral or nothing").** The insertion,
FOB and strike-point loss cooldowns are left exactly as they were. `SortieLossCooldownMinutes` is not
a flat wait: its only real behaviour is doubling the base minutes when two or more hostile air-defence
vehicles are observed. The three callers are flat `X minutes` at fourteen call sites. Routing them
through the helper either doubles them over a defended point, which is a gameplay change nobody asked
for, or passes a literal zero air-defence count at every site purely to defeat the helper's only rule,
which shares a name without sharing a rule and reads worse than the arithmetic it replaces. Recorded
as DECISION-039a in `conductor/decision-log.md`.

**Task 11 — half of it stopped, for the same reason.** The strike maximum now calls
`LiftWaitedTooLong` and is behaviour-identical for every configuration the mod accepts: both are
limit-inclusive, and the existing config check "the package's maximum life is positive" already
guarantees the only input on which the two differ. The insertion stall timeout is LEFT AS IT WAS. Its
rule is `secondsNearLandingZone > timeoutSeconds`, exclusive at the boundary, and a named self-check
records that convention deliberately — "a flight exactly on the stall timeout is not yet stuck".
`LiftWaitedTooLong` is limit-inclusive, so the merge would flip a passing named check into a failing
one at load. Also recorded as DECISION-039a.

**Task 13 — no code change needed; the finding is the deliverable.** The recovery sweep's gate is
`mission.Returning && mission.RtbIssued && !mission.Parked && mission.Intent != LandingIntent.Capture`
(`AirCommand/CommanderAirCommandRecovery.cs`). `RtbIssued` is not a duplicate of `Returning`: it means
the landing state has actually been entered, which is a real precondition for expecting the airframe
on a deck. Task 5's landing-state sync therefore sets BOTH fields, exactly as the commanded-landing
path already does (`AirCommand/CommanderAirCommandLanding.cs`, which sets `Returning = true;
RtbIssued = true;` together), so a game-initiated fuel landing becomes visible to the sweep with no
change to the sweep itself. Weakening the gate to read `Returning` alone would make the sweep
consider airframes that have been marked returning but never handed to a landing state, which is a
different bug.

**Task 2 — one note on fidelity.** The game weighs the HIGHEST opportunity any candidate offered
(`num` in the decompiled `ChooseHQTarget`); the mod's chooser keeps the best-SCORING candidate's,
which is never higher. The refusal therefore fires at least as often as the Basegame's and never less
often. Recorded in the comment at the call site.

**Task 9 — one addition beyond the plan.** The reconcile also carries `AradWanted` across for a
sortie in the belt hold, next to the `HoldReason` it now copies. Without it the sizing would hand the
sortie a suppression demand of zero every thirty seconds and the sweep it is holding for would never
be bought, which would have made the hold a five-minute wait ending in a stand-down.

### Self-checks added

All in `CheckSurvival` in `Operations/CommanderOperationsAirPosture.cs`, registered beside
`CheckAirPosture` in `CommanderOperationsService.SelfCheck`. The self-defence checks in
`CheckAirPosture` were updated in place for the new three-argument `SelfDefenceEngages` and the
removed setting, and one was added for the close bubble being a positive class constant.

### Plant-a-defect proofs

Each defect was planted, built (so the check would really have run), and the file restored and
confirmed byte-identical by sha256. The developer was playing, so no hot install was available for
these; which named check fails is argued from the code, and the arithmetic of each case is given
below.

| Gate | Defect planted | Named check that fails |
|---|---|---|
| Bravery refusal | `rangeMeters > max * 2` → `>=` | "a target exactly at twice the weapon's reach is taken": 60000 >= 60000 makes the third clause true, so the call returns true where the check expects false |
| Disengaging | dropped the `RetreatStandoff` term | "a pilot retreating to standoff is disengaging": ordinal 2 no longer matches, returns false where the check expects true |
| Fuel | `fuelLevel <= fraction` → `<` | "a tank exactly on the fraction is low": 0.25 < 0.25 is false where the check expects true |
| Belt hold | dropped the `!sweepIn` term | "a tracked belt with a sweep in does not hold": returns true where the check expects false |
| Thinnable | dropped the `!liftCover` term | "a lift cover is never thinned out": returns true where the check expects false |
### In-game verification (Task 15)

One hot install, 2026-09-16. `Ground Control (RTS) 0.7.6.0 loaded` at line 8832 of
`BepInEx\LogOutput.log`, after `Reloaded all plugins!`. No `self-check FAILED` and no exception in the
~25 minutes watched after it. Counts over that window:

| Expected line | Seen |
|---|---|
| `goes home: out of ammo` | 3 |
| `goes home: fuel at` | 31 |
| `the pilot is landing for fuel` | 10 |
| `holds — air defence over` | 17 |
| `attrition: thins the sky` | 1 |
| `sweep in; goes in` | 0 |
| `the belt is gone; goes in` | 0 |

Neither belt-clear line was observed. The state machine is not stuck — two holds ended on the
five-minute give-up (`not reinforced in 5 min; stands down`) — but the "a sweep went in" exit was
never reachable, because no anti-radiation sortie was opened at all in this session.

**Open finding for the user: the belt hold is more sensitive than the rule that buys the sweep.**
`BeltHoldsSortie` holds on ONE tracked air-defence vehicle, as the design specifies. The suppression
sortie that would clear the hold is opened by the existing belt clustering, which needs
`AradClusterMinimum` (3) clustered air-defence vehicles; the helicopter hesitation uses
`AirDefenceHesitationCount` (2). Every hold observed in this session was over a one- or two-site belt,
so the sweep it asked for could never be opened and the hold always ran to its five-minute stand-down.
As shipped, a thin belt therefore costs a sortie five minutes and then its airframes. Three ways out,
for the user to choose: hold only at the count that opens a sweep; open a suppression sortie whenever
a sortie is held for a belt; or give the belt hold a shorter give-up clock of its own. Not changed
here — it is a threshold decision, not an execution detail.
---

## Amendment, 2026-09-16: Task 10 replaced mid-execution

A user decision arrived after the first install: **"escalate, never hold."** The attrition brake had
frozen both commanders in the running match, and Task 10's "thin the sky" hung on the very transition
that is now deleted, so the thinning is withdrawn in full rather than rehomed.

**What was reverted.** `SortieThinnable` and `NotifyAttritionHold` (added to
`Operations/CommanderOperationsAirAwacs.cs` earlier the same day) are gone, along with their five
self-check cases in `CheckSurvival`. Nothing thins the sky. The first install's
`attrition: thins the sky — releases 2 quiet sorties` line no longer exists.

**The freeze, and why it fed itself.** The air budget is sized as the room left under the airborne
ceiling times the cheapest wanted airframe (`AirFundCeiling`, `Ai/CommanderEnemyCommanderAir.cs`).
The brake halved the ceiling, so there was no room, so the budget collapsed to one airframe
(`air saved 174 (cap 174)` against a balance of 2,376 with a dozen open requests). With one airframe's
budget the best affordable pick is always the cheapest one, so `AttritionEscalates` could never fire,
so the hold branch was taken again every review, and with the wing still dying nothing was ever quiet
for five minutes.

**Deleted.** `AirAttritionSideRecord.Held`; `AttritionHoldsBuy` and its denial line;
`AttritionQuietMinutes`; `ReleaseAttritionHold` and the two calls to it; `AttritionHoldLifted`;
`AttritionBraking` and `AirAttritionLedger.Braking`; `AttritionCeiling` and the halving inside
`EffectiveAirborneCeiling`; the `(attrition brake)` mark on the air-demand diagnostics line; the
home-CAP baseline exemption comment in `Ai/CommanderEnemyCommanderAirBuy.cs`, which existed only to
work around the hold; `LastLossAt`, whose only reader was the quiet release; and the five
`AttritionCeiling` self-checks in `Operations/CommanderOperationsAirWing.cs`.

**Kept whole.** The ledger, the ten-minute window, the per-side launch and loss counts,
`AttritionBleeding`, `MostLostType`, `AttritionSideRating`, `AttritionEscalates` and the
`buying the best affordable … instead of the cheapest` line — the 2026-09-15 request, unchanged, with
every self-check that still applies.

**Changed.** `AttritionBrakesPick` (returned "buy nothing") became `NoteAttritionPick` (returns
nothing and only writes the line); both call sites now read `if (bleeding) NoteAttritionPick(…)`. When
the best affordable airframe is no better than the type that is dying, the commander logs
`already flying the best it can afford (<type>) — buying on` once per window, on a new
`BestAffordableReported` flag so it does not collide with the escalation line, and buys.

**New self-check and its plant-a-defect proof.** `AttritionBuysOn(bleeding, anythingBetterAffordable)`
is a named rule that always returns true, so the regression cannot come back by accident. Three cases
in `CheckAirAttrition`. Defect planted: `return !bleeding || anythingBetterAffordable;`. It compiles,
and it makes `AttritionBuysOn(true, false)` return false, which fails the named check **"a bleeding
side with nothing better affordable STILL buys"**. Restored and confirmed byte-identical by sha256.
Argued from code rather than observed, as with the other five proofs.
### In-game verification of the amendment (second install, 2026-09-16)

A second hot install was taken for this, on the team lead's instruction, because the developer was
waiting to see the commander buying again. `Ground Control (RTS) 0.7.6.0 loaded` at line 13024 of
`BepInEx\LogOutput.log`. No `self-check FAILED` and no exception in the ~15 minutes watched after it.

The freeze is gone, and the exact case that caused it now buys. Observed:

```
Enemy commander (Primeva) air attrition: 7 of 15 fighters launched in the last 10 min were lost;
  already flying the best it can afford (FS-12 Revoker) — buying on.
Enemy commander (Primeva) air attrition eased: 5 of 15 fighters launched in the last 10 min were lost;
  back to the cheapest in the tier when quiet.
```

Three "buying on" lines across two commanders, one per crossing, so the once-per-window flag works;
zero `buys are held`, zero `(attrition brake)` marks, zero `thins the sky`.

The budget is the proof that the ceiling halving is gone. Before the fix the log read
`air saved 174 (cap 174)` against a balance of 2,376. After it, with both sides bleeding, the caps
read 283, 305, 349 and 390, and the commanders spent through the window on platoons, pickets,
buildings, escorts and repeated fighter launches.

The `buying the best affordable … instead of the cheapest` escalation line was NOT observed in this
window: on this map's roster the fighter tier's best affordable airframe was the type already dying,
so every crossing took the other branch. Its rule and its self-checks are unchanged from 2026-09-15.
---

## Amendment 2, 2026-09-16: the thin-belt hold fixed (follow-up task on the open finding)

The open finding above was decided by the user — **"only hold for a belt worth sweeping"** — and fixed
as a follow-up task on this track. Confirmed evidence from the running log: twelve belt holds since
the reload, five over one site and seven over two, none of which could ever clear, and two sorties run
to `not reinforced in 5 min; stands down`.

**One rule, two callers.** `BeltWorthSuppressing(clusteredLaunchers, clusterMinimum)` is a new pure
predicate in `Operations/CommanderOperationsAirArad.cs` — the belt's own file, where the test it
replaces was written inline. `AradWanted`, which sizes the suppression sortie, now reads it (pointer
comment left at that site), and so does `BeltHoldsSortie`. The hold can no longer ask a weaker
question than the sweep.

**The honest version, and it was cheaper.** The hold counts CLUSTERED launchers, not a raw total in a
ring: `RefreshPostureBelts` runs the same `CollectTrackedAirDefence` plus `BuildAirDefenceClusters`
pass the suppression demand makes, ONCE per commander at the top of the posture watch, and
`LargestBeltNear` reads the biggest cluster with a launcher inside the hold ring. That replaces one
tracking-database walk PER SORTIE per five seconds with one walk plus one clustering pass per
commander, so it costs less than what it replaces rather than more. No duplicated clustering.

**Reverted.** `CountObservedAirDefence` had an optional radius parameter added for the hold earlier
the same day; nothing passes it now, so it is back to the plain sizing-ring count the loss cooldown
and the review line want.

**Self-checks.** The belt cases were rewritten against the threshold: a belt at the cluster minimum
holds, one below it does not, a single launcher never holds, and a belt at the minimum with a sweep in
does not hold. One further check loops the counts 0 to 8 and pins `BeltHoldsSortie` to
`AradWanted > 0` at every one of them, so the hold's threshold and the sweep's cannot drift apart
again without a named failure at load.

**Plant-a-defect proof.** Defect: `BeltWorthSuppressing` reduced to `clusteredLaunchers >= 1`, which is
exactly the bug being fixed. It compiles, and it fails the named checks **"two launchers are below the
minimum and do not hold"**, **"a single tracked launcher never holds: the pilot's own threat dodging
answers it"**, and the paired-threshold loop at counts 1 and 2. Restored and confirmed byte-identical
by sha256. Argued from code, as with the other proofs.
### In-game verification of amendment 2 — NOT DONE

The install was taken (`build-and-install.ps1 -Dev`, 09:08) but **the game had already been closed**.
The log's last lines are `RTS mode disabled` and `[NOSMR] Unloaded` at 09:07, one minute before the
copy landed, and no Nuclear Option process is running. No reload line, so none of this amendment has
been seen in the running game: the belt fix, the self-checks and the plant-a-defect proof are all
verified by build and by code only.

The build is clean and the files are correctly placed — `GroundControlRts.dll` and `.pdb` in
`BepInEx\scripts\` and nothing of the mod in `BepInEx\plugins\` — so the next launch will load this
version. What to watch for then:

- `holds — air defence over the objective (3 sites)` or larger, and NO holds at 1 or 2 sites.
- `sweep in; goes in`, which has never been observed and is the exit this change makes reachable.
- No `not reinforced in 5 min; stands down` following a belt hold.
- No `self-check FAILED` at load, which now includes the nine-case paired-threshold loop.
### Where the belt fix actually is (line numbers, 2026-09-16 09:10)

The change moved the rule, so its old line numbers no longer point at it. Current positions:

| Thing | File | Line |
|---|---|---|
| `BeltWorthSuppressing` — the one rule | `Operations/CommanderOperationsAirArad.cs` | 60 |
| its pre-filter caller | `Operations/CommanderOperationsAirArad.cs` | 82 |
| `AradWanted` reads it | `Operations/CommanderOperationsAirWing.cs` | 641 |
| `BeltHoldsSortie` reads it | `Operations/CommanderOperationsAirPosture.cs` | 74 |
| `RefreshPostureBelts` / `LargestBeltNear` | `Operations/CommanderOperationsAirPosture.cs` | 91 / 104 |
| the watch clusters once per commander | `Operations/CommanderOperationsAirPosture.cs` | 390 |
| the hold reads the clustered count | `Operations/CommanderOperationsAirPosture.cs` | 424-428 |
| the rewritten belt self-checks | `Operations/CommanderOperationsAirPosture.cs` | 845-861 |

Rebuilt in Release and reinstalled at 09:10 so the file in `BepInEx\scripts\` matches this source;
`0 Warning(s)`, `0 Error(s)`. Nothing of the mod is in `BepInEx\plugins\`.