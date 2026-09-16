# Insertion Shield Implementation Plan

Done by the lead in one pass (2026-09-15), small track.

- [x] Task 1: `Supply/CommanderSupplyHeliShield.cs` — shield set, pure rules, sweep, righting, self-check.
- [x] Task 2: `HoldDeployedCargo` shields every activated cargo vehicle; `TickPersistent` sweeps; `ResetSession` clears.
- [x] Task 3: Harmony prefix on `UnitPart.TakeDamage` in `Supply/CommanderSupplyHeliPatches.cs`.
- [x] Task 4: `CommanderSupplyHeliService.SelfCheck()` registered in `Core/CommanderPlugin.cs`; CHANGELOG; decision log.
