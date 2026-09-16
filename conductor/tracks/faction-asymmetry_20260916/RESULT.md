# Result — faction roster asymmetry

Date: 2026-09-16. Recorded by the executing agent. See DECISION-044 in `conductor/decision-log.md`.

## What shipped

One ground predicate and one air predicate, in a new static class `Core/CommanderFactionRoster.cs`.

```
CommanderFactionRoster.MayFieldVehicle(hq, definition, route)   // route: Depot | AirLanded
CommanderFactionRoster.MayFlyAircraft(hq, definition)
```

Callers, all of them:

| Path | Where it reads the predicate |
|---|---|
| Player's depot window | `CommanderGameAccess.CollectFactionVehicleDefinitions`, via `CommanderSettings.LimitToFactionRoster` (renamed, default now on) |
| Factory production, player and computer | the same collector |
| Repair-vehicle lookup, building repair and site construction | the same collector, through `FindRepairVehicleDefinition` |
| Computer's ground catalogue | `CommanderEnemyCommanderService.CollectCatalog`, now calling the shared collector |
| Air-landed insertion cargo, forward bases, platoon and picket lifts | `CommanderSupplyHeliMission.TryGetVehicleCargo` |
| Player's AIR window | `CommanderAirCommandService.RefreshOptions` |
| Every computer air decision | `PassesRoleCapability` — buy, fund ceiling, role-candidate test, rotary launch distance, loadout choice |
| Every transport and cargo launch | `CommanderSupplyHeliService.IsAvailableAirbase` |

## The rosters

Ground, bought at a depot — the game's own convoy groups, which the mod no longer flattens:

| Capability | Boscali | Primeva |
|---|---|---|
| Main battle tank | Type-12 MBT (11) | Spearhead MBT (15) |
| Infantry fighting vehicle | AFV8 IFV (6) | Linebreaker IFV (8) |
| Armoured personnel carrier | AFV8 APC (5) | Linebreaker APC (7) |
| Short-range launcher | AFV8 Mobile Air Defense (8) | Linebreaker SAM (10) |
| Anti-aircraft gun | FGA-57 Anvil (9) | AeroSentry SPAAG (8) |
| Long-range launcher | T9K41 Boltstrike (13) | T9K41 Boltstrike (13) |
| Munitions truck | HLT Munitions Truck (2) | MSV Munitions (2) |
| Repair | M12 Jackknife — **shared** | M12 Jackknife — **shared** |

Ground, air-landed — the mod's own split of the cargo manifest:

| | Boscali | Primeva |
|---|---|---|
| Light family | AFV6 APC, AFV6 IFV, AFV6 AT, AFV6 AA | LCV25 AT, LCV25 AA |
| Point defence | — | Hexhound SAM, Hexhound GMG |
| Shared | M12 Jackknife, HLT Radar Truck | same |

Air:

| | Boscali | Primeva |
|---|---|---|
| Fighters | FS-12 Revoker (65), Alkyon AB-4 (390) | FS-20 Vortex (90), KR-67 Ifrit (126) |
| Dedicated ground attack | SFB-81 Darkreach (225) | A-19 Brawler (36) |
| Bottom tier | CI-22 Cricket (12), T/A-30 Compass (22) | VT-7 Vagrant (29) |
| Shared | SAH-46 Chicane (31), EW-25 Medusa (145), UH-90 Ibis (30), VL-49 Tarantula (118) | same |

Four shared airframes, one shared ground type bought at a depot, two shared air-landed.
The 1996-cost joke entry (`UFO`) is on neither list and is now flown by neither side.

## The price imbalance, and what was done

The study's own Split B pairs the cheap A-19 Brawler with Boscali, which already has the cheaper
fighter. That leaves Primeva paying 225 for its only dedicated ground-attack aeroplane — poorer at
everything. The two ground-attack aeroplanes are **swapped** instead. Cheapest price to cover each
capability:

| Capability | Boscali | Primeva |
|---|---|---|
| Air superiority | 65 | 90 |
| Suppress radar | 65 | 90 |
| Ground attack | 65 | 36 |
| Whole fighter ladder | 455 | 216 |

Each side has a real advantage, neither is priced out of anything, and the shared count stays at
four rather than the five the study's suggested remedy would have needed. Boscali keeps the only
bomber; the strike-package planner already degrades around a side without one.

## One row of the study's Split B that was deliberately not implemented

Split B's table swaps the long-range launcher: Boscali keeps the T9K41 Boltstrike, Primeva takes the
Hexhound SAM instead. That is not done, and the Boltstrike stays in both sides' depot lists, which is
why one ground type is still shared. Two reasons. The Hexhound SAM has no depot path at all — it
exists only as aircraft cargo — so Primeva would have been left with a long-range launcher it could
only fly in, and with nothing to buy at a depot in that role. And removing a vehicle the game itself
issues to a faction would be the mod overriding the game's own roster, which is the exact behaviour
this track exists to stop. Primeva does get the Hexhound SAM as air-landed cargo, which is the
achievable half of that row.

## Self-checks and defect proofs

`CommanderFactionRoster.SelfCheck()`, registered in `Core/CommanderPlugin.cs`. Seven planted
defects, each caught by a named case, source restored byte-identical every time (baseline sha256
`3e7fcfc9e7f0cc01c37522fe403d7aa882f86c6fb503d3cf3793e8d3e4d221e6`). Full table in `plan.md`.

Two runtime verdicts were added beside the existing roster log lines, because the half that depends
on the game's own data cannot be checked from a table at load: `LogRosterCoverage` in the ground
roster block, and the air-landable verdict in `LogInsertionRosterOnce`. Both log a
`self-check FAILED` line naming what is missing.

## Build and install

`dotnet build` clean throughout. One `.\build-and-install.ps1 -Dev` at the end, `0 Warning(s)` /
`0 Error(s)`; a second was needed after a late fix to the shared-vehicle lookup. The running game
reloaded cleanly: `Ground Control (RTS) 0.7.6.0 loaded`, no `self-check FAILED`, no exception.

## Still to verify in the running game

The mission was idle at the time of the install, so no commander review ran. The steps and the
exact log lines that prove each are in `plan.md`. The one that matters most: the
`Insertion cargo roster` lines for Boscali and Primeva must now DIFFER, where before the change
they were character-for-character identical.

## Known gap

The engine's own aircraft restriction list is not written, so a human player can still walk into a
hangar and take the other side's fighter through the game's own selection screen. Reasoning in
`spec.md`, "Out of scope".
