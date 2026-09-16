# Insertion shield and righting — design

Track: `insertion-shield_20260915`. Approved by the user 2026-09-15 ("approve").

## Problem (user's words)

> "we need to make picket air delivered units invulnerable until after they're inserted - they have a
> habit of being dropped from a too-high height and dying on contact with the ground. or parachuting
> in but landing upside down"

## Decision (user, 2026-09-15)

Shield plus righting: no damage of any kind from the moment a cargo vehicle leaves the aircraft until it
has stood on the ground, upright and still for a few seconds (or a timeout); a vehicle that comes to rest
on its side or roof is set upright where it lies. Applies to every cargo run through the supply service:
picket insertions, air-mobile lifts, FOB loads, airdrop or landing. Drop heights and parachutes are not
changed.

## Reuse

- The cargo activation hook already in place: `MountedCargo.ActivateCargoVehicle` postfix →
  `CommanderSupplyHeliService.NotifyCargoActivated` → `HoldDeployedCargo` (`Supply/CommanderSupplyHeliMission.cs`).
- The game's single damage entry for a unit's parts: `UnitPart.TakeDamage(pierce, blast, amount, fire,
  impact, dealer)`; `UnitPart.parentUnit` names the vehicle.
- `CommanderGameAccess.SnapToTerrain` for the ground height; `Unit.rb` for stillness and righting.
- The supply service's `TickPersistent` for the sweep; `CommanderPlugin` self-check list.

## Design

A partial of the supply service (`Supply/CommanderSupplyHeliShield.cs`) keeps a set of shielded vehicles.
`ShieldDeliveredCargo` adds a vehicle when it activates. A Harmony prefix on `UnitPart.TakeDamage` returns
false for a part of a shielded vehicle. `SweepShields` runs every tick: on ground (within
`GroundContactMeters` 2.5 of the terrain), upright (`UprightDotThreshold` 0.7), still (under
`StillSpeedMetersPerSecond` 0.5) for `InsertionSettleSeconds` (3) lifts the shield with a log; a still,
grounded vehicle that is not upright is righted (`RightVehicle`: heading kept, pitch and roll zeroed,
lifted `RightingLiftMeters` 1.5, velocities zeroed) with a log; `InsertionShieldMaxSeconds` (90) lifts
the shield whatever the state, with the numbers in the log. Pure rules `InsertionSettled`,
`NeedsRighting`, `ShieldExpired`, `IsUpright` with self-checks in `CommanderSupplyHeliService.SelfCheck`.

## Out of scope

Aircraft, road trucks, drop heights, parachute physics, excluding player-ordered cargo runs (they take the
shield too).

## Verification

Watch a picket insertion or lift land: `<vehicle> settled at <point> after N s; shield off`, and on a
hillside `righted <vehicle> at <point>`. No `Supply self-check FAILED`.
