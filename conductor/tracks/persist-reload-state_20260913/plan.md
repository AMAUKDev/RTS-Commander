# Execution notes: Preserve mod state across hot reload

Working log for the approved design (`design.md`). Build command used throughout:
`dotnet build GroundControlRts.csproj -c Release` with `NUCLEAR_OPTION_DIR` set to the fallback
`I:\SteamLibrary\steamapps\common\Nuclear Option` (env var was empty in this shell).

Baseline: build clean (0 warnings, 0 errors) before any change.

## Task log

1. Added `Newtonsoft.Json` reference to `GroundControlRts.csproj` (HintPath into the game's
   `NuclearOption_Data\Managed`, `Private=false`, matching the other game references). Build: clean.
2. Added `Core/ICommanderPersistState.cs`-equivalent (`ICommanderPersistState` interface added to
   `Core/ICommanderService.cs`), `Core/CommanderStateModels.cs` (DTOs + writer/reader wrappers),
   `Core/CommanderStateStore.cs` (file owner, session guard, self-check), a `persistState` phase in
   `Core/CommanderServiceRegistry.cs`, the `Developer/PersistStateAcrossReload` setting in
   `Core/CommanderSettings.cs`, and wired `CommanderModeController` (registers the store, calls
   `WriteSnapshotOnDestroy()` from its own `OnDestroy`) and `CommanderPlugin.Awake` (self-check
   call). Build: clean, 0/0.
3. Added `AirCommand/CommanderAirCommandPersist.cs` (partial file, `ICommanderPersistState` added
   to `CommanderAirCommandService`'s interface list): saves live missions launched from a recipe
   (adopted airframes are out of scope — no recipe, not "ours" to relaunch) plus the relaunch
   queue; restores by `PersistentID.TryGetUnit`, rebuilding the recipe via an
   aircraft-`jsonKey`/mount-`jsonKey` lookup and an airbase-by-name lookup (falls back to any
   airbase the faction holds; a mission whose recipe cannot be rebuilt is still kept, just without
   auto-relaunch). `Returning`/`RtbIssued` re-derived from `pilot.currentState is
   AIPilotLandingState/AIHeloLandingState`. Build: clean, 0/0.
4. Added `Economy/CommanderEconomyServicePersist.cs` (partial file, `ICommanderPersistState` added
   to `CommanderEconomyService`) for mine/factory/dock levels by `PersistentID`, plus
   `CommanderStrategicPointService.AttachNearestMine` (new method, `Points/CommanderStrategicPointService.cs`,
   right beside the existing `AttachMine`) to reattach a restored mine to the nearest free resource
   site within 100 m. Build: clean, 0/0.
5. Fail-proofs (Testing rule 5) for both self-checks added in `CommanderStateStore.SelfCheck()`:
   - **Round-trip check**: planted `[JsonIgnore]` on `CommanderAirRecipeRecord.AircraftJsonKey`
     (`Core/CommanderStateModels.cs`). Confirmed by reading the code: the property would come back
     `""` instead of `"f22a"`, tripping the `AircraftJsonKey != "f22a"` branch and logging
     `State store self-check FAILED: the snapshot did not round-trip through JSON intact.` Reverted.
     SHA256 after revert: `be11dc8feb435ac0251723f876d34638540581bcd8f08a9586fee0d48f51c958`
     (matches the pre-edit hash exactly — byte-identical).
   - **Session guard check**: planted `<=` in place of `<` on the level-time comparison in
     `PassesSessionGuard` (`Core/CommanderStateStore.cs`). Confirmed by reading the code: a
     snapshot at exactly the current level time would then be wrongly accepted, tripping
     `PassesSessionGuard(guardSnapshot, "Guard Mission", 100f)` and logging
     `State store self-check FAILED: a snapshot at the current level time (not earlier) was accepted.`
     Reverted. SHA256 after revert: `b5fe73f028da37b62a8974dfbb1d27b635d2ffd50251e84f65aa6d5cad689da6`
     (matches the pre-edit hash exactly — byte-identical).
   Both fail-proofs were confirmed by reading the code (the self-checks run at plugin `Awake`
   inside the actual game, which this environment cannot launch), per the task's explicit
   instruction for this step.
6. Updated `CHANGELOG.md` (Unreleased > Added, developer-facing) and `BUILD.md` (new "Keeping Air
   Command missions and economy levels across a reload" subsection under Hot reload, and corrected
   the older "what a reload resets" line, which used to name mine/factory/dock levels). Build:
   clean, 0/0.
7. **Design §Section 4 (control-point ownership persistence): not done.** Sections 1-3 (mechanism,
   Air Command, economy) used the full task budget for a track of this size; point ownership was
   explicitly a stretch goal only if time remained. Nothing scaffolded for it — a future track can
   pick it up cleanly since `ICommanderPersistState` and the writer/reader already exist.

## Final state

- Build: `dotnet build GroundControlRts.csproj -c Release` → 0 Warning(s), 0 Error(s), every step.
- Files touched: `GroundControlRts.csproj`, `Core/ICommanderService.cs`,
  `Core/CommanderServiceRegistry.cs`, `Core/CommanderSettings.cs`, `Core/CommanderModeController.cs`,
  `Core/CommanderPlugin.cs`, `Points/CommanderStrategicPointService.cs`,
  `AirCommand/CommanderAirCommandService.cs` (interface list only), `Economy/CommanderEconomyService.cs`
  (interface list only), `CHANGELOG.md`, `BUILD.md`.
- Files added: `Core/CommanderStateModels.cs`, `Core/CommanderStateStore.cs`,
  `AirCommand/CommanderAirCommandPersist.cs`, `Economy/CommanderEconomyServicePersist.cs`.
- Not committed (no git repository in this working tree; the developer's instruction was also not
  to commit).

## Game-type facts confirmed by decompiling Assembly-CSharp (ilspycmd), since this codebase has no
   local source for them:
- `PersistentID` is a plain struct: `public uint Id`; `TryGetUnit(out Unit)` is an instance method
  forwarding to `UnitRegistry.TryGetUnit`. Constructed as `new PersistentID { Id = savedId }`.
- `GlobalPosition` is a `[FieldOffset]` struct with public `x, y, z` floats and a
  `(float,float,float)` constructor.
- `WeaponMount.jsonKey` and `UnitDefinition.jsonKey` (so `AircraftDefinition.jsonKey`) are public
  fields.
- `FactionHQ.GetAirbases()` returns `IEnumerable<Airbase>`.
- `Pilot.currentState` is `PilotBaseState`; `AIPilotLandingState`/`AIHeloLandingState` are concrete
  subclasses already used elsewhere in this codebase (`IssueReturnToBase`).

## Deviations from the design (filled in as they happen)

## Self-check fail-proof records (Testing rule 5)

Format per check: defect planted, expected failing log line, confirmation the check actually
failed, restore, and a SHA256 of the restored file to prove it is byte-identical to before.
