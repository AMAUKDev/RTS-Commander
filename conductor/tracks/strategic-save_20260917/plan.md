# Strategic save and load — plan

Track: `strategic-save_20260917`. Spec: `spec.md`. Study:
`conductor/designs/2026-09-17-save-load-study.md`, which was REWRITTEN part way through this track;
the tasks below follow the rewritten version and the phase-5 block records what changed.

Status legend: `[x]` done. Where a planned check could not be written, the reason is stated under
the task — the mod has no unit-test project, so a rule that needs a live `Unit`, `FactionHQ` or
`Airbase` cannot be driven at plugin load and is verified in the running game instead.

## Phase 1 — the gate (nothing else can be tested first)

- [x] **T1 — A save that does not expire.** `Core/CommanderStrategicSaveModels.cs`. Checks: the gate
  accepted and refused in every corner, `a strategic save never expires`, and the pair that pins WHY
  it exists — `the hot-reload guard would have refused a save taken hours ago`.
- [x] **T2 — The store.** `Core/CommanderStrategicSaveStore.cs`: its own file, `RequestSave`, the
  one-shot restore on a fresh run, rename-on-consume, and the hold-off with its cap. Registered
  after `CommanderStateStore`; `SelfCheck` registered in `Core/CommanderPlugin.cs`.
- [x] **T3 — The parallel persistence interface.** `ICommanderPersistStrategic` plus the two registry
  fan-outs. `RestoreStrategic` is load-only, so the store can apply in an order registration order
  does not give.

## Phase 2 — what goes in the file

- [x] **T4 — No `PersistentID` and no replayed clock.** Both refused, the second structurally over
  the real record types.
- [x] **T5 — Points, owners and roads.** Reuses `ToRecord`/`FromRecord` with the mine id suppressed;
  the restore body is shared with the hot-reload path by one `attachMines` parameter.
- [x] **T6 — One valuation, with the study's three special cases.** `StrategicUnitValue(Unit)` over
  `UnitDefinition.value`. Catalogue structures carry a multiplier; mines, factories and docks were
  bought at flat prices with upgrades as sunk cost (`StrategicFlatSunkCost` walks the mod's own
  ladders); a building the commander did not build is worth nothing. The live-`Unit` arm is not
  checkable at load; every pure rule underneath it is.
- [x] **T7 — Treasuries**, by faction name, applied with `hq.SetFunds` under `hq.IsServer`.
- [x] **T8 — Forward bases, online only.** A delivering order is CANCELLED and not refunded
  (`StrategicDeliveringRefund`). Narrowing of the study: it is not cancelled at save time, because
  pressing SAVE must not change a running match — it is absent from the file and named in the log.
- [x] **T9 — The `Prepared` flag**, applied where the per-faction state is created, which is the only
  point early enough. No new check: the rule it feeds is already pinned by `CheckDifficulty`.

## Phase 3 — the load

- [x] **T10 — Ordered apply**: war chest, out-of-treasury pots, airbases, economy buildings, forward
  bases, garrisons. Each step depends on the one before it.
- [x] **T11 — Forward bases rebuilt in place**, records synthesised, name counter seeded past the
  world (`SeedFobNameCounterFromWorld`, `TrailingNumber`), and the two abandonment clocks stamped now
  rather than left at their default — the study's named rule, checked through
  `NewRestoredForwardBaseOrder`.
- [x] **T12 — Garrisons placed and paid**, whole or nothing, in the commander's own priority order,
  with points released to neutral and named in the log when the money runs short.
- [x] **T13 — No point is judged before its garrison is down**, bounded so a failed rebuild releases
  the hold machine rather than freezing it.

## Phase 4 — trigger, proof and record

- [x] **T14 — Buttons, build, defect proofs, changelog.** The box went on the POINTS tab rather than
  GAMEPLAY, beside the other strategic-picture settings. `dotnet build -c Release` 0 warnings / 0
  errors; installed with `build-and-install.ps1 -Dev`. Twenty-two defects planted one at a time, each
  failing a NAMED check, each file restored byte-identical by sha256. Full list in
  `conductor/decision-log.md` under DECISION-053.

## Phase 5 — rework after the study was rewritten

- [x] **T15 — Ownership is not a stored fact.** Restoring owners without garrisons would blank the
  map five seconds after loading. The garrison step is load-bearing, and the checks now say so.
- [x] **T16 — Airbases and mined sites are exempt.** `StrategicGarrisonHoldsThisKind(kind,
  mineStanding)`. An airbase is handed back with `Capture.ForceCapture`; a mined site follows its
  mine, which the economy rebuild has already put back.
- [x] **T17 — Mines, factories and docks rebuilt in kind and charged nothing**, exactly like forward
  bases (developer decision, revising the buy-back I built first). They are NOT cashed in, which is
  what stops the double-count; their upgrade levels are restored from the mod's own level tables;
  `SpawnMine` itself refuses a site the builder does not hold, which is why this step runs after the
  point owners are restored; and a rebuild that fails credits the sunk cost back rather than losing
  it.
- [x] **T18 — Out-of-treasury pots restored** — naval, air, radar-watch, picket and the structure
  bank — so a load does not reset every saving-up decision a commander had made.
- [x] **T19 — Conservation stated as a rule.** `StrategicValueConserved(fundsBefore, cashedIn,
  garrisonSpend, economySpend, fundsAfter)`, checked in six scenarios including both failure
  directions, and reported as a one-line summary at the end of every load so the developer can check
  the arithmetic in the game.

## Left open

- **Not observed in the running game.** The developer's game was closed throughout. What to run and
  which log lines prove it is written out in DECISION-053.
- **Two steps have no prior art and are new behaviour, not a restore.** Handing an airbase back uses
  `Capture.ForceCapture`, which the mod has never called; and re-garrisoning spawns vehicles straight
  onto hold posts, which no other path does. Both are flagged in DECISION-053.
- **An ordinary catalogue structure the commander built is cashed in and not rebuilt.** Nothing in
  the strategic picture hangs off where a radar or a bunker stood. Adding it would be the same shape
  as the mine rebuild.
