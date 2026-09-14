# Plan: heli-picket-insertion

**Track**: `heli-picket-insertion_20260913` · **Design**: `design.md` (approved 2026-09-13; binding —
Decisions 7–10 are the user's own answers) · **Executor**: one agent, tasks in order. Implementation
starts in a later session, after the air-support track's build lands — re-read any file cited by
line number before editing it; the sibling may have moved those lines.

**Build after every task**, 0 warnings 0 errors required (shells here do not inherit the game path —
set it in the same command):

```bash
NUCLEAR_OPTION_DIR="I:\SteamLibrary\steamapps\common\Nuclear Option" dotnet build GroundControlRts.csproj -c Release
```

Do NOT run `build-and-install.ps1`, `build-dev.bat` or `build-release.bat` (the game may be running,
and it is not this session's job anyway). Do NOT commit or stage anything; the user commits. New
`.cs` files compile automatically (SDK-style csproj, no explicit file list).

**Shared surface with the air-support track** (`air-support-tasking_20260913`, building
concurrently): it adds `Operations/CommanderOperationsAir.cs` (a partial of the same operations
service — different file, no edit collision), a claim notify on the existing
`FactionHQ.RegisterFactionUnit` postfix, and an `air=[…]` field on the `Ops … review:` diagnostics
line. This track adds `Operations/CommanderOperationsInsertion.cs` and a `heli=` field on that same
line: **T12 must read `Operations/CommanderOperationsDiagnostics.cs` as it exists when T12 runs and
reconcile the field order with whatever the air track left there** — do not write against the
line-number citations in this plan blindly. No other file is edited by both tracks.

## Goal

A rear control point's two-vehicle picket arrives by air when the point is more than 2 km from any
road (the user's gate): the operations service requests a transport helicopter, the two picket
vehicles are bought as cargo mounts at heli spawn — one air-defence plus the cheapest other — the
heli flies to the point's hold-post ring, lands, unloads, returns and is recovered with its hull
cost refunded. Roadside and near-road points keep driving as today; a lost heli kills its load and
puts the point on a 10-minute cooldown that falls back to driving. Every commanded HQ runs it,
host-only, funded from the ground pot.

## Architecture

- **No new service and no new Harmony patches.** Demand lives in one new partial of the operations
  service, `Operations/CommanderOperationsInsertion.cs`, called from the existing 30 s `Review()`
  (`Operations/CommanderOperationsService.cs:144`) right after `PlanPickets` — the same partial-file
  pattern as `…Front.cs`/`…Offensive.cs`. The whole fly/land/unload/return chain already exists in
  `Supply/CommanderSupplyHeliService.cs` (+`…Mission.cs`, `…Catalog.cs`) and is patched in
  `Supply/CommanderSupplyHeliPatches.cs`; this track adds branches to the service methods those
  patches already call (`OverrideTransportTarget`, `DeployNextAssignedCargo`, `HoldDeployedCargo`,
  `ShouldSuppressAssignedEjection`, `PruneFinishedMissions`, `IssueSupplyReturnToBase`,
  `HandleAircraftReturned`) — no patch changes.
- **The road-distance artefact is retained, not rebuilt**: discovery already caches every road's
  polyline in `roadPointLists` (`Points/CommanderStrategicPointDiscovery.cs:608`, built at
  `:660-684`) and clears it at `:1031` when discovery finishes — T2 removes that one clear.
  `ResetSession` clears it for real resets (`Points/CommanderStrategicPointService.cs:141`) and a
  reload re-runs discovery, so no new lifecycle exists.
- **Every decision rule is a pure static function** over floats/ints/bools — the off-road gate, the
  cooldown window, the cargo-mix chooser, the adoption bookkeeping — driven by `SelfCheck()` at
  plugin load (`.claude/CLAUDE.md` Testing rules; the only automated test this mod has). Insertion
  self-check cases join the operations service's existing `SelfCheck()`
  (`Operations/CommanderOperationsService.cs:966`) and the points service's `SelfCheck()` — no new
  call site in `Core/CommanderPlugin.cs`.
- **Money follows the SAM-run precedent**: hull charged at spawn and refunded on
  `Aircraft.ReturnToInventory` (`HandleAircraftReturned`, `Supply/CommanderSupplyHeliMission.cs:1090`),
  vehicles charged their `cargo.value` once at spawn and never refunded (they stay in the world).
  All of it server-only (`hq.IsServer`), never `CanHostSpawn`'s local-HQ gate.
- **KISS, per design**: land and unload only (no airdrop), no escorts, no platoon/FOB lifts, no
  player orders, no persistence across a reload.

## Tasks

- [x] **T1 — Insertion settings.** `Core/CommanderSettings.cs`, `Operations` section beside the
  existing `Operations*` entries (CommanderSettings.cs:230-253, warm-up list :405-414): keys
  `HeliInsertionEnabled` (bool, true — visible AI spend, killable like every doctrine feature),
  `HeliInsertionOffRoadMeters` (float, 2000 — the user's road gate, design Section 1),
  `HeliInsertionLimit` (int, 1 — one airborne insertion per HQ, design Section 3),
  `HeliInsertionCooldownMinutes` (float, 10 — confirmed by user, design Section 3), each with a
  one-line comment naming what it tunes and its home (POINTS tab slider for OffRoad, config-only
  for the rest). Four `_ = OperationsHeli…;` warm-up touches. Build.

- [x] **T2 — Retain the road polylines past discovery.** `Points/CommanderStrategicPointDiscovery.cs`:
  delete the `roadPointLists.Clear();` in `StepBases` (line 1031) — keep the clears in
  `RestartDiscovery` (line 90, the retry path) and in `ResetSession`
  (`Points/CommanderStrategicPointService.cs:141`, the mission-reload path). Extend
  `LogDiscoveryResults` with the retained count (`roads retained: {roadPointLists.Count} roads, {N}
  points`). Verification: build; the in-game log line `Strategic points discovered: … roads
  retained: …` in the final play task proves retention on a real map.

- [x] **T3 — Nearest-road distance helper + self-check.** `Points/CommanderStrategicPointService.cs`:
  pure `internal static float NearestRoadDistanceMeters(IReadOnlyList<List<GlobalPosition>> roads,
  GlobalPosition point)` — min over every road's segments of `SegmentDistanceSquared` (Discovery:904,
  already `internal`), `float.MaxValue` for an empty list — plus a one-line instance wrapper reading
  the retained `roadPointLists`. Self-check cases in the points service's existing `SelfCheck()`
  over one synthetic polyline: a point exactly 2000 m from the nearest segment, one further, and the
  empty-list → `MaxValue` case the gate reads as "fly" (design Decision 7). Build; self-check lines
  pass at plugin load.

- [x] **T4 — Slider row.** `UI/CommanderOverlayUiSettings.cs`, `DrawOperationsBox` (line 474): box
  grows 262 → 300 px, one new `DrawPointsSlider` row "Insertion off-road" (0–10 km, "0" / " km")
  writing `CommanderSettings.OperationsHeliInsertionOffRoadMeters` (the `OperationsFrontRangeMeters`
  row at :488 is the pattern to copy, km-scaled like it). Footnote gains "insertion limit and
  cooldown live in the config file". Build; the OPERATIONS box showing the row is part of the final
  play task's checklist.

- [x] **T5 — Insertion state, demand gate, request line.** New `Operations/CommanderOperationsInsertion.cs`
  (partial of `CommanderOperationsService`): per-HQ insertion records added to `OperationsState`
  (bound mission, in-flight aircraft, per-point cooldown-until stamps — plain fields, cleared by the
  existing `ResetSession` clearing `states`); a pure gate `internal static bool QualifiesForInsertion(
  bool rear, bool shortHanded, bool unbound, float cooldownRemaining, bool cooldownLive, float
  nearestRoadDistance, float offRoadMeters, int inFlight, int limit)` — fly only when rear ∧ short ∧
  unbound ∧ no live cooldown ∧ distance > offRoadMeters (MaxValue counts as off-road) ∧ under the
  limit; called once per review from `Review()` after `PlanPickets(hq, state)` over the picket
  missions in ranked order, the first qualifying mission only (one request per review, design
  Section 1). On qualification it logs `CommanderAiLog.Note(hq, $"PICKET {label}: requesting air
  insertion ({distance:0} m from the nearest road).")` and hands the point to T7's entry. Self-check
  cases in the operations `SelfCheck()`: exactly 2000 m drives / 2000+ε flies / no-road flies, a
  fresh cooldown blocks / an expired one does not, limit reached blocks, the one-per-review cap.
  Build; self-check passes.

- [x] **T6 — Cargo-mix chooser (pure).** The insertion half of vehicle selection, pure so it can be
  self-checked: `internal static void PickInsertionCargo(IReadOnlyList<CommanderPlatoonRole> roles,
  IReadOnlyList<float> values, float budget, List<int> picks)` in
  `Operations/CommanderOperationsInsertion.cs` — pick one `AirDefence` (cheapest of that role) plus
  the cheapest non-air-defence, both within the remaining budget after the hull's share is deducted
  by the caller; no air-defence candidate → two cheapest others; fewer than two affordable → pick
  nothing (the mission declines, logged once per reason per point). Roles come from the ONE mapping,
  `CommanderPlatoonRoles.Of(cargo as VehicleDefinition)` (`Operations/CommanderPlatoon.cs:43`).
  Self-check cases: air-defence first + cheapest other; the fallback; the budget decline; a
  non-`GroundVehicle` cargo never enters the list (the caller filters; assert the chooser is
  role-blind only through the arrays it is handed). Build; self-check passes.

- [x] **T7 — HQ-parameterised launch entry.** `Supply/CommanderSupplyHeliService.cs` +
  `Supply/CommanderSupplyHeliMission.cs`: `internal bool TryLaunchInsertionAircraft(FactionHQ hq,
  GlobalPosition lz, int cargoCount)` — the second programmatic caller after
  `RequestSamSiteFoundationDrop` (Reuse rule 5). It reuses the SAM path exactly: catalog via
  `RefreshOptions`/`CreateAircraftOption` + `GetCargoMounts` (`CommanderSupplyHeliCatalog.cs:296`) with
  `IsRuntimeCargoMount`/`WeaponChecker.MountAllowedHQ`/`MountAllowedAirbase`, heli + airbase by
  `IsAvailableAirbase`, loadout via `CreateEmptyLoadout` + `PlaceCargoAndClearNonCargo`, charging and
  spawn via `TrySpawnCargoRunAtAirbase` (hull per `PurchasedWithFunds`, each vehicle
  `hq.AddFunds(-cargo.value)` — never `ModifyUnitSupply`; declined charges roll back the SAM way),
  queueing via the existing `queuedCargoSpawns`/`pendingAircraftSpawn`. The gate is
  `NetworkManagerNuclearOption.i.Server.Active && hq.IsServer` — NOT `CanHostSpawn`'s local-HQ test.
  `CargoMission` gains an insertion branch: a point reference + LZ (target) replacing the SAM site
  ids, no foundation/platform logic. `TryAssignPendingAircraft` matches `pending.Hq` as it already
  does. Logs the lift-off line `CommanderAiLog.Note(hq, "launched a {heli} to insert the {label}
  picket.")`. Build; in-game proof deferred to the final task.

- [x] **T8 — Flight: LZ target and ejection suppression.** `Supply/CommanderSupplyHeliMission.cs`:
  `OverrideTransportTarget` handles insertion missions — destination/LZ/touchdown = the mission's LZ
  (a hold post from `CommanderOperationsService.EnsureHoldPosts(point, 2)`, Front.cs:266 — chosen by
  the operations side at request time and carried on the mission record), `UpdateTouchdownPoint(150f)`
  refinement, the SAM-only foundation/platform branches skipped; `state.stateDisplayName = "Inserting
  picket"`. `ShouldSuppressAssignedEjection` extends its rule to insertion missions: `radarAlt < 15f`
  and within 500 m of the mission target (the same numbers as the SAM branch — the game's
  `AIHeloTransportState.EjectionCheck` ejects a stationary transport beyond 200 m of its touchdown
  point, and an insertion LZ is 20+ km from any airbase). Build; the final task proves the heli lands
  and unloads without ejecting.

- [x] **T9 — Deploy and adopt.** `Supply/CommanderSupplyHeliMission.cs` `HoldDeployedCargo` insertion
  branch: `SetHoldPosition(true)`, the existing ramp-clear coroutines (`ClearGroundVehicleFromRamp`),
  then one call into the operations side — `CommanderOperationsService.NotifyPicketVehicleDelivered(
  FactionHQ hq, CommanderStrategicPoint point, Unit unit)` in `CommanderOperationsInsertion.cs`: adds
  the unit to that mission's `PicketMembers` (the B2 claim test `IsClaimedVehicle`, Requisitions.cs:220,
  already reads it) and removes it from `state.Pool` if the `RegisterFactionUnit` claim race pooled it
  first; a delivered-count on the mission record; log the drop line `CommanderAiLog.Note(hq,
  "dropped {vehicle} at {label}.")`. Adoption bookkeeping is a pure helper
  (`AdoptPicketVehicle(members, pool, unit)`) with self-check cases: a pooled unit ends in
  `PicketMembers` and not in both; a fresh unit is simply appended. Build; self-check passes.

- [x] **T10 — Return, recovery, refund.** `Supply/CommanderSupplyHeliMission.cs`: once the mission's
  `ActivatedCargoCount` reaches its expected loads and the ramp is clear, `IssueSupplyReturnToBase`
  (existing), `OverrideAssignedReturnAirbase` lands it at the origin airbase (existing),
  `NotifyAircraftReturned` → `HandleAircraftReturned` refunds the hull (existing — the insertion
  mission carries `PurchasedWithFunds` like a SAM run); operations clears the in-flight record on the
  refund/mission-complete. Log `CommanderAiLog.Note(hq, "transport recovered, hull refunded.")`.
  Build; final task proves the balance moves only by the two vehicles' price.

- [x] **T11 — Loss, cooldown, and the mid-flight edges.** `Supply/CommanderSupplyHeliMission.cs`
  `PruneFinishedMissions`: a disabled aircraft with an undelivered insertion mission notifies the
  operations side — `NoteInsertionLost(hq, point)` stamps the point's cooldown-until
  (`OperationsHeliInsertionCooldownMinutes`, 10) and logs `CommanderAiLog.Note(hq, "lost the
  insertion flight near {label}; cooldown {N} min.")` (the vehicles died with the hull — engine
  behaviour, `MountedCargo.OnPartDetached`; nothing to clean up). The operations side cancels a
  mid-flight insertion whose point was lost or whose mission dissolved (`CancelSamSiteMissions` shape:
  `mission.Cancelled = true` + RTB; undeployed cargo is loadout, nothing else to refund) and delivers
  one whose point promoted to ForwardBase — the vehicles then claim into the pool through the normal
  claim path (a ForwardBase has no `PicketMembers`), one log line. Build; the final task forces the
  shoot-down path.

- [x] **T12 — Diagnostics.** `Operations/CommanderOperationsDiagnostics.cs` `LogReviewDiagnostics`
  (line 54): add `heli=<in-flight insertions>` after `staged=` — **read the file as it exists when
  this task runs: the air-support track adds `air=[…]` to the same line, so reconcile the field
  order rather than assuming the layout this plan cites.** Plus the once-per-mission cargo roster
  line (the `LogAirRosterOnce` pattern, `Ai/CommanderEnemyCommanderAir.cs:297`): per faction, every
  helo cargo mount whose cargo is a `GroundVehicle`, with name and `cargo.value` — the asset data a
  decompile cannot answer (design "What the game allows", Assumed). Build; the final task shows the
  line.

- [x] **T13 — In-game verification (developer plays, agent specifies).** From design.md Verification,
  on the host in Ground Control Duel with the player commander on for both sides: build and install
  with `.\build-and-install.ps1` (game closed first), launch, and read `BepInEx\LogOutput.log`:
  (a) the discovery line ends with `roads retained: …`; (b) the cargo roster line names the faction's
  mountable vehicles; (c) within 10 min a rear point more than 2 km from any road (a hilltop,
  typically) logs `PICKET … requesting air insertion` and the review line shows `heli=1`; (d) the
  transport lifts off, flies, lands at the point's ring and unloads two vehicles — one air-defence,
  one other — onto their posts; (e) the point's label flips to held within 60 s and its income
  starts; (f) the heli returns, `transport recovered, hull refunded` logs, and the balance moved only
  by the two vehicles' price; (g) a roadside point's picket in the same match keeps driving as today;
  (h) shoot the next insertion down: `lost the insertion flight near …`, the retry is blocked by the
  cooldown and drive fill resumes; (i) gate check (testing rule 5): temporarily set
  `OperationsHeliInsertionOffRoadMeters` to 40000 in the config — no insertion lines appear on the
  next match; restore and confirm the behaviour returns.
## Execution log (2026-09-13)

Build command as specified, `NUCLEAR_OPTION_DIR` set inline; every unit below ended
**0 Warning(s) 0 Error(s)**. T1–T4 and T12 built individually; T5–T11 are one compile unit (the
operations partial and the supply partial call each other through `TryLaunchInsertionAircraft` and
the notify API), so they landed and built as one batch — the departure list below records what
that reshaping changed against the plan's literal wording.

- **T1** Settings + warm-up touches, exactly as planned.
- **T2** The one `roadPointLists.Clear()` in `StepBases` removed, with a comment naming the gate
  that now owns the data; `LogDiscoveryResults` gained `roads retained: N roads, M points`.
- **T3** `NearestRoadDistanceMeters` (pure) + instance wrapper + `CheckNearestRoadDistance` in the
  points `SelfCheck()`: roadless map → `MaxValue`, beside-a-road, past-the-end cases.
- **T4** OPERATIONS box 262 → 300 px, "Insertion off-road" slider (0–10 km), footnote updated.
- **T5** `Operations/CommanderOperationsInsertion.cs`: `CommanderInsertion` record, the pure gate,
  `PlanInsertions` (after `PlanPickets`, one request per review), prune + recall sweep, notify API,
  decline dedup per point, `CheckInsertion` in the operations `SelfCheck()`; `OperationsState`
  gained `Insertions`/`InsertionCooldownUntil`/`InsertionDenials` (cleared with `states`).
- **T6** `PickInsertionCargo` pure chooser + `AdoptPicketVehicle` (generic so the self-check needs
  no Unity objects at load), both self-checked.
- **T7** `TryLaunchInsertionAircraft(hq, point, lz, out decline)` in the mission partial: candidate
  mounts per (heli, airbase), every loadable single/pair, the operations chooser per combination,
  cheapest complete combination kept; queue + `TrySpawnCargoRunAtAirbase` ride as planned.
- **T8** `ShouldSuppressAssignedEjection` gained the insertion branch; the LZ override itself
  needed **no new code** — `OverrideTransportTarget`'s generic non-SAM branch already flies any
  assigned cargo mission at its target with the 150 m touchdown refinement (see departures).
- **T9** `HoldDeployedCargo` notifies operations at both settle points (immediate hold and the
  ramp-clear coroutine's success end); `NotifyPicketVehicleDelivered` adopts into
  `PicketMembers` (pool race handled by `AdoptPicketVehicle`), pool-joins when the point went
  front mid-flight.
- **T10** `DeployNextAssignedCargo` issues RTB once `DeliveryCompleted` via the new rotary-correct
  `IssueInsertionReturnToBase`; `HandleAircraftReturned` logs the recovery line for insertion
  missions (refund only when a hull was actually charged).
- **T11** `PruneFinishedMissions` calls `NoteInsertionLost` for an undelivered insertion;
  `PruneInsertions` recalls flights whose point fell or mission dissolved (`CancelInsertion`,
  the `CancelSamSiteMissions` shape) and carries a stale-request valve (see departures).
- **T12** `heli=` rides the review line right after `staged=` (the air track's `air=[…]` summary
  still rides the line's end via `DescribeAir` — reconciled, no overlap); the once-per-mission
  insertion cargo roster log lives in `TryLaunchInsertionAircraft`'s companion
  `LogInsertionRosterOnce` (the supply service owns the catalog).
- **T13** The in-game checklist is in the report to the user; not run in this session (the game is
  the developer's to launch).

### Departures from the plan's literal wording

1. **T5–T11 built as one batch.** The demand gate and the launch entry are one compile unit across
   the two partials; building "after every task" became after every unit that compiles. T1–T4 and
   T12 kept their own builds.
2. **`TrySpawnCargoRunAtAirbase` became `bool`-returning.** The insertion must know whether
   anything left the ground before it opens its record; the existing SAM/UI callers ignore the
   return (statement calls), so behaviour is unchanged for them. The vehicle charge and its
   rollback live in there — one place where supply money meets a spawn — rather than in the entry.
3. **The launch entry does its own combination search.** `RequestSamSiteFoundationDrop` picks by
   rearm capacity; the insertion picks by doctrine mix, so the entry enumerates compatible vehicle
   mounts (per heli, airbase, HQ), tries every loadable single/pair, runs the operations chooser
   per combination and keeps the cheapest complete one. Catalog, queue, charge and spawn paths are
   the SAM ones; only the choosing differs, by design.
4. **The ejection-suppression branch is the whole of T8.** The plan expected an insertion branch in
   `OverrideTransportTarget`; reading the live code showed the generic non-SAM path already flies
   any assigned cargo mission to its target with slope-refined touchdown, so T8 shrank to
   `ShouldSuppressAssignedEjection` (the game's `EjectionCheck` abandons a stationary transport
   more than 200 m from its touchdown with no airbase near — an insertion LZ always qualifies).
5. **RTB is rotary-correct and explicit.** `IssueSupplyReturnToBase` uses the fixed-wing landing
   state; the insertion gets its own `IssueInsertionReturnToBase` on `AIHeloLandingState` (every
   cargo aircraft option has a helo/tiltwing pilot), issued deterministically when
   `DeliveryCompleted` rather than waiting for the Basegame's no-target self-landing. The SAM
   cancel path is untouched.
6. **`PinMissionUnit` is gated to the local HQ.** Unconditional, it would have pinned an enemy
   commander's insertion transport into the player's mission list — free intel. SAM behaviour is
   unchanged (its HQ is always local).
7. **Stale-request valve, added.** A record whose transport never spawned (a queue that did not
   drain) releases its point after 180 s — six reviews — instead of blocking it forever. A late
   orphan flight is harmless: its vehicles activate, find no record, and claim into the pool
   through the ordinary depot claim.
8. **Chooser partner fallback.** When an air-defence mount is affordable but no non-air-defence
   partner is, a second air-defence vehicle is taken rather than no load (a rear point's threat is
   aircraft); recorded in the chooser's doc comment and a self-check case.
9. **Known accepted race (SAM precedent).** `pendingAircraftSpawn` matches by HQ and definition, so
   an enemy transport bought by its own air wing in the same registration window can be adopted as
   the insertion flight (or the insertion's transport miss its own record). Worst case: one
   insertion delivers at a Basegame objective and the vehicles claim into the pool — logged lines
   expose it, nothing crashes. The SAM feature accepts the same window for the local HQ; a robust
   fix needs a per-request spawn token the game does not offer. Watch for it in the play test: two
   `Insertion cargo roster`-adjacent anomalies — a `requesting air insertion` line with no
   `dropped … at …` follow-up, or vehicles appearing where none were sent.

## Execution log addendum — first play test (2026-09-14)

The play test (`LogOutput.log`, last match) showed flights vanishing: request → launch →
silence → the same request again ~3 min later per hilltop, with no drop, lost or recall line
between. One flight that reached its point in time did drop two vehicles (both real —
`Hexhound GMG`, so the Ibis's cargo is vehicles, not crates), which is what identified the
cause.

### Departure 10 — the record was opened after the launch, so no flight ever bound

`RequestInsertion` added the insertion record to `state.Insertions` only after
`TryLaunchInsertionAircraft` returned — but the launch spawns and registers the transport
synchronously inside its own call (the log proved it: the supply side's "launched a …" line,
written from inside the registration notify, appeared BEFORE the operations side's "requesting"
line). The registration notify therefore found no record, `Aircraft` never bound, the 180 s
stale valve killed the record mid-flight, the arriving vehicles found no record (they claimed
into the pool silently), and the still-short picket re-ordered flight after flight — each
re-request buying another hull. Fix: the record opens BEFORE the launch call and is removed
again on a decline (`CommanderOperationsInsertion.cs`, `RequestInsertion`).

### Departure 11 — flight outcomes made visible, exactly one per launch

The stale valve's line was rewritten to the user's shape (`insertion flight to <label> went
stale after N s: never registered; the request is withdrawn.`) and now withdraws the queued
request too (`CancelInsertion` dequeues a matching unspawned request, so a hangar that frees
later cannot launch a transport nobody is waiting for). The registration line now reads
`insertion flight <heli> bound for <label>, carrying <vehicle A> + <vehicle B>.` and is
guaranteed to follow the request line. A delivery with no record on either end pools the
vehicle visibly instead of silently (`NotifyPicketVehicleDelivered`'s fallback). The pending
spawn record carries the vehicle charge (`PendingAircraftSpawn.InsertionCargoValue`), and the
90 s registration timeout refunds it with a COMMANDER LOG line when a bought transport never
matched at registration.

### Departure 12 — the wing cannot take the picket's transport, nor the reverse

`TryAssignPendingAircraft` now skips, for insertion pendings only, any registering airframe
still above `InsertionDeckSpawnMaxMeters` (100 m — the wing launches its buys at 1200 m,
`CommanderAirCommandMissions`' launch altitude, while the insertion's hangar spawn starts on
the deck) and any airframe the wing has already claimed
(`CommanderOperationsService.IsWingAirframe`, reading the shared state's `CommanderAirframes`).
Whichever registration notify runs first, one airframe ends up with exactly one owner. Only
the hangar-launch Gameplay toggle (deck-launched wing buys) weakens the altitude half.

### Departure 13 — cargo is a real vehicle, priced like a purchase, named in the roster

`TryGetVehicleCargo` tightened: insertion cargo is a `VehicleDefinition` whose prefab is a
`GroundVehicle` only (crates and containers were never eligible after the GroundVehicle test,
but a non-vehicle definition could have passed as role "Other"), and munitions trucks
(`CommanderPlatoonRole.Truck`) are excluded — a truck cannot picket and the game's rearm brain
would claim it anyway. Cargo variants carry placeholder prices (the roster showed 0, 1, 2, 20),
so the charge resolves through `CommanderOperationsService.ResolveInsertionVehiclePrice`
(pure, self-checked): the faction's own depot price when its ground catalog lists the same
vehicle by name, the cargo value otherwise. The selection now prefers a load containing an
air-defence vehicle at any price over a cheaper load without one
(`InsertionCombinationBeats`, pure, self-checked — "doctrine over bargains" now holds at the
transport level too, not just within one heli's mounts), and the once-per-mission roster line
prints `Name (Role price[, depot price])` per vehicle so the developer can see exactly what
each transport can carry and what it will cost.
