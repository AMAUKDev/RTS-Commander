# Track: ui-scale-slider

**Created**: 2026-09-13 · **Type**: feature · **Decided by user**: slider + AUTO button (2026-09-13)

## Goal

The commander UI is legible at the developer's resolution. A slider in Settings > UI sets the
UI scale by hand; an AUTO button returns to the mod's automatic preset. The chosen value
survives restarts.

## Problem (with evidence)

- UI scale is automatic only. `UI/CommanderUiScale.cs:19-23` `ApplyResolutionPreset` picks
  1.0x below 1200 px tall, 1.25x up to 1600 px, else 1.5x.
- `Core/CommanderModeController.cs:134` calls `RefreshResolutionPreset()` every frame, which
  re-applies the preset whenever the window size changes, so any manual value would be
  overwritten on the next resize.
- `Core/CommanderSettings.cs:12` — `UiScale` is a plain static property, not a BepInEx config
  entry, so it cannot persist.
- `UI/CommanderOverlayUiSettings.cs:359-363` shows the value as a read-only label
  "Automatic UI scale for W x H: 1x". 1.0x is too small on the developer's display.

## Existing code being reused

- `CommanderUiScale.Scale / Begin / End / ScreenToGui / GuiToScreen` — the whole GUI matrix
  path already keys off `CommanderSettings.UiScale`. Nothing downstream changes.
- Slider helper pattern: `UI/CommanderOverlayUiSettings.cs:324-336` `DrawCameraSlider(y, label,
  value, min, max, format, suffix)`. Reuse it (it is a private instance method in the same
  partial class, so it is directly callable from `DrawUiSettings`).
- Config pattern: any `Get("UI", ..., default)` / `Set(...)` pair in `Core/CommanderSettings.cs`
  plus a `_ = Prop;` touch in the warm-up list.

## Requirements

1. New persisted setting `UI/UiScaleOverride` (float, default `0` = automatic).
   `CommanderSettings.UiScale` becomes: override when `> 0`, else the automatic preset value.
2. `RefreshResolutionPreset` still tracks resolution, but only writes the automatic value; it
   must not clear or clobber a manual override.
3. Settings > UI: replace the read-only label with a slider **0.75x to 2.5x**, step-free,
   showing the current effective value with two decimals and an `x` suffix, plus an **AUTO**
   button that sets the override back to 0 and shows what the automatic value is for the
   current resolution.
4. The `DrawUiSettings` panel box grows to fit; the RESET UI LAYOUT button keeps working.
5. Live preview: moving the slider rescales the UI on the same frame (it already will, because
   every draw reads `CommanderUiScale.Scale`). Verify window positions clamp correctly after a
   scale change (`UI/CommanderUiTheme.cs:286-289` clamps against `CommanderUiScale.Width/Height`).
6. Self-check: `CommanderUiScale.SelfCheck()` asserting the override wins when set, automatic
   applies when 0, and the slider bounds are ordered (`min < max`). Registered in
   `Core/CommanderPlugin.cs` alongside the others.

## Acceptance criteria

- [ ] Fresh install: behaviour identical to today (override 0, automatic preset applies).
- [ ] Drag slider to 1.5x at a 1080p window: every commander window grows, positions stay on
      screen, value persists across game restart (check `BepInEx\config\...cfg` contains
      `UiScaleOverride = 1.5`).
- [ ] Resize the game window with a manual value set: value unchanged.
- [ ] Press AUTO: value returns to the preset for the current resolution, config shows `0`.
- [ ] No `self-check FAILED` in `BepInEx\LogOutput.log` at load.

## Files in scope

`Core/CommanderSettings.cs`, `UI/CommanderUiScale.cs`, `UI/CommanderOverlayUiSettings.cs`,
`Core/CommanderPlugin.cs` (one SelfCheck line), `CHANGELOG.md` (Unreleased > Added).

## Out of scope

Per-window scale, font changes, tactical map size (already has its own setting), DPI detection.
