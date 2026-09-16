# Feasibility: buildable FOB as a spawning base

Read-only study, 2026-09-14. Game code verified by decompiling
`NuclearOption_Data/Managed/Assembly-CSharp.dll` with `ilspycmd`; mod code read in place.

## Verdict

**Feasible.** The game already supports creating a fully functional airbase at runtime, at an
arbitrary position, owned by a chosen faction, and the mod already owns every other piece: building
spawning, helipad-to-base linking, depot rally, cargo helicopter delivery and truck convoys. No
Harmony patch is required for the base itself.

The one thing that is *not* available is carrying a building as helicopter cargo. Buildings are
spawned on arrival instead, exactly as the SAM-site foundation already does.

## 1. Creating a working base at runtime — verified

The mission loader's own routine is public and does all of it:

```csharp
// NuclearOption.SavedMission.Mission
public Airbase SpawnCustomAirbase(SavedAirbase saved, ServerObjectManager som)
```

It instantiates `GameAssets.i.airbasePrefab`, calls `Airbase.SetupCustomAirbase(saved)`, then
network-spawns it. The airbase prefab is registered as a spawnable network prefab for every client
(`RegisterPrefabs()` calls `ClientObjectManager.RegisterPrefab(GameAssets.i.airbasePrefab...)`), so
a runtime base replicates to joined clients, not only to the host.

Order of calls for the mod (all server-only; guard on `hq.IsServer`):

1. Build a `SavedAirbase`: `UniqueName` (unique), `DisplayName`, `faction = hq.faction.factionName`,
   `Center` = the FOB position, `SelectionPosition` = same, `CaptureRange` (default 1000 m — propose
   400 m for a FOB), `CaptureDefense`, `Capturable`, `runways` left empty, `VerticalLandingPoints`
   left empty (the helipad adds one), `roads` = a new empty `RoadNetwork`.
2. Append it to `MissionManager.CurrentMission.airbases`. **Required**: a joining client runs
   `Airbase.OnStartClientOnly`, which looks the name up in that list for a custom airbase and logs
   an error if it is missing.
3. `MissionManager.CurrentMission.SpawnCustomAirbase(saved, NetworkManagerNuclearOption.i.ServerObjectManager)`.

Faction ownership is automatic. `SetupCustomAirbase` → `LinkSavedAirbase` → `AfterLink` →
`CaptureFaction(saved.FindHQ())` → `hq.AddAirbase(this)`. So `FactionHQ.GetAirbases()` returns the
new base immediately. `airbase.capture.ForceCapture(hq)` is a public fallback if the name lookup
ever fails.

**This is the key design lever**: the mod's build-radius rule (`CommanderBuildPreview.TryGetBuildBase`)
walks `hq.GetAirbases()`. Create the airbase *first* and the three FOB buildings then sit inside its
own 2.5 km build ring, so no existing siting rule needs loosening, and the enemy commander's siting
logic works at a captured FOB for free.

Attaching the three structures — `Spawner.SpawnBuilding(prefab, pos, rot, hq, airbase, name, capturable, factoryOptions)`
is public and sets `NetworkHQ` and calls `Building.SetAirbase(airbase)` *before* network spawn:

- **Vehicle depot** (`BuildingType.DEP`). `VehicleDepot.OnStartClient` calls
  `NetworkHQ.AddDepot(this)`, so the game's own deployment picks it (`FactionHQ` depot loop calls
  `depot.TrySpawnVehicle`). The mod's depot rally finds it too: `CommanderSpawnService` refreshes
  from `FindObjectsOfType<VehicleDepot>()` every 5 s. Vehicles exit along the depot prefab's own
  `spawnTransform`. *Assumed*: that the DEP catalogue prefab actually carries the `VehicleDepot`
  component — asset data, verify in game.
- **Helipad** (`BuildingType.HGR` with a rotary roster). The mod's `LinkSpawnedBuilding` already does
  this for bought pads: `Building.SetAirbase` puts it in `airbase.hangars`, then a
  `VerticalLandingPoint` is appended to `airbase.verticalLandingPoints`. `Hangar` carries its own
  `spawnTransform` and its own authored aircraft roster, so **no runway is needed** —
  `Airbase.TrySpawnAircraft` walks hangars only. Rotary recovery lands on the appended pad.
- **Radar** (`BuildingType.RDR`). Ordinary building, no wiring beyond `SetAirbase`.

Capture works out of the box: `Airbase.Update` populates `gridSquares` about one second after spawn,
then the `Capture` component runs against `SavedAirbase.CaptureRange` / `CaptureDefense`.

## 2. Delivery — two variants

**Buildings cannot be helicopter cargo.** `MountedCargo.cargo` is a `UnitDefinition` field authored
on weapon prefabs; the mod's own runtime read (`TryGetVehicleCargo`) only ever sees
`VehicleDefinition`. I found no building cargo mount, and which mounts exist is asset data a
decompile cannot settle. Treat "no building cargo exists" as near-certain but unverified.

**Air variant (Tarantula).** Reuse the SAM-site foundation chain verbatim: three transports are
requested through `CommanderSupplyHeliService`, each carrying construction vehicles as cargo
(`MountedCargo.ActivateCargoVehicle` is already patched, and `HandleFoundationCargoActivated` is the
model). When the third load is on the ground inside the site ring, spawn the airbase then the three
buildings. The mod's cargo chooser loads at most two vehicles per airframe
(`MaxInsertionCargoVehicles = 2`); that is a cargo count, not an airframe count, so three flights is
unaffected.

**Ground variant (trucks).** Three munitions trucks (`CommanderPlatoonRole.Truck`, the same role
`FillMissionTrucks` uses) drive to the site under existing platoon movement. On all three arriving,
spawn the same set. Recommend keeping one truck as the FOB's munitions truck (matching a
`ForwardBase` mission) and consuming the other two. No refund, matching `Demolish`.

## 3. Proposed recipe and price

FOB = vehicle depot + radar + helipad, each resolved with the existing
`ResolveCategoryDefinition(BuildingType, preferDearest)` off the live encyclopedia, so a game patch
degrades the recipe instead of null-referencing. Price = the sum of the three
`GetStructureCost(definition)` values (each `definition.value * BuildingCostMultiplier`, floor 25)
plus the delivery: three transport hulls and their cargo vehicles, charged the way the insertion
chain already charges them. Charge at order time; no refund on loss.

## 4. What the player sees

A new order in the operations UI, a site marker that shows phase (ordered → inbound → building →
online), the existing per-flight air markers for the three transports, and once online the standard
airbase map icon (`DynamicMap.RefreshAirbases()` fires on `AddAirbase`). Log lines to prove it:
`RegisterAirbase <name>`, `Adding Building [hangar] ...`, and the mod's own
`Linked <pad> to <base>`. The FOB then appears in the aircraft-selection base list and as a rally
target in the depot window.

## 5. Risks

- **Enemy capture.** A capturable FOB flips wholesale: `CaptureFaction` moves the base *and* every
  attached building's `NetworkHQ`, so the enemy gains a working depot. Mitigate with a short
  `CaptureRange` and high `CaptureDefense`, or set `Capturable = false`.
- **Teardown.** Losing all three buildings leaves an empty registered airbase. The mod must despawn
  the identity, call `hq.RemoveAirbase`, and drop the `SavedAirbase` from `CurrentMission.airbases`.
- **Hot reload.** A reload resets mod memory but not game state, so FOBs would survive as orphaned
  bases. Record FOB identities in `CommanderStateStore` and re-adopt on activate.
- **Multiplayer client.** Every call above is server-only. A pure client must see the FOB but never
  order one.
- **`RoadNetwork.RegenerateNetwork()`** runs in `Airbase.OnStartServer` on the empty taxi network.
  Expected to be harmless; unverified.
- **Performance.** Negligible — one extra `Capture.Update` per FOB at a one-second interval.

## 6. Captured airbases — the same depot problem

### (a) What happens today

On capture, `Airbase.CaptureFaction(newHQ)` runs `oldHQ.RemoveAirbase` then `newHQ.AddAirbase`, and
sets `building.NetworkHQ = newHQ` for every building in `airbase.buildings`. Consequences, all
verified:

- **Held-asset status: correct and automatic.** Everything in the mod that reads
  `hq.GetAirbases()` — the build ring, air missions, recovery, forward bases, pickets, insertion
  origins, the victory check — picks the captured base up on the next tick. The base keeps its own
  `buildings`, `hangars` and `stores` lists, so aircraft spawning at it works immediately.
- **Faction unit list: mostly correct.** `Unit.HQChanged` adds the building's `persistentID` to the
  new HQ's `factionUnits` and removes it from the old. **Caveat**: it returns early when the
  building is `disabled` or not `Active`. A depot knocked out during the fight for the base ends up
  with the right `NetworkHQ` but stays in the *old* faction's `factionUnits`. `CaptureFaction`'s
  `WaitRepair()` un-disables it five seconds later by writing the field directly, which does not
  re-run the hook.
- **The game's own vehicle deployment: broken.** This is the real defect.
  `FactionHQ.AddDepot(VehicleDepot)` is called from exactly two places, both in `VehicleDepot`:
  `OnStartClient` (once, at spawn) and `OnRepairComplete`. There is **no `RemoveDepot` anywhere in
  the assembly**. So a captured depot is never added to the capturing faction's `depotSorted` list,
  and never removed from the previous owner's. `FactionHQ.DeployVehicles` only prunes entries whose
  depot is null or disabled.
  Two effects follow. The capturing faction's engine deployment ignores the depot it just took. And
  the *previous* owner still iterates it — `VehicleDepot.TrySpawnVehicle` spawns with
  `base.NetworkHQ`, which is now the new owner — so the losing faction spends its own vehicle
  supply producing vehicles that belong to the faction that took the base. *Assumed*: that this
  second effect is visible in play; it follows from the code but has not been watched in game.
  The one path that repairs it is a depot that is disabled at capture and later repaired to
  completion by a repair vehicle, because `OnRepairComplete` calls `AddDepot` with the current HQ.
- **The mod's depot rally: correct.** `CommanderSpawnService.RefreshDepots` rebuilds every five
  seconds from `FindObjectsOfType<VehicleDepot>()` filtered by live ownership, so a captured depot
  appears in the player's depot window and rally on its own. The mod is already immune to the engine
  gap for the player's own buying; the engine's AI deployment is not.
- **The building rung never places a depot.** `GetEnemyBuildReserve` and `TryBuildEnemyEconomy`
  cover radar (`BuildingType.RDR`), mine, factory, defence (`BuildingType.DEF`) and the naval dock.
  `BuildingType.DEP` appears nowhere in either commander's build order.

### (b) What the mod must do when a captured base has no depot

Detection reuses the existing walk: `TryGetUncoveredBase(hq, BuildingType.DEP, coverage, out center)`
is already written and already does "the first base this faction holds with no building of that
category near it". Add `BuildingType.DEP` as a rung-4 item ahead of defence.

Placement and spawn go through the paths already in use:

1. `ResolveCategoryDefinition(BuildingType.DEP, preferDearest: false)` for the cheapest depot in the
   live encyclopedia, priced by `GetStructureCost` (its `value` times `BuildingCostMultiplier`,
   floor 25). *Assumed*: that the DEP catalogue prefab carries the `VehicleDepot` component —
   asset data, confirm in game.
2. Site it inside the base's own build ring. `CommanderBuildPreview.TryGetBuildBase` already answers
   "which held base does this site belong to", and the captured base is in `hq.GetAirbases()`, so
   no rule changes.
3. Spawn through the mod's existing `SpawnBuilding`, which routes to
   `Spawner.SpawnFromUnitDefinitionInEditor` and then `LinkSpawnedBuilding`. `Building.SetAirbase`
   puts it in `airbase.buildings`; `VehicleDepot.OnStartClient` then calls
   `NetworkHQ.AddDepot(this)` with the correct owner, because `SpawnBuilding` sets `NetworkHQ`
   before the network spawn. A freshly built depot therefore joins the engine's deployment list
   properly, unlike a captured one.

For a captured depot that already exists but is stranded in the old faction's list, the fix is one
call — `newHq.AddDepot(depot)` — after capture. It is public and server-only. Nothing in the engine
removes the stale entry from the previous owner, so the mod should also clear it; `depotSorted` is
private, so that needs reflection or a Harmony patch on `DeployVehicles`. Recommend doing the cheap
half (add to the new owner) in this track and treating the stale-entry leak as its own decision.

### (c) The same for a helipad

A captured base launches and recovers helicopters only if it has both a hangar whose authored roster
contains a rotary airframe (`Hangar.GetAvailableAircraft`, checked by the mod's existing
`CanHostRotaryAircraft`) and at least one entry in `airbase.verticalLandingPoints`. The mod's
rotary recovery already depends on the second: `CommanderSupplyHeliMission` calls
`Airbase.TryRequestVerticalLanding`, which fails outright on a base with an empty pad array.

Both gaps are already solved code. Buying a hangar-type building
(`ResolveCategoryDefinition(BuildingType.HGR, ...)`) and letting `LinkSpawnedBuilding` run appends a
`VerticalLandingPoint` to the base and registers the hangar — this is the existing
`Linked <pad> to <base>` path, unchanged. The detection test is
`airbase.verticalLandingPoints == null || airbase.verticalLandingPoints.Length == 0`.

Verified vs assumed for this section: every call, list and early-return described above is read from
the decompiled assembly. What is assumed is which catalogue prefabs carry `VehicleDepot` and a
rotary hangar roster, and whether the losing-faction-feeds-the-winner effect is noticeable in play.

## Open questions — NEEDS USER

1. **Capturable or not?** Should an enemy ground force be able to take a player-built FOB, with the
   depot and helipad changing hands, or should a FOB only ever be destroyed?
2. **Delivery vehicles: consumed or kept?** Air variant — do the delivered vehicles vanish into the
   construction, or stay as the FOB garrison? Ground variant — same question for the three trucks.
3. **Restrict the air variant to the Tarantula by name**, or let any rotary transport with a
   runtime vehicle cargo mount fly it (the current chooser is runtime-resolved, so the Tarantula
   falls out naturally if it is the only one)?
4. **Recipe fixed or chosen?** Always depot + radar + helipad, or does the commander pick which
   three structures the convoy carries?
5. **Site rules.** Must a FOB be placed on a held control point, or anywhere on friendly-controlled
   ground? And a minimum distance from an existing base, so it cannot be stacked on the main field?
6. **Captured bases: auto-build a depot everywhere, or only where it matters?** Every captured base
   without one, charged to the building rung, or only the base nearest the front so the rung is not
   drained by a rear capture nobody will buy vehicles at?
7. **Same question for the helipad** on a captured base with no vertical pad: always, or only when
   the base is inside the air wing's operating area?
8. **The stale depot entry.** Should this track also stop the losing faction from feeding vehicle
   supply into a depot it no longer owns? That needs reflection or a Harmony patch on
   `FactionHQ.DeployVehicles`, so it is a gameplay change of its own.
