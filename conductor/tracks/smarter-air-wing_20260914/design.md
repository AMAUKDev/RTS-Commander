# Design: Smarter air wing

**Date**: 2026-09-14 · **Track**: smarter-air-wing_20260914 · **Approved by user**: 2026-09-14
**Depends on**: air-support-tasking_20260913 (sortie table, demand, ownership), commander-priorities_20260914
(the ladder that funds it), heli-picket-insertion_20260913 (pads and rotary launches).

## Problem (user's words)

"We need a strong preference for AGM-68 and AGM-48 ordnance in CAS missions. … We should by default
have helicopter CAS at front-lines, we should generally be forming up aircraft and attacking with
multiple at the same time rather than 1-by-1 (even if you order lots at once, they will take off
one-at-a-time due to runway restrictions), we should have AWACS back from the front-line, we should
have ARAD strikes on concentrated enemy air defences." Also: airframes sitting in the AIR window's
"Idle" category.

Today: the sortie table (`Operations/CommanderOperationsAir.cs`) binds each airframe the moment it
launches and sends it straight to the objective, so a four-aircraft CAS demand arrives as four
singletons minutes apart; CAS loadouts come from the generic per-mode scorer with no ordnance
preference; attack helicopters are classed Strike and compete with jets on price; there is no
AWACS or ARAD sortie kind; and a commander-owned airframe that loses its sortie can sit with no
mission.

## Decisions (user, 2026-09-14)

1. Packages **form up at an orbit** until the whole package is airborne, bounded by a wait, then go
   in together (not parallel multi-base launches).
2. Helicopter CAS is chosen **by mission kind**: helicopters cover forward bases, pickets and
   platoons in contact; jets fly attack sorties and pre-emptive cover.
3. AGM-68 and AGM-48 are strongly preferred CAS ordnance.
4. One AWACS per commander behind the front; ARAD sorties against concentrated enemy air defence.
5. No commander-owned airframe may idle without a mission.

## Reuse

- Sortie table, demand, binding, ownership, claim on registration, loss cooldown, `air=[…]`
  segment: `Operations/CommanderOperationsAir.cs`.
- Loadout scoring per mode and weapon naming: `AirCommand/CommanderAirCommandLoadout.cs`
  (`ScoreMount`, `BuildWeaponOptions`, `GetWeaponTypeKey`, `WithoutInternalCannons`),
  `AirCommand/CommanderAirCommandScoring.cs` (anti-radar test, ARH test, `CanAiFly`).
- Air Command modes and pilot hooks (station keeping, CAS area filter, RTB, rotary landing):
  `AirCommand/CommanderAirCommandTypes.cs`, `CommanderAirCommandPilotHooks.cs`,
  `CommanderAirCommandService.cs` (`IsRotaryPilot`, `HasVerticalPad`).
- Front geometry: `Operations/CommanderOperationsFront.cs` (front/rear classification,
  `NearestHeldAssetDistance`), threat marks; enemy air-defence tracking via the `CountObserved`
  walk with `IsAirDefence`.
- Buy path and ladder allocation: `Ai/CommanderEnemyCommanderAir.cs` (`BuyAirframe`, role
  capability tests), `Ai/CommanderEnemyCommanderLadder.cs` (rung 2 air share).
- Not reused: a separate wing-planner service (would duplicate demand and ownership bookkeeping —
  every new kind is a sortie kind in the existing table).

## Section 1 — CAS ordnance preference

The strike/CAS scorer adds a `PreferredCasOrdnance` bonus for mounts whose weapon key is
`AGM-68` or `AGM-48` (matched on the weapon name the AIR window prints; the two names are a
class-level string table with a `<summary>`), large enough to outrank any other air-to-ground
store but not to override hardpoint compatibility or strip acceptance. Applies wherever the
commander builds a CAS loadout (buy, relaunch recipe, retask). Roster line gains `CAS ordnance:
AGM-68/AGM-48 available` or `none — falls back to <best A/G>`.

## Section 2 — Helicopter CAS by mission kind

Sortie kinds ForwardBase-cover, Picket-cover and Contact request `Rotary` CAS: candidate
airframes are attack helicopters (rotary pilot, A/G-capable by loadout), launched from any pad or
strip that accepts them within `RotaryCasRangeMeters` (40 km) of the objective. Attack and
Pre-emptive sorties request jets. When no rotary can launch in range the sortie falls back to a
jet with a once-per-objective log line. Helicopter CAS loadouts use the same ordnance preference.

> **Amended 2026-09-14 (user instruction, plan.md Departure 10).** The setting is now
> `HeliCasRangeMeters` and its default is 90 km: at 40 km most objectives on an 80 km map had no pad
> in range at all. The fallback line is printed EVERY time it happens, with the distance to the
> nearest pad, not once per objective.

## Section 3 — Packages

- A sortie with wanted > 1 (or any escort) is a **package**. Each launched airframe is bound and
  sent to the sortie's `FormUpPoint`: 12 km from the launching base toward the objective, on the
  friendly side (clamped to at least `PreemptiveAirRangeMeters` from the nearest tracked hostile).
  It holds a `AirGuard`-style orbit there.
- The package **goes in** when every wanted CAS airframe and the escort are airborne and within
  3 km of the form-up point, or when `PackageFormUpSeconds` (180) has elapsed since the first
  airframe reached it — whichever first. Going in = retasking all bound airframes to the objective
  in the same review. Reinforcements arriving after go-in join directly.
- The ground attack's go-in hold (already bounded by its 240 s form-up timeout) keeps waiting for
  "CAS on station", which now means the package has gone in and its first airframe is inside the
  objective ring.
- Log: `<label>: package forming 2/3 at the form-up point`, `<label>: package goes in (3
  airframes, escort up)`, `<label>: package goes in after 180 s wait (2/3)`.

## Section 4 — AWACS

- One `Awacs` sortie per commander whenever the roster has an airframe carrying the game's radar
  special system (`SpecialAirSystem.Radar`) that `CanAiFly`. Flies `AirGuard` mode at the station
  with the radar loadout; never lent to a sortie and never stripped for another sortie. Replaced
  after loss with the normal loss cooldown; ladder rung 2, immediately after the CAP baseline and
  before CAS demand.
- Log: `tasks <airframe> with radar watch 15 km from <base> toward the front`, `AWACS` entry in
  `air=[…]`.

**Departure 2026-09-14 — the hard limit and the station** (user report, far-start map: "it seems to
have bought 2x AWACS and sent them to the middle of the map. I think we should have a hard limit on
one AWACS and it stays near the airbase"). Three things changed; the rest of this section stands.

- **Wanted is a limit, not a constant.** It was `1`, which meant "one BOUND to this sortie". A radar
  airframe that unbound itself reopened the slot at once and a second was bought while the first was
  still airborne. It is now `AwacsWanted(CountOwnedRadarAirframes(hq, state))` — one per commander,
  counting every owned radar airframe alive in ANY state (on station, outbound, returning, on the
  deck), by the same `FillsAirRole(…, AirRole.Awacs)` capability test the sortie fill binds on. A
  replacement is bought only once the airframe is gone, and then only after `AwacsLossCooldown`.
- **The out-of-ammo RTB no longer fires on a radar airframe.** The seed in `ChooseMissionTarget`
  read "out of ammunition" for any mission whose mode is not `AwacsJammer`, and the operations AWACS
  flies `AirGuard`, so a radar aeroplane with no shootable store counted as empty the moment it
  reached its orbit: `Returning` was set, `PruneSortieAirframes` unbound it, and the slot reopened.
  `CommanderAirCommandService.MissionIsOutOfAmmo(carriesEligibleStore, anyLoadedStore, radarWatch)`
  now answers it: an airframe with NO usable store FITTED for its mode was never armed and is never
  empty. A strike airframe with empty racks is unaffected — its racks exist.
- **The station is measured from the BASE, not the front.** 30 km behind the centre of the front put
  the orbit in the middle of a 69 km-apart map. It is now the main airbase offset
  `AwacsBaseOffsetMeters` (15 km) toward the centre of the front, or toward the nearest asset another
  faction holds when no front has formed, or the airbase itself with neither. Inside the base's own
  air defence, and an airborne radar still reaches 150 km+ from there. `AwacsOrbitRadiusMeters`
  stays 20 km. `AwacsStandoffMeters(hasAim, baseToAimMeters)` keeps its clamp: the station never
  passes the thing it is aimed at.
- **The radar airframe is never retasked off radar watch** (user decision 2026-09-14: "AWACS aircraft
  should never be retasked from being AWACS"). One predicate pair — `IsAwacsAirframe(hq, aircraft)`
  (the same `FillsAirRole(…, AirRole.Awacs)` capability test the fill and the count use) and
  `IsAwacsSortie(sortie)` — feeds one pure rule, `MayBindToSortie(radarAirframe, awacsSortie)`,
  which holds BOTH ways: the radar airframe flies the radar watch and nothing else, and nothing
  without the pod is ever put on the radar watch. Wired into every reassignment path:
  `TryRetaskOne` (the AWACS sortie is never a retask target) and `MayTakeForContact` (a radar
  airframe is never a retask source), `TakeUnboundOwned` (the fill's selection),
  `TakeLendableHomeCapFighter` (home-CAP lending), the idle sweep's patrol test and its refusal
  wording, `IssueHomeCapTask` and `IssuePostureTask` (both patrol doors), and
  `NotifyAircraftRegistered` — a radar airframe registering is bound to the radar watch or to
  nothing, never parked on the home CAP. `BindCas` and `BindCap` now return `bool` and carry the
  backstop gate `MayBindAirframe`, which says the refusal out loud
  (`refuses to retask <airframe>: AWACS stays on radar watch`); the fill loop breaks on a refusal
  rather than offering the same airframe again.
- Self-checks (in `CheckAirSupport`): six on `AwacsWanted` and the limit constant, four on the new
  station geometry, four on `MissionIsOutOfAmmo`, four on `MayBindToSortie` (both directions, both
  ways round) and two on the idle sweep's radar wording.

## Section 5 — ARAD

- Each review, cluster tracked hostile air-defence vehicles (`IsAirDefence`, `ThreatMemorySeconds`
  freshness) with a 5 km link distance. Any cluster of `AradClusterMinimum` (3) or more within
  `ObservedRadiusMeters` of an active objective (attack target, contested point, forward base
  under threat, or a package's objective) opens an `Arad` sortie over the cluster centroid,
  wanted 1 for 3–5 vehicles, 2 for 6+.
- Candidates: airframes with an anti-radiation loadout option (existing anti-radar test);
  loadout picked with the ARAD mode scorer. Priority: below CAP and AWACS, above that objective's
  CAS; a CAS package for the same objective waits at form-up until the ARAD sortie has gone in or
  the cluster dissolves (bounded by the package wait).
- Log: `opens ARAD over <label>: 4 air-defence vehicles clustered`, `ARAD <label>` in `air=[…]`.

## Section 6 — Idle sweep

Each review, every commander-owned airframe with no Air Command mission (`IsOnAnyMission` false)
that is airborne is retasked: fighters and A/A-capable to home CAP, transports and anything
Winchester to RTB, the rest to home CAP. One log line per airframe: `<airframe> was idle; tasked
<mission>`. The AIR window's Idle list then shows only the player's own aircraft.

## Settings and constants

Settings (`Operations`): `PackageFormUpSeconds` 180, `HeliCasRangeMeters` 90000 (was `RotaryCasRangeMeters` 40000; renamed and raised 2026-09-14),
`AradClusterMinimum` 3. Constants with `<summary>`: `PreferredCasOrdnance` table, form-up
distance 12 km, form-up arrival 3 km, AWACS stand-off 30 km / 20 km, ARAD link distance 5 km.

## Out of scope

Parallel multi-base launches; naval; player-side tasking of the player's own aircraft; changing
the CAS ladder sizes; new Harmony patches (all behaviour via existing mission modes).

## Verification

Self-checks: ordnance ranking (AGM-68 > AGM-48 > other A/G > none), rotary-by-kind table, package
readiness (all airborne and close → go; timeout → go; otherwise hold), AWACS station geometry
(30 km behind, home side; fallback), cluster rule (2 → none, 3 → one sortie, 6 → two), idle sweep
rule. In game: a CAS launch line naming an AGM-68 or AGM-48 loadout; `package forming` then
`package goes in` with two or more airframes arriving together; a Chicane launched for a forward
base and a jet for an attack; `tasks … as AWACS` once per side with its orbit behind the front;
`opens ARAD` when three SAMs sit together near an attack target; the Idle list empty of
commander aircraft.

---

## Addendum — two additions asked for after approval (user, 2026-09-14)

### Section 7 — Platoon-requested CAP

The counterpart to the home CAP's cap of 4 (`HomeCapMax`). Any platoon in a live state
(Moving, Attacking or Holding) and any ForwardBase or Picket mission point with tracked hostile
**aircraft** inside `ObservedRadiusMeters` opens a CAP sortie over itself, even when nothing else
makes it an active objective. Size: one fighter per two tracked hostile aircraft, minimum 1, capped
at 3 — the same growth term as `WantedHomeCap` with no baseline (one definition, two callers).
Served in rung 2 immediately after AWACS and before ARAD and CAS, so a raid over a marching platoon
is answered by the wing rather than only by the home patrol.

Log: `CAP <platoon>` in `air=[…]`; the ordinary escort/CAP tasking lines otherwise.

### Section 8 — The last-resort bug

Observed (`BepInEx\LogOutput.log`, lines 3806-3809 of the 2026-09-14 match): the player-side
commander launched a T/A-30 Compass (22) and then a CI-22 Cricket (12) for the home CAP in the same
review, with the rung's slice reading `spent CAP 33 … saved 10`.

Cause: `BuyHomeCapFighter` applies the `definition.value > budget` filter **before** the
last-resort preference in `BetterCapCandidate`, so the LAST RESORT rule only ranks airframes that
are affordable out of this review's **remaining** slice. Rung 1 buys up to three fighters per review
out of one pot; after the first Compass the remaining slice (≈21) no longer covered a second Compass
(22) but did cover the Cricket (12), so the Cricket became the only candidate and the rule never
ran. `TryBuyRole` has the same weakness in a different shape: its first pass excludes the
last-resort airframe and its second admits it, but the first pass fails on *unaffordable* as well as
on *absent*, so the second pass buys the Cricket whenever the ordinary airframe is momentarily out
of budget.

Decision: the last-resort airframe is admissible only when the roster has **no** ordinary airframe
that can fill the role from a held strip **at any price**. An ordinary candidate the review's slice
cannot afford is still a candidate — the commander saves for it and buys nothing this review. One
pure rule (`LastResortAllowed`) read by both buyers, with self-check cases for the observed case.

### Section 9 — Lending the home CAP forward (user, 2026-09-14)

User's words: "Base CAP can be far behind the front-line being useless - we need a mechanism where
base CAP that's not under threat can be re-tasked to support platoons etc (but leaving a minimum of
1 at the base)."

Home-CAP fighters above `HomeCapMinimumHeld` (1) become **lendable** while the base is quiet: no
hostile aircraft tracked inside `HomeCapThreatRadiusMeters` (30 km) of any airbase this commander
holds, for `HomeCapQuietSeconds` (120 — four operations reviews, so one lost track cannot empty the
base). Lent fighters fill CAP-type demand only (a platoon-requested CAP, an objective's CAP wing, a
package's escort slot), nearest demand first.

A lent fighter stays **owned and still counted**: it remains in the home-CAP set, so
`CountHomeCapFighters` and therefore the ladder's shortfall are unchanged and lending can never look
like a loss and trigger a rebuy. The `ladder:` line reports the loan — `CAP 4/4 (2 base +2 air
+0 losses, 2 lent)`.

Recall is immediate: the moment a hostile aircraft is tracked inside the base ring again — or the
lent count rises above what the minimum allows, after a loss — the fighters leave their sorties and
go back to the home patrol. Their sortie's demand reopens and is bought the ordinary way.

Log: `lends <fighter> from home CAP to <sortie label> (base quiet 3 min)` and
`recalls <fighter> to home CAP: hostile air near the base`.

Verification: lendable count is alive minus the minimum and never negative; the quiet timer's
boundary; and a shortfall computed with fighters lent is the same shortfall as with none, so a
recall while short cannot double-buy.

### Section 10 — Contact outranks cover (user, 2026-09-14)

User's words: "air tasks / packages need to be retasked to requests that are in-contact with the
enemy, if their current task is not in-contact with the enemy."

Each review, after the demand is rebuilt and the packages resolved, every sortie whose objective is
in contact and is short of airframes may take one from a sortie that is not. **In contact** means the
platoon or point is being shot at (a live `InContactUntil` or mission `ContactUntil`), the attack has
gone in, or anything hostile is tracked in the objective's ring. Cover over empty ground is not.

Sources are taken in this order:

1. A package still waiting at its form-up point — its airframes are airborne, together and have
   begun nothing, so moving one costs nothing at all.
2. A package that has gone in over a quiet objective — it is working, but on nothing.
3. Pre-emptive cover over a platoon not yet in contact — last, because being already there when the
   shooting starts is the whole point of that sortie.

Within a source rank the airframe nearest the target moves first. The AWACS is never taken, nor a
suppression sortie that has gone in. Capability decides which slot an airframe can fill: a CAS slot
takes a ground-attack-capable airframe, a CAP or escort slot an air-to-air-capable one, and a
suppression slot one that can carry anti-radiation weapons. A home-CAP fighter out on loan may move
on only while the base still holds its minimum patrol (Section 9).

A moved airframe is left alone for `RetaskHoldSeconds` (90 — three reviews). Without it two fights
that are both short would take the same aeroplane off each other every review and it would spend the
match in transit between them. The quiet sortie's demand simply reopens and is refilled or bought the
ordinary way.

Log: `retasks <airframe> from <quiet label> to <contact label>: contact outranks cover`, and
`retask` on the receiving sortie's `air=[…]` entry for that review.

Verification: the in-contact test, the source ordering, the hold's boundary, the capability match,
and that a lent home-CAP fighter is never moved on while the base is at its minimum.

### Section 11 — Markers for sorties and their elements (user, 2026-09-14)

User's words: "can we add markers to packages (and its elements) like we do with platoons?"

A new partial, `Operations/CommanderOperationsAirMarkers.cs`, drawn from the existing `DrawMarkers`
with one additive call. It reuses the platoon marker's own `DrawDotMarker`,
`CommanderUiTheme.DrawWorldLabel`, world-vs-map split, fullscreen-map rule and
`ShouldRetainCommanderMarker` tracking test unchanged — an enemy airframe is marked only where the
player's side actually tracks it.

**One marker per sortie**, at its lead airframe (the first bound one still flying) or at the form-up
point while the package is gathering and nothing has reached it yet. Text:
`<KIND> <label> n/m — <phase>[ · flags]`.

- Kinds: `PKG` for any sortie of more than one airframe, else `CAS`, `ESCORT` (fighters over an
  objective with no strike of its own), `CAP` (a platoon's own request), `AWACS`, `ARAD`.
- Phases, most specific first: `Lent from home CAP`, `Retasked` (airframe facts, only on element
  markers), `Holding for ARAD`, `Holding for escort`, `Forming at form-up (2/3)`, `On station`,
  `Going in`.
- Flags: `+esc`, `pre`, `rotary`.

**One smaller marker per element**, reading `<aircraft> — <role>` with the role as `CAS`, `escort`,
`ARAD` or `radar`, so the package can be watched converging on its form-up point. A fighter out on
loan reads `<aircraft> — lent to <sortie>` instead.

**The standing home patrol** is marked at its first fighter not out on loan:
`HOME CAP <base> n/m[ — N lent forward]`.

Departures recorded at implementation: the lead airframe carries the sortie marker and not a second
element marker of its own, since two labels on one aeroplane read as two aeroplanes; and the form-up
fallback is drawn for the local faction only, because an enemy package gathering there has no tracked
unit to justify a marker.

Verification: the pure label builder — every kind, every phase, the flag order, the element roles and
the n/m arithmetic.

### Section 12 — A marker on every aircraft (user, 2026-09-14)

User's words: "seeing a lot of planes flying around without markers on them. i want to know what
EVERY plane is up to."

Section 11 marked sorties and their members. This marks the rest: every live commander-owned airframe
now carries a label, and an airframe that fits no category is drawn in amber as `UNTASKED <aircraft>`
so a gap in the bookkeeping is visible rather than silent.

Classification, in precedence order: a picket insertion in progress, then anything already going
home, then the standing patrol, then an idle transport, then anything carrying no mission at all, and
last the unaccounted case. Texts: `INSERTION <point> — outbound / unloading / returning`,
`RTB <base> — Winchester / recovered soon`, `HOME CAP <base> — n/m` on the patrol's lead and
`HOME CAP <base>` on the rest, `TRANSPORT <place>`, `IDLE — retasking`, `UNTASKED <aircraft>`.

An opposing commander's aircraft are named only as far as the player could work it out by looking:
`ENEMY CAS <label>`, `ENEMY CAP`, `ENEMY AWACS`, `ENEMY ARAD`, `ENEMY TRANSPORT`, `ENEMY AIRCRAFT` —
never counts, phases or flags, which are that commander's plan. The tracking test is unchanged.

A once-per-review line behind `OperationsDebugLog` counts the walk:
`air markers: 14 owned, 14 drawn, 0 untasked`, so a mismatch is greppable from a match log.

Verification: every classification state maps to a non-empty label, and every enemy kind maps to its
own text.

### Section 13 — Labelling the aircraft the commander does not own (user, 2026-09-14)

User's words, after a restart on the Section 12 build: "no label on aircraft still". The diagnostics
line showed the owned walk working (`air markers: 4 owned, 4 drawn, 0 untasked`), so the unlabelled
aeroplanes were the ones that walk never covers.

A second walk over `hq.factionUnits` names everything else:

- **Ours, on a mission the player set in the AIR window** → `AIR CMD <mode> — ACTIVE|RTB[ · AUTO]`,
  friendly colour.
- **Ours, with a human in it** → nothing. The player knows what they are flying.
- **Ours, AI, nothing steering it** (free or mission-authored) → `GAME AI <aircraft>` in amber.
- **An opposing faction's, tracked** → `ENEMY AIRCRAFT <aircraft>`. The tracking test is unchanged;
  an untracked one is still never drawn.

The diagnostics line gains the count: `air markers: 4 owned, 4 drawn, 0 untasked, 6 other drawn`.

Marker size: `MarkerElementWorldSize` rises from 18 to the platoon marker's 30, and the map size to
match. The label's FONT was never the difference — `CommanderUiTheme.DrawWorldLabel` has one fixed
style, so an aircraft's text has always been exactly as large as a platoon's; the size constant only
moves the dot and the label's offset above it, and a small dot sits inside the aeroplane's own
silhouette at the ranges aircraft are seen at.

Known limitation recorded at implementation: both walks run inside the per-commander loop, so a
faction with no commander managing it (the player commander switch off) still gets no labels.

Verification: every classification case produces text, the skip cases produce none, and the aircraft
marker is asserted no smaller than a platoon's.

### Section 14 — Labels from the first frame (user, 2026-09-14)

User's words: "labels seem to only activate a minute or so after mission start".

Both aircraft walks lived inside the per-commander loop over the operations states, and a state is
only created after point discovery and that commander's first 30-second review — and again after
every hot reload. So for the opening minute of every mission nothing in the sky was named.

The walk that needs no state now runs first, straight off `CommanderGameAccess.GetLocalHq()` and
`FactionRegistry.GetAllHQs()`, and labels every aircraft the commander does not own: our own AIR
window missions, free AI aircraft, and tracked hostile aircraft. Human-flown aeroplanes are still
skipped. The per-commander walk runs afterwards as before and gives the commander's own airframes
their specific labels.

One aircraft, one label: the state-less walk skips anything the commander owns (`LabelsInStatelessWalk`)
and records what it drew in a per-frame set, so the two walks can never both name the same aeroplane.
Platoon markers stay state-bound — a platoon has nothing to say before its commander exists.

This supersedes the state-bound "other aircraft" walk added in Section 13, which is removed: keeping
both would have labelled every non-owned aircraft twice.

Proof line, printed before any `Ops … review:` line and again whenever the count changes:
`Air markers ready: 6 of ours, 3 tracked hostile — labelled without waiting for a commander review.`

### Section 15 — Adopting the strays a hot reload orphans (user, 2026-09-14)

User's report: after a reload every aeroplane read `GAME AI T/A-30 Compass` instead of its tasking.

The label was correct. A hot reload rebuilds this service from nothing, so the ownership set is empty
and the Air Command mission table only restores the player's own recipe-launched missions. Every
airframe the commander had bought became an unowned, mission-less AI aeroplane doing nothing.

The idle sweep now adopts them first. An aircraft of a commanded faction is taken back when it has an
AI pilot, no Air Command mission and no owner — and when one of two things proves it is not a mission
author's own free aircraft:

- the duel, where automatic AI aircraft are off and every airframe in the sky was bought; or
- it registered into the faction **after** a commander was already running, recorded from the
  existing `RegisterFactionUnit` postfix.

Adopted aircraft join the owned set and are then tiered and tasked by the sweep exactly like any
other idle airframe of ours, so a ground-attack aeroplane still never ends up on the patrol. Each
says so once: `adopts <aircraft> (stray after reload); tasked on the home CAP.`

The `GAME AI` label then means what it says: an aircraft the mission author put in the sky.

Known limitation recorded at implementation: on a **stock** mission, aircraft bought before a hot
reload are not re-adopted, because the registration record is wiped with everything else and nothing
then distinguishes them from the mission's own free aircraft. They are adopted on the duel, and any
aircraft bought after the reload is adopted everywhere. Making this work on stock maps across a
reload needs the registration stamp persisted through `CommanderStateStore`, which is a separate job.

### Section 16 — Say where the aeroplane actually is (user, 2026-09-14)

User's words: "seeing aircraft on the runway saying 'On station' while waiting to take-off... that's
not quite right!"

The marker printed the SORTIE's phase, which is true of the sortie and not of the aeroplane. Where an
airframe physically is now outranks it, in this order:

| Situation | Text |
|---|---|
| On the ground, not returning | `Taxiing / awaiting take-off` |
| On the ground after a return | `On deck <base>` |
| Airborne and going home | `Landing <base>` |
| Airborne, within 3 km of a held base | `Departing <base>` |
| Airborne, past the arrival ring from its station | `En route to <label>` |
| Inside the ring it was sent to | the sortie's own phase, as before |

"On the ground" is the recovery code's own `IsOnDeck` — radar altitude below the parked threshold and
either stopped or in the taxi state — widened to internal so the label and the recovery can never
disagree about what being on the ground means.

The same override applies to the sortie marker (through its lead airframe), to every element marker,
and to the home patrol, AWACS, ARAD, lent and adopted labels, which gain the phase behind a `·`.

Verification: the resolver's precedence and every boundary — on deck, returning, the 3 km departure
ring's edge, the arrival ring's edge, and a commander with no base to measure from.

### Section 17 — Hands off another service's aircraft (user, 2026-09-14)

User's question: "haven't seen any successful transport picket insertions yet... are they getting
intercepted and retasked (to land) by the Air Commander?" Yes. The log line was
`UH-90 Ibis was idle; sent home (a transport with nothing to deliver)`.

Supply runs and picket insertions fly under the cargo service's own mission record and the insertion
book. Neither is an Air Command mission, so every `IsOnAnyMission` test the air wing makes reads
false for one, and an insertion halfway to its landing zone looked exactly like an idle transport.

One door, `IsUnderOtherService`, consulted by all six places the air wing touches an airframe: the
registration claim, the sortie fill, the idle sweep, the stray adoption, the retask to contact and
the home-CAP loan. Behind it sits `CommanderSupplyHeliService.IsOnSupplyRun` (widened to internal)
plus the insertion book.

The pending half matters as much as the assigned half: a transport exists from the moment it
registers, and the cargo service matches it to its run inside the same `RegisterFactionUnit` postfix
the air wing's claim runs in, so whichever postfix ran first would otherwise win the aeroplane.

Log, once per aircraft behind the debug flag:
`leaves UH-90 Ibis alone: flying an insertion to HILLTOP 12.`

The marker side already reads correctly: an insertion helicopter has a record in `state.Insertions`,
so it labels as `INSERTION <point> — outbound / unloading / returning` and never as `GAME AI` or
`IDLE`.

Verification: the predicate, on both halves and neither.

### Section 18 — A package says what it actually has (user, 2026-09-14)

The marker `CAS CROSSROADS 20 0/3 — Going in · +esc rotary` appeared while the only airframe bound
to that sortie was its escort — a T/A-30 Compass flying CAP — and the strike slots read `0/4`.
Nothing was going in; there was nothing to go in with.

**Decision.** The sortie marker reads from what is BOUND, not from the phase the sortie believes it
is in.

- A sortie that wants strike airframes, has none bound, and has at least one escort bound reads
  `Escort only, awaiting strike`. It sits below `Lent from home CAP`, `Retasked` and
  `Holding for ARAD` in the phase precedence (those three are more specific about the same
  situation) and above everything else, so it overrides `Forming at form-up`, `Going in` and
  `On station`.
- The escort flag carries its counts: `· CAP 1/3` rather than the bare `· +esc`. A package reading
  `0/4 — Escort only, awaiting strike` has to be able to say how much escort it actually has, and
  the bare flag was the half of the old label that hid it. The flag is suppressed on a sortie whose
  own counts already ARE its fighters — a platoon's own CAP request, an escort over a strike-less
  objective — so a marker never prints the same pair twice.
- The whole line for the reported case is now
  `CAS CROSSROADS 20 0/4 — Escort only, awaiting strike · CAP 1/3`, pinned by a self-check that
  builds it from the three pure pieces.

**Where.** `Operations/CommanderOperationsAirMarkers.cs` — `AirMarkerPhase` (new `escortOnly`
parameter), `AirMarkerFlags` (counts instead of a bool), `SortieMarkerLabel` (the two call sites),
and `CheckAirMarkerLabels`.

### Section 19 — Commander aircraft fly with an empty cannon (user, 2026-09-14)

> "aircraft spawned need to have 0 internal cannon rounds, otherwise they go suicidal and try and
> use their cannons in heavily contested airspace."

#### How the game stores the cannon and its ammunition — VERIFIED in `Assembly-CSharp.dll`

Decompiled with `ilspycmd` on 2026-09-14. Everything in this subsection was read from the
decompiled source, not inferred.

- `Loadout.weapons` is a positional list of `WeaponMount`, one entry per `HardpointSet`. A null
  entry means that hardpoint set carries nothing.
- `WeaponMount` is a `ScriptableObject` with an `ammo` field and a `GunAmmo` flag. It is a shared
  ASSET: the same object backs every aircraft in the game that can carry that store. **Writing to
  `WeaponMount.ammo` would take the rounds off the player's aircraft and off every future spawn for
  the rest of the session.** Nothing in this mod may do it.
- `WeaponInfo.gun` is the flag that says a store is a gun.
- The internal cannon is a `Hardpoint.BuiltInWeapons` entry — a `Gun` component already on the
  airframe. `Hardpoint.SpawnMount(aircraft, mount)` calls `gun.LoadAmmunition(mount)` to fill it and
  `aircraft.weaponManager.RegisterWeapon(...)` to give it a `WeaponStation`.
  `Hardpoint.RemoveMount()` calls `gun.LoadAmmunition(null)`, which sets `magazines = 0` and
  `Weapon.ammo = 0`.
- `WeaponManager.LoadHardpointSet(set, mount)` takes the `RemoveMounts` branch when the mount is
  null and `SpawnMounts` otherwise. `SpawnMount` is the ONLY place a weapon is registered.
- `WeaponStation.AccountAmmo()` re-totals `Ammo` from each `Weapon.ammo`. `WeaponStation.Ready()`
  requires `Ammo > 0`, and `AIPilotCombatModes` aborts a gun run on
  `currentWeaponStation.Ammo <= 0`. Those are the only numbers the AI's gun runs read.

**So `WithoutInternalCannons` does more than drop a cosmetic entry**: a null entry means the built-in
gun is never registered, gets no weapon station, and cannot be selected or fired by the AI at all.

Two residuals, both verified and both harmless: `Gun.Awake()` loads one magazine when its
`startLoaded` flag is set, and `LoadAmmunition(null)` clears `magazines` and `ammo` but not
`bulletsLoaded`. Neither is reachable without a weapon station.

#### The gap that was actually letting cannons through

`CommanderAirCommandMissions.TryLaunchAiAircraft` fell back to
`weaponManager.SelectAIAircraftWeapons(airbase)` whenever the caller passed a null loadout — which
`LaunchBoughtAirframe` does for any airframe with no standard loadout. That method picks a random
legal mount for EVERY hardpoint set, the gun's included. `Aircraft` itself also substitutes
`definition.aircraftParameters.loadouts[1]` for a null or empty loadout.

#### Decision

- The cannon rule is applied at the choke point, `TryLaunchAiAircraft`, not trusted to each caller.
  It runs on the loadout going in AND on whatever `SelectAIAircraftWeapons` produced.
- The supply and naval cargo spawns do not go through that door, so they apply the same rule to
  their own loadouts. They build them empty, so there is nothing to remove today; the rule is there
  so a future cargo recipe carrying a gun pod cannot slip past.
- Belt and braces: `StripCannonAmmo(Aircraft)` empties any gun weapon station that exists anyway,
  through the game's own `Gun.LoadAmmunition(null)` plus `WeaponStation.AccountAmmo()`. It runs once
  right after each launch, and again over every commander-owned airframe on the review sweep
  (`PruneAirBook`) — which is what actually covers an airframe whose stations were still being built
  at spawn, one rearmed at a strip, or one adopted after a hot reload. It logs only when it removes
  rounds, so a quiet log means the loadout rule is doing its job.
- The launch line says so: `launched a A-19 Brawler (Strike) from Maris Airport for 36
  (cannon 0 rounds) (…)`. With INTERNAL CANNONS on it says `(cannon loaded: INTERNAL CANNONS is on)`.
  A loadout that still carries a gun mount at launch logs a named self-check failure.
- Self-check `CheckInternalCannonRule` drives the real `WithoutInternalCannons` with probe mounts:
  the gun goes, every other store stays, a gun-only airframe keeps its gun rather than flying
  unarmed, and an empty or absent loadout carries none.

### Section 20 — A package is one type, ordered together, from one base (user, 2026-09-14)

> "when building mission packages, aircraft should be of the same type. so if there's 2x CAS
> aircraft, both should be the same (for example) and ordered at the same time from the same
> location."

**Decision.**

- The demand walk now hands the chosen sortie back to the buyer, so the buy knows which ELEMENT it
  is filling and how much of it is empty: the escort slots on the CAP side, the strike, suppression
  and radar slots on the other.
- The type is chosen once for the whole element. The tier rule runs against the element's own share
  of the allocation (`budget / elementSize`), so the airframe picked is one the whole element can
  afford rather than one only the first slot can.
- The base is the accepting base NEAREST the objective. `FindAcceptingAirbase` gained that
  behaviour for a caller that passes a reference point with no range limit; the rotary pass, which
  passes both, is unchanged.
- The element is ordered whole or not at all: `PackageElementBuys(elementShort, unitPrice, budget,
  inContact)` returns the whole element when the allocation covers it, nothing when it does not, and
  whatever it can when the sortie is IN CONTACT — one aeroplane now beats none for a platoon being
  shot at, and the pinned type means the second one still matches.
- The chosen type and base are recorded on the sortie (`PackageStrikeType` / `PackageStrikeBase`,
  and `PackageCapType` / `PackageCapBase` for the escort, which may differ). A later top-up after a
  loss reuses them while `KeepsPackageChoice` holds: something is pinned, the airframe can still
  fill the role from a strip this commander holds, and the pinned base still accepts it. Any of those
  failing releases both fields together and the element re-picks.
- The pin is written from what actually LAUNCHED, not from what was chosen, so an order that put
  nothing in the air leaves the next review free.
- Logs: `orders 2x A-19 Brawler from Maris Airport for CROSSROADS 20 (package strike element)`, and
  each airframe's own launch line says `2 of 2 in the element`. The review line carries the type
  behind the counts: `CROSSROADS 20 CAP 1/1 FS-12 CAS 2/2 A-19`.
- Self-check `CheckPackageElements`: twelve element-sizing cases including the contact exception and
  a zero price, and five on type and base persistence.

**Bound.** `MaxAirBuysPerReview` still bounds how many BUY CALLS a review makes; one call may now
launch a whole element. The airborne ceiling is re-read before each airframe of the element.

## Section 12 — the pre-emptive demand cap (user report 2026-09-14, `Ground Control Duel Far`)

**The report.** "We have loads of requests for CAS from platoons and only a handful in the air."

**What the log showed.** Across the 43 reviews of the match, the player side's demand read
(`Ops Player commander (Boscali): air demand:`) ran from `CAP 0/4, CAS 2/3` early to
`CAP 0/28, CAS 2/10` at its peak, against a rung-2 grant of 1 to 163 and an air share of 40 % of
that. The wing could afford one or two airframes a review — 84 CAS sorties were launched in the
whole match, almost all SAH-46 Chicanes at 31 — so a demand list of fifteen to thirty objectives
meant every objective got a fraction of an aeroplane and none got cover. The CAP side was bound
`0/N` on **every single review of the match**: not one platoon-requested or point CAP was ever
filled.

**Where the list comes from.** `AddPreemptiveDemand` opens one sortie per platoon under way, and the
side had ten platoons marching at once. That is the term that scales with the size of the front
rather than with the fighting, and it is the term that was drowning the contact sorties.

**Decision (2026-09-14).** Pre-emptive cover is capped at
`CommanderSettings.OperationsMaxPreemptiveAirObjectives` (default 4) marching platoons, the ones
NEAREST the enemy first — the distance `PreemptiveEnemyDistance` already measures for the hold
clock. Contact sorties, attack sorties, points under attack and the AWACS are never capped by this:
the cap exists to stop the quiet half of the front outbidding the fighting half, not to ration the
fighting half.

- The measuring pass still runs over every platoon, because `PreemptiveAirUntil` is the platoon's
  hold clock and a platoon that stops marching must have it cleared whether or not it made the cut.
- `AlreadyDemanded` is re-read in the adding pass, not the measuring pass: two platoons of one
  mission would both have passed it before either had added anything.
- Log, on a change in the queued count only: `pre-emptive air cover is capped at the 4 marches
  nearest the enemy: 4 covered, 6 queued.`

**Not done, and why.** Pooling pre-emptive cover per attack AXIS rather than per platoon was
considered and left out: the axis is a property of an Attack mission, and the platoons that
dominate this list are `Moving@ForwardBase`, which have no axis. The nearest-first cap gets the same
concentration without inventing a grouping for platoons that are not grouped.

## Section 13 — the home CAP launches from the nearest base (fix 2026-09-14)

`BuyHomeCapFighter` asked `FindAcceptingAirbase(hq, definition)` with no reference point, which
returns the FIRST accepting base in `hq.GetAirbases()` order. Every one of the player side's nine
home-CAP FS-12 Revokers in the 2026-09-14 match launched from `airbase_boscali_north` while the
commander also held `airbase_city`, `highwaystrip2` and a captured field. The sortie buyer has
picked the nearest accepting base to its objective since 2026-09-14 (Section 11); this is the same
rule for the buy that has no objective, anchored on `CommanderCaptureService.GetTerritoryCenter(hq)`
— the middle of what the patrol exists to cover. Proof line: `launched a FS-12 Revoker (Fighter)
from <base>` should stop naming one base for every home-CAP buy.

## Section 14 — the marker says which air it wants and what it is getting (user request 2026-09-14)

**The problem.** A platoon, picket or forward-base marker carried one flag, `Requesting CAS`, set
whenever `sortie.Cas.Count < sortie.Wanted || sortie.Caps.Count < sortie.CapsWanted`. It could not
distinguish a platoon short of its escort from one short of ground attack, said nothing about
whether anything had been bought, and went silent altogether for a platoon the new pre-emptive cap
is holding back — which reads to the player as the wing ignoring them.

**Decision (2026-09-14).** The flag splits in two, escort before strike (the order the sortie itself
fills its slots in), and each carries its own fill state. `AirFlag(role, wanted, bound, onStation,
queued)` is the whole table and is pure:

| State | Reads |
|---|---|
| Nothing wanted | no flag |
| Held by the pre-emptive cap | `Requesting CAS (0/2 · queued)` |
| Asked, nothing bought | `Requesting CAS (0/2)` |
| Bought, still flying out | `Requesting CAS (1/2 · inbound)` |
| At least one airframe over the objective | `CAS overhead` |

`CAP` reads identically with its own counts. A marker can carry both:
`8TH PLATOON 6/6 — Attacking Dustbowl Highway Strip · In contact · Requesting CAP (1/2 · inbound) ·
Requesting CAS (2/2 · inbound)`.

- **"On station" is not a second opinion.** The loop that decided whether an attack may go in was
  moved out to `CommanderOperationsService.CountOnStation(sortie, bound)` — package gone in, airframe
  alive, inside `CasSortieRadiusMeters` — and the marker reads that same method (Reuse rule 3, moved
  not paraphrased). The player can never be told air is overhead by a rule the attack does not
  believe.
- **The queued counts are the real sizing.** `AddDemand`'s sizing arithmetic was cut out to
  `SizeSortie` and the cap's queued branch calls it, so the `2` in `(0/2 · queued)` is the number the
  platoon will actually get when its turn comes. A second copy of that arithmetic would have been
  free to drift. The counts live on the platoon (`QueuedCasWanted` / `QueuedCapWanted`) and are
  cleared every review before the cap runs, so a platoon that is served or stops marching goes quiet
  at once.
- **`MarkerFlags` takes the two flags as strings** rather than growing four more bools, and the
  picket marker passes its own pair through the same one definition it always did.
- **More bound than wanted is clamped**: a retask can leave a sortie holding more airframes than it
  asked for, and `2/1` would read to the player as a bug.
- Self-check `CheckAirFlags`: thirteen cases covering every combination for both roles, including
  the clamp and the queued-beats-inbound precedence, plus a `MarkerFlags` case proving escort is
  named before strike and both can stand at once.

## Section 15 — the radar airframe stops holding the fighter turn (fix 2026-09-14)

**The evidence.** In the `Ground Control Duel Far` match the player side's fighter demand was bound
`CAP 0/N` on **every one of the 43 reviews**, N running from 4 to 28. Not one platoon escort and not
one point patrol was bought all match. The reason is in the 51 denial lines: `[CAP] bought no
aircraft: its air budget is short of the cheapest radar-carrying airframe its strips accept, and it
saves for one rather than launching the last-resort airframe`. "Radar-carrying" is
`RoleCapabilityLabel(AirRole.Awacs)` — the denial is the AWACS, logged under the CAP label because
the radar airframe sits on the CAP side of the alternation.

**The mechanism.** `NextAirDemand` served the AWACS first whenever it was short, unconditionally.
The cheapest radar carrier on that roster is an EW-25 Medusa at 145; the wing's fund took 40 % of a
rung-2 grant of 1 to 163 and the ground-attack side spent whatever it held every review on a 31
Chicane. So the fund never passed 63 (`air saved 6-63 (cap 390)` on every ladder line), the AWACS
was never affordable, and the fighter demand behind it never got a turn. One aircraft the commander
could not buy blocked twenty-eight it could.

**Decision (2026-09-14).** On the fighter side's turn, the AWACS keeps its priority only when it can
actually be bought, or when standing aside would buy nothing:

```
if (awacsShort && (awacsAffordable || !capShort || !capAffordable)) -> Awacs
if (capShort)                                                      -> Cap
```

- **Affordable** means this review's air fund covers the cheapest airframe of that role the
  commander's own strips accept — `CheapestLaunchableValue`, which is `DearestLaunchableValue`
  generalised rather than forked (Reuse rule 5), so the turn order, the buy and the fund ceiling all
  read the same catalogue, the same capability gate and the same accepting-strip pair. The dead
  `cheapestInRole` local left over in `TryBuyRole` was removed with it, so there is one definition of
  "cheapest in role" and not two.
- **Only the buy passes the flags.** The "is anything open" and "what is the fund saving for" reads
  keep the `true` defaults: they ask what the wing WANTS, not what it can pay for this instant, and
  the fund ceiling must keep counting the AWACS or the fund could never grow to it.
- **A wing that can afford neither still saves.** Standing aside for a fighter that cannot be bought
  either would burn the turn and bank nothing — which is the same failure in the other direction.
- The AWACS stays one per commander with its loss cooldown; nothing about the sortie itself changed.
- Self-checks, six cases on `NextAirDemand`: unaffordable AWACS plus affordable fighter buys the
  fighter; affordable AWACS still outranks it; unaffordable AWACS with no fighter demand keeps
  saving; unaffordable both keeps saving; the ground-attack side never takes the turn from this
  path; and the ground-attack side's own turn is unaffected by either flag.

**The trade, stated plainly.** While fighter demand is open and affordable — which in a busy match is
most reviews — the AWACS is now bought late or not at all. That is the intended exchange: one radar
aircraft against every escort and patrol the ground asked for. If the next match shows the commander
never fielding a radar airframe at all, the answer is a reserved slice for it rather than a return to
the unconditional priority, because the unconditional priority is what produced `CAP 0/28`.

## Section 16 — the radar station stands 15 km clear of everything (user report 2026-09-14)

**The report.** "An AWACS was just tasked straight into the enemy and killed because the front-line
was close to the airbase. AWACS should never be tasked closer than 15 km to the enemy."

**The mechanism.** Section 4's station is the main airbase offset `AwacsBaseOffsetMeters` (15 km)
toward the centre of the front, with a 20 km orbit. The offset is measured from the base and clamped
only by the distance to the thing it is aimed at — it never looks at what is between them. On a map
where the front reaches the airbase, "15 km toward the front" is 15 km into the fight, and the 20 km
orbit puts the aircraft 35 km into it at the far edge.

**Decision (departure, 2026-09-14).** The design's offset becomes the CANDIDATE station. The station
actually flown is the nearest one to that candidate whose WHOLE orbit clears every hostile position
by `CommanderSettings.AwacsMinEnemyDistanceMeters` (15 km, new, `Operations` section) — so the
centre stands at least 15 km plus the orbit's own radius from all of them.

- **What counts as hostile:** every control point or base another live HQ holds
  (`TryNearestEnemyAsset`'s walk — base points carry every airbase and resolve their owner live), and
  the last known position of every tracked hostile ground vehicle and aircraft, on the same
  `ThreatMemorySeconds` freshness and the same skip of buildings the existing tracking walks use.
  Bounded at 64 entries, nearest the commander's own ground first, because the list is walked once
  per candidate station.
- **The remedies, in order** (`TryAwacsStation`, pure): slide back along the base-to-front line and on
  past the base away from the front, never further behind the base than the forward offset it
  started from; then, only if no offset on that line works at the full orbit, squeeze the orbit down
  to `AwacsMinOrbitRadiusMeters` (8 km) and no further; then give up. Sliding before squeezing,
  because a full-width orbit in the right place sees more than a pinched one in the wrong place.
- **Grounded** means the AWACS demand is not opened at all. Nothing is bought, and a radar aircraft
  already flying is released by the reconcile — where both patrol doors already refuse it, so it is
  sent home. One line per change: `AWACS grounded: no station 15 km clear of the enemy.`
- **The launch base.** The station is measured from the nearest held airbase to the territory centre
  that itself has nothing hostile within 15 km — `MainAirbase` generalised with a clearance and a
  threat list, behaviour-neutral at zero clearance (Reuse rule 5). The buy then picks the accepting
  strip nearest the station (`FindAcceptingAirbase` with `near`), so a threatened strip is passed
  over for the clear one the station was measured from.
  - **Known limit, stated plainly.** That is a strong preference, not a hard gate: the accepting-strip
    walk lives in `Ai/CommanderEnemyCommanderAir.cs`, outside this change's files, and it still picks
    purely by distance to the station. If the only strip that can spawn the radar airframe is the
    threatened one, the launch will still use it. A hard gate needs a clearance parameter threaded
    through `FindAcceptingAirbase`; it is deliberately not done here.
- **Re-evaluated every review.** The station is recomputed in `AddAwacsDemand`, `SameSortie` matches
  the AWACS on kind alone so the new centre replaces the old, and `SyncBoundAirframe` retasks past
  the existing 3 km hysteresis. A squeezed orbit reaches the aircraft too: the mission area's RADIUS
  is now compared as well as its centre, scoped to the AWACS kind because it is the only mission area
  whose radius moves on its own.
- **Self-checks** on `TryAwacsStation`: a clear candidate kept at the design's offset and full orbit;
  a blocked one slid back exactly far enough and no further; one no forward offset can save slid
  behind the base; the squeeze taken only at the end of the slide and only to its floor; ten metres
  inside the floor grounding the watch; an enemy on the airbase grounding it; an empty picture never
  moving the station.

## Section 17 — a released aircraft is offered elsewhere before it is sent home (team lead 2026-09-14)

**The report.** `tasks SAH-46 Chicane with CAS over HILLTOP 41`, then minutes later `releases SAH-46
Chicane: HILLTOP 41 no longer calls for air support`, after which the helicopter had no task.

**The mechanism.** `ReleaseSortie` put every released aircraft on the standing home patrol via
`IssueHomeCapTask`. The patrol is an air-superiority orbit; `MayHoldPatrol` refuses a helicopter, but
`IssueHomeCapTask` never consulted it, so the task was issued, the pilot could not fly it, and the
idle sweep then skipped the aircraft because it carried a mission. The design's own words — "released
airframes go to the next-hungry sortie (the fill pass below does that)" — were never true either: the
fill pass only ever takes aircraft no sortie holds, and a released one is offered to nothing.

**Decision (departure, 2026-09-14).** `ReleaseDisposal` (pure) is the order, and the release path is
the one place that acts on it:

1. **Retask.** The aircraft is offered to every other sortie in this review's demand, nearest first,
   matched on the same `FillsAirRole` capability test the fill and the buy use, skipping sorties
   standing down after a loss and honouring the radar rule both ways. A strike slot wins over an
   escort slot when it could take either. Bound through the existing `BindCas`/`BindCap`, stamped
   into `AirRetaskedAt` so the retask hold applies. Logged `retasks <aircraft> from <old> to <new>`.
2. **Home CAP**, if nothing wants it and `MayHoldPatrol` allows — which is the existing rule, now
   actually consulted on this path.
3. **Return to base** otherwise, logged `sends <aircraft> home: nothing calls for it`.

The release pass moved AFTER the match pass in `ReconcileSorties`: an aircraft offered to a surviving
sortie has to see how full that sortie really is, which is only true once every match has moved its
bindings across.

## Section 18 — why the objectives flap, and the minimum hold (team lead 2026-09-14)

**The finding.** Eight `no longer calls for air support` lines against five taskings in twenty
minutes is a SAMPLING mismatch, not a decision the plan is making badly.

- A platoon's `InContactUntil` and a picket or forward base's `ContactUntil` are both set to
  `Time.time + ContactHoldSeconds`, and `ContactHoldSeconds` is 20 s
  (`Operations/CommanderOperationsService.cs:548`).
- The air plan is rebuilt every `ReviewIntervalSeconds`, which is 30 s
  (`Operations/CommanderOperationsService.cs:26`). `BuildAirDemand` skips any platoon or mission whose
  clock has expired.
- The clock is refreshed only while a hostile is tracked inside `ContactRangeMeters` (2500 m) or a
  member was lost inside `LossContactSeconds`. Tracking decays and a 2500 m ring is tight, so the
  evidence blinks.

A 20 s memory sampled every 30 s cannot survive a blink: the objective has to have taken fire in the
20 s before the review tick or it reads as quiet, the sortie dissolves, and the next review re-opens
it. The CAS loss cooldown is a separate effect and marks the sortie `cooldown` rather than dissolving
it; it is not the cause here.

**Decision (departure, 2026-09-14).** Rather than lengthening the ground line's contact clock — which
would change how platoons deploy, a gameplay change this report does not license — the hysteresis goes
on the AIR side, where the cost is: `SortieMinHoldSeconds` (120 s, four reviews). A sortie whose
demand closed is kept until the hold runs out, provided it is holdable and actually holds aircraft.

- **Holdable** is a CAS objective or a platoon CAP — the two kinds that open and close on the tracking
  picture. The radar watch and an anti-radiation sortie are not: both close for a reason rather than a
  blink, and the radar watch's own close is now the safety grounding of Section 16, which must reach
  the aircraft at once.
- A held sortie asks for exactly what it already holds, so neither the fill nor the buy adds to it,
  and it reads as quiet — so `RetaskSourceRank` makes it the first place a fight in contact takes an
  aircraft from. It sits behind everything actually demanded in the sortie list. The review line marks
  it `held`.
- Self-checks on the boundary (a moment inside the hold keeps the wing, exactly at it lets go) and on
  which kinds are holdable.

## Section 19 — money-limited air buying, ARAD first, and the right missiles (departure 2026-09-14)

User instruction, verbatim: *"implement those recommendations - note that ARADs are not using the
correct ordinance, should be AGM-99 or AGM-68 etc"*. Four changes, all read off one `LogOutput.log`
from a `Ground Control Duel` match (commander Boscali).

**1. The buy loop is limited by money, not by a count.** The evidence: `air demand: CAP 0/13,
CAS 1/33, ceiling 13/20, budget 214` with `income 380/min … balance 601` climbing, and two to four
launches a review, never more. `MaxAirBuysPerReview = 3` (user decision 2026-09-13) is retired.
`AirBuyContinues` now takes the wing's money, the cheapest airframe an OPEN demand can launch, the
airborne count and the ceiling, and continues while the money covers the price and the sky has room.
`MaxAirBuysPerReviewSafety` (12) is a runaway guard, not a policy: the fund is itself capped at the
price of one airframe by `AirFundCeiling`, so twelve is unreachable in a real review. The price is
read by `ReadAirDemandPrices`, which is `AirFundCeiling`'s own role walk generalised to return both
ends of the range (Reuse rule 5) — so the saving rule and the spending rule can never disagree about
which airframes count. The rung-1 home-CAP loop keeps the one-argument overload; it was already
bounded by the shortfall and its own budget.

**2. The ground's unspent share goes to the wing.** The evidence, on the same reviews:
`holds: balance 491, unit budget 152, 7 vehicle types on offer (5 quiet reviews)`. When
`GroundBuyingBookOnly` holds the ground buyer and the book is empty and the wing has an open
request, `GroundShareGoesToWing` is true and the remaining rung-2 budget, less the naval ask priced
before the hand-over, is accrued into `state.AirFund` through the same `AccrueFund` the ordinary
share uses — so the fund's ceiling bounds it and what the ceiling refuses flows on to the rungs
below. Announced once per transition:
`hands the ground's unspent 152 to the wing: order book empty, 13 air requests open`.

**3. ARAD before rotary CAS.** The evidence: `loses an airframe over 1ST PLATOON: CAS stands down
for 4 min (13 hostile air-defence observed)`, repeatedly, with 31-value SAH-46 Chicanes fed into
seven to twenty-three observed launchers. `RotaryCasAllowed(observedAirDefence, aradFlown)` holds an
objective's helicopters while its ring shows `AirDefenceHesitationCount` (2) or more tracked
air-defence vehicles and no anti-radiation sortie has yet been on station over the belt. The
objective still gets CAS meanwhile — jets, which stand off — so the gate decides WHO flies it, never
whether it is flown. The count is recorded per sortie as `LastAirDefence` by `AddDemand`, using the
same `CountObservedAirDefence` walk the loss cooldown already reads. `AradFlown` is set by
`RefreshAradHolds` the moment an anti-radiation sortie near the objective stops being inbound —
one read, two answers: "still inbound" holds the CAS package at its form-up point, "over the belt"
releases the helicopters. The memory is carried across the review by the reconcile through
`AradMemorySurvives`, which drops it once the belt has thinned below the threshold, so a belt that
comes back is suppressed again. Review line: `CAS 0/4 held: ARAD first (13 AD)` in place of
`rotary`.

**4. A suppression sortie carries anti-radiation missiles, and only flies if it has them.** The
evidence: `tasks FS-12 Revoker with an anti-radiation strike over 1ST PLATOON (23 air-defence
vehicles clustered …)` followed by `launched a FS-12 Revoker (Fighter) … (Fighter tier, best
affordable — 7 in the sky)`. The aeroplane was bought through the FIGHTER role path and bound to the
suppression slot because `FillsAirRole` asks what the TYPE could carry. `FillsAirRoleNow` asks what
the aeroplane IS carrying (`CommanderAirCommandService.CarriesAntiRadiation`, over the live weapon
stations) and is read by all five binding paths: the claim, the fill, the retask to contact, the
released-aircraft retask and the unbound-aircraft search. The pure rule is `MayBindToRole`.

The loadout itself: `AutoConfigureAradLoadout` runs two passes. Every hardpoint group that can carry
a real anti-radiation missile takes one first; only then do the groups that cannot take the named
standoff stores, and one of those is skipped when selecting it would exclude a group that already
has a store — a single greedy pass in group order could have knocked a missile off the aeroplane
through the game's hardpoint exclusions. Before this, the suppression scorer counted anti-radiation
stores and nothing else, so every other pylon flew EMPTY, which is what the user saw.

**Departure from the user's wording, stated.** The user asked for AGM-99 or AGM-68. Checked against
`NuclearOption_Data/resources.assets`: the AGM-99 is described there as *"This air launched
anti-ship missile has a very low altitude cruise profile…"*, the AGM-68 as *"a large, optically
guided missile capable of destroying structures and heavily armored vehicles"*, and neither carries
an `ARMSeeker`. The game's anti-radiation missiles are the **ARAD-116** and the **ARAD-45**. Making
AGM-99 or AGM-68 the primary suppression store would have made every strike aircraft a suppression
candidate (the capability gate is `AradLoadoutScore > 0`) and sent sorties against radars with
nothing aboard that homes on one. They are therefore ranked FIRST AMONG THE SECONDARY stores
(`PreferredAradSecondaryOrdnance` = AGM-99, then AGM-68, then anything else on its ground score),
which is where the user's intent and the asset data agree: the suppression aeroplane now carries the
missiles that kill the radar and the standoff stores that kill the launcher. The launch line reports
the load as `anti-radiation loadout: ARAD-116 x4`.

**Self-checks.** Twenty-nine cases: the money-limited continuation (fund covers the price, fund one
short, fund exactly at it, the ceiling, one place under it, an unlaunchable demand, no demand at
all, the runaway guard); the hand-over gate (all four combinations); the ARAD-first gate (below the
count, at it, thick belt, suppression flown, belt thinned) and its memory rule; the binding rule
(`MayBindToRole`, four cases, plus a null aeroplane); and the ordnance order (`PreferredAradSecondaryRank`
and `ScoreAradMountForCommander`: missile beats AGM-99 beats AGM-68 beats a rocket pod, an
air-to-air missile scores nothing, and the reported load names the missile and its rack size).
