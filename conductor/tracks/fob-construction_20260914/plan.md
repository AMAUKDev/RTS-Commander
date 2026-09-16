# Plan: FOB construction by air or road, and depots on captured airbases

**Track**: fob-construction_20260914 · **Design**: design.md (approved 2026-09-14),
feasibility.md (game calls verified by decompile), decision-log DECISION-014
**Expected agent runs**: 1 executor (this session) + 1 review + evaluation, per Track shape.
**Build after every task**: PowerShell `$env:NUCLEAR_OPTION_DIR = "I:\SteamLibrary\steamapps\common\Nuclear Option"; dotnet build GroundControlRts.csproj -c Release`
— must end `0 Warning(s)` / `0 Error(s)`; the last build of the track on `-t:Rebuild`. No install
scripts, no `build-dev.bat`, no commits, no check files, no `.claude/` edits.

**Concurrency**: an insertion-trees agent holds `Supply/CommanderSupplyHeliMission.cs`,
`Supply/CommanderSupplyHeliPatches.cs`, `Supply/CommanderSupplyHeliService.cs`,
`Operations/CommanderOperationsInsertion.cs` and `Points/CommanderStrategicPointDiscovery.cs`; an
awacs-fix agent holds `Operations/CommanderOperationsAir.cs`. This track's own files are the two new
ones, `Operations/CommanderOperationsFob.cs` and `Economy/CommanderFobBuilder.cs`. Every shared file
is re-read immediately before each edit, takes additive/minimal edits only, and is followed straight
away by a build. A build that fails only inside another agent's files is retried after two minutes
rather than fixed here.

## Architecture

No new service and no new Harmony patch. Two new files:

- **`Operations/CommanderOperationsFob.cs`** — a new partial of `CommanderOperationsService`
  carrying the whole demand and delivery side: the `CommanderFobOrder` record, the placement rule,
  the air and ground delivery, the arrival count, the loss/cancel rules, the teardown watch, the
  marker text and the self-checks.
- **`Economy/CommanderFobBuilder.cs`** — a new partial of `CommanderEconomyService` carrying the
  construction itself: `SavedAirbase` creation, registration in `MissionManager.CurrentMission.airbases`,
  `SpawnCustomAirbase`, the three buildings through the existing `SpawnBuilding`/`LinkSpawnedBuilding`,
  and the teardown. It is a partial of the economy service rather than a free-standing class because
  `SpawnBuilding`, `LinkSpawnedBuilding`, `ResolveCategoryDefinition` and `GetStructureCost` are all
  private to that class and are exactly what construction must reuse (Reuse rule 3 — the existing
  partial `Economy/CommanderEconomyServiceEnemy.cs` is the same shape).

### Reuse: what already does this

| Need | Existing code reused | Why it fits |
|---|---|---|
| Air delivery of ground vehicles to a control point | `CommanderSupplyHeliService.TryLaunchInsertionAircraft` | Already generalised by HQ (Reuse rule 5's second caller); binds to hold posts, threat-gates the route, charges and refunds the hull, recovers the helicopter |
| Hold posts inside a point's ring | `CommanderOperationsService.EnsureHoldPosts` | Already the picket insertion's landing posts |
| Airborne flight limit | `CountInsertionsInFlight` / `OperationsHeliInsertionLimit` | FOB flights add into the same count |
| Ground delivery | the free pool's `CommanderPlatoonRole.Truck` walk in `FillMissionTrucks`, `CommanderMoveService.TryDetachFromRearmLogistics`, `CommanderGameAccess.TrySetDestination` | The forward base's truck already parks this way |
| Building spawn + helipad linking | `CommanderEconomyService.SpawnBuilding` / `LinkSpawnedBuilding` | The one place every build path ends; the pad-to-base link is already solved there |
| Which structure serves a category, and its price | `ResolveCategoryDefinition(BuildingType, preferDearest)`, `GetStructureCost` | Live encyclopedia rather than a name table |
| "This held base has no building of that category" | `TryGetUncoveredBase(hq, BuildingType, coverage, out center)` | Written for radar; `BuildingType.DEP` is the second caller |
| Siting a structure beside a base | `TryFindSiteNear(hq, definition, anchor, out site)` | Already the radar's siter, already respects `IsSiteAllowed` |
| "What to build next" and its savings | `GetEnemyBuildReserve` / `TryBuildEnemyEconomy` / `SpendEnemyStructures` | The FOB is one more wish in the one wish list; the rung banks toward it with no new pot |
| Level-ground test for a building spot | `CommanderStrategicPointService.IsGroundLevelEnough` (in `Points/CommanderStrategicPointDiscovery.cs`) via a new one-line `internal` forwarder to its existing private `TryFindLevelGround` | Reuse rule 3 — no second levelness rule |
| Markers | `DrawLabelledMarker` + a pure `*MarkerText` function beside `PicketMarkerText` | Same dot, same label, same self-check shape |
| Loss cooldown, denial de-duplication, `CommanderAiLog.Note` | the insertion partial's conventions | Copied shape, not copied code |

No existing code does "create a runtime airbase" or "order a FOB", so those are new
(feasibility.md §1 names the game calls).

### Nothing new for Section 3

Once the airbase exists, `hq.GetAirbases()` returns it, so the depot rally, forward-base planning,
insertion origins, helicopter CAS range and the ladder all see it with no new code. Two log lines
prove it rather than any new mechanism: one when the first vehicle spawns at a FOB depot and one
when the first aircraft launches from a FOB.

### Money

The FOB's three structure costs are charged **at order time** out of the building rung's savings
(feasibility §3: "Charge at order time; no refund on loss"), which is what lets the FOB be one more
entry in `GetEnemyBuildReserve` with no new pot. Construction then spawns the three buildings free.
Air-delivery hulls and cargo are charged by the existing insertion chain at launch and the hull is
refunded on recovery, exactly as a picket flight's are. Ground delivery takes trucks already bought
and standing in the free pool, so it costs nothing extra at order time.

## Tasks

Each task ends with a Release build reading `0 Warning(s)` / `0 Error(s)`.

- [x] **T1 — Settings and the order record.** `Operations/FobEnabled` (true) and
  `Operations/FobMinBaseDistanceMeters` (15000) in `Core/CommanderSettings.cs` with the existing
  `Get`/`Set` pair and the warm-up touch. New file `Operations/CommanderOperationsFob.cs` with the
  constants (`FobCaptureRangeMeters` 400, `FobBuildingSpacingMeters` 120, `FobDeliveries` 3,
  `FobPointCooldownMinutes` 10, `FobOffRoadMeters` 2000) each with a `<summary>` saying what the
  number means and why, the `CommanderFobOrder` record and `CommanderFobPhase` enum, and the
  `state.FobOrders` list on `OperationsState`. Nothing runs yet.

- [x] **T2 — The placement rule, pure, with its self-check.** `FobSiteQualifies(held, frontOrNearestFront,
  distanceToNearestBaseMeters, minBaseDistanceMeters, inContact, orderOpen, cooldownLive)` in the new
  partial, and `CheckFob(failures)` registered in `CommanderOperationsService.SelfCheck()` with the
  design's named cases: exactly on the 15 km distance fails (the safe side of the boundary, the
  insertion threat gate's convention), one metre past passes, a rear point that is not the nearest
  to the front fails, a point in contact fails, an open order fails, a live cooldown fails.

- [x] **T3 — The live site walk.** `TryPickFobSite(hq, state, out point, out byAir)` using T2's rule
  over `state.RankedPoints`: held by this commander, `IsFront` or the held point with the smallest
  `DistanceToEnemyMeters`, at least `FobMinBaseDistanceMeters` from every airbase of every faction
  (`FactionRegistry.GetAllHQs()` → `GetAirbases()`), not in contact. `byAir` is true when the point
  is more than `FobOffRoadMeters` from the nearest road (`NearestRoadDistanceMeters`) or when
  `IsInsertionRouteThreatened` refuses the road route from the nearest held asset.

- [x] **T4 — The FOB price and the wish in the building rung.** `CommanderEconomyService.FobStructuresCost()`
  (sum of `GetStructureCost` for `DEP`, `RDR`, `HGR`; zero when the encyclopedia has no entry for one
  of them, which disables the wish) in `Economy/CommanderFobBuilder.cs`, plus its self-check case that
  the price is the sum of exactly those three. `CommanderOperationsService.WantsFob(hq)` answers
  whether a site exists. Both wired into `GetEnemyBuildReserve` between the factory rung and the
  defence rung, and into `TryBuildEnemyEconomy` at the matching position.

- [x] **T5 — Opening the order.** `TryOrderFob(hq)` charges `FobStructuresCost` off `factionFunds`,
  opens one `CommanderFobOrder` for the point, and logs
  `orders a FOB at <point> by <air|road>: depot + radar + helipad for <cost>`. Guarded by
  `hq.IsServer` and `CommanderSettings.FobEnabled`. One open order per commander.

- [x] **T6 — Air delivery.** `DispatchFobAirFlights(hq, state, order)` in the review: while the order
  has fewer than `FobDeliveries` loads delivered or in the air, and the shared airborne count is under
  `OperationsHeliInsertionLimit`, request one flight per review pass through
  `CommanderSupplyHeliService.TryLaunchInsertionAircraft(hq, point, lz, allowance, wantedVehicles: 1,
  requireAirdrop: false, ...)`, bound to `EnsureHoldPosts(point, FobDeliveries)`, one hold post per
  flight. Records go in `state.FobFlights`. Denials log once per reason.

- [x] **T7 — The four shared insertion seams.** Additive edits to
  `Operations/CommanderOperationsInsertion.cs`, each a call into the FOB partial and each preceded by
  a fresh read of the file: `CountInsertionsInFlight` adds the FOB flights in the air;
  `NotifyInsertionAircraft`, `NotifyPicketVehicleDelivered` and `NoteInsertionLost` each try the FOB
  branch first and return when it claimed the event; `IsBoundToInsertion` also answers true for a
  point carrying an open FOB order, so a picket flight and a FOB flight never race for one point.

- [x] **T8 — Ground delivery.** `DispatchFobConvoy(hq, state, order)` takes up to `FobDeliveries`
  `CommanderPlatoonRole.Truck` vehicles out of `state.Pool` the way `FillMissionTrucks` does,
  detaches each from the game's rearm AI (`allowRestock: false`) and drives it to its own hold post.
  Re-issued on the movement clock like any other detachment. A truck that dies is dropped from the
  order; a truck that reaches its post inside the point's radius counts as one delivery.

- [x] **T9 — Arrival and the construction trigger.** One counter per order, fed by the air delivery
  callback (the cargo vehicle is despawned as it is consumed) and by the convoy's per-review arrival
  test. When the count reaches `FobDeliveries` the order moves to `Building` and calls the builder.
  Markers and the review line read the same counter.

- [x] **T10 — Airbase creation.** `CommanderEconomyService.TryCreateFobAirbase(hq, position, label,
  out airbase)` in `Economy/CommanderFobBuilder.cs`: a `SavedAirbase` with a unique
  `UniqueName`/`DisplayName` of `FOB <point label>`, `faction = hq.faction.factionName`,
  `Center`/`SelectionPosition` at the site, `CaptureRange = FobCaptureRangeMeters`, `Capturable =
  true`, empty `runways`/`VerticalLandingPoints`/`ServicePoints` and a fresh `RoadNetwork`; appended
  to `MissionManager.CurrentMission.airbases` **before**
  `CurrentMission.SpawnCustomAirbase(saved, NetworkManagerNuclearOption.i.ServerObjectManager)`.
  Guarded by `hq.IsServer` and a live `ServerObjectManager`. Logs the base's name.

- [x] **T11 — The three buildings.** `TryBuildFobStructures(hq, airbase, center, out built)`: depot,
  radar and helipad on a three-point ring whose chord is `FobBuildingSpacingMeters`, each snapped to
  the nearest level ground through the discovery forwarder added in this task
  (`internal static bool CommanderStrategicPointService.TryFindLevelBuildingSpot(GlobalPosition, out GlobalPosition)` in
  `Points/CommanderStrategicPointDiscovery.cs`, forwarding to the existing private `TryFindLevelGround`),
  each spawned with the existing `SpawnBuilding` so `LinkSpawnedBuilding` links the pad to the new
  base. Logs `FOB <point> online: depot, radar, helipad (<name>)`.

- [x] **T12 — Consumption, loss and cancellation.** The delivered cargo vehicles and the arrived
  trucks are despawned on construction (the existing `ServerObjectManager.Destroy` path the economy
  service already uses for a demolished building). A lost flight or truck leaves the order running
  while at least one delivery is still coming; the last one lost cancels the order with no refund and
  stamps `FobPointCooldownMinutes` on the point. Cancelling recalls any flight still out through
  `CancelInsertion`.

- [x] **T13 — Teardown.** Each review, an online FOB whose three buildings are all gone is torn down:
  `hq.RemoveAirbase(airbase)`, the `SavedAirbase` removed from `MissionManager.CurrentMission.airbases`,
  the airbase object despawned, and `FOB <point> destroyed` logged. Self-check: the teardown rule is a
  pure function of the three buildings' liveness, and two dead buildings out of three is not a teardown.

- [x] **T14 — Markers.** `FobMarkerText(pointLabel, phase, delivered, wanted, byAir)`, pure and
  self-checked, producing `FOB <point> — 2/3 delivered` for the air phase, `FOB <point> — convoy 2/3
  arrived` for the road phase, `FOB <point> — building` and `FOB <point> — online`. Drawn through the
  existing `DrawLabelledMarker` from `DrawDetachmentMarkers`.

- [x] **T15 — The review line.** `fob=<point> 2/3 air` appended to the operations diagnostics line
  while an order is open, beside the existing `heli=` field. Nothing when no order is open.

- [x] **T16 — Section 3's two proof lines.** One line the first time a vehicle spawns at a depot
  standing at a FOB, and one the first time an aircraft launches from a FOB, each logged once per FOB
  so the log proves the base is genuinely in use rather than merely registered.

- [x] **T17 — Captured-base depots and pads.** `BuildingType.DEP` and then `BuildingType.HGR` added to
  `GetEnemyBuildReserve` and `TryBuildEnemyEconomy` as the rung's first wishes, detected with the
  existing `TryGetUncoveredBase` for the depot and with
  `airbase.verticalLandingPoints == null || Length == 0` for the pad, sited with the existing
  `TryFindSiteNear`. Logs `<base> has no depot; building one so it can deploy`. Self-check: the wish
  order is depot before pad before mine.

- [x] **T18 — Self-check sweep and CHANGELOG.** Every rule named in design §5 has a case:
  placement, price sum, delivery-count completion, teardown, captured-base wish order, marker text.
  `CHANGELOG.md` entry. Final `-t:Rebuild` reading `0 Warning(s)` / `0 Error(s)`.

- [x] **T19 — Captured depots handed over, and the losing faction stopped feeding them**
  (Decision 6.3). A five-second watch re-registers a depot with its new owner through the public
  `AddDepot` when ownership changes, and the existing `ShouldBlockAutomaticDeployment` seam refuses a
  depot the deploying faction does not own. Pure `IsForeignDepot` with its self-check.

- [x] **T20 — The FOB cap, spacing and money gate** (Decision 7): `FobMaxPerCommander` 2,
  `FobMinSpacingMeters` 20000, `FobLossCooldownMinutes` 15, the front-nearest-only site rule and the
  banked-savings gate, with self-checks for each.

- [x] **T21 — Abandoning a rear FOB to build at the front** (Decision 8): the advance threshold, the
  quiet and in-use tests, the one-per-ten-minutes limiter, the `Abandoning` phase and its marker,
  demolition through the shared despawn, with self-checks for each.

## Out of scope

Player-issued FOB orders from the UI; persistence of FOB identity across a hot reload; building cargo
on helicopters; commander-chosen recipes; the stale-depot-entry leak in the losing faction's
`depotSorted` list (feasibility §6(b), its own decision).

## Departures

1. **The two new files are partials of existing services, not new classes.**
   `Economy/CommanderFobBuilder.cs` declares `CommanderEconomyService` and
   `Operations/CommanderOperationsFob.cs` declares `CommanderOperationsService`, following
   `CommanderEconomyServiceEnemy.cs`'s own shape. `SpawnBuilding`, `LinkSpawnedBuilding`,
   `ResolveCategoryDefinition`, `GetStructureCost`, `TryFindSiteNear`, `EnsureHoldPosts` and the
   insertion threat gate are all private to those two classes and are exactly what this track has to
   reuse; a free-standing class would have meant widening seven members' visibility (Reuse rule 3).

2. **FOB air flights go out one at a time, not three at once.** Three concurrent requests to one
   point would be the first time the supply service is asked to hold three cargo missions for a
   single `CommanderStrategicPoint`, and its per-point calls (the queue withdrawal inside
   `CancelInsertion`) are written to the invariant that a point carries at most one. The picket
   insertion keeps that invariant with its own bound test; the FOB order keeps it by sending its
   loads in succession. Three flights still fly; they take about a review longer in total.

3. **The air variant's cargo is whatever the shared insertion chooser loads**, which prefers an
   air-defence vehicle (`PickInsertionCargo`, design Decision 8 of the insertion track), rather than
   a vehicle chosen for being cheap. Making it "cheapest" would have meant parameterising the
   chooser's doctrine rule and threading the flag through `TryLaunchInsertionAircraft` in another
   agent's file; the vehicle is consumed either way, so the difference is price, not behaviour.

4. **The captured-base depot and pad are the building rung's FIRST wishes, ahead of radar.** The
   design says "first wish ... ahead of mines/factories" and does not place them against radar. A
   base that can deploy no vehicle and recover no helicopter is a bigger hole than a base that
   cannot see, so they went first. The FOB order itself sits after factories and before base
   defence: a commander that cannot yet pay for a factory cannot pay for three structures and three
   deliveries.

5. **"A base with no depot" is detected by walking the base's own `buildings` list as well as
   `HasBuildingNear`, not by `TryGetUncoveredBase`.** That helper reads `hq.factionUnits` only, and
   a depot knocked out at the moment of capture stays in the LOSING faction's unit list
   (`Unit.HQChanged` returns early for a disabled unit) — which is precisely the case this feature
   exists to answer. Named and explained rather than silently reused (Reuse rule 2).

6. **A separate `ResolveRotaryHangarDefinition`.** The cheapest `HGR` in the encyclopedia is often a
   fixed-wing shelter, which `LinkSpawnedBuilding` deliberately gives no landing point, so a pad
   bought through `ResolveCategoryDefinition` could have left the base exactly as unusable. The new
   resolver answers a different question ("which hangar can take helicopters") and caches and logs
   the same way.

7. **A stale-order valve not in the design.** A commander runs one FOB order at a time, so an order
   that can never finish would hold the slot for the rest of the match. An order still delivering
   after 15 minutes is abandoned with the site's ten-minute cooldown.
