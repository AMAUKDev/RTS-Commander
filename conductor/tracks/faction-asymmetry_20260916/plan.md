# Plan — faction roster asymmetry

Eleven tasks. Each is one behaviour with its verification. Status as of 2026-09-16.

| # | Task | Verification | Status |
|---|---|---|---|
| 1 | New `Core/CommanderFactionRoster.cs`: the two predicates, the six roster tables, the side test, the capability axes | Compiles; `SelfCheck` passes in the harness | done |
| 2 | `SelfCheck` with a named `Expect` case per safety property, registered in `Core/CommanderPlugin.cs` | Seven planted defects, each caught by a named case, file restored byte-identical | done |
| 3 | Split the raw convoy walk out of `CollectFactionVehicleDefinitions` as `CollectConvoyGroupVehicles`, so the predicate can ask it without asking itself | Build clean | done |
| 4 | `CollectFactionVehicleDefinitions` walks a cached whole-game list through the predicate, which is what makes `FindRepairVehicleDefinition` return the Jackknife at last | In-game: repair crews hire, surface-to-air construction finds a Jackknife | pending in-game |
| 5 | The player's depot window: setting renamed `LimitToFactionRoster` and defaulted on, toggle relabelled | In-game: the depot window lists only this faction's kit | pending in-game |
| 6 | The computer's ground catalogue reads the shared collector instead of its own inline convoy walk | In-game: `Ground roster` lines stay distinct per side | pending in-game |
| 7 | The air-landed cargo gate in `TryGetVehicleCargo`, which takes the HQ | In-game: the `Insertion cargo roster` lines DIFFER between the two sides | pending in-game |
| 8 | The air gate in `PassesRoleCapability` (every computer air decision) and in the player's AIR window | In-game: each side buys only its own airframes | pending in-game |
| 9 | The air gate in `IsAvailableAirbase`, covering every transport and cargo launch decision | No-op today (both transports shared); build clean | done |
| 10 | Live coverage verdicts beside the existing roster log lines, logged as `self-check FAILED` when an axis is empty | In-game: no `FAILED` line in `LogOutput.log` | pending in-game |
| 11 | `CommanderSamSiteService` retrofitted onto the shared side test (Reuse rule 5) | Build clean; behaviour-neutral by inspection | done |

## Defect proofs (task 2)

Run against `Core/CommanderFactionRoster.cs`, baseline sha256
`3e7fcfc9e7f0cc01c37522fe403d7aa882f86c6fb503d3cf3793e8d3e4d221e6`, restored identical after every one.

| Planted defect | Named check that failed |
|---|---|
| Repair vehicle removed from the shared list | `the repair vehicle is shared`, plus both `covers every required axis` |
| Boscali loses every capture-capable air-landed vehicle | `Boscali can air-land something that takes ground` |
| Primeva loses anti-radiation missiles | `Primeva can clear an air-defence belt` |
| Both transports lose their cargo hold | `both sides can lift cargo`, plus both `covers every required axis` |
| The ground-attack aeroplane given to both sides | `CAS1 is not also Primeva's` |
| Primeva left with one air-superiority fighter | `Primeva has two air-superiority fighters` |
| An unknown faction restricted instead of left alone | `a third faction reads as unknown` |

The harness is a small net472 console programme that loads the built mod, fakes the plugin's log
sink and calls `CommanderFactionRoster.SelfCheck` alone, because the whole self-check set cannot run
outside the game. It lives in the scratchpad, not in the repository.

## In-game verification (the developer plays)

1. Load `Ground Control Duel`. Read `BepInEx\LogOutput.log`.
2. Confirm `Ground Control (RTS) 0.7.6.0 loaded`, no `self-check FAILED`, no exception.
3. `Ground roster (Boscali)` and `Ground roster (Primeva)` still list seven distinct types a side
   plus the shared M12 Jackknife, each ending with `covers every capability axis`.
4. `Insertion cargo roster (Boscali)` and `(Primeva)` must now DIFFER — Boscali AFV6, Primeva LCV25
   and Hexhound — and each must end `can take ground and defend it`.
5. `Air roster` lines still print all fourteen airframes, with the ones this side may not have
   marked `NOT ON THIS FACTION'S ROSTER`.
6. Open the depot window: only this faction's vehicles are offered.
7. Watch a few minutes of buying: each side buys only its own airframes.
