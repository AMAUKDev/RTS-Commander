# Reach and Points Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use orchestrator-supaconductor:executing-plans to implement this plan task-by-task.

**Goal:** Fewer, reachable control points; ground staffing only within 20 km of an owned vehicle depot; FOBs sited where they extend that reach; bought vehicles spawn at the depot nearest their objective.

**Architecture:** Four seams, each already in the code: the discovery drop pass (validity), the review's point ranking and planners (reach), the FOB site picker (reach-extending FOB), and the AI buy tail (nearest-depot spawn through the depot's own spawn call). Every rule is a pure static function with `SelfCheck` cases; live code only reads inputs and calls it.

**Tech Stack:** C# net472, BepInEx 5, Harmony (no new patches), Unity. Build: `$env:NUCLEAR_OPTION_DIR = "I:\SteamLibrary\steamapps\common\Nuclear Option"; dotnet build .\GroundControlRts.csproj -c Release -nologo -v q` must end `0 Warning(s) 0 Error(s)`. There is no test project: the failing test is a `SelfCheck` case that logs `self-check FAILED` at plugin load; "run" means build, hot-install with `.\build-and-install.ps1 -Dev`, and read `BepInEx\LogOutput.log`. For a pure rule, plant a defect, watch the NAMED case fail in the log, restore byte-identical (Testing rule 5).

**Rules for the executor:** Read `.claude/CLAUDE.md` first. Constants with `<summary>` saying what the number is and why. Do not create `check.mjs`, `_new_block.txt`, `_old_block.txt`. Do not run `build-and-install.ps1` unless the orchestrator says the game is up; the orchestrator installs. Commit only when the user asks.

---

## Task 1: Validity rule (pure) + settings

**Files:**
- Modify: `Core/CommanderSettings.cs` (Points section near line 196–230; warm-up list near 633)
- Modify: `Points/CommanderStrategicPointDiscovery.cs` (self-check near line 1440)

**Step 1: Add settings.** After `PointsPointMinSpacingMeters` add `PointsMaxRoadDistanceMeters` (`Points`, `PointMaxRoadDistanceMeters`, 2000f) with a comment: a control point farther than this from any road cannot be reached by a platoon or road picket, so it is dead weight (user decision 2026-09-14). Change `MaxControlPoints` default 120 → 48 and `ControlPointSpacingMeters` 800f → 3000f, and RENAME both keys (`MaxControlPoints48`? no — use `ControlPointCap` and `ControlPointMinSpacingMeters`) with a comment saying BepInEx keeps saved values over changed defaults, which is why the key changed. Touch all three in the warm-up list.

**Step 2: Write the failing self-check cases** in `CommanderStrategicPointDiscovery.SelfCheck` (beside the `IsGroundLevelEnough` cases):
```csharp
Expect(failures, "a wooded hilltop is invalid", PointIsValid(StrategicPointKind.Hilltop, true, 100f, 2000f), false);
Expect(failures, "an open hilltop 3 km from a road is invalid", PointIsValid(StrategicPointKind.Hilltop, false, 3000f, 2000f), false);
Expect(failures, "an open hilltop at the road limit is valid", PointIsValid(StrategicPointKind.Hilltop, false, 2000f, 2000f), true);
Expect(failures, "a wooded resource site is invalid too", PointIsValid(StrategicPointKind.Site, true, 100f, 2000f), false);
Expect(failures, "a base is never dropped", PointIsValid(StrategicPointKind.Base, true, 9000f, 2000f), true);
```
Build: fails to compile (`PointIsValid` missing) — that IS the failing test.

**Step 3: Implement**
```csharp
/// <summary>Whether a discovered point is worth keeping, pure (user decision 2026-09-14): not
/// woodland, and no farther than <paramref name="maxRoadMeters"/> from a road. A base is always kept —
/// it is the map's own, not the mod's to drop.</summary>
internal static bool PointIsValid(StrategicPointKind kind, bool wooded, float roadDistanceMeters, float maxRoadMeters)
{
    return kind == StrategicPointKind.Base || (!wooded && roadDistanceMeters <= maxRoadMeters);
}
```
**Step 4:** Build clean. **Step 5:** Defect-proof: change `<=` to `<`, build, install, see `an open hilltop at the road limit is valid` FAIL in the log; restore; confirm `git diff` byte-identical for that line.

## Task 2: Apply validity in discovery + log lines

**Files:** `Points/CommanderStrategicPointDiscovery.cs` — the woodland stamp (line ~1310–1340) and the drop/summary lines (~1380–1395).

**Step 1:** In the pass that stamps `point.Wooded`, after stamping, evaluate `PointIsValid(point.Kind, point.Wooded, service.NearestRoadDistanceMeters(point.Position), CommanderSettings.PointsMaxRoadDistanceMeters)`. Invalid points are removed from the point list BEFORE the caps/spacing pass runs (move the stamp earlier if it currently runs after caps; the design needs the cap to choose among valid points). Count `droppedWooded`, `droppedOffRoad`.
**Step 2:** Log per drop: `Strategic point dropped: HILLTOP 12: wooded` / `Strategic point dropped: HILLTOP 12: 3400 m from the nearest road` (reuse the existing `Strategic point dropped:` prefix). Summary line gains `dropped {droppedWooded} wooded, {droppedOffRoad} off-road;`.
**Step 3:** Build clean. Verification in game: discovery line count ≈ 48 with the two drop counts.

## Task 3: Income retune

**Files:** `Core/CommanderSettings.cs` lines 215–230.
Change defaults: Base 30→90, Village 10→30, Crossroads 10→30, Hilltop 5→15, Outpost 5→15, Roadside 3→9. RENAME each key with a `3x` suffix? No: rename to `BaseIncomePerMinuteV2` etc. is ugly; use `IncomeBasePerMinute`, `IncomeVillagePerMinute`, … (one consistent new prefix) and say why in a comment. Warm-up list updated. The existing income-ladder self-check (`Points/CommanderStrategicPointService.cs:~1090`) must still pass: base > village = crossroads > hilltop = outpost > roadside > 0. Build clean; install; no `income ladder` FAILED line.

## Task 4: Depot enumeration helper

**Files:** `Economy/CommanderFobBuilder.cs` (near `depotOwners`, line ~236–300).
Add `internal static void CollectOwnedDepots(FactionHQ hq, List<VehicleDepot> into)`: every `VehicleDepot` in `depotOwners` whose owner is `hq`, not disabled. Falls back (list empty) to nothing — callers handle "no depot" per design §2 (airbase centres). Reuse `depotOwners`; do not `FindObjectsOfType` per call. Also `internal static bool TryNearestOwnedDepot(FactionHQ hq, GlobalPosition near, out VehicleDepot depot, out float meters)`. Build clean.

## Task 5: Reach rule (pure) + state

**Files:** `Operations/CommanderOperationsService.cs` (`OperationsState`, near `FobDenials`), `Operations/CommanderOperationsFront.cs` (self-check `CheckForwardBaseShare` region), `Core/CommanderSettings.cs` (`Operations` section: `DepotReachMeters` 20000f + warm-up).

**Step 1 failing cases:**
```csharp
Expect(failures, "a point at the depot reach is in reach", IsWithinDepotReach(20000f, 20000f), true);
Expect(failures, "a point one metre past the depot reach is out of reach", IsWithinDepotReach(20001f, 20000f), false);
Expect(failures, "no depot at all puts every point out of reach", IsWithinDepotReach(float.MaxValue, 20000f), false);
```
**Step 2 implement:** `internal static bool IsWithinDepotReach(float nearestDepotMeters, float reachMeters) => nearestDepotMeters <= reachMeters;` with `<summary>` citing the user's "only spawn units for objectives closer than some km". Add `internal readonly HashSet<CommanderStrategicPoint> OutOfReach = new();` to `OperationsState`.
**Step 3:** New private `RefreshReach(hq, state)` called in the review right after `RankPoints` (find the call in `CommanderOperationsService` review pipeline): clear the set; collect owned depots (Task 4); if none, use each held airbase centre; for every ranked point compute nearest depot distance; add to `OutOfReach` when not within reach. **Step 4:** Build clean; defect-proof the `<=`.

## Task 6: Planners honour reach

**Files:** `Operations/CommanderOperationsFront.cs` — `PlanForwardBases` (~line 538 gate `IsHeldOrReachable`), `PlanPickets` (~1629), `FillPickets`/`FillOnePicket`; `Operations/CommanderOperationsInsertion.cs` — `IsAirDeliveredPicket` (~742).

- `PlanForwardBases`: skip a point in `state.OutOfReach` (`continue`), beside the `IsHeldOrReachable` gate.
- `PlanPickets`: still create the picket mission for an out-of-reach point (air needs a target).
- `IsAirDeliveredPicket(state, point)`: return true when `state.OutOfReach.Contains(point)` as well as the existing off-road rule, so `FillPickets` never draws pool vehicles for it and the requisition posts nothing (already the behaviour for air-delivered pickets).
- Review line (`Operations/CommanderOperationsDiagnostics.cs`): append ` reach={ranked - outOfReach}/{ranked}` after `fob=`.
Build clean. Verification: no `ForwardBase`/road picket for a point beyond 20 km of a depot; `reach=` present.

## Task 7: FOB siting by reach (pure score)

**Files:** `Operations/CommanderOperationsFob.cs` (`TryPickFobSite` ~409, `FobSiteQualifies` ~241, `FobCapAllowsOrder` ~267, `ReportNoFobSite`, self-check ~1370+), `Core/CommanderSettings.cs` (`FobMinDepotDistanceMeters` 10000f new; DELETE `FobMaxPerCommander` and `FobMinBaseDistanceMeters` with their warm-ups).

**Step 1 failing cases:**
```csharp
Expect(failures, "a site bringing three points in reach beats one bringing two", FobSiteBeats(3, 5000f, 2, 1000f), true);
Expect(failures, "equal reach gain goes to the site nearer the enemy", FobSiteBeats(2, 1000f, 2, 5000f), true);
Expect(failures, "a site too near an owned depot is skipped", FobSiteAllowed(9999f, 10000f, 25000f, 20000f), false);
Expect(failures, "a site inside the FOB spacing is skipped", FobSiteAllowed(12000f, 10000f, 19000f, 20000f), false);
Expect(failures, "a site clear of depots and FOBs is allowed", FobSiteAllowed(12000f, 10000f, 25000f, 20000f), true);
Expect(failures, "nothing out of reach means no FOB", CountBroughtInReach(new List<float>(), 20000f), 0);
```
**Step 2 implement:** `FobSiteAllowed(nearestDepotMeters, minDepotMeters, nearestFobMeters, minFobSpacing)`; `CountBroughtInReach(IReadOnlyList<float> distancesFromCandidateToOutOfReachPoints, reachMeters)`; `FobSiteBeats(gainA, enemyDistA, gainB, enemyDistB)`. Rewrite `TryPickFobSite`: loss cooldown → walk held points → skip in-contact / point cooldown / `!FobSiteAllowed` → score → best. No cap check. Reasons through `ReportNoFobSite` (`every point is within reach`, `no held point is 10 km from an owned depot`, …). Order line: `orders a FOB at {label} by {air|road}: brings {n} points within reach, depot + radar + helipad for {cost}`. Remove `FobCapAllowsOrder` and its cases; `DescribeFob` prints `{online} online`. `WantsFob` uses the same walk (it already calls `TryPickFobSite`).
**Step 3:** Build clean; defect-proof `FobSiteBeats` tie rule.

## Task 8: Order-line objective lookup

**Files:** `Operations/CommanderOperationsRequisitions.cs` (near `FillOldestRequisition` ~106).
Add `internal static bool TryGetOldestRequisitionObjective(FactionHQ hq, CommanderPlatoonRole role, out GlobalPosition objective)`: the same "oldest open line for this role" walk `FillOldestRequisition` makes (extract the selection into one private helper both call — Reuse rule 5), returning its mission's `Point.Position`, or the reserve's territory centre for a reserve line. Build clean.

## Task 9: Spawn at the nearest depot

**Files:** `Ai/CommanderEnemyCommanderService.cs` (buy tail ~line 820–835: `hq.AddFunds(-cost); hq.ModifyUnitSupply(choice, 1);`).

**Step 1 failing case** (in `CommanderEnemyCommanderService.SelfCheck`): pure `SpawnDepotPick(IReadOnlyList<float> depotDistances, IReadOnlyList<bool> depotUsable)` → index of the nearest usable, -1 if none. Cases: nearer wins; disabled skipped; none → -1.
**Step 2 implement** the tail:
```csharp
GlobalPosition objective = role != null
    && CommanderOperationsService.TryGetOldestRequisitionObjective(hq, role.Value, out GlobalPosition lineObjective)
    ? lineObjective
    : CommanderCaptureService.GetTerritoryCenter(hq);
string where;
if (CommanderFobBuilderAccess.TryNearestOwnedDepot(hq, objective, out VehicleDepot depot, out _) && depot.TrySpawnVehicle(choice))
{
    where = $" at {CommanderEconomyService.NearestHeldBaseLabel(hq, depot.transform.GlobalPosition())}";
}
else
{
    hq.ModifyUnitSupply(choice, 1);
    where = " (supply; no depot could spawn it)";
}
hq.AddFunds(-cost);
```
(`TrySpawnVehicle` is what the player queue calls; check whether it consumes supply — the player path calls `CommitAcquisition` AFTER, which decrements supply only if some exists, so a direct spawn with zero supply is a plain spawn; keep the AI's charge as `AddFunds(-cost)` only.) Log: `bought {vehicle} for {cost}{where}.` with the role detail as today. `NearestHeldBaseLabel` is private in `CommanderFobBuilder` — make it `internal static`.
**Step 3:** Build clean. Verification: `bought ... at <base>` lines; after a FOB is online, `at <FOB name>` for objectives in its sector.

## Task 10: Settings cleanup, docs, self-check registration

- Confirm every new setting is touched in the warm-up list; every removed setting's warm-up line deleted.
- `CHANGELOG.md` `## Unreleased`: one entry in plain words covering validity, count/income, reach, FOB siting (no cap), nearest-depot spawn.
- `conductor/tracks/reach-and-points_20260914/metadata.json` → `EXECUTE: PASSED` when done.
- Final full build: `0 Warning(s) 0 Error(s)`.

## DAG

- T1 → T2 (validity), T3 independent, T4 → T5 → T6, T4+T5 → T7, T8 → T9 (T9 also needs T4), T10 last.
- Parallel lanes: {T1,T2,T3} | {T4,T5,T6,T7} | {T8}. T9 after both middle lanes. One executor is fine; the lanes only say what may be reordered.

## Verification (orchestrator, in game)

Load `Ground Control Duel Far`, both commanders on. Expect within 10 minutes:
1. `Strategic points discovered: … dropped N wooded, N off-road` and a total near 48.
2. Review line carries `reach=in/total` with `in < total` early on.
3. No `ForwardBase`/road `Picket` mission for a point farther than 20 km from a depot; such points show `air` in the review's mission list.
4. `orders a FOB at <point> by <air|road>: brings N points within reach …`, later `FOB <point> online`, then `reach=` rises.
5. `bought <vehicle> for <n> at <base>` and, after the FOB, `at <FOB name>`.
6. No `self-check FAILED` except the user-config income ladder if their cfg still overrides roadside.
