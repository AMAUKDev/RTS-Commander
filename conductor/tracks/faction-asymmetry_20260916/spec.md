# Faction roster asymmetry

Date: 2026-09-16. Builds directly on `conductor/designs/2026-09-16-faction-asymmetry-study.md`.
Read the study first; its investigation is not repeated here.

## Goal

Both sides — the human player and every computer commander alike — field only their own faction's
vehicles and their own faction's aircraft. One predicate answers "may this faction field this
vehicle", one answers "may this faction fly this aircraft", and every buy and launch path in the
mod reads them.

## User decisions (2026-09-16, both explicit)

1. The restriction applies to the human player as well as the computer commanders. The depot
   window offers only the player's own faction's vehicles. The user accepted losing access to
   vehicles they can buy today.
2. Split A **and** Split B together, in one track. Not Split C: the two transports stay shared,
   because splitting them costs one side the ability to air-land any vehicle that can take ground.

## Problem, with evidence

- The player's depot window lists every vehicle in the game (`Depot/CommanderSpawnService.cs:633`
  calls the whole-game scan `CommanderGameAccess.TryGetLocalVehicleDefinitions`), and the setting
  that would narrow it defaults to off (`Core/CommanderSettings.cs:24`).
- The computer commander re-implements the convoy walk inline
  (`Ai/CommanderEnemyCommanderService.cs:1219 CollectCatalog`) rather than reading the shared
  collector.
- Air-landed vehicles come off the aeroplane's cargo mounts, never the faction
  (`Supply/CommanderSupplyHeliMission.cs TryGetVehicleCargo`). The running log proves the result:
  the `Insertion cargo roster` line is character-for-character identical for Boscali and Primeva.
- The aircraft catalogue is the whole encyclopedia plus a resource scan
  (`AirCommand/CommanderAirCommandMissions.cs:21`), so both sides' air rosters are identical.
  `FactionHQ.restrictedAircraft` is referenced nowhere in the repository.

**New finding, not in the study.** The air-landed cargo manifest and the convoy groups have a
*zero* intersection. The cargo manifest is AFV6 APC/AA/AT/IFV, LCV25 AT/AA, Hexhound SAM/GMG,
HLT Radar Truck and M12 Jackknife; not one of those appears in either faction's convoy groups.
A predicate that asked convoy-group membership alone would therefore leave BOTH sides unable to
air-land anything, breaking flown-in forward bases and air-mobile platoons outright. The predicate
must carry a per-side air-landed extension, and it does.

## Existing code being reused (Reuse rules)

- `CommanderGameAccess.CollectFactionVehicleDefinitions` — the convoy-group walk. Kept; its raw
  body is renamed `CollectConvoyGroupVehicles` and the public collector becomes convoy groups plus
  the shared allowance, so one function still answers "what may this faction buy".
- `CommanderEnemyCommanderService.CollectCatalog` — its inline convoy walk is replaced by a call to
  the shared collector (Reuse rule 4: one definition, two callers).
- The faction-identity expression `$"{factionTag} {factionName} {factionExtendedName}"` with
  "PALA"/"BDF" substring tests exists twice already, at
  `SamSites/Construction/CommanderSamSiteService.cs:177` and `:339`. Reuse rule 5 (generalise the
  second instance): it moves into the new roster class as `SideOf`, and both call sites are
  retrofitted behaviour-neutrally.
- `PassesRoleCapability` (`Ai/CommanderEnemyCommanderAirLoadouts.cs:257`) is the single gate every
  computer air decision already passes through — the buy, the fund ceiling, the role-candidate
  test, the rotary launch distance and the loadout choice. The air predicate goes in there, one
  line, rather than at each of its callers.
- `CommanderSettings.LimitToFactoryVehicles` — the existing narrowing setting. Its default flips
  from off to on and its meaning widens from "factory vehicles" to "this faction's roster".

**New class, and why nothing existing fits.** `Core/CommanderFactionRoster.cs`. There is no
per-faction gate anywhere in the mod today: the ground collector answers only convoy-group
membership and has no air side, and nothing reads a faction for aircraft at all. It is a static
table-and-predicate class, not a service, because it has no tick, no state and no session.

## Requirements

1. ONE ground predicate, `CommanderFactionRoster.MayFieldVehicle(hq, definition, route)`, read by
   every buy path: the player's depot window, the computer's ground catalogue, the air-landed
   insertion cargo chooser, forward-base construction vehicles and the platoon and picket recipes.
   The route parameter (Reuse rule 4: parameterise, never fork) distinguishes a vehicle bought at a
   depot from one that arrives as aircraft cargo.
2. ONE air predicate, `CommanderFactionRoster.MayFlyAircraft(hq, definition)`, read by the player's
   AIR window and by the computer's role-capability gate.
3. The repair vehicle (M12 Jackknife) is available to BOTH sides by both routes. No faction's
   convoy groups contain one, so without this repairs and surface-to-air site construction break.
4. The four unavoidably shared airframes — SAH-46 Chicane, EW-25 Medusa, UH-90 Ibis, VL-49
   Tarantula — stay shared, with the reason in a comment.
5. Every table is a class-level constant with a `<summary>` saying what it holds and why.
6. A named `Expect` self-check per safety property, registered at plugin load.

## Acceptance criteria

Each of these is a named self-check case:

- Every capability axis is covered on both sides: capture, hold, anti-armour, anti-air, suppress
  radar, ground attack, cargo lift, scout.
- Neither side has zero capture-capable vehicles.
- Neither side is unable to clear an air-defence belt (each has at least one anti-radiation
  airframe it can afford).
- Neither side is unable to lift cargo.
- Both sides can air-land a capture-capable vehicle and an air-defence vehicle.
- The repair vehicle is fielded by both sides.
- No airframe is on both sides' exclusive lists, and every shared airframe is on neither.
- An unknown or renamed faction is never restricted.

## Files in scope

New: `Core/CommanderFactionRoster.cs`.
Edited: `Core/CommanderGameAccess.cs`, `Core/CommanderSettings.cs`, `Core/CommanderPlugin.cs`,
`Depot/CommanderSpawnService.cs`, `Ai/CommanderEnemyCommanderService.cs`,
`Ai/CommanderEnemyCommanderAirLoadouts.cs`, `Ai/CommanderEnemyCommanderAirRoster.cs`,
`AirCommand/CommanderAirCommandMissions.cs`, `Supply/CommanderSupplyHeliMission.cs`,
`SamSites/Construction/CommanderSamSiteService.cs`, `UI/CommanderOverlayUiSettings.cs`,
`CHANGELOG.md`, `conductor/decision-log.md`.

## Out of scope

- **Ships.** No per-faction naval data exists anywhere in the game; the study says so and the
  study is right. Naval buying is untouched and stays shared.
- **The engine's own aircraft restriction list** (`FactionHQ.restrictedAircraft`). Whether it is an
  allow-list or a deny-list cannot be settled from the mod's side without decompiling the game, and
  writing it wrongly mid-match would stop the player flying at all. The study already records that
  the mod's usual route into the sky spawns aircraft already airborne and bypasses the engine check
  entirely, so the restriction is enforced at the mod's own buy and launch decisions, which is what
  the brief asks for. **Known gap:** a human player can still walk into a hangar and take the other
  side's fighter through the game's own selection screen. Closing that needs the engine list, and
  needs its semantics established first.
- The surface-to-air site builder's own PALA/BDF role table. It is already per-faction and correct;
  only its faction-identity test is retrofitted to the shared helper.
