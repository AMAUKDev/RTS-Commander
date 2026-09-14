# Design: Commander priority ladder

**Date**: 2026-09-14 · **Track**: commander-priorities_20260914 · **Approved by user**: 2026-09-14
**Depends on**: platoon-operations, air-support-tasking, heli-picket-insertion (the demands this
ladder arbitrates between).

## Problem

Spending is split across four unconnected pots, each with its own gate, so "priority" is emergent:
the economy service holds back a structure reserve (`Economy/CommanderEconomyServiceEnemy.cs`
`GetEnemyBuildReserve`), the ground spender keeps a quarter-of-balance floor
(`Ai/CommanderEnemyCommanderService.cs` `UnitSpendFloorShare`), the air wing accrues a 40 % fund
(`Ai/CommanderEnemyCommanderAir.cs` `AirframeBudgetShare`, `AccrueFund`), and helicopter insertions
draw on the ground pot (`Operations/CommanderOperationsInsertion.cs`). On a map this size the
platoon order book is never empty, so pickets and buildings are rarely reached, and nothing
guarantees a fighter screen over the commander's own airbase.

## Decisions (user, 2026-09-14)

1. Priority order: (1) home CAP, (2) platoons and their air support, (3) air-delivered pickets,
   (4) buildings.
2. Home CAP is **strict**: while it is short, nothing below is bought. Fighters must carry an
   active-radar-homing (ARH) missile.
3. CAP size = **2 + 1 per 2 tracked enemy aircraft, no upper limit, + 1 per CAP fighter lost to
   enemy air in the last 10 minutes**.
4. Rungs 2–4 get **variability per review, not per commander**: a weighted draw decides which rung
   has first call on the remainder each review, with a floor so pickets and buildings are always
   reached ("the map is huge, so there are nearly always more platoons that could be built").
5. Buildings fold into the ladder as rung 4; the economy service stops spending on its own clock.

6. **Air buys by capability, from the player's own list** (follow-up, same day, after the first
   match: "that's rubbish. highway strips can launch Compass with Scythe missiles, or VT-7
   Vagrants with Scythe missiles. If I make the air missions manually, they spawn and launch" —
   the commander logged "its strips accept no AI-flyable Fighter airframe at all" once, then sat on
   a 400+ fund with CAP demand 0/1 for the whole match). Two root causes, both fixed:
   - The buyer walked `hq.AircraftSupply` — the faction's *issued* list — while the player's AIR
     window lists the encyclopedia (plus loaded resources) gated by each held base's hangar list.
     The Vagrant never even appeared in the commander's `Air roster` line. The buyer now uses the
     window's own list builder and its own airbase acceptance pair (`IsCompatibleAirbase` +
     `CanSpawnAircraft`) — one definition, two callers.
   - CAP (and escort, and CAS) candidates were typed by role *identity* (`roleIdentity.antiAir >
     antiSurface`), so a Compass carrying Scythes could never be a CAP candidate. Candidates are
     now **capability**: an airframe qualifies when the window's own picker can build it a loadout
     that scores for the mission mode (AIR SUPERIORITY for CAP/escort, CAS for strike), and that
     scorer — not the airframe's default loadout — builds the launch loadout, with an
     active-radar-homing missile preferred. Candidate order: Fighter identity first, then any
     other A/A-capable airframe, Cricket LAST RESORT unchanged, cheaper of equals. ARH moves from
     a hard gate (decision 2's first build) to the stated preference.
   - Verified in code: `TryTaskAiAircraft` requires a plane pilot, so any airframe the player flies
     under a manual Air Command mission — the Vagrant, per the report — passes every gate the
     buyer applies (`HasPlanePilot`, and thereby `CanAiFly`). The Vagrant's own prefab pilot list
     is asset data: the `Air roster` line prints it at runtime, and with the catalog fix the
     Vagrant finally appears there, so the next match's log confirms it.
   - A repeated refusal no longer goes silent after its first line ("once per reason" proved to be
     once per match): it re-logs on the holds cadence, and the wording claims no cause it did not
     find (the VTOL parenthesis is gone).
   - **No deadlock**: if no air-to-air-capable airframe can launch from any held base (capability
     + strip tests, last resort included), the strict CAP rung is skipped for that review with a
     periodic `holds: home CAP impossible — no air-to-air-capable airframe can launch from <bases>`
     line, and rungs 2–4 proceed.

## Reuse

- Review cadence and the single spend site: `CommanderEnemyCommanderService.Review` /
  `ReviewPurchases` (tempo fraction `SpendFraction` / `DuelSpendFraction`, `holds:` reporting).
- Home CAP flying: `TaskAirWing` owned-only home CAP, `HomeGuardRadiusMeters`, `AirRole.Fighter`,
  `BuyAirframe` / `TryBuyRole` (last-resort pass, launch expectation, ownership claim).
- Enemy air tracking: the `trackingDatabase` walk in `CountObserved` /
  `CommanderEnemyCommanderDefence.ThreatMemorySeconds`, filtered to `Aircraft`.
- Loadout options: `AirCommand/CommanderAirCommandLoadout.cs` (`BuildWeaponOptions`,
  `GetWeaponTypeKey`, `WithoutInternalCannons`) — add a weapon-class test for ARH missiles there.
- Ground demand: the order book (`OpenRolesByPriority`, `GroundBuyingCapped`); air-support demand
  (`Operations/CommanderOperationsAir.cs` `PlanAirSupport` demand queries); insertion demand
  (`PlanInsertions`); structure wish list (`TryBuildEnemyEconomy` order).
- Not reused: `AccrueFund` / `AirFund` / `NavalFund` as independent pots — replaced by ladder
  allocations (naval keeps its share as part of rung 2's air side, unchanged behaviour otherwise).

## Section 1 — One pot, one ladder

Each review: `spendable = balance × tempo fraction` (existing knob). Rung 1 draws first; the
remainder flows down. `GetEnemyBuildReserve` no longer subtracts from the pot; the structure loop
in `CommanderEconomyServiceEnemy.ReviewEnemy` becomes a rung-4 call that spends only its
allocation (repair crews stay ahead of it inside rung 4, as today).

## Section 2 — Rung 1: home CAP (strict)

- `wantedCap = 2 + floor(trackedEnemyAircraft / 2) + capLossesLast10Min`. Tracked enemy aircraft:
  hostile `Aircraft` in the tracking database seen within `ThreatMemorySeconds`, inside
  `HomeCapThreatRadiusMeters` (30 km) of any of the commander's airbases. CAP losses: owned CAP
  fighters that died while an enemy aircraft was tracked within 15 km of them, remembered for
  `CapLossMemoryMinutes` (10) — "losing aircraft to enemy air" buys more fighters, ground losses do
  not.
- ARH rule: a fighter definition qualifies when one of its loadout options mounts a weapon the
  game classifies as an active-radar-homing air-to-air missile; the buy picks that loadout (still
  `WithoutInternalCannons`). If no roster fighter has an ARH option, the rule logs once and any
  fighter qualifies.
- Shortfall = wanted − owned CAP fighters alive (airborne or parked on deck). Buy up to
  `MaxAirBuysPerReview` while affordable. While shortfall > 0 the review ends after rung 1 — the
  `holds:` line says `home CAP short N`.
- CAP fighters fly the existing home CAP orbit and are never lent to sorties; a sortie's escort is
  bought separately in rung 2.

## Section 3 — Rungs 2–4: weighted draw with a floor

- Weights 60 / 20 / 20 (platoons + their air, pickets, buildings); floor 10 % of the post-CAP
  remainder for every rung with open demand. Draw: a weighted random permutation each review
  (`UnityEngine.Random`, so two commanders draw differently). The first drawn rung spends up to
  its open demand from the remainder minus the other rungs' floors; then the next; unspent money
  stays in the balance.
- Rung 2 internal order: air-support demand (escort, then CAS ladder, then pre-emptive baseline),
  then the order book by `OpenRolesByPriority`, then capture/recon overrides, then the plan buyer
  (capped by `GroundBuyingCapped`).
- Rung 3: insertion requests (already one per review) spend hull + vehicles from the rung's share.
- Rung 4: structures in the existing wish-list order; the rung saves its allocation across
  reviews (`StructureSavings` per HQ) until the next structure is affordable.

## Section 4 — Diagnostics and settings

- One line per review through `CommanderAiLog.Note`:
  `ladder: CAP 3/4 (2 base +1 air +1 losses), draw platoons>buildings>pickets, spent CAP 65 /
  platoons 40 / pickets 0 / buildings 20, saved 35.` Detail behind `OperationsDebugLog`.
- Settings (`Commander` section, Get/Set + warm-up): `HomeCapBaseline` 2, `HomeCapPerEnemyAircraft`
  2 (one more fighter per this many), `LadderPlatoonWeight` 60, `LadderPicketWeight` 20,
  `LadderBuildingWeight` 20, `LadderRungFloorPercent` 10. Constants with `<summary>`:
  `HomeCapThreatRadiusMeters` 30000, `CapLossMemoryMinutes` 10, `CapLossRadiusMeters` 15000.
- Self-checks (pure): CAP formula at 0/1/2/3/4 tracked aircraft and 0/1/2 losses; weights
  normalise; floor arithmetic never allocates more than the remainder or below zero; a rung with
  no demand gets no floor; strict CAP ends the review while short.

## Out of scope

Per-commander personalities; naval buying changes beyond keeping today's share inside rung 2;
player-side UI for the ladder; changing what any rung buys (only when and how much).

## Verification

Self-checks above at plugin load. In game (host, Ground Control Duel, both commanders on): within
the first two reviews the log shows `launched a <fighter> (Fighter)` twice per side and the
`ladder:` line with `CAP 2/2`; fly your own aircraft near the enemy base → `CAP 2/3` and a third
launch; shoot a CAP fighter down with an aircraft → `+1 losses` and a replacement; with CAP full,
across ten reviews every rung appears first in the draw at least once and buildings and insertion
requests appear in `spent`; with CAP short (kill two fighters), `holds: home CAP short 2` and no
ground purchase until they are replaced.
