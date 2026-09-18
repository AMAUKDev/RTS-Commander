# Strategic save and load — spec

Track: `strategic-save_20260917`. Type: feature. Created 2026-09-17.

Study: `conductor/designs/2026-09-17-save-load-study.md`. Read it first; its investigation is not
repeated here. This track ships the study's **Option A** (mod-only snapshot, mod rebuilds what it
owns), narrowed by the developer to the strategic layer only.

## Goal

The developer stops a long match, restarts the mission to clear the frame-rate decay, then reloads
and carries on with the same strategic picture: the same points held, the same forward bases, and a
war chest that holds the money plus the cash value of the army that was standing.

## Problem, with evidence

1. **Nothing can be restored across a mission restart at all.** `CommanderStateStore.TryRestore` is
   only reached when `IsHotReloadLoad` is true (`Core/CommanderStateStore.cs:84`), and
   `PassesSessionGuard` demands the saved level time be *earlier than and within 600 s of* the
   current level time (`:170-175`). A mission restart is not a hot reload and starts the level clock
   at zero, so both tests fail. This is task one; without it nothing else can be verified.
2. **A missing flag hands every computer faction a second opening balance.**
   `CommanderEconomyService.ShouldOpenTreasury(isLocal, alreadyPrepared)` is `!isLocal &&
   !alreadyPrepared` (`Economy/CommanderEconomyService.cs:946`), and `alreadyPrepared` is
   `CommanderState.Prepared` (`Ai/CommanderEnemyCommanderService.cs:1456`), which
   `ResetSession` clears (`:336`). Restore the money without the flag and the next review re-opens
   every hostile treasury on top of it. Nothing logs.
3. **Every stored clock reads as far in the future after a restart.** Every cooldown in the mod is a
   `Time.time` stamp — `OperationsState.FobCooldownUntil`, `LiftCooldownUntil`,
   `InsertionCooldownUntil`, `StrikeCooldownUntil`, `InsertionPauseUntil`, `FobLossCooldownUntil`,
   `CommanderState.ThreatUntil`, and the per-platoon, per-mission and per-sortie stamps. A saved
   value in the thousands replayed against a clock at zero pauses that behaviour for the match.
4. **Forward-base records could outlive their buildings.** The mod injects a `SavedAirbase` into the
   live mission (`Economy/CommanderFobBuilder.cs:879-880`) but spawns its structures with no saved
   record, so a base record can come back with nothing standing under it.
5. **A held control point is lost the moment it is judged with nothing on it.** `Step` sets
   `OwnerIndex = -1` on the first tick with no qualifying garrison
   (`Points/CommanderStrategicPointService.cs:449-454`), and the tick runs every
   `HoldCheckSeconds = 5f` (`:24`). Restoring ownership onto an empty map loses every point in five
   seconds.
6. **A `PersistentID` is a counter that restarts.** `UnitRegistry.Clear()` resets `nextIndex` to
   zero (study Part 1). Every id-keyed record already in the snapshot — air missions, mine, factory
   and dock levels, `CommanderStrategicPointRecord.MinePersistentId` — would resolve to a *different*
   unit after a mission restart. Replaying them is worse than dropping them.

## What already exists and is reused, not rewritten

| Need | Existing code reused | Why it fits / what is added |
|---|---|---|
| A snapshot file, JSON round trip, writer/reader wrappers | `Core/CommanderStateStore.cs`, `Core/CommanderStateModels.cs` | Same file format, same `CommanderStateSnapshot`, same `Newtonsoft.Json` round trip. Added: a second file name, a second gate, a second interface. The hot-reload path is untouched. |
| Per-service opt-in persistence | `ICommanderPersistState` + the registry fan-out (`Core/CommanderServiceRegistry.cs:132,153`) | Does not fit as-is: it restores id-keyed hot-reload records that are poison after a restart (problem 6). A parallel `ICommanderPersistStrategic` with the same shape and the same fan-out is added beside it. |
| Points, owners and roads | `Points/CommanderStrategicPointPersist.cs` (`ToRecord`/`FromRecord`, owner by faction NAME) | Fits exactly and is the discipline problem 6 demands. Reused verbatim; the mine id is the one field suppressed. |
| Building a forward base from a record | `CommanderEconomyService.TryBuildFob(hq, position, label, built, out airbase)` (`Economy/CommanderFobBuilder.cs:804`) | The rebuild path. Already creates the `SavedAirbase`, spawns it and puts the recipe down. |
| The forward base's price | `CommanderEconomyService.FobStructuresCost()` (charged at `Operations/CommanderOperationsFob.cs:986,992`) | The refund for a base still being delivered. |
| Garrison positions | `CommanderOperationsService.EnsureHoldPosts(point, slots)` (`Operations/CommanderOperationsFront.cs:421`) | Already terrain-snapped, off-runway and above sea level; already the ring the picket insertion lands on. |
| Which vehicle garrisons a point, and its price | `CommanderEnemyCommanderService.ChooseCaptureUnit(budget)` (`Ai/CommanderEnemyCommanderService.cs:1181`) over `CommanderGameAccess.CollectFactionVehicleDefinitions(catalog, hq)` | The commander's own "cheapest thing that can move a capture bar", at the commander's own price (`definition.value`). Extracted to a shared static under Reuse rule 5 and retrofitted, behaviour-neutral. |
| Putting a vehicle on chosen ground | `NetworkSceneSingleton<Spawner>.i.SpawnVehicle(...)` as used by `CommanderMobileEmplacementService.RestoreTrailer` (`Units/CommanderMobileEmplacementService.cs:423`) | The mod's one existing spawn-a-vehicle-here primitive. |
| Cash value of a live unit | `definition.value` reads at `Operations/CommanderOperationsAirPlatoonCap.cs:141` (aircraft) and `Operations/CommanderOperationsService.cs:800` (vehicles); `CommanderEconomyServiceCatalog.GetStructureCost` (buildings) | One shared `StrategicUnitValue(Unit)` over them, so there is no second valuation. |
| "Did the commander build this?" | `CommanderEconomyService.WasBuiltByCommander(unit)` (`Economy/CommanderEconomyService.cs:186`) | Stops mission-authored buildings being cashed in. |
| Named checks at plugin load | The `Expect(List<string> failures, ...)` pattern in ten files | Followed, not extracted. |

Nothing else was found. **No new service was found that already does any of this**: there is no
existing save trigger, no existing garrison spawner, no existing treasury restore.

## Decisions taken by the developer (recorded once)

1. **Units are refunded at their value, never recreated.**
2. **Forward bases are rebuilt where they were.** A base still being *delivered* at save time is
   cashed in instead, because the transport it was waiting on no longer exists.
3. **A held point gets a garrison placed directly on load**, or it is lost in five seconds.
4. **Conservation of value:** those garrisons are paid for out of the war chest at the commander's
   own prices. If the chest runs short, points are restored in the commander's own priority order
   until the money runs out, and the rest are logged as lost and why.

## Requirements

- **R1 Gate.** An explicit save writes its own file, `<mission>.strategic.json`, which never expires
  and is accepted on a fresh mission run. The hot-reload file, its gate and its behaviour are
  unchanged. A consumed strategic file is renamed, not replayed.
- **R2 Contents.** Point kinds, positions, radii, labels, wooded marks and owners *by faction name*;
  the retained road polylines; one online forward base per record (faction name, point label,
  position); one treasury per faction; the set of factions already prepared. **No `PersistentID`
  anywhere, no clock stamp that is replayed, no positions, damage, ammunition, loadouts or anything
  in flight.**
- **R3 Treasury.** Saved funds = `hq.factionFunds` + the cash value of every live aircraft and
  ground vehicle of that faction + every building that faction's commander built and that is not
  part of a forward base + the structure stake of every forward-base order still delivering.
- **R4 Load.** Restore owners and roads, set each faction's funds, rebuild each online forward base
  in place, then place and pay for a garrison on every point a faction held, in priority order.
- **R5 No point is judged before its garrison is down.** The hold tick is held off while the rebuild
  is outstanding, with a timeout so a failed rebuild can never freeze the match.
- **R6 Trigger.** Buttons in the settings window. Saving is safe mid-match and does not stall the
  frame beyond one file write.

## Acceptance criteria

- `dotnet build -c Release` ends `0 Warning(s)` / `0 Error(s)`.
- Every rule above has a NAMED `Expect` case registered at plugin load, and each named gate is
  proved by planting a defect, watching that named check fail, and restoring the file byte-identical
  by sha256.
- In the running game: save in a match, restart the mission, and read `BepInEx\LogOutput.log` for
  the strategic restore lines; the points held, the forward bases and the money match what was saved,
  and no faction is handed a second opening balance.

## Files in scope

New: `Core/CommanderStrategicSaveStore.cs`, `Core/CommanderStrategicSaveModels.cs`,
`Operations/CommanderOperationsStrategicPersist.cs`, `Ai/CommanderEnemyCommanderStrategicPersist.cs`.
Edited: `Core/ICommanderService.cs`, `Core/CommanderServiceRegistry.cs`,
`Core/CommanderModeController.cs`, `Core/CommanderPlugin.cs`, `Core/CommanderSettings.cs`,
`Points/CommanderStrategicPointPersist.cs`, `Points/CommanderStrategicPointService.cs`,
`Economy/CommanderEconomyServicePersist.cs`, `Ai/CommanderEnemyCommanderService.cs`,
`UI/CommanderOverlayUiSettings.cs`, `CHANGELOG.md`, `conductor/decision-log.md`.

## Out of scope

Platoon rosters, orders in flight, unit positions, damage, ammunition, loadouts, the enemy
commander's plan and standing posts, mine/factory/dock upgrade levels, multiplayer clients, and any
change to the hot-reload snapshot's behaviour.
