# Design: Truck logistics (income rides on trucks)

**Date**: 2026-09-13 · **Track**: truck-logistics_20260913 · **Approved by user**: 2026-09-13 (three
questions answered in the brainstorm; summary accepted).
**Depends on**: strategic-points_20260913 (mines, control points, income tick). Independent of
platoon-operations_20260913 except where noted; designed so the two do not edit the same files.

Third of four AI tracks. Turns the economy's passive income into convoys that can be escorted,
intercepted and lost.

## Problem

Every income source pays straight into the treasury every 15 s (`CommanderEconomyService.PayIncome`
and `CommanderStrategicPointService.PayPointIncome`). Nothing moves, so nothing can be raided, and a
supply line is not a thing on the map. The user wants traffic both ways and a reason to escort.

## Decisions taken (user, 2026-09-13)

1. **Mines and every control point pay only by truck.** Base income stays per tick.
2. **Trucks are persistent and round-trip**: out with the load to the nearest held base, pay on
   entering the base ring, drive back empty, wait for the next load, repeat. One truck per paying
   point.
3. **A lost truck loses only its load.** A fresh truck spawns at the point on the next cadence at no
   charge. No escorts by rule; the platoon doctrine's FOBs already sit on the front-line points.
4. **Cadence**: one load every 3 minutes carrying 3 minutes of that point's income (both config).

## Section 1 — The truck

- Vehicle type: the faction's rearm truck, found the way the platoon track finds FOB trucks
  (`IsMunitionsTruckDefinition`: prefab carries the game's rearm behaviour). If the faction fields
  none, the point falls back to per-tick income and the log says so once.
- Spawned by the mod at the point (same spawn path the depot uses for bought vehicles, no stock or
  funds consumed; the truck is infrastructure, not a purchase). Owned by the point's HQ. Claimed by
  the logistics service so the operations pool never takes it (the claim test lives in one place;
  add a `IsLogisticsTruck` predicate the operations claim consults).
- Detached from the game's rearm controller with the shared `TryDetachFromRearmLogistics(unit,
  allowRestock: false)` so it does not wander off to restock.
- Driven by the game's road pathfinding via `CommanderGameAccess.TrySetDestination`. Destination:
  the nearest airbase the owner holds, measured by straight-line distance (road distance would need
  a network traversal per point per review; not worth it).

## Section 2 — Loads and payout

- Each paying point (mine level ≥ 1, or a held control point) accrues its per-minute income into a
  **hopper** every income tick instead of paying it. When the hopper holds `LoadMinutes` (3) of
  income and the truck is at the point, the truck takes the whole hopper as its load and departs.
- Payout: when the loaded truck enters the destination base's capture ring, `hq.AddFunds(load)`,
  load = 0, state Returning. The truck drives back to the point and waits.
- Hopper cap: 2 loads. A point whose truck is dead or away stops accruing beyond that, so a cut
  supply line costs real income rather than banking it.
- Server only; same guard as every income path.

## Section 3 — Loss and recovery

- Truck destroyed: its load is gone. The point's hopper keeps accruing (to the cap); a new truck
  spawns at the point on the next cadence tick if the point still pays and the owner still holds it.
- Point lost or mine destroyed: the truck is released — if loaded and closer to home than to the
  point, it completes the run; otherwise it is removed (returned to nothing; it was free).
- Destination base lost while en route: re-target to the next nearest held base; none → the truck
  parks at the point with the load and pays if a base is regained.

## Section 4 — Seeing it and AI awareness

- Truck marker in the world and on the map: `SUPPLY → <base> $X` while loaded, `RETURNING` on the
  way back, `LOADING` at the point. Colour by owner. Enemy trucks show only where tracked.
- Point labels and the STRATEGIC POINT card: income reads `+X/min via truck`, and `N in transit`
  where a load is on the road. COMMANDER LOG income header gains `IN TRANSIT +X`.
- AI awareness (hook only, no new behaviour): a tracked enemy truck within a FOB's ring radius ×3 is
  posted as a threat mark on that FOB's point, and a point whose trucks have died twice in 10 minutes
  gets a threat mark so the platoon doctrine promotes it. Interdiction patrols are the platoon
  track's later iteration.
- Settings: new `Logistics` section — `Enabled` (true), `LoadMinutes` (3), `HopperCapLoads` (2),
  `ReplaceTruckSeconds` (180). Sliders for LoadMinutes on the POINTS tab's OPERATIONS block.

## Out of scope

Escorts by rule; truck purchase costs; fuel; player-issued convoy orders; supply to FOBs (the FOB
truck is the platoon track's and rearms, it does not carry money); multiplayer clients.

## Verification

Self-checks (pure): hopper accrual and cap; load hand-off only when truck present and hopper ≥ one
load; payout state machine (Loading → Outbound → Paid → Returning → Loading); destination choice
(nearest held base, re-target when lost); loss rule (load gone, hopper keeps accruing to cap).
In game (host, Ground Control Duel): a mine spawns a truck within a minute, funds stop ticking from
that mine and jump by 3 minutes' worth when the truck reaches the base, the truck drives back, and
killing a loaded enemy truck denies exactly its load; the COMMANDER LOG shows IN TRANSIT.
