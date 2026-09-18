# Air superiority spiral — measurement and options — 2026-09-17

**Scope:** does the commander economy collapse into an air war, and if so what can be done about it?
Study only; no code changed, no build run.

**Evidence:** `BepInEx\LogOutput.log` as it stood on 16 September 2026, 4,650 lines, mission
`Ground Control Duel Far`. The file holds two play blocks separated by a hot reload at raw line
2,261; the reload resets the mod's memory but not the game, so the blocks are treated separately
throughout. Primeva is the enemy commander, Boscali the commander on the player's side. 84 purchase
reviews in total, 42 per side.

**Mechanics read:** `Ai/CommanderEnemyCommanderLadder.cs` (whole file),
`Ai/CommanderEnemyCommanderService.cs` (`ReviewPurchases`, `SpendPlatoons`),
`Ai/CommanderEnemyCommanderAir.cs` (fund ceiling, buy loop guards),
`Operations/CommanderOperationsAirPosture.cs` (fallback posture, call for help),
`Operations/CommanderOperationsAirWing.cs` (the shared sizing envelope, patrol and
close-air-support ladders), `Operations/CommanderOperationsAirPackages.cs` (escort sizing),
`Operations/CommanderOperationsAirPlatoonCap.cs` (transport and lift escorts).

---

## 1. Verdict in one paragraph

The spiral is real and the airbase-versus-airbase picture is confirmed by the loss geometry. But the
named cause is wrong. The standing home patrol — the first rung, the one the brief blames — is the
**smallest** part of air spending, between 12 and 32 per cent of it, and it shrinks as a match goes
on. Between 53 and 66 per cent of every commander's air money goes on **escorts**, bought inside the
rung named "platoons". Air takes roughly half of each commander's entire income, and that share is
flat rather than climbing, because the growth terms are already pinned against their own ceilings.
The deeper problem the numbers expose is not that too many fighters are bought. It is that the
fighters never arrive: every single air tasking in the log is a patrol, not one close-air-support
mission was tasked, and the patrols spend their lives falling back toward their own runways.

---

## 2. Where the money goes

Purchase reviews report `spent CAP / platoons / pickets / buildings`. Only the first of those is the
home patrol. Aircraft bought for escorts and strikes are paid for **inside** the platoons figure, so
that figure badly overstates how much ground force is being raised. The table below rebuilds the
true split by pricing every airframe launch from its own log line.

| Per play block | Primeva 1 | Boscali 1 | Primeva 2 | Boscali 2 |
|---|---:|---:|---:|---:|
| Reviews | 27 | 27 | 15 | 15 |
| Total commander spend | 2,533 | 3,225 | 3,047 | 3,177 |
| Air spend (all airframes) | 1,558 | 1,562 | 1,605 | 1,635 |
| **Air as a share of everything** | **62%** | **48%** | **53%** | **51%** |
| Air-to-air fighters alone | 1,134 | 1,185 | 1,134 | 1,055 |
| Fighters as a share of everything | 45% | 37% | 37% | 33% |
| Ground vehicles bought from the platoons rung | 564 | 406 | 406 | 342 |
| Forward bases and other buildings | 225 | 625 | 850 | 600 |
| Air-delivered pickets | 186 | 632 | 186 | 600 |

The rung called "platoons" spends **65 to 81 per cent of itself on aeroplanes**. That single fact is
the heart of the problem, and it is invisible on the review line as it prints today.

Air spending broken down by what the aircraft was bought for:

| Purpose | Primeva 1 | Boscali 1 | Primeva 2 | Boscali 2 |
|---|---:|---:|---:|---:|
| Escorts (cover a lift, a platoon or an objective) | 872 | 1,034 | 850 | 947 |
| Standing home patrol (rung 1) | 504 | 325 | 378 | 195 |
| Strikes and everything else | 182 | 203 | 377 | 493 |

The home patrol is a fifth to a third of air spending in the first block and falls to an eighth by
the second. Demoting it would move very little money.

---

## 3. Is it accelerating?

No. It is a plateau held up against a ceiling.

| Measure, per review | Primeva 1 → 2 | Boscali 1 → 2 |
|---|---|---|
| Total spend | 94 → 203 | 119 → 212 |
| Air spend | 58 → 107 | 58 → 109 |
| Air share | 62% → 53% | 48% → 52% |
| Airframes bought | 1.0 → 2.0 | 1.1 → 2.3 |
| Airframes lost | 0.93 → 1.13 | 0.37 → 1.67 |

Income roughly doubles between blocks and air spending doubles with it; the share barely moves. What
does escalate is the **wanted** count. Primeva's home patrol target climbs from two to three to four
and then sits at exactly four — its hard ceiling — for every review of the second block. The
reinforcement call behaves the same way: 37 of 65 calls asked for exactly six fighters, which is the
cap the code sets. Both growth terms are hard against their limits. The caps are the only thing
stopping a runaway, and they are doing their job. Remove them and the spiral is unbounded.

---

## 4. Where aircraft die

| | Primeva | Boscali |
|---|---:|---:|
| Aircraft lost | 42 | 35 |
| Median distance from its own nearest base | 9.0 km | 1.5 km |
| Lost within 10 km of its own base | 57% | 64% |
| Lost within 20 km of its own base | 83% | 82% |
| Median seconds airborne before dying | 120 | 270 |
| Killed by enemy aircraft | 100% | 100% |
| Killed by ground air defence | 0 | 0 |

Aircraft are dying two to four minutes after takeoff, within sight of their own runway, shot by the
other side's fighters. Not one airframe in the log was lost to a surface-to-air weapon. This is the
airbase-versus-airbase picture exactly as described, and it is the single most solid finding here.

---

## 5. How little ground happens

| Event | Primeva | Boscali | Total |
|---|---:|---:|---:|
| Platoons raised | 58 | 61 | 119 |
| Platoons that actually formed up | 34 | 28 | 62 |
| Forward bases ordered | 3 | 1 | 4 |
| Forward bases completed | 0 | 1 | **1** |
| Control points changing hands | 0 | 0 | **0** |
| Air taskings issued | — | — | 309 |
| Of which close air support | — | — | **0** |
| Deliberate strikes flown | 7 | 4 | 11 |
| Sorties reporting themselves outnumbered and holding back | 109 | 189 | 298 |
| Sorties abandoning the objective and joining the home patrol | 87 | 40 | 127 |
| Insertion flights declined or lost | 35 | 53 | 88 |

Every one of the 309 air taskings was a patrol. Against them stand 298 reports of a patrol sitting
back outnumbered and 127 of a patrol giving up entirely. Objectives sit a median of **39 km and 56
minutes by road** from the nearest depot, so the ground cannot walk anywhere; it must fly, which
needs an escort, which needs to beat the other side's fighters. That chain is what consumes the
economy. One forward base was built in two play blocks and no point changed hands.

---

## 6. Every place "the enemy has more aircraft" buys more aircraft

Seven loops, in descending order of how much money they move.

1. **The call for help.** `Operations/CommanderOperationsAirPosture.cs:691`, `CallForFighters` asks
   for `hostiles + 1` capped at six. Both sides run it, each side's fighters are the other's
   hostiles, and it is pinned at six in 57 per cent of calls. This is the engine.
2. **Escort sizing.** `Operations/CommanderOperationsAirPackages.cs:97`, `StrikeEscortWanted` wants
   at least one fighter per tracked hostile aircraft, bounded only by room under the airborne
   ceiling. No cap of its own.
3. **The patrol ladder.** `Operations/CommanderOperationsAirWing.cs:579`, `CapWanted` gives every
   active objective one fighter plus one per tracked hostile in its ring, capped at three — but there
   are thirty-odd objectives.
4. **Transport and lift escorts.** `Operations/CommanderOperationsAirPlatoonCap.cs:420` and `:313`,
   minimum two, then one per tracked hostile.
5. **The home patrol formula.** `Ai/CommanderEnemyCommanderLadder.cs:144`, `WantedHomeCap` adds one
   fighter per two hostiles near base **and one per recent loss**. The loss term is a second,
   independent loop: being shot down makes you buy more of the thing that was shot down.
6. **The air fund ceiling.** `Ai/CommanderEnemyCommanderAir.cs:82`, `AirFundCeiling` sizes the bank
   to the number of open air shortfalls, so more unmet air demand banks more money for air.
7. **The counting itself.** The hostile figure is a radar picture, not a head count. The median
   sortie reported one or two of ours against five to seven hostiles while neither side ever had more
   than about thirty aircraft alive. Both sides over-read the other and buy against the inflated
   number.

---

## 7. Options, ranked

**1. Cap air as a share of the pot, above the ladder.** *Recommended.* Add one bound across rungs 1
and 2 together so no review may commit more than a set share of its pot to airframes; the rest flows
to the rungs below. Changes `Ai/CommanderEnemyCommanderService.cs` (`ReviewPurchases` and
`SpendPlatoons`) with a new pure rule beside `Rung2Split` in the ladder file. The machinery exists:
`Rung2Split`, `AirSplit` and `AccrueFund` already do exactly this shape of work, and every one has a
self-check to copy. *Cost:* a commander genuinely losing the air war can no longer answer. *Risk:*
low, and reversible by a setting. *Work:* small — one function, one self-check, one setting, one
slider.

**2. Delete the loss term from the home patrol formula.** *Recommended, do with option 1.* One line
in `WantedHomeCap`. Losing fighters currently raises the number of fighters wanted, which is straight
positive feedback on attrition and the reason Primeva's target climbed to its ceiling and stayed
there. *Cost:* a commander that loses its patrol replaces it more slowly. *Risk:* low; the threat
term still answers a real raid. *Work:* trivial — a constant and a self-check case.

**3. Give up the objective instead of reinforcing it.** An outnumbered sortie currently asks for
`hostiles + 1`. Make that a decision: if the fighters needed cost more than the objective is worth,
stand the sortie down and let the money go to the ground. `GiveUpSortie` and `StandDownSortie` in
`CommanderOperationsAirPosture.cs` already exist and already carry the loss cooldown; only the
trigger is new. *Cost:* the commander concedes parts of the sky. *Risk:* medium — its lifts then
cannot reach those objectives, and lifts are already the ground's bottleneck. *Work:* medium.

**4. Stop the patrol walking home.** `ClearFallbackPoint` walks a falling-back sortie step by step
toward its own base and, when nothing on the line is clear, stands it down over the runway — 127
times in this log. Hold a hard line instead: a fixed minimum distance from home below which a sortie
is never allowed to retreat, and refuse to launch one whose objective is unreachable. Changes
`CommanderOperationsAirPosture.cs` and two existing settings (20 km posture ring, 15 km fallback
distance). *Cost:* none in money. *Risk:* low. *Work:* small. This treats the geometry symptom
directly but moves no money, so it is a companion to option 1, not a substitute.

**5. Make holding ground pay.** Tie income to held points, depots and forward bases so an air-only
strategy loses on the scoreboard. `Ai/CommanderCaptureService.cs` already knows who holds what;
`Economy/CommanderEconomyService.cs` would need a new income hook, guarded by `hq.IsServer`. *Cost:*
changes the shape of every match. *Risk:* high, and it rewards something that happened zero times in
this log — no point changed hands at all, so there is nothing yet to reward. *Work:* large; needs its
own track.

**6. Make attrition hurt.** Raise the replacement price or add a delay after a loss.
`Ai/CommanderEnemyCommanderAttrition.cs` already keeps the launch-and-loss ledger, so the data is
there. *Cost:* small in code. *Risk:* the losing side spirals downward instead of upward, which may
play worse than what happens today. Do option 2 first; it is the same idea with the sign corrected.
*Work:* small to medium.

**7. Make air demand follow ground need rather than enemy count.** Change the growth term in
`CallForFighters` and `CapWanted` from "hostiles tracked" to "what this sortie is covering". The
shared envelope `SortieElementWanted(floor, grown, cap)` was built for exactly this — only the growth
term differs between its four callers. *Cost:* the commander stops contesting the sky on principle.
*Risk:* high, and it attacks the loop at its strongest point while the lift chain still depends on
winning locally. *Work:* medium. Worth doing eventually, not first.

**Recommended set:** options 1, 2 and 4. Together they guarantee ground funding, remove the
attrition feedback, and stop the patrols collapsing onto their own runways — roughly one small track.

---

## 8. The one question for the developer

**Should a commander be allowed to concede air superiority over most of the map?**

Everything above turns on this. Objectives sit 39 km and nearly an hour by road from the nearest
depot, so the ground force can only reach them by air, and every air move needs an escort that can
survive the other side's fighters. Capping air spending funds the ground rung, but if the lifts then
cannot get through, the extra money buys vehicles that never leave the depot — and one forward base
in two play blocks suggests that is already close to happening. Either the ground must be given a way
to reach objectives without air cover (shorter road moves, closer starting depots, a ground-convoy
route for forward bases), or the air cap has to be set high enough to keep the lift chain alive. The
answer decides whether option 1 alone is enough or whether a ground-mobility track has to come with
it.
