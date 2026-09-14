# Plan: Smarter air wing — ordnance, helicopter CAS, packages, AWACS, ARAD, idle sweep

**Track**: smarter-air-wing_20260914 · **Design**: design.md (approved 2026-09-14, addendum same day)
**Expected agent runs**: 1 executor (this session) + 1 review + evaluation, per Track shape.
**Build after every task**: `NUCLEAR_OPTION_DIR="I:\SteamLibrary\steamapps\common\Nuclear Option" dotnet build GroundControlRts.csproj -c Release` — must end `0 Warning(s)` / `0 Error(s)`. No install scripts, no commits.

- [x] T1 **Settings.** `Core/CommanderSettings.cs`: `PackageFormUpSeconds` (Operations, 180),
  `RotaryCasRangeMeters` (Operations, 40000), `AradClusterMinimum` (Operations, 3) with rationale
  comments and warm-up touches. SelfCheck: config guards (form-up wait positive, rotary range
  positive, cluster minimum at least 2) read into locals first so a retune cannot be folded away —
  inside `CheckAirSupport`.
- [x] T2 **CAS ordnance preference (design SS1).** `AirCommand/CommanderAirCommandLoadout.cs` /
  `CommanderAirCommandScoring.cs`: class-level `PreferredCasOrdnance` name table with a `<summary>`
  (`AGM-68`, `AGM-48`, confirmed against the game's own asset strings), a
  `PreferredCasOrdnanceBonus` applied in the CAS arm of `ScoreLoadout` and only for the commander's
  own builder, large enough to outrank any other air-to-ground store and applied after the hardpoint
  and strip tests so it can never make an incompatible mount legal. `Ai/CommanderEnemyCommanderAir.cs`
  roster line gains `CAS ordnance: AGM-68/AGM-48 available` or `none — falls back to <best A/G>`.
  SelfCheck: ordnance ranking AGM-68 > AGM-48 > other A/G > none.
- [x] T3 **Last-resort bug (addendum SS8).** Pure `LastResortAllowed(bool ordinaryCandidateAtAnyPrice)`
  in `CommanderEnemyCommanderAir.cs`; `BuyHomeCapFighter` counts ordinary candidates before the
  budget filter and refuses the last-resort buy while one exists at any price;
  `TryBuyRole`/`BuyAirframe` split the same way (`out bool sawCandidateAtAnyPrice`, denial reporting
  moved onto the pass that actually ends the buy). SelfCheck: the observed Compass/Cricket case.
- [x] T4 **Sortie kinds and the pure rules.** `Operations/CommanderOperationsAir.cs`:
  `CommanderSortieKind` (Objective, Cap, Awacs, Arad), sortie fields (`Kind`, `WantsRotary`,
  `FormUpPoint`, `HasFormUp`, `FormUpFirstArrivalAt`, `GoneIn`, `AradPending`), constants with
  `<summary>` (form-up 12 km, arrival 3 km, AWACS 30 km / 20 km, ARAD link 5 km, second ARAD
  airframe at 6, platoon CAP cap 3), and the pure rules `SortieIsPackage`, `PackageFormUpDistance`,
  `PackageGoesIn`, `AradWanted`, `AwacsStandoffMeters`, `SortieWantsRotaryCas`, `PlatoonCapWanted`
  with their `CheckAirSupport` cases.
- [x] T5 **Demand widening.** `CommanderAirDemandKind` gains `Awacs` and `Arad`; `NextAirDemand`
  becomes the four-way priority AWACS > CAP > ARAD > CAS; `TryGetAirDemand` reports the kind, the
  objective, the sortie's rotary preference and its label; `AirRole` gains `Awacs` and `Arad` with
  their capability tests, `BuildRoleLoadout` mode map, `RoleCapabilityLabel` text and `CountRole`
  arm. SelfCheck: the four-way order.
- [x] T6 **Packages (design SS3).** Form-up point per sortie from the nearest accepting held base,
  the AirGuard-style hold there replacing the hold-over-home, the go-in test each review, the three
  log lines, late arrivals joining directly; `HoldsForCas` now reads "package gone in and a bound
  airframe inside the objective ring".
- [x] T7 **AWACS (design SS4).** Radar-capable candidate test on the loadout builder, one `Awacs`
  sortie per commander, station geometry from the front centre toward the main base, AirGuard at
  the station, never lent (`TakeUnboundOwned` skip), loss cooldown through the existing stamp,
  demand ahead of CAP.
- [x] T8 **ARAD (design SS5).** Tracked hostile air-defence clustering at 5 km link distance each
  review, cluster near an active objective opens an `Arad` sortie over the centroid sized by
  `AradWanted`, anti-radiation loadout through the `Arad` mode scorer, the same objective's CAS
  package held at form-up until the ARAD sortie goes in (bounded by the package wait).
- [x] T9 **Platoon-requested CAP (addendum SS7).** A `Cap`-kind sortie over any live platoon or
  ForwardBase/Picket point with tracked hostile aircraft inside `ObservedRadiusMeters`, sized by
  `PlatoonCapWanted`, demanded after AWACS.
- [x] T10 **Rotary CAS by mission kind (design SS2).** `AirRole.RotaryCas` capability (rotary prefab
  pilot, plane pilot for tasking, ground-attack-capable), `FindAcceptingAirbase` gains an optional
  range gate, the buy tries rotary first for a rotary sortie and falls back to a jet with one log
  line per objective.
- [x] T11 **Idle sweep (design SS6).** `SweepIdleAirframes` in the air partial, called from
  `PlanAirSupport` and from `TaskAirWing` in place of its own posture loop: A/A-capable → home CAP,
  transports and Winchester → RTB, the rest → home CAP, one log line each.
- [x] T12 **Diagnostics.** `DescribeAir` prints `pkg n/m`, `AWACS`, `ARAD <label>` and
  `CAP <platoon>`.
- [x] T14 **Lend the home CAP forward (design SS9, user 2026-09-14).** Constants
  `HomeCapMinimumHeld` (1) and `HomeCapQuietSeconds` (120) with rationale; pure `LendableHomeCap`,
  `HomeCapIsQuiet` and `HomeCapShortfall`; the quiet clock refreshed in `MaintainHomeCap` on the
  10 s defence cadence; `TakeLendableHomeCapFighter` as the CAP fill's last resort, nearest fighter
  to the sortie first; `RecallLentHomeCap` before the demand is rebuilt; the `ladder:` line reports
  `, N lent`. SelfCheck: lendable count, quiet boundary, and that a loan cannot double-buy.
- [x] T15 **Contact outranks cover (design SS10, user 2026-09-14).** `RetaskHoldSeconds` (90) with
  rationale; pure `SortieIsInContact`, `RetaskSourceRank`, `MayRetask` and `MayMoveHomeCapFighter`;
  `InContact` set at demand time on every sortie kind; `RetaskToContact` between the package update
  and the task sync, taking from the best-ranked quiet sortie and nearest airframe within it;
  `TaskOntoSortie` as the one door so the sync agrees with the move; `retask` on the review line.
  SelfCheck: contact test, source ordering, hold boundary, the home-CAP minimum guard.
- [x] T16 **Sortie and package markers (design SS11, user 2026-09-14).** New partial
  `Operations/CommanderOperationsAirMarkers.cs` reusing the platoon marker's dot, label helper,
  world-vs-map split and tracking test; one marker per sortie at its lead airframe or form-up point,
  one smaller marker per bound airframe, one for the standing home patrol; pure `AirMarkerKind`,
  `AirMarkerPhase`, `AirMarkerFlags`, `AirMarkerLabel` and `AirMarkerElementRole` with
  `CheckAirMarkerLabels` wired into `SelfCheck`; one additive call added to `DrawMarkers`.
- [x] T17 **A marker on every aircraft (design SS12, user 2026-09-14).** `DrawUnboundAirframes`
  walks every owned airframe the sortie markers do not name; pure `ClassifyAirframe`,
  `AirframeMarkerLabel` and `EnemySortieLabel`; amber `UNTASKED` for anything unaccounted for; the
  once-per-review `air markers:` count behind `OperationsDebugLog`; `CheckAirframeMarkerLabels`
  wired into the existing marker self-check. The dead `DrawHomeCapMarker` it replaced is removed.
- [x] T18 **Labels for aircraft the commander does not own (design SS13, user 2026-09-14).**
  `DrawOtherAircraft` walks `hq.factionUnits` for everything the owned walk skips; pure
  `ClassifyOtherAircraft` and `OtherAircraftLabel`; the diagnostics line gains `N other drawn`;
  `MarkerElementWorldSize` / `MarkerElementMapSize` raised to the platoon marker's sizes.
- [x] T19 **Labels from the first frame (design SS14, user 2026-09-14).** `DrawAllAircraftMarkers`
  and `DrawFactionAircraft` run with no operations state, called once at the top of `DrawMarkers`;
  pure `LabelsInStatelessWalk` plus a per-frame set keeps one label per aircraft; the state-bound
  `DrawOtherAircraft` from Section 13 is removed as superseded; `Air markers ready:` line proves the
  labels exist before the first review.
- [x] T20 **Adopt strays after a reload (design SS15, user 2026-09-14).** Pure
  `AdoptsStrayAircraft`; `RegisteredWhileCommanded` stamped from the existing registration postfix
  and pruned each sweep; `AdoptStrayAircraft` runs at the top of `SweepIdleAirframes` so the
  existing tiering tasks them; the sweep's log line says "adopts … (stray after reload)".
- [x] T21 **Say where the aeroplane is (design SS16, user 2026-09-14).** `DepartureRadiusMeters`
  (3 km); pure `ResolveAirPhase` and `AirPhaseText` with 15 self-check cases; `AirframePhaseText`
  and `LeadPhase` as the runtime plumbing; applied to the sortie marker, every element marker and
  the unbound-airframe labels; `CommanderAirCommandService.IsOnDeck` widened to internal.
- [x] T22 **Hands off another service's aircraft (design SS17, user 2026-09-14).** Pure
  `LeavesToOtherService` with four self-check cases; `IsUnderOtherService` as the one door consulted
  by the claim, the fill, the idle sweep, the stray adoption, the retask and the loan;
  `CommanderSupplyHeliService.IsOnSupplyRun` widened to internal and covering the pending-spawn
  window; `leaves … alone` logged once per aircraft behind the debug flag.
- [x] T13 **CHANGELOG + final rebuild.** Unreleased entry; `-t:Rebuild` ending `0 Warning(s)` /
  `0 Error(s)`.

## Departures from the design

1. **AWACS "20 km over the main base"** is implemented as the orbit centred on the main base with a
   20 km mission radius (`AwacsOrbitRadiusMeters`), not as a 20 km offset in an undefined direction:
   with no front there is no direction to stand off along. `AwacsStandoffMeters(hasFront: false, …)`
   therefore returns 0 and the station is the base itself.
2. **The form-up point is one per sortie, not one per airframe.** The design says "12 km from its
   base toward the objective" per airframe but also requires every airframe to be "within 3 km of
   the form-up point" — one point per sortie is the only reading that satisfies both. It is measured
   from the held base nearest the objective.
3. **The hostile stand-off clamp is measured along the base→objective line.** The design says the
   form-up point is clamped to at least `PreemptiveAirRangeMeters` from the nearest tracked hostile;
   pulling the point back along the line is the bounded, self-checkable form of that.
4. **Rotary preference is a buy preference, not a binding filter.** An already-owned jet still fills
   a rotary sortie's CAS slot rather than idling, which is what the idle sweep exists to prevent.
   Only the buy chooses rotary first.
5. **Section 1's ordnance table is a scorer bonus in `ScoreConventionalWeapon`**, where the existing
   1.42x AGM-48/AGM-68 delivery nudge already lived; the nudge is replaced by the named table so
   there is one definition of "preferred CAS ordnance" rather than two. The player's AIR window
   keeps its unweighted list: the bonus is behind a `preferCasOrdnance` flag only the commander sets.
6. **The ARAD sortie never forms up.** It is marked immediate, so suppression goes in as soon as the
   airframe is up rather than waiting three minutes for a second one. The design did not say either
   way; holding a suppression sortie at an orbit while the CAS it is unblocking waits on it would
   have stacked two waits on one clock.
7. **The "opens ARAD" line is printed by the reconcile, not the demand pass.** The demand list is
   rebuilt every 30 s review, so logging there announced the same belt every review for as long as
   it sat there.
8. **A lent fighter is lent only when nothing else is free.** The CAP fill takes an ordinary spare
   airframe first and borrows from the base only when there is none, so the loan never costs the
   base a fighter it did not have to spend.
9. **The idle sweep prints one line per airframe per idle spell, not per review.** An airframe with
   no mission carries no RTB record, so the landing order genuinely has to be re-issued each review
   (the rotary landing state hands itself back to combat whenever the pad it wanted is busy); the
   line does not.
