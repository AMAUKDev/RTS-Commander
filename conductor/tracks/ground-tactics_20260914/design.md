# Design: Ground tactics — ring, defence arc, bounding advance, counter-attack

**Date**: 2026-09-14 · **Track**: ground-tactics_20260914 · **Approved by user**: 2026-09-14
**Depends on**: platoon-operations_20260913 and its 2026-09-14 addendum (reactive contact,
reinforcement requests, marker text), strategic-points (hold radius, road polylines).

## Problem (user's words)

"I have a platoon holding Crossroads 13 — it's just sat in a tight grouping on the point. Enemy
platoons are attacking it, and they just blindly advance along the road as a convoy. Reinforcing
platoons also just cluster on a point."

Today: `EnsureHoldPosts` places posts on a ring at `Radius × 0.6` (`Operations/CommanderOperationsFront.cs`),
so six vehicles bunch; a reinforcing platoon is assigned to the same mission and gets the same
posts; an attack axis drives the road from its release point to the objective, and the contact
drill (`Operations/CommanderOperationsService.cs` `ContactDrill`) only deploys on a *tracked*
hostile, so an untracked defender is met in column.

## Decisions (user, 2026-09-14)

1. **Wide ring by default**, then an **adaptive formation when an enemy is detected** — tanks and
   IFVs at the front.
2. **Reinforcements counter-attack** when the point is already held, preferably **not along roads
   when within 1 km of the enemy**.

## Reuse

- Hold posts and their issue: `EnsureHoldPosts`, `DriveToHoldPosts`, `platoon.Issued`
  (`CommanderOperationsFront.cs`); formation slots `CommanderDestinationFormation.ApplyOffset`
  (Ring / Line / Wedge / Column) and `CommanderMoveService.IssuePlatoonMove(…, facingDegrees)`.
- Contact evidence: `InContactUntil`, `ContactBearingAnchor`, mission `ContactUntil`,
  `TryNearestTrackedHostile`, `ContactRangeMeters` (2.5 km), `ContactHoldSeconds`.
- March order and roles: `OrderForMarch`, `CommanderPlatoonRoles.Of`.
- Attack axes and release points: `CommanderOperationsOffensive.cs` (`Axes`, `ReleasePoint`,
  `FirstGroupArrivedAt`, `HasArrived`, `CountInCohesion`).
- Reinforcement requests and `IsAvailableForMission`: the 2026-09-14 addendum.
- Road geometry: `CommanderStrategicPointService.NearestRoadDistanceMeters` (retained polylines).
- Observed enemy clusters: `CountObserved` / the ARAD cluster walk (reuse the clustering helper the
  smarter-air-wing track adds, or the tracking walk if it has not landed).
- Not reused: a separate tactics service (duplicates the move seam and the state machine).

## Section 1 — Wide ring

`EnsureHoldPosts(point, n)` places posts on the point's **full hold radius**, evenly spaced, with
at least `RingMinSpacingMeters` (250) between neighbours (fewer posts than vehicles → the ring is
oversubscribed and the remainder take an inner ring at half radius). Post assignment by role:
air defence takes the posts nearest the centre-most positions of the ring's threat-facing half
(or simply the two posts closest to the point centre when no threat is known — with a full-radius
ring these are chosen as an inner pair at `Radius × 0.3`), tanks and IFVs the outer ring, carrier
and truck the inner pair on the far side. Facing outward: each post's facing is its bearing from
the centre.

## Section 2 — Defence arc on detection

While the holding platoon or its point is in contact (`InContactUntil` / mission `ContactUntil`
live): tanks and IFVs (Armour and Carrier roles) move to an **arc** centred on the threat bearing
(`ContactBearingAnchor`), at `Radius + DefenceArcStandoffMeters` (300–500 m, clamped so the arc
stays inside the point's hold radius when the radius is small), `DefenceArcSpacingMeters` (150)
apart, tanks central; air defence keeps its posts; carrier-only members (no armour rating) and the
truck take posts on the far side of the ring. The arc is re-aimed every movement tick (5 s) while
the bearing moves more than 15°; the platoon returns to the ring `ContactHoldSeconds` × 3 (60 s)
after contact lapses. No vehicle leaves the hold radius, so the point keeps paying. Log:
`<platoon> forms a defence arc toward <bearing>° at <label>` / `<platoon> returns to the ring`.

## Section 3 — Attack approach in bounds

From the release point onward an attacking platoon does **not** path the road: it advances in
**bounds** of `BoundMeters` (800) straight toward the objective, in Line abreast (tanks leading via
`OrderForMarch`), each bound issued as formation slots on the cross-country line. The next bound
is issued when `CountInCohesion` ≥ half the platoon at the current bound (so no vehicle runs
ahead); a bound also times out after 90 s. Inside `OffRoadRangeMeters` (1000) of any tracked enemy
every destination is a bound (never a long road-following path). The contact drill (line facing a
tracked hostile) keeps priority over bounding. Log once per attack: `<platoon> leaves the road at
the release point; bounding to <label> in 800 m steps`.

## Section 4 — Reinforcements counter-attack or screen

A platoon answering a reinforcement request whose point is already held (garrison ≥
`PointsMinGarrison` present) does not take ring posts:
- **Enemy tracked** near the point: it moves to a **flank position** 1 km off the threat bearing
  (±90°, whichever side is farther from tracked hostiles), then attacks the nearest tracked hostile
  cluster in Line, bounding cross-country (Section 3 rules). Marker: `Counter-attacking from the
  flank`.
- **No enemy tracked**: it **screens** 800 m out on the most threatened approach (the road with the
  nearest tracked hostile history, else the road toward the enemy's nearest asset), in a Line facing
  outward. Marker: `Screening <label>`.
- It folds into the ring only when the garrison drops below minimum; when the request closes it
  returns to reserve (existing rule).

## Section 5 — Diagnostics, settings, self-checks

- Marker situation texts: `Holding <label>` (ring), `Defence line at <label>`, `Bounding to
  <label>`, `Counter-attacking from the flank`, `Screening <label>`.
- Settings (`Operations`, Get/Set + warm-up): `DefenceArcStandoffMeters` 400, `BoundMeters` 800,
  `OffRoadRangeMeters` 1000. Constants with `<summary>`: `RingMinSpacingMeters` 250,
  `DefenceArcSpacingMeters` 150, arc re-aim 15°, bound timeout 90 s, flank offset 1 km, screen 800 m.
- Self-checks (pure): ring spacing and oversubscription; role-to-post assignment; arc geometry on a
  bearing (centre tank on the bearing, neighbours ±150 m, standoff clamp); bound sizing and the
  half-closed rule; the 1 km off-road rule; counter-attack vs screen vs fold at the garrison boundary.

## Assumption to verify in game

The game's ground AI drives cross-country to a nearby off-road destination (formation slots already
rely on this). If 800 m bounds snap to roads, reduce `BoundMeters` to 400 and record it.

## Out of scope

Terrain-masked (reverse-slope) posts; infantry; player-issued postures; changes to the attack sizing
or axis selection.

## Verification

In game (host, Ground Control Duel): a holding platoon's markers spread to the point's edge with the
air defence inside; drive an enemy platoon at it → `forms a defence arc toward …` and tanks visibly
move to the threat side, air defence stays; after the fight `returns to the ring`. Watch an enemy
attack: at the release point the log shows `leaves the road … bounding`, and the platoon crosses
fields in line rather than driving the road in column. Trigger a reinforcement request at a held
point: the arriving platoon's marker reads `Counter-attacking from the flank` and it approaches the
enemy off-road from the side, or `Screening <label>` when no enemy is tracked.
