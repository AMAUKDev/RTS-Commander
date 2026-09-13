# Plan: ui-scale-slider

**Track**: `ui-scale-slider_20260913` · **Spec**: `spec.md` (binding; this plan follows its file:line
references, it does not redesign) · **Executor**: one agent, tasks in order, `dotnet build
GroundControlRts.csproj -c Release` after every task (0 warnings, 0 errors). Do NOT run
`build-and-install.ps1` (the game may be running). Do NOT commit.

If a fresh shell lacks `NUCLEAR_OPTION_DIR`, set it to `I:\SteamLibrary\steamapps\common\Nuclear Option`.

## Read first (Reuse rule 1)

- `UI/CommanderUiScale.cs` (whole file, 53 lines) — the preset ladder and the GUI matrix path.
- `Core/CommanderSettings.cs:12` (`UiScale` plain static), `:29-33` (float `Get`/`Set` pairs),
  `:131-203` (warm-up touch list), `:225-247` (`Get`/`Set`/`GetEntry`).
- `UI/CommanderOverlayUiSettings.cs:324-372` (`DrawCameraSlider`, `DrawUiSettings`).
- `Camera/CommanderCameraTuning.cs:94-140` — the `SelfCheck` / `Expect` / `ExpectTrue` shape to copy.
- `Core/CommanderPlugin.cs:32-37` — self-check registration.
- `UI/CommanderOverlayUi.cs:228-269` — window layout; the `ClampWindow` calls at 266-269 run
  every frame, outside the `positionsInitialized` guard.

## Design facts fixed by the spec

- Config key `UI/UiScaleOverride`, float, default `0` = automatic.
- Effective scale = override when `> 0`, else the automatic preset. Resolution tracking keeps
  writing the automatic value and never touches the override.
- Slider 0.75x to 2.5x, step-free, label with two decimals and `x` suffix, plus an AUTO button.
- Self-check lives in `CommanderUiScale.SelfCheck()`, registered in `Core/CommanderPlugin.cs`.
- Constants get a `<summary>` saying what the number is and why (CLAUDE.md).

## Tasks

- [x] **T1 — Persisted override setting.** In `Core/CommanderSettings.cs` add
  `UiScaleOverride` as a `Get("UI", "UiScaleOverride", 0f)` / `Set(...)` pair beside
  `MapDragSpeed` (line 33), with a comment: `0` means automatic; any positive value is a manual
  multiplier that wins over the resolution preset. Add `_ = UiScaleOverride;` to the warm-up list
  next to `_ = MapDragSpeed;` (line 171). Build.

- [x] **T2 — Automatic value and pure resolve function.** In `UI/CommanderUiScale.cs`:
  - Extract the preset ladder from `ApplyResolutionPreset` (line 20) into
    `internal static float PresetForHeight(int screenHeight)` — MOVE the expression, do not
    retype it. Give the three preset values and the two height thresholds named constants with
    `<summary>` (1.0x below 1200 px tall, 1.25x up to 1600 px, 1.5x above: the heights of 1080p,
    1440p and 4K class displays).
  - Add `internal static float Resolve(float overrideScale, float automaticScale)` returning the
    override when `> 0f`, else the automatic value. Pure, no `Screen` access, so the self-check
    can call it.
  - Add `internal const float MinOverride = 0.75f` and `MaxOverride = 2.5f` with `<summary>`
    (slider bounds from the spec; below 0.75 text is unreadable, above 2.5 a single window
    exceeds a 1080p screen).
  - Replace the plain static `CommanderSettings.UiScale` (`Core/CommanderSettings.cs:12`) with:
    `internal static float AutomaticUiScale { get; set; } = 1.5f;` (keep the comment that this
    is the one plain static, derived from screen size, not the config file) and
    `internal static float UiScale => CommanderUiScale.Resolve(UiScaleOverride, AutomaticUiScale);`
    (read-only).
  - `ApplyResolutionPreset` writes `CommanderSettings.AutomaticUiScale = PresetForHeight(Screen.height)`.
    `RefreshResolutionPreset` is unchanged: it still only re-applies on a size change, and now
    can only ever touch the automatic value (requirement 2).
  - Build. Confirm with Grep that no other file assigned `CommanderSettings.UiScale` (only
    `CommanderUiScale.cs:20` did).

- [x] **T3 — Self-check.** Add `internal static void SelfCheck()` to `UI/CommanderUiScale.cs`,
  copying the `failures` list / `Expect` / `ExpectTrue` / `LogDebug` passed / `LogError
  "UI scale self-check FAILED: ..."` shape from `Camera/CommanderCameraTuning.cs:94-140`
  (private helpers are per-class there; copy the two small helpers rather than making them
  shared — they are four lines each). Cases:
  - override wins: `Resolve(1.5f, 1f) == 1.5f`
  - automatic when zero: `Resolve(0f, 1.25f) == 1.25f`
  - negative is treated as automatic: `Resolve(-1f, 1.25f) == 1.25f`
  - bounds ordered: `MinOverride < MaxOverride`
  - ladder: `PresetForHeight(1080) == 1f`, `PresetForHeight(1440) == 1.25f`,
    `PresetForHeight(2160) == 1.5f`
  - every preset lies inside `[MinOverride, MaxOverride]` — otherwise the slider's clamp would
    silently turn an untouched AUTO value into a manual override on the first frame (see T4).
  Register `CommanderUiScale.SelfCheck();` in `Core/CommanderPlugin.cs` after
  `CommanderCameraTuning.SelfCheck();` (line 37). Build.

  Testing rule 5 (prove the check fails): temporarily set `MinOverride = 3f`, build, read the
  code path to confirm the "bounds ordered" case would add a failure (no game run needed for a
  pure-function check); restore, build, confirm the diff of that constant is byte-identical to
  the intended value. Record in the task notes that this was done.

- [x] **T4 — Slider replaces the read-only label.** In `UI/CommanderOverlayUiSettings.cs`
  `DrawUiSettings` (line 338) replace the label at lines 359-363 with a `DrawCameraSlider` call
  at `y + 260f`, label `"UI scale"`, value `CommanderSettings.UiScale`, bounds
  `CommanderUiScale.MinOverride` / `MaxOverride`, format `"0.00"`, suffix `"x"`. Write
  `CommanderSettings.UiScaleOverride` only when the returned value differs from the effective
  value (`!Mathf.Approximately`), with a comment explaining why: an untouched slider must not
  convert an automatic value into a manual override, and BepInEx writes the config file on
  every changed `Value`. Build.

- [x] **T5 — AUTO button and automatic-value readout.** Directly below the slider (`y + 292f`,
  height 26) draw a `GUI.Button` `"AUTO"` (`CommanderUiTheme.Button`, about 110 wide at
  `x = 28f`) that sets `CommanderSettings.UiScaleOverride = 0f`. To its right a
  `CommanderUiTheme.MutedLabel`: `$"Automatic for {Screen.width} x {Screen.height}:
  {CommanderSettings.AutomaticUiScale:0.##}x"` and, when the override is `0`, append
  `" (active)"`. Build.

- [x] **T6 — Panel box grows; RESET UI LAYOUT still reachable.** Shift the `ToggleUi` hint label
  (was `y + 284f`) and the RESET UI LAYOUT button (was `y + 308f`) down by the space the new row
  took (hint at `y + 326f`, button at `y + 350f`), and grow the `GUI.Box` height at line 340 from
  `344f` to `386f`. Confirm by arithmetic that the tab content (`y` starts at 82, or 162 with
  the help overlay open) plus 386 stays inside the 790 px settings window
  (`UI/CommanderOverlayUi.cs:250`). Build.

- [x] **T7 — Clamp-after-rescale read-check (requirement 5).** No code change expected. Read
  `UI/CommanderOverlayUi.cs:223-269` and `UI/CommanderUiTheme.cs:283-289` and write one
  paragraph in this plan's notes stating whether every commander window is re-clamped every
  frame against `CommanderUiScale.Width/Height` (the `ClampWindow` calls at 266-269 are outside
  the `positionsInitialized` guard, so the expectation is yes). Also list the windows that clamp
  themselves (`CommanderAlertUi.cs:71`, `CommanderUnitListUi.cs:77`) and any that do not. If one
  does not clamp, say so as a finding for the user — do not widen scope to fix it.

- [x] **T8 — CHANGELOG.** In `CHANGELOG.md` under `## Unreleased`, add a bullet at the top of the
  first `### Added` section (currently line 222) in the file's existing voice (bold lead-in,
  what the player sees, why). Mention Settings > UI / HIDE, the 0.75x–2.5x range, AUTO, that the
  value persists as `UI/UiScaleOverride`, and that resizing the window no longer overrides a
  manual value.

- [x] **T9 — Final build and diff review.** `dotnet build GroundControlRts.csproj -c Release`:
  paste the last 5 lines into the notes below. Grep for any remaining reference to the old
  settable `CommanderSettings.UiScale` setter. Tick every task above and fill the notes.

## In-game acceptance (USER, not the executor)

The executor must NOT claim these. They are the spec's acceptance criteria, verified by the
developer after `.\build-and-install.ps1` with the game closed:

- [ ] Fresh install: behaviour identical to today (override 0, automatic preset applies).
- [ ] Drag slider to 1.5x at a 1080p window: every commander window grows, positions stay on
      screen, value persists across game restart (check `BepInEx\config\...cfg` contains
      `UiScaleOverride = 1.5`).
- [ ] Resize the game window with a manual value set: value unchanged.
- [ ] Press AUTO: value returns to the preset for the current resolution, config shows `0`.
- [ ] No `self-check FAILED` in `BepInEx\LogOutput.log` at load.
- [ ] Feel check (not in spec, flagged by the planner): while dragging, the whole UI including
      the slider rescales under the cursor. The feedback is negative (bigger scale moves the
      GUI-space cursor left, lowering the value) so it should settle, but if it feels springy the
      fallback is to apply the override on mouse release only. Report, do not fix.

## Executor notes

### Final build (T9)

`dotnet build "I:\Dropbox (Personal)\Projects\RTS-Commander\GroundControlRts.csproj" -c Release`,
last 5 lines verbatim:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:00.49
```

A build was run after every one of T1-T6 and after the final edits; every one was
`0 Warning(s)`, `0 Error(s)`. Grep after T9 confirms no assignment to `CommanderSettings.UiScale`
survives anywhere in the tree (it is a read-only expression-bodied property now, so one could not
compile); the only two readers are `UI/CommanderOverlayUiSettings.cs:361` and
`UI/CommanderUiScale.cs:48`.

### T3 fail-proof record (Testing rule 5)

Done, with the defect removed afterwards.

1. SHA256 of `UI/CommanderUiScale.cs` before planting:
   `9340FEEDFEA3CE8E70302027BCC090609CCA44B7C543FC41FA8442F5660E641C`.
2. Planted `internal const float MinOverride = 3f;` (from `0.75f`). Build: succeeded,
   `0 Warning(s)`, `0 Error(s)` — the defect is invisible to the compiler, which is the point of
   the self-check.
3. Code path read with the defect in place: `SelfCheck` line 128 evaluates
   `ExpectTrue(failures, "bounds ordered", MinOverride < MaxOverride)`, i.e. `3f < 2.5f`, which is
   `false`; `ExpectTrue` (line ~168) then reaches `failures.Add(name)`, so `failures` holds
   `"bounds ordered"`, `failures.Count == 0` is false, and the loop logs
   `UI scale self-check FAILED: bounds ordered` through `CommanderPlugin.Log.LogError`. The three
   "preset inside slider bounds" cases would fail alongside it, since `IsInsideSliderBounds` tests
   `scale >= MinOverride` and all three presets (1f, 1.25f, 1.5f) are below 3f — four named
   failures in total. No game run was needed: every function involved is pure.
4. Restored `MinOverride = 0.75f`. SHA256 after restore:
   `9340FEEDFEA3CE8E70302027BCC090609CCA44B7C543FC41FA8442F5660E641C` — byte-identical to step 1.
   Rebuilt: `0 Warning(s)`, `0 Error(s)`.

### T7 clamp-after-rescale read-check (requirement 5)

Every window owned by `CommanderOverlayUi` is re-clamped on every frame against the current
`CommanderUiScale.Width`/`Height`, so a scale change made by the new slider cannot leave a window
off screen. `UI/CommanderOverlayUi.cs` lines 266-269 clamp `panelRect`, `reserveWindowRect`,
`pinnedWindowRect` and `settingsWindowRect`, and line 282 clamps `radarWindowRect`; all five sit
after the closing brace of the `if (!positionsInitialized) { ... } else { ... }` block that ends at
line 265, so none of them is gated by `positionsInitialized` and none is gated by visibility either.
`CommanderUiTheme.ClampWindow` (`UI/CommanderUiTheme.cs:284-291`) reads `CommanderUiScale.Width`
and `Height` live on each call, and those derive from `Scale`, which derives from
`CommanderSettings.UiScale` — so the clamp sees a new scale on the same frame the slider writes it.
It shrinks width and height to the screen first and only then clamps x and y, which is the correct
order for a scale increase (a window that no longer fits is made to fit before it is pushed back
inside the margin). The windows that clamp themselves, outside the overlay's list, are
`UI/CommanderAlertUi.cs:71`, `UI/CommanderUnitListUi.cs:77`, `Depot/CommanderDepotUi.cs:70` and
`:88`, `Economy/CommanderEconomyUi.cs:62`, `Naval/CommanderNavalPurchaseUi.cs:48`,
`Supply/CommanderSupplyHeliUi.cs:78`, `SamSites/Analysis/CommanderSamSiteAnalyzerUi.cs:63`,
`AirCommand/CommanderAirCommandUi.cs:113` and `:130`, and
`Map/CommanderTacticalMapService.cs:237`, `:554` and `:584` (the map passes its own
`TacticalMapMargin`). No finding: every commander window reached by the grep for `ClampWindow`
clamps, and no drawn window was found that does not. Rects that are recomputed from
`CommanderUiScale.Width`/`Height` every frame rather than clamped -- `launcherRect`, `moneyRect`,
`enemyPlanRect`, `pinnedLauncherRect`, `selectionBarRect` -- are positioned from the live scaled
dimensions on each pass, so they follow a scale change by construction.

### Deviations from plan or spec

- **T5, label x position (cosmetic, within the plan's tolerance).** The plan says the AUTO button
  is "about 110 wide at `x = 28f`"; it was placed at exactly `x = 28f`, width `110f`, and the muted
  readout starts at `x = 148f` (a 10 px gap after the button) with width
  `settingsWindowRect.width - 176f` so it ends on the same right margin as the rows above it. The
  plan did not fix those two numbers, so this records the choice rather than reporting a conflict.
- **T3, extra self-check helper.** The three "preset inside slider bounds" cases the plan asks for
  are expressed through one four-line private helper, `IsInsideSliderBounds`, rather than three
  inline comparisons, so the bound test has one definition and three callers (Reuse rule 4).
- **T2, constant naming.** `LargeDisplayScale` is `1.5f`, the same number as the pre-existing
  private `BaselineScale`. They are deliberately kept as two constants because they mean different
  things (one is the 4K preset, the other the authoring scale of the GUI matrix); the `<summary>`
  on `LargeDisplayScale` says so explicitly, so a later reader does not "deduplicate" them.
- No other deviation. Nothing was committed or staged, `build-and-install.ps1`/`.bat` was never
  run, and the self-check touches only `Resolve`, `PresetForHeight`, `MinOverride` and
  `MaxOverride` — it never reads or writes `CommanderSettings.UiScaleOverride`, so plugin load
  cannot alter the user's live config file.
