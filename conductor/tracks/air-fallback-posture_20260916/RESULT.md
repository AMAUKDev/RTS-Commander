# Result — air-fallback-posture_20260916

**State (2026-09-16):** built and hot-installed (0 warnings, 0 errors), reviewed by the second-opinion
`default` panel (glm-5.3, kimi-k3, minimax-m3; reports `review-*.md` in this folder), review findings
fixed, **in-game verification outstanding** — the developer closed the game before the reload; the
self-checks and the log lines below run at the next launch.

## Review synthesis and what was done

| Finding | Reviewer(s) | Action |
|---|---|---|
| Fallback stamps survive a re-task outside `TaskOntoSortie` (release to another sortie, home patrol, landing, player) — fighter parks 15 km from a fight it left | glm HIGH, kimi HIGH | Cleared at the mission-reuse door (`CommanderAirCommandOrders.TryTaskAiAircraft`), in `SendReleasedAirframeHome` and `ReleaseToPlayer`; `BindCap`/`BindCas` re-stamp after the task |
| Minimum-hold window lowers a falling-back sortie's `CapsWanted` and clears `InContact`, so the same review strips it | kimi | `HoldSortie` leaves both alone while `FallingBack` |
| Re-engage fires when hostiles leave the objective ring while chasing the fighters | glm, kimi | Hostiles = max(ring at centre, ring at fallback point) while falling back |
| Lift launch gate counts a falling-back escort as up; load launches then recalls | glm | `LiftCoverIsUp` holds the load while the cover is falling back |
| Our count excluded fixed-wing strike aircraft while theirs counted | glm | `CountFightersPresent` counts `Caps` + `Cas` fixed-wing combat aircraft |
| Fighters just bought and still far away counted toward re-engage | self (during review) | Decisions weigh fighters within `AirFallbackDistanceMeters + ObservedRadiusMeters` of the centre |
| Relaunched airframe gets a fresh mission without stamps (≤ 5 s gap) | minimax S2 | Accepted: the 5 s tick re-stamps |
| `LiftCoverIsUp` does not read the give-up cooldown | minimax S3 | Accepted: after give-up the cover's fighter list is empty, so the gate already reads 0 up |

## Verify at next launch (Ground Control Duel or the current mission)

1. `Ground Control (RTS) 0.7.6.0 loaded` and NO `Operations self-check FAILED` after it.
2. `<sortie>: outnumbered N v M within 8 km; falls back 15 km toward <base> and calls for K fighters.`
3. Review line shows ` fallback` on that sortie and its wanted fighters raised.
4. `<sortie>: reinforced N v M; re-engages.` once fighters arrive — or `<sortie>: not reinforced in 5 min; stands down.`
5. A lift whose cover falls back: `lift for … holds at the form-up point: waiting for the escort, which is outnumbered and falling back.`
6. No exception lines.
