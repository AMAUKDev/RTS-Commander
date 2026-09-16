# Design: Fewer, reachable control points; depot reach; FOBs that extend reach

**Date**: 2026-09-14 · **Track**: reach-and-points_20260914 · **Approved by user**: 2026-09-14
**Depends on**: strategic-points (discovery), pickets-first (staffing), fob-construction (FOB order),
heli-picket-insertion (air reach).

## Problem (user's words)

"1. Some control points impossible to get to and tactically insignificant — make heavily wooded
control points invalid, limit to within some km of a road. 2. Many units sitting around doing nothing:
reduce number of control points but raise their funding impact. Only spawn units for objectives closer
than some km. For objectives further than this, MUST set up a FOB with a vehicle depot closer to the
objective, and spawn from there."

Evidence (2026-09-14 match, 82 km map): discovery produced 148 points (30 sites, 55 hilltops,
24 crossroads, 30 road points, 1 outpost, 8 bases). The player side ended with 22 forward bases,
43 pickets and 36 platoons (about 216 vehicles), most of them holding points no enemy would ever
approach. Every FOB order was refused because no held point was 15 km from an airbase.

## Decisions (user, 2026-09-14)

1. **Validity**: a point is dropped at discovery when its footprint is woodland OR its nearest road is
   farther than `PointMaxRoadDistanceMeters` (2000). Resource sites obey the same test. Bases are exempt.
2. **Count**: about 40–50 points per map. `MaxControlPoints` 120 → 48; `ControlPointSpacingMeters`
   800 → 3000. The cap keeps the highest-value points.
3. **Income**: per-kind income triples so a full map pays roughly what it does today: base 90, village
   30, crossroads 30, hilltop 15, outpost 15, roadside 9 per minute (`Points` section).
4. **Reach**: `DepotReachMeters` (20000). A point farther than this from every vehicle depot the
   commander owns is out of reach: no forward base, no road picket, no platoon for it. Helicopter
   insertion is the only way to staff it until a depot is within reach.
5. **FOB siting**: the "15 km from any airbase" rule is replaced. The candidate is the held point at
   least `FobMinDepotDistanceMeters` (10000) from every owned depot that brings the MOST out-of-reach
   points inside `DepotReachMeters`; ties go to the point nearest the enemy. An order opens only while
   at least one point is out of reach. **No cap on the number of FOBs** — `FobMaxPerCommander` is
   retired; the 20 km `FobMinSpacingMeters` is the practical limit.
6. **Spawn at the nearest depot**: a bought ground vehicle spawns at the owned depot nearest the
   objective of the order-book line it fills, through the depot's own spawn call (the player build
   queue's path), settling payment after the spawn. Only when no depot can spawn it does the buy fall
   back to adding supply for the game's deployment loop.

## Reuse

- Discovery: `Points/CommanderStrategicPointDiscovery.cs` — the woodland stamp (`point.Wooded`, via
  `CommanderSupplyHeliService.IsWoodedFootprint`), the levelness drop pass and its
  `Strategic point dropped:` log line, the caps and spacing pass; `NearestRoadDistanceMeters`.
- Staffing: `PlanForwardBases` / `PlanPickets` in `Operations/CommanderOperationsFront.cs` already gate
  on `IsHeldOrReachable`; the reach test is one more gate beside it. `PointQualifiesForInsertion` in
  `Operations/CommanderOperationsInsertion.cs` admits out-of-reach points.
- FOB: `TryPickFobSite`, `FobSiteQualifies`, `ReportNoFobSite` in `Operations/CommanderOperationsFob.cs`;
  `CommanderEconomyService.NearestFobDistance`; depot enumeration in `Economy/CommanderFobBuilder.cs`
  (`FindObjectsOfType<VehicleDepot>` + owner map).
- Spawn: `CommanderSpawnService` queue path (`queue.Depot.TrySpawnVehicle(definition)` then
  `CommitAcquisition`); the AI buy at `Ai/CommanderEnemyCommanderService.cs` (`ModifyUnitSupply(choice, 1)`).
  The order book (`Operations/CommanderOperationsRequisitions.cs`) knows each line's mission and point.
- Not reused: any Harmony patch on `DeployVehicles` for steering — the direct spawn makes it unnecessary.

## Section 1 — Validity, count, income

Discovery's drop pass gains two tests after levelness: `IsWoodedFootprint(point.Position, radius)` and
`NearestRoadDistanceMeters(point.Position) > PointMaxRoadDistanceMeters`. Both apply to every kind
except Base. Each drop logs `Strategic point dropped: <kind> <label>: wooded` / `: <n> m from the
nearest road`. The discovery summary gains `dropped N wooded, N off-road`. The caps pass runs after, with
the new defaults. Income defaults change in `Core/CommanderSettings.cs`; the income-ladder self-check is
unchanged (the ordering holds). A pure `PointIsValid(kind, wooded, roadDistance, maxRoad)` carries the rule.

## Section 2 — Reach

`OperationsState.OutOfReach` (a set of points) is rebuilt each review from the commander's live depots
(`VehicleDepot`, owner = hq, not disabled, at an airbase or a FOB). Pure
`IsWithinDepotReach(nearestDepotMeters, reachMeters)`. `PlanForwardBases` and `PlanPickets` skip
out-of-reach points; `PlanPickets` still creates the picket MISSION for them (so the insertion loop has a
target) but marks it air-only, as `IsAirDeliveredPicket` already does for off-road points. `FillPickets`
never draws pool vehicles for an out-of-reach picket. Review line: `reach=<in>/<total>`. A commander
with no depot at all treats its airbases' centres as depots (a fresh match before any depot exists).

## Section 3 — FOB siting

`TryPickFobSite` walks held points, skips any within `FobMinDepotDistanceMeters` of an owned depot,
scores each by `CountBroughtInReach(point, outOfReach, reachMeters)`, keeps the best (ties: nearest the
enemy). No candidate when nothing is out of reach: `no FOB this review: every point is within reach`.
The order line becomes `orders a FOB at <point> by <air|road>: brings <n> points within reach, depot +
radar + helipad for <cost>`. `FobCapAllowsOrder` and `FobMaxPerCommander` are removed; the review's
`fob=` field shows `<online> online` only. Spacing, loss cooldown, abandonment unchanged.

## Section 4 — Spawn at the nearest depot

The buy loop asks the operations service for the objective of the line it is filling
(`TryGetRequisitionObjective(hq, role, out GlobalPosition)`); the ladder's plan-based buys (an
undiscovered commander) use the territory centre. `NearestOwnedDepot(hq, objective)` picks the depot;
`depot.TrySpawnVehicle(choice)` spawns; on success the faction is charged the same `choice.value` as
today and the log line reads `bought <vehicle> for <cost> at <base> (for <role>)`. On failure the buy
falls back to `ModifyUnitSupply(choice, 1)` with `(supply; no depot could spawn it)` appended.
`PoolIdleCap` still governs the game loop's own deployments of factory supply.

## Section 5 — Settings, diagnostics, self-checks

New settings: `Points.PointMaxRoadDistanceMeters` 2000, `Operations.DepotReachMeters` 20000,
`Operations.FobMinDepotDistanceMeters` 10000. Retuned: `MaxControlPoints` 48, `ControlPointSpacingMeters`
3000, the six income rates. Removed: `FobMaxPerCommander`. Self-checks: validity (wooded drops, off-road
drops, base exempt, site not exempt, at the limit stays); reach (at the limit is in reach, one metre past
is out); FOB score (a point bringing three in beats one bringing two, a point too near a depot is
skipped, nothing out of reach → no site); nearest-depot pick (nearer wins, disabled skipped, foreign
skipped); income ladder still ordered.

## Out of scope

Player-side UI for reach; helicopter insertion rules; map-specific tuning; retiring the deployment-loop
hold (still needed for factory supply).

## Verification (in game)

Load `Ground Control Duel Far`. Discovery line shows a count near 48 with `dropped N wooded, N off-road`.
Within ten minutes: review line `reach=` below the total; no `ForwardBase`/road `Picket` for a point
beyond 20 km of a depot; `orders a FOB at <point>: brings N points within reach`; after `FOB <point>
online`, the next review's `reach=` rises and `bought <vehicle> ... at <FOB name>` appears. Income line
totals near the old match's despite the fewer points.
