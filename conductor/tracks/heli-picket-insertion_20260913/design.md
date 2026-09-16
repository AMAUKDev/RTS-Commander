# Design: Transport helicopters insert two-vehicle pickets at control points

**Date**: 2026-09-13 · **Track**: heli-picket-insertion_20260913 · **Approved by user**: 2026-09-13
(four answers — Decisions 7–10)
**Depends on**: platoon-operations_20260913 (picket missions, the pool), strategic-points_20260913
(garrison rule). Reuses the SAM supply-run machinery (`Supply/`) end to end.

The user's request, verbatim: "we need to be spawning transport helis tasked to drop pickets (2x
units) off at control points as well."

## Problem

- Rear control points get a two-vehicle picket mission (`Operations/CommanderOperationsFront.cs:986`
  `PlanPickets`), filled with the cheapest free-pool vehicles and driven there over the road network
  (`FillPickets`, Front.cs:1059; `DriveToHoldPosts`, Front.cs:333). A bought vehicle spawns at the
  depot beside a base (`Depot/CommanderSpawnService.cs`) and its drive rides the roads, so the slow
  arrivals are the roadless points — hilltops and remote villages, where the cross-country crawl
  into the ring costs the point minutes of income (a point pays only while garrisoned,
  strategic-points design SS2). Pickets also yield to platoons (`PlatoonsNeedThePool`,
  Front.cs:1054), so on a busy front they can wait indefinitely: late or never.
- The mod already flies exactly this shape of mission — buy a heli at an airbase, land it at a
  chosen point, deploy cargo, return, recover, refund — for SAM sites
  (`Supply/CommanderSupplyHeliService.cs` + `CommanderSupplyHeliPatches.cs`). It is local-HQ and
  supply-cargo only today. `Ai/CommanderCaptureService.cs:22-29` (the ponytail remark) named this
  as the deliberate upgrade path once ground capture was confirmed; two of its three "cannot verify
  without running the game" behaviours are now verified in play by the SAM feature (a cargo mount
  reads as a cargo station; `AIHeloTransportState` can be forced onto an aircraft). The third —
  landed troops as capture strength — is irrelevant here: a picket is ground vehicles.

## What the game allows (verified in `Assembly-CSharp.dll`, decompiled with ilspycmd)

Verified:

- **Loading is loadout, not drive-aboard.** A vehicle rides a helicopter as a `WeaponMount` with
  `Cargo == true` whose prefab carries a `MountedCargo` (`MountedCargo.cargo` is a
  `UnitDefinition`). There is no API to load an existing ground unit into an aircraft. So the two
  picket vehicles are **bought as cargo mounts at heli spawn**, not lifted from the pool — the
  mod's own SAM foundation run does exactly this (`RequestSamSiteFoundationDrop`,
  `Supply/CommanderSupplyHeliMission.cs:633`).
- **Deployment is `MountedCargo.Fire`.** `RailLaunch` spawns the vehicle at the ramp via
  `Spawner.SpawnUnit(cargo, pos, rot, vel, owner, player)`, which for a `VehicleDefinition` calls
  `SpawnVehicle(..., owner.NetworkHQ, ...)` — **the deployed vehicle is a live unit of the heli's
  faction**, so it registers with the HQ and counts for the point's garrison (which requires `unit
  is GroundVehicle`, `Points/CommanderStrategicPointService.cs:255`). `ActivateCargoVehicle` then
  enables it; the mod already post-fixes that (`NotifyCargoActivated`).
- **A heli carrying cargo flies itself into transport mode.** `AIHeloCombatState.AssessHQTargets`
  hands any heli with a cargo station (`Cargo && Ammo > 0`) to `AIHeloTransportState`; the game's
  own `TransportMode.CombatVehicle` exists. The mod's patches (`Supply/CommanderSupplyHeliPatches.cs`)
  then own the destination, touchdown search, cargo release and landing — all proven by SAM runs.
- **A shot-down heli kills its load.** `MountedCargo.OnPartDetached` spawns the cargo disabled
  when the carrying part detaches. "Heli lost = vehicles lost" is engine behaviour, not a rule we
  add.
- **`EjectionCheck` ejects a stationary transport** more than 200 m from its touchdown point with
  no airbase near (`AIHeloTransportState.EjectionCheck`). The mod suppresses this for SAM sites
  only today (`SuppressEjectionAtAssignedSamSite`) — the suppression must extend to insertion
  landings or the crew ejects while unloading at a far control point.

Assumed (asset data — a decompile cannot answer; the mod's own precedent is a once-per-mission
roster log, like `LogCaptureRosterOnce`): **which vehicle cargo mounts each transport heli's
hardpoint sets offer per faction**, and which of those vehicles have parachute systems
(`CargoMountSupportsAirdrop`). The design resolves this at runtime: enumerate cargo mounts via the
existing catalog (`CommanderSupplyHeliCatalog.GetCargoMounts`, `WeaponChecker.MountAllowedHQ`),
log the roster once, and decline insertion for a HQ that fields no mountable ground vehicle
(logged once, drive-fill carries on).

## Decisions (items 1–6 as designed; 7–10 approved by user, 2026-09-13)

1. **Picket insertion is an air delivery of the same two-vehicle picket** the doctrine already
   wants (`MinGarrison` cheapest vehicles). It runs *as well as* the drive fill: far points go by
   air, near points keep driving; the pool path is untouched.
2. **The two vehicles are bought as cargo mounts** (see above) and charged their `cargo.value`
   each at spawn — the drive-fill path pays the same prices through the depot, so one price ladder
   is kept (CLAUDE.md Reuse rule 4).
3. **The heli is rented, not bought**: charged at spawn, refunded on recovery — the existing
   `PurchasedWithFunds`/`HandleAircraftReturned` chain (`CommanderSupplyHeliMission.cs:1090`). A
   successful insertion therefore costs the faction only the two vehicles.
4. **Lands, not airdrops** (this track): landing at a chosen LZ is the SAM-proven path; the
   airdrop variant is an open question below.
5. **Applies to every commanded HQ, host-only** (`hq.IsServer`), like every AI feature; the
   player-side commander uses only aircraft this feature itself bought.
6. **Budget: the ground pot, not the air fund.** The insertion is a ground-forces spend (two
   vehicles plus a refunded hull). It never touches `BuyAirframe`'s role wing, never tasks an Air
   Command aircraft (`TryTaskAiAircraft` refuses non-planes anyway, so the air-support-tasking
   sibling track cannot collide), and never touches `TaskAirWing`. Insertion helis count toward
   the game's airborne ceiling (`DuelAirborneLimit`, 8) and the enemy's `TransportLimit` (2)
   automatically, because both count every faction aircraft — stated so nobody "fixes" it.
7. **The gate is the point's distance from the nearest road, not from the nearest held asset**
   (user, 2026-09-13: "more than 2km from road is fine"). A picket point more than
   `OperationsHeliInsertionOffRoadMeters` (default 2000 m) from any road is flown in; a point
   within it is driven to as today. A map with no road data at all treats every point as
   off-road and flies. Roadside and crossroads points sit on roads by construction, so they keep
   driving automatically — the gate naturally selects hilltops and remote villages, exactly the
   pickets the drive arrives late at.
8. **Vehicle mix: one air-defence vehicle plus the cheapest other mountable ground vehicle**
   (user, 2026-09-13 — doctrine over bargains; the insertion is a purchase). A faction whose
   cargo catalog fields no air-defence mount takes two cheapest others and logs that once.
9. **Land and unload only** (user, 2026-09-13): the airdrop variant stays out of scope this
   track.
10. **Loss cooldown 10 minutes confirmed** (user, 2026-09-13).

## Reuse

- **The whole fly/land/deploy/return chain**: `CommanderSupplyHeliService` — `SpawnCargoRun` /
  `TrySpawnCargoRunAtAirbase` (buy + `Airbase.TrySpawnAircraft`), `TryAssignPendingAircraft`
  (mission on `RegisterFactionUnit`), `OverrideTransportTarget` (chosen LZ),
  `DeployNextAssignedCargo` (fires cargo mounts), `HoldDeployedCargo` + the ramp-clear coroutines
  (hold position, drive clear), `IssueSupplyReturnToBase` + `OverrideAssignedReturnAirbase` +
  `NotifyAircraftReturned` (RTB, vertical landing at origin, refund). The one generalisation:
  these are gated local-HQ by `CanHostSpawn`; the AI entry is a new internal
  `TryLaunchInsertionAircraft(FactionHQ hq, …)` that reuses everything below that gate with
  `hq.IsServer` — the second programmatic caller after `RequestSamSiteFoundationDrop` (Reuse rule
  5, parameterise the HQ rather than fork).
- **Demand lives in the operations service, not a new service**: a new partial file
  `Operations/CommanderOperationsInsertion.cs` (the codebase's own pattern — Operations is already
  seven partials), reading `PlanPickets`' missions, `IsHeldOrReachable` and `EnsureHoldPosts`
  directly (same class, no new plumbing). No new `Register` call.
- **The road-distance artefact already exists and is thrown away**: discovery caches every road's
  polyline in `roadPointLists` (`Points/CommanderStrategicPointDiscovery.cs:608`, built by the
  roads pass at `:660-684`) and clears it when discovery finishes (`:1031`, the one line to
  remove). `ResetSession` already clears it on a session reset
  (`Points/CommanderStrategicPointService.cs:141`) and a reload re-runs discovery, so retention
  needs no new lifecycle. The measure is the shared `SegmentDistanceSquared` (Discovery:904,
  already `internal` for the offensive's flip test) — nearest-road distance is a min over every
  road's segments, a few thousand positions walked once per candidate point per review.
- **Adoption**: `CommanderOperationsMission.PicketMembers` + `IsClaimedVehicle` (the B2 fix) are
  the claim record — the insertion adds deployed vehicles there, removing them from `state.Pool`
  if the `RegisterFactionUnit` claim race pooled them first.
- **Cargo selection**: `CommanderSupplyHeliCatalog` (`IsRuntimeCargoMount`, `PlaceCargoAndClearNonCargo`,
  `MountAllowedHQ`/`MountAllowedAirbase`, `IsAvailableAirbase`), role mapping
  `CommanderPlatoonRoles.Of` for the preference order.
- Settings/UI/diagnostics patterns: `Core/CommanderSettings.cs` Operations section,
  `UI/CommanderOverlayUiSettings.cs` `DrawOperationsBox`, `CommanderOperationsDiagnostics.cs`
  review line, `CommanderAiLog.Note`.
- New pieces, each needed because nothing does it: the AI-side demand step, the HQ-parameterised
  spawn entry, an insertion branch in `HoldDeployedCargo` / `ShouldSuppressAssignedEjection` /
  `PruneFinishedMissions` (they key off SAM site ids today), per-point loss cooldown state, four
  settings, one slider row, `heli=` on the review line, self-checks.

## Section 1 — Demand (which points, which vehicles)

- Per operations review (30 s), per commanded HQ, **at most one request**: take the first `Picket`
  mission in ranked order (the order missions are already filled in, value descending) that has
  all of: rear (`!IsFront`), `IsHeldOrReachable` (Front.cs:523 — unchanged, insertion sites inside
  it), `PicketMembers` short of `PointsMinGarrison`, no insertion already bound to it, no live
  loss cooldown, and **the point sits more than `OperationsHeliInsertionOffRoadMeters` from the
  nearest road** (Decision 7, user's gate). Distance is a new `NearestRoadDistanceMeters(point)`
  helper on the points service: the minimum over every retained road segment of the shared
  `SegmentDistanceSquared` (Discovery:904), `float.MaxValue` when the map kept no road data —
  which the gate reads as off-road, so a roadless map flies every picket (Decision 7). Default
  2000 m: within it the drive is a short cross-country hop from the road at ground speed; beyond
  it the crawl into the ring costs the point minutes it does not pay for — and hilltops and
  remote villages are exactly the ones that far off-road, while roadside and crossroads points
  sit on roads by construction and keep driving.
- Air insertion **bypasses the pool guard** (`PlatoonsNeedThePool`): it buys its own vehicles, so
  it never competes with a forming platoon for pool bodies — the drive fill keeps its priority.
- **Vehicles** (Decision 8): from the compatible transport heli's cargo mounts whose
  `MountedCargo.cargo` is a `GroundVehicle` definition, **one air-defence vehicle plus the
  cheapest other** by `cargo.value` — doctrine over bargains: a rear point's threat is aircraft,
  and the insertion is a purchase, not a pool hand-me-down. Roles read through the one existing
  mapping, `CommanderPlatoonRoles.Of` (CommanderPlatoon.cs:43). No air-defence mount in the
  catalog → two cheapest others, logged once per mission. Affordability gate at spawn: funds ≥
  heli `value` + both vehicles' `value`; otherwise decline, log once per reason per point.
- LZ: one of the point's own hold posts (`EnsureHoldPosts(point, 2)` — ring interior at
  `Radius * 0.6`), the very posts the picket will hold.

## Section 2 — Lift (load, fly, drop, return)

1. **Load at spawn** (the exact SAM-run calls): pick the cheapest compatible cargo heli + airbase
   (`IsAvailableAirbase`), build the loadout with the two mounts
   (`CreateEmptyLoadout` + `PlaceCargoAndClearNonCargo`), charge hull + vehicles, spawn with
   `airbase.TrySpawnAircraft(null, definition, livery, loadout, fuel)`. Log the request and
   lift-off (`CommanderAiLog.Note`).
2. **Fly**: the heli enters `AIHeloCombatState`, hands itself to `AIHeloTransportState` (cargo
   aboard), and the existing `OverrideTransportTarget` patch flies it at the LZ with the game's
   own `UpdateTouchdownPoint` slope search (≤ 150 m refinement — worst case the vehicles roll a
   few hundred metres onto their posts on the next review, inside the ring well inside the 60 s
   hold clock). Ejection suppression extends to insertion missions (same rule as SAM:
   `radarAlt < 15f` and within 500 m of the target).
3. **Drop**: `DeployNextAssignedCargo` fires each mount on touchdown; each activated vehicle is
   held (`SetHoldPosition(true)`), ramp-cleared by the existing coroutines, then **adopted into
   the mission's `PicketMembers`** (removed from the pool if the claim race got there first).
   `FillPickets`' next review sees the mission at 2/2 and `DriveToHoldPosts` posts them. Log the
   drop.
4. **Return and recover**: `IssueSupplyReturnToBase` → landing at the origin airbase
   (`OverrideAssignedReturnAirbase`) → `Aircraft.ReturnToInventory` → `HandleAircraftReturned`
   refund. Log the recovery.

Edge: the point gains a threat mark mid-flight (picket promotes to ForwardBase) — deliver anyway;
the vehicles claim into the pool through the normal path and the review spends them (a
ForwardBase has no `PicketMembers`); one log line. The point is lost or the mission dissolves
mid-flight — cancel the SAM way (`CancelSamSiteMissions` shape): RTB, undeployed mounts are
loadout (nothing to refund but the hull), vehicles already delivered stand.

## Section 3 — Losses and limits

- **Heli shot down or crashes**: the two vehicles die with it (engine behaviour, verified) and
  the hull cost is lost (no refund — `PurchasedWithFunds` refunds only on `ReturnToInventory`).
  The point takes a **loss cooldown** (`OperationsHeliInsertionCooldownMinutes`, 10 min —
  confirmed by user, Decision 10; two full reviews plus a truck cadence: an identical loss on
  retry a minute later is a waste, an hour is cowardice) during which it falls back to drive
  fill. Log the loss.
- **Limits**: at most `OperationsHeliInsertionLimit` (default 1) insertion helis airborne per HQ —
  transports deliver, they do not win fights (the `TransportLimit` rationale); at most one request
  per review. Both plus the ceiling in Decisions 6 are the whole guard against air-force spam.
- **Hot reload**: insertion state is not persisted — same stance as platoon state
  (persist-reload design, out of scope); the picket mission rebuilds, an in-flight heli reverts to
  the Basegame's own cargo brain and its fate is whatever the game does with it. Developer-only
  cost, off by default there.

## Section 4 — Diagnostics and settings

- `CommanderAiLog.Note` per phase: `PICKET <label>: requesting air insertion (<N> km by road).` /
  lift-off / `dropped 2 vehicles at <label>.` / `transport recovered, hull refunded.` / `lost the
  insertion flight near <label>; cooldown <N> min.` Decline reasons log once per point per
  reason (the `ReportAirDenial` convention).
- The `Ops … review:` line (`CommanderOperationsDiagnostics.cs:54`) gains `heli=<in-flight
  insertions>` beside `staged=`. The air-support-tasking sibling track may add its own field to
  the same line; whoever lands second reconciles the format — no shared string table is needed.
- Once per mission, a cargo-roster log line per faction: every helo cargo mount's vehicle name and
  price (asset data — the log is the answer, `LogCaptureRosterOnce` pattern).
- Settings, all in the `Operations` section (`Core/CommanderSettings.cs`, warm-up list, same
  Get/Set pattern): `HeliInsertionEnabled` (true — visible AI spend, killable like every doctrine
  feature), `HeliInsertionOffRoadMeters` (2000, the user's road gate, rationale in Section 1 —
  slider on the OPERATIONS box, which grows one row; departure 7 already made the tab scroll),
  `HeliInsertionLimit` (1, Section 3) and `HeliInsertionCooldownMinutes` (10, Section 3) —
  config-only, balance knobs in the recipe's company.
- Self-checks (pure, `CommanderOperationsInsertion`): the road gate at its boundary (a point
  exactly 2000 m from the nearest road drives, one further flies, a map with no road data
  flies), the cooldown window (a fresh loss blocks, an expired one does not), the vehicle-mix
  choice (air-defence first, then cheapest other; no air-defence mount → two cheapest), the
  one-per-review cap, and adoption bookkeeping (a unit claimed by the pool first ends in
  `PicketMembers`, not in both).

## Out of scope

Airdrop delivery (parachute-capable vehicles exist — `CargoMountSupportsAirdrop` — and the
supply service can fly it; resolved out of scope by the user, Decision 9 — land and unload only
this track); sling
loading (`UnitDefinition.CanSlingLoad`/`SlingloadHook` is a player-flown mechanic with no AI
hook — not pursued); inserting platoons or FOB garrisons (six vehicles is a different lift
problem); player-issued insertion orders; escort rules for the heli; persistence across reload;
multiplayer clients.

## Verification

Self-checks above, run at plugin load. In game (host, Ground Control Duel): start the duel with
the player commander on for both sides; within the first 10 min the BepInEx log shows a rear point
more than 2 km from any road (a hilltop, typically) with `PICKET … requesting air insertion` and
the review line `heli=1`; watch (or read from the log) the transport lift off, fly past the front,
land at the point's ring and roll two vehicles — one air-defence, one other — onto their posts;
the point's label flips to held within 60 s and its income starts; the heli returns to its base,
`transport recovered, hull refunded` logs, and the balance moves only by the two vehicles' price.
A roadside point's picket in the same match keeps driving as today (the near-road side of the
gate). Then force the failure paths: shoot the next insertion down and confirm the `lost` line,
the cooldown blocking a retry (drive fill resumes instead), and the vehicles aboard dying with
the hull. Gate check (testing rule 5): temporarily set `HeliInsertionOffRoadMeters` to 40 km — no
point qualifies, no insertion lines appear; restore and confirm byte-identical behaviour
returns.

## Open questions

None — all three were answered by the user on 2026-09-13 and are recorded as Decisions 7–10.
## Section 5 — Reinforcing a picket that has lost a vehicle (user, 2026-09-14)

The gate was built for FIRST delivery. It also has to cover TOP-UP: a picket standing on an
air-delivered point that loses one of its two vehicles must get a flight carrying only the
replacement.

**Decision.**

- The structural gate is unchanged and needs no change — `QualifiesForInsertion` asks whether the
  picket is short, never why. A point whose picket has been shot down to one vehicle is short, and
  the surrounding machinery already leaves it short: `MarkAirDeliveredPickets` reserves any rear
  off-road picket point (it does not look at the garrison count), `FillPickets` skips a reserved
  point, and `PostPicketRequisitions` posts nothing for one. So the shortfall survives to
  `PlanInsertions` exactly as a first delivery does. Verified from code, 2026-09-14.
- What changes is the CARGO. `PickInsertionCargo` takes a `wanted` count, clamped to
  `[1, MaxInsertionCargoVehicles]` (2). One short flies one vehicle, and it is the air-defence one
  — a rear point's threat is aircraft, whether the point is being garrisoned for the first time or
  topped up. Fewer affordable vehicles than were asked for is no load at all, and the caller
  declines: a load that cannot fill the request is the wrong flight, not a cheaper one.
- `CommanderInsertion.ExpectedLoads` is now what the flight CARRIES, not `PointsMinGarrison`, so
  the delivered/expected bookkeeping closes when the last vehicle rolls off.
- The per-point loss cooldown and the commander-wide loss pause bind exactly as before. A picket
  short of a vehicle beside a road still drives; only a point past `HeliInsertionOffRoadMeters`
  flies.
- The log line distinguishes the two: a point with nothing on it keeps
  `PICKET <label>: requesting air insertion (<n> m from the nearest road)`; a point with a
  surviving picket reads `PICKET <label>: requesting air reinforcement (1 vehicle short)`.

**Limits.** `Operations/HeliInsertionFlightsMax` default 6 (renamed from
`Operations/HeliInsertionFlights`, which was 3 — BepInEx keeps a player's existing value under the
old key, so a rename is how a raised default actually reaches an existing config file; the old key
is left orphaned in any config already written). `InsertionRequestsPerReview` 3 → 6 to match: a
review now has both first deliveries and reinforcements to serve.

## Section 6 — Blocked landing zones and the airdrop (user, 2026-09-14)

The user's report, verbatim: "air insertion of pickets sometimes is sent to land in tree covered
areas - it cannot land, units aren't dropped, stuck. we either need to check for that and do an
airdrop (preferred), or ignore those areas, or don't generate control points in those wooded regions
in the first place."

**This reverses Decisions 4 and 9** ("lands, not airdrops"; "the airdrop variant stays out of scope
this track"), for the blocked case only. A landable landing zone is still landed on — landing puts
the vehicles on their posts, an airdrop scatters them under canopies — so the airdrop is the
exception the report asked for, not the new default.

### What the game verifiably supports (decompiled with ilspycmd, 2026-09-14)

- **Scatter trees are not colliders and never were.** `TerrainScatter.GenerateScatters` writes tree
  positions into a binary `TextAsset`; `NuclearOption.Effects.TreeRenderer` draws them GPU-instanced
  out of a `GraphicsBuffer`. `Assembly-CSharp` has no tree collider type, `PhysicsLayers` has no tree
  layer, and `Aircraft.CheckRadarAlt` line-casts `Statics|Ships` only. So **no physics probe can
  detect woodland**, and woodland cannot by itself physically stop a helicopter. Anyone who writes a
  raycast tree test in future is writing a test that always passes.
- **The game's own landing search refuses slope, not trees.**
  `AIHeloTransportState.TransportDestination.UpdateTouchdownPoint` line-casts down on `StaticsMask`
  and accepts a touchdown point only when the surface normal is within 20° of vertical, the hit is
  above sea level, and `FactionHQ.IsDropZoneClear` agrees. Where no sample inside its search radius
  passes, its `slope` stays at the 90° seed, the touchdown point is never refined, and the transport
  hovers over whatever spot the mod handed it — which is the "stuck" the report describes. Dense
  woodland in this game sits on hill flanks, which is exactly the ground the slope rule refuses; that
  is why the symptom correlates with trees.
- **Airdrop is a complete, AI-driven path.** With the state's private `airdrop` flag set — which
  `OverrideTransportTarget` already mirrors from the mission every cycle — `FixedUpdateState` holds
  `num = 200f` radar altitude, gear up, aims down the run-in, and calls `DeployCargo` once the
  horizontal distance to the drop point is under four seconds' flying time. **The 200 m is the game's
  hard-coded value; nothing in the mod sets an altitude.** The mod already ships this path for the
  player's own supply runs (`AirdropDelivery`, `SelectedCargoSupportsAirdrop`).
- **Airdrop needs parachutes.** `CargoMountSupportsAirdrop` requires every `MountedCargo.cargo`'s
  prefab `GroundVehicle`/`Container` to carry a non-null `parachuteSystem`. Which vehicles have one
  is asset data; the roster log remains the answer.
- **Assumed, not verified:** that a parachuted vehicle survives and drives. The engine spawns it the
  same way a landed one is spawned and the parachute field is what the player-facing airdrop toggle
  already gates on, but no in-game airdrop of a VEHICLE (as against a supply container) has been
  watched yet. First play test to confirm.

### Decisions

11. **Scout the landing zone before ordering the flight.** A spot is refused when more than
    `LzMaxTreesInClearRadius` (3) trees stand within `Operations/LzClearRadiusMeters` (40 m), when any
    static collider that is not the ground stands in that circle, or when the strategic height map
    puts the slope above `LzMaxSlopeDegrees` (20°, the game's own threshold — read, not chosen).
    Trees are counted from the game's scatter data through a one-byte-per-cell index built once per
    mission at 25 m resolution.
12. **Order of preference: airdrop, then relocate, then decline** (`ChooseInsertionDelivery`).
    Airdrop beats relocation because relocating puts the vehicles up to 400 m off the posts they were
    bought to hold. Declining marks the point `Wooded` and stamps the existing loss cooldown, which is
    what makes `PicketDeliveryMode` hand the point back to the drive fill.
13. **The relocation search is the levelness nudge's own offsets** (`EmitLevelSearchOffsets`), 50 m
    rings out to `Operations/LzSearchRadiusMeters` (400 m), nearest first.
14. **Stall clock, `Operations/InsertionStallTimeoutSeconds` (120 s).** A bound flight within 500 m of
    its landing zone for longer than that without dropping is converted to an airdrop where the cargo
    still aboard has parachutes, and recalled with the usual cooldown where it does not. This is the
    catch-all for every cause the scout cannot predict.
15. **Wooded points are marked, never dropped** (the user's third option declined): many hilltops
    worth holding are wooded. Discovery stamps `CommanderStrategicPoint.Wooded` at the end of its run,
    and `MarkAirDeliveredPickets` reserves a wooded point for the air only when the commander's roster
    fields parachute-capable cargo.
16. **An airdropped insertion flies itself home.** The landing branch's ramp-clear handshake never
    runs on a drop run, so `OverrideTransportTarget` issues the return the moment the last vehicle is
    out, and `ShouldDelayCargoTakeoff` holds the airframe until then so the game's combat state cannot
    claim it first.
