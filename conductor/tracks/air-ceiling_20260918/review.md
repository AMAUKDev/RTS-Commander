# Independent review — `air-ceiling_20260918`

Reviewer: independent second pair of eyes, 2026-09-18. Read-only: no source file was modified, no
build was run. Judged against `.claude/CLAUDE.md` (Reuse rules, Testing rules),
`conductor/tracks/air-ceiling_20260918/design.md` and DECISION-061.

## Verdict

**FIX-BEFORE-MERGE.**

The arithmetic is right, the marker is on exactly the right sortie, and the ceiling was correctly
retuned rather than rebuilt. Two defects in the *surrounding* control flow defeat the thing the
reserved line exists to protect: with a standing patrol open at the reserved line, a picket
insertion's escort can never win the CAP turn (Finding 1), and at a full sky a lift cover is sized
to zero escorts and the transport then launches immediately and unescorted (Finding 2). Both are
reachable with the shipped defaults, and both directly contradict design §4 decision B ("still
requires an escort into contested air, which is existing behaviour and must not regress") and
acceptance criterion 3.

Neither is a fault in the new predicate. Both are consequences of where the gate was placed and of
how far the ceiling was lowered, and both need a decision before this merges.

---

## Findings

### 1. MAJOR — a refused standing patrol blocks every picket-insertion escort behind it

**Where:** the gate at `Ai/CommanderEnemyCommanderAirBuy.cs:277-288`, against the demand pick at
`Operations/CommanderOperationsAirRadarWatch.cs:822-830` and the demand build order at
`Operations/CommanderOperationsAirPlatoonCap.cs:41`, `:60`, `:62` → `:225`.

**What is wrong.** `TryGetAirDemand` picks the CAP side's answer as the **first** sortie in
`state.AirSorties` with an unfilled non-CAS slot:

```
if (slot != CommanderAirSlot.Cas)
{
    capSortie ??= sortie;
}
```

The list is in demand order. `AddPlatoonCapDemand` posts the standing patrols first (`AddCapDemand`
at `:41` for platoons and `:60` for forward bases and pickets) and only then calls
`AddTransportEscortDemand`, which posts the picket insertion escorts through `AddEscortDemand` at
`:225`. So an insertion escort always sits **behind** the patrols in the list.

The new gate does not skip the patrol and move to the next CAP demand — it refuses the whole CAP
turn and returns `0f`. The CAP side therefore never reaches the escort.

**Failure scenario.** Ceiling 16, reserve 6, live 10 (the design's own stated steady state, with 27
patrol requests open). A picket insertion launches and posts `escort to HILLTOP 19`, wanting two
fighters. Every review: the CAP turn resolves to the first unfilled standing patrol →
`IsStandingPatrol == true` and `10 < 10` is false → `ReportAirDenial`, return `0f`. The insertion
escort is never bought, for the rest of the match. The CAS side is unaffected, so the review does
not stall; the escort simply never gets a turn.

FOB lift covers escape this only by luck of ordering: `AddLiftCoverDemand` runs at
`Operations/CommanderOperationsAirIdle.cs:388`, before `AddPlatoonCapDemand` at `:393`. Picket
insertions do not.

**Suggested fix (either, not both):**
- Move `AddTransportEscortDemand(hq, state, demand)` out of `AddPlatoonCapDemand` and call it from
  `CommanderOperationsAirIdle.cs` beside `AddLiftCoverDemand`, i.e. before the patrols. This is the
  one-line fix and it also makes the "escorts before patrols" ordering explicit rather than
  incidental; or
- Better, and the fix that survives future reordering: teach `TryGetAirDemand` the reserved line.
  Pass a `patrolsAllowed` flag (the buy already knows it) and have the `capSortie ??= sortie` walk
  pass over a sortie whose `IsStandingPatrol` is true when patrols are closed. The CAP turn then
  serves the next real escort instead of being spent on a refusal, and the gate at `:277` becomes a
  belt-and-braces check rather than the whole mechanism.

### 2. MAJOR — at a full sky a lift cover asks for zero escorts, and a zero-escort lift launches at once

**Where:** `Operations/CommanderOperationsAirPlatoonCap.cs:288-289` and `:316`,
`Operations/CommanderOperationsAirPackages.cs:92-99`,
`Operations/CommanderOperationsAirPlatoonCap.cs:431-445`,
`Operations/CommanderOperationsFob.cs:2304-2306`.

**What is wrong.** The lift cover's escort is clamped by the room left under the ceiling:

```
int headroom = Mathf.Max(0, EffectiveAirborneCeiling(hq) - CountAirborne(hq));
...
int escorts = StrikeEscortWanted(CommanderSettings.LiftEscortMinimum, hostileAir, headroom);
```

and `StrikeEscortWanted` ends `return Mathf.Min(wanted, Mathf.Max(0, headroom));` — so at headroom 0
the cover's `CapsWanted` is **0**. The launch gate then reads

```
int wanted = cover?.CapsWanted ?? CommanderSettings.LiftEscortMinimum;
```

and `LiftMayLaunch(up: 0, ahead: 0, wanted: 0, ...)` returns `0 >= 0 && 0 >= 0 && !aradPending` →
**true**. The transport leaves the deck immediately with no escort and no hold.

**Failure scenario.** Ceiling 16 (the new floor), 16 combat aircraft live — an ordinary mid-match
state now that the ceiling is 16 rather than 30-to-60. A forward base is ordered by air. The cover
is posted with `CapsWanted = 0`, the log line reads `escort 0 of 0 up`, and the transport flies the
route alone. This is the exact loss mode the escort rule was written for (the 2026-09-14 match:
15 of 74 picket flights and 7 of 15 construction flights lost to fighters).

This clamp is pre-existing, and I am not calling it a defect the author introduced. What this track
does is turn it from a rare edge into the normal case: the ceiling fell from a floor of 30 and a
maximum of 60 to 16 and 24, so headroom reaches 0 far more often. Acceptance criterion 3 ("a
transport lift still launches with the sky at the ceiling, and still holds for its escort") is
half-satisfied in the worst way — it launches, and it does not hold.

**Suggested fix:** the headroom clamp should bound the escort's *growth*, not its floor. Either
`Mathf.Max(CommanderSettings.LiftEscortMinimum, StrikeEscortWanted(...))` at
`CommanderOperationsAirPlatoonCap.cs:316`, or make `LiftMayLaunch` treat `escortsWanted <= 0` as
"not ready" so the bounded wait at least runs before the load goes naked. The second is the smaller
change and keeps one clock; the first is the one that matches decision B's promise.

The same arithmetic applies to the deliberate strike package's escort at
`Operations/CommanderOperationsAirIdle.cs:433` and `Operations/CommanderOperationsOffensive.cs:1338`
— a package sized at a full sky gets `EscortWanted = 0`. There the consequence is a package that
flies unescorted rather than a transport that does, which is less severe but the same root.

### 3. MINOR-to-MAJOR — the reserve is a buy-side rule only, so a refused patrol takes its fighters by retask instead

**Where:** `Operations/CommanderOperationsAirRetask.cs:26-42` and `:68-88`, against
`Operations/CommanderOperationsAirPlatoonCap.cs:512` and the field doc at
`Operations/CommanderOperationsAirWing.cs:188-199`.

**What is wrong.** `AddCapDemand` sets `InContact = true` by construction ("it only opened because
hostile aircraft are overhead"), which makes every standing patrol a valid **target** for
`RetaskToContact`. The source exclusions cover lift covers (`IsLiftCoverLabel`), strike packages,
the radar aeroplane and an ARAD sortie that has gone in — but **not** picket insertion escorts,
whose label is `escort to <point>` and not the `LIFT ESCORT ` prefix `IsLiftCoverLabel` tests
(`Operations/CommanderOperationsAirPlatoonCap.cs:254`), and not pre-emptive cover.

So a patrol that the new gate refuses to fund does not go without: on the next operations tick it
pulls a fighter off an insertion escort or a pre-emptive sortie instead. Refusing the buy raises
the retask pressure rather than removing the demand.

The field's own summary claims the opposite: "These, and only these, are rationed ... so a busy sky
never costs a lift its escort". That guarantee is not delivered by the code as written.

**Suggested fix:** exclude a sortie that is serving an escort from the retask source walk (the same
way a lift cover is excluded), or refuse a standing-patrol *target* while patrols are past the
reserved line. At minimum, correct the claim in the field's summary so the next reader is not
misled.

### 4. MINOR — "one definition" is claimed but not delivered for the absolute ceiling

**Where:** `Operations/CommanderOperationsUnitEconomy.cs:243-259`, against
`Ai/CommanderEnemyCommanderAirBuy.cs:176`, `:788`, `:882` and
`Ai/CommanderEnemyCommanderLadder.cs:480`.

The live wrapper's summary says it is "One definition, read by the AI commander's air buy ... — the
one path by which a COMMANDER adds an aircraft to the world". Four sites still write the raw
comparison `CountAirborne(hq) >= CommanderOperationsService.EffectiveAirborneCeiling(hq)` and add
aircraft behind it: the element order loop, the cheap-padding loop, and the ladder's home-CAP rung.
None was retrofitted, which is Reuse rule 5's "generalise the second instance" left undone in the
one track that created the general form.

There is also a behavioural divergence between the two spellings, currently unreachable: with
`ceiling <= 0` the predicate says "never binds" while the raw comparison says "blocked"
(`0 >= 0`). It cannot be hit today because `AirborneCeilingFor` floors at
`Mathf.Max(1, configured)` (`Operations/CommanderOperationsAirRadarWatch.cs:1091`), which is worth
knowing: the "an air ceiling of zero never binds" self-check defends a branch the live wrapper can
never reach, so air has no off switch even though the check implies one.

**Suggested fix:** replace the four raw sites with `AirBuyAllowed(hq, standingPatrol: false)`
(behaviour-neutral given the floor of 1), or soften the summary to say which paths it actually
covers.

### 5. MINOR — the diversity cap's total and `CountAirborne` now disagree, against an explicit comment

**Where:** `Ai/CommanderEnemyCommanderAirBuy.cs:978-1015`.

`TallyAirborneByType`'s summary says it is "`CountAirborne`'s own walk with a tally hung off it, so
the share and the total it is a share OF are counted in one pass and can never be counted
differently (Reuse rule 4)". After this track that is false: `CountAirborne` skips transports and
this walk does not.

Effect is small but real — the denominator the 60% type-share cap divides by
(`CommanderSettings.TypeShareCap`) is inflated by the faction's transports, so the cap bites
slightly less than it did, and it can still refuse a transport type for over-representation while
transports are exempt from the ceiling.

**Suggested fix:** apply the same `IsTransportAircraft` skip in the tally, or delete the claim that
the two walks can never differ. The first keeps the pair the comment promises.

### 6. MINOR — a third headroom consumer the track never named

`Operations/CommanderOperationsOffensive.cs:1338` sizes the deliberate strike package's escort from
the same `EffectiveAirborneCeiling - CountAirborne` pair. Design §3 and DECISION-061 both list only
`CommanderOperationsAirIdle.cs:433` and `CommanderOperationsAirPlatoonCap.cs:288`. The direction is
safe here (a smaller count means a larger headroom, so the escort is clipped less), but "we found
every caller" is a claim the change record does not actually support. Worth adding to the decision
log's Impact line.

### 7. NIT — one self-check case cannot fail independently

`Operations/CommanderOperationsUnitEconomy.cs:675`:

```
Expect(failures, "a loss at the air ceiling opens exactly one replacement", AirBuyAllowed(15, 16, 6, false), true);
```

is argument-for-argument and expectation-for-expectation identical to line 673, "a commander under
the air ceiling may buy a protected airframe". No defect can fail one without failing the other, so
as a named check it is decorative. The ground block above it does the same thing three times (662,
663, 666), so this is house precedent rather than a new sin — but the track's own defect-plant
discipline is what found and removed one piece of dead code already, and this is the same category.

### 8. NIT — boundaries not covered

- No "a commander **over** the air ceiling may not buy a protected airframe" case, though the ground
  block has one (`GroundBuyAllowed(120, 80)`). Over the ceiling is newly reachable, since the
  player's own launches still count while transports no longer do.
- Nothing pins the shipped default trio into a coherent order. `AirPatrolReserve` (6) versus
  `AirborneCeiling` (16) versus `AirborneCeilingMax` (24) is exactly the "constant that can be
  retuned into nonsense" Testing rule 1 asks for a check on: a reserve at or above the floor
  silently grounds every patrol, and the existing `AirborneCeilingFor` cases
  (`Operations/CommanderOperationsAirWing.cs:2046-2050`) still assert against the old literals 20
  and 60, so they would not notice. A single case asserting
  `AirPatrolReserve < AirborneCeiling <= AirborneCeilingMax` on the shipped defaults would close it.

### 9. NIT — the refusal line is de-duplicated less than requirement 7 implies

The de-dup key in `ReportAirDenial` (`Ai/CommanderEnemyCommanderAirTasking.cs:23-49`) is the reason
string, and the new reason embeds the live ceiling (`{EffectiveAirborneCeiling(hq)}-aircraft
ceiling`). Income moves that number, so the counter resets whenever it changes. The key also
alternates with the other refusal reasons within one review, which resets it again. Expect roughly
two lines a review rather than one per `HoldReportEveryReviews`. Acceptable given the design already
books this noise as a known cost — recorded so the developer is not surprised by the volume.

### 10. MINOR — home defence above the baseline is starved behind a refused patrol

Decision G is correct as stated: `homeCapDemand` returns at `Ai/CommanderEnemyCommanderAirBuy.cs:253`,
above the gate, and rung 1's strict baseline buys directly in
`Ai/CommanderEnemyCommanderLadder.cs:480` against the absolute ceiling only. Verified.

What the decision does not say is that `homeCap` is only ever set when **no** CAP sortie is open
(`Operations/CommanderOperationsAirRadarWatch.cs:892`, the `else if (kind == Cap)` branch). With any
standing patrol open, `capSortie != null`, so the home patrol's growth above the baseline never
reaches the buy — and now the patrol that took its place is refused as well, so the CAP turn buys
nothing at all. The precedence is pre-existing; the wasted turn is new. Fixing Finding 1 by the
second route (skipping closed patrols in the demand pick) fixes this too, which is a reason to
prefer it.

### UNVERIFIED

- **Which airframes count as transports.** `GetAirRole` (`Ai/CommanderEnemyCommanderAir.cs:175`)
  calls something a transport when `captureCapacity > 0` **and** it has no plane pilot. Whether
  every transport the mod actually flies — the supply helicopter, the picket insertion helicopter,
  the forward-base lift — satisfies both halves cannot be checked without the game's asset data. A
  supply helicopter with `captureCapacity == 0` would be classed Strike or Fighter and would still
  eat the combat ceiling, which would make decision B partly ineffective. Worth one look at the
  `Air roster` log line in the running game.
- **Everything in the running game.** The author states plainly that the developer's game was closed
  throughout and that nothing was observed. Acceptance criteria 2 through 5 are all unproven, which
  is correctly declared rather than hidden — but under Testing rule 2 this track is not done.

---

## Checked and found correct

- **The marker is on exactly the right sortie.** `IsStandingPatrol = true` appears at one site only
  (`Operations/CommanderOperationsAirPlatoonCap.cs:505`, inside `AddCapDemand`). `AddEscortDemand`
  (`:461`) and `AddLiftCoverDemand` (`:286`) leave it false. `AddCapDemand`'s two callers (`:41`
  platoons, `:60` forward bases and pickets) are both genuine standing patrols, and both are skipped
  when anything else already has a sortie over that ground (`AlreadyServed`, `CoveredByLift`).
- **The marker cannot be lost or wrongly gained.** `ReconcileSorties`
  (`Operations/CommanderOperationsAirAwacs.cs:604-694`) copies fields from the *live* sortie onto
  the *fresh* demand object, and the fresh object always carries the flag its own construction gave
  it, so the marker follows this review's demand. The strike package is re-posted as the same object
  (`ReferenceEquals` short-circuit, `:631`), so it keeps false. Held sorties keep their own value and
  ask for nothing. `RetaskToContact` moves aircraft between sorties and never copies or promotes a
  sortie. No other site constructs, clones or mutates a `CommanderAirSortie`'s kind.
- **No save-file gap.** `CommanderAirSortie` is not part of `Core/CommanderStrategicSaveModels.cs`
  or `Operations/CommanderOperationsStrategicPersist.cs`, so the new field needs no serialisation.
- **Home defence bypasses the reserve.** Confirmed at `Ai/CommanderEnemyCommanderAirBuy.cs:253` (the
  `homeCapDemand` early return sits above the gate at `:277`) and
  `Ai/CommanderEnemyCommanderLadder.cs:480` (rung 1 buys against the absolute ceiling only). No
  other demand kind bypasses the gate: it keys on `demandSortie?.IsStandingPatrol`, and AWACS, ARAD,
  CAS and every escort-flavoured CAP sortie carry false. The one demand-less buy that can still
  reach a launch is the emergency threat fighter (`ChooseAirRole`,
  `Ai/CommanderEnemyCommanderAirBuy.cs:49`), which fires only when the faction has zero fighters and
  the opponent is in the sky — correct, and still under the absolute ceiling.
- **No livelock, no starvation of the CAS side.** The gate sits after `advanceTurn: true`, so a
  refused patrol hands the alternation to the other half of the wing, and the review loop
  (`Ai/CommanderEnemyCommanderService.cs:874-890`) stops after two consecutive failures. A refused
  patrol therefore costs at most one wasted iteration per review.
- **No tier regression from the smaller ceiling.** The air fund's ceiling
  (`Ai/CommanderEnemyCommanderAir.cs:82-93`) is `Max(oneAirframe, buyable * cheapestWanted)` where
  `oneAirframe` is the dearest airframe an open demand wants. A smaller ceiling shrinks `ceilingRoom`
  and therefore `buyable`, but the floor is untouched, so the wing can always still save for the
  dearest single airframe it has asked for. No airframe becomes unaffordable.
- **No collision with the game's own aircraft limits.** `hq.AIAircraftLimit` is pinned to zero
  (`Ai/CommanderEnemyCommanderService.cs:1115`), and no rule in the mod reads an aircraft count for
  airbase parking, runway capacity or spawn limits. Excluding transports cannot overrun a game limit.
- **The memo is sound.** `GetAirRole` reads only `definition.captureCapacity`, `HasPlanePilot` and
  `roleIdentity`, all fixed per shared asset, so `transportDefinitions` cannot go stale and needs no
  sweep. It is static and dies with the assembly on a hot reload, which is correct.
- **The gate is cheap.** `demandSortie?.IsStandingPatrol == true &&` short-circuits, so a non-patrol
  buy pays no extra walk of `hq.factionUnits`. A patrol refusal costs two walks and two income
  reads, which on a frame-rate track is worth knowing but is bounded at roughly one per review.
- **Nothing server-only was added.** The gate only refuses; it spawns, funds and despawns nothing,
  so it needs no `hq.IsServer` guard.
- **Config key renames are clean.** No `.cs`, `.json` or `.cfg` file references the old keys
  `AirborneFloor` or `AirborneCeilingMax` as *config keys* — the only hits are prose in `CHANGELOG.md`
  and `conductor/`. The C# property names are unchanged, so the warm-up list at
  `Core/CommanderSettings.cs:1107-1114` needed no edit and correctly gained `_ = AirPatrolReserve`.
  Orphan entries left in an existing user config are expected and harmless.
- **The self-check cases that matter are not vacuous.** The reserve clamp case
  (`AirBuyAllowed(16, 16, -5, true)` → false) genuinely fails if `Mathf.Max(0, patrolReserve)` is
  removed: the line would become 21 and a patrol would buy past the ceiling. The inclusive-at-the-
  ceiling, reserved-line, escort-versus-patrol and reserve-above-ceiling cases each pin a distinct
  branch. Decision I's account of the dead clamp is correct — a clamp on the line could not change
  an answer for any non-negative count, and removing it rather than writing a check around it was
  the right call.
- **The UI box height is right.** Eleven rows at 38 px from `y + 42`, plus a 34 px footnote, is 494,
  which matches `OperationsBoxHeight` (`UI/CommanderOverlayUiSettings.cs:595`); the scroll view's
  content measurement at `:459` reads the same constant, so the two cannot drift.
- **`AirBuyAllowed` is genuinely parallel to `GroundBuyAllowed`**: same inclusive-at-the-ceiling
  convention, same test against the live count so a loss opens exactly one replacement, same
  non-positive-is-off convention, and the only addition is the second line. Not gratuitously
  different.
- **The numbers are coherent.** Ceiling 16, reserve 6, patrols stop at 10; home CAP baseline 2 and
  maximum 4 (`Core/CommanderSettings.cs:892`, `:901`), lift escort minimum 2 (`:731`), transport
  escort minimum 2. There is no state in which the wing can buy nothing at all: the reserve only
  ever closes the patrol line, never the protected one.
