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
  special system (`SpecialAirSystem.Radar`) that `CanAiFly`; wanted 1. Station: 30 km behind the
  centre of the front (mean of front points and ForwardBase missions) along the line toward the
  commander's main base; if no front exists yet, over the main base at 20 km. Flies `AirGuard`
  mode at the station with the radar loadout; never lent to a sortie. Replaced after loss with the
  normal loss cooldown; ladder rung 2, immediately after the CAP baseline and before CAS demand.
- Log: `tasks <airframe> as AWACS 30 km behind the front`, `AWACS` entry in `air=[…]`.

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

Settings (`Operations`): `PackageFormUpSeconds` 180, `RotaryCasRangeMeters` 40000,
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
