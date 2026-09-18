# Unit economy — fewer live units, same war

**Track:** unit-economy_20260918 · **Date:** 2026-09-18 · **Status:** approved by user
**Evidence:** `conductor/designs/2026-09-17-frame-rate-investigation.md` and the measured readings below

## 1. Why

The frame rate collapses over a long match, and the measurement line settled why. Cost grows roughly
as the SQUARE of live unit count, so halving units cuts frame cost to about a quarter.

| Time | Frame time (ms) | Live units | If linear | If squared |
|---|---|---|---|---|
| 0:34 | 8.7 | 157 | 8.7 | 8.7 |
| 13:01 | 14.7 | 301 | 16.7 | 32.0 |
| 20:30 | 46.8 | 427 | 23.7 | 64.4 |
| 29:36 | 120.0 | 480 | 26.6 | 81.3 |

Squared fits; linear is nowhere near. Something in the GAME compares units against each other — the
developers added multithreading for sight checks, which is that shape. We cannot fix the game. We
can field fewer units.

Composition at the worst point, both sides: 214 ground vehicles, 158 buildings (133 of which came
with the mission), 55 aircraft, 45 sitting idle in reserve.

**Ground vehicles are the whole problem**: 14 at the start, 214 at the end, two-thirds of all growth.
Aircraft are a rounding error, so air packages and insertions are safe.

Proven innocent by the same data: the mod's drawing (on-screen images read zero while the frame rate
got worse), wrecks (never above six), and memory (doubled while frame time went up fourteenfold).

## 2. What the user has chosen

Four changes. All must preserve picket capturing, platoon frontline warfare, air insertions and air
package sorties. The user explicitly did NOT choose "fewer, better vehicles".

**2.1 Cash in the idle reserve.** Forty-five vehicles sit unassigned. Anything not assigned to a
platoon, picket or order for longer than a set time is sold back. No gameplay is lost — they were
not fighting. Quickest win and needs no balance judgement.

**2.2 Smaller garrisons on points.** Around forty picket points each hold several vehicles. Reduce
the standing garrison per point to a setting, default one. Capturing still works: a point needs
something standing on it, not a crowd. Accept that a point becomes easier to take.

**2.3 A ceiling on live ground units, per faction.** Above it the commander replaces losses but does
not grow. Predictable frame rate, and it forces the commander to choose where its strength goes
rather than accumulating everywhere. A setting, so it can be tuned in play.

**2.4 Retire units from quiet ground.** A platoon or picket holding ground nobody is contesting, and
that has not been contested for a set time, is cashed in; the commander re-raises there if the front
moves back. This is the user's "sleep" idea done the way that actually helps, since sleeping a unit
would not remove it from the game's pairwise costs.

## 3. How they interact, which matters more than any one of them

All four reduce the same number, and together they could strip the map bare. The ceiling is the
backstop; the other three are how the commander stays under it gracefully rather than by refusing to
buy. Order of application when the commander is over the ceiling: cash in idle first, then quiet
ground, then refuse growth. Never disband something in contact, never disband the last holder of a
point unless the point is being deliberately given up, and never leave a held point with nothing on
it — that would hand it over, since ownership is re-derived from what stands there.

## 4. Settings, all tunable in play

| Setting | Default | What it does |
|---|---|---|
| Idle reserve timeout | 3 min | Unassigned this long is cashed in |
| Garrison per point | 1 | Standing vehicles on a held picket point |
| Ground unit ceiling per faction | 80 | Above it, replace losses only |
| Quiet ground timeout | 5 min | Uncontested this long and the holder is cashed in |

Defaults chosen to land both sides near 160 ground vehicles rather than 214, which by the square law
is roughly half the frame cost. They are starting points, not conclusions.

## 5. Acceptance

1. Self-checks pass at load for every threshold and every "never do this" rule in §3.
2. In a 30-minute match the measurement line shows ground units flattening near the ceiling rather
   than climbing, and frame time materially better than 120 ms at the same point.
3. Points still change hands, platoons still fight at the front, insertions and packages still fly.
4. No held point is ever left with nothing standing on it as a result of this work.
