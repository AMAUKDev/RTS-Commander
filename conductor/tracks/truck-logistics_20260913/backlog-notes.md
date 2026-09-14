# Backlog: Truck logistics, money on arrival (track 3 of 4)

**Status**: not yet designed. Captured 2026-09-13 from the AI-behaviour brainstorm. Depends on
`strategic-points_20260913` (resource sites are the truck origins).

## What the user asked for (verbatim intent)

- Economic locations (resource sites / mines) **spawn munition or fuel trucks** on a cadence.
- Trucks are **convoy-routed to the nearest airbase (or major location)** the owner holds.
- **Money is only granted when they arrive.** A raided convoy is lost income; the AI has a
  reason to escort, the player has a reason to interdict.

## Reuse to read first

`Economy/CommanderEconomyService.cs` `PayIncome` (replace the per-tick payout for mines with
per-delivery), `Supply/CommanderSupplyHeliService.cs` and `Supply/CommanderSamSiteSupply.cs`
(the mod already spawns and routes supply vehicles), `Units/CommanderMoveService.cs` route API,
the game's `RearmVehicleAI` / `HLT Munitions Truck` definitions (`CommanderGameAccess`).

## Open questions for the design session

- Do bases and control points keep paying per tick while only mines switch to trucks, or does
  everything ride on trucks?
- Truck value: one truck = one income interval's worth, or scaled by mine level?
- Escorts: does the AI's garrison/platoon brain assign one, or is a truck's safety purely the
  player's problem?
- What happens to the payout when the destination base falls mid-route?
