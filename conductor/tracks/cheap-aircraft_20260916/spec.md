# Cheap aircraft earn their keep

**Goal.** Put the bottom-tier airframes — the T/A-30 Compass (22), the VT-7 Vagrant (29) — into the
sky in numbers, two ways: they take the jobs that are genuinely easy, and they pad a sortie the
commander cannot afford to fly at full strength. User instruction, 2026-09-16, explicit and
approved: "i also want to see increased use of the cheap aircraft", choosing **both** offered
options and accepting that more aircraft will fly and more will be lost.

## The problem, with evidence

The tier table puts trainers and the last resort in one bottom tier
(`Ai/CommanderEnemyCommanderAir.cs:401-404`), and the tier walk takes the HIGHEST launchable tier in
the role's own order (`HighestLaunchableTier`, `Ai/CommanderEnemyCommanderAir.cs:476-490`). The
bottom tier is last in both orders (`CapTierOrder` / `CasTierOrder`,
`Ai/CommanderEnemyCommanderAir.cs:265-274`), so a commander whose strips launch anything else never
shops there. The roster line calls it `LAST RESORT, bought only when nothing else can fill the role`
and that is what the buy does.

The numbers say the bottom tier is cheap, not useless. Measured from the game's own roster lines:
the Compass is rated air-to-ground 0.64 against the FS-12 Revoker's 0.46 at a third of the price,
and air-to-air 0.62 against 1.00. Against a picket with no air defence and no hostile aircraft
overhead, the difference between a 22 and a 65 is money, not outcome.

Second problem: an element the allocation cannot cover WHOLE buys nothing at all
(`PackageElementBuys`, `Ai/CommanderEnemyCommanderAirChecks.cs:391-412`, and the denial at
`Ai/CommanderEnemyCommanderAirBuy.cs:621-634`). A sortie wanting two strike airframes at 65 with a
100 allocation flies with none, while 22 would have bought a second airframe for the slot that went
empty.

## The existing code being reused (Reuse rules 1-2)

- **`EscortTier`** (`Ai/CommanderEnemyCommanderAir.cs:894-903`) is already "an element may override
  the tier the ordinary order chose". The easy-job override is the SECOND instance of that shape, so
  Reuse rule 5 applies: extract `PreferTier` and retrofit `EscortTier` onto it behaviour-neutrally.
- **`PackageElementBuys`** already computes "how many the allocation covers" and throws it away when
  it is short of the whole element. The padding rule needs exactly that number, so it is extracted
  as `AffordableAirframes` and both read it.
- **`SelectInTier`** already carries the two-pass diversity handling and the last-resort ranking;
  the padding pick reuses the candidate list it walks rather than re-collecting one.
- **`ReadOpenAirDemandRoles`** (`Operations/CommanderOperationsAirRadarWatch.cs:1006`) already
  answers "is a suppression sortie waiting for its aeroplane". No second opinion is written.
- **`AttritionBleeding`** (`Ai/CommanderEnemyCommanderAttrition.cs:206`) is the escalation signal both
  new rules stand aside for.
- **`SortieElementWanted`** (`Operations/CommanderOperationsAirWing.cs:518`) is UNTOUCHED: no sortie
  asks for more aircraft than it did yesterday. Both new rules only change what fills slots the
  sizing envelope had already opened.
- No new service, no new Harmony patch, no new scheduler, no new UI window.

## Requirements

**R1 — one pure predicate for "easy".** Built only from what the mod already measures for the
sortie: hostile aircraft tracked in the objective's ring (`CommanderAirSortie.LastHostileAir`),
hostile ground units observed over it (`LastObserved`), air-defence vehicles observed near it
(`LastAirDefence`), and whether the objective is in contact (`InContact`). Distance from the enemy
is deliberately NOT read: the only distance the mod records per sortie, `EnemyDistanceMeters`, is
set for pre-emptive platoon sorties and left at -1 for every other kind, so it is not an honest
input for a rule every sortie passes through.

**R2 — the line.** Not easy, therefore a proper airframe, whenever ANY of: the objective is in
contact; any air-defence vehicle is observed near it; any hostile aircraft is tracked in its ring;
more than `CheapAirframeMaxObserved` (2) hostile ground units are observed over it; the role is
suppression or radar; the side is bleeding; a suppression sortie is waiting for its aeroplane.

**R3 — padding.** When the element's chosen type covers fewer airframes than the element wants, buy
as many of the proper type as the allocation covers and fill the remaining slots with the cheapest
role-capable candidate the leftover covers. At least one proper airframe must have been bought, or
there is no element to pad.

**R4 — padding displaces nothing.** The proper count is taken first and is the maximum the
allocation covers; padding spends only what is left after it.

**R5 — every existing limit still binds**: the airborne ceiling (read before each airframe in the
launch loop), the per-review budget, the diversity cap and the faction roster split (both inside the
candidate collection the padding pick reuses).

**R6 — bleeding wins.** A side losing more than a third of what it launches buys the best it can
afford; neither new rule fires. Decided here, not left open: the attrition read is a measured fact
about aircraft that have actually died in the last ten minutes, where "easy" is an estimate from
what the commander can currently SEE, and a side that is bleeding is precisely the side whose
picture is wrong. It also closes the feedback loop — more cheap aircraft raise losses, losses raise
the bleeding flag, and the flag switches the cheap buying off until the ledger recovers.

**R7 — the suppression budget is untouchable.** No cheap aircraft on either roster carries an
anti-radiation missile, and a sortie now WAITS for a sweep rather than flying a belt, so a cheap buy
that spends the money for the only belt-clearing airframe would stall the sortie behind it. While a
suppression sortie has an unfilled slot, neither new rule fires.

## Acceptance criteria

- Named self-checks at every boundary of the easy predicate, the padding arithmetic and the tier
  override, registered at plugin load.
- `EscortTier` retrofitted onto the shared override with its existing checks still passing.
- Build ends `0 Warning(s)` / `0 Error(s)`.
- Each new gate proved by planting a defect and naming the check that fails, restoring byte-identical.

## Files in scope

`Ai/CommanderEnemyCommanderAir.cs`, `Ai/CommanderEnemyCommanderAirBuy.cs`,
`Ai/CommanderEnemyCommanderAirChecks.cs`, `Core/CommanderSettings.cs`, `CHANGELOG.md`,
`conductor/decision-log.md`.

## Out of scope

The home patrol's own buy (`BuyHomeCapFighter`): it is the side's standing air defence and its
element rule is already "the most air-to-air per credit", which buys cheap fighters when they are
good value without needing to be told the job is easy. The sortie sizing envelope. The airborne
ceiling. The last-resort admissibility rule (`LastResortAllowed`), which stays exactly as it is —
the CI-22 Cricket is still ranked below every ordinary candidate wherever it appears.
