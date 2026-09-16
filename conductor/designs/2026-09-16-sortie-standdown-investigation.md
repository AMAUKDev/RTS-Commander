# Sortie stand-down investigation — 2026-09-16

**Scope:** why air sorties appear to hold back and dissolve. Investigation only; no code changed.

**Evidence:** `BepInEx\LogOutput.log`, the session beginning at the last `Ground Control (RTS) 0.7.6.0
loaded` line (raw line 5037), 28,110 lines. Mechanics read from
`Operations/CommanderOperationsAirPosture.cs` (whole file), `CommanderOperationsAirPackages.cs`
(`FirstClearAlong`), `CommanderOperationsAirRetask.cs`, `CommanderOperationsAirHomeCap.cs`, and the
two design notes for the fallback posture and the survival layer.

## 1. The premise is wrong: no sortie stood down

The brief counted 1,475 occurrences of the phrase "stands down". Every one of them is a different
message. The give-up message the posture writes is `not reinforced in N min; stands down`, and it
appears **zero** times in the whole session. All 1,475 are the close-air-support loss cooldown,
`loses an airframe over X: CAS stands down for N min` — an aeroplane was shot down, and the
commander paused close air support over that objective. They are combat losses, not abandoned
sorties.

| Event | Count |
|---|---|
| Aircraft lost (`loses an airframe over …: CAS stands down`) | 1,475 |
| Posture give-ups (`not reinforced in N min; stands down`) | **0** |
| Sorties entering the outnumbered hold (`outnumbered N v M … falls back`) | 1,220 |
| Sorties leaving it (`reinforced N v M; re-engages`) | 1,105 |
| Repeat "still outnumbered" reports while holding | 1,772 |
| Sorties entering the air-defence-belt hold | 43 |
| Holds that ended because every fighter was dead | 96 |

So nothing waited five minutes and dissolved. What is actually happening is the opposite, and it is
worse.

## 2. What is actually happening: the hold flaps on and off

A hold is meant to be entered once and left once. Instead, sorties enter and leave it continuously.
There were 1,012 complete cycles of re-engage followed by another fall-back, spread over 66 named
sorties; 22 sorties did this ten times or more, and the two busiest (the enemy commander's patrols
over Resource Site 15 and Crossroads 2) account for over 300 fall-backs between them.

The tell is in the friendly numbers. The count of our own fighters swings by a median of **13** at
each flip, in both directions, while the hostile count does not move.

| Moment the number is taken | Median "ours" | Median "hostiles" |
|---|---|---|
| Deciding to fall back | 3 | 9 |
| Reporting while held | 10 | 15 |
| Deciding to re-engage | 15 | 9 |

Thirteen fighters do not arrive and evaporate every few seconds. A worked example from the player
side's commander over Resource Site 5, three consecutive messages:

```
RESOURCE SITE 5: outnumbered 3 v 8 … falls back 15 km toward FOB ROAD POINT 12 and calls for 9 fighters.
RESOURCE SITE 5: reinforced 17 v 8; re-engages.
RESOURCE SITE 5: outnumbered 2 v 8 … falls back 15 km toward FOB ROAD POINT 12 and calls for 9 fighters.
```

## 3. Root cause: the friendly count changes meaning when the hold turns on

There is one root cause and it explains every number above.

The rule that decides whether one of our fighters counts as being "there" for a sortie is
`FighterPresentFor` in `Operations/CommanderOperationsAirPosture.cs`. It has two clauses. The first
counts a fighter within 35 km of the objective. The second counts a fighter within 20 km of the
sortie's fall-back point — **but only while the sortie is already holding**.

That second clause is switched by the very state the count is used to decide. When the sortie is
flying normally, only the objective clause applies and the count is small, so the sortie falls back.
The instant it is holding, the fall-back clause switches on, the count jumps, the sortie outnumbers
the enemy on paper and re-engages. The clause switches off again and it falls back. The loop is
self-sustaining and needs no enemy action at all.

The jump is large because of a second mechanism. The fall-back point is not simply 15 km back. It is
walked further toward home in 2 km steps until no hostile aircraft is within 20 km of it, and
`FirstClearAlong` in `CommanderOperationsAirPackages.cs` returns the airbase itself when no point on
the line is clear. With 9 to 30 hostile aircraft tracked, no point on the line usually is clear, so
the hold point collapses onto our own airbase — and a 20 km ring drawn round our own airbase counts
every fighter parked, orbiting or rearming at home. Those are the thirteen.

Two further consequences follow from the same defect:

- **The commander believes it has help that is not there.** A sortie logging `still outnumbered 19 v
  28` may have two aeroplanes actually at the hold point. The other seventeen are at base. One such
  sortie held for many minutes, reported nineteen fighters throughout, and ended with the message
  `its fighters are gone` — every fighter it really had was killed while stamped self-defence only.
  This happened 96 times.
- **The five-minute clock never fires**, because the false re-engage always beats it. That is why
  there are no give-ups and why the give-up behaviour has never actually been observed in play.

## 4. Hypotheses

| # | Hypothesis | Verdict | Evidence |
|---|---|---|---|
| 1 | The re-engage bar is unreachable because the hostile count does not fall on retreat | **Killed as the cause, confirmed as a real secondary effect.** The bar is reached far too easily, not too rarely: 868 of 1,220 fall-backs re-engage on the very next posture message. But the hostile count does rise while holding, 9 at fall-back to 15 while held, because retreating fighters sweep new hostiles into the "within 20 km of one of our fighters" ring. That lengthens genuine holds; it does not end them. |
| 2 | Reinforcements never arrive | **Killed.** Reinforcement is working hard: 1,000 retasks with the reason `contact outranks cover`. It is the demand that is absurd — the median call is for 11 fighters and the largest for 31, because the sortie asks for one more than an inflated hostile count. Lending from the home patrol is nearly unused, 20 times in the session. |
| 3 | Five minutes is too short to buy, launch and fly a fighter 15 km | **Untested and currently moot.** The clock never expired once, so the log contains no measurement of it. It becomes testable only after the flapping is fixed. |
| 4 | The two hold reasons interact and trap a sortie | **Killed.** The belt hold fired 43 times against 1,220 outnumbered holds. It is not material. |
| 5 | Sorties are opened over ground that should never be contested | **Partly supported, separate problem.** Sixty-six sorties produced 4,097 posture messages and the top five produced a third of them. The same handful of objectives is fought over repeatedly. Worth its own look, but it is not what is causing the waste here. |

## 5. Candidate fixes, ranked

**Fix 1 — stop the hold point collapsing onto the airbase (recommended).**
Bound the clear-point walk in `ClearFallbackPoint` so the hold point never retreats further than the
present radius from the objective, instead of falling through to the airbase. Then the 20 km ring
round the hold point lies inside the 35 km ring round the objective, the two counts agree whatever
the state, and the oscillation disappears without removing the protection the fall-back-point clause
was added for. Cost: a clamp and one self-check case, no new setting. Risk: when a raid is genuinely
wide, the bounded hold point may still have hostiles near it, which is the failure the walk was
written to prevent — the fighters would then be holding somewhere imperfect rather than at home.

**Fix 2 — judge both decisions on one population.**
Remove the `fallingBack` switch from `FighterPresentFor` so the same fighters are counted whether or
not the sortie is holding. Cost: the smallest possible change, one parameter. Risk: it settles on
the wide count, so a sortie would rarely fall back at all, and the original complaint that started
this work returns. Prefer it only if Fix 1 proves awkward, and only together with Fix 3.

**Fix 3 — make the decision sticky (worth adding on top of Fix 1).**
Require the sortie to have held, and to have been engaged, for a minimum period before the opposite
decision may fire. Two new settings in the Operations section of the config, and two self-check
cases beside the existing `ShouldFallBack` and `ShouldReengage` cases. Cost: low. Risk: it caps the
flap rate without correcting the wrong number, so on its own it hides the defect rather than fixing
it; it is a safety net, not the fix.

## 6. Open item for the user

Once the flapping stops, the five-minute give-up clock will fire for the first time, and the demand
figures suggest it will fire often: sorties are asking for a median of eleven fighters. Whether the
right answer is a longer clock, a cheaper ask, or a rule that refuses to open the sortie at all is a
decision that needs the behaviour observed once in the running game first.
