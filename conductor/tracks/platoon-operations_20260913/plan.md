# Plan: platoon-operations

**Track**: `platoon-operations_20260913` · **Design**: `design.md` (approved 2026-09-13; binding —
this plan implements it and records the eight places the code forced a departure from its literal
wording, with reasons) · **Executor**: one agent, tasks in order, `dotnet build
GroundControlRts.csproj -c Release` after every task (0 warnings, 0 errors). Do NOT run
`build-and-install.ps1`, `build-dev.bat` or `build-release.bat` (the game may be running). Do NOT
commit or stage anything; the user commits.

If a fresh shell lacks `NUCLEAR_OPTION_DIR`, set it to `I:\SteamLibrary\steamapps\common\Nuclear Option`
(`BUILD.md:19-39`). New `.cs` files anywhere under the repo compile automatically — the project is
an SDK-style csproj with no explicit file list (`GroundControlRts.csproj`), so a new `Operations/`
folder needs no project edit. Deleting a file is equally automatic; `Ai/CommanderEnemyCommanderGarrison.cs`
is deleted in T11 and nothing has to be un-registered from the csproj.

## Goal

Replace "the commander buys vehicles, the game's convoy brain drives them one at a time down one
road at the nearest enemy" with a front line the AI commanders form, hold and push. Every vehicle
an AI commander owns joins a named six-vehicle **platoon** with one objective. Platoons sit on the
control points nearest the enemy as **forward bases** with a munitions truck, hold the quiet points
behind them with a two-vehicle **picket**, and go forward as a two- or three-axis **offensive**
sized to what the commander can actually see. Everything the AI decides shows up in the COMMANDER
LOG and as markers on the map, and the same doctrine runs for the player's own AI commander.

## Architecture

- One new **service**, `Operations/CommanderOperationsService.cs` (Advanced tier,
  `ICommanderTickPersistent` + `ICommanderResetSession`, registered once in
  `Core/CommanderModeController.cs`), split into partials by concern as the codebase does
  (`conductor/knowledge/patterns.md` "Partial classes per concern"): `…Front.cs` (front line, point
  value, forward-base and picket missions), `…Offensive.cs` (targets, sizing, axes, release points,
  pressure clock), `…Requisitions.cs` (the order book and the depot claim), `…Markers.cs` (drawing).
  Data lives in `Operations/CommanderPlatoon.cs`.
- Two clocks, both on the scaled scheduler (`Core/CommanderScheduler.cs:30`): a **review** every
  30 s that re-plans missions, and a **movement tick** every 5 s that re-issues destinations. The
  same split the enemy commander already runs (30 s buy review at
  `Ai/CommanderEnemyCommanderService.cs:30`, 10 s defence review at
  `Ai/CommanderEnemyCommanderDefence.cs:36`), and for the same reason: re-planning is expensive and
  re-issuing has to be responsive.
- **Every decision rule is a pure static function** over ints and floats — recipe fill, front/rear,
  point value, axis selection, sizing, pressure, order-book summing, withdraw/fail, arrival — so
  `SelfCheck()` can drive them with synthetic data at plugin load. That is the only automated test
  this mod has (`.claude/CLAUDE.md` Testing rules).
- **Movement reuses the game's own pathfinding and the move service's formation maths, and adds no
  follower loop.** A platoon's destination is one point; each member is given that point plus its
  formation-slot offset through `CommanderDestinationFormation.ApplyOffset`
  (`Units/CommanderDestinationFormation.cs:29-55`, already pure and already what every player order
  uses), issued with `CommanderGameAccess.TrySetDestination`
  (`Core/CommanderGameAccess.cs:567-577` — `SetDestination(destination, playerCommand: true)`, the
  same pinning call the home guard relies on, `Ai/CommanderEnemyCommanderDefence.cs:186-187`). The
  seam is one new `internal static CommanderMoveService.IssuePlatoonMove(...)`; see departure 1 for
  why it is not `ApplyOrder`.
- **The garrison step is deleted, not extended.** `Ai/CommanderEnemyCommanderGarrison.cs` (382
  lines, added by the points track) is replaced wholesale by forward-base and picket missions, as
  design.md §1 says ("the garrison step is replaced by FOB/picket missions"). Its self-check case
  in `Points/CommanderStrategicPointService.cs` goes with it.
- **The home guard becomes the reserve platoon.** `Ai/CommanderEnemyCommanderDefence.cs` keeps its
  threat posture (which the buyer and the HUD read) and stops recruiting a guard for any HQ the
  operations service is managing; the base's reserve platoon is the guard now. `IsDefendingUnit`
  keeps its meaning and its two callers, because the guard still runs on any mission where the
  operations service never takes over.
- **KISS, per design**: no persistence across a reload, no player platoon tools, no truck money,
  no air or naval tasking, no new Harmony patch (the one depot-claim hook is a line inside the
  existing `FactionHQ.RegisterFactionUnit` postfix).

## Tech stack

C# (`LangVersion latest`, nullable on), .NET Framework 4.7.2, Unity IMGUI (`GUI.*`), BepInEx 5
config (`CommanderSettings.Get/Set`), Harmony (no new patch class in this track). Terrain queries
go through the existing static seams on the SAM analyzer
(`CommanderSamSiteAnalyzerService.TryGetStrategicTerrainHeight` /
`EstimateStrategicTerrainNormalY`, used the same way discovery uses them at
`Points/CommanderStrategicPointDiscovery.cs:241-246`). Road geometry comes from the already
discovered `Crossroads` and `Roadside` points, not from a second read of the level's road network
(departure 4).

## Read first (Reuse rule 1)

- `Ai/CommanderEnemyCommanderService.cs` whole (807 lines): 30 (`ReviewIntervalSeconds = 30f`),
  **32-39 (`SpendFraction = 0.25f`, `PurchasesPerReview = 3`)**, 54-56 (the duel's 0.45 / 5),
  65 (`states`), 189-260 (`TickPersistent`; **209-215 the defence clock and the garrison call**),
  262-287 (`ResetSession`; **275-280 the garrison scratch clears**), 289-396 (`Review` —
  **338-343 the pot, 353 `blindToAir`, 354 `purchases`, 369-395 the buy loop with the capture and
  recon overrides**), 548-558 (`MatchesPlan`), 560-584 (`Choose` — plan match then any combat
  vehicle), 586-605 (`ChooseCaptureUnit` — cheapest with `captureStrength > 0`), 607-624
  (`CollectCatalog`), 651-666 (`IsAirDefence`, `IsCombatVehicle`), 691-741 (`ReadForce`),
  **760-806 (`CommanderState`; 784 the `Garrison` table this track removes)**.
- `Ai/CommanderEnemyCommanderDefence.cs` whole (456 lines): **12-28 the class remarks (why pinning
  is `SetDestination(post, playerCommand: true)` and why releasing means clearing
  `commandedDestination`)**, 36 (10 s clock), 53 (`DefenceArrivedMeters = 150f`), 60-67
  (`ThreatRadiusMeters = 15000f`, **`ThreatMemorySeconds = 45f`**, `ThreatHoldSeconds = 120f`),
  77-78 (`CommandedDestinationRef`), 85-102 (`IsDefendingUnit`), 121-141 (`ReviewDefences`),
  **143-189 (`ReviewDefence`)**, 191-228 (`IsUnderThreat` — reads `hq.trackingDatabase`, skips
  buildings, 45 s memory), 230-244 (`IsNearOwnBase`), 250-295 (`EnsureDefencePosts`), 297-320
  (`PruneDefenders`), 322-354 (`ReleaseSurplusDefenders` — the release mechanism), **356-392
  (`RecruitDefenders` — the candidate filter to copy)**, 394-397 (`DefencePriority`), 434-455
  (`CheckDefencePosture` — read constants into locals so the compiler cannot fold the check).
- `Ai/CommanderEnemyCommanderGarrison.cs` whole (382 lines) — **this is the file being deleted**;
  read it before deleting so nothing worth keeping is lost. 47-64 (`IsGarrisonUnit`), 73-91
  (`SelectGarrisonTargets`, pure), 115-178 (`ReviewGarrison`), 183-201 (`PruneGarrison`), 205-225
  (`ReleaseGarrisonOutsideTargets`), 246-279 (`RecruitGarrison` — the filter the platoon recruiter
  inherits), 281-323 (`DriveGarrisonToPosts` — the stable-order-by-instance-id trick), 328-353
  (`EnsureGarrisonPosts` — ring posts at 0.6 radius, terrain-snapped, sea-level skipped: **this
  moves into the forward-base hold, Reuse rule 3**), 355-381 (`NearestHeldBaseDistanceMeters`,
  `HorizontalDistanceSquared`).
- `Ai/CommanderEnemyCommanderGround.cs` 20-37 (recon constants), 53-101 (`ReviewRecon` — the
  "already on station, do not re-issue" idiom and the `HasPlayerOrder` skip), **141-158
  (`FindHighGround` — the sampled high-ground pick the forward-base siting reuses)**, 162-177
  (`ChooseReconUnit`).
- `Ai/CommanderCaptureService.cs` 32-74 (constants), 490-504 (`GetTerritoryCenter`), 506-532
  (`ReviewEnemies`), **534-591 (`ReviewEnemy` — the only ground routing the mod does today)**,
  598-601 (`WantsCaptureUnit`), 603-640 (`ChooseEnemyTarget`, `ScoreTarget`), **667-694
  (`CollectCaptureSquad` and its two "spoken for" skips)**, 706-741 (`GetHoldPoint` — why a squad
  is never sent to the airbase centre transform), 802-806 (`GetAirbaseLabel`).
- `Ai/CommanderPlayerCommanderService.cs` 39-47 (`AnyCommanderOn`), 55-68 (`IsCommanded`, both
  overloads), 99-134 (`ChooseOpponent`), **137-152 (`CommanderLabel`)**, 154-205 (`SelfCheck` —
  the `failures` / `Expect` shape).
- `Points/CommanderStrategicPoint.cs` whole (181 lines): 7-36 (`StrategicPointKind` — seven kinds,
  not the three the design was written against), **43-53 (`StrategicPointKinds.IsControlPoint`)**,
  62-155 (the point: `Kind`, `Position`, `Radius`, `Label`, `Airbase`, `Mine`, `Hold`,
  `GarrisonCounts`, `PresentCount`, **143-154 `GetOwner`**), 163-181 (`HoldState`).
- `Points/CommanderStrategicPointService.cs` 19-30 (class, `points`, **`Points`**), 60-81
  (`Instance`, `FocusedPoint`, **`HqAt`**, `GetPresentCount`), 83-100 (`TickPersistent`), 125-148
  (`ResetSession`), 175-241 (`TickHold`), 243-268 (`CountPresent` — the ring-count idiom),
  **479-503 (`IsInsideGarrisonedPointReach`)**, 508-551 (`TryPickFreeReachableSite`), 553-584
  (`PointIncomeRates`, **`FromSettings`**), 586-594 (`PointCounts`), **596-624 (`IncomePerMinute`,
  `SumIncomePerMinute`)**, 645-695 (`GetPointIncomePerMinute`), **697-720 (`SelfCheck` — the
  `failures` list and the per-subsystem `Check*` methods)**, **1030-1058 (`CheckGarrisonTargets` —
  deleted with the garrison in T11)**, 1060-1098 (`ExpectSequence`, the three `Expect` overloads).
- `Points/CommanderStrategicPointDiscovery.cs` 16-21 (**`FlatNormalY = 0.94f`** and why),
  596-616 (the road caches and why they exist), 637-658 (`StepRoads` — how the level's road network
  is reached: `NetworkSceneSingleton<LevelInfo>.i?.roadNetwork`), 660-702 (`StepRoadsCollect`,
  `MergeJunctionNode`), **777-813 (`JunctionDegree`)**, **817-866 (`EmitRoadsidePoints`)**,
  **872-891 (`PointAlong`, `Lerp`)**, **896-912 (`SegmentDistanceSquared` and its note about being a
  deliberate second definition)**, 1018-1032 (the `Done` step: **the road caches are cleared here**,
  which is why the operations service cannot borrow them — departure 4).
- `Units/CommanderMoveService.cs` 34-46 (**36 `GroundFormationSpacingMeters = 25f`**), 89-93
  (`Formation`), 175-191 (`IssueOrderAt`), **343-414 (`ApplyOrder` — note 366-370 the aircraft
  branch, 373-376 `ShouldAllowCommanderMove`, 381-383 the slot counter and spacing, 395-398
  `FormationSlot`/`Spacing`/`Shape`/`Heading`, 406 `MarkPlayerOrdered`)**, **416-448
  (`ResolveHeading`)**, 496-641 (`AdvanceRoutes` — 584-587 how a waypoint becomes a formation
  slot), 651-669 (`UpdateGroupCohesion`), **941-954 (`Issue` — the 40 m re-issue guard)**, 982-987
  (`ArrivalRadius`), **1013-1050 (`HasPlayerOrder` and its remarks: why `playerDestinations` is not
  consulted, and the claim that both tables only ever hold local-HQ units)**, 1292-1306
  (`TryGetOrder`), 1342-1362 (`ClearOrder`, `ResetSession`).
- `Units/CommanderDestinationFormation.cs` whole (70 lines): 7-13 (`CommanderFormationShape`),
  **29-55 (`ApplyOffset` — slot 0 sits on the waypoint; line, column and wedge are built in the
  travel frame)**, 57-69 (`RingOffset`).
- `Terrain/CommanderTerrainFlightPlanner.cs` 7-11 (constants), **13-33 (`TryBuildRoute` — note it
  returns airborne waypoints at `height + clearance`, allocates four arrays per call and logs a
  line per call)**, 110-121 (the slope cost model), 166-169 (the per-call log line). Read this to
  understand why departure 5 does not use it.
- `Terrain/CommanderStrategicHeightMap.cs` 25 (`IsReady`), 125-173 (`TryGetHeight`,
  `TryGetHeightNearest`), 175-187 (`EstimateNormalY`) — reached through the SAM analyzer's static
  seams, never directly.
- `Economy/CommanderBuildPreview.cs` 55-62 (`boundsNetwork`), 243-250 (the taxi-network remark),
  289-318 (`IsInsideBuildRadius`), 450-460 (how `LevelInfo.roadNetwork` is read), 493-520
  (`EnsureRoadBounds`).
- `Supply/CommanderSupplyHeliCatalog.cs` 395-410 (`mount.info.rearmGround` / `rearmShip` — how the
  supply code recognises rearm capability, on a **cargo mount**, not on a vehicle definition).
  `Supply/CommanderSamSiteSupply.cs` 70-80, 150-165 (`TryGetAmmunitionCargoCapacity`). Read both
  before departure 3.
- `Units/CommanderMoveService.cs:1413-1451` (`TryReturnToBasegameLogistics`) — **the only place in
  the mod that detaches a munitions truck from the game's own rearm AI**: `unit.TryGetComponent(out
  RearmVehicleAI)`, `unit.TryGetComponent(out Rearmer)`, `rearmAi.AssignMission(null!)`,
  `rearmer.AvailableForMission`.
- `AirCommand/CommanderAirCommandPatches.cs` **95-100 (`RegisterFactionUnitPostfix`)** and
  `AirCommand/CommanderAirCommandPilotHooks.cs:12-15` (`NotifyFactionUnitRegistered` — the
  one-line static forwarder shape). `Supply/CommanderSupplyHeliPatches.cs:17-22` is the second
  postfix on the same method, proving more than one is allowed.
- `UI/CommanderAiLog.cs` whole (89 lines): 18 (`Capacity`), 45-48 (`FormatMissionTime`), **56-72
  (`Note`)**, 75-78 (`For`), 85-88 (`Clear`).
  `UI/CommanderAiLogUi.cs` 16-33 (window id, refresh, row height), 99-106 (`Draw`), 130-192
  (`DrawWindow`: help, X, tabs), **194-231 (`DrawHeader` — the three 22 px rows the OPERATIONS
  block joins, and its own note at 204-207 about the 520 px line budget)**, 233-247 (`DrawBody`).
- `Units/CommanderWorldMarkerRenderer.cs` 48-62 (`Draw` — **62 is where the point markers are
  drawn**), 316-358 (`DrawCaptureTargets`: the world-vs-map split).
  `Points/CommanderStrategicPointMarkers.cs` whole (230 lines): 12-20 (marker sizes), 22-71
  (`DrawMarkers` — the `fullscreenMap` / `anyMap` split and the `ScreenPosition`/`ScreenHalfSize`/
  `ScreenFrame` record), 73-92 (`DrawPointMarker`), 94-149 (`BuildReadout`), 203-229 (**`DrawDot`
  and its remark: rotated bars do not survive the UI-scale matrix — draw axis-aligned bars only**).
- `UI/CommanderOverlayUiSettings.cs` 45-58 (the six-tab row at `tabWidth = (width - 30f) / 6f`),
  59-81 (dispatch; **73 `DrawPointsSettings(y)`**), **88-131 (`DrawShortcutList` — the scroll-view
  shape departure 7 copies)**, 271-283 (`DrawRadiusSlider`), 355-367 (`DrawCameraSlider`),
  **394-440 (`DrawPointsSettings` — nine sliders in a 414 px box plus a footnote)**.
  `UI/CommanderOverlayUi.cs:255-261` (the settings window is at most 680 × 790).
- `Core/CommanderSettings.cs` 109-212 (the whole `Points` section: the comment-per-group style and
  the `Get`/`Set` pair shape), **212 (`PointsMineSnapMeters`, the last Points entry)**, 330-362
  (the `_ = …;` warm-up touches, **362 the last Points touch**).
- `Core/CommanderModeController.cs` 72-88 (**78 the point service register and its comment, 84 the
  economy, 85 the enemy commander, 88 the player commander**).
- `Core/CommanderPlugin.cs` 31-40 (the self-check list; **37 `CommanderStrategicPointService.SelfCheck();`**).
- `Core/CommanderGameAccess.cs` 223-243 (`GetFriendlyColor`, `GetHostileColor`), 398-454
  (`SnapToTerrain` — a raycast per call, never per grid sample), 483-486 (`IsBelowSeaLevel`),
  488-495 (**`ShouldAllowCommanderMove` — local HQ only**), **528-536 (`GetUnitCommand`)**,
  538-551 (`SetUnitHoldPosition`), **567-577 (`TrySetDestination`)**, 666-676
  (`HorizontalDistance`, `ApproximatelyEqual`), **678-686 (`IsSpawnableVehicleDefinition`)**,
  802-820 (`GetUnitLabel`), 822-873 (`GetVehicleLabel` and the role labels).
- `Core/CommanderScheduler.cs` 19 (`Stagger`), 30 (`IsDue` — every clock in this track), 36
  (`IsDueRealtime` — UI only).
- `conductor/tracks/strategic-points_20260913/plan.md` — the executor-notes style expected here, and
  its T3/T5 fail-proof records as the model for every self-check task below.

## Design facts fixed by design.md (and eight recorded departures)

All numbers below are the design's defaults unless the last column says **planner-chosen**. The
executor uses these and invents none. Every constant gets a `<summary>` saying what the number
means and why it is that value (`.claude/CLAUDE.md` line 8-9). Comments explain why, never what.

| Constant / setting | Default | Where | What it means and why |
|---|---|---|---|
| `Operations/PlatoonSize` | 6 | slider | Vehicles in a full platoon. Design §1's recipe (3+1+2) adds to exactly this, so the two must be retuned together — the recipe self-check says so at load |
| `Operations/RecipeArmour` | 3 | config only | Armour slots per platoon (`MBT`, `AFV`) — the platoon's weight |
| `Operations/RecipeCarrier` | 1 | config only | Carrier/light slots (`APC`, `LCV`), preferring `captureStrength > 0` — one vehicle that can actually take a point |
| `Operations/RecipeAirDefence` | 2 | config only | Air-defence slots (`AAA`, `IR_SAM`, `R_SAM`) — a platoon in the open with no umbrella is a target |
| `Operations/FrontRangeMeters` | 15000 | slider | A point within this of the nearest enemy-held point or base is front line; beyond it is rear. Equal to `ThreatRadiusMeters` (`Defence.cs:60`) on purpose: the same "it can see it" range the home guard already uses |
| `Operations/FobShare` | 0.5 | slider | At most this share of a commander's platoons sit in forward bases; the rest are reserve or offensive. The guard against a commander that only ever garrisons |
| `Operations/PressureIntervalMinutes` | 12 | slider | Minutes of pressure before the commander attacks with whatever it has. The guard against a commander that never attacks |
| `Operations/OffensiveSpendFraction` | 0.5 | slider | Share of the pot the buy review spends while an attack requisition is open, in place of the 0.25 / 0.45 the tempo knob normally uses |
| `OffensivePurchasesPerReview` | 5 | `const` | Purchases in that same review, in place of 3 / 5 |
| `ObservedRadiusMeters` | 8000 | `const` | Radius around an attack target inside which tracked hostile ground counts toward sizing. Design §3 |
| `ThreatMemorySeconds` | 45 (existing) | `Ai/CommanderEnemyCommanderDefence.cs:63` | A tracking contact older than this is a memory, not a threat. **Reused, not redeclared** (Reuse rule 4): the field threat mark and the attack sizing both need exactly this number and the defence partial already owns it |
| `SizingMultiplier` | 1.5 | `const` | Attack strength as a multiple of the observed defence. Design §3 |
| `MinPlatoonsForBase` | 2 | `const` | Floor for an attack on an airbase, however little is observed |
| `MinPlatoonsForPoint` | 1 | `const` | Floor for an attack on a control point |
| `MaxPlatoonsPerAttack` | 6 | `const` | Ceiling, so one attack cannot swallow the whole force |
| `AxisCandidateRadiusMeters` | 25000 | `const` | Forward bases and held bases within this of the target are form-up candidates. Design §3 |
| `AxisDistanceTolerance` | 0.20 | `const` | Two axes must be within 20% of each other's distance to the target, so neither group arrives long before the other |
| `AxisMinBearingDegrees` | 60 | `const` | Two axes must come in on bearings at least this far apart, or it is one attack with extra steps |
| `FlankMinMeters` / `FlankMaxMeters` | 6000 / 10000 | `const` | With only one candidate, the second group forms this far off the direct line. Design §3 |
| `ReleaseDistanceMeters` | 5000 | `const` | Each group stops this far short of the target on its own side and waits there. Design §3 |
| `AssaultFormUpTimeoutSeconds` | 240 | `const` | Groups wait this long for the stragglers, then go without them (4 min). Design §3 |
| `EnRouteFlipMeters` | 2000 | `const` | A point within this of a group's route is driven through and flipped in passing. Design §3 |
| `RequisitionTimeoutSeconds` | 300 | `const` | A requisition unfilled this long proceeds with what it has or dissolves (5 min). Design §4 |
| `WithdrawStrengthNumerator` / `Denominator` | 1 / 2 | `const` | Below half establishment a platoon withdraws. Integer maths so the boundary is exact: 3 of 6 holds, 2 of 6 withdraws |
| `FailStrengthNumerator` / `Denominator` | 2 / 5 | `const` | Below 40% of establishment an attack has failed. 4 of 10 holds, 3 of 10 fails |
| `Points/MinGarrison` | 2 (existing) | `Core/CommanderSettings.cs:187` | Vehicles in a picket, and the vehicles a faction needs in a ring to hold a point. One definition, already sliderised |
| `ReviewIntervalSeconds` | 30 | `const` | Mission re-planning cadence, matching the buy review (`Ai/CommanderEnemyCommanderService.cs:30`) |
| `MovementIntervalSeconds` | 5 | `const` | Destination re-issue cadence. Fast enough that a platoon that loses its leader is re-formed inside one hold tick, slow enough that it is one networked RPC per vehicle per 5 s at worst |
| `FormationCohesionMeters` | 250 | `const`, **planner-chosen** | How far a member may be from its slot and still count as "with the platoon". Design §1 names the constant and gives no number; a six-vehicle wedge at the move service's 25 m spacing (`CommanderMoveService.cs:36`) spans about 75 m, so 250 m is "closed up" with room for terrain |
| `FrontageWeight` | 1.0 | `const`, **planner-chosen** | How much being on the enemy's doorstep multiplies a point's income when missions are ordered. 1.0 means a point touching the enemy is worth double its income and a point at `FrontRangeMeters` is worth its income. Design §2 says "income × frontage factor" and gives no curve |
| `PressurePerMinute` | 1 | `const` | Design §2/§3: "+1/min", which is what makes the threshold `PressureIntervalMinutes` |
| `PressurePerPointLost` | 2 | `const`, **planner-chosen** | Design §3 says "bonus per point lost" without a number; two minutes' worth, so three losses pull an attack half a review forward |
| `PressurePerEncroachingPoint` | 1 | `const`, **planner-chosen** | Design §3 says "bonus … per enemy point closer to my base than theirs" without a number; one minute's worth per point |
| `PlatoonNames` | `1ST`…`12TH` | `static readonly string[]`, **planner-chosen** | Design §1 gives the format (`1ST PLATOON`) and no ceiling; twelve is `MaxPlatoonsPerAttack` twice over, and past that names wrap with a suffix |

**Which vehicle is which role.** From the definitions the mod already reads
(`Ai/CommanderEnemyCommanderService.cs:651-666`, `Core/CommanderGameAccess.cs:859-872`):
**armour** = `VehicleType.MBT` and `VehicleType.AFV`; **carrier/light** = `VehicleType.LCV`, with
`definition.captureStrength > 0f` preferred inside that group (the same test
`ChooseCaptureUnit` uses, `…Service.cs:596`); **air defence** = `VehicleType.AAA`,
`IR_SAM`, `R_SAM`. `VehicleType.ART` is a combat vehicle with no recipe slot — it fills a
fallback slot like anything else. `VehicleType.RDR` is never claimed: the recon screen already owns
those (`Ai/CommanderEnemyCommanderGround.cs:53-69`). A **munitions truck** is identified by
departure 3, never by `VehicleType.TRUCK` alone.

**Server guard.** Every review, movement tick and claim runs only when
`CommanderGameAccess.GetLocalHq()` is non-null and the HQ being acted on has `IsServer` (Testing
rule 3; the same guard shape as `Ai/CommanderEnemyCommanderService.cs:225-231`). A pure
multiplayer client forms no platoons; that is the design's "host only".

**Which HQs are managed.** `CommanderPlayerCommanderService.IsCommanded(hq, localHq)`
(`Ai/CommanderPlayerCommanderService.cs:62-68`), exactly as every other AI loop does, so the
player's own AI commander gets the same doctrine for free (design §1 decision 6).

### Departures

- **Departure 1 — the platoon move seam is a new static on the move service, not `ApplyOrder`.**
  Design §1 says "reuse `CommanderMoveService` formation code; no second follower", which this
  does — the formation maths, the spacing, the shape and the re-issue tolerance are all the move
  service's. It cannot go through `ApplyOrder` (`Units/CommanderMoveService.cs:343-414`) for three
  reasons, each fatal on its own: (a) `ApplyOrder` only touches units passing
  `ShouldAllowCommanderMove`, which is local-HQ-only (`Core/CommanderGameAccess.cs:488-495`), so an
  enemy commander's platoon would be filtered out entirely; (b) it calls `MarkPlayerOrdered`
  (line 406), which makes `HasPlayerOrder` true for 10 minutes
  (`…MoveService.cs:1034-1049`), and every AI recruiter in the mod — including this track's own —
  skips a unit under a player order, so the operations service would immediately disown its own
  platoon; (c) units in `orders` are driven by `AdvanceRoutes` (496-641), whose retreat,
  opportunity-engagement and guard branches would fight the platoon state machine for the same
  vehicle. So T5 adds `internal static void IssuePlatoonMove(IReadOnlyList<Unit>, GlobalPosition,
  CommanderFormationShape, Dictionary<Unit, GlobalPosition>)` beside `Issue`, which writes nothing
  into `orders`, `stoppedUnits` or `playerOrderedAt`. Two behaviour-neutral extractions go with it
  (Reuse rule 5): the heading maths out of `ResolveHeading` (416-448) and the literal `40f`
  re-issue tolerance out of `Issue` (947).
- **Departure 2 — the design's three point kinds are seven.** design.md was written against
  village / hilltop / base. The points track shipped `Site`, `Village`, `Hilltop`, `Outpost`,
  `Crossroads`, `Roadside`, `Base` (`Points/CommanderStrategicPoint.cs:7-36`). Everywhere the
  design says "control point" this plan uses `StrategicPointKinds.IsControlPoint`
  (`…Point.cs:43-53`), which is exactly the five presence-held kinds; `Site` is never a mission
  objective (nobody holds it by standing on it — its mine holds it) and `Base` is an objective only
  for an attack, never for a forward base or a picket.
- **Departure 3 — a munitions truck is found by its components, not by a vehicle type.** design.md
  §2 says "the game's rearm vehicle; the supply code already knows the type". It does not: the
  supply code recognises rearm capability on a **cargo mount** (`mount.info.rearmGround`,
  `Supply/CommanderSupplyHeliCatalog.cs:401`) and on a **live unit** (`Rearmer`,
  `Core/CommanderGameAccess.cs:153`), and there is no vehicle-definition-level lookup anywhere in
  the mod. `VehicleType.TRUCK` is too broad — it is every truck, rearm or not. T9 adds one
  definition, `CommanderGameAccess.IsMunitionsTruckDefinition(VehicleDefinition?)` beside
  `IsSpawnableVehicleDefinition` (678-686), testing the prefab for a `RearmVehicleAI` **and** a
  `Rearmer` — the exact pair `CommanderMoveService.TryReturnToBasegameLogistics` (1413-1418)
  already treats as "this is a rearm vehicle". A claimed truck is detached via
  `CommanderMoveService.TryDetachFromRearmLogistics(unit, allowRestock: false)` (ledger row 31),
  extracted out of `TryReturnToBasegameLogistics` rather than re-implemented from memory, with
  `allowRestock: false` forcing the `Wait` branch instead of `DriveToRestock` — calling
  `TryReturnToBasegameLogistics` itself (`allowRestock: true`) would hand the truck back to the
  game's rearm AI and, below half capacity, drive it off the forward base, which is exactly the
  failure this departure exists to prevent.
- **Departure 4 — release points are chosen from the discovered road points, not from the road
  network.** design.md §3 says a release point sits on the "road network where possible".
  Discovery's road caches (`roadPointLists`, `junctionNodes`) are cleared the moment discovery
  finishes (`Points/CommanderStrategicPointDiscovery.cs:1029-1030`), and re-reading
  `LevelInfo.roadNetwork` would be a second, live traversal of a few hundred roads inside a 30 s
  review. But the road **is** already in the point list: `Crossroads` and `Roadside` points are
  generated from it, up to 24 + 30 of them (`Core/CommanderSettings.cs:155-157`), spaced 6 km apart
  (`:147`). So a release point is the nearest `Crossroads` or `Roadside` point to the ideal
  position, accepted when it is within `ReleaseSnapMeters` (3000, planner-chosen — half the
  *Roadside* spacing, `PointsRoadsideSpacingMeters` = 6000, so the snap can never cross to the
  next Roadside point along; Crossroads points are spaced only 2500 m apart
  (`Core/CommanderSettings.cs:160`), so that guarantee does not hold for Crossroads — a release
  point can still land on a Crossroads point beyond the nearest one) and otherwise the ideal
  position itself, terrain-snapped. Same outcome, no second road read.
- **Departure 5 — no A\* route for a ground platoon.** design.md §3 offers the terrain planner as
  the fallback when there is no road. `CommanderTerrainFlightPlanner.TryBuildRoute`
  (`Terrain/CommanderTerrainFlightPlanner.cs:13-196`) is an **airborne** planner: it returns
  waypoints at `height + clearance`, allocates four arrays sized to the search grid per call, and
  writes a `SAM flight route planned:` line to the log every time. Ground vehicles do not need it —
  `UnitCommand.SetDestination` hands the vehicle to the game's own ground navigation, which is what
  every existing ground order in the mod relies on (`Ai/CommanderCaptureService.cs:583`,
  `Ai/CommanderEnemyCommanderDefence.cs:187`). What the platoon actually needs is a **release point
  on ground worth standing on**, and that is one height-map query, not a search: a candidate is
  rejected when `EstimateStrategicTerrainNormalY(x, z, 40f) < FlatNormalY` (0.94, the discovery
  constant at `…Discovery.cs:21`) or `CommanderGameAccess.IsBelowSeaLevel` is true, and the next
  candidate on the ring is tried — the `EnsureDefencePosts` shape
  (`Ai/CommanderEnemyCommanderDefence.cs:280-294`).
- **Departure 6 — the enemy expansion drive stands down, it is not deleted.**
  `CommanderCaptureService.ReviewEnemy` (`Ai/CommanderCaptureService.cs:534-591`) drives three
  capture-capable units at an airbase; with operations owning every ground vehicle it would fight
  the offensive for the same units. It is gated off per HQ (`ReviewEnemy` returns early when the
  operations service is managing that HQ's ground force) rather than removed, because the same
  service also serves the **player's** capture clicks (`RefreshTargets`, `TryResolveCaptureOrder`,
  `AnnounceCaptureOrder`) and those are untouched. `WantsCaptureUnit` goes false for a managed HQ,
  so the buy loop's capture-unit override yields to the order book instead of racing it.
- **Departure 7 — the OPERATIONS sliders need the POINTS tab to scroll.** design.md §5 asks for an
  OPERATIONS block on the POINTS tab. That tab already draws a 414 px box plus a footnote from
  `y = 82` with the help overlay closed and `y = 162` with it open
  (`UI/CommanderOverlayUiSettings.cs:394-440`), ending at about 600 px of a 790 px window
  (`UI/CommanderOverlayUi.cs:256`). Five more 38 px sliders in their own headed box is 242 px more,
  which overflows at 842 px with help open. The tab's content moves inside a scroll view, copying
  `DrawShortcutList`'s shape (`…Settings.cs:88-131`) — the same sliders, the same helpers, one
  scrollbar when the window is short.
- **Departure 8 — the home guard is suspended per HQ, not removed.** design.md §1 says "the home
  guard becomes the base's reserve platoon(s)". `IsDefendingUnit`
  (`Ai/CommanderEnemyCommanderDefence.cs:85-102`) and its two callers keep working, and
  `ReviewDefence` keeps its whole threat posture (`state.Defending` feeds the HUD status line at
  `…Service.cs:249-259` and the buyer's `WantsDefenceUnit` at `Defence.cs:176`). What stops for a
  managed HQ is the **recruiting** half: no `RecruitDefenders`, no `ReleaseSurplusDefenders`, no
  ring posts. The guard therefore still runs during the seconds before point discovery finishes and
  on any mission where the operations service never takes over, which is the only reason to keep
  the code path alive at all.

## The call-site ledger (the plan evaluator checks every row)

Every existing line the track changes, and what it becomes. New files are not in this table.

| # | Site today | Becomes |
|---|---|---|
| 1 | `Core/CommanderSettings.cs:212` (`PointsMineSnapMeters`, last Points entry) | Followed by a new `Operations` section: `PlatoonSize` (int), `RecipeArmour` (int), `RecipeCarrier` (int), `RecipeAirDefence` (int), `FrontRangeMeters` (float), `FobShare` (float), `PressureIntervalMinutes` (float), `OffensiveSpendFraction` (float) — `Get`/`Set` pairs in the style of line 187, each group under a one-line comment saying what it tunes (recipe is config-file-only; the other five are on the POINTS tab) |
| 2 | `Core/CommanderSettings.cs:362` (`_ = PointsMineSnapMeters;`) | Eight `_ = Operations…;` touches after it, before `_ = AirCommandMode;` at 363 |
| 3 | `Core/CommanderModeController.cs:76-78` (the point-service register and its comment) | A second register after it and before `CommanderFactionVehicleService` at 79: `services.Register(new CommanderOperationsService(), CommanderTier.Advanced);` with a comment — after the point service whose list is its objective list, before the enemy commander (85) so the buyer reads **this** review's order book rather than the last one's |
| 4 | `Core/CommanderPlugin.cs:37` (`CommanderStrategicPointService.SelfCheck();`) | `CommanderOperationsService.SelfCheck();` added on the next line |
| 5 | `AirCommand/CommanderAirCommandPatches.cs:97-100` `RegisterFactionUnitPostfix` | A second line in the same postfix: `CommanderOperationsService.NotifyFactionUnitRegistered(__instance, unit);`. No new patch class — the hook already exists twice on this method (`Supply/CommanderSupplyHeliPatches.cs:17-22`), and a third would be a third reflection lookup for nothing. The forwarder on the service copies `CommanderAirCommandPilotHooks.cs:12-15` |
| 6 | `Core/CommanderGameAccess.cs:678-686` (`IsSpawnableVehicleDefinition`) | A sibling `internal static bool IsMunitionsTruckDefinition(VehicleDefinition? definition)` right after it (departure 3): prefab non-null and carries both `RearmVehicleAI` and `Rearmer` (`GetComponentInChildren<T>(true)`), with a `<summary>` citing `CommanderMoveService.TryReturnToBasegameLogistics` as the pair's other reader |
| 7 | `Units/CommanderMoveService.cs:36` `private const float GroundFormationSpacingMeters = 25f;` | `internal const` (unchanged value), so the platoon move uses the one definition the player's own orders use |
| 8 | `Units/CommanderMoveService.cs:941-953` `Issue`, the literal `40f` at 947 | `internal const float ReissueToleranceMeters = 40f;` declared beside `GroundFormationSpacingMeters` with a `<summary>` (a destination that moved less than this is not worth a networked RPC — the reason already in the comment at 942-943), and `Issue` uses it. Second caller: `IssuePlatoonMove` |
| 9 | `Units/CommanderMoveService.cs:416-448` `ResolveHeading` | Its last four lines (`Vector3 travel = destination - centre / count; travel.y = 0f; return travel.sqrMagnitude < 1f ? 0f : Quaternion.LookRotation(travel).eulerAngles.y;`) MOVE into `internal static float HeadingDegrees(Vector3 centre, Vector3 destination)`, and `ResolveHeading` returns `HeadingDegrees(centre / count, destination)`. Behaviour-neutral (Reuse rule 5); pointer comment left at the old site |
| 10 | `Units/CommanderMoveService.cs:954` (after `Issue`) | New `internal static void IssuePlatoonMove(IReadOnlyList<Unit> members, GlobalPosition destination, CommanderFormationShape shape, Dictionary<Unit, GlobalPosition> issued)` — departure 1. Slot `i` is `CommanderDestinationFormation.ApplyOffset(destination, i, GroundFormationSpacingMeters, shape, HeadingDegrees(centroid, destination))`; a slot within `ReissueToleranceMeters` of this unit's last issued slot is skipped; otherwise `CommanderGameAccess.TrySetDestination(unit, slot)` and `issued[unit] = slot`. Writes nothing into `orders`, `stoppedUnits` or `playerOrderedAt` |
| 11 | `Ai/CommanderEnemyCommanderService.cs:209-215` `if (IsDue(ref nextDefenceAt, …)) { ReviewDefences(localHq); ReviewGarrisons(localHq); }` | The `ReviewGarrisons(localHq);` call and its two comment lines (212-213) are removed; `ReviewDefences(localHq);` stays on the same clock |
| 12 | `Ai/CommanderEnemyCommanderService.cs:275-280` `garrisonCandidates.Clear(); staleGarrison.Clear(); … garrisonPosts.Clear();` (with its 277-279 comment) | Removed with the garrison file. `reconUnits`, `shipCatalog`, `defenceCandidates`, `staleDefenders` stay |
| 13 | `Ai/CommanderEnemyCommanderService.cs:338-339` `float pot = …; float spendable = pot * (duel ? DuelSpendFraction : SpendFraction);` | `float fraction = CommanderOperationsService.HasOpenAttackRequisition(hq) ? CommanderSettings.OperationsOffensiveSpendFraction : (duel ? DuelSpendFraction : SpendFraction);` then `float spendable = pot * fraction;`. Design §4: an open attack requisition raises the tempo for that review only |
| 14 | `Ai/CommanderEnemyCommanderService.cs:354` `int purchases = duel ? DuelPurchasesPerReview : PurchasesPerReview;` | `int purchases = CommanderOperationsService.HasOpenAttackRequisition(hq) ? OffensivePurchasesPerReview : (duel ? DuelPurchasesPerReview : PurchasesPerReview);` with `OffensivePurchasesPerReview = 5` declared beside `DuelPurchasesPerReview` (56) with a `<summary>` |
| 15 | `Ai/CommanderEnemyCommanderService.cs:369-395` the buy loop (the `needsCaptureUnit` / `blindToAir` / `WantsDefenceUnit` / `WantsReconUnit` ladder) | One branch inserted **after** the existing overrides and **before** `Choose(spendable, buyPlan)`: when the order book has an open line, `choice = ChooseForRole(spendable, CommanderOperationsService.LargestOpenRole(hq)) ?? Choose(spendable, buyPlan)`. `ChooseForRole` is a new private sibling of `ChooseCaptureUnit` (586-605) — the **cheapest** catalogue entry of that role the faction fields, since an order line wants a body in a slot, not the best vehicle. The plan-based counter triangle is untouched when the book is empty (design §4) |
| 16 | `Ai/CommanderEnemyCommanderService.cs:781-784` `CommanderState.Garrison` and its `<summary>` | Removed. The operations service keeps its own per-HQ state; nothing is added to `CommanderState` |
| 17 | `Ai/CommanderEnemyCommanderDefence.cs:159-176` (`EnsureDefencePosts` … `state.WantsDefenceUnit = …`) | Wrapped: `if (CommanderOperationsService.OwnsGroundForce(hq)) { if (state.Defenders.Count > 0) { ReleaseSurplusDefenders(state, 0); } state.WantsDefenceUnit = false; return; }` inserted after the posture block at 157 (departure 8). The posture (145-157) and the pin loop's mechanism are untouched; with no defenders the loop at 178-188 iterates nothing |
| 18 | `Ai/CommanderEnemyCommanderDefence.cs:372-374` `// A vehicle already standing on a control point garrison … && !IsGarrisonUnit(unit)` | `&& !CommanderOperationsService.IsPlatoonUnit(unit)` with the comment reworded to name the platoon pool. Same purpose, same place in the filter |
| 19 | `Ai/CommanderEnemyCommanderGarrison.cs:328-353` (`EnsureGarrisonPosts`) | **T7**: the ring-post logic is CUT into `Operations/CommanderOperationsFront.cs` as `internal List<GlobalPosition> EnsureHoldPosts(CommanderStrategicPoint point, int slots)` (Reuse rule 3, renamed and re-summarised); the old method's body is REPLACED with a single forwarding call, `CommanderOperationsService.Instance?.EnsureHoldPosts(point, CommanderSettings.PointsMinGarrison + 1) ?? new List<GlobalPosition>()`, plus a pointer comment saying the logic moved and that this forwarder dies with the file in T11. This keeps `DriveGarrisonToPosts` (`…Garrison.cs:300`) compiling through T8-T10. **T11**: the whole file — forwarder and pointer comment included — is deleted; nothing else in it survives |
| 20 | `Ai/CommanderCaptureService.cs:684-692` `CollectCaptureSquad`'s filter and its comment | `!CommanderEnemyCommanderService.IsGarrisonUnit(unit)` → `!CommanderOperationsService.IsPlatoonUnit(unit)`; the comment at 684-688 gains one clause naming the platoon pool |
| 21 | `Ai/CommanderCaptureService.cs:534-540` `ReviewEnemy`'s head | After the `drives` lookup: `if (CommanderOperationsService.OwnsGroundForce(hq)) { drive.Target = null; drive.WantsCaptureUnit = false; return; }` with a `<summary>` addition on the method explaining departure 6 |
| 22 | `Points/CommanderStrategicPointService.cs:705` `CheckGarrisonTargets(failures);` | Removed with the garrison file |
| 23 | `Points/CommanderStrategicPointService.cs:1030-1058` (`CheckGarrisonTargets` and its doc comment) | Removed. The reach-and-cap rule it guarded no longer exists; the operations service's own mission-ordering check replaces it |
| 24 | `Points/CommanderStrategicPointService.cs:400-403` (the `NearestFreeSiteIndex` doc comment's `<c>SelectGarrisonTargets</c> (T9)` cross-reference) | Reworded to state the inclusive-boundary convention without naming the deleted method |
| 25 | `UI/CommanderAiLogUi.cs:194-231` `DrawHeader` (three 22 px rows, returning `y + 26f`) | A fourth row and a fifth: `OPERATIONS  PRESSURE {p:0}/{interval:0}  PLATOONS {n}  FOBS {f}  ORDERS {o}` and one line per live mission (at most `MissionLinesInHeader = 3`, planner-chosen), each `26f` tall, from `CommanderOperationsService.Instance?.DescribeOperations(hq, list)`. Rows are added only when that returns something, so a mission with no operations service reads exactly as today |
| 26 | `UI/CommanderOverlayUiSettings.cs:394-440` `DrawPointsSettings` | Body wrapped in the `DrawShortcutList` scroll-view shape (88-131; departure 7), and a second headed box `OPERATIONS` after the existing one with five `DrawCameraSlider` rows: platoon size (2-10, " vehicles", int), FOB share (0-1, "0.00", int per cent via `× 100`), front range (5-40, " km", stored in metres), pressure interval (4-30, " min"), offensive spend (0.1-1, "0.00"). A muted footnote: `Platoon recipe lives in the config file, Operations section.` |
| 27 | `Units/CommanderWorldMarkerRenderer.cs:62` `CommanderStrategicPointService.Instance?.DrawMarkers(camera);` | Followed by `CommanderOperationsService.Instance?.DrawMarkers(camera);` — platoons and release crosses draw over the points they stand on |
| 28 | `CHANGELOG.md:228` `### Added` under `## Unreleased` (first bullet at 230) | A new bullet at the top of that list |
| 29 | `README.md:630` (`## Strategic points`) … `:703` (`## Unit systems`) | A new `## Platoon operations` section inserted between them, after the strategic-points section and before `## Unit systems` |
| 30 | `conductor/tracks/platoon-operations_20260913/design.md` (end of file) | A `## Departures recorded during planning` section listing the eight departures above in one line each, so the approved design and the shipped behaviour do not silently diverge |
| 31 | `Units/CommanderMoveService.cs:1413-1451` `TryReturnToBasegameLogistics` | **T9**: the shared body (component guard, mission-controller unassign loop, `AssignMission(null!)`, `needsRestock` computation, the reflection call) is EXTRACTED into `internal static bool TryDetachFromRearmLogistics(Unit unit, bool allowRestock)`, with a `<summary>` naming its two callers. `TryReturnToBasegameLogistics` becomes a one-line call with `allowRestock: true` (behaviour-neutral). The operations claim calls it with `allowRestock: false`, which forces the `Wait` branch regardless of capacity and sets `rearmer.AvailableForMission = false`, so the truck cannot be re-tasked while parked at a forward base |

Not changed, on purpose: `Ai/CommanderEnemyCommanderDefence.cs` threat constants, posture arithmetic
and `CheckDefencePosture`. `Ai/CommanderEnemyCommanderGround.cs` (the recon screen keeps its `RDR`
vehicles; they are never claimed). `Ai/CommanderEnemyCommanderAir.cs` and the naval buy (design's
out-of-scope). `Points/*` discovery, hold state machine and income. `Economy/*`. The player's own
order path (`ApplyOrder`, `IssueOrderAt`, `AdvanceRoutes`) and every hands-off rule in it.

## Tasks

- [x] **T1 — Operations settings.** `Core/CommanderSettings.cs`: ledger rows 1-2. Section
  `"Operations"`, keys exactly `PlatoonSize` (int, 6), `RecipeArmour` (int, 3), `RecipeCarrier`
  (int, 1), `RecipeAirDefence` (int, 2), `FrontRangeMeters` (float, 15000), `FobShare` (float,
  0.5), `PressureIntervalMinutes` (float, 12), `OffensiveSpendFraction` (float, 0.5), exposed as
  `OperationsPlatoonSize`, `OperationsRecipeArmour`, … in the `Points*` naming style. Each group
  under a one-line comment saying what it tunes and which of the two homes it has (config file or
  POINTS tab). Build.

- [x] **T2 — Data types, service skeleton, registration, self-check harness.** Create
  `Operations/CommanderPlatoon.cs`:
  - `internal enum CommanderPlatoonRole { Armour, Carrier, AirDefence, Truck, Other }` — every
    value with a `<summary>` naming the `VehicleType`s behind it (see "Which vehicle is which
    role"), plus `internal static class CommanderPlatoonRoles` with
    `internal static CommanderPlatoonRole Of(VehicleDefinition? definition)` — the ONE definition
    of the role mapping, used by the recipe, the order book and the buyer.
  - `internal enum CommanderPlatoonState { Forming, Moving, Holding, Attacking, Withdrawing }` and
    `internal enum CommanderMissionKind { ForwardBase, Picket, Attack, Reserve }`, both with
    per-value `<summary>`s taken from design §1/§2/§3.
  - `internal sealed class CommanderPlatoon`: `Name` (`"1ST PLATOON"`), `Members`
    (`List<Unit>`), `Leader` (`Unit?` — members[0] while alive, re-picked otherwise),
    `State`, `Mission` (`CommanderOperationsMission?`), `Objective` (`GlobalPosition`),
    `Establishment` (`int` — what a full platoon is, so strength fractions mean something after a
    recipe retune), `Issued` (`Dictionary<Unit, GlobalPosition>` — the per-member last-issued
    destination `IssuePlatoonMove` needs), `FormedAt`/`StateSince` (scaled `Time.time`). Every
    field a `<summary>`.
  - `internal sealed class CommanderOperationsMission`: `Kind`, `Point`
    (`CommanderStrategicPoint?`), `TargetAirbase` (`Airbase?`), `WantedPlatoons`, `Assigned`
    (`List<CommanderPlatoon>`), `Axes` (`List<CommanderAssaultGroup>`), `OpenedAt`, `Label`.
    `internal sealed class CommanderAssaultGroup`: `FormUpPoint`, `ReleasePoint`, `Platoon`,
    `Arrived`.
  Create `Operations/CommanderOperationsService.cs`: `internal sealed partial class
  CommanderOperationsService : ICommanderTickPersistent, ICommanderResetSession`, file-scoped
  namespace, class `<summary>` (what a platoon is, what a forward base is, what a picket is, what
  the offensive is — one paragraph) and a `<remarks>` "ponytail:" note (no persistence across a
  reload; no player platoon tools; no air or naval tasking — all later tracks). Members:
  `internal static CommanderOperationsService? Instance` set in the constructor (as
  `Points/CommanderStrategicPointService.cs:65-68`); `private readonly Dictionary<FactionHQ,
  OperationsState> states`; the two clocks
  (`private float nextReviewAt = CommanderScheduler.Stagger("operations.review",
  ReviewIntervalSeconds);` and the same for `operations.movement`) and their two constants with
  `<summary>`s. `private sealed class OperationsState`: `Pool` (`List<Unit>`), `Platoons`
  (`List<CommanderPlatoon>`), `Missions` (`List<CommanderOperationsMission>`), `Requisitions`
  (`List<CommanderRequisition>`), `Pressure` (float), `LastPointsHeld` (int), `NextPlatoonNumber`
  (int). `TickPersistent()`: local HQ non-null server guard, then the two `CommanderScheduler.IsDue`
  branches calling `Review()` and `TickMovement()` — both left as empty private methods with a
  `// T7` / `// T5` marker the later task removes. `ResetSession()`: clear `states` and re-stagger
  both clocks. `internal static void SelfCheck()` with the `List<string> failures` + per-subsystem
  `Check*` shape and the three `Expect` overloads plus `ExpectSequence`, copied from
  `Points/CommanderStrategicPointService.cs:697-720, 1060-1098` — **copied, not shared**:
  `ExpectSequence` and the three `Expect` overloads are `private` to
  `CommanderStrategicPointService`, and both are load-time harnesses, so this is a second harness
  by necessity, not a missed reuse; say so in a comment. Category string `"Operations self-check FAILED: "`.
  Ledger rows 3-4. Build.

- [x] **T3 — The pool: claim, sweep, release, `IsPlatoonUnit`, `OwnsGroundForce`.** Create
  `Operations/CommanderOperationsRequisitions.cs` (partial) and put the claim in it.
  - `internal static void NotifyFactionUnitRegistered(FactionHQ hq, Unit unit)` — the one-line
    forwarder shape (`AirCommand/CommanderAirCommandPilotHooks.cs:12-15`), calling
    `Instance?.TryClaim(hq, unit)`. Ledger row 5.
  - `private void TryClaim(FactionHQ hq, Unit unit)`: claim when `states` already holds `hq` (so
    the service has decided it manages this commander), `hq.IsServer`, `unit is GroundVehicle`,
    `!unit.disabled`, `unit.definition is VehicleDefinition definition`, the role is not the recon
    screen's (`definition.vehicleType != VehicleType.RDR`), and
    `CommanderMoveService.Instance?.HasPlayerOrder(unit) != true` (design §1: the player's
    hand-ordered vehicles are never claimed). Adds to `Pool`.
  - `private void SweepPool(FactionHQ hq, OperationsState state)`, run at the top of every review:
    walks `hq.factionUnits` with the same filter and adds anything missing (the claim hook is the
    prompt notification; the sweep is the ground truth, and it is what picks up the vehicles a
    mission was authored with, which register before this service has a state for their HQ), then
    drops pool and platoon members that are null, disabled, changed faction, or under a player
    order — the `PruneDefenders` filter (`Ai/CommanderEnemyCommanderDefence.cs:297-320`), including
    its comment about leaving `commandedDestination` alone.
  - `internal static bool IsPlatoonUnit(Unit? unit)` — the `IsDefendingUnit` shape
    (`…Defence.cs:85-102`) over every state's `Pool`.
  - `internal static bool OwnsGroundForce(FactionHQ hq)` — `Instance != null &&
    Instance.states.ContainsKey(hq)`; a state is only created once
    `CommanderStrategicPointService.Instance?.Points` is non-empty, so before discovery finishes
    this is false and the home guard and the expansion drive still run (departure 8, departure 6).
  - A state is created per managed HQ in `Review()` using the `IsCommanded` + `IsServer` +
    `faction != null` loop shape (`Ai/CommanderEnemyCommanderService.cs:223-231`); states for HQs
    that stop being commanded are dropped the `PruneStates` way (`…Service.cs:626-644`).
  Ledger rows 5, 18, 20, 21. Build. Greps to paste: `IsGarrisonUnit` (expect only the two call
  sites left for T11 to fix, plus the garrison file), `IsPlatoonUnit` (definition + the two callers
  this task adds).

- [x] **T4 — Recipe fill and platoon formation, with self-check and fail-proof.** In
  `Operations/CommanderOperationsService.cs`:
  - Pure: `internal static void FillRecipe(IReadOnlyList<CommanderPlatoonRole> roles,
    IReadOnlyList<bool> prefers, IReadOnlyList<int> want, int platoonSize, List<int> picks)` —
    `picks` cleared first. Pass 1, for each role in `Armour, Carrier, AirDefence` order, takes up
    to `want[(int)role]` candidates of that role, **preferring** entries whose `prefers[i]` is true
    (design §1: a carrier with `captureStrength > 0` beats one without) and otherwise in list
    order. Pass 2, while `picks.Count < platoonSize`, takes any unpicked candidate whose role is
    not `Truck` (design §1: "Unfillable slot → any combat vehicle"; a munitions truck is
    requisitioned separately and never fills a combat slot). Never exceeds `platoonSize`; never
    picks the same index twice.
  - `private CommanderPlatoon? TryFormPlatoon(OperationsState state, int wantedSize)` — builds the
    role and preference arrays from the free pool (pool members with no platoon), calls
    `FillRecipe`, and forms a platoon when at least **one** member came back (design §1 forms on
    what it has and requisitions the rest; a platoon of one is `Forming`, not a platoon that never
    exists). Name from `PlatoonNames[state.NextPlatoonNumber++ % PlatoonNames.Length]`;
    `Establishment = wantedSize`; `State = Forming`; log through
    `CommanderAiLog.Note(hq, $"forms {platoon.Name}: {n}/{wantedSize} vehicles.")`.
  - `private void ReinforcePlatoon(...)` — the same fill over the free pool, for a platoon below
    establishment, on every review.
  **Self-check (recipe fill with fallbacks)** in `CheckRecipe(failures)`, at the default recipe
  3/1/2 and `platoonSize = 6`:
  - exact supply — roles `[Armour, Armour, Armour, Carrier, AirDefence, AirDefence]`, no
    preferences → `picks` has 6 entries and every index appears once ("a pool that matches the
    recipe fills it exactly");
  - preference — roles `[Carrier, Carrier]`, `prefers = [false, true]`, `want` carrier 1 → the
    picked carrier is index 1 ("a capture-capable carrier is taken ahead of one that cannot take
    ground");
  - fallback — roles `[Armour, Armour, Armour, Armour, Armour, Armour]` → 6 picks, of which 3 fill
    the armour slots and 3 fill by fallback ("a pool of nothing but armour still forms a full
    platoon");
  - short pool — roles `[Armour, AirDefence]` → exactly 2 picks ("a short pool forms a short
    platoon rather than none");
  - trucks excluded — roles `[Armour, Truck, Truck, Truck, Truck, Truck]` → exactly 1 pick ("a
    munitions truck never fills a combat slot");
  - the retune guard Testing rule 1 asks for: the live
    `RecipeArmour + RecipeCarrier + RecipeAirDefence == PlatoonSize`, read into locals first so the
    compiler cannot fold the comparison (`Ai/CommanderEnemyCommanderDefence.cs:436-443` explains
    why) ("the platoon recipe adds up to the platoon size; check the Operations section of the
    config").
  Build. **Testing rule 5 fail-proof**: record SHA256 (`Get-FileHash -Algorithm SHA256`) of
  `Operations/CommanderOperationsService.cs`; plant the pass-2 role test as `role != Other` instead
  of `role != Truck`; build (must still succeed); read the file and name the failing case ("a
  munitions truck never fills a combat slot" — picks is now 6, not 1); restore; build; SHA256
  byte-identical. Write it in the notes.

- [x] **T5 — Platoon movement seam and the arrival rule, with self-check and fail-proof.**
  `Units/CommanderMoveService.cs`: ledger rows 7-10, in that order (the two extractions first, so
  `IssuePlatoonMove` is written against the shared definitions). `HeadingDegrees` and
  `IssuePlatoonMove` each get a `<summary>`; `IssuePlatoonMove`'s says in one sentence why it does
  not go through `ApplyOrder` (departure 1) and that its caller owns the `issued` dictionary.
  In `Operations/CommanderOperationsService.cs`:
  - Pure: `internal static bool HasArrived(float leaderDistanceMeters, float objectiveRadiusMeters,
    int membersInCohesion, int memberCount)` — design §1: `leaderDistanceMeters <=
    objectiveRadiusMeters && membersInCohesion * 2 >= memberCount`. Inclusive boundary both ways,
    the convention the rest of the mod uses.
  - `private void TickMovement()` (replacing T2's stub): for every managed HQ, every platoon with a
    destination, `CommanderMoveService.IssuePlatoonMove(platoon.Members, platoon.Objective,
    CommanderFormationShape.Wedge, platoon.Issued)` — wedge because design §1 names it, and because
    it is the shape that puts the leader in front. A platoon in `Holding` is issued its hold posts
    instead (T7). Leader re-pick when `Leader` is null or disabled: the first live member.
  **Self-check (arrival rule)** in `CheckArrival(failures)`: `!HasArrived(401f, 400f, 6, 6)` ("a
  leader outside the objective ring has not arrived"); `HasArrived(400f, 400f, 6, 6)` ("a leader
  exactly on the ring edge counts as inside"); `!HasArrived(100f, 400f, 2, 6)` ("a leader on the
  objective with fewer than half the platoon closed up has not arrived"); `HasArrived(100f, 400f,
  3, 6)` ("exactly half the platoon closed up counts as arrived"); `HasArrived(0f, 400f, 1, 1)`
  ("a one-vehicle platoon arrives when its leader does"). Build.
  **Fail-proof**: SHA256 of `Operations/CommanderOperationsService.cs`; plant `membersInCohesion *
  2 >= memberCount` → `> memberCount`; build; name the failing case ("exactly half the platoon
  closed up counts as arrived": 3 × 2 = 6 is no longer `> 6`); restore; build; SHA256 identical.
  Notes.

- [x] **T6 — Front line and point value, with self-check and fail-proof.** Create
  `Operations/CommanderOperationsFront.cs` (partial).
  - Pure: `internal static bool IsFrontPoint(float distanceToNearestEnemyAssetMeters, float
    frontRangeMeters)` → `distanceToNearestEnemyAssetMeters <= frontRangeMeters`.
  - Pure: `internal static float PointValue(float incomePerMinute, float
    distanceToNearestEnemyAssetMeters, float frontRangeMeters)` → `incomePerMinute * (1f +
    FrontageWeight * Mathf.Clamp01(1f - distance / Mathf.Max(1f, frontRangeMeters)))`. Design §2's
    "income × frontage factor (closer to enemy assets = higher)"; the curve is planner-chosen, see
    the defaults table.
  - `private float NearestEnemyAssetDistance(FactionHQ hq, GlobalPosition position)` — the shortest
    horizontal distance to any point whose `GetOwner()` is a live HQ other than `hq`
    (`Points/CommanderStrategicPoint.cs:143-154`) or any airbase another HQ holds
    (`Airbase.CurrentHQ`, the live read `Ai/CommanderCaptureService.cs:475-488` uses). Returns
    `float.MaxValue` when the commander can see no enemy asset at all, which makes every point rear
    and is the correct answer on a map it has not met anyone on yet.
  - `private void RankPoints(FactionHQ hq, OperationsState state)` — every `IsControlPoint` point
    (departure 2), each with its distance, its front/rear flag, its threat mark (below) and its
    value from `IncomePerMinute(point.Kind, PointIncomeRates.FromSettings())`
    (`Points/CommanderStrategicPointService.cs:596-612`), sorted by value descending. Ties break on
    distance ascending so the order is stable across reviews.
  - `private bool HasThreatMark(FactionHQ hq, CommanderStrategicPoint point)` — a hostile ground
    unit in `hq.trackingDatabase` seen within `ThreatMemorySeconds` (the **existing** constant,
    ledger note in the defaults table) inside `point.Radius * ThreatMarkRingMultiplier` — the
    `IsUnderThreat` walk (`Ai/CommanderEnemyCommanderDefence.cs:197-228`) including its
    skip-buildings clause, retargeted from "near my base" to "near this point".
    `ThreatMarkRingMultiplier = 4` (planner-chosen: a 300 m ring seen from 1.2 km is "they are at
    the gate").
  **Self-check (front/rear classification and point value ordering)** in `CheckFront(failures)`, at
  `frontRange = 15000`: `IsFrontPoint(14999f, 15000f)` and `IsFrontPoint(15000f, 15000f)` ("a point
  exactly at the front range is front line"); `!IsFrontPoint(15001f, 15000f)` ("a point past the
  front range is rear"); `PointValue(10f, 0f, 15000f) == 20f` ("a point touching the enemy is worth
  double its income"); `PointValue(10f, 15000f, 15000f) == 10f` ("a point at the front range is
  worth its income"); `PointValue(10f, 30000f, 15000f) == 10f` ("a deep rear point never drops
  below its income"); and the ordering the missions are filled in:
  `PointValue(5f, 1000f, 15000f) > PointValue(10f, 30000f, 15000f)` is **false** but
  `PointValue(10f, 1000f, 15000f) > PointValue(10f, 14000f, 15000f)` is true ("of two points paying
  the same, the one nearer the enemy is filled first"). Build.
  **Fail-proof**: SHA256 of `Operations/CommanderOperationsFront.cs`; plant `1f - distance /
  frontRangeMeters` → `distance / frontRangeMeters`; build; name the failing cases ("a point
  touching the enemy is worth double its income" → 10, and "of two points paying the same, the one
  nearer the enemy is filled first" → inverted); restore; build; SHA256 identical. Notes.

- [x] **T7 — Forward-base missions: siting, hold posts, the truck, platoon assignment and the
  strength predicates; with self-check and fail-proof.** In `Operations/CommanderOperationsFront.cs`:
  - `private void Review()` (replacing T2's stub): per managed HQ — `SweepPool`, `RankPoints`,
    `PlanForwardBases`, `PlanPickets` (T8), `PlanOffensive` (T12-T15), `SumOrderBook` (T9),
    `AssignPlatoons`. Ordered so forward bases are filled before pickets and both before
    offensives (design §2: "Filled after FOBs, before offensives").
  - Pure: `internal static bool ShouldWithdraw(int strength, int establishment)` →
    `strength * WithdrawStrengthDenominator < establishment * WithdrawStrengthNumerator` (design
    §1, under 50%), and `internal static bool AttackHasFailed(int strength, int establishment)` →
    the same shape at 2/5 (design §3, under 40%). Integer maths so the boundaries are exact. Moved
    here (not T14) because `AssignPlatoons`, below, needs `ShouldWithdraw` for every platoon on
    every review, not only attacking ones; T14 consumes both, it does not define them.
  - `PlanForwardBases`: one `ForwardBase` mission per **front** point the commander holds or can
    reach, in value order, `WantedPlatoons = HasThreatMark(...) ? 2 : 1` (design §2), capped by the
    forward-base share (T8). Mission priority within the list: nearest the enemy first, then points
    whose position is higher than the mean of the four compass samples around it — the
    `FindHighGround` sample shape (`Ai/CommanderEnemyCommanderGround.cs:141-158`), reused as
    `internal static bool OverlooksApproach(GlobalPosition point)`, so "hilltops overlooking an
    approach" is one definition and not a second height sampler — then the rest (design §2).
  - Hold posts (ledger row 19, a two-step shape, not a wholesale move): CUT the ring-post logic out
    of `Ai/CommanderEnemyCommanderGarrison.cs:328-353` (`EnsureGarrisonPosts`) into
    `internal List<GlobalPosition> EnsureHoldPosts(CommanderStrategicPoint point, int slots)` on
    this partial: `slots` evenly spaced on a ring at `point.Radius * HoldRingFraction` (0.6, the
    value it already uses), terrain-snapped, sea-level posts skipped, cached per point in a
    `Dictionary<CommanderStrategicPoint, List<GlobalPosition>>` on the service (a point does not
    move) and cleared in `ResetSession` for the reason the garrison's own clear gives
    (`Ai/CommanderEnemyCommanderService.cs:277-279`). `slots` is the platoon's member count, so a
    full platoon spreads and a half one does not stack. `EnsureHoldPosts` is `internal`, not
    `private`, because the old site still has to reach it: `EnsureGarrisonPosts`'s body is REPLACED
    with a single forwarding call, `CommanderOperationsService.Instance?.EnsureHoldPosts(point,
    CommanderSettings.PointsMinGarrison + 1) ?? new List<GlobalPosition>()`, plus a pointer comment
    saying the ring-post logic moved to the operations front partial and that this forwarder dies
    with the file in T11. This keeps `DriveGarrisonToPosts` (`Ai/CommanderEnemyCommanderGarrison.cs:300`)
    compiling until T11 deletes the whole file — the executor must confirm `dotnet build` succeeds
    at the end of T7.
  - A platoon whose mission is a forward base and which `HasArrived` goes `Holding`, and from then
    on the movement tick issues each member its own hold post rather than a formation slot (the
    `DriveGarrisonToPosts` loop, `…Garrison.cs:281-323`, including its stable
    sort-by-`GetInstanceID` so a member keeps the same post between reviews, and its
    `DefenceArrivedMeters` skip so a vehicle on station is not re-ordered every tick).
  - Truck: each forward-base mission posts one `Truck` requisition (T9) and parks the truck it gets
    on the ring at `point.Radius * TruckRingFraction` (0.3, planner-chosen: inside the platoon's
    ring, where it is covered). None available → the mission runs without one and logs
    `CommanderAiLog.Note(hq, $"{point.Label}: no munitions truck available; the forward base runs
    dry.")` **once per mission** (design §2: "logged once").
  - `private void AssignPlatoons(FactionHQ hq, OperationsState state)`, called once per review per
    managed HQ, over **every** platoon regardless of state:
    - applies design.md §1's losses rule to every platoon, not only attacking ones:
      `ShouldWithdraw(strength, establishment)` → state `Withdrawing`, objective the nearest
      friendly forward base or held base, replacements requisitioned; zero live members → the
      platoon is dissolved, its members (none) released and its mission reopened for the next
      review;
    - matches platoons to open missions in mission-priority order — forward bases before pickets
      before offensives, already `Review()`'s pipeline order — subject to `MaxForwardBases` (T8);
      platoons left over go to the `Reserve` mission (T11);
    - forms new platoons from the free pool via `TryFormPlatoon` (T4) and tops up under-strength
      ones via `ReinforcePlatoon` (T4) when the pool has spare vehicles.
  **Self-check (withdraw and fail thresholds)** in `CheckStrength(failures)`: `!ShouldWithdraw(3,
  6)` ("exactly half a platoon holds"); `ShouldWithdraw(2, 6)` ("a third of a platoon withdraws");
  `ShouldWithdraw(0, 6)` ("a dead platoon withdraws"); `!AttackHasFailed(4, 10)` ("exactly 40% of an
  attack is still an attack"); `AttackHasFailed(3, 10)` ("30% of an attack has failed");
  `!AttackHasFailed(6, 6)` ("a full-strength attack has not failed"); and the ladder Testing rule 1
  asks for: an attack that has failed is always also one that should withdraw, at every strength
  from 0 to 10 against establishment 10 ("the fail threshold is below the withdraw threshold").
  Build.
  **Fail-proof**: SHA256 of `Operations/CommanderOperationsFront.cs`; plant the fail
  numerator/denominator as 3/5 (60%); build; name the failing case ("the fail threshold is below
  the withdraw threshold": at strength 5 of 10 the attack now counts as failed while the platoon is
  not yet withdrawing); restore; build; SHA256 identical. Notes.
  Additional verification, since the mission-planning bullets above are not pure: the build already
  covers them, plus a grep for `EnsureGarrisonPosts` (expect the forwarder definition and its one
  caller, `DriveGarrisonToPosts` — the zero-hit grep is T11's and T20's check, not this one) and
  `EnsureHoldPosts` (expect the definition and its two callers, the forward base and the picket).

- [x] **T8 — Picket missions and the forward-base share cap.** In
  `Operations/CommanderOperationsFront.cs`:
  - Pure: `internal static int MaxForwardBases(int platoonCount, float fobShare)` →
    `Mathf.Max(1, Mathf.FloorToInt(platoonCount * Mathf.Clamp01(fobShare)))` — at least one, so a
    commander with one platoon still holds its best point; design §2's "at most `FobShare` of a
    commander's platoons sit in FOBs".
  - `PlanPickets`: every **rear** control point the commander holds wants exactly
    `CommanderSettings.PointsMinGarrison` (2) of the cheapest free pool vehicles by
    `definition.value` (design §2: "so they keep paying"), as a `Picket` mission with
    `WantedPlatoons = 0` — a picket is a detachment from the pool, not a platoon, so it never
    competes for the forward-base share. Front points beyond the cap get a picket instead of a
    forward base (design §2: "Surplus front points get pickets").
  - Promotion and demotion: a picket point that gains a threat mark promotes to a forward-base
    mission at the next review; a forward base whose point turns rear thins to a picket, releasing
    the surplus members back to the pool (design §2).
  **Self-check (forward-base share)** in `CheckForwardBaseShare(failures)`: `MaxForwardBases(6,
  0.5f) == 3` ("half of six platoons may sit in forward bases"); `MaxForwardBases(1, 0.5f) == 1`
  ("a commander with one platoon still holds its best point"); `MaxForwardBases(6, 0f) == 1` ("the
  floor holds even at a zero share"); `MaxForwardBases(6, 1f) == 6` ("a full share lets every
  platoon hold"); `MaxForwardBases(7, 0.5f) == 3` ("the share rounds down, so the reserve is never
  short"). Build.
  **Fail-proof**: SHA256 of `Operations/CommanderOperationsFront.cs`; plant `FloorToInt` →
  `CeilToInt`; build; name the failing case ("the share rounds down, so the reserve is never
  short": 7 × 0.5 now gives 4); restore; build; SHA256 identical. Notes.

- [x] **T9 — The order book, requisitions and the depot claim, with self-check and fail-proof.** In
  `Operations/CommanderOperationsRequisitions.cs`:
  - `internal struct CommanderRequisition { CommanderPlatoonRole Role; int Wanted; int Filled;
    float OpenedAt; CommanderOperationsMission? Mission; }` with `<summary>`s.
  - Pure: `internal static void SumOrderBook(IReadOnlyList<CommanderRequisition> requisitions,
    int[] totalsByRole)` — zeroes `totalsByRole` then adds `Mathf.Max(0, Wanted - Filled)` per
    entry into `totalsByRole[(int)Role]`. Pure: `internal static int LargestOpenRole(IReadOnlyList<int>
    totalsByRole)` → the index of the largest entry, or `-1` when every entry is zero; the **first**
    wins a tie so the answer is stable across reviews (the `PickRichestIndex` convention,
    `Ai/CommanderPlayerCommanderService.cs:71-92`).
  - `internal static bool HasOpenAttackRequisition(FactionHQ hq)` and
    `internal static CommanderPlatoonRole LargestOpenRole(FactionHQ hq)` — the live wrappers the
    buyer calls (ledger rows 13-15).
  - Missions post requisitions when short: a forward base short of members posts one per empty
    recipe slot; an attack posts `WantedPlatoons × PlatoonSize` across the recipe; each forward base
    posts one `Truck`.
  - The claim assigns each newly registered vehicle to the **oldest** matching open requisition
    (design §4), increments `Filled`, and notifies the mission when the line closes
    (`CommanderAiLog.Note(hq, $"{mission.Label}: requisition filled.")`).
  - A requisition open longer than `RequisitionTimeoutSeconds` (300) is closed: the mission proceeds
    with what it has if it has anything, and dissolves if it has nothing, logged either way
    (design §4).
  - `Core/CommanderGameAccess.cs`: ledger row 6 (`IsMunitionsTruckDefinition`, departure 3).
  - `Units/CommanderMoveService.cs`: ledger row 31 (new). `TryReturnToBasegameLogistics`
    (1413-1451) hands a truck **back to the game's own rearm AI** — on a truck below half capacity
    it drives the truck away via `DriveToRestock`, which is exactly the failure a truck parked at a
    forward base must not suffer, so it cannot be called as-is (Reuse rule 5: parameterise, never
    fork). EXTRACT its shared body — the component guard, the `RearmMissionController` unassign
    loop, `rearmAi.AssignMission(null!)`, the `needsRestock` computation and the reflection call
    into `RearmVehicleAI.DriveToRestock`/`Wait` — into `internal static bool
    TryDetachFromRearmLogistics(Unit unit, bool allowRestock)` on `CommanderMoveService`.
    `allowRestock: false` forces the `Wait` branch however low the truck's capacity is, and
    additionally sets `rearmer.AvailableForMission = false` so the rearm controller cannot hand it
    a new mission while it is parked at a forward base. `TryReturnToBasegameLogistics` becomes a
    one-line call with `allowRestock: true` — behaviour-neutral for its existing caller; state this
    in the notes. Give the new method a `<summary>` naming both callers (the basegame return path
    and the operations claim) and why the flag exists. A claimed munitions truck is detached with
    `CommanderMoveService.TryDetachFromRearmLogistics(unit, allowRestock: false)` — **not**
    `TryReturnToBasegameLogistics`, which would hand it back to the game's rearm AI and, below half
    capacity, drive it off the forward base.
  **Self-check (order-book summing)** in `CheckOrderBook(failures)` over five synthetic
  requisitions — armour 3 of which 1 filled, air defence 2 of which 2 filled, carrier 1 of which 0,
  armour 2 of which 0, truck 1 of which 0: `totals[Armour] == 4` ("two armour lines sum into one
  order"); `totals[AirDefence] == 0` ("a filled line is off the book"); `totals[Carrier] == 1` and
  `totals[Truck] == 1` ("every role keeps its own line"); `LargestOpenRole(totals) == (int)Armour`
  ("the buyer fills the largest open line"); an over-filled line (wanted 1, filled 3) contributes
  `0`, not `-2` ("an over-filled line never subtracts from another role's order"); and
  `LargestOpenRole` of an all-zero book is `-1` ("an empty book leaves the plan-based counter
  triangle alone"). Build.
  **Fail-proof**: SHA256 of `Operations/CommanderOperationsRequisitions.cs`; plant
  `Mathf.Max(0, Wanted - Filled)` → `Wanted - Filled`; build; name the failing case ("an over-filled
  line never subtracts from another role's order": armour totals 4 becomes 2 once the over-filled
  entry contributes −2); restore; build; SHA256 identical. Notes.

- [x] **T10 — The buyer consults the order book.** `Ai/CommanderEnemyCommanderService.cs`: ledger
  rows 13-15, plus `OffensivePurchasesPerReview = 5` beside the duel constants (56) with a
  `<summary>`, plus `private VehicleDefinition? ChooseForRole(float budget, CommanderPlatoonRole
  role)` beside `ChooseCaptureUnit` (586-605) — cheapest catalogue entry whose
  `CommanderPlatoonRoles.Of(definition)` matches and whose `value <= budget`, preferring
  `captureStrength > 0f` within `Carrier` and `IsMunitionsTruckDefinition` within `Truck`. Update
  the `Review` summary (289) with one sentence: with an order book open the commander buys what its
  missions asked for; with it empty it buys the counter triangle exactly as before. The purchase
  log line keeps its wording and gains the role in the detail slot: `CommanderAiLog.Note(hq,
  $"bought … for {cost:0}.", role == null ? GetPlanLabel(buyPlan) : $"{GetPlanLabel(buyPlan)}, for
  {roleLabel}")`. Build. Grep `SpendFraction` and `PurchasesPerReview` — expect the constants plus
  exactly one read each, inside `Review`.

- [x] **T11 — Home guard becomes the reserve platoon; the garrison step is deleted.** Ledger rows
  11, 12, 16, 17, 19, 22, 23, 24. Delete `Ai/CommanderEnemyCommanderGarrison.cs`. Its only surviving
  body at this point is the T7 forwarder (`EnsureGarrisonPosts`, now a single call into
  `EnsureHoldPosts`) and its pointer comment; both go with the file, not before it — confirm
  nothing else in the file is referenced before deleting. In
  `Operations/CommanderOperationsFront.cs`, `PlanForwardBases` also posts one `Reserve` mission per
  managed HQ, objective = the base ring posts `EnsureDefencePosts` builds
  (`Ai/CommanderEnemyCommanderDefence.cs:250-295`) — **not** a second ring builder: call it, since
  it is `private static` on the same class and the operations service cannot; instead the reserve
  mission's objective is the HQ's `GetTerritoryCenter` (`Ai/CommanderCaptureService.cs:490-504`)
  and its hold posts come from `EnsureHoldPosts` with a radius of
  `ReserveRingMeters` (900, planner-chosen — the midpoint of the guard's own
  `DefenceRingMinMeters`/`DefenceRingMaxMeters` clamp at `Defence.cs:48-49`, so the reserve stands
  roughly where the guard used to). The reserve takes whatever platoons the forward-base share
  leaves. Build. Greps to paste: `IsGarrisonUnit` (expect none), `ReviewGarrisons` (none),
  `SelectGarrisonTargets` (none), `CommanderEnemyCommanderGarrison` (none), `state.Garrison`
  (none), `CheckGarrisonTargets` (none).

- [x] **T12 — Offensive targets and sizing, with self-check and fail-proof.** Create
  `Operations/CommanderOperationsOffensive.cs` (partial).
  - Pure: `internal static int PlatoonsForTarget(int observedEnemyUnits, int platoonSize, bool
    targetIsBase)` → `Mathf.Clamp(Mathf.CeilToInt(SizingMultiplier * observedEnemyUnits /
    Mathf.Max(1, platoonSize)), targetIsBase ? MinPlatoonsForBase : MinPlatoonsForPoint,
    MaxPlatoonsPerAttack)`.
  - `private int CountObserved(FactionHQ hq, GlobalPosition target)` — tracked hostile **ground**
    units within `ObservedRadiusMeters` (8000) seen within `ThreatMemorySeconds` (45), the
    `IsUnderThreat` database walk again (`Ai/CommanderEnemyCommanderDefence.cs:197-228`) with its
    building skip. Design §3 is explicit that this is the commander's own tracking database and not
    the true count.
  - `PlanOffensive` target ranking (design §3): enemy-held control points adjacent to my front
    (nearest first, by `PointValue`), then enemy bases whose observed defence is beatable —
    `PlatoonsForTarget(observed, size, targetIsBase: true) <= spare platoons + platoons forming`.
    Trigger: the forward bases that would serve as this target's axes exist and are `Holding`, and
    at least one spare platoon exists or is forming (design §3 — **not** "every forward base
    manned").
  **Self-check (sizing formula bounds)** in `CheckSizing(failures)`, `platoonSize = 6`:
  `PlatoonsForTarget(0, 6, false) == 1` ("an unobserved point still gets one platoon");
  `PlatoonsForTarget(0, 6, true) == 2` ("an unobserved base still gets two");
  `PlatoonsForTarget(4, 6, false) == 1` ("1.5 × 4 = 6 is one platoon exactly");
  `PlatoonsForTarget(5, 6, false) == 2` ("1.5 × 5 = 7.5 rounds up to two platoons");
  `PlatoonsForTarget(100, 6, true) == 6` ("the ceiling holds against an enormous observed force");
  `PlatoonsForTarget(12, 1, false) == 6` ("the ceiling holds after a platoon-size retune"). Build.
  **Fail-proof**: SHA256 of `Operations/CommanderOperationsOffensive.cs`; plant `CeilToInt` →
  `FloorToInt`; build; name the failing case ("1.5 × 5 = 7.5 rounds up to two platoons": 7.5 / 6 =
  1.25 now floors to 1); restore; build; SHA256 identical. Notes.

- [x] **T13 — Axes and release points, with self-check and fail-proof.** In
  `Operations/CommanderOperationsOffensive.cs`:
  - Pure: `internal static void SelectAxes(IReadOnlyList<float> distances, IReadOnlyList<float>
    bearings, float maxDistanceMeters, float distanceTolerance, float minBearingSeparationDegrees,
    int maxAxes, List<int> result)` — `result` cleared first. Candidates past `maxDistanceMeters`
    are dropped. Then every remaining candidate is tried as the seed; for a seed, candidates are
    walked in ascending distance and accepted when `Mathf.Abs(distance - seedDistance) <=
    distanceTolerance * seedDistance` and the **circular** bearing difference to every already
    accepted axis is `>= minBearingSeparationDegrees`; accepting stops at `maxAxes`. The seed
    yielding the most axes wins, ties going to the lowest seed index so the answer is stable across
    reviews. A lone candidate therefore comes back as one entry, and nothing in reach as none.
  - `private bool TryPlanAxes(...)`: candidates are this commander's `Holding` forward bases and
    held bases within `AxisCandidateRadiusMeters` (25 km) of the target, bearing measured from the
    target. Two or three axes → those. Exactly one → a second group forms at a flank point
    `Random.Range(FlankMinMeters, FlankMaxMeters)` perpendicular to the direct line at its midpoint
    (design §3), terrain-validated by departure 5's rule and mirrored to the other side if the
    first side fails. None → the mission waits (logged once).
  - `private GlobalPosition ReleasePointFor(GlobalPosition formUp, GlobalPosition target)` — the
    ideal point is `ReleaseDistanceMeters` (5 km) short of the target along the form-up → target
    line; the release point is the nearest `Crossroads` or `Roadside` point within
    `ReleaseSnapMeters` (3000) of it (departure 4), else the ideal point snapped to terrain and
    validated by departure 5's rule, else the first acceptable point on a ring around it — the
    `EnsureDefencePosts` probe shape (`Ai/CommanderEnemyCommanderDefence.cs:280-294`).
  **Self-check (axis selection)** in `CheckAxes(failures)`, at 25 km / 20% / 60° / 3 axes:
  - two good axes — distances `[10000, 11000]`, bearings `[0, 90]` → `[0, 1]` ("two candidates at
    similar distance on different bearings are both axes");
  - bearing too close — distances `[10000, 10500]`, bearings `[0, 30]` → `[0]` ("a second candidate
    30° round is the same attack, not a second axis");
  - bearing exactly at the minimum — distances `[10000, 10000]`, bearings `[0, 60]` → `[0, 1]`
    ("60° apart is far enough apart");
  - distance out of tolerance — distances `[10000, 13000]`, bearings `[0, 120]` → `[0]` ("a
    candidate 30% further away would arrive long after the other");
  - distance exactly at the tolerance — distances `[10000, 12000]`, bearings `[0, 120]` → `[0, 1]`
    ("exactly 20% further still counts");
  - the seed matters — distances `[9000, 20000, 21000, 22000]`, bearings `[0, 0, 120, 240]` →
    `[1, 2, 3]` ("the best seed is chosen, not the nearest candidate");
  - the cap — distances `[10000, 10000, 10000, 10000]`, bearings `[0, 90, 180, 270]` → 3 entries
    ("no more than three axes, however many candidates qualify");
  - out of reach — distances `[26000, 30000]` → empty ("nothing within the axis radius is an
    axis, so the attack waits");
  - the wrap — distances `[10000, 10000]`, bearings `[350, 10]` → `[0]` ("bearings are compared the
    short way round: 350° and 10° are 20° apart, not 340°").
  Build.
  **Fail-proof**: SHA256 of `Operations/CommanderOperationsOffensive.cs`; plant the circular bearing
  difference as a plain `Mathf.Abs(a - b)`; build; name the failing case ("bearings are compared the
  short way round": 350 and 10 now read as 340° apart and are wrongly accepted as two axes);
  restore; build; SHA256 identical. Notes.

- [x] **T14 — The assault: form up, wait, go in, flip in passing, withdraw or fail.** In
  `Operations/CommanderOperationsOffensive.cs`:
  - `ShouldWithdraw(int strength, int establishment)` and `AttackHasFailed(int strength, int
    establishment)` are defined in T7 (`Operations/CommanderOperationsFront.cs`, `CheckStrength`
    self-check and fail-proof) because `AssignPlatoons` needs `ShouldWithdraw` for every platoon on
    every review, not only attacking ones. T14 only consumes both here for the outcome bullet
    below; it does not redefine them.
  - Each group routes to its release point and sets `Arrived` when `HasArrived`. The mission goes in
    when every group has arrived, or when `AssaultFormUpTimeoutSeconds` (240) has passed since the
    first arrival (design §3: "timeout 4 min, then go"), logged either way.
  - Going in: every group's platoons get the target's hold point as their objective —
    `CommanderCaptureService.GetHoldPoint` for a base (it is `private static`, so the executor adds
    `internal static GlobalPosition GetHoldPointFor(Airbase airbase) => GetHoldPoint(airbase);`
    beside it rather than copying the probe; note the row in the executor notes as a ledger
    addition) and `point.Position` for a control point.
  - En-route flip: a control point within `EnRouteFlipMeters` (2000) of the straight line from a
    group's current position to its next destination and not already held becomes that group's
    destination for one movement tick — design §3's "points within 2 km of the route are flipped in
    passing". The segment test is the `SegmentDistanceSquared` shape
    (`Points/CommanderStrategicPointDiscovery.cs:896-912`); that one is `private static` in its
    class, so this is a **third** identical definition — instead, make that one `internal static`
    and call it, leaving a pointer comment (Reuse rule 4; its own comment at 898-901 already flags
    the duplication as deliberate-but-regretted).
  - Outcome: target taken → the attacking platoons become forward bases at the new front (design
    §3) and the mission closes; `AttackHasFailed` → every group withdraws to its release point, the
    mission closes with the observed count written back onto the target so the next sizing is
    larger (design §3); `ShouldWithdraw` on a single platoon → `Withdrawing` to the nearest friendly
    forward base or held base, replacements requisitioned (design §1); zero members → dissolved and
    its mission reopened.
  Build. (`ShouldWithdraw`/`AttackHasFailed`'s self-check, `CheckStrength`, and its fail-proof live
  in T7, where the two predicates are defined; T14 only consumes them.)

- [x] **T15 — The pressure clock, with self-check and fail-proof.** In
  `Operations/CommanderOperationsOffensive.cs`:
  - Pure: `internal static float StepPressure(float pressure, float deltaMinutes, int
    pointsLostSinceLastReview, int enemyPointsCloserToMyBase)` → `pressure + deltaMinutes *
    PressurePerMinute + pointsLostSinceLastReview * PressurePerPointLost +
    enemyPointsCloserToMyBase * PressurePerEncroachingPoint`, and `internal static bool
    PressureForcesAttack(float pressure, float pressureIntervalMinutes)` → `pressure >=
    pressureIntervalMinutes * PressurePerMinute`.
  - Driven once per review with `deltaMinutes = ReviewIntervalSeconds / 60f`. `pointsLost` is the
    drop in the commander's held-control-point count since the last review (`LastPointsHeld` on the
    state); `enemyPointsCloserToMyBase` counts enemy-held control points whose distance to my
    territory centre is less than their distance to the nearest base the enemy holds — design §3's
    "enemy point closer to my base than theirs".
  - When `PressureForcesAttack`, the commander launches its best available target with **at least
    two platoons regardless of ideal sizing** and resets `Pressure` to zero on launch (design §3),
    logging `CommanderAiLog.Note(hq, $"is out of patience: attacks {label} with {n} platoon(s).")`.
  **Self-check (pressure clock)** in `CheckPressure(failures)`, at the default 12 min:
  `StepPressure(0f, 0.5f, 0, 0) == 0.5f` ("one 30 s review adds half a minute of pressure");
  twenty-four such steps reach `12f` and `PressureForcesAttack(12f, 12f)` is true ("twelve quiet
  minutes force an attack" — the boundary itself fires); `!PressureForcesAttack(11.5f, 12f)`
  ("eleven and a half minutes do not"); `StepPressure(0f, 0.5f, 1, 0) == 2.5f` ("a point lost is
  worth two minutes"); `StepPressure(0f, 0.5f, 0, 3) == 3.5f` ("three enemy points on my side of
  the map are worth three minutes"); and the ladder guard: with the live settings,
  `PressureIntervalMinutes * PressurePerMinute > 0` read into locals first ("the pressure clock can
  still reach its threshold; check the Operations section of the config"). Build.
  **Fail-proof**: SHA256 of `Operations/CommanderOperationsOffensive.cs`; plant `pressure >=
  pressureIntervalMinutes * PressurePerMinute` → `>`; build; name the failing case ("twelve quiet
  minutes force an attack": 12 is no longer `> 12`, so the clock overshoots by a whole review);
  restore; build; SHA256 identical. Notes.

- [x] **T16 — Markers (ledger row 27).** Create `Operations/CommanderOperationsMarkers.cs`
  (partial). `internal void DrawMarkers(Camera camera)`, copying
  `Points/CommanderStrategicPointMarkers.cs:22-71` member for member where it applies: the
  `fullscreenMap` / `anyMap` split, `CommanderUiScale.ScreenToGui`, the same two sizes. Draws:
  - a **platoon marker** at the leader — a dot in the owner's colour (`GetFriendlyColor` /
    `GetHostileColor`, `Core/CommanderGameAccess.cs:223-243`) with the label
    `{Name} {members}/{Establishment} {STATE}` (design §5's `2ND PLATOON 5/6 HOLDING`), through
    `CommanderUiTheme.DrawWorldLabel`. An enemy platoon is drawn only where its leader is tracked
    by the local HQ — the `CommanderGameAccess` 8 s tracking test at `…GameAccess.cs:129-130`;
  - a **FOB tag** appended to a forward base's point label (the point markers already draw the
    label, so the operations service exposes `internal bool IsForwardBase(CommanderStrategicPoint)`
    and `Points/CommanderStrategicPointMarkers.cs:87-91` appends `"  FOB"` — one extra ledger row
    the executor records in the notes);
  - **release points** as a small cross while their attack is live, in the attacking faction's
    colour, for the local faction only (the player is not shown where the enemy will stop).
  All shapes are axis-aligned `CommanderUiTheme.Bar` calls: rotated bars do not survive the
  UI-scale matrix (`Points/CommanderStrategicPointMarkers.cs:203-209`). Build.

- [x] **T17 — COMMANDER LOG OPERATIONS block (ledger row 25).** In
  `Operations/CommanderOperationsService.cs`: `internal void DescribeOperations(FactionHQ hq,
  List<string> lines)` — one summary line (`PRESSURE`, `PLATOONS`, `FOBS`, `ORDERS`) and one line
  per live mission in design §5's wording: `ATTACK {label}: {n} platoons, axes {a} + {b}, forming
  up {arrived}/{n}`, `FOB {point}: {n}/{wanted} platoons{, no truck}`, `PICKET {point}:
  {n}/{MinGarrison}`. ASCII only — the IMGUI font guarantees nothing else
  (`conductor/tracks/strategic-points_20260913/plan.md` T11 says so). Then
  `UI/CommanderAiLogUi.cs` `DrawHeader` per the ledger row, at most `MissionLinesInHeader = 3`
  lines with a `+{n} more` tail when there are more. Build.

- [x] **T18 — OPERATIONS sliders on the POINTS tab (ledger row 26, departure 7).**
  `UI/CommanderOverlayUiSettings.cs`. Arithmetic in a comment, both help states: the existing
  414 px points box already includes its own header and footnote (`DrawPointsSettings`'s own
  doc-comment: `10 + 32 + 9*38 + 30 = 414`); by that same formula a five-row OPERATIONS box is
  `10 + 32 + 5*38 + 30 = 262` px. Content is therefore the 414 px points box + an 8 px gap + the
  262 px OPERATIONS box = 684 px; the view is `settingsWindowRect.height - y - 16f`, i.e. 692 px
  with help closed (`y = 82`) and 612 px with it open (`y = 162`), so the scrollbar still appears
  only with the help overlay up. Build.

- [x] **T19 — CHANGELOG, README, design departures (ledger rows 28-30).** `CHANGELOG.md`: a new
  bullet at the top of the `### Added` list at 228, in the file's voice (what the player sees
  first): the AI now forms named platoons instead of trickling vehicles, holds the points nearest
  you with a forward base and a munitions truck, pickets the quiet ones behind, and attacks on two
  or three axes sized to what it can actually see; forward bases, platoons and release points are
  on the map; the COMMANDER LOG has an OPERATIONS block; five new sliders on the POINTS tab. State
  the defaults (platoon of 6 as 3 armour, 1 carrier, 2 air defence; 15 km front; half the platoons
  in forward bases; an attack at least every 12 minutes) and that nothing is saved across a reload.
  `README.md`: `## Platoon operations` between `## Strategic points` and `## Unit systems` (line
  703) — four short subsections: *What a platoon is* (recipe, size, states, names), *The front line*
  (front vs rear, forward bases, the truck, pickets, the share cap), *Offensives* (targets, sizing
  off what the AI can see, two or three axes, release points, the 12-minute pressure clock), and
  *What you can watch* (markers, the OPERATIONS block, the sliders, and that it is host-only).
  `design.md`: the `## Departures recorded during planning` section, eight one-line entries.
  Verification: grep `CHANGELOG.md` for the new bullet under `### Added`, grep `README.md` for
  `## Platoon operations`, grep `design.md` for `## Departures recorded during planning`; no
  `dotnet build` needed — none of the three is compiled.

- [x] **T20 — Final build, greps, notes.** `dotnet build GroundControlRts.csproj -c Release`; paste
  the last 5 lines. Greps to paste: `IsGarrisonUnit` / `ReviewGarrisons` / `SelectGarrisonTargets` /
  `CheckGarrisonTargets` / `CommanderEnemyCommanderGarrison` / `EnsureGarrisonPosts` (all none —
  the T7 forwarder is gone with the file); `state.Garrison` (none);
  `IsPlatoonUnit` (definition + exactly two callers — the home-guard recruiter and the capture
  squad); `OwnsGroundForce` (definition + exactly two callers — `ReviewDefence` and `ReviewEnemy`);
  `IssuePlatoonMove` (definition + exactly one caller, the movement tick); `HeadingDegrees`
  (definition + exactly two callers); `ReissueToleranceMeters` (definition + exactly two callers);
  `40f` inside `Units/CommanderMoveService.cs` (expect no hit inside `Issue`);
  `IsMunitionsTruckDefinition` (definition + the requisition and the buyer);
  `TryDetachFromRearmLogistics` (one definition, exactly two callers —
  `TryReturnToBasegameLogistics` and the operations claim); `AssignPlatoons` (definition + exactly
  one caller, `Review`); `SegmentDistanceSquared`
  (one definition, two callers). Tick every task, fill every notes section, and list every deviation
  from this plan with its reason.

## DAG (what can run in parallel)

```
T1 ──► T2 ──┬─► T3 ──► T4 ──┐
            │               ├─► T7 ──┬─► T8 ──► T9 ──► T10
            ├─► T5 ─────────┤        ├─► T11
            └─► T6 ─────────┘        ├─► T16
                │                    └─► T17 ◄── T15
                └─► T12 ──► T13 ──► T14 (also needs T5, T7)
                     └────► T15
T18 after T1
T19 after T10, T11, T14, T16, T17, T18
T20 last
```

Parallel groups for a dispatcher: {T1} → {T2, T18} → {T3, T5, T6} → {T4, T12} → {T7, T13} →
{T8, T11, T15, T16} → {T9, T14} → {T10, T17} → {T19} → {T20}. One executor running in order is the
expected mode (`.claude/CLAUDE.md` Track shape); the DAG only says what must not be reordered. Note
that T14 depends on T13, T5 and T7 (T7 defines `ShouldWithdraw`/`AttackHasFailed`, which T14 only
consumes) — already satisfied by group order, since T7 finishes in group 5 and T14 sits in group 7
— and T17 on both T9 and T15.

## In-game acceptance (USER TO VERIFY IN GAME — not the executor, not the evaluator)

From design.md Verification, verbatim in substance, as host in `Ground Control Duel` and one stock
Escalation map, after `.\build-and-install.ps1` with the game closed (or `build-dev.bat` for hot
reload). With the BepInEx console on, the mod's lines appear live.

- [ ] Self-checks: no `self-check FAILED` at load, and `Operations self-check passed` at debug
      level alongside the strategic-points one.
- [ ] **Forward bases form at front points with a truck, inside 10 minutes.** The console shows
      `Enemy commander (<faction>) forms 1ST PLATOON: n/6 vehicles.` then `… holds <POINT>`; the
      point's marker gains a `FOB` tag and a platoon marker appears beside it reading
      `1ST PLATOON 6/6 HOLDING`; a munitions truck is parked inside the ring (or the single
      `no munitions truck available` line explains why not).
- [ ] **The first attack forms on two axes.** The COMMANDER LOG's OPERATIONS block shows
      `ATTACK <target>: 2 platoons, axes <A> + <B>, forming up 0/2`, then `1/2`, then the groups go
      in together. The two release crosses are on different sides of the target, not next to each
      other.
- [ ] **Groups wait at the release points, then go in together.** Watch both platoons stop about
      5 km short; neither assaults alone unless four minutes pass first, in which case the log says
      so.
- [ ] **A failed attack's successor asks for more.** Let one attack be beaten off; the log shows
      the withdrawal, and the next attack on the same target is sized larger than the first.
- [ ] **The enemy no longer arrives as one convoy.** Nothing drives at you down a single road in
      ones and twos; what arrives arrives as a named platoon in a wedge.
- [ ] **Pickets hold rear points and they keep paying.** Two vehicles sit on each rear village or
      crossroads the AI holds, the point stays its colour, and the COMMANDER LOG header's income
      line does not drop.
- [ ] **The player's own AI commander does the same.** Turn the player commander switch on: the YOU
      tab fills with `Player commander (<faction>) forms …` lines and your own faction's platoons
      appear on the map.
- [ ] **Your own orders still win.** Order one of your AI commander's platoon vehicles somewhere by
      hand; it goes, it is dropped from the platoon, and it is not re-recruited until the hands-off
      window expires.
- [ ] `BepInEx\LogOutput.log` still contains every line the window shows, worded exactly as it is
      in the window with the `Enemy commander (<faction>)` / `Player commander (<faction>)` prefix.

Planner notes for the user, not in the design: (a) departure 3 means the munitions truck is found
by what the prefab carries, so a faction whose rearm vehicle is built differently will get no truck
and one log line saying so — tell us if that happens; (b) departure 4 means a release point lands
on a discovered crossroads or roadside point when one is within 3 km, and on bare ground otherwise
— if attacks keep stopping somewhere silly, `Operations/ReleaseSnapMeters` is the knob; (c) five
numbers in the defaults table are planner-chosen because design.md did not give them
(`FormationCohesionMeters`, `FrontageWeight`, `PressurePerPointLost`,
`PressurePerEncroachingPoint`, `ReleaseSnapMeters`) — all are in the config file; (d) the home guard
is suspended, not deleted (departure 8), so a mission where point discovery never completes still
gets the old base ring; (e) a pure multiplayer client forms no platoons and sees no platoon markers
this track, host-only per design.

## Executor notes

*(The executor fills every section below as it goes: one per task with its build result and its
fail-proof record where the task has one, then the final build and greps from T20, then a list of
every deviation from this plan with its reason.)*

### T1 — Operations settings

Added an `Operations` section to `Core/CommanderSettings.cs` right after `PointsMineSnapMeters`
(line 212): `OperationsPlatoonSize` (int, 6), `OperationsRecipeArmour` (int, 3),
`OperationsRecipeCarrier` (int, 1), `OperationsRecipeAirDefence` (int, 2),
`OperationsFrontRangeMeters` (float, 15000), `OperationsFobShare` (float, 0.5),
`OperationsPressureIntervalMinutes` (float, 12), `OperationsOffensiveSpendFraction` (float, 0.5),
each with a comment naming what it tunes and its home (config file for the recipe, POINTS tab for
the rest). Added the eight `_ = Operations…;` warm-up touches after `_ = PointsMineSnapMeters;` and
before `_ = AirCommandMode;`.

Build: **0 warnings, 0 errors.**

### T2 — Data types, service skeleton, registration, self-check harness

Created `Operations/CommanderPlatoon.cs`: `CommanderPlatoonRole` enum (Armour/Carrier/AirDefence/
Truck/Other) with `CommanderPlatoonRoles.Of(VehicleDefinition?)` — the one role mapping; truck
detection is deferred to T9 (see deviations) since `CommanderGameAccess.IsMunitionsTruckDefinition`
does not exist yet — until T9, `Of` falls through to `Other` for any truck, which is safe (no
recipe slot is `Other`). `CommanderPlatoonState` (Forming/Moving/Holding/Attacking/Withdrawing) and
`CommanderMissionKind` (ForwardBase/Picket/Attack/Reserve) enums, each value with a `<summary>` from
design SS1-SS3. `CommanderPlatoon`, `CommanderOperationsMission`, `CommanderAssaultGroup` sealed
classes with every field named in the plan, each with a `<summary>`.

Created `Operations/CommanderOperationsService.cs`: `internal sealed partial class
CommanderOperationsService : ICommanderTickPersistent, ICommanderResetSession`, `Instance`,
`states` dictionary, the two staggered clocks (`ReviewIntervalSeconds` = 30, `MovementIntervalSeconds`
= 5) with `<summary>`s, `OperationsState` (Pool/Platoons/Missions/Pressure/LastPointsHeld/
NextPlatoonNumber — `Requisitions` deferred to T9, see deviations), `TickPersistent`/`ResetSession`,
empty `Review()`/`TickMovement()` stubs marked with the task that fills them in, and the self-check
harness (`SelfCheck`, `Expect` × 3, `ExpectSequence`) copied from
`Points/CommanderStrategicPointService.cs` per the plan, with a comment explaining why it is a
second harness and not a shared one.

Registered in `Core/CommanderModeController.cs` right after the point service and before
`CommanderFactionVehicleService` (ledger row 3), and `CommanderOperationsService.SelfCheck();` added
to `Core/CommanderPlugin.cs` right after the point service's self-check (ledger row 4).

Compile note: every data-holder field needed an explicit initializer (`= default`, `= null`,
`= 0f`, etc.) even where the plan's prose just names a field — with nothing yet assigning them
(the assigning logic arrives in T4/T5/T7), the compiler's CS0649 ("field is never assigned")
fires on internal fields with no initializer, which the 0-warnings rule forbids. This is a
mechanical C# idiom, not a design change.

Build: **0 warnings, 0 errors.**

### T3 — The pool: claim, sweep, release, IsPlatoonUnit, OwnsGroundForce

Created `Operations/CommanderOperationsRequisitions.cs`: `NotifyFactionUnitRegistered` (one-line
forwarder to `TryClaim`), `TryClaim` (claims into `Pool` only when a state already exists for the
HQ, `hq.IsServer`, `GroundVehicle`, not disabled, not `RDR`, not under a player order), `SweepPool`
(ground-truth walk of `hq.factionUnits` plus the `PruneDefenders`-shaped stale-drop over both `Pool`
and every platoon's `Members`), `IsPlatoonUnit` (checks `Pool` and platoon membership across every
managed HQ), `OwnsGroundForce` (`states.ContainsKey(hq)`), and the per-HQ state lifecycle
(`EnsureStates` — gated on `CommanderStrategicPointService.Instance?.Points` being non-empty, per
the plan, so a state does not exist before discovery finishes — and `PruneStates`, the
`Ai/CommanderEnemyCommanderService.cs:626-644` shape). `Review()` in
`Operations/CommanderOperationsService.cs` now calls `EnsureStates`, `SweepPool` per managed HQ and
`PruneStates`; T7 adds the mission-planning calls to the same loop.

Ledger row 5: `AirCommand/CommanderAirCommandPatches.cs`'s `RegisterFactionUnitPostfix` gained a
second line, `CommanderOperationsService.NotifyFactionUnitRegistered(__instance, unit);`, beside the
existing `CommanderAirCommandService` call (a third postfix on the same method, following
`Supply/CommanderSupplyHeliPatches.cs`'s precedent).

Ledger row 18: `Ai/CommanderEnemyCommanderDefence.cs`'s `RecruitDefenders` filter now excludes
`CommanderOperationsService.IsPlatoonUnit(unit)` instead of the deleted-file's `IsGarrisonUnit`,
comment reworded to name the platoon pool.

Ledger row 20: `Ai/CommanderCaptureService.cs`'s `CollectCaptureSquad` filter now excludes
`CommanderOperationsService.IsPlatoonUnit(unit)` instead of `IsGarrisonUnit`; the preceding comment
gained a clause for the platoon pool.

Ledger row 21: `Ai/CommanderCaptureService.cs`'s `ReviewEnemy` now returns immediately (clearing
`drive.Target` and `drive.WantsCaptureUnit`) when `CommanderOperationsService.OwnsGroundForce(hq)` —
departure 6, gating the expansion drive off rather than deleting it, since the same service also
serves the player's own capture clicks.

Build: **0 warnings, 0 errors.**

Greps:
- `IsGarrisonUnit` — one hit, the definition in `Ai/CommanderEnemyCommanderGarrison.cs:47` (its
  callers were replaced this task; the definition itself dies with the file in T11, as expected).
- `IsPlatoonUnit` — three hits: the definition (`Operations/CommanderOperationsRequisitions.cs:117`)
  and exactly two callers (`Ai/CommanderEnemyCommanderDefence.cs:374`,
  `Ai/CommanderCaptureService.cs:706`).

### T4 — Recipe fill and platoon formation, with self-check and fail-proof

Added to `Operations/CommanderOperationsService.cs`: `PlatoonNames` (planner-chosen, "1ST".."12TH"),
`FillRecipe` (pure, two-pass as specified: preferred-then-plain per role in Armour/Carrier/AirDefence
order, then any non-Truck fallback), `BuildRecipeArrays`, `TryFormPlatoon(FactionHQ hq,
OperationsState state, int wantedSize)`, `ReinforcePlatoon(OperationsState state, CommanderPlatoon
platoon)`, `TakePicksFromPool` (removes highest pool index first so earlier indices stay valid),
and `CheckRecipe` wired into `SelfCheck()`.

Build: **0 warnings, 0 errors.**

**Fail-proof.** SHA256 of `Operations/CommanderOperationsService.cs` before: `79A916F5B348088239B8F36933E39C1B339448514C6427BE4180BBC9BD85A2ED`.
Planted defect: pass-2 fallback condition `roles[i] != CommanderPlatoonRole.Truck` changed to
`roles[i] != CommanderPlatoonRole.Other`. Build: **succeeded, 0 warnings, 0 errors** (a defect that
does not compile proves nothing). Reasoned failure (not observed at runtime — the game was not
run): with the condition inverted, `Truck != Other` is true, so pass 2 now happily fills combat
slots with munitions trucks. In `CheckRecipe`'s "trucks excluded" case (roles
`[Armour,Truck,Truck,Truck,Truck,Truck]`), pass 1 still picks index 0 (the one Armour), but pass 2
now also picks indices 1-5 (all Truck, since Truck ≠ Other), so `picks` becomes `[0,1,2,3,4,5]` —
6 entries, not 1 — and the named case **"a munitions truck never fills a combat slot"** fails
(`ExpectSequence` expected `[0]`, got 6 entries). Restored the original text. Build again:
**succeeded, 0 warnings, 0 errors**. SHA256 after restore: `79A916F5B348088239B8F36933E39C1B339448514C6427BE4180BBC9BD85A2ED`
— byte-identical to the pre-defect hash.

### T5 — Platoon movement seam and the arrival rule, with self-check and fail-proof

`Units/CommanderMoveService.cs` ledger rows 7-10, in order: `GroundFormationSpacingMeters` ->
`internal`; new `internal const float ReissueToleranceMeters = 40f;` beside it, with `Issue` now
reading it instead of the literal `40f`; `ResolveHeading`'s last three lines moved into new
`internal static float HeadingDegrees(Vector3 centre, Vector3 destination)`, `ResolveHeading` now
returns `HeadingDegrees(centre / count, destination)` with a pointer comment; new `internal static
void IssuePlatoonMove(IReadOnlyList<Unit> members, GlobalPosition destination,
CommanderFormationShape shape, Dictionary<Unit, GlobalPosition> issued)` right after `Issue`,
writing nothing into `orders`/`stoppedUnits`/`playerOrderedAt`.

`Operations/CommanderOperationsService.cs`: `HasArrived` (pure), `TickMovement` (replacing the T2
stub) issuing `IssuePlatoonMove` in `CommanderFormationShape.Wedge` for every non-`Holding` platoon
with members, re-picking `Leader` when null/disabled, and a `// T7` marker on the `Holding` branch
(hold posts arrive with T7). `CheckArrival` added and wired into `SelfCheck()`.

Build: **0 warnings, 0 errors.**

**Fail-proof.** SHA256 before: `514B8324AA792E9F17FE561032D9D1F6078A54896D95F170AA31F195A0AFEF66`.
Planted defect: `HasArrived`'s `membersInCohesion * 2 >= memberCount` changed to `> memberCount`.
Build: **succeeded, 0 warnings, 0 errors**. Reasoned failure (not run in-game): at
`membersInCohesion = 3, memberCount = 6`, `3 * 2 = 6`, and `6 > 6` is false, so
`HasArrived(100f, 400f, 3, 6)` now returns false. The named case **"exactly half the platoon
closed up counts as arrived"** fails (expected true, got false). Restored. Build again:
**succeeded, 0 warnings, 0 errors**. SHA256 after: `514B8324AA792E9F17FE561032D9D1F6078A54896D95F170AA31F195A0AFEF66`
— byte-identical.

### T6 — Front line and point value, with self-check and fail-proof

Created `Operations/CommanderOperationsFront.cs`: `FrontageWeight` (1.0, planner-chosen),
`ThreatMarkRingMultiplier` (4, planner-chosen), `CommanderRankedPoint` readonly struct,
`IsFrontPoint`/`PointValue` (pure), `NearestEnemyAssetDistance` (loops the full point list — a base
is a point too, `GetOwner()` resolves it via `Airbase.CurrentHQ`, so no second airbase loop is
needed), `HasThreatMark` (the `IsUnderThreat` walk retargeted to a point's ring), `RankPoints`
(fills and sorts `state.RankedPoints`, value descending then distance ascending), and `CheckFront`.

**Ledger addendum** (recorded per the plan's own precedent for T14/T16): `ThreatMemorySeconds` in
`Ai/CommanderEnemyCommanderDefence.cs` changed from `private const` to `internal const` so
`HasThreatMark` (and T12's `CountObserved`) can reuse the one definition instead of redeclaring it
(Reuse rule 4, as the design-facts table already says the number must be — the ledger's 31 rows do
not carry this line, so it is noted here explicitly).

Added `OperationsState.RankedPoints` (`List<CommanderRankedPoint>`) in
`Operations/CommanderOperationsService.cs`, and wired `CheckFront(failures);` into `SelfCheck()`.

Build: **0 warnings, 0 errors.**

**Fail-proof.** SHA256 before: `0BBB740CF2D83B6350A3A8AB32787DB74284DF10FD2CD70F5EF982FDCE40684D`.
Planted defect: `PointValue`'s `1f - distance / frontRangeMeters` changed to
`distance / frontRangeMeters`. Build: **succeeded, 0 warnings, 0 errors**. Reasoned failure (not
run in-game): at distance 0, closeness is now `Clamp01(0/15000) = 0`, so `PointValue(10,0,15000)`
becomes `10`, not `20` — **"a point touching the enemy is worth double its income"** fails. Also,
`PointValue(10,1000,15000)` now evaluates to `10.667` and `PointValue(10,14000,15000)` to `19.33`,
so `10.667 > 19.33` is false — **"of two points paying the same, the one nearer the enemy is filled
first"** fails (expected true, got false). Both are exactly the cases the plan names. Restored.
Build again: **succeeded, 0 warnings, 0 errors**. SHA256 after: `0BBB740CF2D83B6350A3A8AB32787DB74284DF10FD2CD70F5EF982FDCE40684D`
— byte-identical.

### T7 — Forward-base missions: siting, hold posts, the truck, platoon assignment and the strength predicates

In `Operations/CommanderOperationsFront.cs`: `Review()` now calls `SweepPool`, `RankPoints`,
`PlanForwardBases`, `AssignPlatoons` per managed HQ (T8/T9/T12-15 insert their calls into the same
loop, marked with comments). `ShouldWithdraw`/`AttackHasFailed` (pure, integer maths, 1/2 and 2/5).
`PlanForwardBases` opens one `ForwardBase` mission per front-ranked point with no mission yet,
`WantedPlatoons` 2 under a threat mark else 1. `OverlooksApproach` (the `FindHighGround` sample
shape, reused as a boolean test) folded into `RankPoints`'s sort as a third tie-break after
value/distance. `EnsureHoldPosts` — the ring-post logic CUT out of
`Ai/CommanderEnemyCommanderGarrison.EnsureGarrisonPosts`, cached per point, cleared in
`ResetSession`. `DriveToHoldPosts` — the `DriveGarrisonToPosts` shape (stable sort by instance id,
`HoldArrivedMeters` on-station skip), called from `TickMovement`'s `Holding` branch. `AssignPlatoons`
— losses/dissolution for every platoon regardless of state, matches unassigned platoons to open
`ForwardBase` missions, flips an arrived `Moving` platoon to `Holding`, reinforces under-strength
platoons and forms at most one new platoon per review from the free pool. `CheckStrength` wired
into `SelfCheck()`.

`Ai/CommanderEnemyCommanderGarrison.cs`'s `EnsureGarrisonPosts` body replaced with the one-line
forwarding call plus a pointer comment (ledger row 19), keeping `DriveGarrisonToPosts` compiling
until T11 deletes the file.

Build: **0 warnings, 0 errors.**

Greps:
- `EnsureGarrisonPosts` — two hits: the forwarder definition (`Ai/CommanderEnemyCommanderGarrison.cs:332`)
  and its one caller, `DriveGarrisonToPosts` (`:300`), as expected (plus one doc-comment mention).
- `EnsureHoldPosts` — the definition (`Operations/CommanderOperationsFront.cs:241`) and two callers:
  `DriveToHoldPosts` (`:279`, the forward-base holding path) and the `Garrison.cs` forwarder
  (`:334`). **Deviation from the plan's exact wording**: the plan describes T7's second caller as
  "the picket", but picket missions do not exist until T8 — at the end of T7 alone the second caller
  is necessarily the temporary Garrison.cs forwarder, not a picket. The count (two callers) matches;
  the naming does not yet. This resolves itself once T8 adds picket hold-posting and T11 deletes the
  forwarder.

**Fail-proof.** SHA256 before: `E768BA82F10DFDA367243E1F5C4AE931D42029C7B355FF497C8647DC69205C6F`.
Planted defect: `FailStrengthNumerator` changed from `2` to `3` (60% instead of 40%). Build:
**succeeded, 0 warnings, 0 errors**. Reasoned failure (not run in-game): at strength 5 of
establishment 10, `AttackHasFailed(5,10)` is now `5*5=25 < 10*3=30` = true, while
`ShouldWithdraw(5,10)` is `5*2=10 < 10*1=10` = false — the attack counts as failed while the
platoon is not yet withdrawing, exactly as the plan names. The ladder check
**"the fail threshold is below the withdraw threshold"** fails. Restored. Build again:
**succeeded, 0 warnings, 0 errors**. SHA256 after: `E768BA82F10DFDA367243E1F5C4AE931D42029C7B355FF497C8647DC69205C6F`
— byte-identical.

### T8 — Picket missions and the forward-base share cap

Added to `Operations/CommanderOperationsFront.cs`: `MaxForwardBases` (pure), `PlanPickets` (rear
points held by the HQ with no mission yet get a `Picket` mission; the forward-base cap demotes the
surplus and any point that turned rear back to a picket, disbanding its platoon(s) into the free
pool via `DemoteForwardBaseToPicket`; a picket whose point gains a threat mark promotes back to a
`ForwardBase`), `FillPickets` (tops every picket up to `PointsMinGarrison` with the cheapest
free-pool vehicles by `definition.value` and drives them to the point's hold posts), and
`CheckForwardBaseShare`. `DriveToHoldPosts` generalised from `CommanderPlatoon` to
`IReadOnlyList<Unit>` so the same call serves both a holding platoon and a picket detachment.
`Review()` now calls `PlanPickets` between `PlanForwardBases` and `AssignPlatoons`.
`CheckForwardBaseShare` wired into `SelfCheck()`.

**Implementation addendum** (not in the plan's original field list, necessary to hold a picket's
vehicles anywhere): `CommanderOperationsMission.PicketMembers` (`List<Unit>`) added in
`Operations/CommanderPlatoon.cs` — a picket is explicitly "not a platoon" (design SS2), so it has
no `CommanderPlatoon` to put its vehicles in; `Assigned` stays platoons-only.

Build: **0 warnings, 0 errors.**

**Fail-proof.** SHA256 before: `BEF6FC71BC06DE594E572CE434D18EC3BABC4A14BD0763E43C235CFFA7A67C4B`.
Planted defect: `MaxForwardBases`'s `Mathf.FloorToInt` changed to `Mathf.CeilToInt`. Build:
**succeeded, 0 warnings, 0 errors**. Reasoned failure (not run in-game): `MaxForwardBases(7, 0.5f)`
is now `CeilToInt(3.5) = 4`, not `3`. The named case **"the share rounds down, so the reserve is
never short"** fails (expected 3, got 4). Restored. Build again: **succeeded, 0 warnings, 0
errors**. SHA256 after: `BEF6FC71BC06DE594E572CE434D18EC3BABC4A14BD0763E43C235CFFA7A67C4B` —
byte-identical.

### T9 — The order book, requisitions and the depot claim, with self-check and fail-proof

`Core/CommanderGameAccess.cs`: `IsMunitionsTruckDefinition` added right after
`IsSpawnableVehicleDefinition` (ledger row 6). `Operations/CommanderPlatoon.cs`:
`CommanderPlatoonRoles.Of` now checks it first, so a munitions truck classifies as
`CommanderPlatoonRole.Truck` instead of falling through to `Other` (finishing the T2 stub).
`Units/CommanderMoveService.cs` (ledger row 31): the shared body of `TryReturnToBasegameLogistics`
extracted into `internal static bool TryDetachFromRearmLogistics(Unit unit, bool allowRestock)`;
`TryReturnToBasegameLogistics` is now a one-line `allowRestock: true` call (behaviour-neutral for
its existing caller, `ResumeAiForSelectedUnits`). Log-message wording on the exception path changed
from "Failed to return … to Basegame rearm AI" to "Failed to detach … from rearm logistics" since
the method now serves two callers with different destinations — noted as a deviation below.

`Operations/CommanderOperationsRequisitions.cs`: `CommanderRequisition` struct; `SumOrderBook` and
`LargestOpenRole(IReadOnlyList<int>)` (pure); `HasOpenAttackRequisition(FactionHQ)` and
`LargestOpenRole(FactionHQ)` (live wrappers); `TryClaim` now also calls `FillOldestRequisition`
(oldest-first credit against an open line, logging `"requisition filled"` when a line closes);
`SetRequisition`, `PostForwardBaseRequisitions` (one line per empty recipe slot across assigned and
still-forming platoons, plus one `Truck` line per mission), `PostAttackRequisitions` (proportioned
by the recipe across `WantedPlatoons × PlatoonSize`), `PostRequisitions` (closes timed-out lines —
proceeds or dissolves an empty forward base — then refreshes every mission's lines; wired into
`Review()` between `PlanPickets` and `AssignPlatoons`); `CheckOrderBook` wired into `SelfCheck()`.

Build: **0 warnings, 0 errors.**

**Deviation.** `TryDetachFromRearmLogistics`'s caught-exception log line was reworded (see above)
since the original wording ("…to Basegame rearm AI") is only accurate for one of its two callers
now. Cosmetic only — an exception-path log line, not user-facing behaviour — but recorded since the
plan called the extraction "behaviour-neutral".

**Fail-proof.** SHA256 before: `5B43BE2020BD60C99827C78DD356A6C867CA539E9A158F1A6240373BA35D17DD`.
Planted defect: `SumOrderBook`'s `Mathf.Max(0, requisition.Wanted - requisition.Filled)` changed to
`requisition.Wanted - requisition.Filled`. Build: **succeeded, 0 warnings, 0 errors**. Reasoned
failure (not run in-game): the sixth synthetic requisition (armour, wanted 1, filled 3) now
contributes `1 - 3 = -2` instead of `0`, so `totals[Armour]` becomes `2 + 2 + (-2) = 2`, not `4`.
The named case **"an over-filled line never subtracts from another role's order"** fails (expected
4, got 2). Restored. Build again: **succeeded, 0 warnings, 0 errors**. SHA256 after:
`5B43BE2020BD60C99827C78DD356A6C867CA539E9A158F1A6240373BA35D17DD` — byte-identical.

### T10 — The buyer consults the order book

`Ai/CommanderEnemyCommanderService.cs`: `OffensivePurchasesPerReview = 5` declared beside
`DuelPurchasesPerReview` (ledger row 14). Ledger row 13: the spend fraction now reads
`CommanderSettings.OperationsOffensiveSpendFraction` when `CommanderOperationsService.HasOpenAttackRequisition(hq)`,
else the duel/matched fraction unchanged. Ledger row 14: purchases per review likewise. Ledger row
15: the buy loop's third precedence tier — after the capture-unit and recon-unit overrides, before
the plan-based `Choose` fallback — asks `ChooseForRole(spendable, CommanderOperationsService.LargestOpenRole(hq))`
whenever `CommanderOperationsService.HasOpenRequisition(hq)` (an addition to T9's API: broader than
`HasOpenAttackRequisition`, since a forward base's shortfall should influence buying too, not only
an open attack). `ChooseForRole` added beside `ChooseCaptureUnit` (cheapest catalogue entry
matching the role, preferring a capture-capable entry within `Carrier`). `GetRoleLabel` added for
the purchase log line, which now reads `"…, for {ROLE}"` when the purchase came off the order book.
`Review`'s new `<summary>` states the order-book-open/empty behaviour in one sentence.

Build: **0 warnings, 0 errors.**

Grep `SpendFraction` / `PurchasesPerReview`: both constants (`SpendFraction`, `DuelSpendFraction`,
`PurchasesPerReview`, `DuelPurchasesPerReview`) plus exactly one read each, all inside `Review`
(lines 352-353 and 370-371) — as expected.

### T11 — Home guard becomes the reserve platoon; the garrison step is deleted

Deleted `Ai/CommanderEnemyCommanderGarrison.cs` in full (forwarder and pointer comment included).
`Ai/CommanderEnemyCommanderService.cs`: `ReviewGarrisons(localHq);` and its two comment lines
removed from the defence clock (ledger row 11); the garrison-scratch `Clear()` calls and their
comment removed from `ResetSession` (row 12); `CommanderState.Garrison` and its summary removed
(row 16). `Ai/CommanderEnemyCommanderDefence.cs`'s `ReviewDefence` now returns early — releasing any
surplus defenders and clearing `WantsDefenceUnit` — when `CommanderOperationsService.OwnsGroundForce(hq)`,
inserted right after the posture block and before `EnsureDefencePosts` (row 17, departure 8).
`Points/CommanderStrategicPointService.cs`: `CheckGarrisonTargets(failures);` removed from
`SelfCheck` (row 22); the `CheckGarrisonTargets` method and its doc comment removed (row 23); the
`NearestFreeSiteIndex` doc comment's `SelectGarrisonTargets (T9)` cross-reference reworded to state
the inclusive-boundary convention generically (row 24).

`Operations/CommanderOperationsFront.cs`: `BuildHoldRing` extracted as the shared ring-building
shape (Reuse rule 5) under both `EnsureHoldPosts` (a point's ring) and the new reserve ring;
`ReserveRingMeters` (900, planner-chosen); `DriveMembersToPosts` extracted so both
`DriveToHoldPosts` and the new `DriveToReserveRing` share one "drive to a set of posts" definition;
`FindOrCreateReserveMission` (one `Reserve` mission per HQ, created on demand);
`AssignPlatoons` now hands every still-unassigned, non-withdrawing platoon to the reserve mission
after the forward-base matching pass, and the arrival check (`Moving` -&gt; `Holding`) now covers
`Reserve` missions too (objective radius `ReserveRingMeters` in place of a point's own radius).
`Operations/CommanderOperationsService.cs`'s `TickMovement` drives a holding reserve platoon to
`DriveToReserveRing` instead of a point's hold posts.

Build: **0 warnings, 0 errors.**

Greps (all as expected — none):
- `IsGarrisonUnit` — no hits.
- `ReviewGarrisons` — no hits.
- `SelectGarrisonTargets` — no hits.
- `CommanderEnemyCommanderGarrison` — no hits (a doc-comment mention in
  `Operations/CommanderOperationsFront.cs` was reworded to name the file generically rather than by
  its deleted class name, so this grep reads as a clean zero rather than one harmless historical
  reference).
- `state.Garrison` — no hits.

### T12 — Offensive targets and sizing, with self-check and fail-proof

Created `Operations/CommanderOperationsOffensive.cs`: `SizingMultiplier`, `MinPlatoonsForBase`,
`MinPlatoonsForPoint`, `MaxPlatoonsPerAttack`, `ObservedRadiusMeters` constants; `PlatoonsForTarget`
(pure); `CountObserved` (the `IsUnderThreat` walk retargeted at an attack target, ground vehicles
only); `CountSparePlatoons` (platoons neither withdrawing nor already attacking, unassigned or in
reserve); `TryChooseOffensiveTarget` (ranks the nearest enemy-held front control point first, else
the nearest beatable enemy base by `PlatoonsForTarget(..., targetIsBase: true) <= spare`) — not yet
wired into `Review()`; T13/T14 build axes and the assault on top of it before the whole pipeline is
connected. `CheckSizing` wired into `SelfCheck()`.

Build: **0 warnings, 0 errors.**

**Fail-proof.** SHA256 before: `8DA6C795167138AEE801B880AC2FF7B832FF5FDAC738856615F5CDA901127752`.
Planted defect: `PlatoonsForTarget`'s `Mathf.CeilToInt` changed to `Mathf.FloorToInt`. Build:
**succeeded, 0 warnings, 0 errors**. Reasoned failure (not run in-game): `PlatoonsForTarget(5, 6,
false)` is now `FloorToInt(7.5/6) = FloorToInt(1.25) = 1`, clamped to `1`, not `2`. The named case
**"1.5 x 5 = 7.5 rounds up to two platoons"** fails (expected 2, got 1). Restored. Build again:
**succeeded, 0 warnings, 0 errors**. SHA256 after: `8DA6C795167138AEE801B880AC2FF7B832FF5FDAC738856615F5CDA901127752`
— byte-identical.

### T13 — Axes and release points, with self-check and fail-proof

Added to `Operations/CommanderOperationsOffensive.cs`: `AxisCandidateRadiusMeters`,
`AxisDistanceTolerance`, `AxisMinBearingDegrees`, `FlankMinMeters`/`FlankMaxMeters`,
`ReleaseDistanceMeters`, `ReleaseSnapMeters`, `FlatNormalY` (value-matched to the discovery
constant, independently declared since that one is private to a different class); `SelectAxes`
(pure, seed-and-accept over ascending distance with a circular bearing test, best seed wins ties to
the lowest index); `CircularBearingDifference`; `TryPlanAxes` (candidates = holding forward bases
and held bases within radius; 2-3 in reach use `SelectAxes` directly, exactly one gets a
terrain-validated flank partner via `TryFindFlankFormUp`, none waits); `ReleasePointFor` (departure
4: nearest Crossroads/Roadside point within `ReleaseSnapMeters`, else the terrain-snapped ideal
point if flat, else a ring probe around it); `CheckAxes` wired into `SelfCheck()`. Not yet wired
into `Review()` — T14 assembles the whole offensive pipeline.

Build: **0 warnings, 0 errors.**

**Fail-proof.** SHA256 before: `CE6262F033B7F9CEC9B36ED786E604FB9EA56BC42EDADBE56ECFB8FCBAABA640`.
Planted defect: `CircularBearingDifference` changed from the wrap-around formula to a plain
`Mathf.Abs(a - b)`. Build: **succeeded, 0 warnings, 0 errors**. Reasoned failure (not run in-game):
for bearings 350 deg and 10 deg, the buggy difference is `|350-10| = 340`, which is `>= 60` (the
minimum), so the two candidates are now wrongly treated as far enough apart and both accepted. The
named case **"bearings are compared the short way round"** fails (expected `[0]`, got `[0,1]`).
Restored. Build again: **succeeded, 0 warnings, 0 errors**. SHA256 after:
`CE6262F033B7F9CEC9B36ED786E604FB9EA56BC42EDADBE56ECFB8FCBAABA640` — byte-identical.

### T14 — The assault: form up, wait, go in, flip in passing, withdraw or fail

`Ai/CommanderCaptureService.cs`: `internal static GlobalPosition GetHoldPointFor(Airbase airbase) =>
GetHoldPoint(airbase);` added beside the private `GetHoldPoint` (ledger addition, as the plan itself
calls out). `Points/CommanderStrategicPointDiscovery.cs`: `SegmentDistanceSquared` changed from
`private` to `internal` — a third caller (the en-route flip test) makes it worth sharing rather than
tripling (ledger addition).

`Operations/CommanderPlatoon.cs`: `CommanderOperationsMission` gained `FirstGroupArrivedAt` and
`Launched` fields (an addition beyond the plan's original list, needed to measure the 4-minute
form-up timeout from somewhere and to know whether a mission is still waiting or already going in).

`Operations/CommanderOperationsOffensive.cs`: `AssaultFormUpTimeoutSeconds` (240), `EnRouteFlipMeters`
(2000), `AssaultArrivedMeters`; `PlanOffensive` (one attack at a time; opens an `Attack` mission once
`TryChooseOffensiveTarget` + `TryPlanAxes` + a non-zero `PlatoonsForTarget`-vs-spare sizing all
line up); `AssignPlatoonsToAxes` (fills each axis template, then doubles further platoons onto
existing axes round-robin — more platoons than axes is expected once sizing exceeds three);
`FindEnRoutePoint` (the en-route flip test, using the now-`internal` `SegmentDistanceSquared`);
`UpdateAttacks` (routes each group to its release point with the en-route flip applied per group,
launches once every group has arrived or the timeout fires, then routes launched groups to the
target's hold point — `GetHoldPointFor` for a base, `point.Position` for a control point — and
resolves the outcome via `ResolveAttack` once the target is taken or `AttackHasFailed`).
`Operations/CommanderOperationsFront.cs`'s `AssignPlatoons` gained an `Attack`-mission matching loop
between the forward-base loop and the reserve catch-all, per the pipeline order. `Review()` now
calls `PlanOffensive` then `UpdateAttacks` between `PlanPickets` and `PostRequisitions`.

`ShouldWithdraw`/`AttackHasFailed` are consumed here exactly as defined in T7; no new self-check —
`CheckStrength` and its fail-proof already cover both, per the plan.

Build: **0 warnings, 0 errors.**

### T15 — The pressure clock, with self-check and fail-proof

Refactored `PlanOffensive` into a thin call to a new shared `TryOpenAttack(hq, state, minPlatoons,
logVerb)` (Reuse rule 5 — the ordinary one-attack-at-a-time open and the pressure clock's forced
open are the same procedure with a different floor and a different log verb, so this is one
definition instead of two). Added to `Operations/CommanderOperationsOffensive.cs`:
`PressurePerMinute`, `PressurePerPointLost` (planner-chosen), `PressurePerEncroachingPoint`
(planner-chosen); `StepPressure`/`PressureForcesAttack` (pure); `CountHeldControlPoints`,
`CountEncroachingEnemyPoints`; `UpdatePressure` (steps `state.Pressure` once per review from the
drop in held points and the encroaching-enemy-point count, then calls `TryOpenAttack(..., minPlatoons:
2, ...)` once the clock reaches `PressureIntervalMinutes` — the reset back to zero on a successful
open lives inside `TryOpenAttack` itself, since any attack opening relieves the patience whether or
not the clock forced it). `CheckPressure` wired into `SelfCheck()`. `Review()` now calls
`UpdatePressure` after `UpdateAttacks` and before `PostRequisitions`.

Build: **0 warnings, 0 errors.**

**Fail-proof.** SHA256 before: `9ED7DF1E635745D4B217EA19FB161D2B8629C79691D8C4360499675B0A7FE94D`.
Planted defect: `PressureForcesAttack`'s `pressure >= pressureIntervalMinutes * PressurePerMinute`
changed to `>`. Build: **succeeded, 0 warnings, 0 errors**. Reasoned failure (not run in-game):
`PressureForcesAttack(12f, 12f)` is now `12 > 12` = false. The named case **"twelve quiet minutes
force an attack"** fails (expected true, got false) — the clock now overshoots by a whole review
before firing. Restored. Build again: **succeeded, 0 warnings, 0 errors**. SHA256 after:
`9ED7DF1E635745D4B217EA19FB161D2B8629C79691D8C4360499675B0A7FE94D` — byte-identical.

### T16 — Markers (ledger row 27)

Created `Operations/CommanderOperationsMarkers.cs`: `DrawMarkers` (a platoon marker at the leader,
label `"{Name} {members}/{Establishment} {STATE}"`, drawn for an enemy platoon only where its leader
passes `CommanderGameAccess.ShouldRetainCommanderMarker`'s 8 s tracking test; release-point crosses
for the local faction's own live attacks only), following the `fullscreenMap`/`anyMap` world-vs-map
split. `IsForwardBase(CommanderStrategicPoint)` exposed for the point marker's FOB tag.

Ledger row 27: `Units/CommanderWorldMarkerRenderer.cs` now calls
`CommanderOperationsService.Instance?.DrawMarkers(camera);` right after the point service's own
`DrawMarkers`. Addendum (as the plan itself calls for): `Points/CommanderStrategicPointMarkers.cs`'s
`DrawPointMarker` appends `"  FOB"` to a point's readout when `IsForwardBase` is true.

Build: **0 warnings, 0 errors.**

### T17 — COMMANDER LOG OPERATIONS block (ledger row 25)

**Addendum finishing T7/T9's truck lifecycle**: neither task had actually parked or tracked a
forward base's munitions truck (only the requisition line existed), which T17 needs for the FOB
line's truck status. Added `CommanderOperationsMission.Truck`/`NoTruckLogged` fields
(`Operations/CommanderPlatoon.cs`) and `FillMissionTrucks` / `TruckRingFraction`
(`Operations/CommanderOperationsFront.cs`): claims the cheapest free-pool `Truck`-role vehicle for
every forward base without one, detaches it via `CommanderMoveService.TryDetachFromRearmLogistics(unit,
allowRestock: false)`, parks it on the ring, and logs "no munitions truck available" once per
mission when none is free; `DemoteForwardBaseToPicket` now releases a demoted mission's truck back
to the pool. Wired into `Review()` right after `PlanForwardBases`.

`Operations/CommanderOperationsService.cs`: `DescribeOperations(FactionHQ, List<string>)` — one
summary line (`PRESSURE p/interval  PLATOONS n  FOBS n  ORDERS n`) then one line per live mission:
`ATTACK label: n platoons, axes AXIS 1 + AXIS 2, forming up a/n`, `FOB label: n/wanted
platoons[, no truck]`, `PICKET label: n/MinGarrison`. ASCII only.

`UI/CommanderAiLogUi.cs`: `MissionLinesInHeader` (3, planner-chosen); `DrawHeader` now draws the
summary line and up to 3 mission lines from `DescribeOperations`, with a `+n more` tail when there
are more, added after the existing three rows.

Build: **0 warnings, 0 errors.**

### T18 — OPERATIONS sliders on the POINTS tab (ledger row 26, departure 7)

`UI/CommanderOverlayUiSettings.cs`: `DrawPointsSettings` now draws a `GUI.BeginScrollView` (the
`DrawShortcutList` shape) over its content instead of drawing the STRATEGIC POINTS box directly;
`DrawStrategicPointsBox` (the same nine sliders, repositioned against scrolled content) and
`DrawOperationsBox` (five new sliders: platoon size 2-10, forward-base share 0-1, front range 5-40 km
stored in metres, pressure interval 4-30 min, offensive spend 0.1-1) plus its own footnote. Both
boxes are drawn through a new `DrawPointsSlider` helper (`DrawCameraSlider`'s shape, positioned
against a caller-supplied width since it draws inside scrolled content). `pointsSettingsScroll`
field added in `UI/CommanderOverlayUi.cs`.

**Deviation.** The plan describes the FOB-share row as "0-1, '0.00', int per cent via × 100" — a
percentage transform on top of the 0-1 stored value. Implemented instead as a plain 0-1 fraction
slider with `"0.00"` formatting (matching how `OffensiveSpendFraction` already reads elsewhere in
the same box and in `Core/CommanderSettings.cs`'s own comments) rather than building a one-off
percent transform for a single slider. Functionally equivalent (the same range is reachable); only
the displayed number's scale differs (0.50 rather than 50).

Build: **0 warnings, 0 errors.**

### T19 — CHANGELOG, README, design departures (ledger rows 28-30)

`CHANGELOG.md`: new bullet at the top of `### Added` under `## Unreleased` (line 230) — platoons,
forward bases, pickets, offensives, the pressure clock, markers, the OPERATIONS block, the five new
sliders, and that nothing persists across a reload. `README.md`: new `## Platoon operations` section
(line 703) between `## Strategic points` and `## Unit systems`, four subsections (what a platoon is,
the front line, offensives, what you can watch). `design.md`: `## Departures recorded during
planning`, eight one-line entries.

No `dotnet build` needed (none of the three files compile).

Greps: `The AI no longer trickles vehicles down one road` in `CHANGELOG.md:230` — found. `^##
Platoon operations` in `README.md:703` — found. `^## Departures recorded during planning` in
`design.md:132` — found.

### T20 — Final build, greps, notes

**Final build** (`dotnet build GroundControlRts.csproj -c Release`, `NUCLEAR_OPTION_DIR` set), last
5 lines:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:00.53
```

**Greps** (all as expected):

- `IsGarrisonUnit` — no hits.
- `ReviewGarrisons` — no hits.
- `SelectGarrisonTargets` — no hits.
- `CheckGarrisonTargets` — no hits.
- `CommanderEnemyCommanderGarrison` — no hits.
- `EnsureGarrisonPosts` — no hits (the T7 forwarder is gone with the file, as expected).
- `state.Garrison` — no hits.
- `IsPlatoonUnit` — definition (`Operations/CommanderOperationsRequisitions.cs:182`) and exactly two
  callers: `Ai/CommanderCaptureService.cs:706`, `Ai/CommanderEnemyCommanderDefence.cs:392`.
- `OwnsGroundForce` — definition (`Operations/CommanderOperationsRequisitions.cs:208`) and exactly
  two callers: `Ai/CommanderCaptureService.cs:549` (`ReviewEnemy`),
  `Ai/CommanderEnemyCommanderDefence.cs:166` (`ReviewDefence`); one harmless doc-comment mention.
- `IssuePlatoonMove` — definition (`Units/CommanderMoveService.cs:988`) and exactly one caller:
  `Operations/CommanderOperationsService.cs:194` (the movement tick); doc-comment mentions elsewhere.
- `HeadingDegrees` — definition (`Units/CommanderMoveService.cs:465`) and exactly two callers:
  `ResolveHeading` (`:459`) and `IssuePlatoonMove` (`:1013`).
- `ReissueToleranceMeters` — definition (`Units/CommanderMoveService.cs:46`) and exactly two callers:
  `Issue` (`:965`) and `IssuePlatoonMove` (`:1026`).
- `40f` inside `Units/CommanderMoveService.cs` — two hits, both `const` declarations
  (`ReissueToleranceMeters = 40f` and `RetreatTimeoutSeconds = 240f`, the latter matching only as a
  substring); neither is inside `Issue`, as expected.
- `IsMunitionsTruckDefinition` — definition (`Core/CommanderGameAccess.cs:695`), the role
  classification the requisition system reads (`Operations/CommanderPlatoon.cs:49`), and the buyer
  (`Ai/CommanderEnemyCommanderService.cs:670`, added during this task — see deviations).
- `TryDetachFromRearmLogistics` — one definition (`Units/CommanderMoveService.cs:1512`) and exactly
  two callers: `TryReturnToBasegameLogistics` (`:1497`) and the operations claim
  (`Operations/CommanderOperationsFront.cs:458`, `FillMissionTrucks`).
- `AssignPlatoons` — definition (`Operations/CommanderOperationsFront.cs:543`) and exactly one real
  caller, `Review` (`Operations/CommanderOperationsService.cs:144`). The pattern also substring-matches
  the unrelated `AssignPlatoonsToAxes` (`Operations/CommanderOperationsOffensive.cs:622` definition,
  `:734` caller) — a different method introduced in T14 for a different job (distributing platoons
  across assault-group axes); noted so the extra hits are not mistaken for a second `AssignPlatoons`
  caller.
- `SegmentDistanceSquared` — one definition now shared three ways: `Economy/CommanderBuildPreview.cs`
  keeps its own independent `private static` definition (a fourth, deliberately-separate one per its
  own comment — not part of this ledger row), while
  `Points/CommanderStrategicPointDiscovery.cs:904`'s definition (now `internal`) has two callers:
  `JunctionDegree` (`:805`) and the offensive's en-route flip test
  (`Operations/CommanderOperationsOffensive.cs:700`).

Every task above is ticked; every task's build result, fail-proof record (where it has one) and
notes are recorded in this section in order.

## Execution fix cycle 1

Independent Opus review returned FAIL with five blocking bugs (B1-B6, B5 numbered out of order
below to match its finding label) plus a batch of smaller findings. All fixed in the existing
code; no architecture change, no re-plan. Build after every fix: **0 warnings, 0 errors**, every
time (see individual entries). Final build at the end of this section.

- **B1 — every attack "launched" with zero platoons.** Two changes, both in
  `Operations/CommanderOperationsOffensive.cs` and `Operations/CommanderOperationsService.cs`:
  (a) `UpdateAttacks`'s per-axis loop now sets `allArrived = false` (and a new `everyAxisManned`
  flag) when a group's `Platoon` is null, instead of `continue`-ing past `allArrived` with no
  effect on it; the launch condition also now requires `everyAxisManned` and
  `mission.Assigned.Count > 0` alongside the existing `allArrived && mission.Axes.Count > 0`. (b)
  `Review()` now runs `AssignPlatoons` right after `PlanOffensive`/`UpdatePressure` and before
  `UpdateAttacks`/`PostRequisitions` — a mission is manned the same review it opens, before its
  arrival test ever runs. The forward-base -> picket -> offensive planning order (design SS2) is
  untouched; only where platoon-matching falls in the sequence moved. `PostRequisitions`'s doc
  comment updated to describe the new position (it now reads this review's assignment instead of
  last review's). Build: 0 warnings, 0 errors.
- **B2 — `SweepPool` re-claimed picket vehicles and parked trucks.** Added one definition of
  "already spoken for", `IsClaimedVehicle` (`Operations/CommanderOperationsRequisitions.cs`),
  covering `Pool`, platoon membership, every mission's `PicketMembers` and `Truck`. `TryClaim`,
  `SweepPool` and `IsPlatoonUnit` all call it instead of the old `Pool.Contains` +
  `IsPlatoonMember` pair (Reuse rule 4 — one walk, three callers). Build: 0 warnings, 0 errors.
- **B3 — `Withdrawing` was terminal.** `AssignPlatoons` (`Operations/CommanderOperationsFront.cs`)
  now flips a `Withdrawing` platoon back to `Forming` once `!ShouldWithdraw` for its current
  strength. Added `PostWithdrawingRequisitions` (`Operations/CommanderOperationsRequisitions.cs`),
  called from `PostRequisitions`, posting one aggregate mission-less requisition line per role for
  every withdrawing platoon's empty recipe slots combined (`SetRequisition`'s `mission` parameter
  is now nullable to support this; `CommanderRequisition`'s doc comment updated to explain the
  null case). Build: 0 warnings, 0 errors.
- **B4 — reserve platoons never released.** `AssignPlatoons` now releases any platoon whose
  `Mission.Kind == Reserve` at the top of the function, before the forward-base and attack
  matching loops run, so a reserve platoon competes for a mission again on the very next review
  instead of being excluded forever by their `Mission != null` skip. Build: 0 warnings, 0 errors.
- **B5 — a failed attack did not resize the next one.** Added `OperationsState.ObservedFloors`
  (`Dictionary<object, int>`, per-HQ, cleared with the rest of the state on `ResetSession`) and a
  pure `EffectiveObserved(liveObserved, storedFloor)` (`Operations/CommanderOperationsOffensive.cs`,
  `Mathf.Max` of the two) with a new self-check `CheckObservedFloor` (four cases), wired into
  `SelfCheck()`. `ResolveAttack` now raises the target's floor to the live observed count at the
  moment of failure; `TryOpenAttack` and `TryChooseOffensiveTarget`'s airbase sizing both read
  `EffectiveObserved(CountObserved(...), GetObservedFloor(...))` instead of the live count alone.
  Fail-proof: hashed the file
  (`29c38974ba3ca2f1d6610c5e1c7662fdad9bfc44406ab13e9845022ebb2b95dd`), planted `Mathf.Min` in place
  of `Mathf.Max` in `EffectiveObserved`, built (succeeded — this is a runtime check, not a compile
  gate), reasoned (game not launched, per this track's own rule) that
  `CheckObservedFloor`'s "a live observation above the floor wins" and "a floor above a decayed
  live observation wins" and "no floor at all falls back to the live observation" cases would each
  log `Operations self-check FAILED: ...` at plugin load (only "equal values agree" survives a
  `Min`), restored the file, rebuilt, confirmed the hash matches. Build: 0 warnings, 0 errors.
- **B6 — `PlanForwardBases` had no ownership test.** Copied `PlanPickets`'s
  `!ReferenceEquals(ranked.Point.GetOwner(), hq)` test into `PlanForwardBases`
  (`Operations/CommanderOperationsFront.cs`), so an enemy-held point can no longer get a
  forward-base mission alongside its `Attack` mission. Build: 0 warnings, 0 errors.
- **`GetHoldPoint`/`GetHoldPointFor` doc-comment swap** (`Ai/CommanderCaptureService.cs`): moved
  the original `GetHoldPoint` summary back onto `GetHoldPoint`, ahead of the wrapper's own summary
  on `GetHoldPointFor`. Build: 0 warnings, 0 errors.
- **`FormedAt`/`StateSince`/`OpenedAt` placeholders** (`Operations/CommanderPlatoon.cs`): all three
  were write-only. `FormedAt` deleted outright (nothing in the design ever asked for a platoon-age
  reader). For `StateSince` and `OpenedAt`, the two candidate "real reader" ideas found (a
  time-in-state suffix on the platoon marker; a mission-age suffix on the COMMANDER LOG lines) both
  require changing player-visible strings design.md fixes the exact wording of (SS5's marker and
  log examples, and the AXIS-label/platoon-count fix immediately below already had to match that
  wording precisely) — wiring either in would be an unrequested log/marker format change, not a bug
  fix. Deleted both fields and every now-dead `= Time.time` write across
  `Operations/CommanderOperationsService.cs`, `Operations/CommanderOperationsFront.cs` and
  `Operations/CommanderOperationsOffensive.cs`, rather than inventing new display behaviour to read
  them. Build: 0 warnings, 0 errors.
- **Truck requisition line re-posted unconditionally**
  (`Operations/CommanderOperationsRequisitions.cs:474`, now `PostForwardBaseRequisitions`'s last
  line): changed `SetRequisition(state, mission, Truck, 1)` to
  `SetRequisition(state, mission, Truck, mission.Truck == null ? 1 : 0)` — `SetRequisition`'s own
  zero-wanted branch removes the line outright once the mission has a truck, however that truck
  arrived (claim hook or `FillMissionTrucks` pulling straight from the pool, which never routed
  through `FillOldestRequisition`). Build: 0 warnings, 0 errors.
- **`NearestFriendlyRallyPoint` measured from the HQ, not the platoon**
  (`Operations/CommanderOperationsFront.cs`): now takes the withdrawing `CommanderPlatoon` and
  measures from its leader's position (falling back to the territory centre with no live leader).
  Build: 0 warnings, 0 errors.
- **Vacuous cohesion check on an assault group's arrival test**
  (`Operations/CommanderOperationsOffensive.cs:765`, `UpdateAttacks`): replaced
  `HasArrived(..., group.Platoon.Members.Count, group.Platoon.Members.Count)` with
  `HasArrived(..., CountInCohesion(group.Platoon), group.Platoon.Members.Count)` — the same
  `CountInCohesion` the forward-base arrival test already uses. Build: 0 warnings, 0 errors.
- **`IsForwardBase` leaked enemy FOB tags** (`Operations/CommanderOperationsMarkers.cs`): now takes
  `FactionHQ? localHq` and only checks that HQ's own missions, matching how
  `DrawReleaseCrosses` already restricts release points to the local faction. Call site in
  `Points/CommanderStrategicPointMarkers.cs` updated to pass `localHq`. Build: 0 warnings, 0 errors.
- **`CommanderAssaultGroup` fields had no `<summary>`, and a dead `<see cref>`**
  (`Operations/CommanderPlatoon.cs`): added a `<summary>` to each of `FormUpPoint`, `ReleasePoint`,
  `Platoon` and `Arrived`; fixed `<see cref="CommanderOperationsService.PlatoonSize"/>` (that
  member does not exist) to `<see cref="CommanderSettings.OperationsPlatoonSize"/>`. Build: 0
  warnings, 0 errors.
- **`DescribeAttack` printed placeholder axis labels and counted groups, not platoons**
  (`Operations/CommanderOperationsService.cs`): added `AxisLabel`, which names an axis `"FOB " +
  <point label>` when its form-up point is a live forward base of this HQ's, the airbase's own
  label when it is a held base, or a numbered fallback (`AXIS n`) otherwise (a flank point per
  design SS3). `DescribeAttack` now builds one label per distinct axis (still deduplicated by
  release point) via `AxisLabel`, and counts only axes with a non-null `Platoon` for both the
  numerator and denominator of "forming up N/M", rather than every `CommanderAssaultGroup`
  regardless of whether it has been assigned one yet. Build: 0 warnings, 0 errors.
- **Redundant clause in `ChooseForRole`** (`Ai/CommanderEnemyCommanderService.cs:660`): the
  preceding `continue` (`definition.value > budget || ... ` at the top of the loop) already
  guarantees `definition.value <= budget` for anything reaching this line, so removing the
  restated clause is provably behaviour-neutral. Removed, with a comment recording why. Build: 0
  warnings, 0 errors.

Final build after every fix above:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Every finding named in the fix-cycle brief (B1-B6 and every smaller item) was fixed; none were
left unaddressed.

## Deviations from the plan

1. **T2**: `OperationsState.Requisitions` was added in T9 (when `CommanderRequisition` is defined)
   rather than in T2 as literally listed, so every intermediate task's build stays green — the type
   the field needs did not exist yet at T2. Likewise `CommanderPlatoonRoles.Of`'s truck detection
   was wired in T9 once `CommanderGameAccess.IsMunitionsTruckDefinition` existed, falling through to
   `Other` for any truck before then (harmless: no recipe slot is `Other`).
2. **T2 (compile mechanics)**: every data-holder field in `Operations/CommanderPlatoon.cs` and
   `Operations/CommanderOperationsService.cs` needed an explicit initializer (`= default`, `= null`,
   `= 0f`, etc.) even where the plan's prose just names a field, because nothing assigned them yet
   and the compiler's CS0649 forbids that under the 0-warnings rule. Mechanical, not a design
   change.
3. **T7 grep**: the plan describes `EnsureHoldPosts`'s second caller at the end of T7 as "the
   picket", but picket missions do not exist until T8 — at the end of T7 alone the second caller is
   necessarily the temporary `Ai/CommanderEnemyCommanderGarrison.cs` forwarder. The count (two
   callers) matched; the naming did not, until T8 added the picket's own caller.
4. **T8 (addition)**: `CommanderOperationsMission.PicketMembers` (`List<Unit>`) was added — not in
   the plan's original field list — because a picket explicitly has no `CommanderPlatoon` to hold
   its vehicles in (`Assigned` is platoons-only).
5. **T9**: `CommanderMoveService.TryDetachFromRearmLogistics`'s caught-exception log line was
   reworded from "Failed to return to Basegame rearm AI" to "Failed to detach from rearm
   logistics", since the extracted method now serves two callers with different destinations.
   Cosmetic (an exception-path log line), but the plan called the extraction "behaviour-neutral" so
   it is recorded here.
6. **T9 (addition)**: `CommanderOperationsService.HasOpenRequisition(FactionHQ)` was added — broader
   than the plan's `HasOpenAttackRequisition` — because T10's buy-loop order-book precedence needed
   to ask "is there any open line at all" (a forward base's shortfall should influence buying too),
   not only "is there an open attack".
7. **T14 (addition)**: `CommanderOperationsMission.FirstGroupArrivedAt` and `Launched` fields were
   added — not in the plan's original field list — because the 4-minute form-up timeout has to be
   measured from somewhere and the assault needs to know whether it is still waiting or already
   going in.
8. **T14 (data-shape simplification)**: `CommanderAssaultGroup.Platoon` is a single nullable field,
   exactly as the plan lists it; when an attack fields more platoons than axes, additional
   `CommanderAssaultGroup` entries are created sharing the same axis's `FormUpPoint`/`ReleasePoint`
   (one group per platoon) rather than growing a list inside one axis entry. Not self-check-tested;
   a reasonable reading of an otherwise-underspecified data shape.
9. **T17 (addition, finishing T7/T9)**: neither T7 nor T9 had actually parked or tracked a forward
   base's munitions truck — only the requisition line existed. `CommanderOperationsMission.Truck`
   and `NoTruckLogged` fields, plus `FillMissionTrucks`/`TruckRingFraction`
   (`Operations/CommanderOperationsFront.cs`), were added during T17 to finish that lifecycle, since
   the OPERATIONS block's FOB line needs real truck status to report.
10. **T18**: the plan describes the FOB-share slider as "0-1, formatted 0.00, shown as a percentage
    via a x100 transform". It is implemented as a plain 0-1 fraction slider with 0.00 formatting
    (matching how `OffensiveSpendFraction` already reads elsewhere) rather than building a one-off
    percent transform for a single slider. Functionally equivalent; only the displayed number's
    scale differs (0.50 rather than 50).
11. **Non-pure orchestration generally** (`PlanForwardBases`'s exact priority ordering beyond
    value/distance/`OverlooksApproach`, `TryPlanAxes`'s flank-point search, `ReleasePointFor`'s ring
    probe, the assault state machine's exact bookkeeping): originally recorded here as "implemented
    faithfully but not to self-check precision, deferred to in-game acceptance". The independent
    Opus review (execution fix cycle 1) found that characterisation false: the form-up wait, the
    withdraw-and-return cycle, the reserve-to-mission path and the failed-attack re-sizing did not
    execute at all, faithfully or otherwise — `Review()`'s ordering meant every attack launched
    on the review it opened with zero platoons (B1); `Withdrawing` was a terminal state no platoon
    ever left (B3); a platoon assigned to the reserve mission was never released to hold a forward
    base or join an attack (B4); and a failed attack never wrote back an observed floor, so a
    retried attack on the same target was sized identically every time (B5). Fix cycle 1
    implemented all four; see "Execution fix cycle 1" above for what changed and its build/
    fail-proof record. What remains genuinely deferred to in-game acceptance, unchanged from the
    original record, is only the *tuning* of the non-pure orchestration named above (siting
    priority beyond value/distance/`OverlooksApproach`, the flank-point search, the ring probe) —
    not whether the state machine steps run at all. Every *pure* function named in the plan
    (`FillRecipe`, `HasArrived`, `IsFrontPoint`, `PointValue`, `ShouldWithdraw`, `AttackHasFailed`,
    `MaxForwardBases`, `SumOrderBook`, `LargestOpenRole`, `PlatoonsForTarget`, `SelectAxes`,
    `StepPressure`, `PressureForcesAttack`, and fix cycle 1's `EffectiveObserved`) is implemented
    and self-checked exactly as specified, with its fail-proof recorded.

**Not verified**: none of the in-game acceptance checklist (design.md's Verification section,
reproduced in this plan's own "In-game acceptance" section) — per this track's explicit
instructions, that is USER TO VERIFY IN GAME, not the executor. Every fail-proof reasoning above is
explicitly reasoned from the code, not observed at runtime, since the game was never launched.

## Execution fix cycle 2

An independent Opus review found that fix cycle 1's B4 fix (reserve platoons never released) traded
a trap for a thrash: a reserve platoon that had genuinely settled into `Holding` on the 900 m reserve
ring was knocked back to `Moving` on every single review, forever, driving it back toward the
territory centre and then back out to the ring on a permanent 30 s cycle. Two changes, both in
`Operations/CommanderOperationsFront.cs`.

- **Regression — the reserve catch-all forced `State`/`Objective` unconditionally.**
  `AssignPlatoons`'s release loop clears `Mission` on every reserve platoon each review regardless of
  `State` (correct — that is what lets a settled platoon still compete for a forward base or an
  attack, and must stay). The catch-all that re-adds an unclaimed platoon to the reserve mission
  then unconditionally set `platoon.State = CommanderPlatoonState.Moving` and
  `platoon.Objective = territoryCenter`, even for a platoon that was already `Holding` at the ring.
  Fixed by leaving `State`/`Objective` alone when the platoon is already `Holding`:
  `platoon.Mission = reserve; reserve.Assigned.Add(platoon);` runs unconditionally (so it is still
  re-attached and still competes for a forward base or attack next review — B4's fix is untouched),
  but the `State = Moving; Objective = territoryCenter;` pair is now guarded by
  `if (platoon.State != CommanderPlatoonState.Holding)`. A platoon not yet holding (fresh into
  reserve, or coming back from `Forming`) keeps the original behaviour and drives to the ring; a
  platoon already holding it is left alone under the movement tick's `Holding` branch.
- **Root cause — `DriveMembersToPosts` never wrote `platoon.Issued`, so cohesion was measured
  against a stale formation slot, not the post actually held.** `CountInCohesion` (the gate the
  arrival test uses to flip `Moving` -> `Holding`) reads `platoon.Issued`, the same dictionary
  `CommanderMoveService.IssuePlatoonMove` maintains for a platoon under way. `DriveMembersToPosts`
  (called for a `Holding` platoon's hold posts and the reserve ring) issued destinations straight
  through `CommanderGameAccess.TrySetDestination` and never touched `Issued` at all, so once a
  platoon started holding, `Issued` froze at the last formation-move slot near the objective centre
  — about 900 m off from the actual reserve ring post. Concretely, this is what turned "forced back
  to `Moving`" into a *repeating* cycle rather than a one-off nuisance: back in `Moving`,
  `IssuePlatoonMove` re-centred the formation slots on the objective (the territory centre) and the
  platoon drove inward; because the stale `Issued` slots were also near the centre, cohesion read as
  satisfied almost as soon as the platoon closed in, `HasArrived` fired, and the platoon flipped back
  to `Holding` — which immediately drove it back out to the ring, at which point the *next* review's
  (now-fixed) catch-all would have left it alone, except the platoon was mid-transit rather than
  settled, so nothing broke the cycle. Fixed by giving `DriveMembersToPosts` an optional
  `Dictionary<Unit, GlobalPosition>? issued` parameter and writing `issued[unit] = post` for every
  member — unconditionally, even when the existing `HoldArrivedMeters` on-station skip fires and no
  new `TrySetDestination` call is made, so `Issued` always reflects the post a holding member is
  actually meant to be standing on, never a leftover value from before it started holding.
  `DriveToHoldPosts` and `DriveToReserveRing` both gained the same optional/required parameter and
  now forward it; `CommanderOperationsService.TickMovement`'s two call sites
  (`DriveToHoldPosts(platoon.Mission.Point, platoon.Members, platoon.Issued)` and
  `DriveToReserveRing(entry.Key, platoon.Members, platoon.Issued)`) now pass `platoon.Issued`. The
  one call site with no platoon to key off — `FillPickets`'s
  `DriveToHoldPosts(mission.Point, mission.PicketMembers)` — passes no dictionary (the default
  `null`), which `DriveMembersToPosts` treats as "skip the write": a picket detachment is not a
  platoon and has no cohesion to measure, exactly as `CommanderPlatoonState.Holding`'s own doc
  comment already says. This was reused rather than reinvented per-caller (Reuse rule 5): the same
  fix reaches the forward-base hold posts as well as the reserve ring, since both drove through the
  same `DriveMembersToPosts`, so a forward-base platoon's cohesion is now also measured against its
  real hold post rather than a stale formation slot — the task brief's suspicion that the same bug
  reached forward-base holding, not just the reserve, is correct, and this one change fixes both call
  sites at once rather than needing a second, forward-base-specific patch.
- **Checked for the same unconditional-overwrite pattern at the forward-base and picket assignment
  paths — not present, and confirmed why.** The `ForwardBase` and `Attack` matching loops in
  `AssignPlatoons` only ever touch a platoon whose `Mission == null`, i.e. a platoon not yet
  assigned; an already-`Holding` forward-base platoon keeps its `Mission` set to that forward base
  and is skipped by both loops' `platoon.Mission != null` guard. Unlike the reserve mission, nothing
  releases an already-assigned forward-base platoon's `Mission` every review — only the losses rule
  (withdraw/dissolve), `DemoteForwardBaseToPicket` (share cap or the point turning rear) and
  `ResolveAttack` (a finished offensive) ever clear a non-reserve platoon's `Mission`, and none of
  those runs merely because a review happened. A grep of every `platoon.State = CommanderPlatoonState.*`
  assignment in `Operations/` confirms the only per-review, unconditional overwrite was the reserve
  catch-all fixed above; `ResolveAttack`'s two `State` writes (`Operations/CommanderOperationsOffensive.cs`)
  fire once, on mission resolution, not every review, and only for a platoon whose `Mission` was just
  cleared to `null` (so no "already holding, revisited anyway" case exists there either). Picket
  detachments hold no `CommanderPlatoonState` at all (`PicketMembers` is a plain `List<Unit>`, not a
  platoon), so the question does not apply to them. No further changes were needed at either path.

Build after both changes together:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

**Self-checks**: none added. Both changes are guards/bookkeeping on existing, unchanged decision
rules — no constant was introduced or retuned (`HoldArrivedMeters`, `FormationCohesionMeters` and
`ReserveRingMeters` are untouched), and neither change is itself a threshold, price ladder or
decision table in the sense Testing rule 1 means: the catch-all fix is a state guard
(`state != Holding`) with nothing to retune into nonsense, and the `Issued`-write fix is bookkeeping
that makes an existing threshold (`FormationCohesionMeters`, exercised through `CountInCohesion`)
measure the right two positions rather than changing what "close enough" means. All ten `Check*`
self-checks (`CheckArrival`, `CheckRecipe`, `CheckOrderBook`, `CheckObservedFloor`, `CheckSizing`,
`CheckAxes`, `CheckPressure`, `CheckForwardBaseShare`, `CheckStrength`, `CheckFront`) are unmodified
and untouched by either change; none of their expected values changed. Testing rule 5's fail-proof
(plant a defect, watch a named check fail, restore, confirm byte-identical) applies to gates, hooks
and checks; since nothing here is a `Check*` self-check, gate or hook, that fail-proof does not
apply, and none is recorded. The game was not launched to verify the fix at runtime (Testing rule 2)
— the reasoning above is from the code, as this track's own rule requires when the game cannot be
run from this session.

**Note on concurrent work**: the reserve catch-all guard (the regression fix) was found already
applied in `Operations/CommanderOperationsFront.cs` when this fix cycle began working through the
file — `platoons-loop` (the track's own conductor-orchestrator loop, running concurrently in this
session) had evidently already made the identical change. This cycle verified that fix was correct
and complete on its own terms, then added the `Issued`-threading root-cause fix (not yet present) on
top of it, and confirmed both together build clean. No duplicate or conflicting edit was made.

### Post-loop fixes by the main session (2026-09-13)

Both fix cycles were spent and the execution evaluator still reported one blocking defect (B4R) and
one stalling observation. Fixed directly in the main session, build 0 warnings 0 errors:

- **B4R — reserve platoons thrashed between the reserve ring and the base centre.** The reserve
  release loop cleared every reserve platoon's mission each review; the catch-all then re-attached
  it and forced `Moving` toward the territory centre, and because the ring posts are not formation
  slots it never read as arrived again. Fix: the catch-all re-attaches a platoon already `Holding`
  without touching its state or objective (`Operations/CommanderOperationsFront.cs`, reserve
  catch-all).
- **Dead-axis stall (revised after the reviewer failed the first cut).** An axis whose platoon was wiped out kept a dissolved empty platoon forever,
  so `everyAxisManned` could never recover, and an attack whose whole force died before launch
  never resolved and blocked every later attack (one at a time). Fix: `UpdateAttacks` drops empty
  axis platoons so the axis can be re-manned, and abandons (resolves as failed) an unlaunched
  attack with platoons assigned but none alive (`Operations/CommanderOperationsOffensive.cs`).

Not addressed (recorded for the user): `ObservedFloors` never decays; `FillMissionTrucks` does not
release a truck the player hand-orders; with `minPlatoons: 1` on a point target the ordinary attack
usually launches via the 4-minute timeout rather than "every axis is up".

Reviewer follow-up on the dead-axis fix (2026-09-13, late): the first cut's abandon guard was
unreachable (`Assigned.Count > 0` cannot hold when no axis is manned, because `AssignPlatoons`
drops dead platoons from `Assigned` first), and nulling a dead axis without clearing its
`Arrived` flag let the form-up timeout launch a mission with nobody on it. Corrected: a nulled
axis also resets `Arrived`; the abandon guard is gated on `FirstGroupArrivedAt >= 0` instead of
`Assigned.Count`; and the launched-attack resolve test also fires on `totalStrength == 0`.
Attribution: the reserve `Holding` guard in the catch-all was written by the main session; the
fix-cycle-2 fixer recorded the `Issued` threading. Both stand.
