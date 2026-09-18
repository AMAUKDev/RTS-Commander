# Air ceiling — live match observations (2026-09-18)

Automated monitoring of a running match, sampled every four minutes, for track
`air-ceiling_20260918`. The build under test carries the retuned ceiling (floor 16, maximum 24),
transports excluded from the count, the six-slot patrol reserve, and the three fixes the independent
review forced (the demand walk skipping closed patrols, a lift cover never asking for fewer escorts
than its floor, and transport escorts kept off the reassignment source list).

**Baseline to beat**, from the match of 2026-09-18 before this track:
`aircraft=66 ground=188 buildings=159 missiles=44 units=532` at t=16:30, `frameMsAvg` 17-33 ms,
`frameMsWorst` up to 7875 ms.

**Acceptance criteria being watched** (design §6):
1. No `self-check FAILED` at load.
2. `aircraft=` settles near 36 across both sides; `missiles=` falls with it.
3. A transport lift still launches with the sky at the ceiling, and still HOLDS for its escort.
4. Strike packages and the radar aeroplane still get airframes while patrols are refused.
5. Patrol refusals name the RESERVED line, not the absolute ceiling.

Plus the fix-cycle addition: a picket insertion escort must fill WHILE patrol refusals are printing.

---

## Readings

### t=1:34 — sample 1 (16:13 wall clock)

| Measure | Value |
|---|---|
| Frame time avg / worst | 8.4 ms / 75.0 ms |
| Units total | 177 |
| Aircraft / ground / buildings / missiles | 18 / 26 / 133 / 0 |
| Air missions / sorties | 15 / 8 |
| Heap / collections | 146 MB / 142 |

**Air demand.** Boscali 8 request lines, Primeva 5. Every single one is an escort — no standing
patrol has opened yet on either side, because a patrol is only posted where hostile aircraft are
already tracked and the sides have not met in the air.

**Escorts filling** — Boscali `escort to RESOURCE SITE 27 2/2`, `LIFT ESCORT HILLTOP 21 2/2`;
Primeva `escort to RESOURCE SITE 22 2/2`, `escort to RESOURCE SITE 5 2/2`, `LIFT ESCORT CROSSROADS 9
2/2`, `LIFT ESCORT HILLTOP 12 2/2`. Picket insertion escorts (`escort to ...`) are being bought and
filled, which is the mechanism finding 1's fix exists to protect — though with an empty sky this is
not yet a hard test of it.

**Ground.** Both sides one platoon formed, one lifting. Boscali 2ND PLATOON moving to the reserve
ring; Primeva 2ND PLATOON forming at 5 of 6.

**Kills / losses.** None yet — the sides have not made contact.

**Self-check / exceptions.** None. The fourteen new named cases passed silently at load, which is
acceptance criterion 1 met.

**Refusals.** No reserved-line refusals and no absolute-ceiling refusals yet; at 18 live aircraft
across both factions neither side is near its limit.

**Regressions flagged:** none. Too early to judge criteria 2 to 5 — the ceiling has not yet bound on
either side.

### t=5:34 — sample 2 (16:18 wall clock)

| Measure | Value | vs sample 1 |
|---|---|---|
| Frame time avg / worst | 9.4 ms / 83.3 ms | +1.0 ms |
| Units total | 231 | +54 |
| Aircraft | 32 | +14 |
| Ground | 45 | +19 |
| Buildings | 133 | flat |
| Missiles | 10 | +10 |
| Air missions / sorties | 26 / 24 | +11 / +16 |
| Heap / collections | 150 MB / 224 | +4 MB / +82 |

**Air war is fully joined.** 31 kill-and-loss lines, both sides trading fighters and transports.

**CAP fill is poor and the ceiling is NOT the cause.** Boscali `CAP 2/13`, Primeva `CAP 4/14` —
between them 6 fighters against 27 wanted. Zero reserved-line refusals and zero absolute-ceiling
refusals have been logged, so the wing is short because of money or airframe availability, not
because this track's limit is biting. Worth stating plainly: at 32 live aircraft across both sides
nothing has yet tested criterion 2, 4 or 5.

**CRITERION 3 MET — and it is the review's Major 2 fix, seen working.** Four lines:

```
Enemy commander (Primeva) lift for 1ST PLATOON holds at the form-up point: waiting for the escort (0 of 2 up).
Player commander (Boscali) lift for 1ST PLATOON holds at the form-up point: waiting for the escort (0 of 2 up).
Enemy commander (Primeva) lift for FOB HILLTOP 12 holds at the form-up point: waiting for the escort (0 of 2 up).
Player commander (Boscali) lift for FOB HILLTOP 21 holds at the form-up point: waiting for the escort (0 of 2 up).
```

The cover is asking for **2** escorts and the transport is **holding**. Before the fix the cover was
sized to zero at a full sky and `LiftMayLaunch(0,0,0,…)` read that as satisfied, so the lift left at
once and alone. No "went anyway" line has appeared, so the bounded wait has not yet expired on any
of the four.

**Escorts.** Boscali `LIFT ESCORT CROSSROADS 1 4/6 fallback`, `LIFT ESCORT HILLTOP 21 2/2`,
`escort to RESOURCE SITE 8 2/2 held`. Primeva `escort to HILLTOP 7 1/2`, and two lift escorts at
`0/5` and `0/2`, both on cooldown after losses.

**Packages and radar still being bought while CAP starves** — Boscali `AWACS 1/0` and
`STRIKE HILLTOP 4 S 1/2 E 1/2`, Primeva `STRIKE HILLTOP 14 S 2/2 E 2/1`. Criterion 4's spirit is
holding, though not yet under the condition that matters (patrols being refused).

**Kills and losses.** 31 events. Named air-to-air: Primeva lost a `KR-67 Ifrit` to a Boscali
`FS-12 Revoker`; Primeva lost a `UH-90 Ibis` to a Boscali `VT-7 Vagrant`; Boscali lost a
`VT-7 Vagrant` to two attackers. **Eight transport losses** across both sides, plus
`Boscali lost the insertion flight near RESOURCE SITE 8; cooldown 10 min, the picket drives instead`
and `Primeva FOB HILLTOP 12: the load in the air is recalled because there is no safe route`.

**Ground combat.** None. Both sides still building up; 45 ground vehicles between them.

**Self-check / exceptions.** None.

**Flagged for attention, neither a regression from this track:**
1. **Eight transports lost in five minutes.** The lifts that HOLD are safe; the ones dying are
   picket insertion flights, which have their own route-threat gate rather than the form-up hold.
   Pre-existing, but the rate is high enough to be worth a look on its own.
2. **CAP at roughly a fifth of demand.** Not the ceiling. If this persists it is an air-budget or
   airframe-price question, and it will also mask criteria 2, 4 and 5 — the ceiling cannot be
   observed binding if the commander cannot afford to reach it.

**Transport classification (the review's open question)** — still not settled directly. Indirect
evidence is consistent with the exclusion working: 32 aircraft live, roughly 16 a side, with a floor
of 16 a side and no refusal logged, which requires the counted number to be below the raw one.
### t=9:34 — sample 3 (16:22 wall clock)

| Measure | Value | vs sample 2 |
|---|---|---|
| Frame time avg / worst | 10.2 ms / 83.3 ms | +0.8 ms |
| Units total | 266 | +35 |
| Aircraft | 35 | +3 |
| Ground | 79 | +34 |
| Buildings | 133 | flat |
| Missiles | 7 | -3 |
| Air missions / sorties | 27 / 46 | +1 / +22 |
| Heap / collections | 155 MB / 298 | +5 MB / +74 |

**Aircraft have flattened.** 32, 32, 34, 35 over the last five minutes of samples. For reference the
pre-track match read `aircraft=59` at t=12:30 and 66 at t=16:30, so this is running far below it —
but see the caveat below before crediting the change.

**Standing patrols now exist and STILL nothing is being refused.** Boscali 13 patrol requests,
Primeva 9, on top of 3 and 9 escort requests. Zero reserved-line refusals, zero absolute-ceiling
refusals. Neither side has reached the reserved line of ten combat aircraft, because attrition and
money are holding the wing well below it.

**Honest caveat on criterion 2.** The aircraft count is at the target, but the ceiling is NOT what is
holding it there — nothing has been refused. Attributing the flat count to this track is not yet
supportable. What CAN be said is that the retune has not starved the wing: both sides are flying,
trading and re-buying freely.

**CAP fill.** Boscali `CAP 0/4`, Primeva `CAP 1/1` and `CAS 1/1`. Primeva's wing is matching its
(smaller) demand; Boscali's is not.

**Escorts are being fed by reassignment, in the right direction.** Primeva shows
`escort to OUTPOST 1 2/2 air0 retask` and `LIFT ESCORT HILLTOP 12 1/2 air1 retask` — escorts marked
`retask` are RECEIVING aircraft. The fix makes escorts ineligible as a reassignment *source*, not as
a target, so this is exactly the intended asymmetry: an escort can be reinforced from elsewhere, and
can never be stripped to reinforce something else.

**Lift holds now 6, "went anyway" still 0.** No transport has yet left without its escort.

**Kills and losses.** Boscali 16 losses, Primeva 17 — an even fight. Both wings are churning.

**Ground.** 79 vehicles, climbing fast (26 → 45 → 79). Boscali has four platoons, two moving to
forward bases and two forming; Primeva three, two moving to forward bases. **Still no ground
contact** — both sides are deploying toward objectives but have not met.

**Self-check / exceptions.** None.

**Regressions flagged:** none. Criterion 3 remains met and strengthening (6 holds, 0 unescorted
launches). Criteria 4 and 5 remain untestable while nothing is being refused.
### t=13:34 — sample 4 (16:26 wall clock)

| Measure | Value | vs sample 3 |
|---|---|---|
| Frame time avg / worst | 10.1 ms / 66.7 ms | -0.1 ms |
| Units total | 285 | +19 |
| Aircraft | 34 | -1 |
| Ground | 99 | +20 |
| Buildings | 133 | flat |
| Missiles | 4 | -3 |
| Air missions / sorties | 27 / 32 | flat / -14 |
| Heap / collections | 162 MB / 367 | +7 MB / +69 |

**Like-for-like against the pre-track match, at the same minute** (old `t=12:30` against new `t=12:34`):

| | Before | Now |
|---|---|---|
| Frame time avg | 26.5 ms | 10.7 ms |
| Units total | 471 | 277 |
| Aircraft | 59 | 32 |
| Ground | 179 | 94 |
| Buildings | 157 | 133 |

Frame time is two and a half times better and aircraft are nearly halved. **Two honest caveats.**
The ground count is also halved, which is `unit-economy_20260918`'s work and not this track's. And
the two matches are not economically identical — the old one had `econMines=8` by this point and
this one has none, so the earlier commander was richer and could field more of everything. The
comparison is indicative, not controlled.

**Still zero refusals of either kind**, at 34 live aircraft. The ceiling has not bound once in
thirteen and a half minutes. Criteria 4 and 5 remain untested.

**CAP shortfall is now stark on one side.** Boscali `CAP 5/21`, Primeva `CAP 0/1`. Boscali is asking
for twenty-one fighters and flying five. The priority ladder line says why, and it is money:
`Boscali ladder: CAP 2/4 (strict 2/2, +2 wanted; 2 base +0 air +3 losses, capped at 4, 2 lent),
draw platoons>buildings`.

**The reassignment asymmetry is visible and pointing the right way.** Two lines this sample:
`Boscali retasks FS-12 Revoker from 8TH PLATOON to LIFT ESCORT CROSSROADS 1: contact outranks cover`
— a fighter taken OFF a standing patrol and put ON a lift escort. Patrols are being used as the
source and escorts as the destination, which is exactly the direction the `IsTransportEscort` mark
was added to guarantee.

**Lifts: 7 holds, 0 unescorted launches.** Criterion 3 continues to hold.

**Kills and losses.** Boscali 27, Primeva 24. Fighter-on-fighter is now the bulk of it —
`FS-12 Revoker` and `KR-67 Ifrit` losses on both sides, plus continued transport attrition
(`UH-90 Ibis`, `VL-49 Tarantula`).

**Ground.** 99 vehicles. Boscali seven platoons, Primeva two. `points front=25 rear=35 threat=3
pressure=14.0/12` — Boscali's pressure clock has passed its threshold, so a deliberate attack is
due. **Still no ground-to-ground contact**; every "contact" line in the log is an air reassignment.

**Self-check / exceptions.** None.

**Regressions flagged:** none.
### t=17:34 — sample 5 (16:30 wall clock)

| Measure | Value | vs sample 4 |
|---|---|---|
| Frame time avg / worst | 12.2 ms / 116.7 ms | +2.1 ms |
| Units total | 314 | +29 |
| Aircraft | 37 | +3 |
| Ground | 125 | +26 |
| Buildings | 135 | +2 |
| Missiles | 4 | flat |
| Air missions / sorties | 30 / 34 | +3 / +2 |
| Heap / collections | 170 MB / 436 | +8 MB / +69 |

**Like-for-like against the pre-track match** (old `t=16:30` against new `t=17:34`, so this reading
is a minute OLDER and therefore flattered slightly against itself):

| | Before | Now |
|---|---|---|
| Frame time avg | 26.8 ms | 12.2 ms |
| Frame time worst | 258.3 ms | 116.7 ms |
| Units total | 532 | 314 |
| Aircraft | 66 | 37 |
| Ground | 188 | 125 |

Still better than two to one on frame time, and aircraft are a little over half what they were.

**Still zero refusals of either kind at seventeen minutes.** The ceiling has not bound once all
match. The aircraft count is sitting at 34-37 without it.

**The air war has broken open.** Losses now Boscali 27, Primeva 45 — Primeva lost twenty-one
aircraft in the last four minutes and Boscali lost none. Primeva is losing `T/A-30 Compass`
ground-attack aircraft repeatedly, one of them to seven attackers at once, which reads as its strike
side flying into a wing that owns the sky.

**Boscali is now money-bound on the CAS side**, three times this sample:
`[CAS] bought no aircraft: its air budget is short of the cheapest ground-attack-capable airframe its
strips accept, and it saves for one rather than launching the last-resort airframe`. Again: not the
ceiling.

**Air fill.** Boscali `CAP 6/9 CAS 2/3` (much improved from 5/21), Primeva `CAP 2/6 CAS 2/2`.

**Lifts: 8 holds, 0 unescorted launches.** Criterion 3 holding at eight for eight.

**Ground is now the growth story.** 125 vehicles, up from 26 at sample 1. Boscali eight platoons,
Primeva five, nearly all `Moving@ForwardBase`. **Still almost no ground-to-ground combat** — the
only territorial event in the whole log is `ROAD POINT 10 is neutral`. Both sides are deploying to
forward bases and have not met.

**Self-check / exceptions.** None.

**Regressions flagged:** none.

**Interim read at seventeen minutes.** Frame time is holding around 10-12 ms where the old match was
at 26 ms and heading for 120 ms. But the ceiling has not refused a single purchase, so **this
track's mechanism is still unproven in play** — what has been proven is that it does no harm, and
that the two fixes the review forced (the lift escort floor, the escort reassignment asymmetry) both
work. The aircraft count is being held down by attrition and money, not by the new limit.
### t=21:33 — sample 6 (16:34 wall clock)

| Measure | Value | vs sample 5 |
|---|---|---|
| Frame time avg / worst | 13.0 ms / **525.0 ms** | +0.8 ms / +408 ms |
| Units total | 335 | +21 |
| Aircraft | 42 | +5 |
| Ground | 130 | +5 |
| Buildings | 139 | +4 |
| Missiles | 6 | +2 |
| Air missions / sorties | 40 / 40 | +10 / +6 |
| Heap / collections | 175 MB / 498 | +5 MB / +62 |

**The GROUND ceiling is now binding** — `Boscali stops growing its ground force: 81 vehicles live
(ceiling 80). It replaces losses only.` That is `unit-economy_20260918`'s rule working in play, and
the first time either ceiling in the mod has been observed to bite. The idle reserve sale is firing
too: `Primeva cashes in 4 idle vehicle(s) from the reserve for 31` and again for 3 more.

**The AIR ceiling still has not refused anything.** Zero of both kinds at twenty-one minutes.

**Correction to this track's own prediction.** The design said the map would settle "near 36"
aircraft. It is at 42 and climbing. The reason is the income scaling, which the retune deliberately
left alone: the effective ceiling is `max(16, income ÷ 15)` capped at 24 per side, and `econMines`
has gone 0 → 3, so income is rising and each side's ceiling is climbing off its floor of 16 toward
24. **The true steady state is therefore up to 48 across the map, not 36.** That is the rule working
as designed — a richer commander is allowed a bigger wing — but the number quoted in the design and
in DECISION-061 is wrong and should be read as "16 to 24 a side, so 32 to 48 on the map".

**A 525 ms stall appeared.** One frame of just over half a second. The pre-track match had worse
(7875 ms), and one occurrence is not a pattern, but it is the first sign this match of the stall
class that was flagged as a separate fault.

**Air fill is poor on both sides now.** Boscali `CAP 5/19 CAS 1/4`, Primeva `CAP 3/17 CAS 0/2`.
Between them 8 fighters flying against 36 wanted — and still not one refusal, so this remains money
and attrition rather than the ceiling.

**Lifts: 10 holds, 0 unescorted launches.** Criterion 3 holding at ten for ten.

**Kills and losses.** Boscali 38, Primeva 54. Primeva continues to lose the air war.

**Ground.** 130 vehicles, one side at its ceiling. Twelve platoons between them. Territory is still
almost static — `ROAD POINT 10 is neutral` remains the only change of hands in the whole match.

**Self-check / exceptions.** None.

**Regressions flagged:** none from this track. One documentation defect in this track's own records
(the "near 36" figure), corrected above.
### t=25:32 — sample 7 (16:38 wall clock)

| Measure | Value | vs sample 6 |
|---|---|---|
| Frame time avg / worst | 14.7 ms / 241.7 ms | +1.7 ms |
| Units total | 378 | +43 |
| Aircraft | 38 | -4 |
| Ground | 161 | +31 |
| Buildings | 142 | +3 |
| Missiles | 14 | +8 |
| Air missions / sorties | 36 / 37 | -4 / -3 |
| Heap / collections | 176 MB / 555 | +1 MB / +57 |

**Frame time is climbing steadily with unit count**, which is the expected relationship rather than a
fault: 8.4 → 9.4 → 10.2 → 10.1 → 12.2 → 13.0 → 14.7 ms across the seven samples, against units
177 → 231 → 266 → 285 → 314 → 335 → 378. At the same 25-minute mark the pre-track match was not
sampled, but it read 26.8 ms at 16:30 and was heading for 120 ms by the half hour.

**STALLS ARE NOW A PATTERN, and this is the thing worth the developer's attention.** Nine health
readings have carried a worst frame over 200 ms, and they are clustered in the last four minutes:
525.0, 358.3, 408.4, 241.7 ms in consecutive samples, against 58-133 ms for the whole first twenty
minutes. The average is fine; something is periodically blocking for a third to half a second. This
is the fault class the frame-rate investigation named separately from unit count
(`frameMsWorst` climbing while `frameMsAvg` stays flat → periodic stalls, look at `gc0` first). For
the record: `gc0` is rising steadily (498 → 555 over four minutes) while `heapMB` is flat at about
176 MB, so collections are frequent rather than large.

**The air ceiling has STILL refused nothing**, at twenty-five and a half minutes with 38-44 aircraft
live. Twenty-six minutes of play and the mechanism this track exists to add has never once fired.

**The ground ceiling continues to bind and hold** — `Boscali stops growing its ground force: 83
vehicles live (ceiling 80). It replaces losses only.` Ground has still crept to 161 across both
sides, because the ceiling is per-commander and the player's own vehicles are outside it.

**Air fill improved.** Boscali `CAP 3/10 CAS 3/4`, Primeva `CAP 4/8 CAS 2/2` — demand has fallen to
meet supply rather than supply rising.

**Lifts: 10 holds, 0 unescorted launches.** Unchanged, and criterion 3 remains met.

**Kills and losses.** Boscali 55, Primeva 61 — the gap has narrowed from 38-vs-54; Boscali lost 17
aircraft in this window against Primeva's 7, so the air war has swung back.

**Ground.** 161 vehicles, 18 platoons between the sides. Territory still essentially frozen: one
point neutral in twenty-five minutes, no captures by either side.

**Self-check / exceptions.** None.

**Regressions flagged:** none from this track. Two things for the developer, neither caused by it:
1. **The half-second stalls**, now nine occurrences and clustered in the last four minutes.
2. **Territory is static.** Eighteen platoons deployed and no ground has changed hands in
   twenty-five minutes. Worth asking whether the ground war is actually doing anything.
### t=29:30 — sample 8 (16:42 wall clock) — THE HALF-HOUR MARK

| Measure | Value | vs sample 7 |
|---|---|---|
| Frame time avg / worst | 13.4 ms / 275.0 ms | -1.3 ms |
| Units total | 390 | +12 |
| Aircraft | 37 | -1 |
| Ground | 173 | +12 |
| Buildings | 150 | +8 |
| Missiles | 15 | +1 |
| Air missions / sorties | 37 / 43 | +1 / +6 |
| Heap / collections | 190 MB / 613 | +14 MB / +58 |

**Frame time has PLATEAUED.** 13.5, 13.5, 13.4 ms over the last ninety seconds while units held at
384-390. It is no longer climbing. The half-hour mark was the whole point of this exercise: the
pre-track match was measured at **120.0 ms with 480 units alive**, and the frame-rate investigation's
own curve put **427 units at 46.8 ms**. This match is at **390 units and 13.4 ms**.

**That result is better than the unit count alone predicts, and the gap is worth recording.** On the
measured curve, 390 units should cost somewhere around 35-45 ms. It is costing 13.4. So cutting
AIRCRAFT specifically bought more than cutting the same number of arbitrary units would have. That
is evidence — not proof — that an aircraft costs the engine materially more per frame than a ground
vehicle, which nothing in the mod's records had established before. Worth a dedicated measurement if
anyone wants to rely on it.

**Both commanders are now at the ground ceiling** — `Boscali ... 83 vehicles live (ceiling 80)` and
`Primeva ... 81 vehicles live (ceiling 80)`. The ground rule from `unit-economy_20260918` is holding
both sides.

**The air ceiling has refused nothing in twenty-nine and a half minutes.** Final position on
criteria 4 and 5 for this match: **untested**. The wing never got near its limit.

**STALLS ARE NOW CONSTANT, not clustered.** Every one of the last twelve readings carries a worst
frame over 200 ms:

```
24:02 worst 333.3    26:31 worst 250.0    28:30 worst 391.7
24:32 worst 358.3    27:01 worst 366.7    29:00 worst 416.7
25:02 worst 408.4    27:31 worst 475.0    29:30 worst 275.0
25:32 worst 241.7    28:00 worst 383.3
26:01 worst 216.7
```

A quarter to half a second, every thirty-second window, while the average sits flat at 13 ms. `gc0`
is rising about 14 collections per 30 s with `heapMB` flat near 180-190 MB — frequent small
collections, not one big one. **This is the single biggest remaining problem in the build and it has
nothing to do with unit count.** It should get its own track.

**Lifts: 12 holds, 0 unescorted launches.** Twelve for twelve. Criterion 3 is comprehensively met.

**Kills and losses.** Boscali 64, Primeva 69. Even.

**Ground and territory.** 173 vehicles. Territory has finally started to move —
`Created FOB HILLTOP 11 1 for Primeva` and `HILLTOP 10 is neutral`. Primeva is down to 2 platoons
from 8, so it has taken heavy ground losses. `points front=30 rear=30` for Boscali against
`front=24 rear=36` for Primeva.

**Self-check / exceptions.** None, in thirty minutes.

**Regressions flagged:** none from this track, across the whole match.
### t=33:28 — sample 9 (16:46 wall clock)

| Measure | Value | vs sample 8 |
|---|---|---|
| Frame time avg / worst | 14.9 ms / 250.0 ms | +1.5 ms |
| Units total | 407 | +17 |
| Aircraft | 40 | +3 |
| Ground | 193 | +20 |
| Buildings | 151 | +1 |
| Missiles | 10 | -5 |
| Air missions / sorties | 38 / 46 | +1 / +3 |
| Heap / collections | 192 MB / 667 | +2 MB / +54 |

**Still comfortably ahead of the old match.** 407 units at 14.9 ms, against the measured curve's
427 units at 46.8 ms and 480 at 120.0 ms.

**Ground is growing again** — 173 → 193 in four minutes, despite both commanders sitting at their
ceiling of 80. The arithmetic: 80 + 80 = 160 from the two commanders, so roughly 33 vehicles are
outside the ceiling's reach. That is the player's own faction and the paths DECISION-059 deliberately
left ungated (the player's depot queue, insertion cargo, the strategic reload). Not a fault — but if
ground needs to come down further, that gap is where the remainder lives.

**Air ceiling: still zero refusals**, at thirty-three and a half minutes with 40 aircraft live.
Twenty per side against a limit of 16 rising to 24 on income (`econMines` now 5). The wing sits just
under its allowance, which is why nothing fires.

**Stalls continue** — a 600 ms worst frame at t=32:28, then 267 and 250 ms. Unchanged conclusion:
constant, unrelated to unit count, and the biggest remaining problem.

**Lifts: 14 holds, 0 unescorted launches.** Fourteen for fourteen.

**Kills and losses.** Boscali 81, Primeva 76 — Boscali has now overtaken on losses, having lost 17
in this window against Primeva's 7. Fighter attrition is heavy on both sides; `FS-12 Revoker` and
`KR-67 Ifrit` losses dominate, with home-defence fighters being relaunched continuously
(`launched a FS-12 Revoker (Fighter) from airbase_city ... (home CAP...)`).

**Air fill.** Boscali `CAP 7/10 CAS 5/5`, Primeva `CAP 6/25 CAS 5/5`. Ground-attack demand is now
fully met on both sides; only the fighter side is short, and on Primeva heavily so.

**Ground and territory.** 193 vehicles, 20 platoons. `points front=33 rear=27` for Boscali against
`front=26 rear=34` for Primeva — Boscali is pushing forward.

**Self-check / exceptions.** None, in thirty-three minutes.

**Regressions flagged:** none from this track.
### t=37:26 — sample 10 (16:50 wall clock)

| Measure | Value | vs sample 9 |
|---|---|---|
| Frame time avg / worst | 18.3 ms / 183.3 ms | +3.4 ms |
| Units total | 436 | +29 |
| Aircraft | 34 | -6 |
| Ground | **208** | +15 |
| Buildings | 152 | +1 |
| Missiles | 12 | +2 |
| Air missions / sorties | 33 / 57 | -5 / +11 |
| Heap / collections | 201 MB / 714 | +9 MB / +47 |

**Frame time is climbing again**: 13.4 → 14.9 → 18.3 ms over the last twelve minutes, tracking units
390 → 407 → 436. Still well ahead of the measured curve, which put 427 units at 46.8 ms — this is 436
at 18.3 — but the direction has turned.

**GROUND HAS OVERTAKEN THE OLD MATCH AND IS NOW THE DOMINANT COST.** 208 ground vehicles, against
188 in the pre-track match at its worst sampled point. Aircraft are down to 34 and falling; ground is
up to 208 and rising. The composition has completely inverted:

| | Old match (t=16:30) | Now (t=37:26) |
|---|---|---|
| Aircraft | 66 | 34 |
| Ground | 188 | 208 |

**Both commanders are capped at 80 each, so 160 of those 208 are accounted for and roughly 48 are
outside the ceiling's reach** — the player's own faction plus the paths DECISION-059 deliberately
left ungated. That gap is now about a quarter of all ground vehicles and it is growing. **If frame
time keeps climbing, this is where the next cut has to come from, not from the air.**

**Air ceiling: still zero refusals at thirty-seven minutes.** Aircraft are falling on their own
(40 → 34) through attrition. The mechanism has never fired in the entire match.

**Lifts: 16 holds, 0 unescorted launches.** Sixteen for sixteen.

**Kills and losses.** Boscali 109, Primeva 92. Boscali lost 28 aircraft in this four-minute window
against Primeva's 16 — the heaviest exchange of the match, and Boscali is now clearly losing the air
war it was winning at sample 5.

**Air fill.** Boscali `CAP 3/13 CAS 2/4`, Primeva `CAP 2/20 CAS 8/14`. Both fighter sides have
collapsed under attrition; Primeva is flying 2 of 20 wanted.

**Territory is moving at last.** `HILLTOP 10 is neutral`, `ROAD POINT 8 is neutral`,
`Created FOB HILLTOP 22 2 for Primeva`. Twelve territorial events now, against one for the first
twenty-five minutes. 26 platoons between the sides. Boscali `front=33 rear=27`, Primeva
`front=28 rear=32`.

**Self-check / exceptions.** None, in thirty-seven minutes.

**Regressions flagged:** none from this track. The emerging issue is ground count, which belongs to
`unit-economy_20260918` and to the ungated paths its decision I listed.
### t=41:25 — sample 11 (16:54 wall clock)

| Measure | Value | vs sample 10 |
|---|---|---|
| Frame time avg / worst | 16.2 ms / 383.3 ms | **-2.1 ms** |
| Units total | 435 | -1 |
| Aircraft | 37 | +3 |
| Ground | 206 | -2 |
| Buildings | 157 | +5 |
| Missiles | 16 | +4 |
| Air missions / sorties | 37 / 46 | +4 / -11 |
| Heap / collections | 205 MB / 759 | +4 MB / +45 |

**Frame time recovered and ground has plateaued.** 18.3 → 16.2 ms, with ground flat at 206-208 across
three readings and total units flat at 435-443. The ground ceiling is holding the line after all —
sample 10's climb was the approach to the plateau, not an unbounded rise.

**Air ceiling: still zero refusals at forty-one minutes.** Final position is now safe to state: this
track's mechanism did not fire once in a forty-minute match.

**A finding for the OTHER track, worth recording here because this is where it was seen.** Of the
four rules `unit-economy_20260918` added, after forty-one minutes:

| Rule | Times fired |
|---|---|
| Ground ceiling ("stops growing its ground force") | 4 |
| Idle reserve sale ("cashes in N idle vehicle(s)") | 2 |
| **Quiet-ground retirement ("cashes in N vehicle(s) at &lt;point&gt;")** | **0** |
| Garrison of one per point | passive, no log line |

**The quiet-ground retirement has never fired.** It is the rule meant to thin a garrison on a point
nobody has contested for five minutes, and with territory as static as this match has been it should
have been the rule firing MOST. Either its contact clock never reaches five quiet minutes, or the
conditions around it (platoon wholly arrived, held-point floor, in-contact test) are stricter in
practice than intended. Worth a look — it is one of the four rules that track shipped and it is
doing nothing.

**Lifts: 18 holds, 0 unescorted launches.** Eighteen for eighteen.

**Kills and losses.** Boscali 117, Primeva 103. Both wings continue to bleed.

**Air fill.** Boscali `CAP 7/31 CAS 1/6`, Primeva `CAP 2/13 CAS 1/4`. Boscali's fighter demand has
climbed to 31 — the highest of the match — and it is flying 7.

**Territory.** Nine events total. `points front=33 rear=27` Boscali against `front=28 rear=32`
Primeva, unchanged from sample 10. 22 platoons.

**Self-check / exceptions.** None, in forty-one minutes.

**Regressions flagged:** none from this track.
### t=45:23 — sample 12 (16:58 wall clock) — STEADY STATE CONFIRMED

| Measure | Value | vs sample 11 |
|---|---|---|
| Frame time avg / worst | 15.6 ms / 283.3 ms | -0.6 ms |
| Units total | 425 | -10 |
| Aircraft | 35 | -2 |
| Ground | 197 | -9 |
| Buildings | 158 | +1 |
| Missiles | 8 | -8 |
| Air missions / sorties | 34 / 40 | -3 / -6 |
| Heap / collections | 217 MB / 802 | +12 MB / +43 |

**The match has settled.** Across the last four samples (t=33 to t=45) every figure is flat or
falling: frame time 14.9 → 18.3 → 16.2 → 15.6 ms, units 407 → 436 → 435 → 425, aircraft 40 → 34 →
37 → 35, ground 193 → 208 → 206 → 197. Nothing is running away. **At forty-five minutes the
pre-track build was measured at 120 ms with 480 units; this is 15.6 ms with 425.**

**Air ceiling: zero refusals in forty-five minutes.** Unchanged and now conclusive for this match.

**Lifts: 20 holds, 0 unescorted launches.** Twenty for twenty.

**Kills and losses.** Boscali 136, Primeva 119 — 255 aircraft lost between them in forty-five
minutes, against a live population of 35. The wing is being replaced roughly seven times over.

**Air fill.** Boscali `CAP 4/8 CAS 2/4`, Primeva `CAP 1/10 CAS 2/6`.

**Ground rules unchanged from sample 11**: ceiling 4 firings, idle sale 2, **quiet-ground retirement
still 0**.

**Territory.** 11 events. `points front=33 rear=27` Boscali against `front=29 rear=31` Primeva.
24 platoons.

**Memory note.** `heapMB` has drifted 176 → 217 over twenty minutes and `everSpawned` has reached
2112. The game roots every unit ever spawned (frame-rate investigation §2), so this is the known,
unfixable-by-the-mod growth. It has not yet produced a frame-time trend.

**Self-check / exceptions.** None, in forty-five minutes.

**Regressions flagged:** none from this track.
### t=49:21 — sample 13 (17:02 wall clock)

| Measure | Value | vs sample 12 |
|---|---|---|
| Frame time avg / worst | 15.3 ms / 333.3 ms | -0.3 ms |
| Units total | 438 | +13 |
| Aircraft | 40 | +5 |
| Ground | 205 | +8 |
| Buildings | 158 | flat |
| Missiles | 18 | +10 |
| Air missions / sorties | 38 / 54 | +4 / +14 |
| Heap / collections | 223 MB / 846 | +6 MB / +44 |

**Flat.** Frame time 15.6 → 15.3 ms, units 425 → 438, ground pinned at 205 across three consecutive
readings. Approaching fifty minutes with no decay trend.

**Air ceiling: zero refusals at forty-nine minutes.**

**Lifts: 22 holds, 0 unescorted launches.**

**Kills and losses.** Boscali 149, Primeva 126 — 275 aircraft lost in forty-nine minutes.

**Air fill.** Boscali `CAP 6/19 CAS 3/6`, Primeva `CAP 3/9 CAS 5/7`.

**Ground rules.** Ceiling 4, idle sale 2, **quiet-ground retirement still 0**. Territory 13 events.

**Self-check / exceptions.** None, in forty-nine minutes.

**Regressions flagged:** none from this track.

---

## Running conclusions at fifty minutes

**Proven in game.**
1. No `self-check FAILED` and no exception in fifty minutes — the fourteen new named cases pass.
2. Criterion 3 is comprehensively met: **22 lift holds, 0 unescorted launches.** This is the review's
   Major 2 fix, and without it every one of those 22 transports would have launched alone.
3. The escort reassignment asymmetry works — escorts are seen receiving aircraft
   (`retask`) and never being stripped.
4. Frame time is transformed: **15.3 ms at 438 units, against a measured 120 ms at 480 units and
   46.8 ms at 427 units in the pre-track build.**

**NOT proven, and cannot be from this match.**
- The reserved block and the absolute ceiling **never fired once in fifty minutes**. Criteria 4 and 5
  are untested. The wing is held far below its allowance by attrition — 275 aircraft lost against a
  live population of 40 — and by money.
- The transport classification question from the review remains open.

**Open items for other work, found here.**
1. **Constant sub-second stalls** — every reading since t=24 carries a worst frame of 200-600 ms
   while the average sits at 15 ms. Unrelated to unit count. Biggest remaining problem.
2. **The quiet-ground retirement has never fired** in fifty minutes, in a match with almost static
   territory, which is the condition it was written for.
3. **Roughly 45 ground vehicles sit outside the ground ceiling's reach** (205 live against two
   commanders capped at 80 each).
### t=53:20 — sample 14 (17:06 wall clock)

| Measure | Value | vs sample 13 |
|---|---|---|
| Frame time avg / worst | **13.9 ms** / 308.3 ms | -1.4 ms |
| Units total | 428 | -10 |
| Aircraft | **29** | -11 |
| Ground | 192 | -13 |
| Buildings | 170 | +12 |
| Missiles | 7 | -11 |
| Air missions / sorties | 29 / 48 | -9 / -6 |
| Heap / collections | 238 MB / 886 | +15 MB / +40 |

**Frame time is IMPROVING** — 15.6 → 15.3 → 13.9 ms over the last three samples, as aircraft fall
40 → 29 and ground falls 205 → 192. Buildings are the only category growing (158 → 170, `econBuilt`
up to 41), which is the economy finally being built out. Fifty-three minutes in and the trend is
downward, not upward.

**One side's air force has collapsed.** Boscali `CAP 10/12 CAS 7/7` — almost fully met for the first
time all match. Primeva `CAP 0/4 CAS 0/4` — flying nothing at all. Boscali `points front=35 rear=25`
against Primeva `front=27 rear=33`; Boscali is winning.

**Air ceiling: zero refusals at fifty-three minutes.** With aircraft now down to 29 it will not fire.

**Lifts: 23 holds, 0 unescorted launches.**

**Kills and losses.** Boscali 165, Primeva 146 — 311 aircraft between them.

**Ground rules.** Unchanged: ceiling 4, idle sale 2, **quiet retirement 0**. Territory 16 events.

**Self-check / exceptions.** None, in fifty-three minutes.

**Regressions flagged:** none from this track.
### t=57:20 — sample 15 (17:10 wall clock)

| Measure | Value | vs sample 14 |
|---|---|---|
| Frame time avg / worst | 16.9 ms / 416.7 ms | +3.0 ms |
| Units total | 432 | +4 |
| Aircraft | 37 | +8 |
| Ground | 195 | +3 |
| Buildings | **178** | +8 |
| Missiles | 12 | +5 |
| Air missions / sorties | 33 / 58 | +4 / +10 |
| Heap / collections | 243 MB / 930 | +5 MB / +44 |

**Buildings are now the only category growing.** 133 at the start of the match, 178 now, with
`econBuilt` at 49 and climbing every sample. Aircraft and ground are both flat-to-falling and held by
their respective rules; the economy build-out is what is adding units at this stage. Buildings are
static objects rather than thinking units, so the frame cost per one should be far lower — but it is
worth noting that **the category nobody has capped is the one still growing at the hour mark.**

**Air ceiling: zero refusals at fifty-seven minutes.**

**Lifts: 26 holds, 0 unescorted launches.**

**Kills and losses.** Boscali 173, Primeva 152 — 325 aircraft lost.

**Air fill.** Boscali `CAP 3/13 CAS 4/11`, Primeva `CAP 3/17 CAS 1/6`. Primeva has rebuilt from the
total collapse at sample 14.

**Territory has come level.** Both sides now `points front=35 rear=25` — Primeva has clawed back
from `front=27`. 21 territorial events, up from 16.

**Ground rules.** Unchanged: ceiling 4, idle sale 2, **quiet retirement 0** at fifty-seven minutes.

**Self-check / exceptions.** None.

**Regressions flagged:** none from this track.
### t=61:20 — sample 16 (17:14 wall clock) — PAST THE HOUR

| Measure | Value | vs sample 15 |
|---|---|---|
| Frame time avg / worst | **13.8 ms** / 200.0 ms | -3.1 ms |
| Units total | 430 | -2 |
| Aircraft | 29 | -8 |
| Ground | 198 | +3 |
| Buildings | 182 | +4 |
| Missiles | 6 | -6 |
| Air missions / sorties | 29 / 49 | -4 / -9 |
| Heap / collections | 240 MB / 972 | -3 MB / +42 |

**An hour of continuous play and frame time is FALLING**: 16.9 → 15.1 → 13.8 ms over the last ninety
seconds, and the worst frame is down to 200 ms, its lowest since t=24. **The decay this whole line of
work started from has not happened.** For the comparison that matters: the pre-track build was
measured at **120 ms with 480 units at the half hour**. This build is at **13.8 ms with 430 units at
the hour**.

**Buildings have plateaued** at 182 with `econBuilt` flat at 53 across three readings — the economy
build-out has finished, so no category is growing any more.

**Air ceiling: zero refusals in sixty-one minutes.** Final.

**Lifts: 27 holds, 0 unescorted launches.** Twenty-seven for twenty-seven.

**Kills and losses.** Boscali 192, Primeva 161 — **353 aircraft lost** against a live population of
29. The wing has been replaced more than twelve times over.

**Air fill.** Boscali `CAP 4/7 CAS 2/5`, Primeva `CAP 2/6 CAS 4/9`. Demand has fallen to near supply
on both sides.

**Territory.** Boscali `front=35 rear=25`, Primeva `front=33 rear=27`. Even. 21 events.

**Ground rules.** Unchanged all match: ceiling 4, idle sale 2, **quiet retirement 0**.

**Self-check / exceptions.** None, in sixty-one minutes.

**Regressions flagged:** none from this track, in the whole match.
### t=65:18 — sample 17 (17:18 wall clock)

| Measure | Value | vs sample 16 |
|---|---|---|
| Frame time avg / worst | 14.3 ms / 208.3 ms | +0.5 ms |
| Units total | 439 | +9 |
| Aircraft | 32 | +3 |
| Ground | 194 | -4 |
| Buildings | 182 | flat |
| Missiles | 11 | +5 |
| Air missions / sorties | 31 / 50 | +2 / +1 |
| Heap / collections | 267 MB / 1010 | +27 MB / +38 |

**Flat at 14.3 ms and sixty-five minutes.** Every category is now level: aircraft 29-33, ground
189-198, buildings pinned at 182. Nothing has grown for ten minutes.

**Memory is the one figure still moving.** `heapMB` 240 → 267 and `everSpawned` past 3,100. The game
holds a permanent reference to every unit it has ever created, so this rises for the life of the
mission and the mod cannot fix it (frame-rate investigation §2). It has produced no frame-time trend
in sixty-five minutes, which is itself a useful negative result — the memory theory that survived
that investigation is not what was causing the decay.

**Air ceiling: zero refusals at sixty-five minutes.**

**Lifts: 27 holds, 0 unescorted launches.**

**Kills and losses.** Boscali 212, Primeva 168 — 380 aircraft lost. Boscali has lost 20 in four
minutes against Primeva's 7.

**Air fill.** Boscali `CAP 5/11 CAS 4/7`, Primeva `CAP 4/13 CAS 3/10`.

**Territory.** Boscali `front=36 rear=24`, Primeva `front=31 rear=29` — Boscali edging ahead again.
22 events.

**Ground rules.** Unchanged: ceiling 4, idle sale 2, **quiet retirement 0**.

**Self-check / exceptions.** None, in sixty-five minutes.

**Regressions flagged:** none from this track.
### t=69:17 — sample 18 (17:22 wall clock)

| Measure | Value | vs sample 17 |
|---|---|---|
| Frame time avg / worst | 15.6 ms / **125.0 ms** | +1.3 ms / -83 ms |
| Units total | 445 | +6 |
| Aircraft | 41 | +9 |
| Ground | 193 | -1 |
| Buildings | 187 | +5 |
| Missiles | 11 | flat |
| Air missions / sorties | 40 / 53 | +9 / +3 |
| Heap / collections | 254 MB / 1048 | -13 MB / +38 |

**The worst frame is 125 ms — the best since t=23.** The stall pattern that ran 200-600 ms from
sample 7 onward has eased this sample. One reading is not a trend, but it is the first sign of it
letting up.

**Air ceiling: zero refusals at sixty-nine minutes.**

**Lifts: 27 holds, 0 unescorted launches.** Unchanged since sample 16 — no lift has been ordered in
eight minutes.

**Kills and losses.** Boscali 228, Primeva 174 — **402 aircraft lost**. Boscali continues to take the
heavier toll (16 in four minutes against Primeva's 6) while winning on the ground.

**Air fill.** Boscali `CAP 9/18 CAS 3/5`, Primeva `CAP 4/7 CAS 9/9` — Primeva's ground-attack side is
fully met for the first time all match.

**Territory is moving decisively.** Boscali `front=39 rear=21` against Primeva `front=30 rear=30`.
Boscali has gone 33 → 35 → 36 → 39 front points over the last four samples. 26 territorial events, up
from 22. The ground war has finally started to resolve.

**Ground rules.** Unchanged: ceiling 4, idle sale 2, **quiet retirement 0** at sixty-nine minutes.

**Self-check / exceptions.** None.

**Regressions flagged:** none from this track.
### t=73:16 — sample 19 (17:26 wall clock)

| Measure | Value | vs sample 18 |
|---|---|---|
| Frame time avg / worst | **13.0 ms** / 225.0 ms | -2.6 ms |
| Units total | 432 | -13 |
| Aircraft | 33 | -8 |
| Ground | 190 | -3 |
| Buildings | 190 | +3 |
| Missiles | 5 | -6 |
| Air missions / sorties | 33 / 53 | -7 / flat |
| Heap / collections | 261 MB / 1086 | +7 MB / +38 |

**13.0 ms at seventy-three minutes** — the lowest average since t=21, and the sixth consecutive
sample in the 13-17 ms band. There is no decay curve in this match.

**Air ceiling: zero refusals at seventy-three minutes.**

**Lifts: 28 holds, 0 unescorted launches.**

**Kills and losses.** Boscali 233, Primeva 186 — 419 aircraft lost.

**Air fill.** Boscali `CAP 3/9 CAS 12/19` — twelve ground-attack airframes flying, the largest strike
effort of the match, which matches its ground push. Primeva `CAP 5/18 CAS 1/5`.

**Boscali is winning decisively.** `points front=43 rear=17` against Primeva `front=33 rear=27`.
Boscali's front count has gone 33 → 35 → 36 → 39 → **43** over five samples. The ground war that did
nothing for the first hour is now resolving quickly.

**Ground rules.** Unchanged: ceiling 4, idle sale 2, **quiet retirement 0** at seventy-three minutes.

**Self-check / exceptions.** None.

**Regressions flagged:** none from this track.
### t=77:14 — sample 20 (17:30 wall clock)

| Measure | Value | vs sample 19 |
|---|---|---|
| Frame time avg / worst | 13.5 ms / 325.0 ms | +0.5 ms |
| Units total | 434 | +2 |
| Aircraft | 37 | +4 |
| Ground | 181 | -9 |
| Buildings | 191 | +1 |
| Missiles | 0 | -5 |
| Air missions / sorties | 37 / 34 | +4 / -19 |
| Heap / collections | 265 MB / 1124 | +4 MB / +38 |

**Seventy-seven minutes, 13.5 ms.** A dip to 11.4 ms at t=76:14 with aircraft momentarily down to 23
— the lowest aircraft count of the match — is a neat incidental datapoint: **23 aircraft gave 11.4 ms,
37 aircraft gave 13.5 ms, with ground and buildings unchanged.** Fourteen aircraft cost about 2 ms,
or roughly 0.15 ms each. Ground vehicles over the same match moved far less per unit. That is the
clearest in-match evidence yet that an aircraft is the more expensive kind of unit, which is the
premise this whole track rested on and which had never been measured directly.

**Air ceiling: zero refusals at seventy-seven minutes.**

**Lifts: 28 holds, 0 unescorted launches.**

**Kills and losses.** Boscali 255, Primeva 194 — **449 aircraft lost.**

**Air fill.** Boscali `CAP 5/13 CAS 7/7`, Primeva `CAP 3/13 CAS 4/11`.

**Territory has swung back.** Boscali `front=36 rear=24` (down from 43), Primeva `front=29 rear=31`
(up from 33 rear). Primeva has counter-attacked. 26 events, unchanged, so these are captures rather
than points going neutral.

**Ground rules.** Unchanged: ceiling 4, idle sale 2, **quiet retirement 0** at seventy-seven minutes.

**Self-check / exceptions.** None.

**Regressions flagged:** none from this track.
### t=81:13 — sample 21 (17:34 wall clock)

| Measure | Value | vs sample 20 |
|---|---|---|
| Frame time avg / worst | 14.3 ms / 216.7 ms | +0.8 ms |
| Units total | 427 | -7 |
| Aircraft | 29 | -8 |
| Ground | 180 | -1 |
| Buildings | 191 | flat |
| Missiles | 8 | +8 |
| Air missions / sorties | 29 / 44 | -8 / +10 |
| Heap / collections | 291 MB / 1159 | +26 MB / +35 |

**Eighty-one minutes, 14.3 ms, everything flat.** Aircraft 29-32, ground 180-181, buildings pinned at
191. The match has been in steady state for forty minutes.

**Air ceiling: zero refusals at eighty-one minutes.**

**Lifts: 28 holds, 0 unescorted launches.** No lift ordered in twelve minutes.

**Kills and losses.** Boscali 268, Primeva 207 — **475 aircraft lost** in eighty-one minutes, against
a live population of 29.

**Air fill.** Boscali `CAP 3/9 CAS 4/6`, Primeva `CAP 4/11 CAS 2/4`. Both wings are small and
roughly matched.

**Territory.** Boscali `front=36 rear=24`, Primeva `front=33 rear=27`. Primeva has continued its
recovery from `front=29`; the swing that took Boscali to 43 has been largely reversed. 26 events.

**Ground rules.** Unchanged: ceiling 4, idle sale 2, **quiet retirement 0** at eighty-one minutes.

**Memory.** `heapMB` 291, `everSpawned` 3,763. Still climbing, still no frame-time effect.

**Self-check / exceptions.** None.

**Regressions flagged:** none from this track.
