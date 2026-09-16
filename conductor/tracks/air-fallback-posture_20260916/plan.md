# Air Fallback Posture Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use orchestrator-supaconductor:executing-plans to implement this plan task-by-task.

**Goal:** A commander's sortie whose fixed-wing fighters are outnumbered by 2+ falls back toward its own airbase, fights only in self-defence, raises its wanted fighters so the existing retask/buy pipeline reinforces it, re-engages once it outnumbers the enemy by 1, and stands down after 5 unreinforced minutes.

**Architecture:** One new partial-class file `Operations/CommanderOperationsAirPosture.cs` on `CommanderOperationsService`, ticked from `TickLogistics` (5 s). It reads counts through existing helpers, keeps three fields on `CommanderAirSortie`, and acts through two new fields on `AirMission` that the two existing pilot hooks (hold point, target choice) already consult. No new Harmony patch, no new service, no new scheduler.

**Tech Stack:** C# / net472 / BepInEx 5 / Harmony. Build: `$env:NUCLEAR_OPTION_DIR = "I:\SteamLibrary\steamapps\common\Nuclear Option"; .\build-and-install.ps1 -Dev` (must be 0 warnings, 0 errors). Verification: `SelfCheck()` cases (log `self-check FAILED` at load) plus `BepInEx\LogOutput.log` in the running game.

**Read first (Reuse rule 1):** `conductor/tracks/air-fallback-posture_20260916/design.md` §3–§4; `Operations/CommanderOperationsLogistics.cs` (the 5 s watch, its `Expect` self-checks and "logged once on change" pattern); `Operations/CommanderOperationsAirRetask.cs:184` (`TaskOntoSortie`); `Operations/CommanderOperationsAirAwacs.cs:600-660` (reconcile copies live sortie state onto the new demand object — posture fields MUST be copied there too); `AirCommand/CommanderAirCommandPilotHooks.cs:159-195` and `:259-340`.

**Rules that apply:** constants at class level with a `<summary>` saying what the number means and why; settings in `Core/CommanderSettings.cs` with `Get`/`Set` and a warm-up touch; every threshold gets a self-check beside the existing ones; server-only work stays under the `hq.IsServer` guard already in `TickLogistics`; never commit (the user commits).

---

## Tasks

### Task 1: Settings [x]

**Files:** Modify `Core/CommanderSettings.cs` (after `FobAbortStandoffMeters`, ~line 630; warm-up list after `_ = FobAbortStandoffMeters;`).

Add five Operations keys, each with a comment in the file's style (what the number means, why that value, user decision 2026-09-16):

```csharp
internal static int AirFallbackMargin { get => Get("Operations", "AirFallbackMargin", 2); set => Set("Operations", "AirFallbackMargin", value); }
internal static int AirReengageMargin { get => Get("Operations", "AirReengageMargin", 1); set => Set("Operations", "AirReengageMargin", value); }
internal static float AirFallbackDistanceMeters { get => Get("Operations", "AirFallbackDistanceMeters", 15000f); set => Set("Operations", "AirFallbackDistanceMeters", value); }
internal static float AirSelfDefenceRadiusMeters { get => Get("Operations", "AirSelfDefenceRadiusMeters", 6000f); set => Set("Operations", "AirSelfDefenceRadiusMeters", value); }
internal static float AirFallbackGiveUpMinutes { get => Get("Operations", "AirFallbackGiveUpMinutes", 5f); set => Set("Operations", "AirFallbackGiveUpMinutes", value); }
```

Verify: build clean.

### Task 2: Pure rules + self-checks (new file) [x]

**Files:** Create `Operations/CommanderOperationsAirPosture.cs` (`internal sealed partial class CommanderOperationsService`, namespace `GroundControlRts`, file header remark explaining the posture in the style of `CommanderOperationsLogistics.cs`). Register `CheckAirPosture(failures)` in `Operations/CommanderOperationsService.cs` next to `CheckLogistics(failures);` (~line 1928).

Pure functions (all `internal static`):

```csharp
internal static bool ShouldFallBack(int ours, int hostiles, int margin) => margin > 0 && hostiles - ours >= margin;
internal static bool ShouldReengage(int ours, int hostiles, int margin) => ours - hostiles >= margin;
internal static bool FallbackGaveUp(float secondsFallingBack, float minutes) => minutes > 0f && secondsFallingBack >= 0f && secondsFallingBack >= minutes * 60f;
internal static GlobalPosition FallbackPoint(GlobalPosition center, GlobalPosition basePosition, float meters);  // center moved `meters` along the horizontal line toward the base; clamped so it never passes the base; base == center → center
internal static int ReinforcementWanted(int hostiles, int reengageMargin) => Mathf.Max(0, hostiles + reengageMargin);
```

Self-checks in `CheckAirPosture` (use the file's `Expect(failures, name, actual, expected)`):
- 2 v 3 does not fall back (margin 2); 2 v 4 does; 0 v 2 does; margin 0 never falls back.
- 5 v 5 does not re-engage (margin 1); 6 v 5 does; 3 v 5 does not.
- give-up: 0 s no; 299 s no; 300 s yes (5 min); −1 never; 0 min disables.
- fallback point: 15 km from a centre toward a base 40 km away lands 15 km from the centre and 25 km from the base; a base 10 km away yields the base itself (clamped); base at centre yields centre.
- reinforcement wanted: 5 hostiles + margin 1 = 6.
- config: `AirFallbackMargin > AirReengageMargin` is NOT required, but `AirFallbackMargin >= 1`, `AirSelfDefenceRadiusMeters > 0`, `AirFallbackDistanceMeters > 0`, `AirFallbackGiveUpMinutes * 60 > LogisticsWatchSeconds`.

Verify: plant `hostiles - ours > margin` (strict), watch the named check fail at load, restore, confirm byte-identical to your intended version.

### Task 3: Sortie state [x]

**Files:** Modify `Operations/CommanderOperationsAirWing.cs` (fields after `RetaskedThisReview`, ~line 231); `Operations/CommanderOperationsAirAwacs.cs:631-640` (reconcile copy).

Add to `CommanderAirSortie`:
```csharp
/// <summary>Scaled Time.time since this sortie's fighters have been falling back, or -1 while they hold their ground (design.md air-fallback-posture_20260916 §4.2).</summary>
internal float FallingBackSince = -1f;
internal GlobalPosition FallbackPoint;
/// <summary>The "ours v hostiles" pair last logged, so the posture line is written on change only.</summary>
internal string FallbackReported = string.Empty;
internal bool FallingBack => FallingBackSince >= 0f;
```
In the reconcile block (beside `wanted.CapBand = live.CapBand;`) copy all three fields with a comment: the posture is a live fact about the fighters in the air, like the form-up clock.

Verify: build clean.

### Task 4: AirMission override fields [x]

**Files:** Modify `AirCommand/CommanderAirCommandTypes.cs` (`AirMission`, near `ForcedTarget` ~line 269).

```csharp
/// <summary>Where this airframe holds instead of its route/area while its sortie is falling back, or null. Set and cleared by the operations posture only (design.md air-fallback-posture_20260916 §4.3).</summary>
internal GlobalPosition? HoldOverride { get; set; }
/// <summary>While > 0, targets farther than this from the airframe are ignored: it defends itself and nothing more.</summary>
internal float SelfDefenceRadiusMeters { get; set; }
```

Verify: build clean.

### Task 5: Hold-point hook honours the override [x]

**Files:** Modify `AirCommand/CommanderAirCommandPilotHooks.cs:180-193` (`GetActiveRoutePoint`).

At the top: `if (mission.HoldOverride is GlobalPosition hold) return hold;` — with a comment that the route is NOT advanced while overridden (it resumes where it left off). Also `ApplyMissionTargetAltitude`/`ConstrainMissionDestination` need no change (check by reading them; note the finding in the task).

Verify: build clean.

### Task 6: Target hook honours self-defence radius [x]

**Files:** Modify `AirCommand/CommanderAirCommandPilotHooks.cs:299-315` (candidate loop in `ChooseMissionTarget`).

After `range` is computed: `if (mission.SelfDefenceRadiusMeters > 0f && range > mission.SelfDefenceRadiusMeters && !ReferenceEquals(target, mission.ForcedTarget)) continue;`. Pure twin `internal static bool TargetOutsideSelfDefence(float range, float radius)` with self-checks in `CommanderOperationsAirPosture.CheckAirPosture` (radius 0 = off; 5 999 in; 6 001 out).

Verify: named checks pass.

### Task 7: Counting our fighters (generalise, Reuse rule 5) [x]

**Files:** Modify `Operations/CommanderOperationsFob.cs:1995` (`CountLiftEscortsUp`).

Move the body to `Operations/CommanderOperationsAirPosture.cs` as `internal static int CountFightersUp(CommanderAirSortie? sortie)` counting alive, non-rotary airframes in `sortie.Caps` (use `CommanderAirCommandService.IsRotaryAirframe(aircraft.definition)`); leave `CountLiftEscortsUp` as a one-line forwarder with a pointer comment (Reuse rule 3).

Verify: build clean; lift log lines `escort N of M up` unchanged in the game.

### Task 8: Counting hostile fixed-wing (parameterise, Reuse rule 4) [x]

**Files:** Modify `Operations/CommanderOperationsAirRadarWatch.cs:666` (`CountHostileAirInRing`).

Add an optional parameter `bool fixedWingCombatOnly = false`. When true skip units whose `Aircraft` has a rotary pilot (`CommanderAirCommandService.IsRotaryPilot` over `aircraft.pilots`) or whose every weapon station is cargo (`station.Cargo`) / has none. Existing callers unchanged.

Verify: build clean.

### Task 9: Nearest own base position (split, Reuse rule 3) [x]

**Files:** Modify `Ai/CommanderEnemyCommanderAirTasking.cs:194` (`NearestOwnBaseMeters`).

Extract `internal static bool TryNearestOwnBase(FactionHQ hq, GlobalPosition position, out GlobalPosition basePosition, out float meters)`; `NearestOwnBaseMeters` calls it. Pointer comment at the old body.

Verify: build clean; loss lines still print `X km from <base>`.

### Task 10: Stamp / clear a fighter's posture [x]

**Files:** `Operations/CommanderOperationsAirPosture.cs`; `Operations/CommanderOperationsAirRetask.cs:184` (`TaskOntoSortie`).

```csharp
private static void ApplyPosture(CommanderAirSortie sortie, Aircraft aircraft)
{
    if (CommanderAirCommandService.Instance == null || !CommanderAirCommandService.Instance.TryGetMission(aircraft, out AirMission mission)) return;
    mission.HoldOverride = sortie.FallingBack ? sortie.FallbackPoint : null;
    mission.SelfDefenceRadiusMeters = sortie.FallingBack ? CommanderSettings.AirSelfDefenceRadiusMeters : 0f;
}
```
Add `internal bool TryGetMission(Aircraft, out AirMission)` on `CommanderAirCommandService` if no accessor exists (check `missions` dictionary usage in `AirCommand/CommanderAirCommandOrders.cs:48,133`). Call `ApplyPosture(sortie, aircraft)` at the end of `TaskOntoSortie` so a retasked fighter inherits its new sortie's posture. `ApplyPostureToAll(sortie)` loops `Caps` and `Cas`.

Verify: build clean.

### Task 11: The posture tick [x]

**Files:** `Operations/CommanderOperationsAirPosture.cs`; `Operations/CommanderOperationsLogistics.cs:65-80` (`TickLogistics`: add `WatchAirPosture(hq, entry.Value);` after `WatchInsertionFlights`).

`WatchAirPosture(hq, state)`: for each `sortie` in `state.AirSorties` with `Kind != Awacs` and `CountFightersUp(sortie) > 0` (skip sorties whose caps are all rotary):
- `ours = CountFightersUp(sortie)`, `hostiles = CountHostileAirInRing(hq, sortie.Center, ObservedRadiusMeters, fixedWingCombatOnly: true)` (make `ObservedRadiusMeters` reachable: it is `private const` in `CommanderOperationsOffensive.cs:26` of the same partial class — fine).
- Not falling back and `ShouldFallBack` → `TryNearestOwnBase`; if none, skip (no home to fall back to). Set `FallingBackSince = Time.time`, `FallbackPoint`, `InContact = true`, `CapsWanted = Mathf.Max(CapsWanted, ReinforcementWanted(hostiles, AirReengageMargin))`, `ApplyPostureToAll`, log `"{Label}: outnumbered {ours} v {hostiles} within {ring:0} km; falls back {dist:0} km toward {baseLabel} and calls for {wanted} fighters."` (base label via `CommanderCaptureService.GetAirbaseLabel` if a base object is at hand; else omit the name).
- Falling back and `ShouldReengage` → clear (`FallingBackSince = -1`, `FallbackReported = ""`), `ApplyPostureToAll`, log `"{Label}: reinforced {ours} v {hostiles}; re-engages."`
- Falling back, still short: keep `CapsWanted` topped up every tick (the review rebuilds demand each 30 s, so re-assert on the tick); if the pair changed, log once `"{Label}: still outnumbered {ours} v {hostiles}; holding {dist} km back."` (`FallbackReported`).
- Falling back and `FallbackGaveUp(Time.time - FallingBackSince, AirFallbackGiveUpMinutes)` → Task 12.

Verify: build clean; in game, the fallback line appears for an outnumbered sortie within 10 s (Acceptance 2).

### Task 12: Give up [x]

**Files:** `Operations/CommanderOperationsAirPosture.cs`; read `Operations/CommanderOperationsAirAwacs.cs:803` (`ReleaseSortie`) and the sortie cooldown field (`CooldownUntil`) and the package cancel used by `Operations/CommanderOperationsAirPackages.cs`.

`GiveUpSortie(hq, state, sortie)`: log `"{Label}: not reinforced in {minutes:0} min; stands down."`; clear posture on all fighters; `sortie.CooldownUntil = Time.time + <the loss cooldown the sortie already uses>`; `Kind == Strike` → the existing package cancel path; lift cover (`IsLiftCoverLabel`) → nothing more (Task 13 makes the transport react); otherwise `ReleaseSortie(hq, state, sortie, state.AirSorties)` and remove it from `state.AirSorties`. Read `ReleaseSortie`'s `openSorties` contract first: it wants the sorties that survive the review; pass the current list minus this sortie.

Verify: build clean.

### Task 13: Escorted flights react [x]

**Files:** Modify `Operations/CommanderOperationsLogistics.cs:259` (`bool escortsLost = CountLiftEscortsUp(cover) <= 0;`) → `bool escortsLost = cover == null || cover.FallingBack || CountLiftEscortsUp(cover) <= 0;` with a comment. Modify `Operations/CommanderOperationsAirPackages.cs:785` (`SortieHoldsAtFormUp`) → also true while `sortie.FallingBack` (a package whose escort is falling back does not go in). Check that `GoneIn` is not forced true elsewhere while falling back (`:310,:322,:355`) — guard those with `!sortie.FallingBack` where they mean "go in now".

Self-check: in `CheckAirPosture`, `DeliveryHopeless(false, false, true, true)` is already covered; add a check that `SortieHoldsAtFormUp` semantics are pure only if it already has a pure twin — otherwise document in the task result that this is verified in-game (Acceptance 4).

Verify: build clean.

### Task 14: Review line shows the posture [x]

**Files:** Modify `Operations/CommanderOperationsAirHomeCap.cs:505-520` (review status text): append `" fallback"` after the CAP counts when `sortie.FallingBack` (both the `Cap` case and the default case). Nothing else in UI.

Verify: build clean.

### Task 15: CHANGELOG + in-game verification [x]

**Files:** `CHANGELOG.md` `## Unreleased` — one paragraph in the file's voice naming the five settings and the three log lines.

Build with `.\build-and-install.ps1 -Dev` (0/0). Confirm `Ground Control (RTS) 0.7.6.0 loaded` after the reload and no `self-check FAILED` / `Exception` in `BepInEx\LogOutput.log`. Then watch for: `outnumbered … falls back`, `reinforced … re-engages`, `not reinforced … stands down`, and a lift cover falling back followed by `waits for a clear route` or `diverts to`. Record what was and was not observed in the task result — the developer plays; you read the log.

---

## Execution record (2026-09-16)

All fifteen tasks done; build `0 Warning(s) 0 Error(s)`. Deviations from the plan as written:

- Task 2: `FallbackGaveUp` forwards to `LiftWaitedTooLong` rather than restating the rule (Reuse rule 4);
  the self-checks exercise it under its own name. The plant-a-defect proof could not be run at load because
  the developer closed the game before the reload fired; see the code-reading argument in the task result.
- Task 5: `ConstrainMissionDestination` DID need a change — it pulls an attacking airframe's destination
  back to the mission area, which would fly a falling-back fighter into the fight it left; it now returns
  early while `HoldOverride` is set. `ApplyMissionTargetAltitude` needs none.
- Task 6: a target inside the self-defence radius is also exempt from the mission-area restriction, or a
  fighter 15 km from its area could not defend itself against a pursuer.
- Task 7/8: one definition, `IsFixedWingCombatAircraft(Aircraft)` (alive, no rotary pilot, at least one
  non-cargo station), serves both our count and the hostile filter.
- Task 9: `TryNearestOwnBase` returns the `Airbase` (for the log label) plus the metres.
- Task 12: the given-up sortie STAYS in `state.AirSorties` so the reconcile carries its cooldown across the
  next review (removing it would let the review re-open it at once); its airframes are released through
  `ReleaseBoundAirframes` and the lists cleared, the pattern the strike escort release already uses. The
  lift cover is treated the same way, so its transport sees "no escort up". The strike's four-line
  forget step moved into `ForgetStrikeSortie` (one definition for the abandoned strike and the stand-down).
- Task 13: `SortieHoldsAtFormUp` is unchanged; only the go-in decision is guarded with `!FallingBack`. The
  hold override already carries the escorted flight, and widening the form-up test would have re-routed a
  gone-in package to the form-up point for a review after the escort re-engaged.
- Task 3 (added): the reconcile also carries a falling-back sortie's `InContact` and raised `CapsWanted`
  onto the new demand object, so the retask in the same review sees the call for help.
- Task 15: game closed by the developer before the reload; in-game log lines not observed, to be tested at
  the next launch.

## DAG

Tasks 1–4 independent (1 first is convenient). 5, 6 depend on 4. 7, 8, 9 independent. 10 depends on 3, 4. 11 depends on 2, 3, 7, 8, 9, 10. 12 depends on 11. 13 depends on 3. 14 depends on 3. 15 last.

## Review

One independent review of the full diff (second-opinion `default` panel if Ollama is up, else one Opus skeptic agent), one evaluation against §5 of the design, at most two fix cycles.
