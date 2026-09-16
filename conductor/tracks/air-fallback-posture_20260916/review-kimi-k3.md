# Air Fallback Posture — review

- **Reviewer:** kimi-k3 (second-opinion panel)
- **Track:** air-fallback-posture_20260916 · 2026-09-16
- **Inputs read:** design.md, plan.md, `Operations/CommanderOperationsAirPosture.cs` (full, current head — includes the mid-review `CountFightersPresent` fix), `Operations/CommanderOperationsLogistics.cs` (TickLogistics, WatchLiftOrders, WatchDeliveryHope), `Operations/CommanderOperationsAirWing.cs` (sortie fields, SortieIsHoldable, SortieMinHoldSeconds=120 s, RetaskSourceRank), `Operations/CommanderOperationsAirAwacs.cs` (ReconcileSorties, HoldSortie, ReleaseSortie, ReleaseBoundAirframes, TryRetaskReleasedAirframe, SendReleasedAirframeHome), `Operations/CommanderOperationsAirRetask.cs` (RetaskToContact, TryRetaskOne, TaskOntoSortie), `Operations/CommanderOperationsAirDemand.cs` (PlanAirSupport ordering, fill loop cooldown), `Operations/CommanderOperationsAirRadarWatch.cs` (CountHostileAirInRing, SyncAirTasks, SyncBoundAirframe, BindCap, BindCas, IssueAirTask, IssueHomeCapTask, IssuePostureTask, ReleaseToPlayer), `Operations/CommanderOperationsAirPackages.cs` (UpdatePackages GoneIn sites, UpdateStrikePackage, ForgetStrikeSortie), `Operations/CommanderOperationsAirArad.cs:109-120` (Arad demand: CapsWanted=0), `Operations/CommanderOperationsAirPlatoonCap.cs` (AddLiftCoverDemand: Kind=Cap, InContact=hostileAir>0), `Operations/CommanderOperationsAirHomeCap.cs:530-535` (review line), `Operations/CommanderOperationsFob.cs` (CountLiftEscortsUp forwarder, LiftCoverIsUp), `AirCommand/CommanderAirCommandTypes.cs` (AirMission fields), `AirCommand/CommanderAirCommandPilotHooks.cs` (GetActiveRoutePoint, ChooseMissionTarget, ConstrainMissionDestination, TargetOutsideSelfDefence), `AirCommand/CommanderAirCommandOrders.cs` (TryTaskAiAircraft, TryAdoptAircraft, TryGetMission), `AirCommand/CommanderAirCommandService.cs` (TryReturnAiAircraftHome, ResetSession clears missions), `Ai/CommanderEnemyCommanderAirTasking.cs` (TryNearestOwnBase), `Ai/CommanderEnemyCommanderDefence.cs:66` (ThreatMemorySeconds=45 s), `Core/CommanderSettings.cs` (five keys + warm-up). Also read `review-minimax-m3.md` (the other panel reviewer) to avoid duplicating: its S1 (BindCap/BindCas stamp gap, ≤5 s) and S3 (LiftCoverIsUp ignores cover cooldown) stand against current code and are endorsed, not repeated.
- **Note:** the tree moved during this review — `CommanderOperationsAirPosture.cs` gained `CountFightersPresent`/`PresentRadiusMeters`/`FighterPresent` (a fix for reinforcements counting before they arrive). All findings below are against the current file, not the version in the other review.
- **Not edited.** Read-only; nothing in the repo was changed by this review.

---

## Summary

The state machine itself is sound: fall-back/re-engage hysteresis is real (hostiles ≥ ours+2 to enter, ours ≥ hostiles+1 to leave — no boundary flap between the two), the reconcile carries posture/cooldown/InContact correctly for matched sorties, the give-up path clears stamps before releasing, and the counting predicate is one definition used on both sides. The pure-rule self-check table is genuinely at the boundaries.

The headline finding the other review missed: **the reconcile's release path can dissolve a falling-back sortie without ever clearing the stamps**, leaving fighters parked at an abandoned hold point on a 6 km leash for the rest of that mission object. There are three independent triggers for it, one of which is the *ordinary* end of a lift cover. Plus one confirmed review-vs-watch flap of `InContact`/`CapsWanted` inside the 120 s sortie hold window.

---

## Findings, ranked by severity

### C1 — HIGH: a falling-back sortie released by the reconcile leaves stale `HoldOverride`/`SelfDefenceRadiusMeters` on its fighters forever

**Files:**
- `Operations/CommanderOperationsAirAwacs.cs:684-687` (unmatched sortie → `ReleaseSortie`)
- `Operations/CommanderOperationsAirAwacs.cs:815-850` (`ReleaseSortie` → `ReleaseBoundAirframes` — no posture clear anywhere)
- `Operations/CommanderOperationsAirAwacs.cs:915-916` (re-bind via `BindCap`/`BindCas` — no `ApplyPosture`)
- `Operations/CommanderOperationsAirRadarWatch.cs:458-484` (`IssueHomeCapTask` — no clear)
- `Operations/CommanderOperationsAirPosture.cs:325-340` (`ApplyPosture`, the only writer of both fields)

**Mechanism.** `ApplyPosture` is called from exactly three places: the posture tick (`ApplyPostureToAll`), and `TaskOntoSortie` (the contact-retask door). The *release* door routes through `ReleaseBoundAirframes` → `TryRetaskReleasedAirframe`/`BindCap`/`BindCas` (IssueAirTask only) or `SendReleasedAirframeHome`/`IssueHomeCapTask`/`TryReturnAiAircraftHome`. None of these touch the stamps. `EndFallback` — the only clearing helper — is called only from `WatchAirPosture` and `GiveUpSortie`. Once a sortie is released it is removed from `state.AirSorties` (`CommanderOperationsAirAwacs.cs:671,710`), so no future tick ever visits it: the stamps are permanent for that `AirMission` object.

`GetActiveRoutePoint` (`CommanderAirCommandPilotHooks.cs:184`) returns the stale `HoldOverride` ahead of the route and the area, and `SyncBoundAirframe`'s hands-off check (`CommanderOperationsAirRadarWatch.cs:246-252`) compares only Mode/AreaCenter against `AirIssued` — the override is invisible to it, so the sync never detects or repairs the condition. The fighter holds at the dead sortie's fallback point, engaging only within 6 km of itself, while the bookkeeping says it is bound to a healthy sortie.

**Trigger A (ordinary, not exotic) — lift cover.** A lift cover is `Kind = Cap` (`CommanderOperationsAirPlatoonCap.cs:324`), is demanded only while the order is `Delivering` (`:293`), and is **holdable** (`SortieIsHoldable` = Objective|Cap). Escorts fall back → `escortsLost` becomes true (`CommanderOperationsLogistics.cs:266`) → the hopeless rule recalls/withdraws the flights (`WatchDeliveryHope`). If the order completes, is given up, or leaves `Delivering`, the cover demand closes. It is then held for 120 s (`SortieMinHoldSeconds`) and released — all while still falling back, because the give-up clock (5 min) cannot beat the hold clock (2 min). Escorts carry stale stamps to their next sortie or the home patrol.

**Trigger B — strike escort loiter release.** `UpdateStrikePackage` (`CommanderOperationsAirPackages.cs:499-512`) releases a gone-in strike's escorts after `StrikeLoiterMinutes` via `ReleaseBoundAirframes` and clears `sortie.Caps` — with no `EndFallback`. A strike that went in, then began falling back (nothing prevents a gone-in sortie from entering posture), hits this path while stamps are on. Released escorts keep them.

**Trigger C — player takes the airframe.** `ReleaseToPlayer` (`CommanderOperationsAirRadarWatch.cs:290-305`) unbinds a player-ordered airframe but leaves the stamps on the mission; the player's own route is then overridden by the dead fallback point. Player-side AI commander mode only.

**Failure scenario.** Two escorts fall back off a lift, transport recalled, order lapses; review holds then releases the cover; escorts retask onto a healthy CAP. Both fly to and circle the old fallback point 15 km from their new objective, defending a 6 km bubble, and the review line gives no hint. They stay there until that mission object is destroyed by an RTB/relaunch. In a losing match this compounds — every dissolved fallback leaks blind fighters.

**Fix shape.** One clear at the release door: in `ReleaseBoundAirframes`, for each non-disabled airframe, `mission.HoldOverride = null; mission.SelfDefenceRadiusMeters = 0f` before the retask/home decision (same two writes as `ApplyPosture`'s cleared branch — that is the one definition, so factor a `ClearPosture(Aircraft)` next to `ApplyPosture`). `GiveUpSortie`'s `EndFallback` already covers its own path. S1's fix (`ApplyPosture` at the end of `BindCap`/`BindCas`) is complementary, not a substitute: it stamps the *new* sortie's posture but cannot clear a fighter sent to the patrol or home.

### C2 — MEDIUM: `HoldSortie` stomps `InContact=false` and `CapsWanted=Caps.Count` on a falling-back sortie every review, and the same review's retask then strips it

**Files:**
- `Operations/CommanderOperationsAirAwacs.cs:785-796` (`HoldSortie`)
- `Operations/CommanderOperationsAirDemand.cs:250-258` (review order: ReconcileSorties → FillSorties → … → RetaskToContact)
- `Operations/CommanderOperationsAirRetask.cs:94` (`RetaskSourceRank(source.InContact, …)` — `InContact` false makes it strip-able)
- `Operations/CommanderOperationsAirPosture.cs:247,299-304` (the 5 s tick re-asserts both)

**Scenario.** A holdable sortie's demand closes while it is falling back (platoon contact blinked, objective closed — the same condition family as C1-A). Each review: `HoldSortie` sets `InContact = false` and `CapsWanted = Caps.Count`; `RetaskToContact` in the *same* review reads the stomped `InContact` and treats the falling-back sortie as a quiet source — the design's "a sortie in contact is never a source" — and pulls a fighter off it (subject to the 90 s per-airframe hold). The 5 s tick then re-raises `InContact`/`CapsWanted`, which no review-time reader sees until the next review stomps them again. For up to 120 s the call for help is effectively off *and* the sortie is being cannibalised, while the log claims it is "holding 15 km back" and calling for fighters.

The retasked fighter itself is handled cleanly (`TaskOntoSortie` stamps it from the new sortie) — the damage is to the falling-back sortie's reinforcement and to the design contract in design.md §4.4.

**Fix shape.** `HoldSortie` should not flatten a sortie that is `FallingBack`: skip the `InContact`/`CapsWanted` stomp (or release-hold a falling-back sortie as still-in-contact). One-line guard, no structural change.

### C3 — MEDIUM (design tension, code correct as specced): re-engage can be earned by losing the picture, not by reinforcement

**Files:**
- `Operations/CommanderOperationsAirRadarWatch.cs:678` (45 s `ThreatMemorySeconds` freshness gate)
- `Operations/CommanderOperationsAirPosture.cs:220,231` (hostiles counted in a ring around the *fixed* `sortie.Center`; re-engage on ours ≥ hostiles+1)

**Scenario.** The fighters hold at the fallback point, 15 km from the centre. A pursuer is only counted if it is *tracked and within 8 km of the centre*: chase outside the ring (or out of sensor coverage for >45 s) and the count drains to zero, `ShouldReengage(ours, 0, 1)` fires, and the sortie "re-engages" — the log says "reinforced 4 v 0; re-engages" — flying back toward exactly the aircraft that chased it away. Sensors re-acquire on the way in, `ShouldFallBack` fires again, and the sortie can yo-yo between the two lines on an enemy that never left. The 45 s memory blunts tracking blinks but not a genuine pursuit out of the ring.

This is faithful to design §4.1 (the ring is the sizing ring around `sortie.Center`), so file it as a design question rather than a code defect: likely resolutions are counting hostiles near the *fighters* (fallback point) too, or requiring the ring to stay clear for N consecutive watches before re-engaging. Confirm in-game before changing anything.

### C4 — LOW: `ours` counts only `Caps`; design §4.1 said "Caps (and Cas that are fighters)" — undocumented deviation

**Files:**
- `Operations/CommanderOperationsAirPosture.cs:129-146` (`CountFightersUp` — Caps only, faithful to the moved `CountLiftEscortsUp` body)
- `conductor/tracks/air-fallback-posture_20260916/design.md:44`

**Impact.** Fighter-type airframes in `Cas` (a multirole jet bound to a CAS slot — the wing explicitly allows owned jets to fill non-rotary CAS slots) are *stamped* by `ApplyPostureToAll` ("the escorted flight turns with its escort") but never *counted* in the ours/hostiles comparison. A CAS flight of two fighter-types with `CapsWanted = 0` can never fall back at all (`ours=0` → skipped); a strike package is judged purely on its escorts, ignoring fighter-bomber CAS. Direction is safe (under-calling fallback, not over-), and `CountLiftEscortsUp`'s old behaviour is preserved as required, but the deviation list in plan.md does not mention it. Either count non-rotary combat Cas airframes (one line in `CountFightersPresent`) or record the cut in plan.md's deviation list.

### C5 — LOW: end of fallback leaves `InContact = true` and raised `CapsWanted` on the live object until the next review

**Files:**
- `Operations/CommanderOperationsAirPosture.cs:287-292` (`EndFallback` does not reset `InContact`)

**Impact.** Between the re-engage tick and the next review (~≤30 s), the only readers are tick-side (none read `InContact`); the reconcile rebuilds demand fresh and only carries the raise while `live.FallingBack` (`CommanderOperationsAirAwacs.cs:653-657`), so nothing acts on the stale raise. Verified as benign; noting it so nobody "fixes" the reconcile copy later without this context.

---

## Verified fine (evidence, code as of this review)

1. **Reconcile carry for matched sorties.** `CommanderOperationsAirAwacs.cs:650-657` copies all three posture fields and, only while falling back, forces `InContact=true` and `CapsWanted = max(wanted, live)`. The strike package's same-object path (`:619-624`) skips the copy so nothing is double-bound. Cooldown carried unconditionally (`:628`). Correct.
2. **Give-up path is internally consistent.** `GiveUpSortie` (`CommanderOperationsAirPosture.cs:351-375`) logs, clears stamps *before* releasing (so released fighters leave clean), sets cooldown before `ReleaseBoundAirframes` (self-retask excluded twice: `ReferenceEquals` at Awacs:880 and `CooldownUntil` at :881), abandons the strike through the shared `ForgetStrikeSortie`, and keeps the sortie listed so the reconcile carries the cooldown. Fill (`AirDemand.cs:165`), buy (`RadarWatch.cs:805`) and retask-target (`Retask.cs:29`) all respect that cooldown — no immediate re-open. The lift-cover give-up makes the transport react: `escortsLost` true via `FallingBack`/`0 escorts` (`Logistics.cs:266`) → `DeliveryHopeless` recall/divert. One limit: hopeless needs `hostileAirNearRoute` too, and a cover chased 15 km away may leave the route reading clean — pre-existing rule shape, design §4.5 as written.
3. **No retask loop with "contact outranks cover".** Strike sorties and lift covers can never be *sources* (`Retask.cs:82,88`); a falling-back sortie is `InContact` → unreachable as a source except in the C2 hold window. A raised demand can only take from quiet Objective/Cap sorties and the lendable home patrol — the pre-existing division of responsibility, as designed.
4. **Counting correctness.** Hostile side: freshness (45 s), non-disabled, `unit is Aircraft`, own-faction excluded, and the fixed-wing-combat filter, ring measured from `sortie.Center` (`RadarWatch.cs:670-697`). Our side: alive, non-rotary-pilot, ≥1 non-cargo station (`AirPosture.cs:93-123`). Tiltwing excluded as rotary — consistent with the whole wing's convention. Rotary never posture-stamped (`ApplyPosture` early return) and Arad sorties can never enter posture (`CapsWanted=0` at `AirArad.cs:115`, no Cap binds possible). `CountFightersPresent` short-circuits the destroyed-Unity-object case before touching `.transform` (`:183-186`).
5. **Pilot hooks.** `GetActiveRoutePoint` honours the override before advancing the route and does not advance the route while overridden (`PilotHooks.cs:184-187`). `ConstrainMissionDestination` returns early under the override (`:235`) — the deviation was necessary. `ChooseMissionTarget`: self-defence radius is measured from the *airframe*, commanded targets exempt, in-radius targets exempt from the mission-area restriction (`:332-350`). AirGuard long-shot cap still applied after the radius check. All correct.
6. **Server-only, load-safety, cost.** `WatchAirPosture` runs under the existing `hq.IsServer` guard (`Logistics.cs:70-73`), iterates an empty sortie list safely, pure self-checks touch no game objects at load. Per-tick cost is one tracking-database walk per sortie (same shape the 30 s review already does) plus a constant-time stamp per bound airframe. No allocation concerns beyond one small string per falling-back sortie per watch.
7. **Give-up clock semantics.** `Time.time` scaled, `IsDue` scaled — pause-consistent. Limit-inclusive via the shared `LiftWaitedTooLong`; `AirFallbackGiveUpMinutes*60 > LogisticsWatchSeconds` config check present.
8. **Self-check table.** Covers enter (2v3 no / 2v4 yes / 0v2 yes / margin 0 / −1), leave (5v5 no / 6v5 yes / 3v5 no), give-up (0/299/300/−1 s, 0 min off), fallback point (15 of 40 km → 15/25, altitude kept, 10 km clamp to base, base-on-centre), reinforcement (5+1=6, 0+1=1), self-defence (0 off, 5999 in, 6001 out), present-radius (15 000 in, 23 000 boundary in, 40 000 out) and the four config floors. A planted `>` for `>=` in `ShouldFallBack` fails the named "two fighters against four fall back at margin 2" — the boundary is genuinely covered. Registered at `CommanderOperationsService.cs:1929`.
9. **Settings.** Five keys with file-style rationale comments and warm-up touches (`CommanderSettings.cs:652-675, 966-970`).
10. **Mid-review fix is sound.** `CountFightersPresent` uses fallback distance + ring (23 km default) so on-station CAP orbits and the fallback point count, inbound reinforcements do not — and the self-checks exercise exactly/on/inside/outside. Consistent with the new `ours` semantics everywhere it is read.

## Not verifiable from code

- **In-game log lines and behaviour** (design acceptance 2–5): the posture lines, the " fallback" review badge, a lift cover waiting/diverting, and the stand-down line were not exercised (executor's record: game closed before reload). The chase-loop in C3 in particular needs a real match to judge.
- **Plugin-load self-check pass and the planted-defect proof** were not run at load (executor deviation, plan.md §Execution record). The pure functions read correctly, but rule 5 of the testing rules wants the named check seen failing once — still outstanding.

## Recommendations

1. **Fix C1 before merge.** Clear both stamps in `ReleaseBoundAirframes` (factor a `ClearPosture(Aircraft)` beside `ApplyPosture`); also fixes C1-B and C1-C through the same door. Then minimax's S1 (stamp in `BindCap`/`BindCas`) becomes harmless symmetry rather than a second leak to chase.
2. **Fix C2 with a one-line guard** in `HoldSortie` (`if (sortie.FallingBack) return;` before the InContact/CapsWanted flatten, keeping the log behaviour).
3. **Decide C3 as a design question after one in-game observation**; do not pre-emptively ship a fix.
4. **Record the Caps-only counting (C4)** either as code (count fighter-type CAS) or as a plan.md deviation note.
5. Minimax's S3 (`LiftCoverIsUp` should read `cover.CooldownUntil`) — endorse as a small follow-up; after C1's fix its blast radius shrinks further since a given-up cover's escorts will at least be clean.
6. Before sign-off, run the in-game pass and the planted-defect proof the executor could not.
