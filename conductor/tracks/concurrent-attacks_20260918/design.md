# Concurrent attacks: several pushes at once, each with its own air

Track `concurrent-attacks_20260918`. Design approved by the user on 2026-09-18 through the
brainstorming skill, from a live observation: "endless CAP warfare and not much combined arms".

## 1. Goal

Make the ground war generate enough offensive action that air support has something to support, so
what the player sees is combined arms rather than fighters circling control points.

## 2. The problem, with evidence from the 81-minute match of 2026-09-18

(`conductor/designs/2026-09-18-air-ceiling-match-observations.md`)

- **One commander's mission board: 48 Picket, 11 ForwardBase, 1 Attack, 1 Reserve.** Sixty-one
  missions, one offensive.
- **Platoon states:** 1 of 10 platoons `Attacking`, the rest `Moving@ForwardBase` or `Forming`. The
  other commander, 2 of 11.
- **559 aircraft launched: 382 fighters (68%), 177 ground-attack (32%).** 475 lost.
- **Territory:** one change of hands in the first 25 minutes; 26 events in 81 minutes.

The chain: 60 control points generate 59 defensive missions, nearly every platoon is garrison or in
transit, one attack at a time crawls forward, the ground never generates close-support demand, and
the only war left is fighters over points.

## 3. The existing code being changed (Reuse rules 1 and 2)

- **The hard gate**: `Operations/CommanderOperationsOffensive.cs:602-608`. `TryOpenAttack` returns
  false if ANY attack mission exists. One attack per commander, always, whatever the army size.
- **`CountSparePlatoons`** (`:99`) counts only platoons with no mission or a Reserve mission. A
  platoon on a forward base is not spare, which is why attacks stay small.
- **`MaxForwardBases(platoonCount, share, threatened)`** (`Operations/CommanderOperationsFront.cs:2186`)
  is the mod's existing "how many of these am I allowed" shape — a floor plus a share of the platoon
  count. The attack allowance is written to match it rather than inventing a second shape.
- **`IsThreatenedFrontPoint(state, mission)`** (`:2200`) is the mod's ONE definition of "the enemy is
  at this point", already read by the forward-base allowance, the pool order and the order book. The
  call-up rule reuses it; it does not invent a second idea of danger.
- **The strike package is a single FIELD, not a count**: `OperationsState.StrikeSortie`
  (`Operations/CommanderOperationsService.cs:382`), guarded at
  `Operations/CommanderOperationsOffensive.cs:1317`. It is read in ten places across five files
  (`CommanderOperationsAirIdle.cs:404`, `CommanderOperationsAirPackages.cs:479, :511, :572, :556`,
  `CommanderOperationsAirPosture.cs:837`, `CommanderOperationsOffensive.cs:1271, :1288, :1385`).
  **This is the largest part of the work and it is a refactor, not a one-liner.**

## 4. Decisions taken by the user

- **(A) Several attacks at once**, the count scaled to the army rather than fixed.
- **(B) One strike package per open attack.** Every push keeps its own air ahead of it — this is what
  makes it combined arms rather than simply more tank pushes. A deliberate strike opened by the
  clock when no attack is running stays at one, as now.
- **(C) An attack may call up a platoon from an UNTHREATENED forward base.** The base keeps its
  picket and its truck; the platoon goes. Without this the change is largely inert, because on the
  measured match only one to three platoons per commander were ever genuinely spare. A threatened
  base is never stripped.

## 5. Design decisions taken here, for the user to overrule

- **(D) The package attaches to the ATTACK MISSION, and `state.StrikeSortie` keeps its single slot
  for the deliberate strike.** Rejected alternative: turning `state.StrikeSortie` into a list. Two
  reasons. An attack's package should die with the attack, which a field on the mission gives for
  free; and the deliberate strike's clock, its one-at-a-time rule and its stand-down path are all
  built around that single slot and are not what this track is changing. The ten readers are
  retrofitted to one new helper that yields EVERY open package — the deliberate one plus one per
  attack — so no caller keeps its own idea of what is flying (Reuse rule 5).
- **(E) The call-up takes the platoon, never the picket or the truck.** A forward base with its
  platoon called up still holds its point, because the picket is what holds ground.

## 6. Requirements

1. `MaxConcurrentAttacks(platoonCount, share, floor)` — pure, shaped like `MaxForwardBases`.
   `TryOpenAttack` counts open attacks against it instead of refusing at the first.
2. An allowance of zero or less means ONE attack — the behaviour before this track, so it can be
   switched off. (The repo's convention is that zero disables a rule; here "disabled" means the old
   single attack, not no attacks at all.)
3. `CommanderOperationsMission` gains its own strike package for an Attack mission; the package is
   forgotten when the attack closes, through the existing `ForgetStrikeSortie` path.
4. One helper enumerates every open package; the ten existing readers of `state.StrikeSortie` all
   go through it.
5. `TryCallUpPlatoon` — an attack short of platoons may take one from a ForwardBase mission for which
   `IsThreatenedFrontPoint` is false, leaving `PicketMembers` and `Truck` untouched.
6. Two settings: `Operations/MaxAttacks` (floor, default 2) and `Operations/AttacksPerPlatoon`
   (share, default 0.2 — one more attack per five platoons), with sliders.
7. The commander logs each attack it opens and each platoon it calls up, with the reason.

## 7. Acceptance criteria

- At plugin load, silence from the operations self-check.
- In a match: more than one `Attack` in the mission board at once, and more than one platoon reading
  `Attacking@`.
- Each open attack has a strike package; `CAS` fill rises against `CAP` fill from the measured 32/68.
- A threatened forward base is never stripped of its platoon.
- Territorial events per hour rise above the measured 26.

## 8. Files in scope

`Operations/CommanderOperationsOffensive.cs`, `Operations/CommanderOperationsFront.cs` (the call-up
and the threat test's new caller), `Operations/CommanderOperationsService.cs` (the mission field, the
state field's readers), `Operations/CommanderOperationsAirPackages.cs`,
`Operations/CommanderOperationsAirIdle.cs`, `Operations/CommanderOperationsAirPosture.cs`,
`Operations/CommanderPlatoon.cs`, `Core/CommanderSettings.cs`, `UI/CommanderOverlayUiSettings.cs`,
`CHANGELOG.md`, `conductor/decision-log.md`.

## 9. Out of scope

- **The 48 pickets and the 60-point map.** The deeper cause; the user chose not to touch it today.
- Any change to how air demand is weighted, or to the air ceiling.
- The sub-second frame stalls, and the quiet-ground retirement that has never fired — both recorded
  in the observations file as separate work.

## 10. Known risk, stated up front

Attacking in three places with a ten-platoon army may mean losing in three places. The allowance is a
slider and that is how the developer backs off it. The call-up also means forward bases are held more
thinly while a push is on, which is a real trade and deliberately the commander's to make.
