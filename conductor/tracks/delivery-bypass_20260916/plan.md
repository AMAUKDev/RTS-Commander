# Delivery Bypass Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use orchestrator-supaconductor:executing-plans to implement this plan task-by-task.

**Goal:** A cargo transport stops trying to land. It arrives, holds low and roughly still, the mod
places its vehicles on clear ground nearby, empties the aircraft and sends it home. Every delivery,
not only stalled ones (user decision, 2026-09-16).

**Architecture:** Design in `conductor/tracks/delivery-bypass_20260916/design.md` (§4.1–§4.7); the
measured evidence and the step-by-step delivery sequence in
`conductor/designs/2026-09-16-transport-delivery-survey.md` Part 2. All new supply-side code goes in
ONE new partial-class file, `Supply/CommanderSupplyHeliUnload.cs`, so the 3,148-line
`Supply/CommanderSupplyHeliMission.cs` — the file with the worst incident history in the repo (a
hand-off crash that stopped all transports spawning; a "no transports spawning at all" regression,
both recorded in its own comments) — takes only small, local edits and no rewritten method. No new
service, no new Harmony patch, no new scheduler, no new clock.

**Tech Stack:** C# / net472 / BepInEx 5 / Harmony. The developer is PLAYING right now and every hot
install wipes the commander's memory, so use `dotnet build` (no install) while working and do ONE
`$env:NUCLEAR_OPTION_DIR = "I:\SteamLibrary\steamapps\common\Nuclear Option"; .\build-and-install.ps1 -Dev`
at the very end (Task 12). Verification: `SelfCheck()` cases logged at plugin load, then
`BepInEx\LogOutput.log` in the running game.

**Rules that apply:** `.claude/CLAUDE.md` Reuse and Testing rules. Move code, never paraphrase it;
one definition, two callers; every threshold gets a NAMED `Expect` self-check; constants at class
level with a `<summary>` saying what the number means and why; the one new setting gets a `Get`/`Set`
pair and a warm-up touch. Never run a git command that changes state. Preserve each file's BOM and
CRLF (`Supply/*.cs` are CRLF without BOM; `Core/CommanderSettings.cs`,
`Operations/CommanderOperationsInsertion.cs`, `Ai/CommanderEnemyCommanderAirTasking.cs`,
`CHANGELOG.md`, `conductor/decision-log.md` are CRLF WITH BOM). Append only to `CHANGELOG.md` and
`conductor/decision-log.md` (DECISION-050).

**Explicitly out of scope** (design §6): the route watch's recall rule and anything about escorts.
Those are the two largest failure buckets and touching them here would confound the measurement.

---

## What was read first, and what is reused rather than written (Reuse rule 1 and 2)

| Needed | Existing thing reused | Why nothing new |
|---|---|---|
| "at the landing zone" ring | `CommanderOperationsService.InsertionStallRadiusMeters` (500 m) | Design §4.1 names it; it is already the stall clock's ring and already self-checked. |
| The bounded wait | `CommanderOperationsService.HasStalledAtLandingZone` + `CommanderSettings.OperationsInsertionStallTimeoutSeconds` (120 s) | Design §4.2: no new clock, no new setting. The stall path in `Operations/CommanderOperationsInsertion.cs:1463` and its twin in `Operations/CommanderOperationsFob.cs:2654` already run it. |
| Clear ground | `CommanderSupplyHeliService.TryFindClearLandingZone` (`Supply/CommanderSupplyHeliLandingZone.cs:382`) | Design §4.3 forbids a second clear-ground search. It already walks nearest-first over trees, static scenery, slope and sea level. |
| Putting a vehicle on that ground | `RightVehicle` in `Supply/CommanderSupplyHeliShield.cs:180` | It already does exactly this move (server-only, zero the velocities, snap to terrain, lift `RightingLiftMeters`). Reuse rule 5: the second instance means extracting the first. |
| Creating the vehicle | `Weapon.Fire` on a cargo mount, in `DeployNextAssignedCargo` (`Supply/CommanderSupplyHeliMission.cs:2170`) | Design §4.4. Confirmed in the decompile: `MountedCargo.Fire` sets `fired = true` and `ammo = 0`, and `MountedCargo.GetAmmoLoaded()` returns 0 once `fired` — which is the field `HasDeployableCargo` and `TrySelectNextCargoWeapon` read, so the aircraft reads empty to both the mod and the game. |
| Release spacing | `CargoMission.NextCargoReleaseAt` / `ReleasedCargoCount` / `ActivatedCargoCount` | Design §4.5. The 2.5 s landing gap and the released-vs-activated wait already exist in `DeployNextAssignedCargo`; only the ramp-clear drive has to go. |
| The trip home | `IssueInsertionReturnToBase` (`Supply/CommanderSupplyHeliMission.cs:279`) | Design §4.6. Already issued from the same routine once `DeliveryCompleted`. |
| The shield and the delivery credit | `ShieldDeliveredCargo`, `NotifyPicketVehicleDelivered`, `NotifyFoundationCargoActivated` | Design §4.6. All hang off cargo activation, which still happens. |
| The parachute fallback | `TryConvertInsertionToAirdrop` (`Supply/CommanderSupplyHeliLandingZone.cs:450`) | Design §4.2/§4.6. Becomes the SECOND path, taken only when the bounded wait expires too high. |
| The per-flight identity | `CommanderCargoFlightSlot.Matches` (`Supply/CommanderCargoFlightSlot.cs`) | A wave has several deliveries in the air for one point; the forced unload must address the RIGHT flight, exactly as the airdrop conversion does. |

**Correction to design §4.7, found while reading.** The design says the airframe fate line is written
only for the computer enemy commander because the service "skips the local faction". It does not skip
it: `TaskAirWing` runs for every commanded HQ including the player's. The real defect is that
`airborneSince` is ONE dictionary shared by every faction, and `ReportLostAircraft(hq)` drains all of
it under whichever HQ the review loop reaches first. Measured in `BepInEx\LogOutput.log`: 1,227 fate
lines, every one attributed to Primeva (the enemy), none to Boscali (the player), and 199 "recovered"
lines likewise. So the player's transports are unmeasured AND the enemy's numbers are inflated by the
player's losses. Task 11 fixes the attribution, which is what §4.7 actually wants.

---

## Tasks

### Task 1 [x]: The one setting and the constants
**Files:** `Core/CommanderSettings.cs` (Supply section, near `OperationsInsertionStallTimeoutSeconds`;
warm-up list ~:1006).
Add `SupplyUnloadInPlaceEnabled` (bool, default true) with the usual `Get`/`Set` pair, a comment
saying what it means and why it exists (an in-game off switch for the bypass, wanted because this is
the file with the worst incident history in the repo and the developer must be able to fall back to
the game's own landing gate without a rebuild), and a warm-up touch.
No other setting. Every threshold is a class constant in Task 2, because each one is pinned to a
number in the game's own code and retuning it from the config window would be a footgun.
Verify: `dotnet build` clean.

### Task 2 [x]: New file, the pure unload rule, the spacing rule, the self-checks
**Files:** Create `Supply/CommanderSupplyHeliUnload.cs` (`internal sealed partial class
CommanderSupplyHeliService`, namespace `GroundControlRts`, CRLF, no BOM, file header remark in the
voice of `Supply/CommanderSupplyHeliShield.cs`).
Class constants, each with a `<summary>` giving the number AND why it is that number:
- `UnloadHeightMeters = 25f` — the height at or below which a transport unloads in place. The game
  commands a 20 m auto-hover once inside 300 m of the touchdown point (survey Part 2 step 6) and only
  starts descending below that within 20 m horizontally, so the ceiling MUST clear 20 m or the normal
  path would almost never fire and every delivery would fall through to the bounded wait — which is
  the hang this track exists to remove. 25 m clears it with 5 m of margin for hover overshoot.
- `UnloadSpeedMetersPerSecond = 10f` — the game's OWN release gate speed (`speed < 10`, survey step
  7), kept unchanged on purpose: the only number the engine itself treats as "stopped". The bypass
  changes exactly one of the game's two gate numbers, the 2 m height.
- `ForcedUnloadHeightMeters = 40f` — the ceiling once the existing stall clock has expired (design
  §4.2). Higher than the normal ceiling so a transport that could not get down still delivers, and
  still low enough that the insertion shield covers the fall; above it the parachute path takes over.
- `UnloadVehicleSpacingMeters = 30f` — how far apart consecutive vehicles of one load are placed.
  Wider than the 20 m `OperationsLzClearRadiusMeters` half-width a transport needs to set down, so two
  vehicles of the same load never occupy each other's ground; the clear-ground search then walks out
  from each spot independently.

Pure rules, all `internal static` so the self-check can drive them:
- `UnloadsInPlace(float metresFromLandingZone, float radarAltMeters, float speed, float ringMeters,
  float heightCeilingMeters, float speedLimit)` — all three inside their bounds.
- `UnloadOffsetMeters(int releaseIndex, float spacingMeters, out float east, out float north)` —
  index 0 is the aircraft's own ground point; 1..4 are `spacingMeters` out on the four cardinal
  bearings, and it wraps (index 5 returns index 1's offset) so a load of any size answers.

`SelfCheckUnload()` with a private `Expect(List<string> failures, string name, bool actual, bool
expected)` in the style of `Ai/CommanderPlayerCommanderService.cs:191`, called from the FIRST line of
the existing `CommanderSupplyHeliService.SelfCheck()` in `Supply/CommanderSupplyHeliShield.cs:217` so
it is registered at plugin load through the one call already in `Core/CommanderPlugin.cs:39`. NAMED
cases, one per boundary:
- "a transport in the ring, low and slow unloads"
- "exactly on the height ceiling still unloads" / "one metre above the ceiling does not"
- "exactly on the speed limit does not unload" (the game's own gate is `speed < 10`, strict)
- "just inside the speed limit unloads"
- "exactly on the ring still unloads" / "one metre outside the ring does not"
- "the forced ceiling admits a height the normal ceiling refuses"
- "the forced ceiling still refuses a transport above it" (this is what keeps the parachute path)
- "the first vehicle is placed under the aircraft"
- "the second vehicle is a full spacing away"
- "opposite vehicles are two spacings apart"
- "a fifth vehicle wraps onto the first bearing"
Verify: `dotnet build` clean; the checks are exercised in Task 10's plant-a-defect proof.

### Task 3 [x]: Placing a vehicle on chosen ground — generalise `RightVehicle`
**Files:** `Supply/CommanderSupplyHeliShield.cs` (`RightVehicle`, the `ShieldRecord` class,
`SweepShields`).
MOVE the body of `RightVehicle` into `PlaceVehicleUpright(Unit unit, GlobalPosition ground)` (same
file, keeps the server-only guard, the kept heading, the zeroed velocities and `RightingLiftMeters`);
`RightVehicle` becomes a two-line caller that adds the log line. Reuse rule 5, behaviour-neutral —
the righting path must read identically afterwards.
Add `internal GlobalPosition? PlaceAt` to `ShieldRecord`. In `SweepShields`, BEFORE the on-ground /
upright / still measurements, if `record.PlaceAt` is set: call `PlaceVehicleUpright` with it, clear
`PlaceAt`, reset `record.StillSince = -1f`, log `"{Label} set down at {Where}."`, `continue`.
Why the sweep and not the activation postfix: the sweep already moves delivered vehicles every tick
and is proven; moving one inside the frame the engine is still activating it in is new risk in the
file with the worst incident history.
Verify: `dotnet build` clean; the righting self-checks in the same file still pass unchanged.

### Task 4 [x]: Choosing the ground, and asking for the placement
**Files:** `Supply/CommanderSupplyHeliUnload.cs`; `Supply/CommanderSupplyHeliShield.cs`
(`ShieldDeliveredCargo`, to accept the wanted ground).
`TryChooseUnloadGround(Aircraft aircraft, int releaseIndex, out GlobalPosition ground)`:
terrain under the aircraft (`CommanderGameAccess.SnapToTerrain(aircraft.GlobalPosition())`), offset
by `UnloadOffsetMeters(releaseIndex, UnloadVehicleSpacingMeters, …)`, then
`TryFindClearLandingZone(desired, out ground)`. When the search finds nothing clear inside its
radius, fall back to `SnapToTerrain(desired)` and return true anyway — design §4.3: the terrain
directly beneath is still better than not delivering. Log the fallback once per flight.
`ShieldDeliveredCargo` takes an optional `GlobalPosition? placeAt` and stores it on the record; every
existing caller passes nothing and is unchanged.
In `HoldDeployedCargo` (`Supply/CommanderSupplyHeliMission.cs:1873`), the ONE line
`ShieldDeliveredCargo(cargoUnit, mission);` becomes a call that passes the chosen ground when
`mission.UnloadInPlace` is set (Task 5 adds the flag), and nothing otherwise.
Verify: `dotnet build` clean.

### Task 5 [x]: The mission flag and the release routine's two bypass branches
**Files:** `Supply/CommanderSupplyHeliMission.cs` — `CargoMission` (~:3120, beside
`CargoClearancePending`); `HoldDeployedCargo` (~:1928 and ~:1945); `ShouldDelayAssignedCargoTakeoff`
(~:1854).
Add `internal bool UnloadInPlace { get; set; }` to `CargoMission` with a `<summary>` naming this
track and saying that it is latched once and never cleared, because a transport that has begun
unloading must not stop because it drifted.
In `HoldDeployedCargo`, the ramp-clear handshake is the ONE thing the bypass genuinely breaks
(design §4.5 and survey Part 3's table). Both starts of it — `if (cargoStillOnAircraft &&
!mission.Airdrop)` for a ground vehicle and the static-cargo `else if` below — gain
`&& !mission.UnloadInPlace`, so a bypassed load takes the SAME `else` branch an airdrop takes: hold
position, credit the foundation, credit the picket with `mission.InsertionFlightId`. Nothing is
paraphrased; two conditions gain one term each.
In `ShouldDelayAssignedCargoTakeoff`, the non-airdrop return gains
`|| (mission.UnloadInPlace && mission.InsertionPoint != null && !mission.ReturnIssued)`, mirroring
the airdrop branch two lines above: the game hands an emptied transport straight to its combat state,
which would take the airframe before the return flight is issued.
Nothing calls this yet. Verify: `dotnet build` clean; behaviour unchanged (`UnloadInPlace` is never
set).

### Task 6 [x]: The unload tick — every delivery, latched
**Files:** `Supply/CommanderSupplyHeliUnload.cs`; ONE call inserted in
`Supply/CommanderSupplyHeliMission.cs` `OverrideTransportTarget` immediately after the
`mission.LastTransportOverrideFixedTime = Time.fixedTime;` line (~:1609) and BEFORE the three-second
landing-spot throttle, because the release cadence needs a per-fixed-frame tick.
`TickUnloadInPlace(AIHeloTransportState state, Aircraft aircraft, CargoMission mission)`:
- Return at once unless `CommanderSettings.SupplyUnloadInPlaceEnabled`, `mission.InsertionPoint !=
  null`, `!mission.Airdrop`, `!mission.Cancelled`, `!mission.RouteTransitActive`,
  `mission.NavalTarget == null`, `mission.DepositSiteId < 0`, `mission.FoundationSiteId < 0`,
  `mission.JacknifeSiteId < 0`. The SAM platform and naval approaches keep their own arrival rules;
  this is the picket / forward-base / platoon-lift delivery only.
- If not yet latched, measure `CommanderGameAccess.HorizontalDistance(aircraft.transform.position,
  mission.Target.ToLocalPosition())`, `aircraft.radarAlt` and `aircraft.speed`, and latch when
  `UnloadsInPlace(…, CommanderOperationsService.InsertionStallRadiusMeters, UnloadHeightMeters,
  UnloadSpeedMetersPerSecond)`. On latching, log once through `CommanderAiLog.Note(mission.Hq, …)`:
  `"{point}: unloading in place at {radarAlt:0} m, {speed:0} m/s — the transport does not land."`
- Once latched, call the existing `DeployNextAssignedCargo(state)` every tick. Everything after that
  is the routine that already exists: the doors, the 2.5 s gap, the released-vs-activated wait, the
  delivery credit and `IssueInsertionReturnToBase`.
Verify: `dotnet build` clean.

### Task 7 [x]: The bounded wait — forced unload at the existing stall clock
**Files:** `Supply/CommanderSupplyHeliUnload.cs`; `Operations/CommanderOperationsInsertion.cs`
(~:1467, the stall branch); `Operations/CommanderOperationsFob.cs` (~:2656, its twin).
`internal bool TryForceUnloadInPlace(FactionHQ hq, CommanderStrategicPoint point, int flightId =
CommanderCargoFlightSlot.Unslotted)` in the new file, written in the shape of
`TryConvertInsertionToAirdrop` (same walk over `assignedMissions`, same per-HQ / per-point /
`CommanderCargoFlightSlot.Matches` guard, so a wave unloads the flight that stalled and not the one
beside it). It latches `UnloadInPlace` only when `UnloadsInPlace(…, ForcedUnloadHeightMeters, …)`
holds; otherwise it returns false and changes nothing.
In BOTH stall branches, the forced unload is tried FIRST and the existing airdrop conversion is
unchanged behind it:
`if (TryForceUnloadInPlace(...)) { NearLandingZoneSince = Time.time; log; continue; }`
`else if (!Airdrop && TryConvertInsertionToAirdrop(...)) { … as today … }`
`else { … recall as today … }`
Resetting `NearLandingZoneSince` gives the forced unload its own full window before the recall bites,
exactly as the airdrop conversion already does — the transport can never hang: it either unloads
within the window, or converts to a parachute drop, or is recalled.
Log line: `"{point}: could not land after {timeout:0} s; unloading in place at {alt:0} m."`
Verify: `dotnet build` clean.

### Task 8 [x]: Self-check that a bypassed delivery still credits its order
**Files:** `Supply/CommanderSupplyHeliUnload.cs` (`SelfCheckUnload`).
Design §5 acceptance 1 asks for it explicitly. The credit is `UpdateDeliveryCompleted`'s rule, so add
a pure twin `DeliveryComplete(int activated, int expected, bool cargoStillAboard, bool clearancePending)`
in the new file, make `UpdateDeliveryCompleted` call it (Reuse rule 4 — one definition, two callers),
and add NAMED cases: "a full load with an empty aircraft is delivered"; "a load short of expected is
not"; "cargo still aboard is not"; "a pending ramp clearance is not"; and the one this track needs —
"a bypassed load with no ramp clearance still completes". Behaviour-neutral for the landing path.
Verify: `dotnet build` clean.

### Task 9 [x]: Prove the placement and the ramp-clear seam by reading, not by guessing
**Files:** none changed.
Walk the bypass end to end against the code and write the findings into the task record: which field
proves the aircraft reads empty (`MountedCargo.fired` → `GetAmmoLoaded() == 0` → `HasDeployableCargo`
and `TrySelectNextCargoWeapon` both false → `DeployNextAssignedCargo` takes the return-to-base
branch); that `ActivateCargoVehicle` runs AFTER `PushCargoOut`'s loop so the postfix sees a vehicle
clear of the aircraft; and that nothing else sets `CargoClearancePending` on a bypassed flight.
If any link does not hold, STOP and report rather than patching around it.

### Task 10 [x]: Plant a defect, watch a NAMED check fail, restore (Testing rule 5)
**Files:** temporarily `Supply/CommanderSupplyHeliUnload.cs`, then restored.
Three separate proofs, each built with `dotnet build` and each restored and confirmed byte-identical
by `sha256sum` before the next:
1. Invert the height comparison in `UnloadsInPlace` → the named height-ceiling checks must fail.
2. Make `UnloadOffsetMeters` return the same offset for every index → the spacing checks must fail.
3. Make `ForcedUnloadHeightMeters` smaller than `UnloadHeightMeters` → the "forced ceiling admits a
   height the normal ceiling refuses" case must fail.
The whole operations self-check cannot run outside the game (it throws building a Unity object in
`CheckAirSuperiorityRefusal`), so run the new block alone in a small harness if one is needed, and
record which NAMED check failed each time.

### Task 11 [x]: The fate line, attributed to the faction that lost the airframe (design §4.7)
**Files:** `Ai/CommanderEnemyCommanderAirTasking.cs` (`TrackAircraft` ~:177, `ReportLostAircraft`
~:202); `Ai/CommanderEnemyCommanderService.cs` (the `TrackedAirframe` class, wherever it is declared).
Add `internal FactionHQ? Owner` to `TrackedAirframe`; `TrackAircraft(FactionHQ hq, Aircraft aircraft)`
sets it on every tick (an airframe that changes hands is re-attributed); `ReportLostAircraft(hq)`
collects only entries whose `Owner` is `hq`, so the first faction the review loop reaches stops
draining every other faction's losses under its own name. Nothing else about the line changes.
Verify: `dotnet build` clean, then in Task 12 look for a `Player commander (…) lost … in the air;
last seen …` line, of which the current 38,388-line log has none.

### Task 12 [x]: CHANGELOG, decision log, build, ONE install, watch
**Files:** `CHANGELOG.md` `## Unreleased` (append; one paragraph naming the setting, the constants and
the log lines); `conductor/decision-log.md` DECISION-050 (append; the every-delivery choice, the
bounded wait, and the §4.7 correction); `conductor/tracks.md` row updated to EXECUTE/complete.
Then the ONE hot install, as above. Wait ~15 s; confirm a new `Ground Control (RTS) 0.7.6.0 loaded`
with no `self-check FAILED` and no exception. Then watch for:
- `unloading in place at … m` and vehicles appearing without a touchdown
- `set down at` from the shield sweep
- `settled at … shield off` and `delivered (n/m)` — the two lines that prove a vehicle arrived
- `could not land after … s; unloading in place` (the bounded wait firing)
- `Player commander (…) lost … in the air; last seen …`
Report which were and were not observed, and whether a delivery happened with no touchdown.

## DAG
1 → 2 → 4, 6. 3 → 4. 5 → 6, 7. 6 → 7. 2 → 8, 10. 9 after 6. 11 independent. 12 last.

## Review
One independent review of the full diff (second-opinion `default` panel), one evaluation against
design §5, at most two fix cycles.

---

## Execution record

`[x]` done as written, `[-]` stopped under the task's own escape hatch. One executor, 2026-09-16.

Deviations from the plan as written, all recorded in DECISION-050:
- Task 2 split into two files rather than one. The constants, the pure rules and the named cases went
  into a NEW `Supply/CommanderCargoUnloadRule.cs` with no Unity, Harmony or game-assembly dependency,
  for the same reason `Supply/CommanderCargoFlightSlot.cs` has none: the supply service's own type
  initializer cannot be run in a harness, so the decision table has to stand on its own to be proven
  outside the game. `Supply/CommanderSupplyHeliUnload.cs` keeps the runtime half.
- Task 10 ran four plant-a-defect proofs rather than three; the fourth pins that the unload height
  clears the game's 20 m hover, which is the single number that decides whether the bypass fires at
  all or every delivery waits out the stall clock.
- Task 4's ground chooser is wrapped in a try/catch. It runs inside a Harmony postfix on the engine's
  own cargo activation, and an escaping exception there is the shape of two incidents this delivery
  path has already had.
- Task 11 fixes a different defect from the one the design describes; see the correction in the plan
  above and in DECISION-050.
