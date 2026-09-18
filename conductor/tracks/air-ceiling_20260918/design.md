# Air ceiling: a smaller wing that still flies packages, escorts and transports

Track `air-ceiling_20260918`. Design approved by the user on 2026-09-18 through the brainstorming
skill. One executor, one independent review of the PR diff, one evaluation.

## 1. Goal

Cut the number of live aircraft on the map without losing airframe variety, strike packages,
transport lifts or air support over a ground fight.

## 2. The problem, with evidence

Frame cost grows as the square of live unit count
(`conductor/designs/2026-09-17-frame-rate-investigation.md`). The ground half was addressed by
`unit-economy_20260918`; the health line from the developer's match of 2026-09-18 shows the result
and what is left:

```
Health t=16:30 frameMsAvg=26.8 frameMsWorst=258.3 units=532 aircraft=66 ground=188
buildings=159 missiles=44 ...
```

Ground has flattened (the ceiling logs `104 vehicles live (ceiling 80). It replaces losses only.`).
Aircraft have not: 66 live, where the frame-rate investigation recorded 5 to 7 and wrote air out of
scope on that basis. Missiles ride on aircraft and add 44 more.

Counting one commander's standing air requests from the same log: **33 requests, 27 of them
patrols.** Two transport escorts, two package escorts, one radar aircraft and one strike make up the
rest. The things the user wants to protect are six lines; the patrols are twenty-seven.

## 3. The existing code being reused (Reuse rules 1 and 2)

**There is already an income-scaled air ceiling and this track does not add a second one.**

- `CommanderOperationsService.AirborneCeilingFor(configured, incomePerMinute, incomePerAirframe, max)`
  — `Operations/CommanderOperationsAirRadarWatch.cs:1089`. Pure. Floor `AirborneFloor` (30), one more
  airframe per `AirborneIncomePerAirframe` (15) of income a minute, never above `AirborneCeilingMax`
  (60). `EffectiveAirborneCeiling(hq)` at `:1111` is its live wrapper.
- `CommanderEnemyCommanderService.CountAirborne(hq)` — `Ai/CommanderEnemyCommanderAirTasking.cs:317`.
  Every live `Aircraft` of the faction, transports included.
- The gate: `Ai/CommanderEnemyCommanderAirBuy.cs:176`, the first test in `BuyAirframeForTurn`, which
  refuses the buy and reports a denial.
- The same ceiling and count pair sizes package escorts (`Operations/CommanderOperationsAirIdle.cs:433`)
  and lift escorts (`Operations/CommanderOperationsAirPlatoonCap.cs:288`), deliberately and with a
  comment saying so (Reuse rule 4). Changing the pair changes both, which is intended.
- `CommanderOperationsUnitEconomy.GroundBuyAllowed(live, ceiling)` — the ground precedent this
  track's new predicate is written to match, including "zero never binds".
- The lift launch gate — `Operations/CommanderOperationsFob.cs:2320` — already holds a transport at
  its form-up point until its escort is up and ahead, with a bounded wait. Unchanged by this track;
  it is the reason escorts must never be squeezed.

**Why the ceiling is not biting:** both commanders sit on the FLOOR of 30, not on the income scaling
and not on the maximum of 60. Thirty per side is where 66 comes from.

## 4. Decisions taken by the user

- **(A) Retune, do not rebuild.** Floor 30 to 16, maximum 60 to 24. `AirborneIncomePerAirframe`
  unchanged at 15.
- **(B) Transports sit OUTSIDE the ceiling**, so a lift is never blocked by a full sky. They still
  require an escort into contested air, which is existing behaviour and must not regress.
- **(C) A reserved block protects everything else.** A standing patrol may buy only while
  `live < ceiling - reserved`; escorts, packages, the radar aircraft, anti-radiation sorties, air
  support over an objective and home defence may buy while `live < ceiling`. Reserved starts at 6.
- **(D) Air support over a ground fight is PROTECTED**, not squeezed. Only fighters circling because
  hostile aircraft were seen near a point or platoon, with nothing else making it an objective, are
  rationed.
- **(E) Over the ceiling means stop buying, nothing more.** No aircraft is recalled, landed or cashed
  in, matching the ground ceiling exactly so the mod has one rule rather than two.
- **(F) A marker field, not a label comparison.** A lift escort and a point patrol are both
  `CommanderSortieKind.Cap` today and are told apart only by their printed label. The standing patrol
  is marked explicitly at the one site that creates it.

## 5. Requirements

1. `AirborneFloor` default 16, `AirborneCeilingMax` default 24. Both keys reissued so BepInEx
   delivers the new defaults to an existing config (the repo's standing rename convention).
2. `CountAirborne(hq)` no longer counts an aircraft whose air role is `AirRole.Transport`.
3. A new pure predicate `AirBuyAllowed(liveAircraft, ceiling, reserved, isStandingPatrol)` beside
   `GroundBuyAllowed`, with the same conventions: inclusive at the ceiling, a non-positive ceiling
   never binds, a non-positive reserved block means patrols and everything else share one line.
4. `CommanderAirSortie` gains `IsStandingPatrol`, default false, set true only in `AddCapDemand`
   (`Operations/CommanderOperationsAirPlatoonCap.cs:487`, both its call sites at `:41` and `:60`).
   `AddEscortDemand` (`:461`) and `AddLiftCoverDemand` (`:286`) leave it false.
5. The gate at `Ai/CommanderEnemyCommanderAirBuy.cs:176` keeps refusing at the absolute ceiling. A
   second refusal, after the demand is resolved, refuses a standing patrol at the reserved line and
   says which of the two lines stopped it.
6. A new setting `Operations/AirPatrolReserve`, default 6, with a slider beside the ground ceiling's.
7. The commander's log says what happened, once, in the existing denial shape.

## 6. Acceptance criteria

- At plugin load, silence from the operations self-check; any failure names its own case.
- In a match: `aircraft=` on the health line settles near 36 rather than 66, and `missiles=` falls
  with it.
- A transport lift still launches with the sky at the ceiling, and still holds for its escort.
- A strike package and a radar aircraft still get airframes with patrols refused.
- The commander logs a patrol refusal naming the reserved line, not the absolute ceiling.

## 7. Files in scope

`Core/CommanderSettings.cs`, `Operations/CommanderOperationsUnitEconomy.cs` (the predicate and its
checks, beside the ground one), `Operations/CommanderOperationsAirWing.cs` (the marker field),
`Operations/CommanderOperationsAirPlatoonCap.cs` (the marker set),
`Operations/CommanderOperationsAirRadarWatch.cs` (the reserved line's live wrapper),
`Ai/CommanderEnemyCommanderAirTasking.cs` (`CountAirborne`), `Ai/CommanderEnemyCommanderAirBuy.cs`
(the second gate), `UI/CommanderOverlayUiSettings.cs`, `CHANGELOG.md`, `conductor/decision-log.md`.

## 8. Out of scope

- **Area-merged patrol requests** — two platoons beside each other making one request instead of two.
  The user's stated next track, and the thing that removes the refusal noise this track creates.
- Recalling or selling airborne aircraft.
- Any change to ground vehicles, buildings, control point counts or the idle reserve.
- The multi-second frame stalls (`frameMsWorst=7875`), which are not a unit-count problem.

## 9. Known cost, stated up front

With 27 patrol requests per side and room for about ten, the commander will refuse its own patrol
orders on most reviews and log it. That noise is expected and is what the area-merge track removes.
