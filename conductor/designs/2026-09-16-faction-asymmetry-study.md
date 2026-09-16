# Faction asymmetry study

Date: 2026-09-16. Read-only study, no code changed.

Goal from the user: "introducing unit asymmetry into the mission. Ideally we want minimum shared
units while still maintaining capability parity." The two sides should field as few of the same
vehicle and aircraft types as possible, while each still being able to capture, hold ground, kill
armour, kill aircraft, suppress radar, strike ground, lift cargo and scout.

## 1. The headline: the ground war is already asymmetric, and the mod is undoing it

The game defines a faction's ground roster as the list of vehicles inside that faction's convoy
groups. Boscali and Primeva ship almost completely different lists. The mod reads those lists and
prints them at mission start, and the log from the live match is unambiguous.

| Capability | Boscali (BDF) | Primeva (PALA) |
|---|---|---|
| Main battle tank | Type-12 MBT (11) | Spearhead MBT (15) |
| Infantry fighting vehicle | AFV8 IFV (6) | Linebreaker IFV (8) |
| Armoured personnel carrier | AFV8 APC (5) | Linebreaker APC (7) |
| Short-range surface-to-air | AFV8 Mobile Air Defense (8) | Linebreaker SAM (10) |
| Anti-aircraft gun | FGA-57 Anvil (9) | AeroSentry SPAAG (8) |
| Long-range surface-to-air | T9K41 Boltstrike (13) | T9K41 Boltstrike (13) |
| Munitions resupply truck | HLT Munitions Truck (2) | MSV Munitions (2) |

Numbers in brackets are the game's own worth rating, which the mod uses as the price. Source:
`Ground roster` lines in `BepInEx\LogOutput.log`, produced by the roster probe in
`Ai\CommanderCaptureService.cs:381`.

Seven types a side, and exactly one is shared: the long-range surface-to-air launcher. Prices track
each other closely, and Primeva is uniformly a little dearer. That is a designed asymmetry, and it
is already close to what the user asked for.

The team lead's briefing said both sides drive Spearhead MBT, AFV6, LCV25, Hexhound SAM,
AeroSentry SPAAG, Linebreaker APC and M12 Jackknife. That observation is correct about what happens
in play, but it is not the game doing it. It is the mod. Two separate mod behaviours ignore the
convoy groups entirely:

- **The player's depot window lists every vehicle in the game.** The list comes from a scan of all
  loaded vehicle assets in `Core\CommanderGameAccess.cs:718`, and the setting that would narrow it
  to the faction's own kit defaults to off (`Core\CommanderSettings.cs:24`).
- **Air-landed vehicles come off the aeroplane, not the faction.** When a transport flies a vehicle
  to a forward position, the choice is whatever that aircraft prefab carries as cargo
  (`Supply\CommanderSupplyHeliMission.cs:1121`). The log proves the result: the insertion cargo
  roster line is character-for-character identical for Boscali and Primeva. Both sides air-land
  AFV6, LCV25, Hexhound SAM, Hexhound GMG, M12 Jackknife and HLT Radar Truck, none of which is in
  either faction's convoy groups.

A third path is worse still. The surface-to-air site builder scans every unit asset in the process
and picks by a hardcoded name table that recognises only the strings "PALA" and "BDF"
(`SamSites\Construction\CommanderSamSiteService.cs:280` and `:338`).

So the first and cheapest win available is not new asymmetry. It is stopping the mod from erasing
the asymmetry the game already ships.

## 2. The air war is fully shared, and the game offers nothing to split it with

The game has no per-faction aircraft roster at all. What a base can launch is baked into the hangar
building prefab as a private list (`Hangar.cs:349`), so every hangar of a given type offers the same
aeroplanes to whoever holds the base. Liveries carry a faction field; the airframes do not.

The mod then widens even that. Its aircraft catalogue is the whole encyclopedia plus a scan of every
loaded aircraft asset (`AirCommand\CommanderAirCommandMissions.cs:21`), and both the player's air
window and the computer commander's buying run off it. The logged air roster is therefore identical
for both sides: thirteen usable airframes plus one joke entry.

| Airframe | Price | What it is for |
|---|---|---|
| CI-22 Cricket | 12 | Last resort, bought only when nothing else fits |
| T/A-30 Compass | 22 | Last resort (trainer) |
| VT-7 Vagrant | 29 | Last resort (trainer) |
| UH-90 Ibis | 30 | Helicopter transport, carries cargo |
| SAH-46 Chicane | 31 | Attack helicopter, the only rotary ground-attack type |
| A-19 Brawler | 36 | Dedicated ground attack |
| FS-12 Revoker | 65 | Fighter, carries anti-radar missiles |
| FS-20 Vortex | 90 | Fighter, carries anti-radar missiles |
| VL-49 Tarantula | 118 | Tilt-wing transport, two cargo slots |
| KR-67 Ifrit | 126 | Fighter, carries anti-radar missiles |
| EW-25 Medusa | 145 | The only radar-and-jamming aircraft |
| SFB-81 Darkreach | 225 | Heavy ground attack, no air-to-air ability at all |
| Alkyon AB-4 | 390 | Bomber, rated well enough to also fly air defence |

Three of those cannot be split because the game ships only one of them. The Medusa is the only
aircraft that carries the radar pod the mod's early-warning role requires. The Chicane is the only
airframe with both a helicopter and an aeroplane pilot, which is what the mod's helicopter
close-support role tests for. And there are only two transports.

## 3. What "capability" means, in the mod's own words

The mod already classifies units, and any split has to be judged against that vocabulary rather
than a new one.

For aircraft, the roles are `Strike`, `Fighter`, `Transport`, `RotaryCas`, `Awacs` and `Arad`
(`Ai\CommanderEnemyCommanderAir.cs:147`). Classification is data-driven, not a name list: a type
with cargo capacity and no aeroplane pilot is a transport, otherwise the higher of its anti-air and
anti-surface ratings decides. Whether an airframe can actually fill a role is then tested by asking
the game's own weapon-fitting code to build a loadout and checking it scores above zero. Anti-radar
capability is detected by looking for an anti-radiation seeker component on the missile, so it
cannot go stale.

On top of the roles sits a quality ladder, `AirframeTier`, running Fighter, Multirole, Strike,
LastResort, Excluded. The commander buys up the ladder as threat rises, which means a side with
only one airframe in a role has no ladder to climb.

For ground vehicles the roles are `Armour`, `Carrier`, `AirDefence`, `Truck` and `Other`
(`Operations\CommanderPlatoon.cs:13`), assigned from the game's vehicle type plus capture strength,
with one narrow name test for uncrewed vehicles. Capturing ground is decided by the game's own
capture strength field being above zero (`Ai\CommanderCaptureService.cs:318`) — no name list.

Against those axes, here is where each side stands today.

| Axis | Boscali covers it with | Primeva covers it with |
|---|---|---|
| Take and hold ground | AFV8 APC and IFV, and in fact every ground type | Linebreaker APC and IFV, likewise |
| Kill armour | Type-12 MBT, AFV8 IFV | Spearhead MBT, Linebreaker IFV |
| Kill aircraft from the ground | AFV8 Mobile Air Defense, FGA-57 Anvil, T9K41 Boltstrike | Linebreaker SAM, AeroSentry SPAAG, T9K41 Boltstrike |
| Rearm units in the field | HLT Munitions Truck | MSV Munitions |
| Repair and build | nothing in the convoy groups | nothing in the convoy groups |
| Air superiority | shared: Revoker, Vortex, Ifrit, Alkyon AB-4 | same four |
| Strike ground from the air | shared: Brawler, Darkreach, Alkyon, plus every fighter | same |
| Helicopter close support | shared: Chicane only | Chicane only |
| Suppress radar | shared: Revoker, Vortex, Ifrit, Medusa, Alkyon | same five |
| Early warning and jamming | shared: Medusa only | Medusa only |
| Lift cargo | shared: Ibis, Tarantula | Ibis, Tarantula |

Two things stand out. Every ground vehicle on both sides can capture, including the munitions
truck, so capture is never at risk from a ground split. And neither faction's convoy groups contain
a repair vehicle, so the mod's repair-vehicle lookup returns nothing for both sides today and the
M12 Jackknife it uses is coming from the leaks described above.

## 4. Can a mod do this, and where

Yes, but the ground and the air need different seams, and neither is a single point.

**Ground has one good data source and five bypasses.** The faction's convoy groups are the right
answer, and `CommanderGameAccess.CollectFactionVehicleDefinitions` reads them. It covers the
player's factory choices, the computer's factory choice and the repair-vehicle lookup. It does not
cover: the computer commander's entire ground army, which re-implements the same convoy walk
inline at `Ai\CommanderEnemyCommanderService.cs:1223`; the player's depot window; the air-landed
cargo; the surface-to-air site builder; or ships, which have no faction concept anywhere. The fix is
a single predicate along the lines of "may this faction field this unit", applied at each of those
points. Nothing needs to edit the game's own faction assets, which keeps the change reversible and
keeps a save file clean.

**Air has one catalogue and it is the wrong list.** The engine does have a per-faction aircraft
restriction: `FactionHQ.restrictedAircraft`, a list of asset keys filled from the mission file and
synchronised to clients. The game enforces it in two places — the player's aircraft selection screen
and the server-side spawn check. It is writable at runtime through the property
`NetworkrestrictedAircraft`, and it must be written by assigning a new list rather than adding to
the existing one, or clients will not see the change.

Two caveats matter. The mod consults `restrictedAircraft` nowhere at all — zero references in the
whole repository — so setting it would not change a single mod-driven purchase. And the mod's main
route to getting an aircraft into the sky skips the engine check entirely: when no clear hangar is
available it spawns the aircraft already airborne over the base
(`AirCommand\CommanderAirCommandMissions.cs:359` into `:501`), calling the spawner directly.

So the cleanest seam for air is: set the engine's restriction list so the human player's aircraft
selection screen shows only their own side's types, and separately teach the mod's own catalogue
and its two launch doors to respect the same list. Setting the engine list alone changes what the
player sees but not what the commander buys. Teaching the mod alone leaves the player able to walk
into a hangar and take the other side's fighter.

What neither seam reaches: air-landed cargo, because it comes from the aircraft prefab's mounts, and
ships, which have no per-faction data at all.

## 5. Three candidate splits

**Split A — repair the leaks, split nothing new.** Make both sides field only their convoy groups,
give Primeva a substitute for the shared long-range launcher, and leave the air shared. Result: one
or zero shared ground types, thirteen shared airframes. Every axis stays covered on both sides
because nothing is taken away. The air tier ladder, the diversity cap, escort and suppression rules
are all untouched. This is a bug fix wearing a feature's clothes, and it is by some distance the
cheapest and safest.

**Split B — ground finished, air split where doubles exist.**

| Axis | Boscali | Primeva |
|---|---|---|
| Light vehicle family (air-landed) | AFV6 | LCV25 |
| Long-range surface-to-air | T9K41 Boltstrike | Hexhound SAM |
| Fighter | FS-12 Revoker, Alkyon AB-4 | FS-20 Vortex, KR-67 Ifrit |
| Dedicated ground attack | A-19 Brawler | SFB-81 Darkreach |
| Last resort | CI-22 Cricket, T/A-30 Compass | VT-7 Vagrant |
| Suppress radar | Revoker, Alkyon | Vortex, Ifrit |
| Shared, unavoidably | SAH-46 Chicane, EW-25 Medusa, UH-90 Ibis, VL-49 Tarantula | same |

Shared count falls to four airframes and zero or one ground types. Both sides keep a two-step
fighter ladder and an anti-radar carrier. The price complaint is real: Boscali's fighter ladder runs
65 then 390 while Primeva's runs 90 then 126, and Boscali's dedicated ground-attack aircraft costs
36 against Primeva's 225. Primeva would need a cheaper strike option, most simply by leaving the
Brawler shared, which raises the shared count to five.

**Split C — Split B, plus the transports split one each.** Boscali takes the UH-90 Ibis, Primeva
takes the VL-49 Tarantula. This is the most aggressive option and it is the one I would not ship.
The two transports carry different cargo, and the mod's air-mobile buying only ever asks for the
carrier and air-defence roles (`Operations\CommanderOperationsRequisitions.cs:997`). The Tarantula
carries carrier-role vehicles; the Ibis, by the logged manifest, carries a surface-to-air launcher,
a gun truck and the repair vehicle, and no carrier-role vehicle at all. Boscali would keep its
transport and lose the ability to air-land anything that can take ground.

## 6. Risks, and which split avoids each

- **A side with no cargo aircraft cannot build a flown-in forward base.** Only Splits B and C touch
  transports; B keeps both shared and is safe. C breaks this for whichever side loses the Tarantula.
- **A side with no radar-suppression weapon cannot clear an air-defence belt, which the belt-hold
  rule now waits for.** Five airframes carry anti-radar missiles, so any split that gives each side
  at least one fighter keeps this. All three splits do.
- **A side with no capture-capable vehicle cannot take ground.** Every ground vehicle on both sides
  already has capture strength, so no split reaches this. Safe in all three.
- **A side with one airframe in a role has no quality ladder and trips the diversity cap.** The mod
  buys up a tier ladder and caps how much of the sky one type may be. Splits B and C leave each side
  two fighters, which is the minimum that works. Only one dedicated ground-attack type a side, but
  every fighter is also anti-surface capable, so the strike role stays populated.
- **Neither faction has a repair vehicle in its convoy groups.** Fixing the leaks without adding one
  would remove the M12 Jackknife from both sides and break repairs and surface-to-air site
  construction. This bites Split A hardest, because A is otherwise pure gain. Whichever split is
  chosen, the repair vehicle has to be added to both convoy groups or explicitly kept shared.
- **Ships have no faction data anywhere.** Two independent whole-game ship lists exist. No split
  reaches naval, and it should be stated as out of scope rather than quietly left broken.
- **The surface-to-air site builder recognises only the strings "PALA" and "BDF".** Any third
  faction, or a renamed one, silently gets the best-scoring parts in the whole game.

## 7. Recommendation

Do Split A first, as its own track, and treat it as a correctness fix rather than a feature. The
game already gives us a near-total ground split and the mod is throwing it away; recovering it costs
one predicate and a handful of call sites, changes nothing about balance that the game's own
designers did not already choose, and makes the two sides look and feel different immediately. The
repair vehicle must be added to both convoy groups in the same change, or repairs break.

Then judge Split B on how Split A actually plays. B is achievable and the numbers mostly work, but
it is a balance exercise, not a plumbing one: it needs a cheaper ground-attack aircraft for Primeva
and it will want retuning of the tier ladder for two-airframe roles.

Do not do Split C. Splitting the transports costs one side the ability to air-land a
capture-capable vehicle, and the fix for that is changing an aircraft prefab's cargo mounts, which
is a different and much larger job.

**The single open question for the user.** Should the human player be restricted to their own
faction's roster, or only the computer commanders? Restricting the player is what makes the
asymmetry felt, and the engine already supports it for aircraft through the mission restriction
list. But it also means the depot window stops offering most of the vehicles it offers today, and
the player loses options they currently have. Everything else in this study follows from that
answer.
