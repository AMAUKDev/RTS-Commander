# Air fallback posture — independent review (glm-5.3)

Reviewer: second-opinion session (glm-5.3:cloud), 2026-09-16. Read-only; findings only.

**Caveat — concurrent edit.** `Operations/CommanderOperationsAirPosture.cs` changed DURING this
review: a second session added `FighterPresent`/`CountFightersPresent`/`PresentRadiusMeters`
(the "a just-bought fighter counts before it arrives" fix). Everything below reflects the file as
it stood at the end of the review (the version with the present-radius fix, including the re-run
self-checks). Line numbers are that version.

**Build verified by the reviewer:** `dotnet build` → `Build succeeded. 0 Warning(s) 0 Error(s)`.

## What I investigated

Read in full: `design.md`, `plan.md` (with execution record), `Operations/CommanderOperationsAirPosture.cs`.
Read the touched regions and traced the callers: `CommanderOperationsLogistics.cs` (tick, hope rule),
`CommanderOperationsAirAwacs.cs` (reconcile 592-720, `HoldSortie`/`ReleaseSortie`/`ReleaseBoundAirframes`/
`TryRetaskReleasedAirframe`/`SendReleasedAirframeHome` 780-963, `SameSortie` 742-777),
`CommanderOperationsAirRetask.cs` (60-199), `CommanderOperationsAirRadarWatch.cs`
(`BindCap`/`IssueAirTask`/`IssueHomeCapTask` 380-499, `SyncBoundAirframe`/`ReleaseToPlayer` 223-305,
`CountHostileAirInRing` 660-697), `CommanderOperationsAirPackages.cs` (go-in 299-371, strike
472-525), `CommanderOperationsAirPlatoonCap.cs` (cover demand 286-338), `CommanderOperationsFob.cs`
(launch gate 2008-2043, forwarder 1993-2000), `CommanderOperationsAirWing.cs` (sortie fields
181-348, `RetaskSourceRank` 953-966, `SortieLossCooldownMinutes` 1273-1277),
`CommanderOperationsAirHomeCap.cs` (review line), `AirCommand/CommanderAirCommandPilotHooks.cs`
(1-390: mission lifecycle, hold/target hooks), `CommanderAirCommandOrders.cs` (100-215: mission
creation/reuse), `CommanderAirCommandService.cs` (missions dict, `TryReturnAiAircraftHome`,
`IsWinchester`), `CommanderAirCommandRecovery.cs` (full), `CommanderAirCommandTypes.cs`
(`AirMission` fields), `Ai/CommanderEnemyCommanderAirTasking.cs` (190-232), `Core/CommanderSettings.cs`
(643-675, 964-970), `CommanderOperationsService.cs` (registration 1928-1929). Grepped the whole repo
for `CooldownUntil`, `missions[`, `RemoveMission`, `CountFightersUp` callers, posture-field callers.

## Key findings

The feature is well built where it was planned: the reconcile carries the posture, the give-up
clears stamps before releasing, the retask door stamps, the cooldown gates every re-entry door,
self-checks cover the boundaries. The two high findings are one root cause on a path the plan never
named: the **release** door and the **mission-reuse** door do not clear the two new `AirMission`
fields.

---

### HIGH-1 — Stale posture stamps survive every release path that does not go through `TaskOntoSortie`

**Root cause.** When an existing mission record is reused, only `Mode`, `AreaCenter`, `Radius`,
`TargetAltitude` are rewritten — `HoldOverride` and `SelfDefenceRadiusMeters` are never cleared:
`AirCommand/CommanderAirCommandOrders.cs:112-124` (`TryTaskAiAircraft`, `retaskExisting: true`),
same in `TryAdoptAircraft` (`Orders.cs:152-164`) and `SetAircraftOrder` (`Orders.cs:187-205`).

**Failure scenario.** A falling-back sortie's fighters hold `HoldOverride` = the fallback point and
a 6 km self-defence radius (`CommanderOperationsAirPosture.cs:338-339`). Its objective stops
demanding (captured, tracking gone); after the 120 s minimum hold (`CommanderOperationsAirWing.cs:76`)
the reconcile releases it — `ReleaseSortie` (`CommanderOperationsAirAwacs.cs:686, 815-820`) — with
**no `EndFallback`**. Then:

- `TryRetaskReleasedAirframe` binds the fighter to another sortie through `BindCap`/`BindCas`
  (`CommanderOperationsAirAwacs.cs:914-916`), which call `IssueAirTask`
  (`CommanderOperationsAirRadarWatch.cs:396-428`) but never `ApplyPosture`; or
- `SendReleasedAirframeHome` → `IssueHomeCapTask` (`CommanderOperationsAirRadarWatch.cs:458-484`)
  puts it on the standing home patrol through the same mission-mutating door.

**Nothing ever clears the stamp.** The 5 s tick re-stamps only members of a FALLING-BACK sortie
(`Posture.cs:221-229, 248`); a stable sortie never touches its fighters' stamps. The idle sweep only
handles mission-less airframes (`IssuePostureTask` refuses mission-carrying ones,
`CommanderOperationsAirIdle.cs:182`). Recovery needs `mission.Returning && mission.RtbIssued`
(`CommanderAirCommandRecovery.cs:209`) — never set on these paths. Only destruction
(`NotifyUnitDisabled` → `RemoveMission`, `CommanderAirCommandPilotHooks.cs:25-31`) or
`ReturnToInventory` (`PilotHooks.cs:67-79`) removes the record.

**Symptoms.** A "home patrol" fighter that never guards home — it holds the old fallback point,
possibly over enemy ground, engaging nothing beyond 6 km, with its mission-area constraint disabled
(`PilotHooks.cs:235`). A fighter retasked to a stable sortie holding 15 km from the wrong objective,
counted as "present" only by luck. It persists until the airframe dies.

**Fix (one line at the door).** Clear `HoldOverride`/`SelfDefenceRadiusMeters` in the
retask-existing branch of `TryTaskAiAircraft` (`Orders.cs:119-123`) — safe because the posture tick
re-asserts both within one 5 s watch for genuinely falling-back sorties — plus the same in
`TryAdoptAircraft`, `SetAircraftOrder`, and `ReleaseToPlayer` (HIGH-2).

Note the paths that ARE clean: give-up clears stamps before releasing (`Posture.cs:356` before
`:371-374`); the retask door stamps (`CommanderOperationsAirRetask.cs:198`); the fill only binds
mission-less airframes; recovery/relaunch gets a fresh record (`HandleAircraftReturned` →
`RemoveMission`).

### HIGH-2 — Player re-task of a falling-back fighter leaves the override overriding the player

Same root cause, player-facing. The player gives an Air Command order to a fighter whose sortie is
falling back: the mission record is mutated (`Orders.cs:187-205`, stamps untouched). Next review the
hands-off check sees the Mode/AreaCenter drift and hands the fighter over —
`ReleaseToPlayer` (`CommanderOperationsAirRadarWatch.cs:246-252, 290-305`) removes it from the
sortie **without clearing the stamps**. From then on the player's route is overridden by the hold
point (`PilotHooks.cs:184-187`), targets beyond 6 km are refused (`PilotHooks.cs:338`), and the
area-constraint hook is off (`PilotHooks.cs:235`) — until the airframe is destroyed or recovered.
This contradicts design §4.6, "Player-tasked Air Command missions are untouched". With the
player-side AI commander, the local faction's fighters are exactly these. Fix: clear both fields in
`ReleaseToPlayer` (and per HIGH-1 at the mission-mutation door, which covers it anyway).

---

### MEDIUM-3 — Re-engage is judged against a ring the fighters have left

`hostiles` are counted within 8 km of `sortie.Center` (`Posture.cs:220`, ring constant
`CommanderOperationsOffensive.cs:26`), from tracking positions up to 45 s stale
(`ThreatMemorySeconds`, `Ai/CommanderEnemyCommanderDefence.cs:66`; `RadarWatch.cs:678`). The
fallback point is 15 km from the centre. If the hostile fighters pursue the falling-back fighters
(the natural response in a dogfight game, and the user's own complaint was fighters flying straight
at 5x), the pursuit leaves the ring; once their tracks go stale, `hostiles` → 0 and
`ShouldReengage(2, 0, 1)` fires — the log says `reinforced 2 v 0; re-engages` (nobody reinforced
anything) and the two fighters turn back into the five chasing them. The present-radius fix
(`CountFightersPresent`) fixed the mirror image of this for OUR side but not for theirs. Mitigations:
the pursuers must stay unobserved for 45 s, and our fighters do fight in self-defence within 6 km,
so the fight often keeps the tracks fresh. Design §4.1 pins the ring to `sortie.Center`, so this is
a design gap as much as a code one — counting hostiles near the fighters (e.g. within the ring of
the centre OR within `PresentRadiusMeters` of any present fighter) would close it.

### MEDIUM-4 — The lift launch gate ignores a falling-back cover

`LiftCoverIsUp` (`CommanderOperationsFob.cs:2008-2043`) counts `CountLiftEscortsUp` — falling-back
fighters are alive, so the gate reads "2 of 2 up" and a waiting load LAUNCHES under a cover that is
retreating from the fight; the hope rule (`CommanderOperationsLogistics.cs:266`) then recalls it on
the first in-air watch. Net effect: a launch-and-recall cycle per fallback. Design §3/§4.5 wired
only the in-flight hope rule, not the launch gate. Fix: treat `cover.FallingBack` as "not up" in
`LiftCoverIsUp` (or add it to `LiftMayLaunch`).

### MEDIUM-5 — Our count excludes fixed-wing CAS; the design included them (and their equivalents count against us)

Design §4.1: "`ours` = alive fixed-wing fighters in `sortie.Caps` **(and `Cas` that are fighters)**".
The implementation counts `Caps` only (`CountFightersUp` `Posture.cs:137-144`,
`CountFightersPresent` `:180-190`), while the hostile side counts ANY fixed-wing with a non-cargo
station (`IsFixedWingCombatAircraft`, `Posture.cs:93-123` via `RadarWatch.cs:685`) — their attack
jets and bombers included. Meanwhile our strike element (fixed-wing, non-cargo stations) IS stamped
to turn with the escort (`ApplyPostureToAll` loops `Cas`, `Posture.cs:316-319`) but never counts
toward ours. So their A-10-equivalent counts against us, ours does not count for us: a systematic
bias toward falling back and a CapsWanted raised by more than the fight needs. Plan Task 7 narrowed
the count to `Caps` — the plan and the design conflict and the executor followed the plan silently;
the design is the user-approved document, so this needs either the code widened (count
`Cas`-that-are-fixed-wing-combat on our side, as the design says) or a one-line design amendment.

---

### LOW-6 — `TryNearestOwnBase` may return a disabled airbase

`Ai/CommanderEnemyCommanderAirTasking.cs:210-232` skips null/center-null but not
`candidate.disabled`; the relaunch door defends against exactly that
(`CommanderAirCommandRecovery.cs:166-168, 181`). If `GetAirbases()` can enumerate a wrecked field,
the fallback steers 15 km toward it. (Other consumers — `NearestHeldBaseLabel`
(`CommanderFobBuilder.cs:703-715`) — also skip the check, so this is low-confidence; one
`disabled` skip makes it moot either way.)

### LOW-7 — `HoldSortie` drops the call for help for up to one review, and makes a falling-back sortie strippable in that window

`HoldSortie` sets `InContact = false`, `CapsWanted = Caps.Count` (`CommanderOperationsAirAwacs.cs:787-789`)
on a falling-back sortie whose demand just closed. The posture tick re-asserts within 5 s
(`Posture.cs:247`), but a review running in between sees it as quiet: `RetaskSourceRank(inContact:
false)` makes it a retask SOURCE (`CommanderOperationsAirWing.cs:953-958`, used at
`CommanderOperationsAirRetask.cs:94`), so its fighters can be stripped from a sortie still in a
fight. Narrow window, self-healing; the invariant "a held sortie asks for nothing" is now
posture-dependent — worth a comment or a `FallingBack` guard in `HoldSortie`.

### LOW-8 — Ghost fallback / never-give-up with one distant fighter

The give-up gate reads `CountFightersUp` (bound alive anywhere, `Posture.cs:205`) while the
decision reads present-only (`:219`): one survivor RTB 80 km out keeps the sortie from standing
down, while `ours = 0` keeps demanding fighters every tick. Self-corrects when bought help arrives
in-ring (they become present, re-engage fires or the fight resumes), but it can burn several minutes
of fighter demand for a sortie with nobody on station. Consider gating the give-up on
`CountFightersPresent == 0` as well.

### LOW-9 — Doc defect from the mid-review fix: double `<summary>`, undocumented method

`Posture.cs:148-161`: `PresentRadiusMeters` carries TWO stacked `<summary>` blocks — the second is
its own, the first is `WatchAirPosture`'s old text (that method, `:195`, now has no doc). Compiles
clean (duplicate summary tags raise no compiler warning — verified 0/0), but the file's otherwise
strict doc convention is broken and the misplaced text will mislead the next reader.

### PROCESS-10 — Plant-a-defect proof and in-game acceptance not run

The execution record states the game was closed before the reload, so testing rule 5 (prove the
self-check gate FAILS on a planted defect) is unmet for `CheckAirPosture`, and none of the design
§5.2-5.5 log lines were observed in the running game. The code-reading argument in the task result
is reasonable; the next launch should explicitly watch for `outnumbered … falls back`,
`reinforced … re-engages`, `not reinforced … stands down`, and the lift recall, plus one
player-ordered fighter over a falling-back sortie (HIGH-2 reproduces in seconds).

---

## Verified fine (evidence)

- **Reconcile survives the review** (`Awacs.cs:646-657`): all three posture fields copied by value
  (`GlobalPosition` is a struct — no aliasing); `InContact = true` and
  `CapsWanted = max(wanted, live)` re-asserted only while falling back, so the raise never resets
  and never over-raises a stable sortie. Bindings move once (`:626-627`), no double-bind.
  Matching is by Mission/ContactPlatoon/label (`SameSortie`, `:742-777`), so a moving contact centre
  does not shed the posture.
- **Give-up order correct** (`Posture.cs:351-374`): line → `EndFallback` (stamps cleared while
  still bound) → loss-cooldown formula (the one every non-AWACS loss uses,
  `AirWing.cs:1273-1277`) → strike forget → release. `ForgetStrikeSortie`
  (`CommanderOperationsAirPackages.cs:519-525`) does not touch `state.AirSorties`, so the forward
  loop index in `WatchAirPosture` is safe; `ReleaseBoundAirframes` does not mutate the list.
- **No immediate re-open after give-up**: cooldown gates the demand walk
  (`CommanderOperationsAirDemand.cs:165`), the retask target (`AirRetask.cs:29`), the fill
  (`AirRadarWatch.cs:951, 1011, 1129`), and the release-retask target (`Awacs.cs:881`). The lift
  cover is re-posted by label every review (`CommanderOperationsAirPlatoonCap.cs:286-338`) so the
  cooldown carries through the reconcile.
- **No reinforcement loop**: falling-back sorties cannot strip each other — `RetaskSourceRank`
  returns -1 (never a source) for in-contact sorties (`AirWing.cs:955-957`), the posture keeps
  `InContact` true every tick, and lift covers/strikes/ARAD-gone-in are excluded as sources
  (`AirRetask.cs:74-91`). A fighter retasked ONTO a falling-back sortie is stamped for the
  fallback point, not the objective (`TaskOntoSortie`, `AirRetask.cs:184-199`).
- **Premature re-engage on inbound fighters fixed** by the concurrent `CountFightersPresent`
  change, with named boundary self-checks (`Posture.cs:435-437`).
- **Counting hygiene**: own side skips null/disabled/rotary/all-cargo airframes
  (`Posture.cs:93-123`); hostile ring skips own faction, disabled, stale tracks (>45 s),
  rotary and cargo-only (`RadarWatch.cs:670-697`); AWACS kind never judged (`Posture.cs:200`);
  ring = `ObservedRadiusMeters` 8 000 (`Offensive.cs:26`).
- **Hooks behave**: override returned without advancing the route (`PilotHooks.cs:184-187`);
  RTB wins over the override (`:173`); self-defence radius boundary exact
  (`TargetOutsideSelfDefence`, `:273-276`; self-checks 5 999 in / 6 001 out); in-radius targets
  exempt from the mission-area restriction (`:343-347`); `ConstrainMissionDestination` early-outs
  while overridden (`:235`); `Returning`/landing airframes take no targets and no hold (`:50, 173`).
- **Package go-in** guarded by `!FallingBack` at the decision (`Packages.cs:355-361`); `GoneIn` is
  not forced while falling back (the `:310/:322` sets are the no-form-up and no-base cases, which
  cannot co-occur with a fallback because `BeginFallback` needs a base).
- **Server-only and cost**: the whole watch sits under `hq.IsServer` (`Logistics.cs:70-80`); the
  tick is one tracking-database walk per sortie per 5 s — negligible.
- **Settings**: five keys with the file's comment style and warm-up touches
  (`CommanderSettings.cs:643-675, 964-970`); config-sanity self-checks include
  give-up > watch interval and margin ≥ 1.
- **Self-check coverage** (`Posture.cs:378-470`): every pure rule has named `Expect`s at the
  boundaries (margin 0/negative off, give-up exactly 300 s / 299 s / never-started / disabled,
  fallback point 15-and-25 km / clamped to a near base / base-on-centre / altitude kept,
  reinforcement 5+1, present-radius boundary, self-defence 5 999/6 001). A planted
  `>`-instead-of-`>=` in `ShouldFallBack` would fail "two fighters against four fall back at
  margin 2". Registration beside `CheckLogistics` confirmed (`CommanderOperationsService.cs:1928-1929`).
- **First tick / zero sorties / plugin load**: empty-list loops, null-safe `Instance?`, pure
  self-checks only — nothing throws. Verified by inspection and by the clean build.
- **Recovery/relaunch (the prompt's "AirMission reuse" concern) is clean**: `ReturnToInventory`
  → `HandleAircraftReturned` → `RemoveMission` (`PilotHooks.cs:67-79`); a relaunched airframe gets
  a fresh mission record. Destroyed airframes lose their record promptly
  (`NotifyUnitDisabled`, `PilotHooks.cs:25-31`). The stale-stamp leak is only on the
  still-flying reuse paths (HIGH-1/2).

## Recommendations, in order

1. Clear `HoldOverride`/`SelfDefenceRadiusMeters` wherever an existing mission record is re-targeted
   — `CommanderAirCommandOrders.cs:119-123` (retask-existing), `:160-163` (adopt),
   `:197-201` (player order) — and in `ReleaseToPlayer` (`CommanderOperationsAirRadarWatch.cs:290`).
   The 5 s re-stamp makes this safe. Closes HIGH-1 and HIGH-2.
2. Add `cover.FallingBack` to the lift launch gate (MEDIUM-4).
3. Decide the `Cas`-that-are-fighters question against design §4.1 — widen the count or amend the
   design (MEDIUM-5).
4. Consider counting hostiles near the fighters, not only near the centre, for the re-engage test
   (MEDIUM-3) — design change, so user first.
5. Skip `candidate.disabled` in `TryNearestOwnBase` (LOW-6); fix the double `<summary>` (LOW-9);
   add a `FallingBack` guard or comment in `HoldSortie` (LOW-7); gate the give-up on present
   fighters too (LOW-8).
6. On the next launch, watch for the three posture lines, a lift recall after a cover fallback,
   and one player-ordered fighter over a falling-back sortie (PROCESS-10, and the HIGH-2 repro).