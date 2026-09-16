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

   **Revised 2026-09-14 (user decision): the strict hold applies to the BASELINE ONLY.** The hold
   fires while fewer than `HomeCapBaseline` (2) fighters are alive; everything the formula wants
   above the baseline — the tracked-aircraft term and the loss term, up to `HomeCapMax` — is
   ordinary CAP-type demand inside rung 2, served through the wing's existing CAP/CAS turn
   alternation (`PrefersCasThisBuy`), bought out of the same pot by the same home-CAP buyer.

   Why: the hold fired on the *grown* total, and the grown total carries a loss term that only
   climbs. The 2026-09-14 match logged
   `CAP 2/4 (2 base +2 air +6 losses, capped at 4), home CAP short 2 — nothing below it is bought`
   on every review for many minutes, with a balance between 3 and 44 and not one CAS airframe,
   helicopter or truck bought in all that time. A commander that has lost six fighters is exactly
   the commander that must still be allowed to buy ground.

   The `ladder:` line reports both halves: `CAP 2/4 (strict 2/2, +2 wanted; 2 base +2 air
   +6 losses, capped at 4)`. The `holds:` line now reads
   `home CAP below its baseline: 1/2 alive (the formula wants 4, the rest is rung 2's)`.
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
- Shortfall = wanted − owned CAP fighters alive (airborne or parked on deck). Rung 1 itself buys
  only toward the **baseline**: `strictShort = max(0, HomeCapBaseline − alive)`, up to
  `MaxAirBuysPerReview` while affordable. While `alive < HomeCapBaseline` the review ends after
  rung 1 — the `holds:` line says `home CAP below its baseline: n/2 alive`.
- The rest — `HomeCapExtraWanted(wanted, alive, baseline)` — is rung 2's. The operations demand
  walk opens the CAP side of the alternation for it (`HomeCapAirDemand`), the air fund is allowed
  to save for the fighter it wants (`ReadOpenAirDemandRoles`), and when the CAP side wins its turn
  with no sortie asking, `BuyAirframeForTurn` routes the buy to `BuyHomeCapFighter` — the same
  buyer, the same airframe tier rule, the same home patrol.
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

## Addendum 2026-09-14 — rung 4 built nothing in a whole match

**The report.** "AI commander also doesn't build any defences at captured bases."

**What the log showed.** In the `Ground Control Duel Far` match, all 100 `ladder:` lines — 50 from
the player's commander and 50 from the enemy's — read `buildings 0`. Rung 4 appeared in the draw
order constantly (`draw buildings>platoons>pickets`), so it had demand; it never completed a
structure. The enemy side sat on a balance of 500 to 730 the whole time, so money was not the
reason on that side.

**Cause 1 — the defence target was faction-wide (fixed).** `GetEnemyBuildReserve` asked
`CountBuildings(hq, BuildingType.DEF) < EnemyDefenceBuildingTarget` (3), and `CountBuildings`
counted every emplacement the faction owned anywhere, mission-authored ones included. The
emplacements around a starting airfield already exceed three, so a commander was over target from
the first second of the mission and no base it captured was ever hardened. Radar was already
per-base through `TryGetUncoveredBase`; defence was not.

**Decision (2026-09-14).** Base defences are counted PER BASE: every airbase the commander holds
wants `EnemyDefenceBuildingTarget` defensive structures within `BaseBuildingCoverageMeters`
(1500 m), and the build is sited beside the base that is short rather than wherever the shared dart
lands — the same shape `TryBuildEnemyRadar` has always had.

- `TryGetUncoveredBase` was generalised into `TryGetBaseShortOfBuildings(hq, type, coverage, target)`
  and now calls it with `target: 1` (Reuse rule 5 — the defence target was the second per-base
  count, so the first was generalised rather than forked). `HasBuildingNear` became the `> 0` case
  of a new `CountBuildingsNear`. The faction-wide `CountBuildings` is deleted: it has no honest
  caller left and keeping it invites the bug back.
- The per-base coverage ring reuses `CommanderFobBuilder`'s `BaseBuildingCoverageMeters` rather than
  declaring a second constant with the same value and the same justification (Reuse rule 4).
- Self-checks: `BuildingsStillWantedAtBase` returns the whole target for an undefended base, zero at
  target, and never a negative; and every base coverage ring is wider than the
  `BaseSiteMaxMeters` siting ring, because a ring inside it would never count the building just
  placed and the commander would build at the same base for ever.
- Log: `built a 12.7mm MG Emplacement at a base short of its 3 defences.`

**Cause 2 — not yet identified, and now instrumented.** With the defence target fixed, rung 4 has a
wish again, but the 2026-09-14 log cannot say which of three things stopped it before: no wish at
all, a bank short of the price, or a site search that kept failing. All three print `buildings 0`.
`ReportStructureWish` now says which, on an eight-review cadence and only while the rung is buying
nothing:

- `saves for its next structure at 250: the rung has banked 60 of it (this review's allocation 31,
  9 quiet reviews).`
- `saves for its next structure at 500: the rung has the 500 but the balance is 333 (…).`
- `saves for its next structure at 25: the bank and the balance both cover it, so the site search is
  what failed (…).`
- `builds nothing: its structure list is finished — every base has radar, the mine and factory
  targets are met and no upgrade is wanted (…).`

The next match's log decides whether anything further is needed. **Open question deliberately left
open:** whether `DuelSpendFraction` (0.45) is the binding constraint on a big-income side. It cannot
be answered from the 2026-09-14 log because no line in it carries income — which is what
`ReportIncome` now fixes (`income 435/min (points 405 from 4 bases, …; mines 30), balance 220.`).
No income rate and no tempo fraction has been changed without that evidence.

## Addendum 2026-09-14 (2) — "existing forces first" inside rung 2

**User decision, 2026-09-14.** Rung 2 (platoons and their air) spends in this order:

1. Air support for platoons and points **currently in contact** — strike, escort, platoon patrol.
2. Replacements and reinforcements for platoons the commander **already has**, plus forward-base
   munitions trucks.
3. Air cover for platoons **on the march** (pre-emptive, inside the four-march cap of Section 12 of
   the smarter-air-wing design).
4. **New** platoons and **new** pickets, out of whatever is left.

### The money: a third reserved while the front is calling

`Rung2Split(allocation, inContactShort)` → `(airReserve, groundShare)`. While ANY sortie over
something actually fighting has an unfilled slot, `Rung2AirReserveShare` (one third) of the rung's
allocation is the wing's before the ground buyer sees any of it; otherwise the reserve is zero and
the review behaves exactly as it did. The reserve is added to the wing's ask and banked through the
existing `AccrueFund` into the existing air savings, still capped at the dearest wanted airframe, so
it accumulates across reviews until that airframe is affordable. Whatever the ceiling refuses flows
straight back to the ground rather than evaporating.

- "In contact" is `HasInContactAirShortfall(hq)`: a sortie with `InContact` set and a strike or
  escort slot unfilled. **Pre-emptive cover deliberately does not count** — a march that has met
  nothing is tier 3, and reserving against it would hand the wing a third of every review for ever,
  which is the opposite of "existing forces first".
- The wing's ordinary 40 % share is still taken, but now out of the ground's two thirds, so a
  contact review gives the wing more than a quiet one without changing the quiet one at all.
- `ladder:` line adds `rung2: air 33% reserved (contact),` when the reserve is active, so a review
  in which the ground bought little because the front was calling for air reads differently from one
  in which it simply had no money.
- Self-checks: a third exactly, the two shares summing back to the allocation, zero reserve when
  nothing is short, and neither share ever negative on a negative or empty allocation.

### The ground order: verified, and it did NOT already do this

`OrderOpenRoles` put the truck first, then the roles a **threatened purpose** was waiting on, then
everything else. That is a front-versus-rear split, not an existing-versus-new one, and it got the
new ordering wrong in both directions: a brand-new platoon being raised at a threatened base
outbid a replacement for a platoon standing under strength elsewhere, and a reinforcement for a
quiet rear platoon sat in the same bucket as sixteen empty pickets' opening orders. Worse, a forward
base summed BOTH into one line per role — the shortfall of the platoons it has and
`unformed × recipe` for the platoons it is still waiting to form — so the two could not be told
apart at all.

**Change.** `CommanderRequisition` gains `ForNewForce`, and a mission may now hold one line of each
per role. The posters split accordingly:

| Poster | Existing force | New force |
|---|---|---|
| Forward base | each assigned platoon's empty recipe slots; the truck | `unformed × recipe` |
| Attack | topping its assigned platoons back up to `platoonSize` | platoons above what it has been given |
| Picket | a detachment that has lost a vehicle | a detachment with nothing on the ground |
| Reserve/withdrawing | every platoon's empty recipe slots | the standing `ReservePlatoons` block |

`OrderOpenRoles` then walks four buckets: truck, existing-threatened, existing-quiet,
new-threatened, new-quiet. The 2026-09-14 threatened-before-quiet fix is preserved INSIDE each half
rather than above it. `PayOff` spends this review's purchases down the same order, so five buys in
one review do not all chase the top line.

- Self-check `CheckExistingBeforeNew`: a one-vehicle replacement outranks a sixteen-vehicle
  new-picket line; a new platoon for a threatened purpose still waits behind a replacement for a
  quiet one; threatened still leads quiet inside the existing half; a role open in both halves is
  listed once; the truck leads the book from whichever half its line sits in.

### The radar airframe's reserved slice (user decision 2026-09-14)

The turn-order fix of the same day (smarter-air-wing design Section 15) stopped the radar airframe
holding the fighter turn, at the stated cost that on a front with fighter demand open every review
it would never be bought at all. This is the counterweight, so it still arrives.

`AirSplit(airAllocation, wantsAwacs)` → `(awacsSlice, turnShare)`. `AwacsSliceShare` (one quarter)
of the AIR SIDE's own per-review allocation goes into `CommanderState.AwacsSavings` while
`CommanderOperationsService.WantsAwacsPurchase(hq)` holds; the other three quarters go to `AirFund`
and the fighter/strike turn order built the same day.

- **"Wants one" is one read of the existing sortie**, not a new opinion: the AWACS sortie exists, is
  past its loss cooldown (`CooldownUntil`, the cooldown every sortie carries) and still has an
  unfilled slot — and its `Wanted` is `AwacsWanted(CountOwnedRadarAirframes(...))`, so owning one
  closes the slot. That is all three of the decision's conditions in one place (Reuse rule 4).
- **The savings are capped at the price of the thing** — `AffordablePrice(CheapestLaunchableValue(hq,
  state, AirRole.Awacs))` — so they stop the moment they can pay for one, and a commander whose
  strips launch no radar airframe banks nothing at all rather than banking toward `float.MaxValue`.
- **Bought through the ordinary path, not a second one.** The buy that serves the AWACS turn is
  given `AirFund + AwacsSavings` as its budget and the affordability flag the turn order reads is
  computed against that same sum, so the savings make the AWACS turn winnable rather than opening a
  parallel purchase. The charge then comes out of `AwacsSavings` first and `AirFund` only for the
  remainder (`LastAirBuyWasAwacs`). There is no second binding path and so no way to buy two.
- **The slice is released, not stranded.** Once one is owned the slice is zero and whatever is banked
  flows into the turn share the same review — the rule `AirFund`'s own ceiling overflow already
  follows.
- **The buy loop now continues while EITHER pot has money**, or a fund emptied by a strike airframe
  would end the review with the radar airframe's slice sitting full.
- `ladder:` line adds `awacs saved 96/145,` while it is saving, and nothing once one is owned.
- Self-checks: a quarter exactly, the two shares summing back to the allocation, zero slice once one
  is owned, neither share negative on a negative allocation, and the savings target being the price
  when one is launchable and zero when none is.
