# Strike Packages, Role-Fit Types and CAP Altitude Bands — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use orchestrator-supaconductor:executing-plans to implement this plan task-by-task.

**Goal:** Give both AI commanders deliberate multi-type strike packages against enemy-held control points, choose airframe types by the element's role instead of "cheapest in tier", and vary CAP station altitude.

**Architecture:** One new sortie kind (`Strike`) inside the existing operations air wing, fed by the offensive planner and a strike clock; the existing package machinery (form-up, go-in, one type per element) carries it; the buy's tier picker gains an element kind and a diversity cap; CAP sorties carry a station band that becomes the air mission's target altitude. No new service.

**Tech Stack:** C# net472, BepInEx 5 / Harmony, Unity. Verification = `SelfCheck` cases run at plugin load (log `self-check FAILED`) plus the running game's `BepInEx\LogOutput.log`.

**Rules for the executor (from `.claude/CLAUDE.md`):** read the code named under Reuse in `design.md` before touching it; constants at class level with a `<summary>` saying what the number means and why; every threshold gets a self-check case beside the existing ones (`Expect(failures, name, actual, expected)` helpers in `Operations/CommanderOperationsService.cs`; the enemy air checks in `Ai/CommanderEnemyCommanderAirChecks.cs` use their own `Expect`); no aircraft names in code; build with `$env:NUCLEAR_OPTION_DIR = "I:\SteamLibrary\steamapps\common\Nuclear Option"; dotnet build .\GroundControlRts.csproj -c Release -nologo -v q` and require 0 warnings 0 errors after every task; do NOT install (`build-and-install.ps1`) — the lead installs; do not commit. Settings go in `Core/CommanderSettings.cs` (Operations section, `Get`/`Set` pair + warm-up line). CHANGELOG entry under `## Unreleased` at the end. Preserve each file's BOM and CRLF line endings.

---

## Phase A — the Strike sortie

### Task 1: The sortie kind and the strike record
**Done (2026-09-15).** `CommanderSortieKind.Strike`, `CommanderStrikeScale` and the strike fields on `CommanderAirSortie` in `Operations/CommanderOperationsAirWing.cs`; the strike clock, cooldown table and CAP band cursor on `OperationsState`.

**Files:** Modify `Operations/CommanderOperationsAirWing.cs` (`CommanderSortieKind`, `CommanderAirSortie`), `Operations/CommanderOperationsService.cs` (`OperationsState`).
- Add `CommanderSortieKind.Strike` with a `<summary>`.
- On `CommanderAirSortie` add: `StrikeWanted`, `EscortWanted`, `AradWanted`, `BomberWanted` (int), `StrikeScale` (enum Light/Defended/Hard), `DefendersAtOrder` (int), `OpenedAt`, `WentInAt`, `Delivered` (bool: first strike airframe inside the target ring after go-in), `TargetAirbase` (Airbase?), and the target `Point` if not already there.
- On `OperationsState` add: `StrikeClockMinutes` (float), `LastStrikeAt` (float, -1), `StrikeCooldownUntil` (Dictionary<CommanderStrategicPoint, float>), `CapBandCursor` (int).
- Verification: builds clean; no behaviour change yet.

### Task 2: Package shape by target scale (pure + self-check)
**Done (2026-09-15).** `StrikePackage` and `StrikePackageFor` in `Operations/CommanderOperationsAirPackages.cs`, with `CheckStrikePackages` called from `CheckAirSupport`.

**Files:** Modify `Operations/CommanderOperationsAirPackages.cs`.
- Add `internal readonly struct StrikePackage { int Strike, Escort, Arad, Bomber; StrikeScale Scale; }` and pure `internal static StrikePackage StrikePackageFor(int defenders, bool airDefence, int hostileAir, bool isBase, bool hasBomber)` implementing design Section 2's table exactly, with the escort floor `Mathf.Max(baseEscort, hostileAir)`. Constants: `StrikeElementSize` 2, `DefendedEscort` 2, `HardArad` 1, `BaseBomberMin` 1, `BaseBomberMax` 2, `LightDefendersMax` 2, each with a `<summary>`.
- Self-check (in `CheckAirSupport` or a new `CheckStrikePackages` called from it): light with 0 defenders → 2/0/0/0; light with 1 hostile air → escort 1; defended 3 defenders → 2/2/0/0; defended with 4 hostile air → escort 4; hard with air defence → 2/2/1/0; base without bomber → 2/2/1/0; base with bomber → bomber 1..2; escort never below hostile air in every row.

### Task 3: Strike clock and target choice (pure + self-check)
**Done (2026-09-15).** `StrikeDue`, `StrikeTargetBeats`, `TryChooseStrikeTarget` and the nine settings; `CheckStrikeClock`.

**Files:** Modify `Operations/CommanderOperationsOffensive.cs` (beside `UpdatePressure`), `Core/CommanderSettings.cs`.
- Settings: `StrikeIntervalMinutes` 6, `StrikePointCooldownMinutes` 10, `StrikeRangeMeters` 80000 (`Get("Operations", ...)`, warm-up lines, `<summary>` comments explaining the numbers as in design Section 6).
- Pure `internal static bool StrikeDue(float minutesSinceLast, float intervalMinutes, bool attackOpen)` (never due while an attack is open; due at or past the interval; a negative "since" means never struck → due). Pure `internal static bool StrikeTargetBeats(float value, float bestValue)`.
- `private bool TryChooseStrikeTarget(FactionHQ hq, OperationsState state, out CommanderStrategicPoint? point)`: enemy-held (`GetOwner()` is another HQ) ranked points (`state.RankedPoints`, value from `RankPoints`) within `StrikeRangeMeters` of a held airbase that can launch a Strike-tier airframe (reuse `TryFindInsertionLaunchBase`-style nearest held base + `CommanderAirCommandService.IsCompatibleAirbase`), not in `StrikeCooldownUntil`.
- Self-check: due at 6 min, not at 5.9, never while an attack is open, due when never struck.

### Task 4: Opening a Strike sortie from both sources
**Done (2026-09-15).** `OpenStrikeSortie` called from both `TryOpenAttack` and `UpdateStrikeClock`; `UpdateStrikeClock` runs straight after `UpdatePressure` in `Review`.

**Files:** Modify `Operations/CommanderOperationsOffensive.cs` (`TryOpenAttack`, new `UpdateStrikeClock`), `Operations/CommanderOperationsAirDemand.cs` (the demand walk that builds sorties each review), `Operations/CommanderOperationsService.cs` (`Review` pipeline: call `UpdateStrikeClock` right after `UpdatePressure`).
- Read the target once: `defenders = EffectiveObserved(CountObserved(...))`, `airDefence` = tracked air-defence within the anti-radar cluster distance (reuse the ARAD cluster read in `CommanderOperationsAirArad.cs`), `hostileAir` = tracked hostile aircraft within the threat radius of the target (reuse the read `TransportEscortWanted`'s caller uses), `isBase`, `hasBomber` (Task 8's roster query). Fill the sortie from `StrikePackageFor`.
- `TryOpenAttack` opens the Strike sortie on its target after the attack mission is added. `UpdateStrikeClock` steps `StrikeClockMinutes` per review like `StepPressure` and opens one when `StrikeDue` and no Strike sortie is open.
- One open Strike sortie per commander; a point struck takes `StrikePointCooldownMinutes` when the sortie ends.
- Log: `orders a strike on <point> (<scale>: N defenders, M hostile air): <types once bought>`; `strike clock: next deliberate strike in N min` only when it changes by a whole minute.
- Verification: build; log lines appear in the game (lead verifies).

### Task 5: Demand and binding for the Strike sortie
**Done (2026-09-15).** `AddStrikeDemand` posts the sortie object itself; suppression serves a strike package in `InsertAradDemand`; the reconcile guards the same-object match; the review line gained a `STRIKE` segment.

**Files:** Modify `Operations/CommanderOperationsAirDemand.cs`, `Operations/CommanderOperationsAirPlatoonCap.cs` (escort demand), `Operations/CommanderOperationsAirArad.cs`.
- A Strike sortie posts demand: `StrikeWanted` as `CommanderAirDemandKind.Cas`-class demand flagged as a strike element (add an element kind field on the demand entry: Strike / Escort / Arad / Bomber / Cap / Other), `EscortWanted` via `AddEscortDemand` (the existing escort path, minimum from the package not the platoon rule), `AradWanted` through the ARAD demand with `AradPending` set on the strike sortie, `BomberWanted` as a strike-element demand flagged Bomber.
- Binding: an owned airframe fills the element whose role it can fly (reuse `MayFillRole`).
- Verification: review line prints `STRIKE <point> S 0/2 E 0/2 A 0/1` beside the other sorties.

### Task 6: Running the package: form-up, ARAD first, go-in, loiter, end
**Done (2026-09-15).** `UpdateStrikePackage` and `CloseFinishedStrike` in `Operations/CommanderOperationsAirPackages.cs`; `StrikeEnds` and `EscortReleases` with `CheckStrikeRun`; a strike package is never a retask source.

**Files:** Modify `Operations/CommanderOperationsAirPackages.cs`, `Operations/CommanderOperationsAirRetask.cs` (a Strike sortie is never a retask source), `Core/CommanderSettings.cs` (`StrikeLoiterMinutes` 4, `StrikeSortieMaxMinutes` 12).
- Form-up point and go-in rule are the existing package rules; the anti-radar element goes in one review before the rest (reuse `AradPending`).
- After go-in: strike and bomber elements are tasked on the target ring with the air-command strike mode (`TryTaskAiAircraft(aircraft, mode, center, radius, retaskExisting: true)` as `CommanderOperationsAirRadarWatch.cs:423` does); escorts hold CAP over the target; `Delivered` becomes true when the first strike airframe is inside the target ring after go-in.
- End: strike element spent or lost, or `StrikeSortieMaxMinutes` since `OpenedAt`; escorts released to the idle sweep after `StrikeLoiterMinutes` past `WentInAt`. On end: `LastStrikeAt = Time.time`, cooldown on the point, log `strike on <point> done: N defenders destroyed` (defenders at order minus `CountObserved` now, floored at 0) or `strike on <point> abandoned: <reason>`.
- Pure + self-check: `StrikeEnds(strikeAlive, minutesOpen, maxMinutes)`, `EscortReleases(minutesSinceGoIn, loiterMinutes)`.

### Task 7: The attack waits for the strike
**Done (2026-09-15).** `AttackMayGoIn` and the rewritten go-in hold in `UpdateAttacks`, with `StrikeHoldLogged` on the mission. Self-check cases live in `CheckStrikeClock`.

**Files:** Modify `Operations/CommanderOperationsOffensive.cs` (`UpdateAttacks` go-in hold).
- The attack's go-in hold reads "strike delivered" (the Strike sortie on this target has `Delivered`) OR the existing 240 s form-up timeout, whichever first; an attack whose Strike sortie was abandoned goes in on the timeout as today. Pure `AttackMayGoIn(strikeDelivered, strikeAbandoned, secondsForming, timeoutSeconds)` + self-check (delivered → yes; abandoned → only on timeout; neither → only on timeout).
- Log: `<attack label>: goes in behind the strike` / `goes in after 240 s without the strike`.

## Phase B — role-fit types and the diversity cap

### Task 8: Element kind reaches the tier picker
**Done (2026-09-15).** `ElementKind`, `IsBomberAirframe`, `HasBomberCandidate`, `PreferForElement` and `EscortTier` in `Ai/CommanderEnemyCommanderAir.cs`; the element kind threaded from `TryGetAirDemand` through `TryBuyRole` and `BuyHomeCapFighter`; `CheckElementFit`.

**Files:** Modify `Ai/CommanderEnemyCommanderAir.cs` (`AirframeCandidate`, `SelectInTier`, `BetterInTier`), `Ai/CommanderEnemyCommanderAirBuy.cs` (`TryBuyRole`, `PackageElementBuys` callers), `Ai/CommanderEnemyCommanderAirCatalog.cs`.
- Add `internal enum ElementKind { Other, HighCap, Escort, Strike, Bomber }` and carry it from the demand entry (Task 5) through `TryBuyRole` to `SelectInTier(candidates, tier, threatHigh, allocation, elementKind, shareOfType)`.
- `AirframeCandidate` gains `AntiAir`, `AntiSurface` (from `definition.roleIdentity`) and `IsBomberClass` (prefab key contains "Bomber", read where the roster line reads the key; no aircraft names).
- Pure `internal static bool PreferForElement(ElementKind kind, AirframeCandidate a, AirframeCandidate b, bool threatHigh)`: HighCap → higher `AntiAir / Price`; Escort → Multirole tier over Fighter tier, then `BetterInTier`; Strike → higher `AntiSurface`; Bomber → `IsBomberClass` first, then `AntiSurface`; Other → today's `BetterInTier`. Escort tier choice: `HighestLaunchableTier` for Escort prefers Multirole when launchable and affordable, else Fighter.
- `hasBomber` roster query for Task 4: any launchable Strike-tier candidate with `IsBomberClass`.
- Self-check (`CheckAirBuyRules`): HighCap picks the better anti-air-per-cost of two Fighter-tier candidates; Escort picks the Multirole-tier candidate over a cheaper Fighter; Strike picks higher anti-surface; Bomber picks the bomber class; Other unchanged (existing cases still pass).

### Task 9: The diversity cap
**Done (2026-09-15).** `TypeShareExceeded` and `TypeShareSampleFloor`; `AirframeCandidate.ShareExceeded` with the two-pass skip in `SelectInTier`; `CountAirborneOfType` and the diversity note on the launch line.

**Files:** Modify `Ai/CommanderEnemyCommanderAir.cs`, `Ai/CommanderEnemyCommanderAirBuy.cs`, `Core/CommanderSettings.cs` (`TypeShareCap` 0.6).
- Pure `internal static bool TypeShareExceeded(int countOfType, int sideTotal, float cap)` (false when `sideTotal < 3` so a first buy is never blocked). Before the pick, count the side's airborne airframes per type (the attrition ledger already knows the side; `CountAirborne` knows the airframes); a candidate whose type exceeds the cap is skipped when another launchable, affordable candidate exists in the tier.
- Log note on the launch line: `(<tier>, <preference word>: escort multirole preferred)` / `(diversity: FS-12 Revoker is 70 percent of the fighters; buying FS-20 Vortex)` — types named in the log only.
- Self-check: 3 of 4 exceeds at 0.6; 2 of 4 does not; 1 of 1 does not (small sample).

### Task 10: CAP altitude bands
**Done (2026-09-15).** `NextCapBand`, `CapBandMeters`, `EscortCapBand`, `AssignCapBand` and `HomeCapAltitude`; `TryTaskAiAircraft` and `AirMission.TargetAltitude` carry the band; `CheckCapBands`.

**Files:** Modify `Operations/CommanderOperationsAirWing.cs` (`CommanderAirSortie.CapBandMeters`), `Operations/CommanderOperationsAirDemand.cs` (band assigned when a CAP sortie or escort element is opened), the CAP tasking site (`TryTaskAiAircraft` call for CAP — find it in `Operations/CommanderOperationsAirHomeCap.cs` / `AirPlatoonCap.cs` and pass the band as the mission's target altitude; extend `TryTaskAiAircraft` with an optional `targetAltitude` that flows into `new AirMission(...)`'s altitude argument), `Core/CommanderSettings.cs` (`CapBandLowMeters` 1500, `CapBandMidMeters` 4000, `CapBandHighMeters` 7500), `Operations/CommanderOperationsDiagnostics.cs` (review line `CAP 2/2 FS-20 @4000`).
- Pure `internal static int NextCapBand(int previous, int bands)` rotating 0→1→2→0; `internal static float CapBandMeters(int band, float low, float mid, float high)`; escorts take the strike element's band plus one step (clamped). Self-check: rotation wraps; band metres map; escort step clamps at high.

## Phase C — visibility and docs

### Task 11: Markers
**Done (2026-09-15).** `STRIKE` in `AirMarkerKind`, `strike` in `AirMarkerElementRole`, `ENEMY STRIKE` in `EnemySortieLabel`, each with a self-check case.

**Files:** Modify `Operations/CommanderOperationsAirMarkers.cs` (`ClassifyAirframe`, labels).
- `STRIKE <point> — forming n/N`, `STRIKE <point> — inbound`, `ESCORT <point>`, `BOMBER <point>`; anti-radar keeps `ARAD <point>`. Reuse the existing sortie label path; no new marker renderer.

### Task 12: CHANGELOG, decision log impact, settings docs
**Done (2026-09-15).** CHANGELOG entry under `## Unreleased`, DECISION-031 impact list, setting comments.

**Files:** Modify `CHANGELOG.md` (`## Unreleased`, full sentences naming every new key and log line), `conductor/decision-log.md` (append the Impact file list to DECISION-031), `Core/CommanderSettings.cs` comments.

### Task 13: Full rebuild and self-check dry run
**Done (2026-09-15).** Full `--no-incremental` rebuild: 0 warnings, 0 errors.

- `dotnet build ... --no-incremental` → 0 warnings 0 errors. List every new self-check name in the final report so the lead can grep `self-check FAILED` after install. Report the exact log lines to watch for in the game.

## Verification in the running game (lead)
Load `Ground Control Duel Far`; within twenty minutes expect: `orders a strike on …` with a mixed package; escorts ≥ tracked hostile air; `strike on … goes in`; `STRIKE`/`ESCORT`/`BOMBER` markers; CAP review entries showing three bands; at least two fighter types and two strike types launched; `LogOutput.log` free of `self-check FAILED` and `Exception`.

## DAG
A1 → A2 → A3 → A4 → A5 → A6 → A7; B8 → B9; B10 independent of A after A1; C11 after A6; C12, C13 last. One executor runs it in order.
