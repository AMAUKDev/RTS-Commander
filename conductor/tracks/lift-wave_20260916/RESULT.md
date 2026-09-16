# lift-wave_20260916 — RESULT

Status: **complete, not observed in game** (the developer's game was not running).

## What shipped

An air-mobile platoon lift launches every load it still owes in one review, to one landing zone,
under one escort, or it launches nothing. Forward-base construction is unchanged.

Decision record: `conductor/decision-log.md`, DECISION-046. Player-facing prose: `CHANGELOG.md`,
top of Unreleased.

## Was the one-cargo-run-per-point invariant lifted cleanly?

Yes, by giving each run an identity rather than by keying on anything else. `CommanderCargoFlightSlot`
holds the one definition — `Unslotted` (zero) and `Matches(runSlot, wantedSlot)` — and the id travels
on `QueuedCargoSpawn`, `PendingAircraftSpawn` and `CargoMission`. Six entry points take it and every
one defaults to zero, so the player's supply window, the SAM foundation drop, the naval run and every
picket insertion keep exactly the behaviour they had:

| Call | Was | Now |
|---|---|---|
| `CancelInsertion` | every run on the point | that load, when a load is named |
| `TryRedirectInsertion` | every run, one target | one call per load, each to its own hold post |
| `TryConvertInsertionToAirdrop` | first run found on the point | the load that actually stalled |
| `NotifyInsertionAircraft` | first record with no aircraft | the record that asked for the transport |
| `NotifyPicketVehicleDelivered` | first record on the point | the load that unloaded |
| `NoteInsertionLaunchFailed` | first undelivered record | the load abandoned on the deck |

The class lives outside `CommanderSupplyHeliService` for a concrete reason: that class's type
initializer cannot run outside a running game, so the rule could not otherwise have been self-checked.

**`pendingAircraftSpawn` did NOT need to hold more than one**, and this is stated plainly rather than
papered over. It is transient — set at spawn, cleared at registration in the same frame — and
requests arriving while it is occupied queue on `queuedCargoSpawns`, drained one per second by
`TryProcessQueuedCargoSpawns`. Three loads requested in one review leave the ground within about two
seconds of each other and cross the map together. The wave is one REVIEW, not one frame. Widening the
slot would have been a real concurrency risk in code that has already caused two incidents, for no
gain, and is recorded as out of scope.

## Files changed

- new `Supply/CommanderCargoFlightSlot.cs`
- `Operations/CommanderOperationsFob.cs` — six pure rules, `TryLaunchLiftLoad`,
  `RecallPartialLiftWave`, `WithdrawLiftFlight`, `CheckLiftWave`; `DispatchFobAirFlights` and
  `RedirectLiftFlights` rewritten; `CommanderFobFlight.SlotId` and `.LoadOrdinal`
- `Operations/CommanderOperationsService.cs` — `OperationsState.LastLiftSlotId`
- `Operations/CommanderOperationsInsertion.cs` — the three notify entry points
- `Operations/CommanderOperationsAirMarkers.cs` — per-transport load number
- `Supply/CommanderSupplyHeliMission.cs`, `Supply/CommanderSupplyHeliService.cs`,
  `Supply/CommanderSupplyHeliLandingZone.cs`
- `CHANGELOG.md`, `conductor/decision-log.md`

No new setting, no new service, no new Harmony patch, no new scheduler.

## Verification

`dotnet build` — 0 Warning(s) / 0 Error(s). Release built and installed to the plugins folder.

`CheckLiftWave` was split out of `CheckFob` precisely so it could be run outside the game, and passes
alone through a reflection harness. Seven defects were planted one at a time; each failed a NAMED
check and each file was restored byte-identical by sha256.

| Planted defect | Named check that caught it |
|---|---|
| wave size ignores what is already flying | a lift with its whole wave in the air raises nothing more |
| the ceiling is asked for one load, not the wave | four transports already out refuses a three-ship wave rather than sending two of it |
| the money gate prices one load, not the wave | a commander who can pay for two loads of three may not launch the wave |
| every load of a wave takes the same hold post | no two loads of one wave are ever sent to the same post |
| every transport prints the same load number | each transport of a wave prints the load it is carrying |
| the wave line does not say the lift went at once | a three-load wave says it went all at once |
| a slotted recall still reaches every load on the point | a slotted call never reaches the load flying beside it |

## Not observed: game not running; verify at next launch

Load `Ground Control Duel`, let a commander raise an air-mobile platoon, and read
`BepInEx\LogOutput.log` for:

- three `lift 1/3`, `lift 2/3`, `lift 3/3 … away` lines in one review, followed by one
  `the whole lift goes in ONE wave — 3 of 3 loads away together` line;
- no `self-check FAILED` line at load;
- when it cannot go: one `the whole lift goes at once or not at all: it wants 3 transports and only
  N of 6 may fly`, or one `the lift waits: … in the bank`, or the existing
  `holds at the form-up point` line;
- three transports on the map reading `1/3`, `2/3` and `3/3` rather than all three reading `1/3`.
