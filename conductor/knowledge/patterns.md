# Code Patterns & Conventions

Discovered patterns from the codebase (Ground Control (RTS), BepInEx mod for Nuclear Option).

## Architecture Patterns
- **Service + registry.** Every feature is a class implementing hooks from `Core/ICommanderService.cs`, registered once in `Core/CommanderModeController.cs`. Registration order = execution order. Advanced-tier services only run on gated missions (`Core/CommanderFeatureGate.cs`).
- **Partial classes per concern.** Large services are split into `Foo.cs`, `FooEnemy.cs`, `FooCatalog.cs`, `FooAir.cs` etc. Add a new partial rather than growing the main file.
- **Scheduler, not Update counters.** `CommanderScheduler.IsDue(ref next, interval)` for game logic (scaled time), `IsDueRealtime` for UI throttles. Constructors stagger their first run with `CommanderScheduler.Stagger("name", interval)`.
- **Settings via BepInEx config.** `Core/CommanderSettings.cs`: `Get("Section","Key",default)` / `Set(...)` pair plus a `_ = Prop;` touch in the warm-up list so the entry exists in the config file. `UiScale` is the one exception: a plain static, recomputed from screen height in `UI/CommanderUiScale.cs`.
- **Settings window.** `UI/CommanderOverlayUiSettings.cs` draws tabs by absolute `Rect`. Sliders follow `DrawRadiusSlider` / `DrawCameraSlider(y, label, value, min, max, format, suffix)`.
- **Self-checks as tests.** `SelfCheck()` static per subsystem, called from `Core/CommanderPlugin.Awake`. Logs `... self-check FAILED ...` via `CommanderPlugin.Log.LogError`.
- **Harmony patches beside their service** in `*Patches.cs`, `[HarmonyPatch]` static classes.
- **Server guard.** Anything mutating faction state checks `hq.IsServer` first.
- **Enemy AI seam.** AI services loop `FactionRegistry.GetAllHQs()` and `continue` on `ReferenceEquals(hq, localHq)` (`Ai/CommanderEnemyCommanderService.cs:190`, `Ai/CommanderEnemyCommanderDefence.cs:124`, `Ai/CommanderCaptureService.cs:516`, `Economy/CommanderEconomyServiceEnemy.cs:53`). "AI commander for the player" means making that skip conditional.

## Common Solutions
- Money: `hq.AddFunds(delta)`; buy a vehicle into the reserve: `hq.ModifyUnitSupply(def, 1)`.
- Spawning a building: `CommanderEconomyService.SpawnBuilding(...)` after `CommanderBuildPreview.IsSiteAllowed(...)`.
- Log lines are the UX for AI decisions: `CommanderPlugin.Log.LogInfo($"Enemy commander ({hq.faction.name}) ...")`.

## Anti-patterns to Avoid
- `Time.time` checks inline instead of the scheduler (breaks pause / 2x / 4x).
- Name tables of prefabs or vehicles where the game's own asset data (`vehicleType`, `roleIdentity`, `buildingType`) already answers the question.
- Two spenders drawing on one `factionFunds` pool without a reserve hand-off (`GetEnemyBuildReserve` exists for this).
- Registering logic in `Update` of a MonoBehaviour instead of a service hook.
