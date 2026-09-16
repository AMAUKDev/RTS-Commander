# Air fallback posture — outnumbered fighters fall back, call for help, re-engage

**Track:** air-fallback-posture_20260916 · **Date:** 2026-09-16 · **Status:** approved by user (brainstorm, 2026-09-16)

## 1. Goal

A commander's fighters that meet superior numbers break off, fall back toward their own airbase,
ask the review for reinforcements, and go back in once they outnumber the enemy — instead of flying
straight into five aircraft and dying one at a time (user, 2026-09-16: "too often we see a CAP
escort fly straight at 5x enemy aircraft, rather than retreating and calling for help and then
re-engaging").

## 2. Problem, with evidence

- `tasks FS-12 Revoker on CAP over HILLTOP 11 (wing 1/3; 3 hostile air tracked)` — one fighter is
  sent against three, and the next two follow one review apart (`LogOutput.log`, 2026-09-16).
- The review (`Operations/CommanderOperationsAirAwacs.cs`, `CapsWanted = cap`) sizes a sortie's
  escort to the hostile air it tracks, and `Operations/CommanderOperationsAirRetask.cs` moves
  fighters to a contact ("contact outranks cover") — but nothing tells the fighters ALREADY over the
  sortie to do anything but engage while the numbers catch up.
- The game's own restraint is `CombatAI.ChooseHQTarget` (bravery × opportunity vs threat) and
  `AIPilotCombatModes.ApplyThreatAvoidance` (morale from friendlies within 5 km). Neither counts
  aircraft, neither steers a fallback, neither asks for help. Commander fighters spawn at bravery
  0.5 (`Spawner.SpawnAircraft(..., 1f, 0.5f)` in `AirCommand/CommanderAirCommandMissions.cs`).

## 3. Existing code reused (Reuse rules 1–4)

| Need | Existing thing | Why it fits / what is added |
|---|---|---|
| Where a fighter flies | `CommanderAirCommandService.TryGetMissionHoldPoint` → `GetActiveRoutePoint` (`AirCommand/CommanderAirCommandPilotHooks.cs:159`) | Add one nullable `HoldOverride` on `AirMission`; the route helper returns it when set. |
| What a fighter shoots at | `ChooseMissionTarget` (`CommanderAirCommandPilotHooks.cs:259`) | Add `SelfDefenceRadiusMeters` on `AirMission` (0 = off); candidates beyond it are skipped. |
| Hostile air near a sortie | `CountHostileAirInRing` (`Operations/CommanderOperationsAirRadarWatch.cs:666`), ring `ObservedRadiusMeters` 8 000 (`CommanderOperationsOffensive.cs:26`) | Parameterise with a fixed-wing-combat filter (rotary and cargo-only excluded via `IsRotaryPilot`, cargo stations). |
| Our fighters on a sortie | `CountLiftEscortsUp` (`Operations/CommanderOperationsFob.cs:1995`) | Generalise to `CountFightersUp(sortie)`; the lift cover keeps calling it. |
| Fallback direction | `NearestOwnBaseMeters` (`Ai/CommanderEnemyCommanderAirTasking.cs:194`) | Split out `TryNearestOwnBase(hq, pos, out GlobalPosition)`; the loss line keeps its distance. |
| Reinforcement | `CapsWanted` + `InContact` → existing retask and buy pipeline | Posture only raises the demand; no new buyer, no new retask. |
| Escort gone → transport turns | `DeliveryHopeless(escortsLost, hostileAirNearRoute)` (`Operations/CommanderOperationsLogistics.cs`) | `escortsLost` also true while the cover is falling back. |
| Strike waits for its escort | `SortieHoldsAtFormUp` (`Operations/CommanderOperationsAirPackages.cs:785`) | A package whose escort is falling back holds at form-up (`GoneIn = false`). |
| 5 s clock | `TickLogistics` (`Operations/CommanderOperationsLogistics.cs:65`) | Posture runs from the same tick, after the delivery watch. |
| Stand down | `ReleaseSortie` (`Operations/CommanderOperationsAirAwacs.cs:803`) + sortie cooldown | Give-up releases through it. |

## 4. Design

**4.1 Decision (pure, self-checked).** Per sortie with at least one fighter up:
`ours` = alive fixed-wing fighters in `sortie.Caps` (and `Cas` that are fighters), `hostiles` =
fixed-wing combat aircraft tracked within 8 km of `sortie.Center`.

| Rule | Setting (Operations) | Default |
|---|---|---|
| Fall back when `hostiles - ours >=` | `AirFallbackMargin` | 2 |
| Re-engage when `ours - hostiles >=` | `AirReengageMargin` | 1 |
| Fallback distance toward nearest own airbase | `AirFallbackDistanceMeters` | 15 000 |
| Self-defence target radius while falling back | `AirSelfDefenceRadiusMeters` | 6 000 |
| Give up if not reinforced within | `AirFallbackGiveUpMinutes` | 5 |

Pure functions: `ShouldFallBack(ours, hostiles, margin)`, `ShouldReengage(ours, hostiles, margin)`,
`FallbackGaveUp(secondsFallingBack, minutes)` (limit-inclusive, ≤0 disables — the
`LiftWaitedTooLong` convention), `FallbackPoint(center, basePos, meters)`.

**4.2 State on `CommanderAirSortie`.** `FallingBackSince` (−1 when not), `FallbackPoint`,
`FallbackReported` (last logged ours/hostiles pair, so the line is written on change only).

**4.3 Acting.** Entering fallback: every bound fighter's `AirMission.HoldOverride = FallbackPoint`,
`SelfDefenceRadiusMeters = setting`; the fighter keeps its sortie binding. Leaving (re-engage,
give-up, sortie released): both cleared. A fighter retasked onto or off the sortie by the review is
stamped or cleared by the same helper the review already uses to task onto a sortie
(`TaskOntoSortie`). Hot reload wipes sortie state; the posture is recomputed on the next 5 s tick.

**4.4 Calling for help.** While falling back: `sortie.InContact = true`,
`sortie.CapsWanted = max(CapsWanted, hostiles + AirReengageMargin)`. The existing retask takes
fighters from quiet sorties and the lendable home patrol; the existing buyer fills what is left.
The attrition brake's fighter hold is NOT bypassed (documented limitation).

**4.5 Give up.** `FallbackGaveUp` → objective/CAP sortie: `ReleaseSortie` and the sortie's loss
cooldown; lift cover: `escortsLost` already true → hopeless rule recalls/diverts the transport;
strike package: existing package cancel. Each writes one line.

**4.6 Scope.** Only sorties in `state.AirSorties` of a commanded HQ (player-side AI and enemy).
Player-tasked Air Command missions are untouched. Rotary CAS is never put in posture (a helicopter
is not counted on either side and never falls back under this rule).

**4.7 Logs.** `<sortie>: outnumbered 2 v 5 within 8 km; falls back 15 km toward <base> and calls
for 6 fighters.` · `<sortie>: reinforced 6 v 5; re-engages.` · `<sortie>: not reinforced in 5 min;
stands down.`

## 5. Acceptance criteria

1. Self-checks for every rule in 4.1 pass at plugin load (`self-check FAILED` absent).
2. In a running match, a sortie whose fighters are outnumbered by ≥2 logs the fallback line within
   10 s, its fighters turn toward their airbase (Air Command map shows them leaving the ring), and
   the next review shows the sortie's wanted fighters raised (`CAP … 2/6 air5`).
3. When fighters arrive so that ours − hostiles ≥ 1, the re-engage line is logged and the fighters
   return to the sortie centre.
4. A lift cover falling back makes the transport wait/divert (`waits for a clear route` /
   `diverts to`) rather than fly on alone.
5. Five minutes unreinforced logs the stand-down line and the sortie releases its fighters.
6. Build `0 Warning(s) 0 Error(s)`; hot reload clean; no exceptions in `LogOutput.log`.

## 6. Files in scope

New: `Operations/CommanderOperationsAirPosture.cs`. Modified: `Operations/CommanderOperationsAirWing.cs`
(sortie fields), `Operations/CommanderOperationsLogistics.cs` (tick + `escortsLost`),
`Operations/CommanderOperationsAirRadarWatch.cs` (fixed-wing filter), `Operations/CommanderOperationsFob.cs`
(`CountFightersUp` generalisation), `Operations/CommanderOperationsAirPackages.cs` (form-up hold),
`Ai/CommanderEnemyCommanderAirTasking.cs` (`TryNearestOwnBase`), `AirCommand/CommanderAirCommandTypes.cs`
(`AirMission` fields), `AirCommand/CommanderAirCommandPilotHooks.cs` (override + radius),
`Core/CommanderSettings.cs`, `CHANGELOG.md`.

## 7. Out of scope

Player-tasked missions; helicopter posture; ground-unit retreat; bypassing the attrition brake;
UI for posture (the existing sortie marker text may append " falling back" only if trivial).

## 8. Decisions taken (user, 2026-09-16)

Threshold: hostiles exceed ours by 2+. Help: retask first, then buy. Fallback toward home;
re-engage only when we OUTNUMBER (margin 1). Scope: all commander CAP and escorts, escorted flight
turns with them. Approach A (sortie posture in the operations review).
