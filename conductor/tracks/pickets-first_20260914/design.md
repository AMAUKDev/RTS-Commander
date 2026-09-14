# Design: Pickets first — pickets capture, platoons only toward the enemy

**Date**: 2026-09-14 · **Track**: pickets-first_20260914 · **Approved by user**: 2026-09-14
**Depends on**: platoon-operations (pool, missions, order book), heli-picket-insertion (air lift),
commander-priorities (ladder rungs), ground-tactics (postures).

## Problem (user's words)

"1. pickets should be the primary capture mechanic, rather than spamming large platoons all over
the map (including away from the enemy). 2. platoons should be used if going towards the enemy (or
suspected enemy). 3. multiple airborne insertions of pickets at once." Plus the cap audit: the
8-platoon cap never bound (any open requisition lifts it; the book never empties) and the player
side reached 27 platoons; the reinforcement cap of 3 bound 24 times.

## Decisions (user, 2026-09-14)

1. Pickets (two vehicles) capture and hold every control point that is not a front point; they take
   pool vehicles ahead of new platoon formation.
2. A platoon forms only for a purpose facing the enemy: a forward base on a front point, an attack,
   or the standing reserve (one platoon by default).
3. Ground buying is order-book driven only: the plan buyer stops buying vehicles nobody requested.
   The platoon cap is removed (superseded).
4. Up to three insertion flights airborne per commander and three requests per review (one per
   point), behind the existing threat gate and loss cooldown.
5. Reinforcement platoons per request: 6 (was 3). Home CAP max stays 4 (lending covers the rest).
   Insertion loss cooldown stays 10 minutes.

## Reuse

- Pool and claim: `SweepPool`, `TryClaim`, `PlatoonsNeedThePool` (`Operations/CommanderOperationsRequisitions.cs`,
  `CommanderOperationsFront.cs`); picket planning `PlanPickets` / `FillPickets`; forward-base
  planning `PlanForwardBases` (front points, threat marks, `IsHeldOrReachable`); attack sizing and
  `AssignPlatoons`; `TryFormPlatoon` / `MaxPlatoonsFormedPerReview`; standing reserve requisition
  (`PostWithdrawingRequisitions`, `OperationsReservePlatoons`); the buyer's `ReviewPurchases` branch
  order and `GroundBuyingCapped`; insertion `PlanInsertions`, `OperationsHeliInsertionLimit`,
  `CountInsertionsInFlight`, route threat gate, loss streak pause.
- Not reused: the platoon cap (`OperationsMaxPlatoons`, `GroundBuyingCapped`'s count) — deleted.

## Section 1 — Pickets first

- `PlanPickets` covers **every** control point that is not a front point (held by us, neutral, or
  reachable per `IsHeldOrReachable`), not only "rear" ones, ranked by value. Pool order flips:
  `FillPickets` runs before platoon formation and `PlatoonsNeedThePool` no longer withholds the pool
  from pickets; instead `PicketsNeedThePool` withholds pool vehicles from *platoon formation* while
  any picket mission on a non-front point is short.
- The order book posts picket shortfalls as requisitions (AirDefence + cheapest other), so the buyer
  buys picket vehicles when the pool is empty.
- Insertion stays the delivery for points > 2 km from a road; near-road pickets drive.

## Section 2 — Platoons only with a purpose

- `TryFormPlatoon` runs only when `PlatoonPurposeCount(state) > platoonCount`, where purposes =
  ForwardBase missions on front points wanting platoons + Attack missions' wanted platoons +
  `OperationsReservePlatoons` (default lowered to 1). No purpose → no platoon; the log says once per
  change: `no purpose for a new platoon; N vehicles wait as picket stock`.
- Every formation logs its purpose: `forms 3RD PLATOON for ForwardBase CROSSROADS 13` /
  `for Attack Maris Airport` / `as the reserve`.
- A platoon whose purpose resolves (point lost or no longer front, attack over) and that is not
  needed as reserve dissolves back to the pool (existing fold path) so its vehicles become pickets.

## Section 3 — Order-book-only ground buying

`GroundBuyingCapped` becomes `GroundBuyingBookOnly(ownsGroundForce, hasOpenRequisition)`: with an
operations state, the ground buyer buys only for open requisitions (picket shortfalls, forward-base
trucks, platoon replacements, reinforcements, the standing reserve). The plan buyer (`Choose(spendable,
buyPlan)`), the capture and recon overrides still run when the operations service does not own the
force (stock missions before discovery). `OperationsMaxPlatoons` is deleted (cfg key left orphaned).

## Section 4 — Several insertions at once

`OperationsHeliInsertionLimit` default 3; `PlanInsertions` issues up to `InsertionRequestsPerReview`
(3) requests per review, one per point, farthest-off-road first, each behind the threat gate, the
per-point cooldown and the commander-wide loss pause. `heli=n/3` on the review line.

## Section 5 — Caps

`MaxReinforcementPlatoons` 3 → 6 (self-check updated). `HomeCapMax` 4 unchanged.
`OperationsHeliInsertionCooldownMinutes` 10 unchanged.

## Self-checks

Purpose count arithmetic (FOB + attack + reserve; no purpose → no formation); pickets-first pool
withholding; book-only rule (owns + no book → no buy; owns + book → buy; not owned → plan buyer);
insertion per-review cap and airborne limit; reinforcement cap at 6.

## Verification (in game)

Within ten minutes the review line shows most non-front points with `pickets=2`, `heli=2/3` or
`3/3` while hilltops are being lifted, and platoons only where the line reads `ForwardBase` on a
front point or `Attack`; the log carries `forms … for …` with a purpose and `no purpose for a new
platoon` when the front is quiet; no `bought … for …` plan line without a matching requisition; a
reinforcement request may read `requests 5 platoon(s)`.
