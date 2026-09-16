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

## Departure 10 — the helicopter CAS range is 90 km, under a new key (2026-09-14, user instruction)

**What the match showed.** `CROSSROADS 20 CAP 0/2 CAS 0/4 rotary` never gets a Chicane, and the
whole 2026-09-14 log contains ZERO `falls back to a jet` lines. Two separate causes:

1. Section 2's rotary gate asks for a pad or strip within `RotaryCasRangeMeters` (40 km) of the
   objective. The map the match was played on is 81920 m across, so most front-line objectives have
   no held pad within 40 km at all and the rotary pass can never find a candidate.
2. The fallback line is only reached when the buy is serving a CAS demand in the first place, and
   the demand walk never returned one (Departure 11 below). So the range was never even the binding
   reason — the reader had no way to see either fault from the log.

**The decision.** The default is 90 km, which covers the diagonal of an 80 km map (about nineteen
minutes each way at the 80 m/s rotary transit speed the wing already uses). The config key is
RENAMED `Operations/RotaryCasRangeMeters` → `Operations/HeliCasRangeMeters`, because BepInEx keeps
whatever value is already written in the .cfg: without the rename, every existing install would have
kept the old 40 km and the new default would have reached nobody. This follows the same convention
as `MapDragSpeed` and `KeepStateAcrossHotReload`. **The old `RotaryCasRangeMeters` key is now
orphaned** in existing config files; it is inert and can be deleted by hand.

**The fallback line now prints every time it happens, with the distance.** The once-per-objective
`RotaryFallbackLogged` set is gone from `CommanderState` — how OFTEN a rotary sortie is refused is
the thing the reader is being asked to judge, and one line per review per rotary sortie is what
makes "raise the range" or "this roster fields no attack helicopter" an obvious call instead of a
guess. The no-candidate variant now reads:

`no attack helicopter can launch within 90 km of CROSSROADS 20 (nearest pad 104 km); CAS falls back
to a jet.`

The distance comes from the new `NearestRotaryLaunchMeters`, which measures over the same capability
gate and the same airbase-acceptance pair the buy itself uses (Reuse rule 4), ignoring only the
range gate, and reports `no pad at all` when the roster has no rotary CAS airframe any held base
accepts.

## Departure 11 — the buy alternates between the fighters and the strike (2026-09-14, user instruction)

**The report.** "We're not getting much platoon CAS spawning in general, seems to be CAP."

**Root cause, from the 2026-09-14 log and the code.** Two rules compound.

1. `NextAirDemand` (`Operations/CommanderOperationsAir.cs`) served a CAP shortfall before any CAS
   shortfall, unconditionally. That was written as a tie-break (user decision 2026-09-13: "when the
   fund covers one airframe and both are wanted, it buys the fighter") and with tens of objectives
   open it became a permanent veto. A CAP demand the roster cannot fill at all is the worst case:
   the player commander in this match logs `bought no aircraft: its strips accept no
   air-to-air-capable airframe at all` seven times while its `CAS 0/1 pre` slots sit untouched.
2. `TryGetAirDemand` read only each sortie's SINGLE next slot, and `NextSortieSlot` puts the escort
   ahead of the CAS it is escorting. A pre-emptive platoon sortie is `CAP 0/1 CAS 0/1`, so its next
   slot is `Escort` — a CAP demand. With six platoons calling for pre-emptive air, the commander had
   to buy six fighters before it could buy one strike airframe, and the review line reads exactly
   what the user saw: `air=[… 6TH PLATOON CAP 0/1 CAS 0/1 pre; 7TH PLATOON CAP 0/1 CAS 0/1 pre; …]`
   with every CAS slot at zero.

**The decision.** A 1:1 alternation between the two sides of the wing, and a demand read that can
see a CAS slot standing behind an unbought escort.

- Pure rule `PrefersCasThisBuy(capOpen, casOpen, lastBuyWasCas)` with seven self-check cases: with
  one side open it takes that side, with both open it takes the side that did not get the last buy.
- `NextAirDemand` gains `preferCas`. The AWACS keeps its outright priority above the alternation;
  ARAD travels with the CAS side and is still served before the CAS it clears the way for; a CAS
  turn that finds nothing on the CAS side hands the buy back to the fighters rather than buying
  nothing.
- `OperationsState.LastAirBuyWasCas` is the whole memory. `TryGetAirDemand` advances it only under
  its new `advanceTurn` flag, which only the buy sets — the "is anything open" and "what is the fund
  saving for" reads never cost a side its turn. A buy that then fails to find an affordable airframe
  still spends its turn, deliberately: a demand the roster cannot fill is exactly the one that must
  not hold the other side up for the rest of the match.
- The demand walk now records a sortie's unfilled CAS slot whether or not its escort is up, so a
  wing of unescorted sorties no longer reads as pure CAP demand.
- `NextSortieSlot` gains `preferCas`, which puts the sortie's own CAS ahead of its escort for that
  one binding, and the claim walks the sortie list twice — first the way the buy read it, then the
  other way — so an airframe bought on a CAS turn actually lands in the CAS slot it was bought for,
  and one bought for a slot that could not be filled as asked still finds any slot it can fill
  instead of idling on the home patrol. Five self-check cases, including one asserting the CAP-side
  turn is byte-identical to the order Approval 1 wrote.
- `ReadOpenAirDemandRoles` reads CAS demand the same way, so the air fund's ceiling is allowed to
  save for the strike airframe the next CAS turn will ask for.

**What was investigated and NOT changed.**

- *Platoon-requested CAP is already capped the way the instruction asks.* `AddCapDemand` opens a
  platoon CAP sortie only when `CountHostileAirInRing` is above zero, and `PlatoonCapWanted` returns
  1 for one or two tracked hostile aircraft, 2 for four, 3 for six, capped at three. The
  `PlatoonCapMinimum` of 1 is a floor on a sortie that has already proved a hostile aeroplane is
  overhead; it opens nothing on its own and is not the flooder. The `CAP CROSSROADS 20 0/3` lines in
  the log are four or more tracked hostile aircraft, which is what that ladder is for.
- *The loss cooldown is not the cause.* Ten `CAS stands down for N min` lines in the whole match,
  five of two minutes and five of four; nineteen `cooldown` tags across every review line of both
  commanders. Real, bounded, and far too rare to explain `CAS 0/4` for a whole match.
- *The strike-buy denials are a symptom, not the cause.* Every `bought no aircraft` line in the
  match is an air-to-air or radar shortfall (`no air-to-air-capable airframe at all` ×7, `saves for
  one rather than launching the last-resort airframe` ×10, home-CAP tier ×8). Not one is a strike
  denial, because a strike buy was hardly ever attempted.

**Not verified in the running game.** The self-check cases run at plugin load and were desk-checked
arithmetically. The proof to look for in the next match is CAS counts climbing on the `air demand:`
line (`CAP n/m, CAS n/m`) and non-zero CAS on the `air=[…]` sortie list, plus at least one
`tasks … with CAS` for a platoon's pre-emptive sortie before that sortie's escort is up.

## Departure 12 — the radar airframe stops outranking the whole wing (2026-09-14, user instruction)

**What the live log shows.** Player side, one match: `air demand: CAP 0/9, CAS 0/22`, with nine
`bought no aircraft: its air budget is short of the cheapest radar-carrying airframe its strips
accept, and it saves for one rather than launching the last-resort airframe`, four of the same for
the fighters, and launches of nine FS-12 Revoker plus three EW-25 Medusa and not one strike
airframe. The ladder line reads `air saved 40 (cap 390)`.

**Root cause.** `NextAirDemand` returned `Awacs` before anything else unconditionally, and a
denial ends the review's air buying at the caller's `break`. A Medusa is 145 and the slice is small,
so the commander read "AWACS" every review, refused every review, and never reached the
ground-attack demand at all. Departure 11's alternation could not help: it explicitly excluded the
AWACS from the turn.

**The decision.**

1. **The radar airframe is a CAP-side buy and takes its turn like everything else.** On the CAP
   side's turn it is still served first, ahead of the fighters (design SS4's order, intact). On the
   CAS side's turn, suppression and ground attack come first and the radar airframe waits. It is
   still served if the CAS side turns out to have nothing open, so a wing that only wants an AWACS
   still buys one. `NextAirDemand` is restructured around the turn; the turn now advances on an
   AWACS buy as well.
2. **A lost radar airframe is not replaced for ten minutes.** `AwacsLossCooldownMinutes` = 10,
   deliberately the same number as `OperationsHeliInsertionCooldownMinutes`, though not self-checked against
   it — that one is a player-facing setting, and a self-check that fails because a slider moved is a
   false alarm. `SortieLossCooldownMinutes` is the pure rule;
   `StampAirLoss` reads it and the loss line says `loses the radar airframe: no replacement is
   bought for 10 min.` The cooldown survives the review because `ReconcileSorties` already carries
   `CooldownUntil` onto the rebuilt sortie.
3. **A failed buy no longer ends the review's air buying.** The demand read advances the turn
   whether or not the buy succeeded, so the caller now makes one more call, which asks the other
   side of the wing. Two failures in a row means both sides are stuck and the review is genuinely
   over. This is what makes "the ground-attack side still buys while the fighters are saving" true.
4. **The denial line names the side and what the other side did.** `[CAP] bought no aircraft: …
   (earlier this review: the CAS side bought an airframe for 22).` The de-duplication key is still
   the reason alone, a stable string, so the context can move without re-logging an unchanged
   refusal.

**Not done, and why: the split air fund.** The instruction offered "let it save only from a share
(e.g. the budget's CAP-type half)". Halving the fund by side would have made the dearest airframe
unbuyable outright: `AirFundCeiling` caps the fund at the price of the dearest airframe an open
demand wants, so a side limited to half the fund can never reach that price. Giving each side its
turn, and letting the other side spend when one is saving, reaches the same goal — the CAS half
always gets its turn — without breaking the ceiling.

**Investigated, no defect found: the platoon CAP baseline.** The instruction asks that a
platoon-requested CAP open "only when hostile aircraft are actually tracked near that platoon".
`AddCapDemand` already returns without opening anything when `CountHostileAirInRing` is zero, and
that count only admits contacts seen inside `ThreatMemorySeconds`, which is 45 seconds — shorter
than one 30-second review, so these are live contacts and not stale memory. `PlatoonCapWanted`
returns 1 for one or two tracked aircraft and only grows past that at four and six. Every
`CAP <x> 0/1` line in the log is therefore one or two hostile aeroplanes genuinely overhead; the
`0/3` ones are four or more. `PlatoonCapMinimum` is a floor on a sortie that has already proved a
hostile aeroplane is there, and opens nothing by itself.

What was genuinely missing is that the log gave the reader no way to check any of this. The review
line's CAP-only sorties now carry the count that opened them: `CAP HILLTOP 5 0/1 air2`. If a future
match shows `air0` on one of those, the gate really is broken and the line will say so.

## Departure 13 — a suppression missile must have a warhead (2026-09-14, user instruction)

**The report.** "It's spawned ARAD helicopters (fine) with 4x eyeball loadouts — they're just recon
missiles and won't actually kill anything."

**What the weapon data says.** From `resources.assets`, the game's own description of the store:
"Eyeball Mk.II — This modified AGM-48 replaces the missile's usual warhead with a panoramic optical
sensor suite capable of detecting enemy targets and registering them as contacts on faction
datalink." It is an AGM-48 airframe with the warhead removed.

**Root cause.** `IsAradWeapon` accepted a store if ANY of four things was true, and two of them are
not evidence of an anti-radiation weapon at all:

- `targetRequirements.minRadar > 0` says only that the TARGET must be emitting. A passive sensor
  round aimed at emitters satisfies it exactly as an anti-radiation missile does.
- a `weaponName` containing "ARAD" is a name, not data.

Neither says anything about a warhead, and nothing in the old rule did. Four hardpoints of sensor
rounds therefore scored as a full suppression loadout.

**Fields keyed on, and how each was verified.**

| Field | Where it comes from | Verified how |
|---|---|---|
| `WeaponInfo.pierceDamage`, `WeaponInfo.blastDamage` | decompiled `WeaponInfo` | **Verified**: the same two numbers `Unit.TakeDamage(pierceDamage, blastDamage, …)` is given, and `Missile.InterceptPriority` reads `info.blastDamage` to decide whether a missile is worth intercepting. The catalog-level mirror of the missile prefab's own `warhead` / `blastYield` / `pierceDamage` payload block. |
| `WeaponInfo.missile` | decompiled `WeaponInfo` | **Verified**: a public bool beside `bomb`, `glideBomb`, `gun`, already used elsewhere in this scorer. |
| `ARMSeeker` component on `WeaponInfo.weaponPrefab` | decompiled `ARMSeeker : MissileSeeker` | **Verified**: the game's own anti-radiation seeker, which keeps a list of `Radar` returns; the sibling seekers are `ARHSeeker`, `SARHSeeker`, `IRSeeker`, `LaserSeeker`, `OpticalSeeker`. |
| `WeaponInfo.nuclear` | decompiled `WeaponInfo` | **Verified**: already excluded everywhere else in this scorer. |
| The Eyeball's actual field VALUES | `resources.assets` | **Assumed**, from the game's own in-game description quoted above. The serialized asset values were not parsed out of the binary; the rule is keyed on the fields, so if the assumption is wrong the roster line will say so by naming the Eyeball under `ARAD:`. |

**No recon flag exists to key on.** There is no reconnaissance weapon type, camera flag or recon
component anywhere in `Assembly-CSharp` — the class list has no `Recon`, `Eyeball` or weapon-side
`Camera` type at all. The absence of a warhead IS the game's own way of expressing it, which is why
the damage pair is the right key.

**The decision.**

- `IsAradCandidate(missile, nuclear, pierceDamage, blastDamage, hasArmSeeker)` — pure, nine
  self-check cases driven with synthetic weapons, including the Eyeball's own shape (ARM seeker,
  zero warhead → not a suppression weapon). A store must be a missile, not nuclear, carry an
  `ARMSeeker`, and deliver damage. The `minRadar` and name clauses are gone.
- The catalog's `effectiveness.antiRadar` rating is deliberately NOT also required. The asset values
  cannot be read outside the running game, so requiring both could have silenced suppression
  entirely; the seeker component is unambiguous on its own, and a radar-homing air-to-air missile
  carries an `ARHSeeker` rather than an `ARMSeeker`, so it is excluded by the seeker test and needs
  no rating comparison.
- `DeliversDamage(pierceDamage, blastDamage)` is the shared read, and it now gates CAS eligibility
  and CAS scoring as well as strategic strike, so a warhead-less round can never count toward "this
  airframe can attack ground". Air-to-air scoring is deliberately left alone: it already requires an
  anti-air rating above 0.05, which no sensor round carries, and widening the gate there would have
  risked a real missile whose catalog damage is recorded differently.
- **No anti-radiation missile on the roster now means no suppression sortie opens at all.**
  `InsertAradDemand` takes the AWACS gate's own `HasRoleCandidate` test. The tightened rule can
  answer "no" where the old one always found something, and an unfillable sortie sitting in the
  demand queue is precisely the failure Departure 12 exists to prevent.
- The roster line names the weapon per airframe: `…, ARAD: AGM-65 Sledge` or `…, ARAD: none`.

**Not verified in the running game.** The self-check cases run at plugin load and were desk-checked.
The proof to look for in the next match is the `Air roster (…)` lines: every airframe should read a
real anti-radiation missile or `none`, and none of them should read `Eyeball Mk.II`.

## Departure 14 — the warhead rule broke three ordnance self-checks (2026-09-14, follow-up to Departure 13)

**What failed.** At plugin load, in the installed build:

```
Enemy air buy self-check FAILED (the AGM-68 outranks the AGM-48): expected True, got False.
Enemy air buy self-check FAILED (the AGM-48 outranks a twenty-store rocket pod): expected True, got False.
Enemy air buy self-check FAILED (an ordinary ground store still outranks a weapon that cannot hit the ground): expected True, got False.
```

**Root cause: the probes, not the rule.** `MakeCasMount` built its synthetic weapons with a name, an
anti-surface rating and a rack size, and left `pierceDamage` and `blastDamage` at their default
zero. Departure 13 made the CAS scorer require a warhead, so every probe — the AGM-68, the AGM-48
and the rocket pod alike — scored zero, and three comparisons that had nothing wrong with them
became `0 > 0`. The fourth check in the same group, "a weapon with no ground effectiveness scores
nothing for CAS", still passed, which is exactly why the failure looked like a ranking collapse: it
was, but the ranking was collapsed by the test data.

**Fixed.**

- `MakeCasMount` now models a warhead by default (`pierceDamage` 60, `blastDamage` 200 —
  representative, not exact; the rule reads "greater than zero"), with a `warhead: false` parameter
  for the one probe that should not have one.
- The recon probe is now the Eyeball Mk.II's real shape: the AGM-48's name, a four-round rack, an
  anti-surface rating of **0.9**, and no warhead. It was previously rated 0 against ground, so the
  check that it is "not preferred CAS ordnance" was passing on the rating and never touched the
  warhead at all. Two new cases assert what the rule is actually for: a warhead-less round scores
  zero for CAS however it is rated, and never outranks a real ground store.
- `PreferredCasOrdnanceRank` now requires a warhead as well. Its own comment already said the recon
  variant "carries a sensor instead of a warhead", but it was testing the anti-surface rating to get
  there — an assumption about asset data nobody has read.

**The open risk, and the evidence line that settles it.** The rule keys on `WeaponInfo.pierceDamage`
and `WeaponInfo.blastDamage`. Those are verified as the game's own damage fields (Departure 13's
table), but the VALUES the real AGM-68 and AGM-48 carry cannot be read at build time: the weapon
assets are binary, and the only plain-text stats table in `resources.assets` is the unit-definition
table (`MissileDefinition;AGM-48 ;MSL;…`), which carries no `WeaponInfo` fields. If those two
missiles declared no warhead, the CAS ordnance preference would be silently switched off in play.

The roster therefore prints the numbers once per faction, read straight off the hardpoint sets
rather than through the scorer, so the report can show the scorer being wrong:

```
Air roster (<faction>): CAS ordnance data: AGM-68 (pierce 60, blast 220, A/G 0.90, rank 0), AGM-48 (pierce 40, blast 150, A/G 0.90, rank 1)
```

and, in the failure case, the line says so in words rather than leaving two zeros to be noticed:

```
AGM-48 (pierce 0, blast 0, A/G 0.90, rank -1) *** DECLARES NO WARHEAD — the warhead rule is reading the wrong field and the CAS ordnance preference is OFF for this store ***
```

If that appears in the next match, the fix is to take `DeliversDamage` off the CAS path
(`PreferredCasOrdnanceRank`, the CAS branch of `ScoreLoadout`, and `IsStationEligible`) and leave it
on the suppression path alone, where the seeker test carries the rule on its own.

**Not verified in the running game.** The five ordnance cases were desk-checked arithmetically
against `ScoreConventionalWeapon`: with a warhead the AGM-68 scores about 201, the AGM-48 about 102,
the twenty-round rocket pod about 3.3, and both the warhead-less and the air-to-air probes exactly
zero, which is the order the three checks assert.

## Departure 15 — the warhead rule read the wrong field for cluster weapons (2026-09-14, live data)

**What the installed build reported.** The three ordnance self-checks passed, and the evidence line
Departure 14 added said:

```
CAS ordnance data: AGM-48 (pierce 0, blast 0, A/G 0.64, rank -1) *** DECLARES NO WARHEAD … ***,
                   AGM-68 (pierce 700, blast 120, A/G 0.81, rank 0)
```

One AGM-48 entry, so that is the real store — the user's preferred close-support missile, lethal in
play — and it declares no damage at all on its `WeaponInfo`. Departure 13's rule had silently
switched the CAS ordnance preference off for it. This is exactly the outcome the evidence line was
added to catch, caught on the first reload.

**Where the AGM-48 keeps its damage.** It is a CLUSTER weapon. Its missile prefab carries a
`SubmunitionDispenser`, which on approach spawns a swarm of submunitions:

- `SubmunitionDispenser.submunitions` — `GameObject[]`, one entry per submunition carried.
- `SubmunitionDispenser.submunitionType` — a `WeaponInfo` of its own, and it is
  `submunitionType.weaponPrefab` that `Spawner.SpawnMissile` actually launches at each detected
  target (`AssignSubmunitionTargets`).

The damage therefore lives on the SUBMUNITION's `WeaponInfo`, not the parent's. The parent really
does have no warhead, which is why reading it alone was both literally true and completely wrong.

**Every damage-carrying field found, with where it lives.**

| Field | Type | Declares damage? |
|---|---|---|
| `WeaponInfo.pierceDamage`, `WeaponInfo.blastDamage` | catalog | Yes — the only two damage numbers on a store's catalog entry. The AGM-68 reads 700 / 120. |
| `SubmunitionDispenser.submunitionType` (a `WeaponInfo`) + `.submunitions.Length` | missile prefab, private | Yes — a cluster store's whole lethality, and the AGM-48's. |
| `Missile.warhead` (a `Missile.Warhead`), `Missile.blastYield`, `Missile.pierceDamage` | missile prefab, private | The runtime source the catalog pair mirrors; `Warhead.Detonate` is handed `blastYield`. Nothing a catalog read needs on top of the pair. |
| `WeaponInfo.pK`, `WeaponInfo.armorTierEffectiveness`, `WeaponInfo.airburstHeight` | catalog | No. A kill probability, an armour-tier multiplier and a fuze height — none of them a damage amount. |
| `DamageInfo.fireDamage`, `DamageInfo.impactDamage` | runtime only | No declarative source on `WeaponInfo` at all. Fire comes from effects and impact from kinetics; neither can be read off a store's catalog entry. |

**The corrected rule.** `CarriesLethalPayload(missile, pierceDamage, blastDamage, submunitionCount,
submunitionPierce, submunitionBlast)` — a store is lethal when it declares damage itself, OR when it
dispenses submunitions that do.

It also adds an exemption that the first version lacked: **a store that is not a guided missile is
never judged on these numbers at all.** Only a missile has been observed keeping its damage
somewhere else, and silently disarming a gun, bomb or rocket pod whose data is recorded differently
would be a worse failure than the one being fixed. The reported defect is a missile; the rule now
only ever refuses a missile.

`GetSubmunitionLoad` is the live read. Both dispenser fields are private and serialized, so it
reaches them by reflection — the approach the loadout scorer already uses for the game's private
`rangeFalloff` — and memoizes per weapon, since the scorers ask per mount per review. A store with
no prefab at all (only the self-check's synthetic probes) is answered without being remembered, so
the table never holds a destroyed probe alive.

**The Eyeball stays out, by either branch.** Its prefab is the AGM-48 airframe with the warhead
replaced by a sensor. If it carries no dispenser, it declares nothing and dispenses nothing. If it
kept the dispenser and swapped the submunitions for sensors, those submunitions declare no damage
and the cluster branch refuses it too. No name and no sensor-component test is needed; the roster
line now says which branch answered.

**The same corrected test is on the suppression gate.** `IsAradCandidate(missile, nuclear,
hasArmSeeker, carriesLethalPayload)` takes the payload read as a parameter, so a suppression missile
that dispensed its damage the way the AGM-48 does can no longer be refused for the same wrong
reason.

**The evidence line now names the field that carried the damage:**

```
AGM-48 (cluster 12x <submunition> pierce 30/blast 10, A/G 0.64, rank 1), AGM-68 (warhead pierce 700/blast 120, A/G 0.81, rank 0)
```

and the alarm, if one is still wrong, reads `*** NO LETHAL PAYLOAD FOUND — if this store kills in
play, the rule is reading the wrong field … ***`.

**Self-checks.** Five new cases on the payload rule, driven with synthetic numbers: own warhead →
lethal; cluster of twelve with submunition damage → lethal; no warhead and no submunitions → not
lethal; a dispenser whose submunitions declare nothing → not lethal; a non-missile → never judged.
The suppression cases were rewritten against the new signature.

**The cluster case is checked on the pure rule, not through a Unity probe.** Building a probe that
carries a live `SubmunitionDispenser` means adding the component to a prefab, and its `Awake`
registers itself with a missile that does not exist — it would throw at plugin load. The arithmetic
lives in `CarriesLethalPayload`, and that is what the synthetic cluster case drives.

**Not verified in the running game.** The proof is the next reload's `CAS ordnance data:` line: the
AGM-48 should read a `cluster Nx …` payload and `rank 1`, the AGM-68 `warhead pierce 700/blast 120`
and `rank 0`, and neither should carry the alarm.

## Departure 16 — the recon round is excluded by name, and nothing else is excluded at all (2026-09-14)

**Why a name test, after three data-keyed rules failed.** The reported defect was the commander
hanging four Eyeball Mk.II sensor rounds on a suppression helicopter — a store that flies like a
weapon, is rated like a weapon, and destroys nothing. Three attempts to key the exclusion on the
game's data all refused a real weapon along with it:

1. **Departure 13 — the catalog's damage pair.** `WeaponInfo.pierceDamage` / `blastDamage`, the two
   numbers `Unit.TakeDamage` is handed. The installed build's roster line answered
   `AGM-48 (pierce 0, blast 0, A/G 0.64, rank -1)` beside `AGM-68 (pierce 700, blast 120)`. The
   AGM-48 is the user's preferred close-support missile and kills in play, so the rule had silently
   switched the ordnance preference off for it.
2. **Departure 15 — the submunitions.** The AGM-48's missile prefab carries a
   `SubmunitionDispenser` whose `submunitionType` is a `WeaponInfo` of its own, and that is what
   `Spawner.SpawnMissile` launches at each target. Reading it by reflection off the catalog mount
   found nothing: the next reload reported `AGM-48 (NO WARHEAD AND NO SUBMUNITIONS)`. The dispenser
   is presumably on a prefab the catalog entry does not reach, or its array is empty until spawn.
3. **The asset data itself.** There is no reconnaissance flag, weapon type, camera field or sensor
   component anywhere in `Assembly-CSharp` — the whole class list has no `Recon`, `Eyeball` or
   weapon-side `Camera` type. `WeaponInfo`'s other numeric fields are a kill probability, an
   armour-tier multiplier and a fuze height, none of them a damage amount; `DamageInfo`'s fire and
   impact channels have no declarative source on a store at all.

Every data-keyed rule available therefore disarms a real store in order to exclude one sensor round.
Excluding that one asset by its own designation does not. `ReconRoundDesignations` is a class-level
string table with exactly that reasoning written into its summary, so a second recon round is one
line and a future reader knows why this one rule breaks the "never key on the name" convention.

**What the rule is now.**

- **CAS, strike and "can this airframe attack ground"**: no payload test at all. Ranking is the A/G
  rating and the preferred-ordnance designation table, exactly as before any of this. The ONLY
  exclusion added is `IsReconRound` — a designation match against the recon table, over the same
  whole-identity string (`weaponName`, `shortName`, asset name, mount name, mount key) the
  preferred-ordnance table is matched against.
- **Suppression**: an `ARMSeeker` on the missile prefab AND not a recon round. No payload test.
  `IsAradCandidate(missile, nuclear, hasArmSeeker, reconRound)`.
- **The roster line stays as information**, with the alarm text removed: it prints the payload if the
  catalog exposes one (`warhead pierce 700/blast 120`, `cluster 12x …`) and `payload unknown`
  otherwise, plus `RECON ROUND — excluded` where the exclusion fired. Nothing decides anything on
  it; a reader asking why a store was picked deserves to see what the commander can and cannot see.
- **The memoised submunition reflection helper is kept**, because the information line still reads
  it. It is no longer on any decision path.

**Self-checks.** Five designation cases (the Eyeball by full mount name and by bare designation; the
real AGM-48 and the AGM-68 are not recon rounds; an unnamed store is not one), five suppression
cases against the new signature, and the CAS ordnance probes rebuilt to model the live data: the
AGM-48 probe now declares NO damage and a 0.64 ground rating, exactly as the roster reported, and
must still rank 1 and outrank a twenty-round rocket pod. The Eyeball probe carries the AGM-48's
designation and rack, is rated 0.9 against ground, and must score zero and rank -1 — everything
except its name says "ground attack", which is the whole reason the exclusion is keyed on the name.

**What this gives up.** A recon variant of some other store, added later under a different name,
would be treated as a weapon until its designation is added to the table. That is the cost of the
trade and it is the right way round: the failure mode is one useless store being picked, not the
commander's best missile being refused.

**Not verified in the running game.** The proof is the next reload's `CAS ordnance data:` line: the
AGM-48 should read `rank 1` and the AGM-68 `rank 0`, both without alarm text, and any Eyeball entry
should read `RECON ROUND — excluded`.
