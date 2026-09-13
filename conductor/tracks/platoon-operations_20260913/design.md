# Design: Platoon operations (FOBs, pickets, offensives)

**Date**: 2026-09-13 · **Track**: platoon-operations_20260913 · **Approved by user**: 2026-09-13
**Depends on**: strategic-points_20260913 (the point list is the objective list; the garrison step it added is replaced here).

Second of four tracks. Replaces "piecemeal vehicles trickle to points, everything else streams at
the nearest enemy in one convoy" with a front line the AI commanders form, hold and push.

## Problem

- Bought vehicles enter through `hq.ModifyUnitSupply` and the game's depot deployment; the game's
  convoy brain then drives them at the nearest known enemy along the road network. One stream, one
  road (`Ai/CommanderEnemyCommanderService.cs` class remarks; `CommanderCaptureService.ReviewEnemy`
  is the only ground routing the mod does today, and it drives three units to a hold point).
- The points track's garrison step (`Ai/CommanderEnemyCommanderGarrison.cs`) sends idle singletons
  to nearby control points: random, small, and not coordinated with anything.
- The AI never forms up, never attacks from more than one direction, and never scales an attack to
  what it can see.

## Decisions taken (user, 2026-09-13)

1. **The mod owns every vehicle an AI commander buys.** Claimed at the depot into an operations
   pool; the game's convoy brain never gets them.
2. **A FOB is a platoon plus one supply truck at a control point**, and doubles as a form-up area.
3. **Platoon recipe: 3 armour, 1 carrier or light vehicle (prefer capture-capable), 2 air defence**,
   size 6. Missing roles fall back to any combat vehicle rather than waiting.
4. **Offensives run on 2 or 3 axes** from roughly equidistant form-up points and **scale to the
   observed enemy** (the commander's own tracking database, not the true count).
5. **As many FOBs as the front has valuable points**, subject to a FOB share of the platoon count;
   **rear points get a two-vehicle picket** so they keep paying.
6. **AI only in this track.** The player commands with existing orders; the player-side AI runs the
   same doctrine. Player platoon tools are a later track.
7. **Guard against endless expansion**: point value weighted by contact, attacks gated on the axes'
   FOBs only, a pressure clock forcing an attack at least every 12 minutes, and a FOB share cap.
8. **Buying cap acknowledged**: the buyer's tempo (25%/45% of pot, 3/5 purchases per 30 s) is a
   knob; open attack requisitions raise it for the review.

## Section 1 — Platoons and the depot claim

- **Claim**: for every AI-commanded HQ (`CommanderPlayerCommanderService.IsCommanded`), any ground
  vehicle registering with the faction (`FactionHQ.RegisterFactionUnit` postfix, already patched
  for Air Command) is claimed into the operations pool. Claimed vehicles are ordered only by the mod.
  The player's hand-bought vehicles are never claimed while the player commander is off; with it
  on, the existing `HasPlayerOrder` hands-off rule (10 min) protects anything the player has ordered.
- **Platoon**: named (`1ST PLATOON`…), up to `PlatoonSize` (6) vehicles from the pool by recipe
  3 armour (MBT/AFV) · 1 carrier/light (APC/LCV, prefer `captureStrength > 0`) · 2 air defence
  (AAA/IR_SAM/R_SAM). Unfillable slot → any combat vehicle. One objective, one state:
  Forming, Moving, Holding, Attacking, Withdrawing.
- **Movement**: leader routed by the game's pathfinding (`UnitCommand.SetDestination`), members in
  the move service's wedge formation slots (reuse `CommanderMoveService` formation code; no second
  follower). "Arrived" = leader inside the objective ring and ≥ half the members within
  `FormationCohesionMeters`.
- **Losses**: under 50% strength → Withdrawing to the nearest friendly FOB or base, requisition
  replacements. Zero → dissolved, mission reopens.
- **Absorbed**: the home guard becomes the base's reserve platoon(s); the garrison step is replaced
  by FOB/picket missions; recon vehicles stay a separate small job.

## Section 2 — Front line, FOBs and pickets

- **Front line** recomputed every 30 s: for each held point/base, distance to the nearest
  enemy-held point or base. Within `FrontRangeMeters` (15 km) = front; else rear.
- **Point value** = income × frontage factor (closer to enemy assets = higher). Missions are filled
  in value order, so the line thickens toward the enemy.
- **FOB mission (`Hold`)**: one platoon at each front point held or reachable; a point with a
  threat mark (hostile ground tracked within 45 s) asks for two. Priority: nearest the enemy, then
  hilltops overlooking an approach, then the rest.
- **Logistics**: each FOB requisitions one munitions truck (the game's rearm vehicle; the supply
  code already knows the type), parked in the ring. None available → FOB runs without, logged once.
- **Picket mission**: rear points want exactly `MinGarrison` (2) cheapest vehicles so they keep
  paying. Filled after FOBs, before offensives. A picket that sees the enemy raises a threat mark
  and the point promotes to front/FOB. A FOB whose point turns rear thins to a picket.
- **FOB share**: at most `FobShare` (50%) of a commander's platoons sit in FOBs; the rest are
  reserve or offensive. Surplus front points get pickets.

## Section 3 — Offensives

- **Trigger**: the FOBs that would serve as axes for the chosen target exist and are manned, and at
  least one spare platoon exists or is forming. Not "every FOB manned".
- **Targets**, ranked: enemy-held points adjacent to my front; enemy bases when observed defence is
  beatable.
- **Sizing**: observed = tracked hostile ground units within 8 km of the target seen in the last
  45 s. Platoons = ceil(1.5 × observed / PlatoonSize), min 2 for a base, min 1 for a point, max 6.
- **Axes**: form-up candidates = my FOBs and held bases within 25 km of the target. Pick 2–3 whose
  distances to the target are within 20% of each other and whose bearings from the target differ by
  ≥ 60°. One candidate only → second group forms at a flank point 6–10 km off the direct line. None
  → wait.
- **Approach**: each group routes to a **release point** 5 km short of the target on its side
  (road network where possible, terrain planner otherwise). Groups wait until all arrive (timeout
  4 min, then go), then assault together. Points within 2 km of the route are flipped in passing.
- **Pressure clock**: +1/min, bonus per point lost and per enemy point closer to my base than
  theirs. At `PressureIntervalMinutes` (12) the commander launches its best available attack with
  ≥ 2 platoons regardless of ideal sizing; resets on launch.
- **Outcome**: taken → attackers become FOBs at the new front. Failed (strength < 40%) → withdraw
  to release points; the mission closes with the observed count updated.

## Section 4 — Requisitions and the buyer

- Missions post requisitions (N of role R). The operations service sums an **order book** each
  review: armour, carriers, air defence, trucks.
- The buy loop keeps the plan-based counter triangle only when the order book is empty; otherwise
  each purchase fills the largest open line, cheapest vehicle of that role the faction fields.
  Existing overrides (first air defence, capture unit) stay ahead.
- Open attack requisition → this review spends `OffensiveSpendFraction` (50%) with
  `OffensivePurchasesPerReview` (5).
- Depot claim assigns each vehicle to the oldest matching requisition; mission notified when full.
- A requisition unfilled for 5 min proceeds with what it has or dissolves, logged.

## Section 5 — Seeing it

- **COMMANDER LOG**: OPERATIONS block per faction — pressure, platoon states, open requisitions,
  one line per mission (`ATTACK Maris Airport: 3 platoons, axes FOB Hilltop 12 + Crossroads 4,
  forming up 2/3`).
- **Markers**: platoon marker at the leader (`2ND PLATOON 5/6 HOLDING`), `FOB` tag on point labels,
  release points as small crosses during an attack. Enemy platoons only where tracked.
- **Settings**: OPERATIONS block on the POINTS tab — platoon size, FOB share, front range, pressure
  interval, offensive spend. Recipe config-only.

## Out of scope

Player platoon tools (FORM PLATOON, choosing axes); truck logistics with money-on-arrival (track 3);
air and naval support tasking (track 4); multiplayer clients (host-only like every AI feature).

## Verification

Self-checks (pure): axis selection on a synthetic layout (equidistance + bearing), sizing formula,
pressure clock, front/rear classification, order-book summing, recipe fill with fallbacks.
In game (host, Ground Control Duel): within 10 min FOBs form at front points with a truck; the log
shows a first attack forming on two axes; groups wait at release points then go in together; a
failed attack's successor asks for more; the enemy no longer arrives as one convoy; pickets hold
rear points and they keep paying.
