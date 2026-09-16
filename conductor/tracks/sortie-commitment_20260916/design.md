# Sortie commitment — count what is assigned, launch whole, scale with the threat

**Track:** sortie-commitment_20260916 · **Date:** 2026-09-16 · **Status:** approved by user
**Investigation this answers:** `conductor/designs/2026-09-16-sortie-standdown-investigation.md`
**Builds on:** `air-fallback-posture_20260916`, `air-survival-layer_20260916`

## 1. Goal

Stop the fall-back posture thrashing, stop the commander believing in help it has not got, and stop
aircraft being fed into fights one at a time. A sortie should know its own strength, arrive whole,
and be sized to what it faces.

## 2. Evidence

From the investigation, measured on the live 2026-09-16 match:

| Measure | Value |
|---|---|
| Holds entered | 1,220 |
| Complete on-off cycles | 1,012 across 66 sorties |
| Holds re-engaging on the very next message | 868 of 1,220 |
| Median jump in our own count at each flip | 13, in both directions |
| Movement in the hostile count at those flips | none |
| Sorties reporting help that was not there, ending `its fighters are gone` | 96 |
| Times the five-minute give-up actually fired | 0 |

The 1,475 `stands down` lines were aircraft being shot down, not the posture giving up. The give-up
clock has never fired in play and remains unproven.

Root cause, in `Operations/CommanderOperationsAirPosture.cs`: `FighterPresentFor` counts a fighter
near the hold point ONLY while the sortie is already holding. The state being decided switches the
clause that decides it. The jump is 13 because `ClearFallbackPoint` walks the hold point back until
no hostile is within the posture ring and `FirstClearAlong` returns the airbase when nothing is
clear — so "fighters near the hold point" becomes "the whole fleet parked at home".

## 3. Decisions taken (user, 2026-09-16)

All four parts below, as one track. A sortie goes in when every wanted aircraft has gathered or when
a bounded wait expires, whichever comes first — the rule strike packages already use, extended to
every sortie kind.

## 4. Design

**Part 1 — a sortie's strength is what is assigned to it, not what is nearby.**
`ours` becomes the alive, airborne, fixed-wing combat aircraft BOUND to the sortie (`Caps` + `Cas`),
with no distance test at all. `FighterPresentFor`, `CountFightersPresent`, `CountPresent`,
`PresentRadiusMeters` and `FighterPresent` go. `CountFightersUp` already counts bound fighters and
becomes the one definition (Reuse rule 4). The hostile count is unchanged: tracked hostile
fixed-wing within `AirPostureRingMeters` of the objective or of any of our own fighters.

This alone removes the feedback loop, because nothing in `ours` depends on the hold state, and
removes the false `19 v 28` readings.

**Part 2 — a hold point that collapses onto the airbase means the sortie is not viable.**
When `ClearFallbackPoint` finds nothing clear on the line and returns the base itself, the sortie
does not hold over its own runway. It releases its fighters through the existing release door
(`ReleaseBoundAirframes`), which offers them to other open sorties and then to the home patrol, and
takes its ordinary loss cooldown. One log line: `<sortie>: no clear ground to hold — its fighters
join the home patrol`. The patrol has had no minimum since DECISION-038, so they are immediately
available to the next demand.

**Part 3 — every sortie kind goes in whole.**
The package machinery already exists and is proven: `SortieHoldsAtFormUp`, `TryFindFormUpPoint`,
`PackageGoesIn` and `CommanderSettings.PackageFormUpSeconds` in
`Operations/CommanderOperationsAirPackages.cs`. Today it applies only to sorties that
`SortieIsPackage` recognises. Widen it so a fighter patrol, a ground-attack sortie and an escort all
gather at a form-up point and go in when complete or when the bounded wait expires. One definition,
more callers — do NOT copy the rule. The form-up point is the existing one, which is already kept
clear of the enemy.

Exempt by nature: the radar aircraft, a suppression sortie that has gone in, and a single-airframe
sortie, which has nobody to form up with.

**Part 4 — every sortie's size scales with what it faces.**
`StrikeEscortWanted(floor, hostileAirNow, headroom)` already scales an escort with tracked enemy
aircraft and is capped by room under the airborne ceiling. Make patrols and ground-attack sorties
read one shared sizing rule of the same shape, so a quiet objective draws a pair and a contested one
draws a package. Keep every existing floor and the diversity cap. Name the new rule and give it
self-checks at each boundary; do not silently change what a quiet objective asks for today.

## 5. Acceptance criteria

1. Self-checks pass at load, including new cases pinning that a sortie's strength does not change
   with its hold state.
2. In a running match: flip-flop cycles fall from ~1,000 to a small number; no sortie reports a
   strength it does not have; `no clear ground to hold` appears instead of a sortie holding over its
   own base; a contested objective draws more aircraft than a quiet one; sorties arrive together
   rather than one per review.
3. Build 0/0, hot reload clean, no exceptions.

## 6. Files in scope

`Operations/CommanderOperationsAirPosture.cs`, `Operations/CommanderOperationsAirPackages.cs`,
`Operations/CommanderOperationsAirWing.cs`, `Operations/CommanderOperationsAirRetask.cs`,
`Operations/CommanderOperationsAirAwacs.cs`, `Operations/CommanderOperationsAirDemand.cs`,
`Operations/CommanderOperationsAirRadarWatch.cs`, `Core/CommanderSettings.cs`, `CHANGELOG.md`.

## 7. Out of scope

The five-minute give-up clock stays as it is; it has never fired and this track does not tune what
it cannot observe. The belt-ahead hold is unchanged. Player-tasked aircraft are untouched.
