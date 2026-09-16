# Strike packages, role-fit types and CAP altitude bands — design

Track: `strike-packages_20260915`. Approved by the user 2026-09-15 ("approve - go").

## Problem (user's words)

> "we need more variety than revoker spam, we need multi-aircraft type packages (strike on enemy
> held control points etc)" … "current CAP mission generations needs height variety too. i'm still
> not seeing any aircraft variety either!"

Evidence, 2026-09-15 match logs: the wing's sorties are all reactive — CAP over things, CAS over
platoons in contact, AWACS, anti-radar — so demand is dominated by CAP, and the tier picker's
"cheapest in the top launchable tier when quiet" rule (`SelectInTier` / `BetterInTier`,
`Ai/CommanderEnemyCommanderAir.cs`) returns the FS-12 Revoker every time. Roster in play has FS-20
Vortex and KR-67 Ifrit (fighters), A-19 Brawler, SFB-81 Darkreach, Alkyon AB-4 (strike/bomber) that
are never bought while quiet. Nothing plans an offensive air strike on an enemy-held control point.

## Decisions (user, 2026-09-15)

1. Strike trigger: **both** — a strike ahead of every planned ground attack, and periodic strikes on
   valuable enemy points when no attack is planned.
2. Package shape **varies with the scale of the target** (light / defended / hard; a base adds a
   bomber element).
3. Type choice: **role fit per sortie element**, read from the game's own ratings, not a fleet-mix
   quota. Plus a diversity cap so one type cannot make up the whole side.
4. **CAP escorts scale with enemy threat: at least one-for-one with hostile aircraft tracked** near
   the target or route.
5. CAP sorties get **altitude variety**: rotating low / medium / high station bands.
6. Approach: a new sortie kind inside the existing wing (option 1 of three); no new service.

## Reuse (read first; name what is reused)

- Sortie kinds and the demand walk: `Operations/CommanderOperationsAirWing.cs`
  (`CommanderSortieKind` Objective/Cap/Awacs/Arad), the demand queue and `AddEscortDemand` /
  `AddTransportEscortDemand` / `TransportEscortWanted` (`CommanderOperationsAirPlatoonCap.cs`) — the
  escort-count rule the new strike escort reuses.
- Packages: `Operations/CommanderOperationsAirPackages.cs` (form-up point, go-in rule,
  `PackageFormUpSeconds`), design smarter-air-wing Section 3 and Section 20 (one type per element,
  ordered together from one base; `PackageElementBuys`, `KeepsPackageChoice`, `PackageStrikeType`).
- Anti-radar: `CommanderOperationsAirArad.cs` (`AradClusterMinimum`, the ARAD-first gate).
- Offensives: `Operations/CommanderOperationsOffensive.cs` (`TryOpenAttack`, `UpdatePressure`,
  `StepPressure`, `PressureForcesAttack`, the 240 s form-up timeout and its "CAS on station" hold,
  `CountObserved` for defenders).
- Type picking: `Ai/CommanderEnemyCommanderAir.cs` (`ForRole`, `TierOrder`, `HighestLaunchableTier`,
  `SelectInTier`, `BetterInTier`, `AirframeCandidate`), `Ai/CommanderEnemyCommanderAirBuy.cs`
  (`TryBuyRole`, `PackageElementBuys`), ratings `definition.roleIdentity.antiAir` / `antiSurface`,
  the prefab key the roster log already prints (e.g. `FastBomber1`).
- Attrition brake (`Ai/CommanderEnemyCommanderAttrition.cs`): strike losses feed it unchanged.
- Air missions: `AirCommand/CommanderAirCommandTypes.cs` `AirMissionRecipe.TargetAltitude`, the
  `AirMission` constructor's altitude argument (`CommanderAirCommandOrders.cs:143`).
- Markers: `Operations/CommanderOperationsAirMarkers.cs` (`ClassifyAirframe`, sortie labels).
- Point ranking: `Operations/CommanderOperationsFront.cs` `RankPoints` (income × closeness),
  `TryNearestTrackedHostile`.

Why nothing existing fits as-is: Objective sorties are platoon-bound and reactive (stand-down after a
loss, platoon escort minimums); the pressure clock opens ground attacks, not air ones; the tier picker
has no notion of which element it is choosing for.

## Section 1 — Triggers and targets

- New `CommanderSortieKind.Strike`. At most one open Strike sortie per commander.
- Source A, the offensive: when `TryOpenAttack` opens an attack mission on a point or base, it also
  opens a Strike sortie on the same target. The attack's go-in waits for **strike delivered** (the
  package has gone in and its first strike airframe is inside the target ring) or the existing 240 s
  form-up timeout, whichever comes first.
- Source B, the strike clock: `state.StrikeClock` steps with the review like `StepPressure`; when no
  attack is open and `StrikeIntervalMinutes` (6) have passed since the last strike went in, pick the
  enemy-held point with the highest `RankPoints` value within `StrikeRangeMeters` (80 km) of a held
  base that can launch a strike airframe, not on strike cooldown, and open a Strike sortie there.
- A struck point takes `StrikePointCooldownMinutes` (10).
- Pure: `StrikeDue(minutesSinceLast, interval, attackOpen)`, `StrikeTargetBeats(value, other)`.

## Section 2 — Package by target scale

Read once when the sortie is posted:
`defenders = CountObserved(target)`, `airDefence` = tracked air-defence within the anti-radar cluster
distance of the target, `hostileAir` = hostile aircraft tracked within the threat radius of the target
or the route, `isBase`, `hasBomber` = roster has a Strike-tier type whose prefab key names a bomber.

| Scale | Condition | Strike | Escort (CAP) | Anti-radar | Bomber |
|---|---|---|---|---|---|
| Light | ≤ 2 defenders, no air defence, no hostile air | 2 | max(0, hostileAir) | 0 | 0 |
| Defended | ≥ 3 defenders or hostile air tracked | 2 | max(2, hostileAir) | 0 | 0 |
| Hard | air defence tracked, or a base | 2 | max(2, hostileAir) | 1 | base: 1–2 if hasBomber |

Escort is **never below the hostile aircraft count** (decision 4) and is capped by the airborne
ceiling's headroom. Pure `StrikePackageFor(defenders, airDefence, hostileAir, isBase, hasBomber)`
returning the four element sizes, with self-checks for every row and the escort floor.
Elements are bought whole-or-nothing by `PackageElementBuys`; escorts are CAP demand exactly as
transport escorts are.

## Section 3 — Role fit per element and the diversity cap

`SelectInTier` gains an `ElementKind` (HighCap, Escort, Strike, Bomber, Other) and a preference:

- HighCap: highest `antiAir / value` in the Fighter tier (favours the light fighter when money allows).
- Escort: Multirole tier first when launchable and affordable, else Fighter tier by `antiAir`.
- Strike: highest `antiSurface` the element's budget covers in the Strike tier.
- Bomber: Strike-tier types whose prefab key names a bomber (`FastBomber`, `Bomber`); none → no
  bomber element.
- Other: today's rule unchanged.

Diversity cap: a type already making up more than `TypeShareCap` (0.6) of that side's airborne
airframes is skipped when another launchable, affordable type exists in the tier. Pure
`TypeShareExceeded(countOfType, sideTotal, cap)`, `PreferForElement(kind, candidateA, candidateB)`.
No aircraft names in code; the roster log prints what was chosen and why
(`… KR-67 Ifrit (escort: multirole preferred)`).

## Section 4 — CAP altitude bands

`CapBandMeters` = { 1500, 4000, 7500 } above ground. Every CAP sortie (home CAP, point CAP, escort)
takes the next band in rotation when opened (`NextCapBand(previous)`), stored on the sortie and passed
as the mission's target altitude. Escorts take their strike element's band plus one step. The review
line prints the band beside the CAP count (`CAP 2/2 FS-20 @4000`).

## Section 5 — Running the package

Form-up and go-in are the existing package rules. Additions: the anti-radar element goes in one
review (30 s) before the rest; after go-in the strike element flies the game's strike mission on the
target ring; escorts hold CAP over the target for `StrikeLoiterMinutes` (4) then recover. A Strike
sortie ends when its strike element is spent or lost, or after `StrikeSortieMaxMinutes` (12). Losses
feed the attrition brake unchanged. The attack's go-in reads "strike delivered".

## Section 6 — What the player sees

- Markers: `STRIKE <point> — forming 3/5`, `STRIKE <point> — inbound`, `ESCORT <point>`,
  `ARAD <point>` (existing), `BOMBER <point>`.
- Logs (CommanderAiLog): `orders a strike on CROSSROADS 7 (defended: 4 defenders, 2 hostile air):
  2x A-19 Brawler, 2x KR-67 Ifrit escort`, `strike on CROSSROADS 7 goes in`,
  `strike on CROSSROADS 7 done: 3 defenders destroyed` (observed count before vs after),
  `strike clock: next deliberate strike in N min`.
- Settings (Operations section, Get/Set + warm-up): `StrikeIntervalMinutes` 6,
  `StrikePointCooldownMinutes` 10, `StrikeRangeMeters` 80000, `StrikeLoiterMinutes` 4,
  `StrikeSortieMaxMinutes` 12, `TypeShareCap` 0.6, `CapBandLowMeters` 1500, `CapBandMidMeters` 4000,
  `CapBandHighMeters` 7500.
- Self-checks: every pure rule above, beside the existing `CheckAirSupport` / `CheckAirBuyRules`.

## Out of scope

Naval strikes; treating an enemy airbase's runway as a target in itself (a base is a hard point);
a player UI to order a strike by hand; changing the ground attack's own sizing.

## Verification (running game)

Load `Ground Control Duel Far`, wait for the first ground attack or six minutes: expect
`orders a strike on …` with a mixed package, escorts at least equal to tracked hostile air,
`strike on … goes in`, markers on the aircraft, CAP lines showing three different bands, and at least
two fighter types and two strike types launched within twenty minutes. `LogOutput.log` free of
`self-check FAILED`.
