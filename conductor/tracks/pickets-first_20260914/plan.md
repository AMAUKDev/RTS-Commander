# Plan: Pickets first — pickets capture, platoons only toward the enemy

**Track**: pickets-first_20260914 · **Design**: design.md (approved 2026-09-14), decision-log
DECISION-013
**Expected agent runs**: 1 executor (this session) + 1 review + evaluation, per Track shape.
**Build after every task**: PowerShell `$env:NUCLEAR_OPTION_DIR = "I:\SteamLibrary\steamapps\common\Nuclear Option"; dotnet build GroundControlRts.csproj -c Release`
— must end `0 Warning(s)` / `0 Error(s)`; the last build of the track on `-t:Rebuild`. No install
scripts, no `build-dev.bat`, no commits, no check files.

**Concurrency**: an airframe-tiers agent holds `Ai/CommanderEnemyCommanderAir.cs`,
`Operations/CommanderOperationsAir.cs` and `AirCommand/*`; a smarter-air-wing agent may also touch
`Operations/CommanderOperationsAir.cs` and `CommanderOperationsAirMarkers.cs`. This track's files are
`Operations/CommanderOperationsFront.cs`, `…Requisitions.cs`, `…Insertion.cs`, `…Service.cs`,
`…Diagnostics.cs`, the single `ReviewPurchases` ground-cap branch of
`Ai/CommanderEnemyCommanderService.cs`, plus the two shared files `Core/CommanderSettings.cs` and
`CHANGELOG.md`. The shared files and `Ai/CommanderEnemyCommanderService.cs` are re-read immediately
before each edit, take additive/minimal edits only, and are followed straight away by a build. A
build that fails only inside the air agents' files is retried after two minutes rather than fixed
here.

## Architecture

No new service, no new Harmony patch, no new file. Every change is inside the operations service's
existing partials and the one buyer branch that reads them.

The doctrine is three pure rules plus one ordering change:

- **`PicketsNeedThePool(shortPicketMissions)`** replaces `PlatoonsNeedThePool` and points the other
  way: the pool is withheld from *platoon formation* while a picket is short, instead of being
  withheld from pickets while a platoon is short. The pipeline order already puts `PlanPickets` →
  `FillPickets` ahead of `AssignPlatoons` → `TryFormPlatoon` in `Review()`
  (`Operations/CommanderOperationsService.cs:269` vs `:287`), so no call has to move — only the
  guard flips.
- **`PlatoonPurposeCount(forwardBasePlatoonsWanted, attackPlatoonsWanted, reservePlatoons)`** is the
  new ceiling on formation: a platoon exists for a front-point forward base, an attack, or the
  standing reserve, and for nothing else.
- **`GroundBuyingBookOnly(ownsGroundForce, hasOpenRequisition)`** replaces `GroundBuyingCapped`,
  dropping the platoon count and the cap from the rule entirely.
- **`PicketRequisitionRoles`** turns a picket's shortfall into order-book lines so the buyer can fill
  a picket when the pool is empty.

Every rule carrying a number stays a pure static over ints and bools so `SelfCheck()` drives it at
plugin load, the only automated test this mod has. `MaxPlatoonsFormedPerReview` (6) stays as the
runaway-loop guard; the purpose count is what actually bounds the force now.

`PlanPickets` already plans a picket for **every** non-front control point that is held or reachable
and has no other mission (`Operations/CommanderOperationsFront.cs:1177-1194`) — design Section 1's
"every control point that is not a front point" needs no code change there, and T2 records the check
rather than inventing work. What changed under it is who gets the pool first.

## Tasks

- [x] T1 **Setting defaults (design Sections 2 and 4).** `Core/CommanderSettings.cs` (re-read first,
  minimal edit, build straight after): `OperationsReservePlatoons` default 2 → 1 and
  `OperationsHeliInsertionLimit` default 1 → 3, each comment rewritten to say why the new number is
  the number (one standing reserve because platoons now form only for a purpose; three flights
  because a commander lifting one hilltop at a time never garrisons a mountain map). Leave
  `OperationsMaxPlatoons` alone — T10 deletes it, after the buyer stops reading it.
  Verification: build clean; the in-game settings window shows the new defaults on a fresh config.

- [x] T2 **Pool order flips to pickets first (design Section 1).**
  `Operations/CommanderOperationsFront.cs`: delete `PlatoonsNeedThePool` and add pure
  `PicketsNeedThePool(int shortPicketMissions)` — true while any picket mission on a non-front point
  is short of `PointsMinGarrison`. `FillPickets` stops consulting the old guard and always fills from
  the pool; it returns (or records on the state) the count of picket missions still short after the
  fill, which is what the formation gate in T4 reads. Record in the task notes that `PlanPickets`
  already covers every non-front held-or-reachable point, so Section 1's coverage clause needs no
  edit. SelfCheck in `CheckForwardBaseShare`: the three `PlatoonsNeedThePool` cases are replaced by
  "one short picket withholds the pool from platoon formation" (true), "no short picket releases the
  pool" (false), "a negative or zero count never withholds" (false).
  Verification: build clean, self-check passes at load.

- [x] T3 **Picket shortfalls reach the order book (design Section 1).**
  `Operations/CommanderOperationsRequisitions.cs`: new `PostPicketRequisitions(state, mission)`
  called from `PostRequisitions`' mission loop for `CommanderMissionKind.Picket`, posting the
  shortfall (`PointsMinGarrison` less `PicketMembers.Count`) through pure
  `PicketRequisitionRoles(int short, out int airDefence, out int carrier)` — the first vehicle
  `AirDefence`, every further one `Carrier`, the order-book form of the insertion load's own doctrine
  (`PickInsertionCargo`: one air-defence vehicle plus the cheapest other). `SetRequisition` with a
  zero want clears the line when the picket fills, exactly as the truck line does. SelfCheck in
  `CheckOrderBook`: a picket short of two wants one air-defence and one carrier; short of one wants
  one air-defence and no carrier; short of none wants nothing.
  Verification: build clean, self-check passes; in game the review line's `book:` field carries
  `ad=`/`carrier=` counts while pickets are empty.

- [x] T4 **A platoon forms only for a purpose (design Section 2).**
  `Operations/CommanderOperationsFront.cs`: pure
  `PlatoonPurposeCount(int forwardBasePlatoonsWanted, int attackPlatoonsWanted, int reservePlatoons)`
  = the sum, floored at zero; a live counter walks `state.Missions` summing `WantedPlatoons` over
  `ForwardBase` missions whose point is still front and over `Attack` missions, then adds
  `OperationsReservePlatoons`. The formation loop at the end of `AssignPlatoons` runs only while
  `PlatoonPurposeCount(state) > state.Platoons.Count` **and** `PicketsNeedThePool` is false.
  SelfCheck in `CheckForwardBaseShare`: two forward bases plus one attack plus one reserve wants
  four; nothing wanted anywhere still wants the reserve; a purpose count at or below the platoon
  count forms nothing; the count never goes negative.
  Verification: build clean, self-check passes.

- [x] T5 **The formation log says what the platoon is for (design Section 2).**
  `Operations/CommanderOperationsService.cs` `TryFormPlatoon` takes a purpose string and logs
  `forms 3RD PLATOON for ForwardBase CROSSROADS 13` / `for Attack Maris Airport` / `as the reserve`
  in place of today's bare `forms {name}: n/6 vehicles.` (the vehicle count stays on the line). The
  caller in `AssignPlatoons` resolves the purpose from the first unmet purpose in the same order the
  count sums them: an unfilled front-point forward base, then an unfilled attack, then the reserve.
  Verification: build clean; in game the COMMANDER LOG carries a purpose on every `forms` line.

- [x] T6 **"No purpose" says so once (design Section 2).**
  `Operations/CommanderOperationsFront.cs`: when the purpose gate blocks formation and the pool is
  non-empty, log `no purpose for a new platoon; N vehicles wait as picket stock` — once per change,
  via a flag on `OperationsState` holding the last logged pool count and gate state, the
  `ReportInsertionDenial` de-duplication convention. The line clears (and may log again) as soon as
  a purpose appears.
  Verification: build clean; in game the line appears once while the front is quiet, not every 30 s.

- [x] T7 **Dissolving a platoon to the pool, one definition (Reuse rule 5).**
  `Operations/CommanderOperationsFront.cs`: extract the member-return loop inside
  `DemoteForwardBaseToPicket` into `DissolveToPool(OperationsState state, CommanderPlatoon platoon)`
  — members to `state.Pool`, `Issued` cleared, mission released through `ReleaseFromMission`, the
  platoon removed from `state.Platoons` — and call it from `DemoteForwardBaseToPicket`.
  Behaviour-neutral; T8 is the second caller.
  Verification: build clean; the demote path still logs and behaves as before.

- [x] T8 **A purpose-resolved platoon becomes picket stock (design Section 2).**
  `Operations/CommanderOperationsFront.cs`: in the reserve step of `AssignPlatoons`, only
  `OperationsReservePlatoons` unassigned platoons attach to the reserve mission; every further
  unassigned platoon goes through `DissolveToPool` and logs
  `{name} has no purpose left; its vehicles become picket stock.` Ordering is unchanged — the
  forward-base and attack matching loops have already had their pick, so only genuinely purposeless
  platoons reach this step.
  **Departure from the design's wording** (recorded below): the dissolve is immediate rather than a
  `Withdrawing` march to the rally point, because `AssignPlatoons`' own re-entry branch flips a
  full-strength `Withdrawing` platoon straight back to `Forming`
  (`Operations/CommanderOperationsFront.cs:838-849`), so a surplus platoon sent to withdraw would
  never survive to be folded. `DissolveToPool` is the existing fold path's own body.
  Verification: build clean; in game a forward base that stops being front drops its platoon and the
  review line's `pool=` rises by six.

- [x] T9 **Order-book-only ground buying, the rule (design Section 3).**
  `Operations/CommanderOperationsRequisitions.cs`: `GroundBuyingCapped` becomes
  `GroundBuyingBookOnly(bool ownsGroundForce, bool hasOpenRequisition)` = `ownsGroundForce &&
  !hasOpenRequisition` — once the operations service owns the force, the only reason to buy a ground
  vehicle is a line in the book. The `<summary>` records what the old rule got wrong: the cap lifted
  on any open requisition and the book never emptied, so it never bound and the player side reached
  27 platoons. SelfCheck in `CheckOrderBook` replaces the five cap cases: "an owned force with an
  empty book buys nothing" (true), "an owned force with an open line buys" (false), "a force this
  service does not own still runs the plan buyer" (false, both book states).
  Verification: build clean, self-check passes.

- [x] T10 **The buyer stops buying on plan (design Section 3).**
  `Ai/CommanderEnemyCommanderService.cs`, the `ReviewPurchases` ground-cap branch only (re-read
  immediately before the edit, minimal diff, build straight after): `groundCapped` becomes
  `bookOnly` from `GroundBuyingBookOnly(OwnsGroundForce(hq), hasOpenBook)`; the hold/resume log
  becomes `holds ground purchases: every vehicle is bought to order and the book is empty.` /
  `resumes ground purchases: the order book has an open line.`; `CommanderState.GroundCapped` is
  renamed `GroundBookOnly` with its comment. Inside `SpendPlatoons` the early return stays, and the
  capture / recon / plan fallback after an unfilled book line runs only when
  `!CommanderOperationsService.OwnsGroundForce(hq)` — a commanded force buys to order, an
  undiscovered one keeps the stock behaviour. Then delete `OperationsMaxPlatoons` (property and
  warm-up touch) from `Core/CommanderSettings.cs`; the `Operations/MaxPlatoons` cfg key is left
  orphaned in existing config files on purpose and the CHANGELOG says so.
  Verification: build clean (this task is where the deleted setting must compile away); in game no
  `bought … for <plan>` line appears without a matching open line once a commander owns its force.

- [x] T11 **Several insertions at once (design Section 4).**
  `Operations/CommanderOperationsInsertion.cs`: `PlanInsertions` issues up to
  `InsertionRequestsPerReview` (new class constant, 3, with a `<summary>` saying why three) requests
  per review instead of returning after the first, one per point, **farthest-off-road first** — the
  qualifying points are gathered into a scratch list with their road distances and sorted descending
  before any request is made, rather than taken in ranked-value order. `inFlight` is re-read after
  each successful request so the airborne limit still binds inside one review. The existing route
  threat gate, per-point cooldown, loss-streak pause and `InsertionAllowance` share are untouched and
  still tested per point; an empty allowance still stops the review's remaining requests.
  SelfCheck in `CheckInsertion`: the per-review cap is at least one and no larger than the airborne
  limit's default; `QualifiesForInsertion` still refuses at `inFlight == limit` and allows at
  `limit - 1` with the new default of 3.
  Verification: build clean, self-check passes; in game two or three `requesting air insertion` lines
  appear in the same review while hilltops are empty.

- [x] T12 **The review line shows the flights (design Section 4).**
  `Operations/CommanderOperationsDiagnostics.cs`: the `heli=` field becomes `heli=n/3` — in flight
  over `OperationsHeliInsertionLimit`. Re-read the file as it stands when this task runs and keep the
  field order the air tracks left it in.
  Verification: build clean; the `Ops … review:` line reads `heli=2/3`.

- [x] T13 **Reinforcement cap 3 → 6 (design Section 5).**
  `Operations/CommanderOperationsService.cs`: `MaxReinforcementPlatoons` 3 → 6, its `<summary>`
  rewritten for the new number (a six-platoon answer is a counter-attack in its own right, and with
  platoons no longer spread over the whole map there are platoons to send). The existing
  `CheckReinforcements` cases are updated: an enormous deficit still caps at
  `MaxReinforcementPlatoons`, and a new case pins the boundary — a deficit of exactly six platoons
  asks for six, seven still asks for six.
  Verification: build clean, self-check passes; in game a request may read `requests 5 platoon(s)`.

- [x] T14 **CHANGELOG entry.** `CHANGELOG.md` (re-read first, additive only, build straight after):
  one player-facing section under `## Unreleased` in the file's existing voice, covering pickets
  taking the pool first and capturing every point away from the front, platoons forming only for a
  forward base, an attack or the one reserve, ground purchases going to order only, several picket
  flights at once, and the larger reinforcement answer. It names the orphaned `Operations/MaxPlatoons`
  config key as no longer read and safe to delete by hand. Match the file's own line endings.
  Verification: build clean.

- [x] T15 **Final rebuild and self-check sweep.** `dotnet build GroundControlRts.csproj -c Release
  -t:Rebuild` ending `0 Warning(s)` / `0 Error(s)`, and a read-through of every `SelfCheck` case
  touched by T2, T3, T4, T9, T11 and T13 to confirm each new rule has a case that would fail if the
  constant were retuned into nonsense (Testing rule 1). Report the exact build lines and the log
  lines that prove each design section.

## Departures

1. **T8 dissolves immediately instead of withdrawing to a rally point.** The design says a
   purpose-resolved platoon "dissolves back to the pool (existing fold path)". The `Withdrawing` →
   `FoldWithdrawnPlatoons` route cannot carry a full-strength platoon: `AssignPlatoons` flips any
   `Withdrawing` platoon that is not under-strength back to `Forming` at the top of the next review,
   before it can reach its rally point. The fold path's own body is reused verbatim through
   `DissolveToPool` (T7), so the vehicles land in the pool the same way, one review sooner.
2. **T3 reads "cheapest other" as `Carrier`.** The order book addresses roles, not prices, so a
   picket's second vehicle is requisitioned as a carrier — the cheapest non-air-defence combat role
   in the recipe, and the only one that can take a point, which is a picket's whole job. The buyer's
   `ChooseForRole` still picks the cheapest definition inside that role, so the bought vehicle is the
   cheapest carrier on the catalog.
3. **`PlanPickets`' coverage is already Section 1's coverage.** No edit is made for "pickets on every
   non-front point"; T2 records the verification instead of writing a change that would be a no-op.
4. **`OperationsMaxPlatoons`' config key is left orphaned** rather than migrated away, per the design
   and the brief; the CHANGELOG says the key is no longer read.

5. **Forward-base demand no longer depends on the platoon count** (fix, 2026-09-14, team lead brief).
   Section 2's purpose count was correct in itself but could never grow: `MaxForwardBases` was
   `max(1, platoons x FobShare)`, so one platoon allowed one forward base, one forward base was one
   purpose, and one purpose never justified a second platoon. The allowance is now demand-led —
   `MinForwardBases` (2, constant with its rationale) on the best-ranked front points plus one per
   front point the enemy is actually at — and `OperationsFobShare` survives only as an assignment
   limiter that can raise the allowance for a platoon-rich commander, never cut it below the demand.
   The setting and its slider are untouched. The demotion walk was also reordered to run in RANKED
   order rather than mission-creation order, so the bases that keep their platoons are the best
   points rather than the oldest missions.

6. **`PicketsNeedThePool` now yields to a purpose facing the enemy.** DECISION-013's pickets-first
   doctrine is kept exactly as written for a quiet map, and amended for contact: the pool order is
   (a) forward bases on threatened or in-contact front points and attacks, (b) pickets on
   front-adjacent points ranked by value, (c) quiet rear pickets, (d) the standing reserve. Three new
   pure rules carry it — `PicketsNeedThePool(short, openThreatened)`, `PlatoonsAheadOfPickets` and
   `PoolHeldForThreatenedPurposes` — each with self-check cases. The reserve is deliberately kept
   behind the pickets: `PlatoonsAheadOfPickets` lets a review form only as many platoons as the
   threatened purposes ask for while any picket is short.

7. **The order book is bought in two halves.** `OpenRolesByPriority` sorted by line size alone, so
   sixteen rear pickets' air-defence and carrier lines outbid the armour a forward base in contact
   was asking for. It now orders truck, then the roles a threatened purpose is waiting on, then
   everything else; `OrderOpenRoles` is the pure rule and `CheckBuyOrder` its self-check. One
   definition of "facing the enemy" (`IsThreatenedFrontPoint`) is shared by the pool order, the
   forward-base allowance and the book.

8. **The air fund's ceiling is a price, not a multiple of the grant.** `budget x AirBudgetShare x
   FundSaveReviews` had no relationship to any airframe, which is how the 2026-09-14 match banked
   `air saved 372` while the ground order book sat six lines deep. The ceiling is now the price of
   the dearest airframe an open air demand actually wants (CAP/escort, CAS, rotary CAS, AWACS,
   ARAD), floored at one dearest fighter, read off the same catalog, role gate and accepting-strip
   pair the buy itself uses. Anything already above it flows back into the same review's pot before
   the share is taken. `ReadOpenAirDemandRoles` is the new demand read; the ladder line now prints
   `air saved N (cap M)`.

9. **Review line gains `purposes=<fob>/<attack>/<reserve>`** next to `platoons=`, so a commander that
   cannot afford to grow and one that has no reason to can be told apart from the log alone.

10. **Not verified in the running game.** As with the original execution notes, the new self-check
    cases were desk-checked arithmetically and run at plugin load; no match has been played against
    this build.

## Execution notes (2026-09-14)

- **Every task built `0 Warning(s)` / `0 Error(s)`**; the final check was a full
  `-t:Rebuild`. Two intermediate builds failed only inside the air agents' files
  (`Ai/CommanderEnemyCommanderAir.cs`, then `Operations/CommanderOperationsMarkers.cs` calling into
  `CommanderOperationsAirMarkers.cs`); both cleared on their own and neither was touched here.
- **`PicketsNeedThePool` binds on a narrower set of moments than it first appears to**, and this is
  correct rather than a weakness. `FillPickets` runs before the formation step and drains the pool
  into the pickets, so a picket left short almost always means an empty pool, and the formation loop
  would have stopped anyway. What the guard actually catches is the vehicles that land in the pool
  *during* `AssignPlatoons` — a demoted forward base, a folded remnant, and now a dissolved surplus
  platoon (T8). Those are held as picket stock for the next review instead of being turned straight
  back into the platoon that was just dissolved, which is exactly the loop the doctrine exists to
  break. There is no deadlock: a picket that cannot be filled leaves the pool empty, and a picket
  that can be filled is filled on the next review whether or not a flight is available to it.
- **`CommanderOperationsService.PlatoonCount` has no callers left** — the deleted cap log line was
  its only one. It was kept rather than deleted: it is a harmless generic read, and removing it
  while sibling agents are editing the buyer risks breaking a call that lands after this track.
- **Not verified in the running game.** The self-check cases added here run at plugin load and were
  desk-checked arithmetically, not observed failing. The in-game verification listed in design.md is
  still outstanding.

## Departure 11 — off-road points are reserved for the helicopter (2026-09-14, user instruction)

**What the match showed.** Every picket line in the 2026-09-14 log reads `pickets=2` with `heli=0/3`
and not one `requesting air insertion` line, on a map whose points service reports 674 roads. The
insertion gate is never wrong; it is never *reached*. `QualifiesForInsertion` requires `shortHanded`
(`Operations/CommanderOperationsInsertion.cs`), but two earlier steps of the same review have
already closed the shortfall: `FillPickets` (`Operations/CommanderOperationsFront.cs`) tops every
picket up out of the free pool, and `PostPicketRequisitions`
(`Operations/CommanderOperationsRequisitions.cs`) writes what the pool could not cover onto the
order book so the buyer fills it by road. `PlanInsertions` runs after both and finds every point
full.

**The decision.** A point farther than `OperationsHeliInsertionOffRoadMeters` from the nearest road
is now AIR-DELIVERED: the drive fill skips it and no road-stock requisition is posted for it, so the
shortfall survives to the insertion step that exists to serve it. The reservation is given up — the
point reverts to driving, exactly as before — when the flight is impossible for this commander this
review: no transport airframe with vehicle cargo can launch from a held base, the loss-streak pause
is running, or that point's own loss cooldown is live.

**How it is built.**

- Pure rule `PicketDeliveryMode(offRoad, insertionPossible, paused, cooldownLive)` →
  `Drive`/`Air`, in `Operations/CommanderOperationsInsertion.cs` beside `QualifiesForInsertion`,
  with six self-check cases (each input flipped on its own, plus the all-negative case) and an
  `Expect` overload for the enum next to the existing ones.
- `MarkAirDeliveredPickets` is the live read, called from `PlanPickets` after the review's picket
  missions exist and before `FillPickets`. It fills `OperationsState.AirDeliveredPickets`, which the
  drive fill, the order book and the review line all read through `IsAirDeliveredPicket` — one
  definition, three callers.
- Whether a flight is possible at all is read ONCE per commander per review through the new
  `CommanderSupplyHeliService.HasLaunchableVehicleTransport(hq)`, which is
  `TryLaunchInsertionAircraft`'s own candidate walk (`CollectVehicleMountCandidates`) stopped at the
  first hit, with the price and route tests left out: affordability is the flight's own business
  and moves review to review, while this answers the standing question of whether the roster can do
  it at all.
- A picket on a FRONT point is never reserved. It is a demoted forward base, and
  `QualifiesForInsertion`'s `rear` test would refuse it, so reserving it would leave it empty.
- An air-delivered picket no longer counts toward `ShortPicketMissions`, so it does not withhold the
  free pool from platoon formation through `PicketsNeedThePool`. It is not waiting on the pool; the
  vehicles it is waiting for are bought as cargo at heli spawn.
- The review line tags it: `missions=[Picket HILLTOP 44 0/0 pickets=0 air]`.

**Risk to watch in the next match.** The reservation is released only by the three conditions above.
An off-road point whose flights are refused for a reason that carries no cooldown — a persistent
`hostile air defence tracked along every route`, or `cannot afford the picket's vehicles` — stays
reserved and therefore stays empty for as long as that reason holds, where before it would have been
driven to. The decline lines name the point every time the reason changes, so the log will say so;
if it happens in play, the answer is to add those reasons to the per-point cooldown rather than to
weaken the reservation.

**Not verified in the running game.** The new self-check cases run at plugin load and were
desk-checked arithmetically. The proof to look for in the next match is a `requesting air insertion`
line for a point whose review line reads `pickets=0 air`.

## Departure 12 — the picket rung banks across reviews (2026-09-14, user instruction)

**What the match showed.** In the 2026-09-14 `Ground Control Duel` log, the player-side commander's
picket insertions were refused on every review: `the ladder's picket share cannot cover the flight`
appears 92 times and `the priority ladder's picket share is empty this cycle` 31 times, while every
`ladder:` line reports `pickets 0`. Not one insertion flew.

**Why.** Rung 3's allocation was granted and thrown away each review. `ReviewPurchases`
(`Ai/CommanderEnemyCommanderService.cs`) called `GrantInsertionAllowance(hq, budget)` with this
review's share only, and `PlanInsertions` (`Operations/CommanderOperationsInsertion.cs`) charged
flights against it until the next review overwrote it. Rung 3's weight is 20 % and its floor 10 % of
a post-CAP remainder around 100, so the share was 10–30 a review; an insertion is a transport hull
(a Tarantula at 118, refunded on recovery) plus two picket vehicles (~16). The share could never
reach the price. The building rung already banked its allocation (`StructureSavings`), and so did
the air side (`AirFund`, `AwacsSavings`); rung 3 was the one rung that did not.

**The departure.** Rung 3 now accumulates, through the same `AccrueFund` the naval hull, the air
fund and the radar airframe's slice use.

- `CommanderState.PicketSavings` / `PicketSavingsTarget` hold the bank and its cap, beside
  `AwacsSavings` / `AwacsSavingsTarget` (`Ai/CommanderEnemyCommanderService.cs`).
- The cap is one complete flight: `PicketSavingsTarget(flightPrice, roadPairPrice)`
  (`Ai/CommanderEnemyCommanderLadder.cs`), where the flight price comes from the new
  `CommanderSupplyHeliService.CheapestInsertionFlightValue` — the `HasLaunchableVehicleTransport`
  walk with the price kept instead of stopping at the first hit — and the fallback, for a commander
  whose airbases launch no vehicle-carrying transport, is the cheapest pair of vehicles its own
  catalog offers, priced through the same `PickInsertionCargo` chooser the flight's cargo goes
  through (one chooser, three callers).
- Excess above the cap goes back into the same review's pool and the rung's budget is recomputed,
  the pattern the air fund's ceiling overflow already follows.
- What the bank takes is now deducted from the pool. The old grant was not — a share nobody had
  banked could not be double-spent, but a bank can be.
- Last cycle's charged flights come off the bank at the top of `ReviewPurchases`, from the single
  `TakeInsertionSpend` call that also feeds the `ladder:` line, so the deduction happens before the
  rung accrues again rather than after.

**The two decline reasons are gone.** `PlanInsertions` now gates on
`PicketSavingsCover(allowance, target)` — the shape of the FOB order's own `FobAffordable` money
gate — and a point that is merely waiting logs `PICKET <label>: saving for the flight (N/M)` once
per stretch of saving, through the existing per-point dedup map. The supply side's
`PicketShareDecline` is a named constant now so the operations side can tell "the rung is saving"
apart from "the flight is refused". The running total is on every `ladder:` line as
`pickets saved N/M`.

**Deliberate cost.** The gate is the price of a FULL flight even for a picket that has lost one of
its pair and only wants one vehicle. That costs such a point a review or two of extra saving and
buys one rule instead of two; the rung's cap is the same number, so the bank always reaches it.

**Not verified in the running game.** The build is clean (`0 Warning(s)`, `0 Error(s)`). The nine
new self-check cases run at plugin load and were desk-checked arithmetically, not observed failing.
The proof to look for in the next match is a `pickets saved N/M` field on the `ladder:` line that
climbs, then a `requesting air insertion` line, and no further
`the ladder's picket share cannot cover the flight`.

## Departure 13 — resource sites fly first (2026-09-14, user instruction)

**The instruction.** "Resource sites need to be a priority for air insertion." Taking a site is what
permits a mine (`CommanderStrategicPointService.SiteMinePermitted`) and the mine is what pays;
no other point on the map earns anything, so a site the commander does not hold is worth more than
the hilltop that happens to be farthest from a road.

**The departure.** `PlanInsertions` (`Operations/CommanderOperationsInsertion.cs`) ordered its
candidates farthest-off-road first. It now orders them by a rank:

- `IsPriorityInsertionSite(kind, held)` — a `StrategicPointKind.Site` we do not hold, and nothing
  else. A site already held is holding ground, not unlocking income, so it ranks like any other
  point.
- `InsertionCandidateRank(kind, held, distance)` — a priority site ranks by its distance to
  `CommanderCaptureService.GetTerritoryCenter(hq)` (the mean of the airbases held, the same read
  `RequestInsertion` already makes to choose the landing post, taken once per review); everything
  else shares `InsertionSiteRankCeiling`, a million metres, which is larger than any distance a
  stock map can produce. So no site ever sorts behind a point that is not one.
- The sort compares rank first and falls back to road distance descending, so the points that are
  not sites keep exactly the order they had.

**The savings queue jump is the same rule.** Rung 3 banks enough for one flight at a time
(Departure 12) and the request loop spends it on the first candidate that passes its gates, so
putting the site first IS giving it the flight when a site and a hilltop both qualify and there is
money for one. No second mechanism was added.

**The request line says why.** `PICKET RESOURCE SITE 27: requesting air insertion (site first).`
instead of the metres-from-the-road wording, so the log shows why a site was served ahead of points
farther off the road. `siteFirst` is passed down from the candidate rather than recomputed.

**Self-checks.** Seven cases beside the insertion gate's own: a site before a hilltop at equal
distance, a far site before a near hilltop, a held site ranking exactly like another point, the
nearer of two sites first, a site past the ceiling still ahead of a crossroads, a negative distance
clamped to zero, and the predicate itself on all three of its cases.

**Defect proof.** `IsPriorityInsertionSite` was inverted to `kind == Site && held`, the project
still built clean (so the cases run against the broken rule and four of them fail by construction),
and the file was restored and confirmed byte-identical by checksum. The `self-check FAILED` console
line itself can only be observed at plugin load in the running game.

**Not verified in the running game.** The proof to look for in the next match is a
`requesting air insertion (site first)` line for a `RESOURCE SITE` point while `HILLTOP` points that
are farther off the road are still waiting.
