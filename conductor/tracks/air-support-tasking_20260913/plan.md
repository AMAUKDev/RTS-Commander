# Plan: Air support tasking driven by the ground plan

**Track**: air-support-tasking_20260913 · **Design**: design.md (approved 2026-09-13)
**Expected agent runs**: 1 executor (this session) + 1 review + evaluation, per Track shape.
**Build after every task**: `NUCLEAR_OPTION_DIR="I:\SteamLibrary\steamapps\common\Nuclear Option" dotnet build GroundControlRts.csproj -c Release` — must end `0 Warning(s)` / `0 Error(s)`. No install scripts, no commits.

- [x] T1 **Settings.** `CommanderSettings`: `CasLossCooldownMinutes` (Operations, 2) and
  `AirborneCeiling` (Operations, 8) with rationale comments + warm-up touches. SelfCheck: config
  guards (cooldown > 0, ceiling ≥ 1) survive a retune — inside T2's `CheckAirSupport`.
- [x] T2 **Air model + pure rules.** `Operations/CommanderOperationsAir.cs`: `CommanderAirSortie`
  record, `CommanderPendingAirLaunch`, class constants with `<summary>` (6/3 km sortie radius, 3 km
  retarget hysteresis, 200/80 m/s transit, per-objective cap 4, 2-AD hesitation), pure
  `CasWanted`/`PlanSortie`/`CasLossCooldownMinutes`/`CasTransitSeconds`/`AttackHoldsForCas` and
  `CheckAirSupport` wired into `SelfCheck` (ladder boundaries, escort rule, cooldown table, transit
  boundaries, hold-release boundaries, config guards).
- [x] T3 **Demand + reconcile + fill + prune.** `PlanAirSupport(hq, state)` in the air partial,
  called from `Review` after `UpdateAttacks`: build the priority list (Attack with
  `FirstGroupArrivedAt >= 0` → contact platoons → threatened ForwardBases), update/dissolve/create
  sorties by Mission or platoon reference, fill from unbound owned airframes (escort first), prune
  dead/Returning bounds (loss stamp on dead-not-Returning), drop player-touched airframes via the
  issued-task snapshot, and add `OperationsState` fields. SelfCheck covered by T2 + review; verify
  build.
- [x] T4 **AirCommand seams.** `TryTaskAiAircraft` gains `retaskExisting` (update Mode/AreaCenter/
  Radius in place — the same writes `TryAdoptAircraft` performs); `RegisterFactionUnitPostfix` gains
  the ops aircraft notify; `PendingSpawnTimeoutSeconds` internal.
- [x] T5 **Launch claim.** `RecordCommanderLaunch` / `ClearCommanderLaunch` (pending expectation,
  set before `TryLaunchAiAircraft`, cleared on failure) + `NotifyAircraftRegistered` claim: match
  HQ+definition, never claim an airframe already carrying a mission, bind into the hungry sortie,
  task it, note it.
- [x] T6 **Buy loop.** Enemy service: remove the `if (duel)` around fund accrual + `BuyAirframe`;
  ceiling reads `AirborneCeiling`; `ChooseAirRole` reads `CommanderOperationsService.TryGetAirDemand`
  (Escort → Fighter, Cas → Strike, none → no Strike buy, once-per-reason denial); facing = sortie
  objective; `GetAirRole`/`AirRole` internal.
- [x] T7 **Residual posture.** `TaskAirWing` rewritten: every unbound *commander-owned* airframe
  (owned-set query) holds `AirGuard` over home territory; `StrategicStrike` and the 1-in-3 rotation
  deleted; `ReviewPosture` loses its `IsDuelMission` gate; `HomeGuardRadiusMeters` internal.
- [x] T8 **Escort wait.** In the fill/reconcile pass: a sortie wanting an escort holds its CAS
  airframes in `AirGuard` over the home CAP centre until the escort is bound, then releases both to
  the objective together; contact sorties task immediately. Verified by T2's checks + review.
- [x] T9 **Attack go-in hold + lead time.** `UpdateAttacks`' all-arrived branch holds while
  `AttackHoldsForCas` (sortie exists, no bound CAS within the sortie ring, transit from the nearest
  held airbase feasible), never past `AssaultFormUpTimeoutSeconds` from the same
  `FirstGroupArrivedAt` stamp; `CasHoldLogged` on the mission logs once. Transit boundary checks in
  T2.
- [x] T10 **Loss cooldown.** On a bound airframe dying (not Returning): stamp `LastLossAt`, note the
  loss with the observed hostile air-defence count; `TryGetAirDemand` skips sorties inside
  `CasLossCooldownMinutes` (doubled at ≥ 2 observed AD). Cooldown table checked in T2.
- [x] T11 **Diagnostics.** `air=[CAS <label> live/wanted [+esc]]` appended to the `Ops … review:`
  line; `CommanderAiLog.Note` for bind/escort/release/loss/hold; chatty retarget moves behind
  `OperationsDebugLog`.
- [x] T12 **Changelog + metadata.** CHANGELOG entry under Unreleased in the file's existing style;
  `metadata.json` loop_state stays honest (EXECUTE until in-game verification).

## Departures recorded during execution

1. **The loss cooldown gates the fill and the claim too**, not only the buy: "CAS stands down for N
   min" is only true if the sortie also stops taking already-airborne airframes while cooled. The
   design text said "no replacements launch"; binding an idle airframe would fly it into the same
   SAM ring the cooldown exists to avoid.
2. **The transit estimate is a bind-time local, not sortie state** (`EstOnStationBy` was planned):
   the hold reads physical presence, so a stored estimate was write-only. The lead-time arithmetic
   lives in `CasTransitSeconds` (self-checked) and in the hold's feasibility test
   (`CasCanArriveInTime`), and prints in the tasking line ("on station in ~N s").
3. **"Nearest friendly asset" for the escort hold = the home CAP centre** (the territory centre):
   the same "home ground" aggregate the reserve ring and the old home guard use — one definition,
   per the design's own reuse rule, rather than a new nearest-asset search.
4. **A pre-existing CS8601 in `OrderForMarch` was fixed** (`Unit?` + `!`, behaviour-identical):
   it surfaced the moment any real compile ran this session — the session's first "0 warnings"
   build had been a no-op incremental — and the finished build must show `0 Warning(s)`. It is
   platoon-track code, not air-track code, and is the only non-air edit outside the design's file
   list.
5. **The escort's CAP box uses the sortie radius** (6 km over the objective), not the 15 km home
   guard box: the escort keeps station over the same ring its CAS works.
6. `DuelAirframeBudgetShare` renamed `AirframeBudgetShare` (same 0.4) and `DuelAirborneLimit`
   replaced by the `AirborneCeiling` setting: both lost their duel prefix when the duel gate came
   out; the changelog names the rename.
## Post-ship revision (user decision 2026-09-13: CAP first) — additions

T1-T12 above shipped and ran in the first match. The user then asked, verbatim: "is there some cap
to number of aircraft up? opposition has money but i'm seeing barely any CAP missions. priority
should be CAP -> CAS (CAP FIRST) - platoons will handle CAS." Evidence from that match's
LogOutput.log: 15 x "it is at the 8-aircraft ceiling" (player-side commander, balance 1191); 8 x
"no sortie wants strike aircraft" (enemy, quiet reviews with no live sortie — no CAP demand existed
at all); 9 x "its air fund is N" (3-11) with the cheapest airframe at 12; 46 transport re-buys
churning the one-buy-per-review slot. Design decision 11 records the doctrine change.

- [x] T13 **CAP sorties.** `CommanderAirSortie`: `EscortWanted`/`Escort` → `CapsWanted`
  (`CapWanted`: baseline 1 + one per tracked hostile aircraft, capped `CapPerObjectiveCap` 3) /
  `Caps` list; `AddDemand` counts tracked hostile aircraft and adds the sortie even with nothing
  observed (the objective is active); `CommanderAirDemandKind.Escort` → `Cap`.
- [x] T14 **CAP-first order.** `TryGetAirDemand`/`FillSorties`/`TryClaimAircraft` serve the whole
  wing's CAP shortfall before any CAS; `NextAirDemand(capShort, casShort)` is the pure tie-break the
  demand walk reads; `ChooseAirRole` serves CAP→Fighter and Cas→Strike before the transport top-up.
  The first bound CAP is the escort (package rule and go-in hold kept: `CasIsHeldForCap`).
- [x] T15 **Throughput.** Buy loop: "buy while the air fund covers the next wanted airframe and the
  ceiling allows", bounded `MaxAirBuysPerReview` 3 (`AirBuyContinues`); `AirborneCeiling` 8 → 12
  (was binding); `AirFundCeiling` = max(reviews-of-saving cap, 3 x dearest AI-flyable fighter) — the
  naval fund keeps its old cap explicitly. Once-per-reason denials kept.
- [x] T16 **Diagnostics + self-checks.** Once-per-review `air demand: CAP n/m, CAS n/m, ceiling k/K,
  fund f` behind `OperationsDebugLog` (`ReadAirDemandCounts`); `DescribeAir` now
  `<label> CAP a/b CAS c/d`; `CheckAirSupport` gains the CAP ladder, `NextAirDemand`,
  `AirBuyContinues`/`MaxAirBuysPerReview` and `AirFundCeiling` cases. Build `-t:Rebuild`: 0 warnings,
  0 errors.

Departure: the once-per-reason denial text changed ("no sortie is asking for air support and the
wing has its fighters and transports") — the old string named the strike-only rule that CAP-first
deleted; the reason key resets with it, so the new reason logs once.
