# Unit economy — execution plan

**Track:** unit-economy_20260918 · **Design:** `design.md` (approved by the user, 2026-09-18)
**Goal:** field materially fewer LIVE GROUND VEHICLES per faction, without touching aircraft, air
packages or insertions, and without ever handing a held point over by emptying it.

## What already exists, and why each of the four changes reuses it rather than adding a second one

Reuse rule 1 was run over the whole tree before this plan was written. Findings:

| Design item | Existing code | Decision |
|---|---|---|
| 2.1 Cash in the idle reserve | `CommanderOperationsService.SellSurplusPool` (`Operations/CommanderOperationsService.cs:751`), with `PoolUnitSellable`, `PoolUnitWanted`, `PoolSurplusToSell`, `CommanderEconomyService.DespawnUnit` and `PoolSellRefundFraction` | REUSE. The sale becomes timeout-driven instead of cap-driven; no new sale, no new refund path. |
| 2.2 Garrison per point | `CommanderSettings.PointsMinGarrison` (`Core/CommanderSettings.cs:257`), read by fifteen call sites including the drive fill (`Operations/CommanderOperationsFront.cs:2681`), the insertion load (`Operations/CommanderOperationsInsertion.cs:1264`) AND the ownership rule (`Points/CommanderStrategicPointService.cs:223`) | REUSE, retuned to 1. A SECOND garrison setting was rejected: the ownership rule and the fill rule must read one number, or a garrison below the ownership threshold would hand every point away. |
| 2.3 Ground ceiling | nothing; the nearest relative is the pool-full hold (`Ai/CommanderEnemyCommanderService.cs:858`) and `CommanderFactionVehicleService.DeploymentHeldForFullPool` | NEW pure predicate, wired in beside the pool-full hold and following its logged-once-per-change shape. |
| 2.4 Retire quiet ground | `CommanderPlatoon.InContactUntil`, `CommanderOperationsMission.ContactUntil`, `LastLossAt`, `DetectHoldingContact`, `DetectMissionContact`, `TryNearestTrackedHostile`, and `DissolveToPool` (`Operations/CommanderOperationsFront.cs:2283`) for taking a platoon off the board | REUSE all of them. No second idea of "in contact" and no second disband path. |
| Missile count | `CommanderHealthDiagnostics.CountLiveUnits` (`Core/CommanderHealthDiagnostics.cs:307`) | REUSE the existing single walk: `Missile` derives from `Unit` (confirmed by reflection against the shipped `Assembly-CSharp.dll`), so live missiles are already inside `units=` and only need their own branch. |

## Settings (design §4)

Four settings, two of them renames of an existing entry rather than new entries. A BepInEx key is
renamed whenever a DEFAULT changes, because BepInEx keeps whatever value is already in the player's
`.cfg` and a default change under the old key would never reach anyone — the convention already used
by `MapDragSpeed`, `LimitToFactionRoster` and `PoolIdleSellAfterMinutes`.

| Design setting | Property | Key | Default | Note |
|---|---|---|---|---|
| Idle reserve timeout | `IdleReserveMinutes` | `Operations/IdleReserveMinutes` | 3 min | renamed from `PoolIdleSellMinutes` (`Operations/PoolIdleSellAfterMinutes`, 1 min) |
| Garrison per point | `PointsMinGarrison` | `Points/GarrisonPerPoint` | 1 | renamed from `Points/MinGarrison` (2) |
| Ground unit ceiling | `GroundUnitCeiling` | `Operations/GroundUnitCeiling` | 80 | new |
| Quiet ground timeout | `QuietGroundMinutes` | `Operations/QuietGroundMinutes` | 5 min | new |

## The interaction (design §3), and the three absolute rules

One pure decision table, `NextUnitEconomyStep`, states the order: over the ceiling a commander cashes
in the idle reserve first, then retires quiet ground, and only then refuses to grow. The running code
implements that order structurally — inside one review, `SellSurplusPool` runs, then
`RetireQuietGround`, and the buyer reads the ceiling afterwards on its own review — and the table is
what the commander's log line uses to say which step it is on, so it is a live caller and not a
decorative check.

The three absolute rules are enforced by two pure functions and one live guard, each with its own
named self-check:

1. **Never disband anything in contact** — `QuietGroundRetirement` answers zero while `inContact`,
   and the live pass re-reads `ContactUntil` and every assigned platoon's `InContactUntil` at the
   moment of the decision.
2. **Never leave a held point with nothing standing on it** — `HeldPointKeepFloor` answers
   `max(1, garrison per point)` for a held point, and retirement is capped at `holders - floor`.
   Because the garrison setting IS the ownership threshold, the floor is by construction enough to
   keep ownership, which is re-derived every five seconds from what stands in the ring
   (`Points/CommanderStrategicPointService.cs:195`, `HoldCheckSeconds = 5f`; the failure mode is
   written up in `conductor/designs/2026-09-17-save-load-study.md`).
3. **Never disband the last holder of a point unless the point is being deliberately given up** —
   same floor; and this implementation never gives a point up deliberately, so the floor is absolute.
   `HeldPointKeepFloor(pointHeld: false, ...)` answers 0 and is the seam a future deliberate
   withdrawal would use.

## Tasks

- [x] **T1 — Track files.** `plan.md` (this file), `metadata.json`, a row in `conductor/tracks.md`.
- [x] **T2 — Settings.** The four entries above in `Core/CommanderSettings.cs` with `Get`/`Set`
      pairs and warm-up touches; every reader of the renamed `PoolIdleSellMinutes` updated.
      Verification: builds clean; the warm-up list touches all four.
- [x] **T3 — The pure core.** New partial `Operations/CommanderOperationsUnitEconomy.cs` holding the
      constants, `GroundBuyAllowed`, `HeldPointKeepFloor`, `QuietGroundRetirement`,
      `NextUnitEconomyStep` and `CheckUnitEconomy(failures)`; `CheckUnitEconomy` added to
      `CommanderOperationsService.SelfCheck()`. Verification: the offline harness (T4) reports no
      failures.
- [x] **T4 — Offline check harness.** A reflection harness in the scratchpad that loads the built
      `GroundControlRts.dll` and invokes `CheckUnitEconomy` directly. No `Quaternion` and no
      `Vector3` may appear in the checked code, because those throw outside the player.
      Verification: harness prints `PASS` with zero failures.
- [x] **T5 — Cash in the idle reserve (design 2.1).** `SellSurplusPool` sells every pool vehicle idle
      longer than `IdleReserveMinutes`, regardless of the pool cap, still keeping back the vehicles an
      open order-book line wants and every vehicle the player has given an order to. Per-vehicle sale
      extracted to `CashInVehicle` and priced by `StrategicUnitValue` (Reuse rule 5).
- [x] **T6 — Smaller garrisons (design 2.2).** `PointsMinGarrison` default 1 under its new key; the
      drive fill, the insertion load, the hold posts, the order book and the strategic reload all
      already read it, so this is a retune plus a self-check that the fill rule and the ownership rule
      read the same number.
- [x] **T7 — The ceiling (design 2.3).** `CountLiveGroundVehicles(hq)` and
      `GroundBuyAllowed(hq)` in the new partial; wired into the AI ground buy
      (`Ai/CommanderEnemyCommanderService.cs`, beside the pool-full hold) and the AI repair-crew hire
      (`Units/CommanderRepairService.TrySendEnemyRepairCrew`). Every other ground purchase path is
      listed in the report with the reason it is NOT gated.
- [x] **T8 — Retire quiet ground (design 2.4).** A `QuietSince` stamp on `CommanderOperationsMission`,
      refreshed wherever contact is already detected; `RetireQuietGround(hq, state)` called from
      `Review` immediately after `SellSurplusPool`; whole platoons go out through the existing
      `DissolveToPool`.
- [x] **T9 — Missile count.** `missiles=` on the health line, from the existing single walk.
- [x] **T10 — Settings window.** Sliders for the four settings in `UI/CommanderOverlayUiSettings.cs`
      following the existing `Draw*Slider` helpers.
- [x] **T11 — Build.** `.\build-and-install.ps1 -Dev` ends `0 Warning(s)` / `0 Error(s)`, installed
      into `BepInEx\scripts` with nothing in `BepInEx\plugins`.
- [x] **T12 — Prove each gate fails.** One planted defect per absolute rule and per threshold: the
      ceiling, the quiet-ground timeout, the held-point floor and the in-contact rule. Each must fail
      a NAMED check in the harness; each file restored byte-identical by sha256.
- [x] **T13 — Records.** `CHANGELOG.md` (append under Unreleased) and `conductor/decision-log.md`
      (DECISION-059 — the log already carries DECISION-058).

## As built — two departures from this plan, both found while reviewing the work

1. **Holders are counted inside the capture ring, not off the mission roster.** The first cut counted
   every member of every platoon assigned to a point as standing on it, so a platoon still driving
   there could have justified cashing in the one vehicle genuinely holding the point. `CountHoldersInRing`
   now uses the ownership walk's own `FastMath.InRange` test at the point's own capture radius.
2. **`PlatoonRetirable` was added** so the "a platoon must have wholly arrived, and must fit inside
   what the floor allows" rule is a pure predicate with its own named checks rather than an inline
   condition. Five defects were planted in the end rather than four, one per gate.

## Out of scope

Aircraft, air packages, insertions and lifts. Fewer-but-better vehicles (the user explicitly did not
choose it). The player's own manual depot purchases. The strategic reload's garrison placement, which
restores what was already there.

## Acceptance

Design §5, plus: every new threshold and every absolute rule has a named self-check, and each of the
four gates has been proved to fail when deliberately broken.
