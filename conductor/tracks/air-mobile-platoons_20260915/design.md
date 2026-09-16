# Air-mobile platoons and protected lifts — design

Track: `air-mobile-platoons_20260915`. Approach 1 approved by the user 2026-09-15 ("approach 1 go").

## Problem (user's words)

> "one of the problems with the 'Far' scenario is that platoons take an age to meet an enemy (this
> is what FOBs were designed to solve, but they get shot down alot and often only succeed after
> loads of platoon units have spawned and are slowly slowly driving across the map). How can we
> tackle this? Aerial insertions of platoons…"

Evidence: on the 82 km map a platoon raised at the home depot drives 15–20 km to an in-reach
objective at roughly 15 m/s — twenty minutes and more before first contact. FOB construction
flights were lost to air defence or to the deck (2026-09-15 logs) and a FOB comes online late.
The transports' cargo roster carries the light vehicle family (APC, IFV, AT, AA) but no tanks
(`Insertion cargo roster` log line).

## Decisions (user, 2026-09-15)

1. **Real cargo only**: air-mobile platoons are made of vehicles the transports can actually carry;
   tanks still drive. No "transport as a token".
2. **Fly beyond a drive-time threshold**: when the nearest usable depot is more than about ten
   minutes' drive from the objective, the platoon is raised air-mobile.
3. **Escort plus sweep first**: every transport package (air-mobile lift AND FOB construction
   flight) gets fighters at least one-for-one with tracked hostile air, and an anti-radar sortie
   goes in ahead when air defence is tracked near the route or landing zone; the transports wait at
   the form-up point until the cover is up.
4. Approach 1: air-mobile platoons inside the operations service, built on the FOB order and the
   insertion flight, no new service.

## Reuse (read first)

- FOB orders and flights: `Operations/CommanderOperationsFob.cs` — `CommanderFobOrder` (loads,
  `Delivered`, `Lost`, `LaunchFailures`, `LastProgressAt`, `Posts`), `CommanderFobFlight`,
  `DispatchFobAirFlights` (one flight at a time per point, landing-zone scout, airdrop fallback,
  `preferHeavyHull`), `PruneFobFlights` (stall clock, loss, abandoned-on-deck), `FobOrderCanContinue`,
  `FobOrderHasStalled`, `TryBindFobAircraft`, `TryTakeFobDelivery`, `TryNoteFobLaunchFailed`.
  **This is the second instance of "deliver N loads of vehicles by air to a point"; Reuse rule 5 says
  generalise it**: the order becomes a lift with a purpose (FOB construction or platoon), and the FOB
  keeps its behaviour through the purpose.
- Insertion flights and the supply side: `Operations/CommanderOperationsInsertion.cs`
  (`QualifiesForInsertion`, `InsertionStandoffClear`, `NotifyInsertionAircraft`,
  `NotifyPicketVehicleDelivered`, `NoteInsertionLost`, `NoteInsertionLaunchFailed`, `AdoptPicketVehicle`),
  `Supply/CommanderSupplyHeliMission.cs` (`TryLaunchInsertionAircraft(hq, point, target, allowance,
  wantedVehicles, requireAirdrop, out decline, out charged, preferHeavyHull)`, `InsertionVehicle`,
  `CheapestInsertionFlightValue`, the cargo roster by role).
- Escorts and packages: `Operations/CommanderOperationsAirPlatoonCap.cs` (`AddTransportEscortDemand`,
  `TransportEscortWanted`, `TransportEscortMinimum` 2), `Operations/CommanderOperationsAirPackages.cs`
  (form-up, go-in, `AradPending`, the strike package's `StrikeEscortWanted(floor, hostileAirNow,
  headroom)` from track strike-packages_20260915), `Operations/CommanderOperationsAirArad.cs`
  (`InsertAradDemand`, the ARAD-first gate).
- Platoons and the order book: `Operations/CommanderOperationsFront.cs` (`PlanForwardBases`,
  `EnsureHoldPosts`, `RefreshReach`, `OutOfReach`), `Operations/CommanderOperationsService.cs`
  (`TryFormPlatoon`, `AssignPlatoons`, recipe `RecipeArmour` 3 / `RecipeCarrier` 1 /
  `RecipeAirDefence` 2), `Operations/CommanderOperationsRequisitions.cs`
  (`PostForwardBaseRequisitions`, `PostRecipeShortfall`, coverage), `Operations/CommanderPlatoon.cs`
  (`CommanderPlatoonRoles.Of`).
- Depots and drive distance: `Economy/CommanderFobBuilder.cs` `TryNearestOwnedDepot`,
  `Points/CommanderStrategicPointService.cs` road network (`NearestRoadDistanceMeters`,
  `TryNearestRoadPoint`).
- Markers: `Operations/CommanderOperationsMarkers.cs` (`FobMarkerText`, convoy markers),
  `Operations/CommanderOperationsAirMarkers.cs` (`FindFobFlight`, `INSERTION FOB <point>` label).

Why nothing fits as-is: the picket insertion delivers at most two vehicles to a point and adopts
them into a picket, not a platoon; the FOB order delivers loads but only to build; neither has an
escort-and-sweep package; the transport escort is fighters only.

## Section 1 — The drive-time gate

- `DriveMinutes(meters, detourFactor, speedMetersPerSecond)` pure: straight-line distance from the
  nearest usable depot to the objective × `RoadDetourFactor` (1.3, roads are not straight) ÷
  `GroundSpeedMetersPerSecond` (15, a loaded tracked vehicle on a road) ÷ 60.
- `PlatoonFlies(driveMinutes, thresholdMinutes, liftPossible)` pure: true when the drive exceeds
  `AirMobileDriveMinutes` (10) and a vehicle-carrying transport can launch from a held base within
  `HeliInsertionRangeMeters` of the objective, and the objective clears `InsertionStandoffClear`
  with the FOB standoff OR the lift will be escorted (decision 3 makes escorted lifts legal inside
  the standoff; the lift's escort floor rises to 2 there).
- Read in `PlanForwardBases` / the attack planner when a mission would raise a NEW platoon: the
  mission is marked `AirMobile` and its requisition lines use the air-mobile recipe (Section 2).
  An existing driving platoon is never converted.
- Log: `raises 3RD PLATOON air-mobile for HILLTOP 9: 34 km from the nearest depot, 23 min by road`.

## Section 2 — The air-mobile recipe and the lift order

- Recipe `AirMobileRecipe`: Carrier 4 (IFV / APC / AT, whatever the cargo roster offers by role),
  AirDefence 2, Armour 0 — six vehicles, three loads of two, `LiftLoadsPerPlatoon` (3),
  `LiftVehiclesPerLoad` (2). The armour slot (`RecipeArmour`) is posted as an ordinary shortfall
  line ONLY once a usable depot is within the drive threshold (a FOB comes online), so tanks follow
  by road later; until then the platoon is complete at six light vehicles.
- `CommanderFobOrder` is generalised into a **lift order** with `Purpose` (FobConstruction |
  Platoon), keeping every FOB rule by purpose: loads wanted (`FobDeliveries` 1 for FOB,
  `LiftLoadsPerPlatoon` for a platoon), per-load clock, lost-load replacement up to
  `FobMaxLostLoads`, first-loss-with-nothing-landed cancels, abandoned-on-deck re-send, the
  landing-zone scout and airdrop fallback, heavy-hull preference. A platoon lift's cargo is chosen
  from the recipe's remaining roles (the cargo chooser gains a wanted-roles argument; today it
  prefers air defence and then the cheapest).
- Vehicles landed by a platoon lift are adopted straight into the mission's platoon (the picket
  adoption path, `AdoptPicketVehicle`, gains the platoon case), stand on the point's hold posts, and
  the platoon state machine takes over exactly as if they had driven in.
- Money: a lift is charged at launch from funds like a construction flight; a lift is launched only
  while `funds >= LiftFundsMultiple` (2) × the flight price (`FobAffordable`'s shape), so a lift
  never empties the treasury.
- One lift order per point at a time (the supply side's one-mission-per-point invariant, unchanged).

## Section 3 — Protection: escort and sweep for every lift

- A lift order (FOB or platoon) opens a **lift cover sortie** over its landing zone from its first
  launch to its last landing: escorts = `StrikeEscortWanted(floor, hostileAirNow, headroom)` with
  floor `LiftEscortMinimum` (2), so never fewer than the hostile aircraft tracked near the zone or
  the route; an anti-radar element of 1 when air defence is tracked within the ARAD cluster distance
  of the zone or the route (`TryFindInsertionRouteThreat`).
- The transport waits at the package form-up point until the escorts are airborne and the ARAD
  element has gone in (`AradPending`), bounded by `PackageFormUpSeconds` as every package is; the
  existing `AddTransportEscortDemand` becomes the lift cover's escort half so there is one escort
  rule for transports.
- Log: `lift 1/3 for 3RD PLATOON away (escort 2 of 2 up, sweep gone in)`,
  `lift for 3RD PLATOON holds at the form-up point: waiting for the sweep`.

## Section 4 — What the player sees

- Markers: `LIFT 3RD PLATOON — 1/3, outbound` on the transport; `LIFT ESCORT HILLTOP 9` on escorts;
  the platoon's own marker reads `3RD PLATOON (air-mobile) 4/6` until complete.
- Review line: the `fob=` field becomes `lift=` and names the purpose (`lift=FOB HILLTOP 9 0/1`,
  `lift=3RD PLATOON 2/6`).
- Logs: `3RD PLATOON: 2 vehicles landed on HILLTOP 9 (4/6)`, `3RD PLATOON complete on HILLTOP 9
  by air; armour follows by road when a depot is within 10 min`, `3RD PLATOON lift cancelled: <FOB
  reason text>`.
- Settings (Operations): `AirMobileDriveMinutes` 10, `RoadDetourFactor` 1.3,
  `GroundSpeedMetersPerSecond` 15, `LiftLoadsPerPlatoon` 3, `LiftEscortMinimum` 2,
  `LiftFundsMultiple` 2. Self-checks for every pure rule.

## Section 5 — The forward landing zone (lead decision 2026-09-15, fix cycle 1)

Added after the first live run: most platoon lifts were cancelled with `the enemy holds the point`
and the platoon then drove for an hour — the problem the track exists to remove. A transport cannot
land on ground the enemy is standing on, so the lift lands short instead.

- When an air-mobile mission's objective is enemy-held, the lift's landing point is the nearest point
  the commander OWNS or that nobody holds within `LiftAirheadMaxMeters` (10 000) of the objective,
  chosen when the lift is ordered. Own beats neutral at equal distance; nearer beats farther.
- The landed vehicles adopt into the platoon at the landing zone, where the platoon gathers in
  `Forming` exactly as one being bought gathers at its form-up point. When the lift completes the
  platoon moves out to the objective through the ordinary march, which drives the last leg.
- With no such ground the mission is raised as a driving platoon (`AirMobile` cleared):
  `raises 3RD PLATOON by road: CROSSROADS 9 is enemy-held and no friendly ground within 10 km to land on`.
- The FOB purpose keeps its "enemy holds the point" cancel unchanged. A platoon lift whose LANDING
  zone changes hands keeps its air-mobile mark and picks other ground next review.
- Log: `orders a lift for 3RD PLATOON onto HILLTOP 4 (forward landing zone, 6 km short of CROSSROADS 9): 3 loads of 2`.

Also decided in the same cycle: a lift's money gate is asked at every launch rather than once at the
order; a platoon under its establishment gets a top-up lift of `ceil(shortfall / VehiclesPerLoad)`
loads; a cancelled platoon lift takes its own cooldown rather than the FOB site's.

## Out of scope

Tanks by air (no such cargo); a lift of an existing driving platoon; player UI to order a lift;
changing the FOB site rules.

## Verification (running game)

`Ground Control Duel Far`: within fifteen minutes expect `raises … air-mobile …`, three `lift n/3`
launches with `escort 2 of 2 up`, `vehicles landed … (6/6)` and a platoon holding a point 30+ km from
its depot; a FOB construction flight now logging its escort; no `self-check FAILED`.
