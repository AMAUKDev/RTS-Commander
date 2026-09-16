# Air-Mobile Platoons and Protected Lifts — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use orchestrator-supaconductor:executing-plans to implement this plan task-by-task.

**Goal:** Raise a new platoon as an air-mobile light platoon (six vehicles the transports can carry, three lifts of two) whenever its objective is more than ten minutes' drive from the nearest usable depot, and give every lift, FOB construction included, an escort-and-sweep package.

**Architecture:** The FOB order (`CommanderFobOrder`) becomes a lift order with a purpose (FOB construction or platoon); every FOB rule stays, keyed by purpose. A platoon lift's landed vehicles are adopted straight into the mission's platoon. A lift cover sortie (escorts sized like the strike package's, anti-radar when air defence is tracked) is opened for the lift's lifetime and the transport waits at the form-up point for it. No new service.

**Tech Stack:** C# net472, BepInEx 5 / Harmony, Unity. Verification = `SelfCheck` cases at plugin load plus `BepInEx\LogOutput.log`.

**Rules for the executor (from `.claude/CLAUDE.md`):** read the Reuse code in `design.md` first; every number a class-level constant or `CommanderSettings` entry with a `<summary>`; every pure rule gets self-check cases beside the existing ones (`Expect(failures, ...)` in the Operations partials' `Check*` methods); MOVE code, never paraphrase (the FOB generalisation is a rename-and-widen, not a rewrite); build after every task with `$env:NUCLEAR_OPTION_DIR = "I:\SteamLibrary\steamapps\common\Nuclear Option"; dotnet build .\GroundControlRts.csproj -c Release -nologo -v q` → 0 warnings 0 errors; last task `--no-incremental`; do NOT install; do not commit; preserve each file's byte-order mark state and CRLF endings exactly (check the bytes before writing; several files have no mark). CHANGELOG under `## Unreleased` in full sentences. Log lines via `CommanderAiLog.Note` in the existing plain style.

---

## Phase A — the lift order

### [x] Task 1: The FOB order becomes a lift order with a purpose
**Files:** Modify `Operations/CommanderOperationsFob.cs` (`CommanderFobOrder` ~173, `CommanderFobPhase` ~153, `FobDeliveries` ~57, `ReviewFobs` ~789, `DispatchFobAirFlights` ~1000, `BuildFob` ~1402, `CancelFobOrder` ~1455, the review-line `fob=` field in `Operations/CommanderOperationsDiagnostics.cs`).
- Add `internal enum CommanderLiftPurpose { FobConstruction, Platoon }` and on the order: `Purpose` (default FobConstruction), `LoadsWanted` (int; `FobDeliveries` for FOB, `LiftLoadsPerPlatoon` for platoon), `VehiclesPerLoad` (1 for FOB as today, `LiftVehiclesPerLoad` 2 for platoon), `Mission` (`CommanderOperationsMission?`, platoon lifts only).
- Every use of the constant `FobDeliveries` inside the order's lifecycle reads `order.LoadsWanted` instead; `FobReadyToBuild` runs only for `FobConstruction`; a platoon lift completes when `Delivered >= LoadsWanted` and closes with the log in Task 5. Keep the class and file names (a rename is out of scope; the doc comment explains the widening).
- Review line: `fob=` → `lift=<purpose label> n/N` (`lift=FOB HILLTOP 9 0/1`, `lift=3RD PLATOON 2/3`).
- Self-check: `FobOrderCanContinue`/`FobReadyToBuild` cases unchanged; add `LiftComplete(delivered, loadsWanted, purpose)` cases (FOB 1/1 → build; platoon 3/3 → complete; platoon 2/3 → not).

### [x] Task 2: The drive-time gate (pure + settings)
**Files:** Modify `Operations/CommanderOperationsFront.cs` (beside `RefreshReach`), `Core/CommanderSettings.cs`.
- Settings: `AirMobileDriveMinutes` 10, `RoadDetourFactor` 1.3, `GroundSpeedMetersPerSecond` 15, `LiftLoadsPerPlatoon` 3, `LiftEscortMinimum` 2, `LiftFundsMultiple` 2 (Operations section, Get/Set, warm-up, `<summary>` comments with the reasons in design Section 1/2/3).
- Pure `internal static float DriveMinutes(float meters, float detourFactor, float speedMetersPerSecond)` and `internal static bool PlatoonFlies(float driveMinutes, float thresholdMinutes, bool liftPossible)`.
- `private float DriveMinutesToObjective(FactionHQ hq, GlobalPosition objective)`: `TryNearestOwnedDepot` distance → `DriveMinutes`; `float.MaxValue` when no depot.
- `liftPossible` = `CommanderSupplyHeliService.Instance.HasLaunchableVehicleTransport(hq)` and a held base within `OperationsHeliInsertionRangeMeters` of the objective (reuse `TryFindInsertionLaunchBase`).
- Self-check: 34 km at 1.3 / 15 m/s ≈ 49 min; 10 km ≈ 14 min; 6 km ≈ 8.7 min not flying; flying needs liftPossible.

### [x] Task 3: Raising a platoon air-mobile
**Files:** Modify `Operations/CommanderOperationsFront.cs` (`PlanForwardBases` ~728 and the attack planner's platoon raise if separate), `Operations/CommanderPlatoon.cs` (`CommanderOperationsMission` ~332: add `AirMobile` bool and `LiftOrder` reference), `Operations/CommanderOperationsRequisitions.cs` (`PostForwardBaseRequisitions`, `PostRecipeShortfall`).
- When a mission would raise a NEW platoon and `PlatoonFlies(...)`: mark `mission.AirMobile = true`; its requisition lines use the air-mobile recipe `AirMobileRecipe` (Carrier 4, AirDefence 2, Armour 0 — constants with `<summary>`); the armour shortfall (`RecipeArmour`) is posted only once `DriveMinutesToObjective` is within the threshold (a FOB came online).
- The order book must NOT buy the air-mobile vehicles at a depot: an `AirMobile` mission's lines are excluded from `OpenRolesByPriority` (they are bought as cargo by the lift, Task 4). Pure `LinesBoughtByAir(airMobile, role)` + self-check (armour line still ground-bought when posted; carrier/AD lines by air).
- Log: `raises <platoon> air-mobile for <point>: <km> km from the nearest depot, <min> min by road`.

### [x] Task 4: Opening and flying a platoon lift
**Files:** Modify `Operations/CommanderOperationsFob.cs` (`DispatchFobAirFlights` ~1000, order creation), `Supply/CommanderSupplyHeliMission.cs` (`TryLaunchInsertionAircraft` ~554: add optional `IReadOnlyList<CommanderPlatoonRole>? wantedRoles` so the cargo chooser prefers the roles the recipe is still short of; `TryInsertionCombination` reads it — no change when null).
- `OpenPlatoonLift(hq, state, mission)`: one lift order per point (existing invariant), `Purpose = Platoon`, `LoadsWanted = LiftLoadsPerPlatoon`, `VehiclesPerLoad = LiftVehiclesPerLoad`, posts = the point's hold posts (`EnsureHoldPosts(point, 6)`), `LastProgressAt` now. Launch gate: `FobAffordable`-shaped `LiftAffordable(funds, price, multiple)` with `LiftFundsMultiple`; decline reason logged via `ReportFobDenial`.
- `DispatchFobAirFlights` passes `wantedVehicles: order.VehiclesPerLoad` and the remaining recipe roles; everything else (LZ scout, airdrop, heavy hull, abandoned-on-deck, stall clock, loss rules) is unchanged and now serves both purposes.
- Self-check: `LiftAffordable(100, 40, 2)` true; `(70, 40, 2)` false; `LiftAffordable` with multiple 1 equals `FobAffordable`'s funds test.
- Log: `lift 1/3 for 3RD PLATOON away`.

### [x] Task 5: Landed vehicles join the platoon
**Files:** Modify `Operations/CommanderOperationsFob.cs` (`TryTakeFobDelivery`), `Operations/CommanderOperationsInsertion.cs` (`NotifyPicketVehicleDelivered` ~1466, `AdoptPicketVehicle` ~694), `Operations/CommanderOperationsService.cs` (`TryFormPlatoon` / platoon membership).
- For a `Platoon` lift: each landed vehicle is adopted into the mission's platoon (create the platoon on the first landing if the mission has none, using the existing forming path so state, leader election and hold posts are the ones every platoon uses); `order.Delivered` counts loads (a load = `VehiclesPerLoad` landings, or the flight's actual cargo count).
- Completion: `Delivered >= LoadsWanted` → order closes, mission stays; log `3RD PLATOON complete on HILLTOP 9 by air; armour follows by road when a depot is within 10 min` and `3RD PLATOON: 2 vehicles landed on HILLTOP 9 (4/6)` per load.
- Cancel (FOB rules): `3RD PLATOON lift cancelled: <reason>`; the mission falls back to a driving raise (clear `AirMobile`).
- Self-check: `LoadsLanded(vehiclesLanded, perLoad)` (3 landed of 2 per load → 1 load, 4 → 2).

## Phase B — protection

### [x] Task 6: The lift cover sortie
**Files:** Modify `Operations/CommanderOperationsAirPlatoonCap.cs` (`AddTransportEscortDemand` ~207 becomes the lift cover's escort half), `Operations/CommanderOperationsAirPackages.cs` (`StrikeEscortWanted` ~92 reused), `Operations/CommanderOperationsAirArad.cs` (`InsertAradDemand` accepts a lift cover), `Operations/CommanderOperationsAirWing.cs` (`CommanderSortieKind.LiftCover` or reuse `Cap` with a lift reference — choose reuse if the demand walk can carry the order reference; document the choice).
- For every open lift order (both purposes): escorts = `StrikeEscortWanted(LiftEscortMinimum, hostileAirNow near zone or route, headroom)`; ARAD 1 when `TryFindInsertionRouteThreat` (Insertion.cs ~551) reports tracked air defence on the route or within the ARAD cluster distance of the zone; `AradPending` set until it goes in.
- Transport hold: `DispatchFobAirFlights` does not launch (denial `waiting for the escort` / `waiting for the sweep`) until the cover's escorts are airborne and ARAD has gone in, bounded by `PackageFormUpSeconds` from the cover's opening (then it goes anyway, logged once).
- Self-check: `LiftMayLaunch(escortsUp, escortsWanted, aradPending, secondsWaiting, formUpSeconds)` cases.
- Log: `lift 1/3 for 3RD PLATOON away (escort 2 of 2 up, sweep gone in)`, `lift for 3RD PLATOON holds at the form-up point: waiting for the sweep`.

### [x] Task 7: FOB construction flights take the same cover
**Files:** `Operations/CommanderOperationsFob.cs`.
- No new code beyond Task 6 if the cover keys on the order; verify the FOB purpose gets the hold and the log; add the FOB case to the `LiftMayLaunch` self-check.

## Phase C — visibility, docs, rebuild

### [x] Task 8: Markers and review line
**Files:** `Operations/CommanderOperationsAirMarkers.cs` (`FindFobFlight` label → `LIFT <purpose label> — n/N, outbound`; escorts `LIFT ESCORT <point>`), `Operations/CommanderOperationsMarkers.cs` (platoon marker `(air-mobile) 4/6` until complete), `Operations/CommanderOperationsDiagnostics.cs` (done in Task 1).

### [x] Task 9: CHANGELOG, decision log impact, settings docs
- CHANGELOG subsection under `## Unreleased` naming every new key and log line; append the Impact file list to DECISION-032 in `conductor/decision-log.md`.

### [x] Task 10: Full rebuild
- `--no-incremental` → 0 warnings 0 errors; list every new self-check name and every log line to watch for; tick tasks in this plan; set `metadata.json` loop_state to EXECUTE / PASSED.

## Verification in the running game (lead)
`Ground Control Duel Far`: `raises … air-mobile …` within 15 min; three `lift n/3 … away (escort 2 of 2 up …)`; `vehicles landed … (6/6)`; a platoon holding a point 30+ km from its depot; a FOB flight logging its escort; no `self-check FAILED`.

## DAG
A1 → A2 → A3 → A4 → A5 → B6 → B7 → C8 → C9 → C10. One executor.
