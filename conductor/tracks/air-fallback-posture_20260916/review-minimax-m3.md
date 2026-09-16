# Air Fallback Posture — review

- **Reviewer:** minimax-m3 (second-opinion panel)
- **Track:** air-fallback-posture_20260916 · 2026-09-16
- **Inputs read:** design.md, plan.md, `Operations/CommanderOperationsAirPosture.cs`, `Operations/CommanderOperationsLogistics.cs`, `Operations/CommanderOperationsAirWing.cs`, `Operations/CommanderOperationsAirAwacs.cs` (esp. ReconcileSorties and ReleaseSortie/ReleaseBoundAirframes), `Operations/CommanderOperationsAirRetask.cs`, `Operations/CommanderOperationsAirPackages.cs`, `Operations/CommanderOperationsAirRadarWatch.cs` (CountHostileAirInRing, BindCap, BindCas, SyncAirTasks, SyncBoundAirframe, IssueAirTask, PruneAirBook), `Operations/CommanderOperationsFob.cs` (CountLiftEscortsUp, FindLiftCover, LiftCoverIsUp), `Operations/CommanderOperationsAirPlatoonCap.cs` (IsLiftCoverLabel, AddCapDemand), `Operations/CommanderOperationsService.cs` (SelfCheck wiring, TickPersistent ordering), `AirCommand/CommanderAirCommandPilotHooks.cs` (GetActiveRoutePoint, ChooseMissionTarget, TargetOutsideSelfDefence, ConstrainMissionDestination), `AirCommand/CommanderAirCommandTypes.cs` (AirMission fields and constructor), `AirCommand/CommanderAirCommandService.cs` (IsRotaryPilot, ProcessAutoRecreate scheduling), `AirCommand/CommanderAirCommandOrders.cs` (TryTaskAiAircraft — the AirMission creator), `AirCommand/CommanderAirCommandMissions.cs` (TryAssignPendingAircraft — the relaunch path's new AirMission constructor), `AirCommand/CommanderAirCommandRecovery.cs`, `AirCommand/CommanderAirCommandMapOverlay.cs` (RemoveMission), `Ai/CommanderEnemyCommanderAirTasking.cs` (TryNearestOwnBase), `Core/CommanderSettings.cs` (five new keys, warm-up list).
- **Not edited.** All findings below are read-only; nothing was changed in the repo.

---

## Summary

The core idea is correct: stamp `HoldOverride` + `SelfDefenceRadiusMeters` onto the AirMission, raise `CapsWanted` + `InContact` so the existing pipeline brings help, and stand the sortie down if no help comes. Self-checks are comprehensive. The reconcile carries the live facts correctly. Division of responsibility with the existing retask is preserved (lift covers still cannot be stripped). Counting is consistent (one `IsFixedWingCombatAircraft` definition used for both sides; AWACS excluded; Awacs sortie skipped). Per-tick cost is bounded by the existing `CountHostileAirInRing` walk + a single fixed-wing predicate per bound airframe.

The implementation is solid for the design's stated intent. The findings below are mostly about two narrow seams where the stamp misses a freshly-bound fighter for up to one watch interval (≤5 s), and one documentation/contract concern about the strike give-up path. None of these are blockers; all are noted with a fix shape.

---

## Findings, ranked by severity

### S1 — BindCap / BindCas do NOT call ApplyPosture; fillers and retaskers get the stamp on different frames

**Files:**
- `Operations/CommanderOperationsAirRadarWatch.cs:310` (`BindCas`) and `:396` (`BindCap`)
- `Operations/CommanderOperationsAirRetask.cs:184` (`TaskOntoSortie`) — calls `ApplyPosture` at line 198

**Scenario.** Sortie A is falling back at T0. At T0+25 s, `Review()` runs and calls `FillSorties` → `BindCap` for two freshly-bought idle fighters. `BindCap` writes `AreaCenter = sortie.Center` (or FormUpPoint) via `IssueAirTask` → `TryTaskAiAircraft` (`AirCommand/CommanderAirCommandOrders.cs:91`). `HoldOverride` is NOT set there. The fighters begin transiting toward `sortie.Center`, not toward `FallbackPoint`. On the next watch tick (≤5 s later), `WatchAirPosture` → `ApplyPostureToAll(sortie)` (the "still short" branch at `CommanderOperationsAirPosture.cs:202-203`) sets `HoldOverride`, and the route helper (`CommanderAirCommandPilotHooks.cs:184`) returns the override.

Up to one watch interval (default 5 s, clamped to ≥0.5 s via `Mathf.Max(0.5f, CommanderSettings.LogisticsWatchSeconds)` at `CommanderOperationsService.cs:433`), a freshly-bound fighter flies to the area centred on the objective rather than toward the airbase. On most missions this is invisible. On a hot match with short sorties and fast fighters, the fighter crosses several km before turning.

**Why retask works but fill does not.** `TryRetaskOne` (`CommanderOperationsAirRetask.cs:51`) ends in `TaskOntoSortie` (`AirRetask.cs:184`) which calls `ApplyPosture(sortie, aircraft)` as its last line (line 198). The retasker's stamp is set in the same microsecond as the area centre. The fill path (`FillSorties` → `BindCap`/`BindCas`) has no matching call.

**Fix shape (not applied).** Either add `ApplyPosture(sortie, aircraft)` after the bind log line in `BindCap`/`BindCas` (`CommanderOperationsAirRadarWatch.cs:317` and `:404`), or have the next `WatchAirPosture` tick followed hard by `BindCap`/`BindCas` so the 5 s gap collapses. The first option is cheaper and matches the retask path one-for-one. Verified as the gap, not the bug.

### S2 — `ApplyPosture` reads `TryGetMission`, which returns null for a recovered-airframe slot before `TryAssignPendingAircraft` repopulates `missions`

**Files:**
- `Operations/CommanderOperationsAirPosture.cs:287` (`CommanderAirCommandService.Instance?.TryGetMission(aircraft)`)
- `AirCommand/CommanderAirCommandService.cs:206-210` (`RecoverLandedAircraft` → `ProcessAutoRecreate` ordering)
- `AirCommand/CommanderAirCommandMissions.cs:747-778` (`TryAssignPendingAircraft` adds the new AirMission only after the next spawn registers)

**Scenario.** A fighter on a falling-back sortie runs out of ammo, `Returning` is set, the recovery sweep (`CommanderAirCommandRecovery.cs:203`) parks it on the deck, `HandleAircraftReturned` (`CommanderAirCommandPilotHooks.cs:67`) calls `RemoveMission`. The mission is gone from `missions`. `AutoRecreate` queues the recipe; on the next `ProcessAutoRecreate` tick, `TrySpawnFromRecipe` launches a fresh airframe and `TryAssignPendingAircraft` (`AirCommand/CommanderAirCommandMissions.cs:747`) inserts a NEW `AirMission` (`CommanderAirCommandTypes.cs:185`) with `HoldOverride = null` and `SelfDefenceRadiusMeters = 0f` (constructor leaves them default).

`ApplyPosture` on the next watch tick (`CountFightersUp` returns false for the returned-not-yet-relaunched frame because the recipe's queued mission doesn't have a registered airframe yet, so the relaunched airframe is bound by `BindCap`/`BindCas` once it lands, which is S1's gap again), then on the watch after that, `ApplyPostureToAll` stamps the new mission. The new fighter flies to `AreaCenter` (which is the recipe's centre, frozen at launch time, not the live sortie centre) for one watch interval.

**Practical impact.** Bounded by the recovery/relaunch latency (typically a few seconds for the recovery plus `RelaunchRetrySeconds` = 5 s plus spawn time). For a fallback that already has hold/override, the recovery-to-relaunch gap can be 15-20 s of no posture. Most likely invisible in a hot fight because the sortie has likely re-engaged or given up by then.

**Fix shape.** As S1, plus a tweak to `BindCap`/`BindCas` calls. Verified as a gap.

### S3 — `GiveUpSortie` sets `sortie.CooldownUntil` and clears `Caps`/`Cas`, but the lifted sortie stays on `state.AirSorties` for the coerce's `CooldownUntil` to take effect. `LiftCoverIsUp` does NOT check `cover.CooldownUntil` at all.

**Files:**
- `Operations/CommanderOperationsAirPosture.cs:306` (`GiveUpSortie`)
- `Operations/CommanderOperationsFob.cs:2008` (`LiftCoverIsUp`)

**Scenario.** A lift cover gives up. `GiveUpSortie` ends fallback, sets `CooldownUntil = Time.time + CasLossCooldownMinutes(...) * 60` (which scales with observed AD and is typically 2-5 minutes), clears `Caps` and `Cas`, but leaves the sortie in `state.AirSorties` (per plan deviation note). `DescribeDeliveryHopelessness` (`CommanderOperationsLogistics.cs:262-268`) now sees `cover.FallingBack = false`, `CountLiftEscortsUp(cover) = 0`, so `escortsLost = true` and the transport waits/diverts — the design's contract is met.

However, `LiftCoverIsUp` (`CommanderOperationsFob.cs:2008`) reads `cover?.CapsWanted ?? CommanderSettings.LiftEscortMinimum`. It does NOT read `cover.CooldownUntil`. So a fresh lift load dispatched to the same order immediately after give-up would re-launch with the same cover, only to find no escorts and wait. It's not catastrophic (the wait will trigger the same hopeless loop), but the cooldown's intent (don't relaunch immediately) is silently bypassed.

**Why this is borderline a track issue.** `GiveUpSortie` introduces a new use of `CooldownUntil` on the cover — previously only loss events stamped it. The semantics of "this cover took a cooldown" are now real, but the only reader of cover cooldowns (`LiftCoverIsUp`) doesn't check them. The new use leaks state that nothing reads.

**Fix shape.** Either (a) add `if (cover.CooldownUntil > Time.time) return false;` at the top of `LiftCoverIsUp` (or factor `IsSortieHot` to share), or (b) document in `GiveUpSortie`'s summary that the cooldown is a "no immediate relaunch" hint, and rely on the transport's hopeless-loop to handle the gap. (a) is the safer match to what `CooldownUntil` already means elsewhere in the file. Verified as a leak.

### S4 — `GiveUpSortie` runs `EndFallback` first (clears posture), then runs the release; released fighters are stamped CLEAR at the moment they were just stamped HOLD. Fine for outgoing fighters; consider the released fighter that retasks onto ANOTHER falling-back sortie.

**Files:**
- `Operations/CommanderOperationsAirPosture.cs:306-330` (`GiveUpSortie`)
- `Operations/CommanderOperationsAirAwacs.cs:822-850` (`ReleaseBoundAirframes` → `ReleaseDisposal` → `TryRetaskReleasedAirframe` → `BindCap`/`BindCas`)

**Scenario.** A peak-AS sortie gives up. Its 2 fighters are released via `ReleaseBoundAirframes`. `TryRetaskReleasedAirframe` finds a nearby falling-back CAP sortie (in contact, short on fighters), `BindCap` runs. `BindCap` writes `AreaCenter` (per S1's gap). Within 5 s the relaunched fighter's stamp will be set by `ApplyPostureToAll` on the new parent. End-to-end: this works, but it's bounded by one watch tick.

**Subtle case.** If the second falling-back sortie ALSO gives up before that 5 s tick (cascading give-ups), the fighter sees no override for the entire fallback+give-up window. It flies to the parent's AreaCenter, where it fights whatever is there. Acceptable given the design — these are fallback-and-recover scenarios, not the steady-state posture.

**Action.** None; document at design.md as a known property of the same `BindCap`/`BindCas` semantics gap from S1.

### S5 — Reading order at give-up: `if (sortie.Kind == CommanderSortieKind.Strike && ReferenceEquals(state.StrikeSortie, sortie))` runs BEFORE the airframe release. If the strike is the given-up sortie, `ForgetStrikeSortie` runs first, then the airframes release.

**Files:**
- `Operations/CommanderOperationsAirPosture.cs:320-329`

**Order check.** Good: `ForgetStrikeSortie` clears `state.StrikeSortie` and `state.StrikeClockMinutes`, then `ReleaseBoundAirframes` runs on the same `sortie` reference, releasing both strike airframes and escorts. The strike closure log line follows `ForgetStrikeSortie`. A second `ReleaseBoundAirframes` for the strike is not duplicated. Verified.

**Concern.** `ReleaseBoundAirframes` offers released airframes to `state.AirSorties` (the parameter). `GiveUpSortie` passes `state.AirSorties` as `openSorties` (the fourth argument at `CommanderOperationsAirPosture.cs:326-328`). The released airframes may be retasked onto another open sortie. If that open sortie's `FallingBack` is true, the retask path (`TaskOntoSortie` → `ApplyPosture`) does the right thing immediately. If not, the retask goes onto the standard area. All good.

### S6 — Reconcile copies `InContact = true` and `CapsWanted = Max(wanted, live)` ONLY when `live.FallingBack`. A sortie that clears FallingBack on this review will NOT carry the post-fallback demand forward.

**Files:**
- `Operations/CommanderOperationsAirAwacs.cs:653-657`

**Logic check.** At the reconcile moment:
```csharp
if (live.FallingBack)
{
    wanted.InContact = true;
    wanted.CapsWanted = Mathf.Max(wanted.CapsWanted, live.CapsWanted);
}
```
This means: the carry-forward only happens while the SORTIE is still falling back. The watch's `CallForFighters` re-asserts `CapsWanted = Max(CapsWanted, ReinforcementWanted)` every tick the sortie is short. So between watches and reviews, `CapsWanted` is held high by the watch itself. Between reviews and within the same review, the reconcile's max carries it. Once the sortie ends fallback (re-engage or give-up), the resize rule (`CapWanted(hostileAir)`) takes over on the next review.

Cascade scenario. A sortie becomes outnumbered, watches call for 6 fighters (`CapsWanted = 6`). Buy lands 4 within one review cycle (the review sizes it BACK DOWN — but the reconcile keeps it max'd). On next review, the buy still wants to see "is anything short, top up". The Max hold in the reconcile keeps the demand visible. Good. Verified that there's no narrow frame where `CapsWanted` resets and the buy sees nothing.

### S7 — `IsFixedWingCombatAircraft` returns false for tiltwings (rotary-piloted, but the design says only helicopters are exempt)

**Files:**
- `Operations/CommanderOperationsAirPosture.cs:93` (the new fixed-wing predicate)
- `AirCommand/CommanderAirCommandService.cs:585` (`IsRotaryPilot` returns true for `Pilot.PilotType.Helo` OR `Pilot.PilotType.Tiltwing`)

**Behaviour check.** Tiltwing → `IsRotaryPilot` returns true → `IsFixedWingCombatAircraft` returns false. Counted on neither side. Consistent with `CommanderAirCommandScoring.cs:954` (`IsRotaryAirframe` definition) and the rest of the wing. No aircraft labelled as tiltwing is ever bought for a CAP slot (per `CommanderOperationsAirWing.cs:780` rules). Verified consistent.

### S8 — `FallingBack` clear log line is silent when there's nothing to clear

**Files:**
- `Operations/CommanderOperationsAirPosture.cs:166-171` — the "its fighters are gone; the fallback ends" line fires only if `sortie.FallingBack` was true at the moment `CountFightersUp` returned 0.

**Scenario.** Post-give-up, the sortie still on `state.AirSorties` has `FallingBackSince = -1`, `Caps.Count == 0`. Next watch, `CountFightersUp == 0`, the `if (sortie.FallingBack)` is false (since `EndFallback` cleared it), so no log line is written. Good — no flapping. But ALSO: if the sortie is mid-fallback when its last fighter dies (shot down over the objective), `CountFightersUp == 0`, `FallingBack == true`, line fires, `EndFallback` clears. Then nothing happens until give-up — the sortie sits on the list with no fighters, no fallback, no cooldown, and the review's sizing may refuse to fill it (no contact, no cap). That's a sort of "dissolved by enemy action" that's not logged. Acceptable given the sortie will be dissolved by the minimum hold on the next review; document.

---

## Things I verified as fine

1. **Self-checks.** `CheckAirPosture` (`CommanderOperationsAirPosture.cs:333`) covers all five decision rules with named boundaries:
   - Fallback margins 0/-1 (off), 2 (hostiles 3 vs 2 no, 4 yes, 0 vs 2 yes).
   - Re-engage: parity (no), superiority by 1 (yes).
   - Give-up: 0 s no, 299 s no, 300 s yes, -1 no, 0 min off.
   - FallbackPoint: 15 km toward a 40 km base lands 15 km from centre/25 km from base; 10 km base clamps to base; base at centre returns centre; altitude preserved.
   - ReinforcementWanted: `5+1 = 6`, `0+1 = 1`.
   - TargetOutsideSelfDefence: 0f is off, 5999 in, 6001 out.
   - Config sanity: `AirFallbackMargin >= 1`, `AirSelfDefenceRadiusMeters > 0`, `AirFallbackDistanceMeters > 0`, `AirFallbackGiveUpMinutes * 60 > LogisticsWatchSeconds`.
   These hold at the boundaries. A defect (`hostiles - ours > margin` strict, no `>=`) would fail the named "2 v 4 falls back" case. Good.

2. **`CountHostileAirInRing` parameterisation.** `fixedWingCombatOnly = true` is wired through line 685 (`CommanderOperationsAirRadarWatch.cs:685`). One `IsFixedWingCombatAircraft` predicate, used for both sides (Reuse rule 4 satisfied). The ring radius defaults to `ObservedRadiusMeters` (8 km) per `sortie.Center`. Verified correct.

3. **`GetActiveRoutePoint` order.** HoldOverride is checked FIRST (line 184), AreaCenter is the fallback. ConstrainMissionDestination returns early while HoldOverride is set (line 235). Both correct.

4. **`ChooseMissionTarget` exemption.** In-radius targets are exempt from the mission-area restriction (line 343-346), and commanded targets (`ForcedTarget`) bypass the radius filter entirely (line 333). Verified against the plan's deviation note.

5. **`ApplyPostureToAll` covers both `Caps` and `Cas`.** Includes attack-helicopter CAS because rotary CAS must not be put in posture — the early return on `IsFixedWingCombatAircraft` correctly skips them. Verified against design §4.6.

6. **Lift cover retask source exclusion.** `IsLiftCoverLabel(source.Label)` is in the source filter at `CommanderOperationsAirRetask.cs:88`. A falling-back lift cover cannot be stripped. Verified.

7. **AWACS skipped.** Both at `WatchAirPosture` (`sortie.Kind == Awacs` continue at line 158) and at `TryRetaskOne` (target check at line 55) and `TryRetaskReleasedAirframe` (`MayBindToSortie`). Verified consistent.

8. **Strike go-in guarded.** `UpdatePackages` line 355-361: `if (sortie.FallingBack) continue;`. The companion dev notes confirm `SortieHoldsAtFormUp` is unchanged (per `design.md` deviation). Verified.

9. **`escortsLost` now reads `cover.FallingBack`.** `CommanderOperationsLogistics.cs:266` is exactly as the plan prescribed. Verified.

10. **`TryNearestOwnBase` returns `Airbase?` plus meters.** `CommanderEnemyCommanderAirTasking.cs:210` is one definition, two callers (loss line + posture). Reuse rule 3 satisfied.

11. **Per-tick cost.** `WatchAirPosture` runs every 5 s. Inner loop is `CountFightersUp` (O(Caps.Count), each a fast fixed-wing predicate), `CountHostileAirInRing` (O(trackingDatabase.Count), pre-existing), and per bound airframe `ApplyPosture` (TryGetMission lookup, two field writes). Per-aircraft is constant-time. The watch is cheap.

12. **`hq.IsServer` guard.** `TickLogistics` short-circuits at line 70 before `WatchAirPosture` is called. Verified.

13. **No NRE at plugin load or on the first tick with zero sorties.** The `Awacs` early-return + `CountFightersUp <= 0` early-return both run for empty sortie lists. `ApplyPosture` guards via `IsFixedWingCombatAircraft` (returns false on null aircraft). Self-check `CheckAirPosture` runs at plugin load (`CommanderOperationsService.cs:1928-1929`). No exceptions on zero sorties.

14. **`FallbackGaveUp` forwards to `LiftWaitedTooLong`.** One definition, two callers (per Reuse rule 4). The deviation note in plan.md is correct: the self-check exercises it under its own name.

15. **Review marker badge.** "fallback" appended to the review line when `sortie.FallingBack` (`CommanderOperationsAirHomeCap.cs:530-535`). Verified.

16. **Give-up cooldown uses the existing loss-cooldown sizing.** `SortieLossCooldownMinutes(awacs: false, airDefence, CasLossCooldownMinutes, AwacsLossCooldownMinutes)` is the same path `StampAirLoss` uses (`CommanderOperationsAirRadarWatch.cs:617`). New sortie cooldown matches an established contract (except see S3 about who reads it).

---

## What the evidence did not let me verify

- **In-game log lines.** The plan's deviation §15 says the developer closed the game before the reload fired; in-game log lines (the three posture lines, the "fallback" review badge, the lift cover "waits / diverts") are not observed yet. Cannot verify from code alone that the wording matches the user's expectations, or that the cadence of the lines (once-per-change vs once-per-tick) reads cleanly. The code reads once-on-change via `FallbackReported` (`CommanderOperationsAirPosture.cs:204-212`); the per-review badge is updated each review. Both look fine on paper; please exercise in-game before sign-off.

- **Five-minute give-up not exercised in-game.** The boundary at `FallbackGaveUp(300f, 5f) == true` is checked at load. The actual timer running for 5 scaled minutes against `Time.time - FallingBackSince` is not observed. `Time.time` is the Unity scaled clock; `CommanderScheduler.IsDue` uses the same. Scaled time pauses with the game. The 5-min default plus the review's 30 s cycle plus the buy's 2-3 min transit is plausibly short enough to matter — confirm by leaving a sortie outnumbered for 5 min and watching for the stand-down line.

- **Strike go-in delay across a re-engage.** The "the package goes in on the first review after the escort re-engages" claim (deviation note) requires: re-engage clears `FallingBack`; next review's `UpdatePackages` sees `sortie.GoneIn` still true (because the go-in decision was made before the fallback began, but the package's fighters were not gone-in). Cannot trace this from code alone — needs in-game.

---

## Recommendation

Approve with the two seams noted (S1 and S3). S1's effect is bounded to one watch tick; the cheapest fix is the one-liner `ApplyPosture(sortie, aircraft)` at the end of `BindCap` and `BindCas`, mirroring the retask path. S3 is a contract leak between the new `GiveUpSortie` cooldown and the only reader of cover cooldowns (`LiftCoverIsUp`); either the cooldown is checked or the doc says it's a no-op. Both fixes are small and low-risk.

The rest of the feature is correct, well-tested by the self-check table, and meets the design's intent as stated. The execution deviation log in `plan.md` is honest about the design choices that changed during implementation (FallOffGate vs LiftWaitedTooLong unification, ConstrainMissionDestination guard widened, IsFixedWingCombatAircraft shared, SortieHoldsAtFormUp kept naive, etc.) and each deviation is grounded in code, not paper.

Ready for merge once S1 and S3 are addressed or documented as accepted.
