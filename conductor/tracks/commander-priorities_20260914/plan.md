# Plan: Commander priority ladder

**Track**: commander-priorities_20260914 · **Design**: design.md (approved 2026-09-14, DECISION-009)
**Expected agent runs**: 1 executor (this session) + 1 review + evaluation, per Track shape.
**Build after every task**: `NUCLEAR_OPTION_DIR="I:\SteamLibrary\steamapps\common\Nuclear Option" dotnet build GroundControlRts.csproj -c Release` — must end `0 Warning(s)` / `0 Error(s)`. No install scripts, no commits.

**ARH classification (decompiled from `Assembly-CSharp.dll`, task brief §4)**

- VERIFIED: the game has a dedicated seeker class `ARHSeeker : MissileSeeker`; `Missile.Awake` reads its
  seeker via `gameObject.GetComponent<MissileSeeker>()`, so the seeker component lives on the missile's
  own prefab object. `WeaponInfo` exposes `missile` (bool), `effectiveness.antiAir` (0–1) and
  `weaponPrefab` (GameObject). The mod's own `IsAradWeapon` (`AirCommand/CommanderAirCommandScoring.cs:293`)
  already classifies ARM missiles exactly this way: `info.weaponPrefab?.GetComponentInChildren<ARMSeeker>(true) != null`.
- Therefore the member that identifies an ARH air-to-air missile:
  `info.missile && info.effectiveness.antiAir > 0.05f && info.weaponPrefab.GetComponentInChildren<ARHSeeker>(true) != null`
  — the 0.05 threshold is the one the AIR window's loadout label already uses for its `A/A` tag
  (`AirCommand/CommanderAirCommandLoadout.cs:718`).
- VERIFIED: `AircraftParameters.StandardLoadouts` is a public `StandardLoadout[]`; `StandardLoadout` has
  `disabled`, `loadout.weapons` (List of `WeaponMount`) and `AllowedByHQ(weaponManager, hq)` — so the ARH
  loadout for a fighter is found by walking those, no reflection.
- ASSUMED (mitigated): that the ARH seeker is reachable from `WeaponInfo.weaponPrefab` via
  `GetComponentInChildren` exactly as `ARMSeeker` is. Mitigation is in the design itself: if no roster
  fighter has an ARH option the rule logs once and any fighter qualifies.

**In-game verification (design.md Verification, adjusted to the implemented log lines)**

Host, Ground Control Duel, both commanders on. Log lines that prove each rung:

1. Within the first two reviews, per side: two `launched a <fighter> (Fighter) from <base> for <n>
   (home CAP).` lines and a `ladder: CAP 2/2 (2 base +0 air +0 losses), …` line.
2. Fly your own aircraft within 30 km of the enemy's airbases: `ladder: CAP 2/3 (2 base +1 air +0
   losses), …` and a third home-CAP launch.
3. Shoot a home-CAP fighter down with an aircraft: `ladder: … +1 losses …`, a replacement launch, and
   `holds: home CAP short 1 …` while it is missing.
4. Kill two CAP fighters at once: `holds: home CAP short 2 …` and no `bought <vehicle>` line until they
   are replaced.
5. With CAP full, across ten reviews: every rung name (`platoons`, `pickets`, `buildings`) appears first
   in `draw` at least once; a `ladder:` line shows non-zero `buildings` after the savings bank reaches
   the next structure's price; `pickets` is non-zero after an insertion flight is charged.
6. Plugin load: no `Enemy … self-check FAILED` lines (the new `CheckLadder`/`CheckAirBuyRules` cases pass).

- [x] T1 **Settings.** `Core/CommanderSettings.cs`: new `Commander` section — `HomeCapBaseline` (2),
  `HomeCapPerEnemyAircraft` (2, one more fighter per this many), `LadderPlatoonWeight` (60),
  `LadderPicketWeight` (20), `LadderBuildingWeight` (20), `LadderRungFloorPercent` (10), each with a
  rationale comment, Get/Set pair, and an `Initialize` warm-up touch. Build.
- [x] T2 **ARH weapon-class test.** `AirCommand/CommanderAirCommandScoring.cs`, beside `IsAradWeapon`:
  `internal static bool IsArhAirToAirMissile(WeaponInfo info)` per the classification above. Self-check:
  classifier cases (seeker+missile+antiAir ⇒ true; no seeker ⇒ false; antiAir 0 with seeker ⇒ false)
  added to `CheckAirBuyRules` (`Ai/CommanderEnemyCommanderAir.cs:529`), synthetic `WeaponInfo` + a
  `GameObject` carrying `ARHSeeker`, destroyed after. Departure fix wired in the same task:
  `CheckAirBuyRules` exists but is called by nobody — wire `CheckAirBuyRules();` into
  `CommanderEnemyCommanderService.SelfCheck()`. Build.
- [x] T3 **Ladder partial + pure rules.** New `Ai/CommanderEnemyCommanderLadder.cs` (the review is the
  single spend site; a partial keeps the driver out of the service file): constants
  `HomeCapThreatRadiusMeters` (30000) with `<summary>`; pure `WantedHomeCap(baseline, perAircraft,
  tracked, losses)`, `LadderFloor(remainder, floorPercent)`, `LadderRungBudget(pool, floor,
  demandingAfter)`, `LadderDrawPick(weights[], t)` (weighted index, −1 none), `LadderHoldsForCap(short,
  ceilingBlocked)`; `CheckLadder()` wired into `SelfCheck()` beside `CheckDefencePosture`: CAP formula at
  0/1/2/3/4 tracked × 0/1/2 losses, weights normalise (all ≥ 0, sum > 0, as locals), floor arithmetic
  never above the remainder or below zero, a rung with no floor owed gets the whole pool,
  strict-CAP-holds table incl. the ceiling carve-out, config guards. Build.
- [x] T4 **Home-CAP bookkeeping (operations).** `OperationsState`: `HomeCapAirframes` set,
  `HomeCapEnemyAirNear` map, `CapLossTimes` list, `InsertionAllowance`, `InsertionSpentSinceLadder`.
  `Operations/CommanderOperationsAir.cs`: `CapLossRadiusMeters` (15000) + `CapLossMemoryMinutes` (10)
  consts with summaries; `MaintainHomeCap(hq)` — dead members stamp `CapLossTimes` when their last
  observed `enemy air within 15 km` was true, then drop; live members' proximity refreshed through
  `CountHostileAirInRing` (widened `internal`, one-word-widening comment); `CountHomeCapFighters`,
  `HomeCapLosses` (10-minute window). `CommanderPendingAirLaunch.ForHomeCap` flag +
  `RecordCommanderLaunch` parameter; the claim parks a home-CAP launch on the home CAP (owned, never
  sortie-bound); `TakeUnboundOwned` skips home-CAP members (never lent); `ReleaseToPlayer` drops them
  with the airframe. Enemy defence review (10 s clock) calls `MaintainHomeCap` per commanded HQ — the
  loss test reads the position while it is still fresh. Build.
- [x] T5 **Rung-1 buy.** `Ai/CommanderEnemyCommanderAir.cs`: extract the launch-and-charge block of
  `TryBuyRole` into one `LaunchBoughtAirframe(...)` used by both callers (Reuse rule 3/4);
  `BuyHomeCapFighter(hq, state, budget)` — Fighter role, `CanAiFly`, airbase-accepted, ARH rule with the
  memoised per-HQ `ArhLoadouts` (`CommanderState`) and the logged-once any-fighter fallback, loadout still
  `WithoutInternalCannons`, LAST RESORT gate kept, launch through `RecordCommanderLaunch(forHomeCap: true)`;
  `CountTrackedEnemyAircraftNearBases(hq)` — the `IsUnderThreat` walk (freshness `ThreatMemorySeconds`,
  `Aircraft` filter) keyed to `HomeCapThreatRadiusMeters` around the commander's own airbases through
  `IsNearOwnBase` (widened `internal`). Build.
- [x] T6 **One pot + strict rung 1.** `Ai/CommanderEnemyCommanderService.cs` `Review`/`ReviewPurchases`:
  `spendable = factionFunds × tempo fraction` — the reserve hold-back and `UnitSpendFloorShare` are
  deleted with a comment saying they were the previous fix for the starvation the ladder now prevents
  structurally; the `catalog.Count == 0` early return goes (a commander with no ground list still flies
  CAP). Rung 1 runs first: shortfall = `WantedHomeCap` − live home-CAP fighters; buys up to
  `MaxAirBuysPerReview` while affordable and under the airborne ceiling; while short (and not
  ceiling-blocked — departure 1) the review ends and `ReportHold` says `holds: home CAP short N …`.
  Air buys below rung 1 stop accruing: `AirFund`, `AccrueFund`'s air call, `AirframeBudgetShare`,
  `AirFundCeiling`, `DearestFighterPrice` deleted (comment at the buy loop); the two `AirFundCeiling`
  cases in `CheckAirSupport` deleted; the "air fund is short" denial string becomes "air budget is short"
  (stable string, reason key resets once); the `fund {AirFund}` debug fragment becomes the rung budget.
  Build.
- [x] T7 **Rung 4 — structures from their allocation.** `Units/CommanderRepairService.cs`: extract
  `FindWorstDamagedBuilding(hq)` behind `TrySendEnemyRepairCrew` and a new `WantsEnemyRepairCrew(hq)`
  (one definition, two callers). `Economy/CommanderEconomyServiceEnemy.cs`: `NextEnemyStructureCost(hq)`
  — the next rung-4 want's price in the existing order (build via `GetEnemyBuildReserve`, then dock,
  mine, factory upgrades); `SpendEnemyStructures(hq, allocation)` — repair crews ahead as today (funds
  gate unchanged), then the build/upgrade ladder gated on per-HQ `structureSavings` + allocation (both
  savings ≥ price AND funds ≥ price; the `EnemyReserveMultiple` pot-protection comment updated — the
  ladder bounds the spend now), one purchase per review as today.
  `Economy/CommanderEconomyService.cs`: `TickPersistent` stops calling `ReviewEnemies` (the enemy loop
  no longer spends on its own clock); `ResetSession` clears `structureSavings`; `GetEnemyBuildReserve`
  doc updated — still THE definition of "what to build next", no longer a hold-back.
  `UI/CommanderAiLogUi.cs`: the `RESERVE TARGET` readout becomes `NEXT BUILD` (same value, true label).
  Build.
- [x] T8 **Rungs 2–4 — weighted draw + ladder line.** `Ai/CommanderEnemyCommanderLadder.cs`: demand flags
  (platoons: book/capture/recon/plan-cap/air-demand/naval; pickets: `HasInsertionDemand`; buildings:
  `NextEnemyStructureCost` or repair wanted), floor per demanding rung, weighted permutation via
  `LadderDrawPick` (`UnityEngine.Random` — two commanders draw differently), budget walk
  `LadderRungBudget`. Rung 2 `SpendPlatoons` = the existing purchase loop refactored to spend from its
  budget: air buys first (demand order, direct spend), then naval (`ReviewNaval` takes its share of the
  rung budget; `NavalFund` + `AccrueFund` survive as the rung-2-internal ship accumulator), then order
  book → capture/recon → plan buyer. Rung 3 grants its budget as the insertion allowance. Rung 4 banks
  `min(budget, price − savings)` into savings via `SpendEnemyStructures`. One `ladder:` `CommanderAiLog.Note`
  line per review in the design's format (CAP a/w with the formula split, draw order, spent per rung —
  pickets read from the consumption accumulator — saved), floors/budgets detail behind
  `OperationsDebugLog`. Build.
- [x] T9 **Rung 3 — insertion allowance plumbing.** `Operations/CommanderOperationsInsertion.cs`: the
  per-point qualifying walk extracted into `TryGetInsertionCandidate` (one definition, two callers);
  `HasInsertionDemand(hq)` for the draw; `PlanInsertions` requests only when the allowance covers the
  flight — zero allowance reports once per point. `Supply/CommanderSupplyHeliMission.cs`:
  `TryLaunchInsertionAircraft` takes the allowance (cargo budget `min(funds, allowance) − hull`), a
  distinct decline when the allowance was the binding limit, and `out` the charged total;
  `RequestInsertion` deducts the allowance and adds to the spend accumulator. Build.
- [x] T10 **Changelog + metadata + departures.** CHANGELOG entry under Unreleased in the file's style;
  `metadata.json` loop_state EXECUTE/IN_PROGRESS until in-game verification; departures below kept
  current. Build.

## Departures recorded during execution

1. **The strict CAP hold lifts while the airborne ceiling blocks the buy** (T3/T6): the ceiling counts
   every faction aircraft, the player's included, so a strict hold under it would let the player park
   20 aircraft of their own and starve the enemy commander's ENTIRE economy by never letting the CAP
   fill. The hold now applies while the CAP is short and buyable; a ceiling-blocked CAP reads as
   satisfied for the ladder and the `ladder:` line still shows the true count.
2. **`CheckAirBuyRules` was dead code** — written for the 2026-09-13 air track, never wired into
   `SelfCheck()`. Wired now (T2) with the ARH classifier cases; its existing `ChooseAirRole` /
   LAST RESORT cases all still hold.
3. **Rung 3's allowance is not earmarked out of the review pool at grant time** (T8/T9): the design's own
   example line (`pickets 0`, `saved 35`) books a rung's spend when it is charged, not when it is
   granted, and the insertion charges on the operations clock after the ladder line is already printed.
   The allowance still bounds the flight to the rung's share; the next ladder review re-grants whatever
   was not consumed.
4. **Rung 4's spend gates drop the `EnemyReserveMultiple` for builds/upgrades** (T7): the multiple
   existed to protect the unit spender from the economy spender sharing one balance — the ladder is that
   protection now, so the gate is savings ≥ price AND funds ≥ price. The repair-crew gate is untouched
   ("as today").
5. **`IsArhAirToAirMissile` lives in `CommanderAirCommandScoring.cs` beside `IsAradWeapon`**, not in the
   loadout file the design named — the weapon-class tests (`IsAradWeapon`,
   `IsStrategicStrikeWeapon`) already live there, and Reuse rule 4 wants the sibling classification
   together. Same class, same partial.

6. **The insertion partial changed under this track mid-execution** (a parallel effort in the same
   session family added the route-threat launch gate, the loss-streak pause and per-point decline
   dedup keys between this track's read of the file and its edit). Rung 3's plumbing integrates with
   them: the structural gate is extracted as `PointQualifiesForInsertion` shared by the request loop
   and the ladder's `HasInsertionDemand` (the shape the plan sketched as one candidate walk would
   have dropped the threat gate's per-point fall-through); the threat test stays a launch-time gate,
   and the pause counts as no demand; the allowance cap threads through `TryLaunchInsertionAircraft`
   around the new threat logic unchanged.

7. **The quiet-review hold line moved into `ReviewPurchases`** (T6/T8): the strict rung-1 path
   reports its own hold, so a second report from `Review` would have counted the same review twice
   against the `HoldReportEveryReviews` throttle. `Review` now only resets the quiet counter when
   something was bought. The strict path also clears the draw list before its line, or the line
   would have printed a draw from the previous review.

8. **T7 and T8 shipped as one compile unit**: T7's build left structures paused for exactly one
   in-session build (the economy had stopped spending on its own clock, and the ladder call only
   lands with the draw in T8). The two tasks were executed back-to-back so no intermediate build
   was ever a candidate for the running game; the plan's task order is otherwise as written.

## Follow-up (user report, 2026-09-14, "highway strips can launch Compass with Scythes"): air buyer

The first match exposed two faults in the air buy: the candidate list walked `hq.AircraftSupply`
(the faction's issued list) where the AIR window walks the game's catalogue against each held
base's hangars, and CAP candidates were typed by role identity so a Scythe-carrying Compass could
never qualify — one denial line, then silence on a 400+ fund with CAP demand 0/1 all match.

- [x] F1 **Shared list and acceptance.** `AirCommand/CommanderAirCommandMissions.cs`:
  `CollectAircraftDefinitions` extracted from `RefreshOptions` (one definition, two callers);
  `IsCompatibleAirbase` widened internal; the enemy buyer's catalog (`airCatalog`, resolved once
  per mission, cleared on reset) + `FindAcceptingAirbase` = the window's own pair
  (`IsCompatibleAirbase` + `CanSpawnAircraft`). `Air roster` logs the same catalogue. Build.
- [x] F2 **Capability candidates + scored loadouts.** `AirCommand/CommanderAirCommandLoadout.cs`:
  `TryBuildRoleLoadout` over the window's own auto-configure (`AutoConfigureRoleLoadout` gained a
  `preferArhMissiles` preference) — one definition for the capability test and the launch loadout.
  `Ai/CommanderEnemyCommanderAir.cs`: `CapLoadoutScore`/`CasLoadoutScore` memos, `PassesRoleCapability`
  (plane pilot + capability for combat roles; identity + CanAiFly for transports),
  `BuildRoleLoadout` (never the default), `BetterCapCandidate` (Fighter identity first, then
  non-last-resort, then cheapest); `TryBuyRole` and `BuyHomeCapFighter` walk the catalogue;
  `CountRole` counts by capability. `LaunchBoughtAirframe` takes a built `Loadout`. Build.
- [x] F3 **Claim and fill by capability.** `Operations/CommanderOperationsAir.cs`:
  `TryClaimAircraft` binds A/A-capable buys to hungry CAP sorties first, then A/G-capable to CAS;
  `TakeUnboundOwned` matches the same capability; both read `CommanderEnemyCommanderService
  .IsAntiAirCapable`/`IsAntiSurfaceCapable`. Build.
- [x] F4 **Denials + no deadlock.** `ReportAirDenial` re-logs a repeated reason on the holds
  cadence (`AirDenialReviews`); the VTOL parenthetical dropped, reasons say the capability
  (`RoleCapabilityLabel`). `Ai/CommanderEnemyCommanderLadder.cs`: `HasAnyCapCandidate` valve;
  `LadderHoldsForCap(capShort, unfillable)` covers both causes; `ReviewHomeCap` marks `Impossible`,
  `ReviewPurchases` skips the rung with the periodic `holds: home CAP impossible — no
  air-to-air-capable airframe can launch from <bases>` line and the lower rungs proceed; the
  `ladder:` line says so. Self-checks: `BetterCapCandidate` order table, the unfillable hold table.
  Build.
- [x] F5 **Record.** Design decision 6 (dated 2026-09-14) with the verified Vagrant finding —
  `TryTaskAiAircraft` requires a plane pilot, so anything the player flies under a manual Air
  Command mission passes every gate the buyer applies; the prefab pilot list is asset data, now
  printed by the `Air roster` line for every candidate. CHANGELOG entry. Build.

Departure: F2 replaced this track's T5 ARH hard gate with the preference the follow-up asks for —
the `ArhLoadouts` standard-loadout memo, its roster-level hold and its logged fallback are gone;
the window's picker scorer (with the ARH preference inside it) is the one definition of both the
capability test and the launch loadout.
