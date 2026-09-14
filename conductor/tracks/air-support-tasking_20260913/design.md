# Design: Air support tasking driven by the ground plan

**Date**: 2026-09-13 · **Track**: air-support-tasking_20260913 · **Approved by user**: 2026-09-13
**Depends on**: platoon-operations_20260913 (the mission list this track reads), strategic-points_20260913 (the points).

Last of four tracks. The air wing becomes another consumer of the ground plan: sortie targets come from
the operations mission list, not from an air brain of its own. Naval is out (user decision).

## Problem

- The enemy's air wing picks its own targets and they never move. `TaskAirWing`
  (Ai/CommanderEnemyCommanderAir.cs:400-439) alternates every unmissioned airframe between
  `StrategicStrike` over the opponent's **opening airbase** — `GetStrikeTarget`, :454-475, frozen on
  purpose — and a 1-in-3 home `AirGuard` (:425-430, radii :26-29). Nothing reads the ground plan: a
  platoon in contact, a launched attack, a FOB under a threat mark — the wing orbits the enemy's first
  base regardless.
- The whole air leg is duel-only. `ReviewPosture` gates `TaskAirWing` behind `IsDuelMission`
  (Ai/CommanderEnemyCommanderService.cs:511-516) and the buy leg behind `if (duel)` (:418-426): on
  every stock mission the commander never buys an aircraft and never tasks one.
- `TryTaskAiAircraft` (AirCommand/CommanderAirCommandOrders.cs:79-96) creates a bare `AirMission`
  with no recipe (:94), so a lost enemy airframe is re-bought by the fund whenever it can afford one,
  with no link between the loss and the objective it died supporting.

## Decisions taken (user, 2026-09-13)

1. **Unified**: the operations mission list is the single source of air objectives; no separate air
   brain choosing targets.
2. **Priority**: contested objectives first (a platoon in contact — `InContactUntil` — or a live
   Attack mission), then front objectives (ForwardBase missions ranked by threat). Pickets and rear
   points get none.
3. **CAS only** over the objective area. A CAP escort is added only when hostile air is tracked near
   that objective (the commander's tracking database, `ThreatMemorySeconds` freshness).
4. **Magnitude scales with observed enemy presence** — `CountObserved` + the `ObservedFloors` idea,
   no second observed-enemy counter. Explicit ladder below; every threshold gets a SelfCheck case.
5. **The player-side AI tasks only airframes it bought itself**; the player's own Air Command
   missions are never retasked (same hands-off rule as ground, `HasPlayerOrder`).
6. **Naval is out of scope** for this track.
7. **Every commanded HQ, host-only, duel map AND stock missions** where the enemy commander is
   enabled — see Reuse for what happens to the `IsDuelMission` gates.
8. **Launches ride the existing `BuyAirframe` / `TryLaunchAiAircraft` path**, so the hangar toggle,
   internal-cannons default, recovery-on-landing and AUTO relaunch all apply unchanged. Money comes
   from the existing `AirFund`; CAS demand feeds that budget, no new one.
9. **Losses**: an airframe is re-requested only while the objective is still contested, behind a
   cooldown so the commander does not bleed airframes into strong air defence (observed hostile
   air-defence counts, cheap).
10. **Diagnostics**: one `CommanderAiLog.Note` line per tasking / release / loss, chatty detail behind
    `OperationsDebugLog`, and the sortie summary appended to the existing `Ops … review:` line.
11. **CAP first (user, 2026-09-13, after the first playtest).** The wing's first job is air
    superiority: a CAP over every active objective AND the home CAP, sized by tracked hostile air in
    the ring PLUS a baseline of one fighter per active objective even with no hostile air seen yet —
    the tracking picture only shows what has already been spotted. Only then CAS (the ladder below):
    platoons handle CAS; the wing handles the sky. Consequences, same date:
    - The demand queue serves ALL CAP demand before ANY CAS demand; when the fund can buy one
      airframe and both are wanted, it buys the fighter. Live sortie demand (CAP or CAS) also now
      outranks the wing's transport top-up.
    - The escort becomes the sortie's first CAP fighter; the escort-before-CAS package rule and the
      attack go-in hold are kept unchanged.
    - Throughput: the one-airframe-per-review throttle is replaced by "buy while the air fund covers
      the next wanted airframe and the ceiling allows", bounded by `MaxAirBuysPerReview = 3`;
      `AirborneCeiling` rises 8 → 12 (8 was the binding limiter in the first match — the player-side
      commander sat at it with money in hand; transports and insertion helicopters count against
      it); the air fund's ceiling never sits below `MaxAirBuysPerReview` × the dearest AI-flyable
      fighter on the roster.
    - One `air demand: CAP n/m, CAS n/m, ceiling k/K, fund f` line per review behind
      `OperationsDebugLog`; the once-per-reason `bought no aircraft` denials stay.

12. **Pre-emptive CAS and CAP (user, 2026-09-14, after the second playtest).** "We need to modify the
    air-tasking to have PRE-EMPTIVE CAS and CAP if going near enemy." Every demand source above
    opens *after* the fight starts: an attack's groups have to reach their release points, a platoon
    has to already be `InContactUntil`, a forward base has to already carry a threat mark — and the
    CAS ladder gives zero airframes at zero observed. A platoon therefore marched into enemy
    territory with nothing overhead until it was being shot at. New demand source, ahead of contact:
    - **Who.** Any platoon in state `Moving` or `Attacking` — not `Forming`, `Holding` or
      `Withdrawing`, none of which is walking into anything. `WantsPreemptiveAir(state, distance,
      range)` is the pure rule, self-checked at both boundaries.
    - **Near the enemy.** The leader's position OR the platoon's objective within
      `PreemptiveAirRangeMeters` of an enemy-held strategic point or base, or of a tracked hostile
      ground unit's last known position. The range is `ObservedRadiusMeters` (8 km) itself, not a
      copy of its value: the sizing ring, the escort test, the air-defence test and this one are one
      number, so "near the enemy" means one thing everywhere — and it is the ring the platoon's own
      contact would be counted in, which is exactly the fight the sortie exists to be early for.
      The points walk is the existing `NearestEnemyAssetDistance`; base points carry every airbase
      and resolve their owner live (`CommanderStrategicPoint.GetOwner`), so a second walk over
      `FactionRegistry` HQs' `GetAirbases()` would be the same answer twice (Reuse rule 4). The
      tracked-hostile walk is `CountObserved`'s, answering "how far" instead of "how many".
    - **Size.** Baseline 1 CAS + 1 CAP at 0 observed; above that the ladder is untouched and the
      CAP escort rule applies on top.
    - **Priority.** Below contact and attack sorties, above threatened forward bases (Section 1).
      Per-objective and per-commander caps and the CAP-first ordering are unchanged.
    - **Dissolution.** The sortie ends the moment the platoon stops marching, and otherwise
      `PreemptiveAirHoldSeconds` (2 × `ReviewIntervalSeconds` = 60 s) after it leaves the range. The
      ground line's own `ContactHoldSeconds` (20 s) is shorter than one review, so reusing it would
      give no hysteresis at all: a platoon skirting the ring would release its wing on one review
      and re-buy it on the next. One sortie record serves the platoon throughout — the pre-emptive
      sortie is matched on the same `ContactPlatoon` field, so a platoon that walks into its first
      contact keeps the airframes it already had.
    - **No double tasking.** A platoon whose own mission already raised a demand this review (its
      attack's groups have arrived) is skipped: that fight already has a sortie over it.
    - **Diagnostics.** `tasks <aircraft> with CAS over <platoon> (pre-emptive, enemy <n> km)`,
      `tasks <aircraft> as the escort over <platoon> (pre-emptive)`, and `pre` on the sortie's entry
      in the review line's `air=[…]`.

## Approval (user, 2026-09-13)

1. **Timing (the one addition).** CAS must be overhead when the ground attack actually goes in, and
   the escort must be with the package, not trailing it:
   - An Attack mission's CAS demand opens when its groups **begin arriving at their release
     points** (`FirstGroupArrivedAt`, Operations/CommanderOperationsOffensive.cs) — not when the
     mission forms.
   - Launch lead time = distance from the launching airbase to the target over a nominal transit
     speed: `CasTransitSeconds(distance, rotary)` — jet 200 m/s (conservative cruise, so estimates
     err late not early), rotary 80 m/s via `IsRotaryPilot` (≈ 290 km/h, a heli's honest transit).
     Self-checked at its boundaries.
   - The attack's go-in holds while its CAS sortie has nothing on station, but **never past the
     attack's existing form-up timeout** (`AssaultFormUpTimeoutSeconds`, 240 s from the same
     `FirstGroupArrivedAt` stamp) — one bounded wait reused, no second clock. If no held airbase is
     close enough for CAS to arrive inside that window, the attack does not hold at all.
   - When an escort is wanted it is bought/tasked **in the same review as or before** the CAS (one
     buy per review makes that "the review before", escort-first in the demand order). The CAS
     airframe holds `AirGuard` over the nearest friendly asset (the home CAP centre) until the
     escort is bound, then both release to the objective together.
   - **Contact sorties stay immediate**: a platoon in contact gets CAS tasked at once, no lead time
     and no escort wait.
2. **Home CAP confirmed**: standing `AirGuard` over home territory stays the default posture for
   every unbound airframe.
3. **Stock missions confirmed**: the duel-only gate on the air buy leg goes too; the enemy fields a
   bought wing on stock missions; mission-authored free aircraft are never touched.

## Reuse

- **Objectives and ranking** already exist: `OperationsState.Missions` / `RankedPoints`
  (Operations/CommanderOperationsService.cs:81-84), threat marks (Operations/CommanderOperationsFront.cs:107-137),
  `InContactUntil` / `ContactBearingAnchor` (Operations/CommanderPlatoon.cs:133-139).
- **Observed strength**: `CountObserved` (8 km ring, `ThreatMemorySeconds` freshness,
  Operations/CommanderOperationsOffensive.cs:68-94), `EffectiveObserved` (:45-48) and the shared
  `ObservedFloors` table (Operations/CommanderOperationsService.cs:111) — the same numbers the ground
  attack sizing reads. The escort and air-defence tests use the same 8 km ring
  (`ObservedRadiusMeters`), so "at the objective" means one thing everywhere.
- **Flying**: the Air Command mission table stays the only way an AI aircraft is flown —
  `TryTaskAiAircraft` (:79-96), retarget by updating `Mode`/`AreaCenter`/`Radius` on an existing entry
  (the same three writes `TryAdoptAircraft`'s update branch performs, Orders.cs:117-123), and every
  existing pilot patch applies unmodified (NoTarget zeroing, CAS area filter, station keeping,
  AirCommand/CommanderAirCommandPatches.cs). **No new Harmony patches in this track.**
- **Launch, loadout, recovery**: `BuyAirframe` (Ai/CommanderEnemyCommanderAir.cs:168-282) →
  `TryLaunchAiAircraft` (hangar toggle, air-spawn branch, AirCommand/CommanderAirCommandMissions.cs:237-302)
  → `WithoutInternalCannons` (:243, AirCommand/CommanderAirCommandLoadout.cs:215-236); out-of-ammo RTB
  (AirCommand/CommanderAirCommandPilotHooks.cs:53-61) and recovery-on-landing
  (AirCommand/CommanderAirCommandRecovery.cs:203-270) serve any mission aircraft.
- **Money**: `CommanderState.AirFund`, `AccrueFund` (40% share, Ai/CommanderEnemyCommanderAir.cs:47-52)
  and the one-airframe-per-review buy. CAS demand is read by `ChooseAirRole` the same way the ground
  buyer reads the order book — a static query on the operations service.
- **Not reused, and why**: `TrySpawnFromRecipe` (Missions.cs:327-418) is the player's UI path — it
  hard-reads the local HQ (:336), charges raw `factionFunds`, and writes status lines. Commander AUTO
  relaunch is the sortie's standing demand re-purchasing through the buy loop (Decisions 8-9); the
  player's recipe/relaunch queue (Recovery.cs:84-136) stays player-only.
- **Hands-off (decision 5)**: a per-HQ *pending-launch expectation* — `BuyAirframe` records
  (HQ, definition, expires 45 s = `PendingSpawnTimeoutSeconds`, AirCommand/CommanderAirCommandService.cs:12)
  and a fourth notify on the existing `RegisterFactionUnit` postfix
  (AirCommand/CommanderAirCommandPatches.cs:95-103) claims a matching newly registered airframe into
  the hungry sortie — the air mirror of the depot pool claim (Operations/CommanderOperationsRequisitions.cs:57-90).
  The player's AIR-window buys are already mission-bound at registration (`TryAssignPendingAircraft`,
  Missions.cs:420-451) and stock-mission authored free aircraft carry no expectation, so neither is
  ever claimed or retasked. One mechanism, both commanders.
- **NEW code**: one partial, `Operations/CommanderOperationsAir.cs` on the operations service (the
  sortie table is per-HQ state beside the missions it reads, filled in the same 30 s review; a
  separate service would need a second scheduler and a second copy of "which HQs are commanded"),
  plus two config entries. **Gate change (decision 7)**: the `if (duel)` around the air fund and
  `BuyAirframe` (Ai/CommanderEnemyCommanderService.cs:418-426) and the `IsDuelMission` around
  `TaskAirWing` (:511-516) are deleted; `TaskAirWing` shrinks to the residual posture (below), and
  sortie tasking runs for every commanded HQ. Host-only comes free: both review loops already check
  `hq.IsServer`.

## Section 1 — Demand

Each operations review, per commanded HQ, build the sortie list in priority order:

1. **Attack missions** (forming or launched): over the target — the point, or the base's hold point.
2. **Platoons in contact** (`InContactUntil >= Time.time`): over `ContactBearingAnchor` — the last
   tracked contact, the commander's own picture, which is what a CAS area filter can actually find.
3. **Platoons marching near the enemy but not yet in contact** (pre-emptive, decision 12): over the
   platoon's leader, the moving centre the existing 3 km retarget hysteresis already smooths.
4. **ForwardBase missions whose point carries a threat mark**, in `RankedPoints` order.

Pickets, Reserve and rear points get none. Each active objective gets, in this order:

**CAP first (decision 11)** — `CapWanted(tracked hostile aircraft in the ring)`:

| Tracked hostile aircraft in the ring | CAP fighters | Why |
|---|---|---|
| 0 | 1 | the baseline: air superiority over every active objective before anything is seen there |
| 1 | 2 | a pair meets a two-ship |
| 2+ | 3 | per-objective cap (`CapPerObjectiveCap`); past three the wing concentrates under one sky and every other objective goes bare |

**Then CAS** — `CasWanted(EffectiveObserved(CountObserved(hq, target), GetObservedFloor(state, key)))`:

| Effective observed hostiles | CAS airframes | Why |
|---|---|---|
| 0 | 0 | aircraft do not orbit ground they cannot see |
| 1-2 | 1 | one standard loadout out-shots a pair of vehicles |
| 3-5 | 2 | a squad-plus; two keep coverage through a rearm cycle |
| 6-9 | 3 | a platoon's worth of targets |
| 10+ | 4 | per-objective cap; more over one ring concentrates the wing and overkills |

**Pre-emptive baseline (decision 12)** — a pre-emptive sortie takes the CAS ladder with its count
floored at one (`PlanPreemptiveSortie`): **0 observed → 1 CAS + 1 CAP**, and every value above the
floor is the ladder's own. The CAP baseline needs no change; `CapWanted` already floors at one.

**Escort**: the sortie's FIRST bound CAP fighter is the escort (the one whose binding releases the
held CAS, Approval 1). With the baseline the escort rule is no longer conditional on hostile air
being seen — the baseline already puts a fighter there. A pre-emptive sortie is not immediate: like
an attack's, its CAS holds over home until the escort is bound. Only a platoon already in contact
tasks at once.

## Section 2 — Tasking

- **Sortie record** (`CommanderAirSortie` in `OperationsState`): objective, mode, wanted count, bound
  airframes, `LastLossAt`. The Air Command `AirMission` stays the flying authority; the sortie is the
  binding and the demand.
- **Fill**: unbound commander-owned airframes already airborne are retasked first; the shortfall
  becomes demand the buy loop fills (one per review, as today). `ChooseAirRole` serves it: escort
  demand or a flying opponent keeps the existing Fighter-first rule; CAS shortfall buys Strike; no
  open demand buys no Strike (an untasked strike airframe would only cycle to stock). Transport
  unchanged. The launch faces the sortie's objective instead of the frozen strike base
  (Ai/CommanderEnemyCommanderAir.cs:250-254).
- **Bind**: the claimed airframe gets `Cas` over the objective at `CasSortieRadiusMeters` (6 km,
  floor 3 km — inside the 8 km sizing ring so the strike engages what the sizing saw; wide enough for
  the station-keeping clamp at 0.75 x radius to hold a orbit, PilotHooks.cs:233-249).
- **Retarget**: each review the centre is recomputed from the objective (a contact anchor moves; an
  attack launches from release points). Bound missions are rewritten when the centre moved more than
  3 km — half the ring; inside that the objective is still well within the area filter, and a retask
  makes the pilot re-path.
- **Release**: when the objective stops qualifying (attack resolved, contact expired, threat mark
  cleared), the sortie dissolves. Bound airframes go to the next-hungry sortie, else to the standing
  posture: **every unbound airframe holds `AirGuard` over home territory** (the old 1-in-3 share,
  HomeGuardRadiusMeters, becomes the default; `StrategicStrike` tasking is deleted — that was the
  separate brain). Released airframes are not RTB'd; a Winchester one already goes home by the
  existing rule.

## Section 3 — Budget and losses

- The AirFund accrual and buy run for every commanded HQ (gate deleted), same 40% share, same
  `AirborneCeiling` (was `DuelAirborneLimit`, 8) and the same once-per-reason denial lines.
- **Loss**: a bound airframe dying stamps the sortie's `LastLossAt`. While
  `CasLossCooldownMinutes` (2) has not elapsed, no replacements launch at that objective — 2 min is
  four buy reviews, long enough for the tracking picture or the front to change. The cooldown
  doubles to 4 min when ≥2 tracked hostile air-defence vehicles stand in the ring at the moment of
  loss (`IsAirDefence`, Ai/CommanderEnemyCommanderService.cs:822-825) — the objective is defended in
  exactly the way CAS dies to. While contested and off cooldown the standing demand keeps replacing
  losses through the buy loop: that is the commander's AUTO relaunch. A Winchester recovery is a
  rearm cycle, not a loss, and never starts a cooldown.

## Section 4 — Diagnostics and settings

- Tasking / retarget / escort / release / loss lines go through `CommanderAiLog.Note` (COMMANDER LOG
  and LogOutput.log): `tasks <aircraft> with CAS over Hilltop 12 (5 observed)`, `adds an escort over
  Hilltop 12: hostile air tracked in the ring`, `releases <aircraft>: Maris Airport taken`, `loses an
  airframe over Maris Airport: CAS stands down for 4 min (2 air-defence observed)`.
- The `Ops … review:` line (Operations/CommanderOperationsDiagnostics.cs:54-104) gains
  `air=[CAS Hilltop 12 2/2 +esc; CAS Maris 1/2]` (live/wanted per sortie). Shortage arithmetic and
  hysteresis moves stay behind `OperationsDebugLog`.
- New settings, config-only, `Operations` section, `Get`/`Set` pair + warm-up touch
  (Core/CommanderSettings.cs conventions): **`CasLossCooldownMinutes` = 2** (the replacement
  blackout) and **`AirborneCeiling` = 8** (the wing ceiling, now on every mission). No new sliders.
- Ladder, escort ring, 6/3 km sortie radius, 3 km retarget hysteresis and the 2-air-defence
  hesitation are class-level constants with `<summary>` rationales and SelfCheck cases.

## Out of scope

Naval tasking; commander-flown ARAD/AWACS sorties; the player's own AIR window behaviour; stock
missions' authored free aircraft (never claimed); persistence of sortie bindings across a hot reload
(the operations state's documented stance — rebuilt from the live roster,
Operations/CommanderOperationsService.cs:15-19).

## Verification

Self-checks: ladder at every boundary (0, 1, 2, 3, 5, 6, 9, 10, 100); the CAP ladder at 0, 1, 2 and
9 tracked hostile aircraft and its negative-input floor; CAP served before CAS at equal demand
(`NextAirDemand`); the buys-per-review bound (`AirBuyContinues`, `MaxAirBuysPerReview` 1..5) and the
air fund ceiling's fighter floor; cooldown 2 min at 0-1 observed air-defence and 4 at ≥2; priority
order on synthetic missions; retarget hysteresis at 3 km; config guards (cooldown > 0, ceiling ≥ 1).
Pre-emptive (decision 12): `WantsPreemptiveAir` exactly at the range, one metre past it, one metre
inside it, `Attacking` yes, `Forming`/`Holding`/`Withdrawing` no, and no enemy anywhere no; the range
is `ObservedRadiusMeters` and the hold outlasts one review; `PlanPreemptiveSortie` gives 1 CAS +
1 CAP at 0 observed and the ladder's own values at 1, 6 and 100 observed and at 2 tracked hostile
aircraft.

In game, pre-emptive: order a platoon at a point on the enemy's side of the map and watch the
COMMANDER LOG before any contact. `tasks <jet> as the escort over <N>TH PLATOON (pre-emptive)` and
`tasks <jet> with CAS over <N>TH PLATOON (pre-emptive, enemy <n> km)` must both appear BEFORE that
platoon's first `in contact` / contact-sortie line, and the review line must carry
`air=[<N>TH PLATOON CAP 1/1 CAS 1/1 pre]`. Walk the platoon back out of the ring: the entry survives
one review and is gone by the next. Stop the platoon on its objective (Holding): the sortie releases
its airframes to the home CAP at the next review.

In game (host). Duel: build and install, load Ground Control Duel, wait for the first attack to
form. Log lines that prove it: `air demand: CAP n/m …` on the review line with `m ≥ 1` while an
attack is forming; a fighter launched for the CAP (`launched a <jet> (Fighter) …`) BEFORE the first
`(Strike)` launch of the same match; `tasks <jet> as the escort over <target>` or `on CAP over
<target>`, then `tasks <jet> with CAS over <target>`; the review line's
`air=[<label> CAP a/b CAS c/d]`. Three or more `launched a` lines between two Ops review lines proves
the multi-buy throughput. Fly a hostile aircraft inside the ring → the CAP wing grows
(`(wing 2/3; 1 hostile air tracked)`). Shoot the CAS airframe down → the loss line, no
replacement for the cooldown, then a relaunch while the attack is still live. Take the objective →
the release line; unbound airframes show AirGuard over home and no StrategicStrike line ever
appears. PLAYER COMMANDER ON: the player-side commander buys and tasks only its own airframes; a
player AIR mission is never named by a sortie line. One stock mission (Escalation, MATCHED): the air
fund accrues and CAS flies over the attack target — the duel gate is gone — and no mod line ever
names a mission-authored free aircraft. Hangar toggle on and off: launch path changes, tasking
unchanged.

## Resolved questions (user, 2026-09-13)

1. **Home CAP share** — kept: standing `AirGuard` over home territory for every unbound airframe
   (see Approval 2).
2. **Buying air on stock missions** — yes: the duel-only gate on the buy leg is removed too;
   mission-authored free aircraft are never claimed or retasked (see Approval 3).

## Open questions — NEEDS USER

None.