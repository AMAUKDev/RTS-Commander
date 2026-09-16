# Design: FOB construction and captured-base depots

**Date**: 2026-09-14 · **Track**: fob-construction_20260914 · **Approved by user**: 2026-09-14
**Feasibility**: `feasibility.md` in this folder (game calls verified by decompile).
**Depends on**: heli-picket-insertion (transport chain), pickets-first (held points), commander-priorities
(building rung), the helipad-linking fix in `Economy/CommanderEconomyService.cs`.

## Problem (user's words)

"We need a special type of ground OR air insertion task (Tarantula only for air) — 3× trucks or 3×
Tarantulas sent to one location, where it builds a FOB consisting of a vehicle depot, a radar station
and a helipad — should then be treated as a 'base' for spawning etc." And: "same for when we capture
an airbase — need to make sure there's a vehicle depot placed and it becomes used."

## Decisions (user, 2026-09-14)

1. A FOB is **capturable** like any airbase: hold its ring and it flips with its buildings.
2. **Trucks are consumed** by construction; **transport helicopters return home** and are recovered
   like an insertion flight.
3. **Fixed recipe**: vehicle depot + radar + helipad.
4. **Placement**: on a control point the commander holds, at least `FobMinBaseDistanceMeters` (15 km)
   from any existing airbase (its own or the enemy's). Any rotary transport with a runtime vehicle
   cargo mount may fly it; the Tarantula falls out of the existing chooser.
5. Captured airbases: if a captured base has no vehicle depot (or no helipad), the building rung
   places one there first, so the base is used for spawning.

## Reuse

- Base creation: `MissionManager.CurrentMission.SpawnCustomAirbase(SavedAirbase, ServerObjectManager)`
  (game), `Airbase.SetupCustomAirbase`, `hq.AddAirbase` via `CaptureFaction`; `CurrentMission.airbases`
  registration for joining clients.
- Buildings: `Spawner.SpawnBuilding(prefab, pos, rot, hq, airbase, name, capturable, factoryOptions)`
  through the mod's `SpawnBuilding` / `LinkSpawnedBuilding` (helipad → `Building.SetAirbase` +
  `VerticalLandingPoint`), `ResolveCategoryDefinition(BuildingType.DEP/RDR/HGR)`, `GetStructureCost`.
- Depot use: `VehicleDepot.OnStartClient` → `hq.AddDepot`; the mod's depot rally
  (`Depot/CommanderSpawnService.cs`) refreshes depots every 5 s.
- Delivery: the SAM-foundation chain in `Supply/CommanderSupplyHeliMission.cs`
  (`RequestSamSiteFoundationDrop`, `HandleFoundationCargoActivated`) for the air variant; munitions
  trucks via `FillMissionTrucks` / platoon movement for the ground variant.
- Demand and money: the ladder's building rung (`Ai/CommanderEnemyCommanderLadder.cs`), insertion
  threat gate and loss pause, `IsHeldOrReachable`, points ownership.
- Not reused: helicopter building cargo (does not exist in the game data).

## Section 1 — Demand

Each review, a commander with the building rung's turn and savings ≥ the FOB price considers ONE FOB
order when: it holds a control point that is (a) ≥ 15 km from every airbase, (b) a front point or the
held point nearest the front (a FOB is for projecting force, not for the rear), (c) not under contact
right now, and (d) no FOB order is already open. Air variant when the point is > 2 km off-road or the
road route fails the threat gate; ground variant otherwise. Price = three structure costs + three
truck prices (ground) or three hulls' rental + cargo (air, hulls refunded on recovery). Log:
`orders a FOB at <point> by <air|road>: depot + radar + helipad for <cost>`.

## Section 2 — Delivery

- **Air**: three transport flights through the supply-heli service, each carrying construction cargo
  (one cheap vehicle, consumed on arrival — the mod cannot carry buildings), bound to the point's
  hold posts, behind the route threat gate; the flights count against `HeliInsertionFlightsMax`.
  Helicopters return and recover as insertions do. Marker `FOB <point> — 2/3 delivered`.
- **Ground**: three munitions trucks drive as a mini-platoon (existing move seam, bounding rules
  inside 1 km of the enemy) to the hold posts. Marker `FOB <point> — convoy 2/3 arrived`.
- **Construction** fires when the third load is inside the point's ring: create the airbase first
  (`SavedAirbase` with `CaptureRange` 400 m, `Capturable` true, faction = the commander's), register
  it in `CurrentMission.airbases`, then spawn depot, radar and helipad inside its build ring at
  `FobBuildingSpacingMeters` (120 m) apart on level ground (reuse the levelness test), link the
  helipad. Consume the trucks / cargo vehicles. Log: `FOB <point> online: depot, radar, helipad
  (<name>)`. The point keeps its own control-point identity; the FOB base is a second asset on it.
- **Loss**: a flight or truck lost → the order continues with the remainder if ≥ 1 is still coming,
  else it is cancelled (no refund on what was spent), point cooldown 10 min. All three buildings
  destroyed → the airbase identity is torn down (`hq.RemoveAirbase`, despawn, remove from
  `CurrentMission.airbases`), log `FOB <point> destroyed`.

## Section 3 — The FOB as a base

Once online: appears in `hq.GetAirbases()`, so forward-base planning, insertion ranges, depot rally,
helicopter CAS range, AWACS station choice and the ladder all see it with no new code; vehicles bought
for the nearest depot spawn there; helicopters launch and recover on its pad; the enemy may capture it
by the airbase rules (400 m ring, `CaptureDefense` = the game default), taking the buildings with it.

## Section 4 — Captured airbases

Each building-rung review, for every airbase the commander holds: if it has no `VehicleDepot`, a depot
is the rung's first wish (ahead of mines/factories) sited inside the base's build ring; if it has no
vertical pad, a helipad is next. Applies to captured bases and to the FOB itself if a building dies.
Log: `<base> has no depot; building one so it can deploy`. The depot rally then uses it automatically.

## Section 5 — Settings, diagnostics, self-checks

Settings (`Operations`): `FobMinBaseDistanceMeters` 15000, `FobEnabled` true. Constants with
`<summary>`: `FobCaptureRangeMeters` 400, `FobBuildingSpacingMeters` 120, three deliveries.
Review line: `fob=<point> 2/3 air` while an order is open. Markers as above plus the standard airbase
icon once online. Self-checks: placement rule (distance, held, front-nearest, not in contact), price
sum, delivery-count completion, teardown when all three buildings are gone, captured-base wish order
(depot before pad before mines).

## Decisions 6-8 (user, 2026-09-14, after the first implementation pass)

### Decision 6 — Captured airbases are made usable, and the depot leak is closed

Three parts, all required:

1. **A depot at every held base without one.** The building rung's first wish, sited inside the
   base's own ring through the existing `TryFindSiteNear`. Log:
   `<base> has no depot; building one so it can deploy`.
2. **A helipad at every held base with no vertical landing point**, second wish, through the
   existing `LinkSpawnedBuilding` path so the base gains a real `VerticalLandingPoint`. Its own log
   line is the existing `Linked <pad> to <base>`.
3. **The capture leak, both halves.** `FactionHQ.AddDepot` is called once, when a depot spawns, and
   the assembly contains no `RemoveDepot` at all (feasibility §6a). After a base changes hands the
   losing faction's deployment loop still walks that depot, spends its own vehicle supply at it, and
   `VehicleDepot.TrySpawnVehicle` hands the vehicle to `base.NetworkHQ` — the faction that took the
   base. The loser was buying vehicles for the winner.
   - **Re-register:** the depot is added to the new owner through the public `AddDepot`. Log:
     `<base> captured: depot re-registered to <faction>`.
   - **Refuse:** the deployment loop no longer feeds a depot the deploying faction does not own.

### Decision 7 — A hard cap on how many FOBs exist

"Be careful with the FOBs, we don't want EVERY capturable point turning into a FOB."

- `FobMaxPerCommander` (2) — FOBs a commander OWNS right now, read off the live base list, so one
  taken from the enemy counts against the captor and one lost stops counting at once. An order on
  its way counts too.
- `FobMinSpacingMeters` (20 km) between FOBs, chaining with the existing 15 km from any airbase — a
  FOB is an airbase once online, so both rules then apply to it.
- The order opens only when the building rung has banked the FULL price, so a FOB is never paid for
  out of the floors the ladder reserves for the platoon and picket rungs.
- The site is the front-nearest held point alone — the held point with the smallest distance to the
  nearest enemy asset — not any front point.
- `FobLossCooldownMinutes` (15) after a FOB is destroyed or captured, commander-wide.

### Decision 8 — A rear FOB may be abandoned to build one nearer the front

"We should also be able to abandon a FOB and then build one closer to the front-line if needed."

At the cap, when the front-nearest candidate point is at least `FobAdvanceMeters` (20 km) closer to
the enemy than the commander's rearmost FOB, and that rear FOB is quiet (nothing hostile tracked
within `ObservedRadiusMeters` for `FobQuietMinutes`, no platoon or picket on its point) and idle (no
vehicle or aircraft of its faction standing on it in the last review), it is abandoned: the three
structures are demolished through the same despawn the player's DEMOLISH button uses, with no
refund, and the airbase identity is torn down as on destruction. Announced one review ahead so the
marker reads `FOB <point> — abandoning` and the log line
`abandons <name>: the front has moved <n> km past it` is visible before the base disappears. The
15-minute loss cooldown does NOT apply — abandoning is a decision, not a defeat. At most one
abandonment per `FobAbandonIntervalMinutes` (10).

## Out of scope

Player-issued FOB orders from the UI (AI only, like every doctrine feature); persistence of FOB identity
across a hot reload (record as a follow-up: without it a reload leaves an orphaned but working base);
building cargo on helicopters; commander-chosen recipes.

## Verification (in game)

Load `Ground Control Duel Far`, both commanders on. Within the first 15 minutes the log shows
`orders a FOB at <point> by air` (or `by road`), three `INSERTION`/`TRUCK` markers converging, `FOB
<point> online`, then a new airbase icon on the map; a later `launched a <helicopter> … from <FOB
name>` line and vehicles spawning at its depot; the review line's `fob=` clears; capturing an enemy
base logs `<base> has no depot; building one` and the depot appears inside its ring.
