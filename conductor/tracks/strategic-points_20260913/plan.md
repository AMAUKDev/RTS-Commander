# Plan: strategic-points

**Track**: `strategic-points_20260913` · **Design**: `design.md` (approved 2026-09-13; binding — this
plan implements it and records the four places the code forced a departure from its literal
wording, with reasons) · **Executor**: one agent, tasks in order, `dotnet build GroundControlRts.csproj
-c Release` after every task (0 warnings, 0 errors). Do NOT run `build-and-install.ps1`,
`build-dev.bat` or `build-release.bat` (the game may be running). Do NOT commit or stage anything;
the user commits.

If a fresh shell lacks `NUCLEAR_OPTION_DIR`, set it to `I:\SteamLibrary\steamapps\common\Nuclear Option`
(`BUILD.md:19-39`). New `.cs` files anywhere under the repo compile automatically — the project is
an SDK-style csproj with no explicit file list (`GroundControlRts.csproj`), so a new `Points/` folder
needs no project edit.

## Goal

Give every map a spread of things worth holding — resource sites, villages, hilltops and the bases
— so the economy is map-driven instead of "mines next to base": mines can only be built on a
resource site; villages and hilltops pay whoever keeps at least two ground vehicles standing in the
ring; bases pay their holder; both commanders (enemy and the player's own AI) build their mines on
sites and garrison nearby points; and the player can watch every AI decision live in a COMMANDER LOG
window instead of reading `BepInEx\LogOutput.log`.

## Architecture

- One new **service** `Points/CommanderStrategicPointService.cs` (Advanced tier,
  `ICommanderTickPersistent` + `ICommanderResetSession`, registered once in
  `Core/CommanderModeController.cs`), split into partials by concern as the codebase does
  (`conductor/knowledge/patterns.md` "Partial classes per concern"): `…Discovery.cs` (find the
  points once per mission, budgeted per frame), `…Markers.cs` (draw them). Data lives in
  `Points/CommanderStrategicPoint.cs`.
- The **hold state machine**, the **spacing filter** and the **income sum** are pure static
  functions over ints/floats so `SelfCheck()` can drive them with synthetic data at plugin load —
  the only automated test this mod has (`.claude/CLAUDE.md` Testing rules).
- The **siting rule stays single**: `CommanderBuildPreview.Evaluate` is the one place every build in
  the mod passes through (`Economy/CommanderEconomyService.cs:922-927` says so); the mine-on-site
  and reach rules go in there, so player ghost, player click and both AI commanders obey them.
- The **garrison** is a new partial of the enemy commander,
  `Ai/CommanderEnemyCommanderGarrison.cs`, on the existing 10 s defence clock, mirroring the home
  guard in `Ai/CommanderEnemyCommanderDefence.cs`. The player-side AI gets it for free through
  `CommanderPlayerCommanderService.IsCommanded` (`Ai/CommanderPlayerCommanderService.cs:62-68`).
- The **COMMANDER LOG** is one funnel, `UI/CommanderAiLog.cs` `Note(hq, text)`, that every existing
  AI log line already routes through by way of `CommanderLabel(hq)`; it writes to the BepInEx log
  exactly as today and to a per-faction newest-first list capped at 200. The window
  `UI/CommanderAiLogUi.cs` is a copy of the ORDER OF BATTLE window's shape (`UI/CommanderUnitListUi.cs`).
- **KISS, per design**: no persistence of owners across reload, no personalities, no routing of
  attackers (they still use the game's convoy AI), no trucks. Points reset on scene change.

## Tech stack

C# (`LangVersion latest`, nullable on), .NET Framework 4.7.2, Unity IMGUI (`GUI.*`), BepInEx 5
config (`CommanderSettings.Get/Set`), Harmony (no new patches in this track — the one map-click hook
goes inside the existing `CommanderTacticalMapControlsPatch`). Height data from the existing
strategic height map (`Terrain/CommanderStrategicHeightMap.cs`, 20 m/px) through the static seams on
the SAM analyzer (`SamSites/Analysis/CommanderSamSiteAnalyzerService.cs:139-155`).

## Read first (Reuse rule 1)

- `Terrain/CommanderStrategicHeightMap.cs` whole (242 lines): `IsReady` 25, `TryStart` 31-123 (the
  GPU bake, started by the SAM analyzer — never start a second one), `TryGetHeight` 125-149,
  `TryGetHeightNearest` 151-173, `EstimateNormalY` 175-187.
- `SamSites/Analysis/CommanderSamSiteAnalyzerService.cs` 137 (`ScanQueriesPerFrame` = clamp of the
  `SAM Analyzer/RaycastsPerFrame` setting), **139-155** (the three static seams
  `TryGetStrategicTerrainHeight`, `TryGetStrategicHeightMapSize`, `EstimateStrategicTerrainNormalY` —
  use these, do not reach into the analyzer's private field), 274-298 (`TickPersistent` state
  machine: Waiting → Sampling → Coverage, one batch per frame).
  `SamSites/Analysis/CommanderSamSiteAnalyzerSampling.cs` 10-25 (`TryStart`: waits for
  `MissionManager.IsRunning`, 8 s after level load, and `LoadedMapSettings`), **44-66**
  (`SampleTerrainBatch`: `regionsPerFrame = Clamp(ScanQueriesPerFrame / 16, 1, 16)` — the per-frame
  budget shape to copy), 104-146 (`ExtractRegionCandidates`: how a grid is walked with
  `TryGetHeightNearest`), 148-165 (`SampleAverageHeight`: the "mean of a ring" helper — copy its
  four-compass-point shape for the hilltop prominence test).
- `Economy/CommanderBuildPreview.cs` 94-127 (`Tick`, `IsSiteAllowed`), **129-196** (`Evaluate` — the
  one siting rule; note line 147: the radius check is skipped when `hq == null`, which is exactly
  the "base-radius check disabled" the design asks for during discovery), 289-318
  (`IsInsideBuildRadius`), 638-679 (`SelfCheck` shape).
- `Economy/CommanderEconomyService.cs` 44-46 (`IncomeIntervalSeconds = 15`), 98-108 (`mineLevels`,
  `preview`), 171-179 (`IsCommanderBuilt`, `IsBuiltMine`), 295-298 (`GetMineIncomePerMinute`),
  318-375 (`SelfCheck` — the ladder-monotonic style), 389-401 (`FriendlyIncomePerMinute`),
  434-449 (`TickPersistent`), 458-486 (`ResetSession`), 541-633 (`TryPlaceBuildingFromWorld` — the
  mine branch at 619-633), **743-774 (`PayIncome`)**, **805-816 (`SpawnMine`)**, 909-947
  (`SpawnBuilding` — "every build path in the mod ends here"), 986-997 (`CountMines`),
  1053-1109 (`ResolveDefinition`).
- `Economy/CommanderEconomyServiceEnemy.cs` 48-63 (`ReviewEnemies`), 65-116 (`ReviewEnemy`),
  **150-199 (`GetEnemyBuildReserve`)**, **201-227 (`TryBuildEnemyEconomy`)**, **418-431
  (`TryBuildEnemyMine`)**, 624-687 (`TryFindEnemyBuildSite`, `TryPickEnemyBuildSite` — the
  150-350 m ring around an owned building that the design calls the problem).
- `Economy/CommanderEconomyServiceCatalog.cs` 279-292 (`GetCategoryLabel`: CIV, FAC, RDR, DEP, HGR,
  DEF, AMMO).
- `Ai/CommanderEnemyCommanderDefence.cs` whole (454 lines): class remarks 12-28 (**why pinning is
  `SetDestination(post, playerCommand: true)` and release is clearing `commandedDestination`** —
  the garrison must use the same mechanism or its vehicles walk off at the enemy), 36 (10 s clock),
  77-78 (`CommandedDestinationRef`), 85-102 (`IsDefendingUnit`), 121-141 (`ReviewDefences` loop),
  143-190 (`ReviewDefence`), 251-296 (`EnsureDefencePosts` — ring posts snapped to terrain,
  sea-level skip), 298-321 (`PruneDefenders`), 327-355 (`ReleaseSurplusDefenders`), **357-390
  (`RecruitDefenders` — the candidate filter to copy)**, 392-395 (`DefencePriority`), 432-453
  (`CheckDefencePosture`: read constants into locals so the compiler cannot fold the check).
- `Ai/CommanderEnemyCommanderService.cs` 24 (class line: `ICommanderTickPersistent,
  ICommanderResetSession`), 65 (`states`), 182-250 (`TickPersistent`; **202-205 the defence clock**),
  252-271 (`ResetSession`), 644-654 (`IsCombatVehicle`), **748-789 (`CommanderState`)**.
- `Ai/CommanderEnemyCommanderGround.cs` 53-69 (`reconUnits`: a recon vehicle is
  `definition.vehicleType == VehicleType.RDR`), 94-99 (the `HasPlayerOrder` skip).
- `Units/CommanderMoveService.cs` 1007-1020 (`HasPlayerOrder` and its remarks).
- `Ai/CommanderCaptureService.cs` 168-191 (`FactionRegistry.airbaseLookup` walk), 425-463
  (`UpdateDiscovery`: `FastMath.InRange(unit.transform.GlobalPosition(), center, range)` — the ring
  count idiom), **490-504 (`GetTerritoryCenter`)**, 668-697 (`CollectCaptureSquad` and the
  `IsDefendingUnit` skip), 809-857 (`CaptureTarget`: live ownership read from `Airbase.CurrentHQ`
  and `capture.controlBalance`, never stored).
- `Ai/CommanderPlayerCommanderService.cs` 62-68 (`IsCommanded`), **137-152 (`CommanderLabel`)**,
  154-205 (`SelfCheck` with `failures` / `Expect` — the shape every new self-check copies).
- `Units/CommanderWorldMarkerRenderer.cs` 21-24 (base colours), 47-126 (`Draw`), **316-358
  (`DrawCaptureTargets`: the world-vs-map split with `DynamicMap.mapMaximized` and
  `map.TryWorldToMapScreen`)**, 360-375 (`BuildCaptureBar`: ASCII only), 387-396 (`DrawWorldPoint`).
- `UI/CommanderUiTheme.cs` 346-372 (`DrawWorldMarker`: bracket built from `Bar(...)`
  `GUI.DrawTexture` calls), 374-377 (`Bar`, private), 380 (`DrawWorldLabel`, internal).
- `Map/CommanderTacticalMapService.cs` 396-413 (`TryWorldToMapScreen`).
  `Map/CommanderTacticalMapPatches.cs` 113-147 (`TrackIconClick`: click resolves on release with a
  6 px slop; `ClickNearestIcon` 150-183).
- `Units/CommanderMarkerService.cs` 288-308 (`TryGetMarkerUnitAt`: nearest-hit pick) and
  `Units/CommanderMarkerView.cs` 100-115 (`TryHit`: visible + rect contains) — the hit-test shape
  for clicking a point.
- `Core/CommanderInputController.cs` 192-237 (`HandlePrimaryClick`; **206-214 the empty-click
  branch**).
- `UI/CommanderOverlayUiSelection.cs` 104-140 (`DrawSelectionBar`; returns at 107-110 when nothing is
  selected). `UI/CommanderOverlayUi.cs` 285-294 (selection bar rect), 308-339 (`ContainsScreenPoint`),
  356-400 (`Draw`: world markers first, then HUD boxes).
- `UI/CommanderUnitListUi.cs` whole (364 lines) — the window to copy for the COMMANDER LOG: 13-15
  (window id, refresh, row height), 38-56 (`Visible`/`Toggle`/`Hide`/`ResetPosition`/
  `ContainsScreenPoint`), 58-83 (`Tick`: size, `ClampWindow`, `IsDueRealtime` refresh), 85-93
  (`Draw`), 143-182 (`DrawBattleLog`: timestamp column + text rows in a scroll view — the body
  shape), 213-248 (`DrawWindow`: help button, X, filter tabs), 317-325 (`DrawFilterTab`).
- `UI/CommanderOverlayUiPanel.cs` 53-61 (the ORDER OF BATTLE button — the COMMANDER LOG button sits
  right under it), 141-147 (`settingsY`/`experimentalY` arithmetic that bounds the button column).
- `UI/CommanderOverlayUi.cs` 38 / 157 (`unitListUi` field and construction), 179 / 200 (`Hide` on
  activate/deactivate), 296-299 (`Tick`), 331 (hit-test row), 435 (`Draw`).
- `UI/CommanderOverlayUiSettings.cs` 45-76 (tab row: five tabs, `tabWidth = (width - 30) / 5`,
  dispatch by `settingsTab`), 137-268 (`DrawGameplaySettings` — read its comment at 147-149: the
  COMMAND box already reaches 784 of a 790-tall window with help open), **271-283
  (`DrawRadiusSlider`)**, 355-367 (`DrawCameraSlider(y, label, value, min, max, format, suffix)`).
- `Units/CommanderAlertService.cs` 19-23 (`MaxLogEntries = 120`, `log`), 272-279 (`AddLog`:
  newest-first `Insert(0)`, trim the tail), 306-322 (`LogEntry`, `Timestamp` = `mm:ss` of
  `Time.timeSinceLevelLoad`).
- `Core/CommanderSettings.cs` 87-88 (`BuildRadiusKm` and its comment), 99-103 (the last Gameplay
  entries), 140-217 (warm-up list; `_ = SamScanQueriesPerFrame;` at 210).
- `Core/CommanderModeController.cs` 73-75 (`samSiteService` register), 80-81 (`economyService`
  register), 82 (enemy commander), 85 (player commander).
- `Core/CommanderPlugin.cs` 31-39 (self-check calls; `CommanderCaptureService.SelfCheck();` at 36).
- `Core/CommanderGameAccess.cs` 17-20 (`GetLocalHq`), 398-454 (`SnapToTerrain` — a raycast per
  call, 13 probes worst case: **do not call it per grid sample**, use the height map), 483-486
  (`IsBelowSeaLevel`), 528-536 (`GetUnitCommand`).
- `Core/CommanderScheduler.cs` 19 (`Stagger`), 30 (`IsDue` — scaled time, for all game logic here),
  36 (`IsDueRealtime` — UI refresh only).
- `conductor/tracks/player-ai-commander_20260913/plan.md` — the executor-notes style expected here,
  and its T3 fail-proof record as the model for every self-check task below.

## Design facts fixed by design.md (and four recorded departures)

All numbers below are the design's defaults. The executor uses these and invents none.

| Constant / setting | Default | Where | What it means and why |
|---|---|---|---|
| `Points/FillGridMeters` | 8000 | config only | Grid cell for generated resource-site fill: one site per ~8 km cell that has none, so every map has a spread |
| `Points/SiteMinSpacingMeters` | 2000 | config only | Two resource sites never closer than this; the newer one is dropped |
| `Points/VillageClusterMeters` | 300 | config only | Civilian buildings this close chain into one village |
| `Points/VillageMinBuildings` | 4 | config only | A cluster smaller than this is a farm, not a village |
| `Points/VillageRadiusMeters` | 400 | config only | Village control ring, around the cluster centroid |
| `Points/HilltopGridMeters` | 1000 | config only | Height-map sample grid for the hilltop scan |
| `Points/HilltopRingMeters` | 1500 | config only | A sample must be the highest within this ring |
| `Points/HilltopProminenceMeters` | 60 | config only | …and at least this far above the ring's mean height |
| `Points/HilltopRadiusMeters` | 300 | config only | Hilltop control ring |
| `Points/HilltopVillageExclusionMeters` | 1000 | config only | No hilltop within this of a village (the village is the point there) |
| `Points/MaxNonBasePoints` | 30 | config only | Cap on sites + villages + hilltops per map |
| `Points/PointMinSpacingMeters` | 1500 | config only | No two non-base points closer than this (earlier wins) |
| `Points/AirbaseExclusionMeters` | 2000 | config only | No non-base point within this of an airbase centre |
| `Points/MinGarrison` | 2 | slider | Ground vehicles a faction needs inside a ring to count as present |
| `Points/HoldSeconds` | 60 | slider | Cumulative seconds alone with ≥ MinGarrison before a point flips |
| `Points/BaseIncomePerMinute` | 30 | slider | Per airbase held, paid on the 15 s income tick |
| `Points/VillageIncomePerMinute` | 10 | slider | Per village held |
| `Points/HilltopIncomePerMinute` | 5 | slider | Per hilltop held |
| `Economy/GoldMineIncomePerMinute` | 20 (existing) | slider (existing meaning; fourth income slider) | × mine level, unchanged (`Economy/CommanderEconomyService.cs:295-298`) |
| `HoldCheckSeconds` | 5 | `const` in service | Scaled-clock cadence of the ring count |
| `IncomeIntervalSeconds` | 15 (existing) | `Economy/CommanderEconomyService.cs:44` | Points share the mines' tick |
| `DefenceReviewIntervalSeconds` | 10 (existing) | `Ai/CommanderEnemyCommanderDefence.cs:36` | Garrison review shares the home-guard clock |
| `GarrisonReachMeters` | 12000 | `const` in garrison partial | Control points this far from a held base are garrisoned, nearest first |
| garrison size | `MinGarrison + 1` | derived | One spare so a single loss does not drop the point |
| `MaxGarrisonedPoints` | 3 | `const` in garrison partial | Per commander, so the home guard is never starved |
| `Economy/BuildRadiusKm` | 2.5 (existing) | `Core/CommanderSettings.cs:88` | Mine reach = this from a held base **or** a currently garrisoned control point |
| `AiLogCapacity` | 200 | `const` in `UI/CommanderAiLog.cs` | Decisions kept per faction |
| `MineSnapMeters` | 1000 | config only, **planner-chosen** | See departure 4 |

Every constant gets a `<summary>` saying what the number is and why it is that value
(`.claude/CLAUDE.md` line 8-9). Comments explain why, never what.

**Which building types are which.** From `GetCategoryLabel`
(`Economy/CommanderEconomyServiceCatalog.cs:279-292`): **industrial = `BuildingType.FAC` (INDUSTRY)
and `BuildingType.AMMO` (AMMUNITION — the storage class the design names)**; **civilian =
`BuildingType.CIV`**. `RDR`, `DEP`, `HGR`, `DEF` are military infrastructure and count as neither
(a vehicle depot spawns units and must never become a mine site). Buildings the mod itself built
(`CommanderEconomyService.IsCommanderBuilt`, `Economy/CommanderEconomyService.cs:171-174`) are
skipped: a mod-built mine wears an industrial prefab and would otherwise seed a second site on top
of itself.

**Ownership reads.** Base owner is `Airbase.CurrentHQ`, read live (design §1 "never stored"), exactly
as `Ai/CommanderCaptureService.cs:439, 482` do. A resource site's owner is `Mine.NetworkHQ` of the
mine standing on it; the site is free when `Mine` is null or `disabled`. Village and hilltop owner is
the hold state machine's `Owner`.

**Server guard.** Discovery, the hold tick and income run only when `CommanderGameAccess.GetLocalHq()`
is non-null and `IsServer` (Testing rule 3; same stance as `PayIncome`,
`Economy/CommanderEconomyService.cs:762-766`). A pure client therefore draws no points; that is the
design's "host only".

- **Departure 1 — a resource site on an existing building sits *beside* it, not on it.** Design §1
  says every industrial building "becomes a site" and §2 says a mine is built "at a site". A mine
  cannot be spawned on top of the building: `Evaluate` rejects any site whose footprint overlaps a
  live `Unit` (`Economy/CommanderBuildPreview.cs:159-179`). So discovery takes the building as the
  anchor and probes a ring 150-350 m out (the same ring `TryPickEnemyBuildSite` uses,
  `Economy/CommanderEconomyServiceEnemy.cs:679-686`, up to `EnemySiteAttempts = 12` tries, line 15)
  for the first position where `IsSiteAllowed(mineDefinition, candidate, hq: null, …)` passes, and
  stores **that** as the site position. No legal spot → no site for that building (logged once in
  the discovery table as "dropped: no clear ground").
- **Departure 2 — the Points sliders live on a sixth settings tab, not inside Gameplay.** Design §4
  says "Settings > Gameplay gains a Points block". The Gameplay tab's COMMAND box already ends at
  784 px of a 790 px window with the help overlay open (`UI/CommanderOverlayUiSettings.cs:147-151`);
  six 38 px slider rows do not fit. The block becomes a `POINTS` tab drawn by the same
  `DrawRadiusSlider` / `DrawCameraSlider` helpers, so the sliders look and behave as designed, one
  click further right. Behaviour is identical; only the tab is new.
- **Departure 3 — the "selection bar readout" for a point is a card, not a selection.** Design §4:
  "Clicking a point selects it like a building and shows the readout in the selection bar."
  `CommanderSelectionService` selects `Unit`s only; a village or hilltop has no `Unit`. A site with a
  mine already selects the mine (`IsCommanderBuilt` makes it clickable). For the other two kinds the
  click sets `CommanderStrategicPointService.FocusedPoint`, and `DrawSelectionBar` draws a
  STRATEGIC POINT card in the selection bar's place while nothing is selected. Clicking empty ground
  clears it, as it clears a selection. Same place on screen, same information; no fake `Unit`.
- **Departure 4 — one number the design does not give: how far a mine ghost snaps.** §2: "the
  player's ghost snaps to the nearest free site within reach and refuses elsewhere". Without a
  cursor distance the ghost would jump across the map to a site 40 km away. `Points/MineSnapMeters`
  (default 1000, config only) bounds the snap; a cursor further than that from every free reachable
  site is "elsewhere" and is refused with `Blocked: a gold mine has to stand on a resource site.`
  Flagged for the user in the acceptance section.

## The call-site ledger (the plan evaluator checks every row)

Every existing line the track changes, and what it becomes. New files are not in this table.

| # | Site today | Becomes |
|---|---|---|
| 1 | `Economy/CommanderBuildPreview.cs:135-157` `Evaluate`: radius check at 147 `if (hq != null && !IsInsideBuildRadius(hq, target, radiusKm))` | For a mine definition, first `target` is replaced by the snapped free site (`CommanderStrategicPointService.Instance?.TrySnapMineSite(target, out site)`; failure → `reason = "Blocked: a gold mine has to stand on a resource site."`, return false). The radius test becomes `IsInsideBuildRadius(hq, target, radiusKm) \|\| (mine && CommanderStrategicPointService.Instance?.IsInsideGarrisonedPointReach(hq, target, radiusKm) == true)`. Non-mine buildings: unchanged. |
| 2 | `Economy/CommanderBuildPreview.cs:109-113` `Tick`: `site = SnapToTerrain(ground)` then `Evaluate(definition, site, …)` and `Draw(definition, site)` | For a mine definition, after the snap-to-terrain, `if (TrySnapMineSite(site, out snapped)) site = snapped;` before `Evaluate`/`Draw`, so the ghost sits on the site it will land on. `Evaluate` re-snaps idempotently (a site position snaps to itself). |
| 3 | `Economy/CommanderEconomyService.cs:805-816` `SpawnMine(hq, position, randomRotation)` | `if (CommanderStrategicPointService.Instance?.TrySnapMineSite(position, out GlobalPosition site) != true) return null; position = site;` before `SpawnBuilding`; after a successful spawn, `CommanderStrategicPointService.Instance.AttachMine(site, mine)`. |
| 4 | `Economy/CommanderEconomyService.cs:108` `private readonly CommanderBuildPreview preview` | Two forwarding members added beside `IsBuiltMine` (176-179): `internal bool IsSiteAllowed(BuildingDefinition d, GlobalPosition p, FactionHQ? hq, out string reason) => preview.IsSiteAllowed(d, p, hq, out reason);` and `internal BuildingDefinition? MineDefinition => ResolveDefinition(CommanderBuildKind.Mine);`. Discovery uses these (with `hq: null`) so there is one preview and one siting rule. |
| 5 | `Economy/CommanderEconomyService.cs:743-749` `PayIncome`: `HoldFundsInTreasury(); if (mineLevels.Count == 0) return;` | `HoldFundsInTreasury(); CommanderStrategicPointService.Instance?.PayPointIncome(IncomeIntervalSeconds / 60f); if (mineLevels.Count == 0) return;` — points pay first because a faction with no mines still holds bases. |
| 6 | `Economy/CommanderEconomyService.cs:986-997` `CountMines(hq)` (private) | Unchanged; a sibling `internal float GetMineIncomePerMinute(FactionHQ hq)` is added right after it (same loop, sums `GetMineIncomePerMinute(entry.Value)`), so the COMMANDER LOG header can show mine income per faction. `FriendlyIncomePerMinute` (389-401) is left as is — it is the BUILD window's readout for the local faction. |
| 7 | `Economy/CommanderEconomyServiceEnemy.cs:177-181` `GetEnemyBuildReserve`: `if (service.CountMines(hq) < target) return MineBuildCost;` | `if (service.CountMines(hq) < target && CommanderStrategicPointService.Instance?.TryPickFreeReachableSite(hq, out _) == true) return MineBuildCost;` — no reachable site, no saving for a mine; fall through to the factory (design §3). |
| 8 | `Economy/CommanderEconomyServiceEnemy.cs:210-214` `TryBuildEnemyEconomy` mine branch | Same guard as row 7 (`&& TryPickFreeReachableSite(hq, out _) == true`); the comment at 204 already says "Same order as GetEnemyBuildReserve". |
| 9 | `Economy/CommanderEconomyServiceEnemy.cs:418-431` `TryBuildEnemyMine`: `TryFindEnemyBuildSite(hq, mine, ref site)` | `CommanderStrategicPointService.Instance?.TryPickFreeReachableSite(hq, out GlobalPosition site) == true && SpawnMine(hq, site, randomRotation: true) != null`. Summary rewritten: "Builds on the nearest free resource site this commander can reach." `TryFindEnemyBuildSite` keeps its other callers (factory, defence) and is not touched. |
| 10 | `Ai/CommanderEnemyCommanderService.cs:202-205` `if (IsDue(ref nextDefenceAt, DefenceReviewIntervalSeconds)) { ReviewDefences(localHq); }` | `{ ReviewDefences(localHq); ReviewGarrisons(localHq); }` — same clock, garrison after the home guard so the guard has first pick. |
| 11 | `Ai/CommanderEnemyCommanderService.cs:748-789` `CommanderState` | Gains `internal readonly Dictionary<Unit, CommanderStrategicPoint> Garrison = new();` (unit → the point it holds) with a `<summary>`. |
| 12 | `Ai/CommanderEnemyCommanderService.cs:252-271` `ResetSession` | Also clears the garrison partial's scratch lists (`garrisonCandidates`, `staleGarrison`). `states.Clear()` already drops the per-HQ dictionaries. |
| 13 | `Ai/CommanderEnemyCommanderDefence.cs:365-379` `RecruitDefenders` candidate filter | Adds `&& !IsGarrisonUnit(unit)` with a one-line comment: a vehicle standing on a control point is spoken for. |
| 14 | `Ai/CommanderCaptureService.cs:689-692` `CollectCaptureSquad` filter | Adds `&& !CommanderEnemyCommanderService.IsGarrisonUnit(unit)` beside the existing `IsDefendingUnit` skip; the comment at 684-688 already explains the reason. |
| 15 | 24 lines of `CommanderPlugin.Log.LogInfo($"{CommanderPlayerCommanderService.CommanderLabel(hq…)} …")` — `Ai/CommanderCaptureService.cs:549-550, 589-591`; `Ai/CommanderEnemyCommanderAir.cs:262-263, 390, 433-434, 507-508`; `Ai/CommanderEnemyCommanderDefence.cs:154-157`; `Ai/CommanderEnemyCommanderGround.cs:223-224`; `Ai/CommanderEnemyCommanderService.cs:301-303, 379-381, 421-423, 471-472, 502-503`; `Economy/CommanderEconomyServiceEnemy.cs:110-111, 143-145, 246-247, 268-269, 429, 451-452, 472-475, 487, 511-513`; `Units/CommanderRepairService.cs:185-186` | Each becomes `CommanderAiLog.Note(hq, $"…rest of the string…")` (the label is prepended inside `Note`); the one site with a detail, `Service.cs:380` `CommanderLabel(hq, GetPlanLabel(buyPlan))`, becomes `CommanderAiLog.Note(hq, $"bought …", GetPlanLabel(buyPlan))`. The text after the label is byte-identical. `Defence.cs:154-157` (a ternary inside one `LogInfo`) becomes one `Note` with the ternary on the text. |
| 16 | `Units/CommanderAlertService.cs:321` `Timestamp => $"{FloorToInt(MissionTime / 60f):00}:{FloorToInt(MissionTime % 60f):00}"` | `Timestamp => CommanderAiLog.FormatMissionTime(MissionTime);` — the formatter MOVES to the new file (Reuse rule 3), pointer comment left here. |
| 17 | `Core/CommanderSettings.cs:103` (after `SamScanQueriesPerFrame`) and `:210` (warm-up `_ = SamScanQueriesPerFrame;`) | 18 new `Points` entries (table above; `MinGarrison` is `int`, the rest `float`) with `Get`/`Set` pairs, and 18 `_ = …;` touches after line 210. |
| 18 | `Core/CommanderModeController.cs:73-75` (`samSiteService` register) … `:80-81` (`economyService` register) | Between them: `services.Register(new CommanderStrategicPointService(), CommanderTier.Advanced);` with a comment: after the SAM analyzer whose height map it waits for, before the economy so the hold state is fresh when income is paid. |
| 19 | `Core/CommanderPlugin.cs:36` `CommanderCaptureService.SelfCheck();` | `CommanderStrategicPointService.SelfCheck();` added on the next line. |
| 20 | `UI/CommanderOverlayUiPanel.cs:53-61` ORDER OF BATTLE button, then `y += 42f;` | A second 34 px button `COMMANDER LOG` (`aiLogUi.Visible ? SelectedButton : PrimaryButton`, click → `aiLogUi.Toggle()`) and another `y += 42f;`. Arithmetic (help closed, 760 px panel): today's column ends at y = 466 and the helper text needs `experimentalY - y ≥ 36` with `experimentalY = 574`; after the button y = 508 and 574 − 508 = 66 ≥ 36. Fits. |
| 21 | `UI/CommanderOverlayUi.cs:38` field, `:157` construction, `:179` and `:200` `unitListUi.Hide()`, `:296-299` `Tick`, `:331` hit-test row, `:435` `Draw` | A parallel `aiLogUi` (`CommanderAiLogUi`) line beside each: field, `aiLogUi = new CommanderAiLogUi();`, `aiLogUi.Hide();` ×2, `aiLogUi.Tick();`, `\|\| aiLogUi.ContainsScreenPoint(screenPoint)`, `aiLogUi.Draw();`. No `Show*` setting — the window is opened from the panel and has no hide toggle (design names none). |
| 22 | `UI/CommanderOverlayUiSettings.cs:46-51` five tabs at `tabWidth = (width - 30f) / 5f`; `:54-73` dispatch | `/ 6f`, a sixth `DrawSettingsTab(new Rect(12f + tabWidth * 5f, y, tabWidth, 32f), "POINTS", 5);`, and `else if (settingsTab == 5) { DrawPointsSettings(y); }` before the final `else` (which is CONTROLS, tab 2). |
| 23 | `Units/CommanderWorldMarkerRenderer.cs:61` `DrawCaptureTargets(camera);` | followed by `CommanderStrategicPointService.Instance?.DrawMarkers(camera);` |
| 24 | `Core/CommanderInputController.cs:206-214` `if (clicked == null) { lastClickedUnit = null; if (!additive) selectionService.DeselectAll(); return; }` | Before `DeselectAll`: `if (CommanderStrategicPointService.Instance?.TryFocusPointAt(mousePosition) == true) return;` and after `DeselectAll()`: `CommanderStrategicPointService.Instance?.ClearFocus();`. |
| 25 | `Map/CommanderTacticalMapPatches.cs:146` `ClickNearestIcon(map);` | `if (CommanderStrategicPointService.Instance?.TryFocusPointAt(Input.mousePosition) != true) ClickNearestIcon(map);` |
| 26 | `UI/CommanderOverlayUiSelection.cs:104-110` `DrawSelectionBar`: `if (count == 0) return;` | `if (count == 0) { DrawFocusedPointCard(); return; }` — the card uses `selectionBarRect`. `UI/CommanderOverlayUi.cs:289` height formula gets a third case: no selection but a focused point → `74f + 44f`. `:327` hit-test `(showSelectionBar && selectionService.SelectedUnits.Count > 0 && …)` → `(showSelectionBar && (selectionService.SelectedUnits.Count > 0 \|\| CommanderStrategicPointService.Instance?.FocusedPoint != null) && …)`. |
| 27 | `CHANGELOG.md:228` `### Added` under `## Unreleased` (first bullet at 230) | New bullet at the top of that list. `README.md:628-630` (end of `### Player commander`, before `## Unit systems`) | New `## Strategic points` section inserted before `## Unit systems`. |
| 28 | `I:\SteamLibrary\steamapps\common\Nuclear Option\BepInEx\config\BepInEx.cfg:48` `Enabled = false` under `[Logging.Console]` | `Enabled = true`. Not repo code. |

Not changed, on purpose: `Economy/CommanderEconomyServiceEnemy.cs:624-687` (`TryFindEnemyBuildSite` /
`TryPickEnemyBuildSite`) — factories, radars and defences still go beside owned buildings; only the
mine moves to sites. `Ai/CommanderEnemyCommanderDefence.cs` home-guard sizes and posts. Kill bounties
(`killReward`). The convoy AI for attackers.

## Tasks

- [x] **T1 — Points settings.** `Core/CommanderSettings.cs`: after `SamScanQueriesPerFrame` (line 103)
  add the 18 `Points` entries from the table (section `"Points"`, keys exactly as in the table:
  `FillGridMeters`, `SiteMinSpacingMeters`, `VillageClusterMeters`, `VillageMinBuildings` (int),
  `VillageRadiusMeters`, `HilltopGridMeters`, `HilltopRingMeters`, `HilltopProminenceMeters`,
  `HilltopRadiusMeters`, `HilltopVillageExclusionMeters`, `MaxNonBasePoints` (int),
  `PointMinSpacingMeters`, `AirbaseExclusionMeters`, `MinGarrison` (int), `HoldSeconds`,
  `BaseIncomePerMinute`, `VillageIncomePerMinute`, `HilltopIncomePerMinute`, `MineSnapMeters`) as
  `Get`/`Set` pairs in the style of line 88, each group under a one-line comment saying what it
  tunes (discovery spacing is config-file-only; garrison/hold/income are on the POINTS tab). Add
  `_ = …;` touches for all 18 after line 210 (`_ = SamScanQueriesPerFrame;`). Build.

- [x] **T2 — Data type, service skeleton, registration.** Create `Points/CommanderStrategicPoint.cs`:
  `internal enum StrategicPointKind { Site, Village, Hilltop, Base }` and
  `internal sealed class CommanderStrategicPoint` with `Kind`, `Position` (`GlobalPosition`, on
  terrain), `Radius` (m), `Label` (e.g. `"SITE 3"`, `"VILLAGE 2"`, `"HILL 5"`, or the airbase name via
  `CommanderCaptureService.GetAirbaseLabel`, `Ai/CommanderCaptureService.cs:803-807`), `Airbase?`
  (Base only), `Unit? Mine` (Site only), and a `HoldState Hold` struct (`int OwnerIndex` /
  `int CandidateIndex` as HQ registry indices, −1 = none; `float Progress` seconds; `bool Contested`)
  — ints, not `FactionHQ`, so the state machine is pure (T5). Owner is exposed as
  `FactionHQ? GetOwner()`: Base → `Airbase.CurrentHQ`; Site → `Mine == null || Mine.disabled ? null :
  Mine.NetworkHQ`; else the HQ at `OwnerIndex` in the service's `hqOrder` list. Every field has a
  `<summary>`.
  Create `Points/CommanderStrategicPointService.cs`: `internal sealed partial class
  CommanderStrategicPointService : ICommanderTickPersistent, ICommanderResetSession`, file-scoped
  namespace, class `<summary>` (what a point is, what pays, what does not — one paragraph — and a
  `<remarks>` "ponytail:" note that owners are not persisted across reload, same stance as mine
  levels at `Economy/CommanderEconomyService.cs:31-35`). Members: `internal static
  CommanderStrategicPointService? Instance` set in the constructor (as `Economy/CommanderEconomyService.cs:124-128`);
  `private readonly List<CommanderStrategicPoint> points`; `internal IReadOnlyList<CommanderStrategicPoint> Points`;
  `private readonly List<FactionHQ> hqOrder` (the registry order snapshot the hold indices refer to,
  refreshed each hold tick); `private DiscoveryState discovery` (enum `Waiting, Sites, Villages,
  Hilltops, Bases, Done` — the SAM analyzer's state-per-batch shape,
  `SamSites/Analysis/CommanderSamSiteAnalyzerService.cs:274-298`); `private float nextHoldAt =
  CommanderScheduler.Stagger("points.hold", HoldCheckSeconds)`; `private const float HoldCheckSeconds
  = 5f` with `<summary>`. `TickPersistent()`: `FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
  if (localHq == null || !localHq.IsServer) return;` then `if (discovery != Done) { StepDiscovery();
  return; }` then `if (CommanderScheduler.IsDue(ref nextHoldAt, HoldCheckSeconds)) TickHold();`
  (both bodies land in T3-T5; this task leaves them as empty private methods with a `// T3` /
  `// T5` marker that T3/T5 remove). `ResetSession()`: clear `points`, `hqOrder`, `discovery =
  Waiting`, re-stagger `nextHoldAt`, `FocusedPoint = null`. Register per ledger row 18. Build.

- [x] **T3 — Discovery, part 1: resource sites (existing industry + grid fill) and the spacing filter,
  with self-check and fail-proof.** Create `Points/CommanderStrategicPointDiscovery.cs` (partial).
  `StepDiscovery()` runs one state per call:
  - `Waiting`: return unless `MissionManager.IsRunning` and
    `CommanderSamSiteAnalyzerService.TryGetStrategicHeightMapSize(out Vector2 mapSize)` is true
    (`SamSites/Analysis/CommanderSamSiteAnalyzerService.cs:146-150`) and
    `CommanderEconomyService.Instance?.MineDefinition != null` (ledger row 4). Then → `Sites`.
    Comment: the height map is baked by the SAM analyzer (`…Sampling.cs:10-25`); this service never
    starts a bake of its own.
  - `Sites`: **existing industry** — `Building[] buildings = UnityEngine.Object.FindObjectsOfType<Building>()`
    (one-off; precedent `Economy/CommanderEconomyServiceEnemy.cs:611` does the same per review), keep
    those with `definition is BuildingDefinition d && (d.buildingType == BuildingType.FAC ||
    d.buildingType == BuildingType.AMMO)` and `!CommanderEconomyService.IsCommanderBuilt(unit)`; for
    each, departure 1: probe a ring 150-350 m (`Random.Range` angle/distance as
    `…Enemy.cs:679-686`) up to 12 times for the first `economy.IsSiteAllowed(mineDefinition,
    candidate, null, out _)` (radius check off because `hq` is null — cite
    `Economy/CommanderBuildPreview.cs:147`), snap it with `CommanderGameAccess.SnapToTerrain` (one
    raycast per accepted site, fine), skip `IsBelowSeaLevel`. **Grid fill** — walk cells of
    `FillGridMeters` over `mapSize` (`-mapSize.x/2 … +mapSize.x/2`, the frame `TryGetHeight` uses,
    `Terrain/CommanderStrategicHeightMap.cs:127-138`); a cell with no site already inside it gets up
    to 12 random probes within the cell; a probe qualifies when
    `TryGetStrategicTerrainHeight(x, z, out h)` gives `h > 1f`,
    `EstimateStrategicTerrainNormalY(x, z, 40f) ≥ 0.94f` (≈ 20°, the "flat" the design asks for —
    constant `FlatNormalY` with summary), and `IsSiteAllowed(mineDefinition, candidate, null, …)`
    (this is also the off-road check). Budget: `FillCellsPerFrame = 4` (const, summary: 12 probes ×
    ~5 raycasts each is the frame cost); keep a `fillIndex` and return until the grid is done. Then
    the **spacing pass** (pure): `internal static void ApplySpacing(List<CommanderStrategicPoint>
    candidates, float minSpacing, IReadOnlyList<GlobalPosition> exclusionCentres, float
    exclusionRadius, int cap)` — walks in order, drops any candidate within `minSpacing` of an
    already-kept one or within `exclusionRadius` of an exclusion centre, stops keeping at `cap`;
    horizontal distance only. Sites use `SiteMinSpacingMeters`, airbase centres from
    `FactionRegistry.airbaseLookup` (`Ai/CommanderCaptureService.cs:168-170`) with
    `AirbaseExclusionMeters`, cap `MaxNonBasePoints`. Existing-industry sites go first in the list
    so the fill yields to them. → `Villages`.
  - `Villages`, `Hilltops`, `Bases`: stubs returning to the next state (filled in T4).
  - `Done`: log the table (T4).
  **Self-check (discovery spacing invariants)**: `internal static void SelfCheck()` in the service
  file, `failures` / `Expect` shape from `Ai/CommanderPlayerCommanderService.cs:162-205`, category
  string `"Strategic points self-check FAILED: …"`. Synthetic list of 6 points along a line 1000 m
  apart plus one 500 m from an airbase centre; `ApplySpacing(list, 1500f, [base], 2000f, 3)`:
  expect count 3 ("cap holds"), first point kept and second dropped ("newer of a close pair is
  dropped"), the base-adjacent one dropped ("nothing inside the airbase exclusion"), and every kept
  pair ≥ 1500 m apart ("kept points respect spacing"). Register per ledger row 19. Build.
  **Testing rule 5 fail-proof**: record SHA256 (`Get-FileHash -Algorithm SHA256`) of
  `Points/CommanderStrategicPointDiscovery.cs`; plant the spacing test as
  `distance < minSpacing * 0.5f`; build (must still succeed); read the path and name the cases that
  fail ("newer of a close pair is dropped", "kept points respect spacing"); restore; build; SHA256
  byte-identical. Write it in the notes.

- [x] **T4 — Discovery, part 2: villages, hilltops (budgeted), bases, the table; with self-check and
  fail-proof for the two threshold comparisons.**
  - `Villages`: from the same `Building[]` (kept from T3 in a field, cleared at `Done`), take
    `BuildingType.CIV` not `IsCommanderBuilt`; single-link clustering: for each building, join the
    first cluster whose *any* member is within `VillageClusterMeters` (O(n²) once; n is a few
    hundred — say so in a comment). Pure: `internal static bool QualifiesAsVillage(int
    buildingCount, int minBuildings) => buildingCount >= minBuildings;` — a cluster whose size
    passes `QualifiesAsVillage(clusterSize, VillageMinBuildings)` → a `Village` at the centroid
    (`SnapToTerrain` once), `Radius = VillageRadiusMeters`. → `Hilltops`.
  - `Hilltops`: sample grid `HilltopGridMeters` over `mapSize` using
    `TryGetStrategicTerrainHeight`; pure: `internal static bool QualifiesAsHilltop(float
    sampleHeight, float ringMeanHeight, float prominenceMeters, bool isHighestInRing) =>
    isHighestInRing && sampleHeight - ringMeanHeight >= prominenceMeters;` (boundary: a sample
    exactly `prominenceMeters` above the ring mean qualifies). A sample is a hilltop when `h > 1f`
    and `QualifiesAsHilltop(h, mean(ring), HilltopProminenceMeters, isHighestInRing)` is true, where
    `isHighestInRing` is `h` being ≥ every one of 8 ring samples at `HilltopRingMeters` (compass +
    diagonals) and `mean(ring)` copies the shape of `SampleAverageHeight` (`…Sampling.cs:148-165`,
    widened to 8 points); skip a hit inside any airbase's `SavedAirbase.CaptureRange`
    (`Ai/CommanderCaptureService.cs:186`) or within `HilltopVillageExclusionMeters` of a village.
    **Budget**: `HilltopSamplesPerFrame = 256` (const,
    summary: 9 height-map reads each, no raycasts, so ~2300 array lookups per frame; a 200 km map is
    40 000 samples ≈ 160 frames ≈ 3 s), with a `hilltopIndex` cursor — the same shape as
    `regionsPerFrame` in `…Sampling.cs:61-66`. Snap accepted hilltops with `SnapToTerrain` (one
    raycast each, rare). → `Bases`.
  - `Bases`: one `Base` point per `FactionRegistry.airbaseLookup` entry with a non-null `center`
    and `SavedAirbase` (`Ai/CommanderCaptureService.cs:428-434`), `Position =
    center.GlobalPosition()`, `Radius = SavedAirbase.CaptureRange`. Then run `ApplySpacing` **once
    over villages + hilltops together** (villages first) with `PointMinSpacingMeters`, the airbase
    exclusion and `cap = MaxNonBasePoints − sites.Count`; append to `points` after the sites.
    → `Done`.
  - `Done` (first entry only): log one `LogInfo` per point, `Strategic point {Label}: {Kind} at
    ({x:0},{z:0}) r={Radius:0} m, nearest base {dist/1000:0.0} km`, preceded by one summary line
    with counts and the wall-clock duration (`Time.realtimeSinceStartup − discoveryStartedAt`, as
    `…Sampling.cs:99-101`). Any site dropped under departure 1 is logged as
    `Strategic point dropped: industrial building {GetUnitLabel} has no clear ground within 350 m.`
  Build.
  **Self-check (village and hilltop thresholds)** — added to the same shared `SelfCheck()` in
  `Points/CommanderStrategicPointService.cs` as T3/T5/T6/T7/T9 use, at the default `VillageMinBuildings
  = 4` and `HilltopProminenceMeters = 60`: `!QualifiesAsVillage(3, 4)` ("a cluster of
  `VillageMinBuildings − 1` does not qualify"); `QualifiesAsVillage(4, 4)` ("a cluster of
  `VillageMinBuildings` qualifies"); `!QualifiesAsHilltop(159f, 100f, 60f, true)` ("a sample
  `prominenceMeters − 1` above the ring mean fails"); `QualifiesAsHilltop(160f, 100f, 60f, true)`
  ("a sample exactly at `prominenceMeters` above the ring mean passes" — the boundary counts as
  qualifying); `!QualifiesAsHilltop(1000f, 100f, 60f, false)` ("a sample that is not the highest in
  its ring fails regardless of prominence"). Build. The clustering itself (which buildings join
  which cluster) and the ring sampling itself (which 8 points a candidate is compared against) stay
  out of this self-check, because both read live map and building data that a synthetic check
  cannot construct without re-implementing the terrain query it exists to guard.
  **Fail-proof**: SHA256 of `Points/CommanderStrategicPointDiscovery.cs`; plant `sampleHeight -
  ringMeanHeight >= prominenceMeters` → `sampleHeight - ringMeanHeight > prominenceMeters` in
  `QualifiesAsHilltop`; build; name the failing case ("a sample exactly at `prominenceMeters` above
  the ring mean passes": 160 − 100 = 60 is no longer `> 60`); restore; build; SHA256
  byte-identical. Notes.

- [x] **T5 — Hold state machine (pure) + the 5 s ring count, with self-check and fail-proof.** In
  `Points/CommanderStrategicPointService.cs`:
  - Pure: `internal static int QualifyingFaction(IReadOnlyList<int> counts, int minGarrison, out
    bool contested)` — `contested` = two or more entries > 0; returns the single index with
    `count ≥ minGarrison` when exactly one exists and `!contested`, else −1.
  - Pure: `internal static void Step(ref HoldState state, int qualifying, bool contested, float
    deltaSeconds, float holdSeconds)` — rules, in this order: `state.Contested = contested; if
    (contested) return;` (frozen: owner, candidate and progress untouched — design §2 "contested,
    frozen, pays nobody"); `if (qualifying < 0) { Owner = −1; Candidate = −1; Progress = 0; return; }`
    (below minimum or empty → neutral now, progress reset so the next arrival starts from zero);
    `if (qualifying == Owner) { Candidate = −1; Progress = 0; return; }` (held, paying); `if
    (qualifying != Candidate) { Candidate = qualifying; Progress = 0; } Progress += deltaSeconds; if
    (Progress ≥ holdSeconds) { Owner = qualifying; Candidate = −1; Progress = 0; }`.
  - `internal static bool Pays(in HoldState state) => state.Owner >= 0 && !state.Contested;`
  - `TickHold()`: refresh `hqOrder` from `FactionRegistry.GetAllHQs()` (non-null, `faction != null`)
    and, if its membership changed, reset every Village/Hilltop `Hold` to neutral (indices are only
    valid for one order — comment). Per Village/Hilltop point: zero a reusable `int[] counts` of
    `hqOrder.Count`; for each HQ walk `hq.factionUnits`, `id.TryGetUnit(out unit) && unit is
    GroundVehicle && !unit.disabled && FastMath.InRange(unit.transform.GlobalPosition(),
    point.Position, point.Radius)` → `counts[i]++` (aircraft excluded by the type test — design §2
    "any ground vehicle (including mobile AA)"; the idiom is `Ai/CommanderCaptureService.cs:453-454`).
    Then `Step(ref point.Hold, QualifyingFaction(counts, MinGarrison, out contested), contested,
    HoldCheckSeconds, HoldSeconds)`; on an owner change log
    `CommanderPlugin.Log.LogInfo($"{point.Label} {(newOwner == null ? "is neutral" : $"held by
    {newOwner.faction.name}")}.")` (the per-HQ log funnel arrives in T10 and reroutes this line).
    Keep `point.GarrisonCounts` (the `int[]` copy for the marker label, T12) and
    `point.PresentCount(hq)`.
  - Sites: `TickHold` also clears `point.Mine` when `Mine.disabled` (site freed — design §2 "Mine
    destroyed → site free").
  **Self-check (hold state machine)** — added to T3's `SelfCheck`, `holdSeconds = 60`, `dt = 5`:
  "neutral to held after HoldSeconds": 11 steps with `qualifying = 0` leave `Owner == −1`, the 12th
  sets `Owner == 0`; "lapse resets": from held, one step with `qualifying = −1` → `Owner == −1`,
  then 6 steps with `0` (30 s) then one with `−1` then 11 steps with `0` still `Owner == −1`
  (progress restarted from zero, not resumed); "contested freezes": from `Progress = 30` a step with
  `contested = true` leaves `Progress == 30` and `Owner` unchanged and `Pays` false even when
  `Owner ≥ 0`; "minimum garrison respected": `QualifyingFaction([1, 0], 2, out c) == −1`,
  `QualifyingFaction([2, 0], 2, out c) == 0 && !c`, `QualifyingFaction([2, 1], 2, out c) == −1 &&
  c`, `QualifyingFaction([3, 3], 2, out c) == −1 && c`. Build.
  **Fail-proof**: SHA256 of `Points/CommanderStrategicPointService.cs`; plant `if (contested) return;`
  → `if (false) return;` in `Step`; build; name the failing case ("contested freezes"); restore;
  build; SHA256 byte-identical. Notes.

- [x] **T6 — Income: bases, villages, hilltops on the mines' tick, with self-check and fail-proof.**
  In `Points/CommanderStrategicPointService.cs`:
  - Pure: `internal static float IncomePerMinute(StrategicPointKind kind, float baseRate, float
    villageRate, float hilltopRate)` (`Site` → 0: the mine on it pays through `mineLevels`), and
    `internal static float SumIncomePerMinute(int bases, int villages, int hilltops, float baseRate,
    float villageRate, float hilltopRate)`.
  - `internal void PayPointIncome(float share)` (called from ledger row 5 with
    `IncomeIntervalSeconds / 60f`): for each HQ in `FactionRegistry.GetAllHQs()` with `IsServer`
    (Testing rule 3 — same guard as `Economy/CommanderEconomyService.cs:762-766`):
    `hq.AddFunds(GetPointIncomePerMinute(hq, out _, out _, out _) * share)`.
  - `internal float GetPointIncomePerMinute(FactionHQ hq, out int bases, out int villages, out int
    hilltops)`: bases = count of `hq.GetAirbases()` non-disabled with `center != null`
    (`Ai/CommanderEnemyCommanderDefence.cs:253-260`); villages/hilltops = points of that kind with
    `Pays(Hold)` and `GetOwner() == hq`. Returns `SumIncomePerMinute(...)` with the three
    `CommanderSettings` rates.
  Ledger rows 5 and 6. **Self-check (income table sums)** — in `SelfCheck`: `SumIncomePerMinute(2, 1,
  3, 30, 10, 5) == 85` ("income sums"), `IncomePerMinute(Site, …) == 0` ("a site pays only through
  its mine"), and the live defaults ordered `Base > Village > Hilltop > 0` read from
  `CommanderSettings` ("income ladder is base > village > hilltop; check the Points section of the
  config") — the retune-into-nonsense guard Testing rule 1 asks for. Build.
  **Fail-proof**: SHA256; plant `villages * villageRate` → `villages * hilltopRate` in
  `SumIncomePerMinute`; build; name the failing case ("income sums", 2·30+1·5+3·5 = 80 ≠ 85);
  restore; build; SHA256 identical. Notes.

- [x] **T7 — Mines only on sites; reach = base or garrisoned point (ledger rows 1-4), with self-check
  and fail-proof.**
  In `Points/CommanderStrategicPointService.cs`: pure `internal static int
  NearestFreeSiteIndex(IReadOnlyList<float> distances, IReadOnlyList<bool> free, float
  snapMeters)` — the index of the nearest entry with `free[i]` true and `distances[i] <=
  snapMeters`, else −1 (boundary: a distance exactly equal to `snapMeters` counts as inside, the
  planner-chosen number from departure 4). `internal bool TrySnapMineSite(GlobalPosition target,
  out GlobalPosition site)` computes the horizontal distance from `target` to every `Site` point
  and whether each is free (`Mine == null || Mine.disabled`), then delegates to
  `NearestFreeSiteIndex` — one definition of the snap rule (Reuse rule 4); `site =
  points[index].Position` when the index is not −1. `internal void AttachMine(GlobalPosition
  site, Unit mine)` — the site at that exact position (≤ 1 m) gets `Mine = mine`; `internal bool
  IsInsideGarrisonedPointReach(FactionHQ hq, GlobalPosition target, float radiusKm)` — true when a
  Village/Hilltop with `Pays(Hold)` and `GetOwner() == hq` is within `radiusKm * 1000` of `target`
  (the same `Mathf.Max(radiusKm, 0.1f) * 1000f` shape as `Economy/CommanderBuildPreview.cs:300`);
  `internal bool TryPickFreeReachableSite(FactionHQ hq, out GlobalPosition site)` — among free sites
  passing `CommanderBuildPreview.IsInsideBuildRadius(hq, p, BuildRadiusKm) ||
  IsInsideGarrisonedPointReach(hq, p, BuildRadiusKm)`, the nearest to
  `CommanderCaptureService.GetTerritoryCenter(hq)` (`Ai/CommanderCaptureService.cs:490-504`).
  Then apply rows 1-4 exactly. `Economy/CommanderEconomyService.cs`: `internal static bool
  IsMineDefinition(BuildingDefinition? d)` beside `IsNavalDockDefinition` (246-252), same shape, so
  `Evaluate` can ask. Update the `Evaluate` summary (129-134): a mine is also blocked off a resource
  site. Player click path check: `TryPlaceBuildingFromWorld` (541-633) calls `IsSiteAllowed(placed,
  position, …)` at 563-568 then `SpawnMine(hq, position)` at 621 — with row 3, `SpawnMine` snaps
  `position` itself, so the mine lands on the site the ghost showed. Build. Then confirm by reading:
  every mine spawn path (`TryPlaceBuildingFromWorld` 619-633, `TryBuildEnemyMine` 419-431) ends in
  `SpawnMine`, and `SpawnMine` ends in `SpawnBuilding` → `IsSiteAllowed`. Write that trace in the notes.
  **Self-check (mine snap)** — added to the same shared `SelfCheck()` in
  `Points/CommanderStrategicPointService.cs` as T3/T5/T6 use: `NearestFreeSiteIndex([1500f],
  [true], 1000f) == −1` ("nothing within range"); `NearestFreeSiteIndex([1000f], [true], 1000f) ==
  0` ("one free site just inside `snapMeters`" — the boundary itself counts as inside);
  `NearestFreeSiteIndex([900f, 400f], [true, true], 1000f) == 1` ("two free sites in range: the
  nearer one"); `NearestFreeSiteIndex([300f, 700f], [false, true], 1000f) == 1` ("the nearest site
  occupied and a free one further but still in range: the free one");
  `NearestFreeSiteIndex([1000.1f], [true], 1000f) == −1` ("a free site just outside: −1"). Build.
  **Fail-proof**: SHA256 of `Points/CommanderStrategicPointService.cs`; plant `distances[i] <=
  snapMeters` → `distances[i] < snapMeters` in `NearestFreeSiteIndex` (or drop the `free[i]` test —
  pick one and name it); build; name the failing case ("one free site just inside `snapMeters`":
  1000 no longer counts as inside); restore; build; SHA256 byte-identical. Notes.

- [x] **T8 — Enemy mine step picks the nearest free reachable site (ledger rows 7-9).** Apply the
  rows. In `TryBuildEnemyMine` keep the log line; the text becomes `built a gold mine on
  {point.Label}.` — so add `internal bool TryPickFreeReachableSite(FactionHQ hq, out
  CommanderStrategicPoint point)` as the primary overload and make the `GlobalPosition` one call
  it. Update the `GetEnemyBuildReserve` summary (150-159) with one sentence: a mine is only wanted
  while a site is in reach, otherwise the commander saves for the next thing. Build. Grep
  `TryFindEnemyBuildSite(hq, mine` — expect no hits.

- [x] **T9 — Garrison partial (ledger rows 10-14), with self-check and fail-proof.** Create
  `Ai/CommanderEnemyCommanderGarrison.cs`,
  `internal sealed partial class CommanderEnemyCommanderService`, class-part `<summary>` (what a
  garrison is, that it uses the home guard's pinning mechanism and why — cite the Defence remarks
  12-28 in a `<see cref>`), `<remarks>` "ponytail:" the garrison stands still and shoots for itself,
  no patrol, no reaction to raids (later tracks). Constants with summaries: `GarrisonReachMeters =
  12000f`, `MaxGarrisonedPoints = 3`, `GarrisonArrivedMeters = 150f` (same as `DefenceArrivedMeters`
  at `Defence.cs:53` and for the same RPC reason — but a *second* identical constant is Reuse rule 4's
  tell, so **reuse `DefenceArrivedMeters` directly** and do not declare a new one). Scratch lists
  `garrisonCandidates`, `staleGarrison` (cleared in `ResetSession`, row 12).
  - Pure: `internal static void SelectGarrisonTargets(IReadOnlyList<float> distances, float
    reachMeters, int maxPoints, List<int> result)` — fills `result` with the indices of the
    nearest `maxPoints` entries with `distance <= reachMeters`, in ascending distance order (a
    distance exactly equal to `reachMeters` counts as in reach); `result` is cleared first.
  - `internal static bool IsGarrisonUnit(Unit? unit)` — the `IsDefendingUnit` shape
    (`Defence.cs:85-102`) over `state.Garrison`.
  - `private void ReviewGarrisons(FactionHQ localHq)` — the `ReviewDefences` loop shape
    (`Defence.cs:121-141`), per HQ `ReviewGarrison(hq, state)`.
  - `ReviewGarrison(hq, state)`: (a) prune: drop entries whose unit is null/disabled/other HQ/
    `HasPlayerOrder` (comment as `Defence.cs:306-310`: leave `commandedDestination` alone); (b) target
    points: for every Village/Hilltop point compute its distance to the nearest base `hq` holds,
    then delegate to `SelectGarrisonTargets` with `reachMeters = GarrisonReachMeters`, `maxPoints =
    MaxGarrisonedPoints` — one definition for the reach test and the cap (Reuse rule 4); the
    indices `SelectGarrisonTargets` returns are the targets, nearest first — a point another
    faction currently holds is still a target (taking it is the point); (c) for each target short of
    `MinGarrison + 1` units: recruit from `hq.factionUnits` with the `RecruitDefenders` filter
    (`Defence.cs:365-379`: `GroundVehicle`, not disabled, `VehicleDefinition` and `IsCombatVehicle`,
    `HasPlayerOrder != true`) **plus** `!state.Defenders.ContainsKey(unit)` (home guard),
    `definition.vehicleType != VehicleType.RDR` (recon, `Ground.cs:64`), and not already in
    `state.Garrison`; take nearest-first to the point; (d) for every garrison unit not within
    `DefenceArrivedMeters` of its post: `GetUnitCommand(unit)?.SetDestination(post, true)` where the
    post is a slot on a ring at `point.Radius * 0.6f` (`MinGarrison + 1` slots, evenly spaced,
    `SnapToTerrain`, skip `IsBelowSeaLevel` — the `EnsureDefencePosts` shape, `Defence.cs:281-294`),
    cached per point in a `Dictionary<CommanderStrategicPoint, List<GlobalPosition>>` on the
    service (a point does not move); (e) a point that leaves the target list releases its units the
    `ReleaseSurplusDefenders` way (`CommandedDestinationRef(vehicle) = false`, `Defence.cs:350-353`).
    Log through the same `LogInfo($"{CommanderLabel(hq)} garrisons {point.Label} with {n}
    unit(s).")` once per (point, first fill) — T10 reroutes it.
  Rows 10-14. Build. Grep `IsGarrisonUnit` — expect the definition plus exactly two callers
  (`Defence.cs` recruit filter, `CaptureService.cs` squad filter).
  **Self-check (garrison targets)** — added to the same shared `SelfCheck()` in
  `Points/CommanderStrategicPointService.cs` as T3/T5/T6/T7 use: five candidates at distances
  `[5000f, 20000f, 8000f, 15000f, 3000f]` with `reachMeters = 12000f`, `maxPoints = 3` →
  `result == [4, 0, 2]` ("five candidates, three inside reach, nearest first" — 3000, 5000, 8000 m
  kept in that order; 20000 and 15000 excluded); six candidates all inside reach at `[1000f, 2000f,
  3000f, 4000f, 5000f, 6000f]` with `maxPoints = 3` → `result == [0, 1, 2]` only ("six inside reach,
  cap holds to the three nearest"); one candidate exactly at the boundary (`[12000f]`, `reachMeters
  = 12000f`, `maxPoints = 1`) → `result == [0]` ("candidate exactly at `reachMeters` is included" —
  the same inclusive boundary convention as `MineSnapMeters`, T7); no candidate in reach (`[20000f,
  30000f]`, `reachMeters = 12000f`) → `result` empty ("no candidate in reach"). Build.
  **Fail-proof**: SHA256 of `Ai/CommanderEnemyCommanderGarrison.cs`; plant `maxPoints` →
  `maxPoints + 1` in `SelectGarrisonTargets` (or reverse the ascending sort — pick one and name it);
  build; name the failing case ("six inside reach, cap holds": result now has four entries, not
  three); restore; build; SHA256 byte-identical. Notes.

- [x] **T10 — One log funnel: `CommanderAiLog.Note` (ledger rows 15-16).** Create `UI/CommanderAiLog.cs`,
  `internal static class CommanderAiLog`: `private const int Capacity = 200` (summary: design's
  "last 200 decisions"; ~30 s per review × 4 spenders means an hour of play); `private static readonly
  Dictionary<FactionHQ, List<Entry>> logs`; `internal readonly struct Entry(float MissionTime, string
  Text)`; `internal static string FormatMissionTime(float seconds)` — **moved** from
  `Units/CommanderAlertService.cs:321` (row 16; identical `mm:ss` output, pointer comment at the old
  site); `internal static void Note(FactionHQ hq, string text, string? detail = null)` — builds
  `$"{CommanderPlayerCommanderService.CommanderLabel(hq, detail)} {text}"`, calls
  `CommanderPlugin.Log.LogInfo(line)` (so `LogOutput.log` is byte-identical to today), then
  `Insert(0, new Entry(Time.timeSinceLevelLoad, text))` into the HQ's list and trims past `Capacity`
  (the `AddLog` shape, `Units/CommanderAlertService.cs:272-279`); `internal static
  IReadOnlyList<Entry> For(FactionHQ hq)` (empty list when none); `internal static void Clear()`
  called from `CommanderStrategicPointService.ResetSession` (the one reset hook this track owns —
  comment why it lives there). Apply row 15 to all 24 sites: the text after the label is
  byte-identical; the hold-change and garrison lines from T5/T9 become `Note` too (T5's line has an
  `hq` only when there is an owner — for "is neutral" use the previous owner; if there is none,
  keep `LogInfo`). Build. Grep `CommanderLabel(` — expect exactly two hits outside its definition:
  `CommanderAiLog.Note` and the `IsCommanded` doc comment; grep `LogInfo(` in `Ai/`, `Economy/…Enemy.cs`,
  `Units/CommanderRepairService.cs` — the survivors must be lines with no `hq` (roster/prefab/category
  resolution lines: `Ai/CommanderCaptureService.cs:410`, `Economy/CommanderEconomyServiceEnemy.cs:310-313`,
  `Economy/CommanderEconomyService.cs:1074-1076`, `Ai/CommanderEnemyCommanderAir.cs:311` if it has no
  label). List them in the notes.

- [x] **T11 — COMMANDER LOG window (ledger rows 20-21).** Create `UI/CommanderAiLogUi.cs`,
  `internal sealed class CommanderAiLogUi`, copying `UI/CommanderUnitListUi.cs` member for member
  where it applies: `WindowId = 0x434F4D41` (distinct), `RefreshIntervalSeconds = 0.5f`, `RowHeight
  = 26f`; `Visible`, `Toggle`, `Hide`, `ResetPosition`, `ContainsScreenPoint`, `Tick` (same size
  maths as 65-77; initial x `486f + 40f` so it does not sit exactly on ORDER OF BATTLE), `Draw` →
  `GUI.Window(WindowId, windowRect, DrawWindow, "COMMANDER LOG", CommanderUiTheme.Window)`.
  `DrawWindow`: help button + X (213-220); **one tab per HQ** — on each realtime refresh rebuild
  `tabs` = local HQ first, labelled `"YOU"` (the player commander's lines land here when the switch
  is on: design §4's "your AI if on" is the same HQ's log, so it is the same tab), then every other
  `FactionRegistry.GetAllHQs()` HQ with a faction, labelled `faction.name.ToUpperInvariant()`. Tabs
  drawn with the `DrawFilterTab` shape (317-325). **Header**
  (three 22 px rows under the tabs): `FUNDS {FundsLabel(hq.factionFunds)}`;
  `INCOME/MIN  BASES +{b}  VILLAGES +{v}  HILLTOPS +{h}  MINES +{m}` where `b/v/h` come from
  `CommanderStrategicPointService.Instance.GetPointIncomePerMinute(hq, out …)` × rates and `m` from
  `CommanderEconomyService.Instance.GetMineIncomePerMinute(hq)` (row 6), all through
  `CommanderEconomyService.FundsLabel`; `PLAN {plan}   RESERVE TARGET {reserve}` where plan comes
  from a new `internal string GetPlanLabel(FactionHQ hq)` on `CommanderEnemyCommanderService`
  (`states.TryGetValue(hq, out s) ? GetPlanLabel(s.Plan) : "NONE"` — ASCII only, the IMGUI font
  guarantees nothing else) and reserve
  is `FundsLabel(CommanderEconomyService.GetEnemyBuildReserve(hq))` (already `internal static`,
  `…Enemy.cs:160`). **Body**: the `DrawBattleLog` shape (143-182) over `CommanderAiLog.For(hq)`:
  timestamp column via `FormatMissionTime`, text label; `NO DECISIONS YET` when empty. Rows 20-21.
  Build.

- [x] **T12 — Points on the world and the tactical map (ledger row 23).** Create
  `Points/CommanderStrategicPointMarkers.cs` (partial of the service). `internal void DrawMarkers(Camera
  camera)`: skip when `Event.current.type != EventType.Repaint` is already handled by the caller
  (`Units/CommanderWorldMarkerRenderer.cs:49-52`); for every point: colour by owner — `GetOwner() ==
  localHq` → `CommanderGameAccess.GetFriendlyColor()`, another HQ → `GetHostileColor()`
  (`Core/CommanderGameAccess.cs:223, 233`), none → a `NeutralPointColor` equal in value to
  `NeutralBaseColor` (`Units/CommanderWorldMarkerRenderer.cs:21`) — **move** that colour constant
  into `CommanderUiTheme` as `internal static readonly Color NeutralMarker` and have both callers
  use it (Reuse rule 4). Shape by kind: diamond (Site), square (Village), triangle (Hilltop); Base
  points draw nothing here (the capture renderer already marks bases). Shapes are drawn with
  `GUI.DrawTexture(rect, Texture2D.whiteTexture)` bars exactly as `CommanderUiTheme.Bar`
  (`UI/CommanderUiTheme.cs:374-377`) — make `Bar` `internal` and call it; a diamond is four short
  bars stepping diagonally, a triangle three, a square the four edges. **Striped when contested**:
  alternate bars between the owner colour and `Color.white`. **Label**: `$"{Label}  {readout}"` via
  `CommanderUiTheme.DrawWorldLabel` (380), where readout is `mine L{level}` / `free` for a site
  (level via `CommanderEconomyService.Instance.GetMineLevel(mine)`), `{own}/{MinGarrison}` for a
  village or hilltop (`own` = the local faction's present count; when neutral and another faction
  is present show `{theirs}/{MinGarrison}` in hostile colour), plus `CONTESTED` when contested and
  `{progress:0}s` while a candidate is accumulating. World vs map: the `DrawCaptureTargets` split
  (`Units/CommanderWorldMarkerRenderer.cs:330-357`): `DynamicMap.mapMaximized` → project with
  `CommanderTacticalMapService.Instance.TryWorldToMapScreen`, size 18; else camera projection with
  the `DrawWorldPoint` early-out (387-396), size 30. Record each point's last drawn GUI position
  and half-size in the point (`ScreenPosition`, `ScreenHalfSize`, `ScreenFrame = Time.frameCount`)
  for T13's hit test. Row 23. Build.

- [x] **T13 — Click a point to read it (ledger rows 24-26, departure 3).** In the markers partial:
  `internal CommanderStrategicPoint? FocusedPoint { get; private set; }`, `internal bool
  TryFocusPointAt(Vector2 screenPosition)` — convert with `CommanderUiScale.ScreenToGui`, pick the
  nearest point whose `ScreenFrame ≥ Time.frameCount − 1` and whose square of `ScreenHalfSize`
  contains the point (the `TryGetMarkerUnitAt` nearest-hit shape,
  `Units/CommanderMarkerService.cs:288-308`); `ClearFocus()`. Rows 24-26. `DrawFocusedPointCard()` in
  `UI/CommanderOverlayUiSelection.cs`: `GUI.Box(selectionBarRect, …Panel)`, muted label `STRATEGIC
  POINT`, header label `{Label}  |  {KIND}`, one line `OWNER {faction or NEUTRAL}   INCOME
  +{rate}/min   GARRISON {own}/{MinGarrison}{  CONTESTED}` or for a site `MINE L{n} +{income}/min` /
  `FREE  —  build a gold mine here`, and a `CENTER` button (`CommanderTacticalMapService.Instance
  ?.JumpCameraToPosition(point.Position)`, `Map/CommanderTacticalMapService.cs:343`). Build.

- [x] **T14 — POINTS settings tab (ledger row 22, departure 2).** `UI/CommanderOverlayUiSettings.cs`:
  row 22, then `private void DrawPointsSettings(float y)`: one `Panel` box, header `STRATEGIC POINTS`,
  then six slider rows on a 38 px pitch using the existing helpers — `MinGarrison` via
  `DrawCameraSlider(y, "Minimum garrison", value, 1f, 6f, "0", " vehicles")` rounded to an int;
  `HoldSeconds` via `DrawCameraSlider(…, 15f, 300f, "0", " s")` snapped to 5 s; the four income rates
  (`BaseIncomePerMinute` 0-100, `VillageIncomePerMinute` 0-50, `HilltopIncomePerMinute` 0-50,
  `GoldMineIncomePerMinute` 0-100) via `DrawCameraSlider(…, "0", " /min")` snapped to whole numbers.
  A muted footnote: `Discovery spacing lives in the config file, Points section.` Arithmetic in a
  comment: `y ≤ 162`, box 10 + 32 + 6×38 + 30 = 300 → 462 < 790. Build.

- [x] **T15 — Verification environment: BepInEx console on (ledger row 28; not repo code).** Edit
  `I:\SteamLibrary\steamapps\common\Nuclear Option\BepInEx\config\BepInEx.cfg` line 48 under
  `[Logging.Console]`: `Enabled = false` → `Enabled = true`. Nothing else in the file changes.
  Verification: `Select-String -Path <cfg> -Pattern '^Enabled = true'` shows exactly one hit in the
  `[Logging.Console]` block (the file has several `Enabled` keys; quote the surrounding two lines in
  the notes). Tell the user in the notes that the console window will now open with the game and
  how to turn it back off.

- [x] **T16 — CHANGELOG and README (ledger row 27).** `CHANGELOG.md`: new bullet at the top of the
  `### Added` list at line 228, in the file's voice (what the player sees first): resource-site
  diamonds, village squares and hilltop triangles on the map and in the world; mines snap to sites
  and refuse elsewhere; two vehicles standing in a village ring for a minute take it and it pays
  every 15 s while they stay; bases pay their holder; both commanders now build their mines on sites
  and post small garrisons on nearby points; the COMMANDER LOG window under ORDER OF BATTLE shows
  every AI decision live with funds and income by source; the POINTS settings tab. State the
  defaults (30/10/5 per minute, 2 vehicles, 60 s) and that owners are not saved across a reload.
  `README.md`: `## Strategic points` before `## Unit systems` (line 630) — three short subsections:
  *What is on the map* (kinds, how they are found, the config keys under `Points/`), *Holding and
  earning* (the rule, the numbers, the contested/neutral states, mines-on-sites and reach), *Watching
  the AI* (COMMANDER LOG, tabs, header). Mention the BepInEx console tip from design §4.
  Verification: grep `CHANGELOG.md` for the new bullet under `### Added` and grep `README.md` for
  `## Strategic points` to confirm both insertions landed in the right place (top of the `Added`
  list; before `## Unit systems`); no `dotnet build` needed — neither file is compiled.

- [x] **T17 — Final build, greps, notes.** `dotnet build GroundControlRts.csproj -c Release`; paste the
  last 5 lines. Greps to paste: `CommanderLabel(` (expect: definition, `CommanderAiLog.Note`, one doc
  comment), `TryFindEnemyBuildSite(hq, mine` (none), `IsGarrisonUnit` (definition + 2 callers),
  `NeutralBaseColor` (none — moved), `"Enemy commander (`/`"Player commander (` (none),
  `FloorToInt(MissionTime` (none — moved). Tick every task, fill every notes section, and list every
  deviation from this plan with its reason.

## DAG (what can run in parallel)

```
T1 ──► T2 ──► T3 ──► T4 ──► T5 ──► T6 ──► T7 ──► T8
                          │      └──► T9
                          │      └──► T12 ──► T13
T10 (independent) ───────┴──► T11 (needs T6 for the income header)
T1 ──► T14
T15 (independent)
T16 after T8, T9, T11, T13, T14
T17 last
```

Parallel groups for a dispatcher: {T1, T10, T15} → {T2} → {T3} → {T4} → {T5} → {T6, T9, T12, T14}
→ {T7, T11, T13} → {T8} → {T16} → {T17}. One executor running in order is the expected mode
(`.claude/CLAUDE.md` Track shape); the DAG only says what must not be reordered.

## In-game acceptance (USER TO VERIFY IN GAME — not the executor, not the evaluator)

From design.md Verification, verbatim in substance, as host in `Ground Control Duel` and one stock
Escalation map, after `.\build-and-install.ps1` with the game closed (or `build-dev.bat` for hot
reload). With the BepInEx console on (T15), the mod's lines appear live.

- [ ] Self-checks: no `self-check FAILED` at load; `Strategic points self-check passed` at debug level.
- [ ] Points appear on the map with expected kinds: within ~10 s of mission start the console prints
      the discovery table (`Strategic point SITE 1: Site at (…)` …) and the tactical map shows diamonds,
      squares and triangles; none sits on an airbase; no two are on top of each other.
- [ ] A mine cannot be placed off-site: arm BUILD GOLD MINE; away from any diamond the ghost is red
      and the status line reads `Blocked: a gold mine has to stand on a resource site.`; within
      1 km of a free diamond the ghost jumps onto it and turns green (departure 4 — say if 1 km
      feels wrong); clicking builds the mine on the diamond and the diamond's label reads `mine L1`.
- [ ] A two-vehicle platoon takes a village after 60 s and income rises: drive two ground vehicles
      into a square; its label counts `2/2` and a rising seconds readout; after 60 s it turns
      friendly-coloured, the console says `VILLAGE n held by <your faction>`, and the COMMANDER LOG
      header's `VILLAGES +10` appears; faction funds rise by 2.5 every 15 s more than before.
- [ ] Driving one vehicle away drops it to neutral: within 5 s the label shows `1/2`, the colour
      goes neutral, the console says `VILLAGE n is neutral`, income stops.
- [ ] Drive an enemy vehicle in while yours are there (or watch the AI garrison arrive): the marker
      stripes and reads `CONTESTED`; nobody is paid.
- [ ] The AI builds its first mine on a site rather than at its base: the first
      `Enemy commander (<faction>) built a gold mine on SITE n.` line, and the mine stands on a diamond
      that is not inside the base.
- [ ] The COMMANDER LOG shows the AI's purchases and garrison orders live: CMD → COMMANDER LOG; the
      enemy tab lists `bought …`, `built …`, `garrisons HILL n with 3 unit(s).` with mm:ss stamps as
      they happen; the header shows funds, `INCOME/MIN BASES +30 …`, plan and reserve target.
- [ ] `BepInEx\LogOutput.log` still contains every line the window shows, worded exactly as before
      (`Enemy commander (<faction>) …`).
- [ ] Player commander ON: the YOU tab fills with `Player commander (<faction>) …` lines, and its
      mines land on sites too.

Planner notes for the user, not in the design: (a) departure 1 means a site can be up to 350 m from
the industrial building it was seeded from; (b) departure 4's 1 km snap radius is the one number
this plan chose — it is `Points/MineSnapMeters` in the config if it feels wrong; (c) a pure
multiplayer client sees no points at all this track (host-only, per design); (d) the Gameplay
sliders moved to a POINTS tab (departure 2).

## Executor notes

### Final build (T17)

`dotnet build GroundControlRts.csproj -c Release` (with `NUCLEAR_OPTION_DIR` exported as
`I:\SteamLibrary\steamapps\common\Nuclear Option`), last 5 lines:

```
  Determining projects to restore...
  All projects are up-to-date for restore.
  GroundControlRts -> I:\Dropbox (Personal)\Projects\RTS-Commander\bin\Release\net472\GroundControlRts.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Greps (all run from the repo root):

- `grep -rn "CommanderLabel(" --include=*.cs .` — 2 hits: the definition
  (`Ai/CommanderPlayerCommanderService.cs:144`) and `CommanderAiLog.Note`'s call
  (`UI/CommanderAiLog.cs:54`). The plan expected 3 (definition, `Note`, one doc comment); there is no
  third hit because no doc comment in the current codebase mentions `CommanderLabel` by name —
  recorded as a deviation below.
- `grep -rn "TryFindEnemyBuildSite(hq, mine" --include=*.cs .` — no hits.
- `grep -rn "IsGarrisonUnit" --include=*.cs .` — 3 hits: the definition
  (`Ai/CommanderEnemyCommanderGarrison.cs:47`) and exactly two callers
  (`Ai/CommanderEnemyCommanderDefence.cs:374`, `Ai/CommanderCaptureService.cs:690`).
- `grep -rn "NeutralBaseColor" --include=*.cs .` — no hits (moved to
  `CommanderUiTheme.NeutralMarker`; the pointer comment at the new site was reworded during T17 to
  describe the move without repeating the old identifier, so the grep itself would confirm the move
  cleanly).
- `grep -rn '"Enemy commander (\|"Player commander (' --include=*.cs .` — no hits.
- `grep -rn "FloorToInt(MissionTime" --include=*.cs .` — no hits (moved to
  `CommanderAiLog.FormatMissionTime`).

Every task's own build ran individually and passed at `0 Warning(s)`, `0 Error(s)` (see each task's
description above); the six fail-proofs each needed one deliberate build with the defect in place,
which is not this rule — see each fail-proof record. Nothing was installed, staged or committed.

### T3 fail-proof record (Testing rule 5)

1. SHA256 of `Points/CommanderStrategicPointDiscovery.cs` before planting:
   `96F8F8E97D5B19D4DE401E4DB92624232EC7A98152BC885AA64C631EC62EE40E`.
2. Planted defect in `ApplySpacing`: `HorizontalDistance(candidate.Position, kept[k].Position) < minSpacing`
   became `< minSpacing * 0.5f`. Build: succeeded, `0 Warning(s)`, `0 Error(s)` (invisible to the
   compiler, as expected).
3. Reasoned from the code with the defect in place, against `CheckDiscoverySpacing`'s synthetic
   list (line0=0, line1=1000, baseAdjacent=100500 near an airbase at 100000, line2=2500, line3=4000,
   line4=5500, line5=7000; `minSpacing=1500`, `exclusionRadius=2000`, `cap=3`): line1 is now only
   blocked at <750 m, and its distance to the kept line0 is 1000 m, so it is wrongly kept — the case
   **"newer of a close pair is dropped"** now fails (`candidates.Contains(line1)` is `true`, expected
   `false`). With line1 kept, the final kept set is `[line0, line1, line2]` (cap still reached at 3,
   so **"cap holds"** still passes) and the pairwise distance line0-line1 = 1000 m < 1500 m, so
   **"kept points respect spacing"** also fails. The airbase exclusion test is a separate code path
   untouched by this defect, so **"nothing inside the airbase exclusion"** still passes — the defect
   is not load-bearing for that case, which is expected: it targets the spacing rule only.
4. Restored `< minSpacing`. SHA256 after restore: `96F8F8E97D5B19D4DE401E4DB92624232EC7A98152BC885AA64C631EC62EE40E`
   — byte-identical to step 1. Rebuilt: `0 Warning(s)`, `0 Error(s)`.

### T4 fail-proof record (village and hilltop thresholds)

1. SHA256 of `Points/CommanderStrategicPointDiscovery.cs` before planting:
   `8CCE383847794EB0DB7DB7BFC10A3C2B4EAD2D0FDC0A71C7D23DAF8B8645DC51`.
2. Planted defect in `QualifiesAsHilltop`: `sampleHeight - ringMeanHeight >= prominenceMeters` became
   `sampleHeight - ringMeanHeight > prominenceMeters`. Build: succeeded, `0 Warning(s)`, `0 Error(s)`.
3. Reasoned from the code: `QualifiesAsHilltop(160f, 100f, 60f, true)` computes `160 - 100 = 60`,
   which is no longer `> 60`, so the call now returns `false` where the self-check expects `true` —
   the case **"a sample exactly at `prominenceMeters` above the ring mean passes"** fails. The other
   four hilltop/village cases are unaffected: `159f` vs `159 > 60` is still false (case still
   passes), and `1000f` with `isHighestInRing = false` is still gated out by the `&&` regardless of
   the comparison operator.
4. Restored `>=`. SHA256 after restore: `8CCE383847794EB0DB7DB7BFC10A3C2B4EAD2D0FDC0A71C7D23DAF8B8645DC51`
   — byte-identical to step 1. Rebuilt: `0 Warning(s)`, `0 Error(s)`.

### T5 fail-proof record

1. SHA256 of `Points/CommanderStrategicPointService.cs` before planting:
   `7ABFC6C0DE8FAD61D370C3FAF173EBB1967562406D503267F1E83463806AF7C8`.
2. Planted defect in `Step`: `if (contested) return;` became `if (false) return;`. Build: succeeded
   with `1 Warning(s)` (CS0162 "Unreachable code detected" on the now-dead `return;`), `0 Error(s)`
   — the compiler flags the planted `if (false)` itself, which is expected and different from the
   defect being invisible; the *semantic* effect (processing continues instead of freezing) is what
   the self-check exists to catch, not the literal `if (false)`.
3. Reasoned from the code: `state.Contested = contested;` still runs (unaffected), so `Pays` still
   correctly reads `false` and `OwnerIndex` is untouched by this one call, but the early return no
   longer happens, so the qualifying/candidate/progress logic below keeps running while contested.
   In the **"contested freezes"** scenario (owner = 0, candidate = 1, `Progress = 30`, then one step
   with `qualifying = 1, contested = true`): candidate is already 1 so it is not reset, and
   `Progress += 5` runs anyway, giving `Progress = 35`. The case
   **"contested freezes: progress unchanged"** fails (expected 30, got 35). The sibling assertions
   in the same group do not: `"contested freezes: owner unchanged"` still passes (owner was never
   written by this defect) and `"contested freezes: pays false even with an owner in place"` still
   passes (`Contested` is still set true unconditionally, so `Pays` is still false) — the defect is
   only load-bearing for the progress-freeze half of the rule, which is exactly what
   `if (contested) return;` exists to guarantee.
4. Restored `if (contested) return;`. SHA256 after restore:
   `7ABFC6C0DE8FAD61D370C3FAF173EBB1967562406D503267F1E83463806AF7C8` — byte-identical to step 1.
   Rebuilt: `0 Warning(s)`, `0 Error(s)`.

### T6 fail-proof record

1. SHA256 of `Points/CommanderStrategicPointService.cs` before planting:
   `8CF5E0343154CC66C33A6274CB6599341D4724677376449599AB65E4BB5D49A3`.
2. Planted defect in `SumIncomePerMinute`: `villages * villageRate` became `villages * hilltopRate`.
   Build: succeeded, `0 Warning(s)`, `0 Error(s)`.
3. Reasoned from the code: `SumIncomePerMinute(2, 1, 3, 30f, 10f, 5f)` now computes
   `2*30 + 1*5 + 3*5 = 60 + 5 + 15 = 80`, not `85` — the case **"income sums"** fails (expected 85,
   got 80). `"a site pays only through its mine"` is unaffected (`IncomePerMinute` is a separate
   function untouched by this edit) and `"income ladder is base > village > hilltop"` reads the live
   `CommanderSettings` rates directly, not `SumIncomePerMinute`, so it also still passes.
4. Restored `villages * villageRate`. SHA256 after restore:
   `8CF5E0343154CC66C33A6274CB6599341D4724677376449599AB65E4BB5D49A3` — byte-identical to step 1.
   Rebuilt: `0 Warning(s)`, `0 Error(s)`.

### T7 fail-proof record (mine snap)

1. SHA256 of `Points/CommanderStrategicPointService.cs` before planting:
   `DE3B211AD95FB7E46B2D520DEAC2E2AAA9547E4D459918BCF20C3C327EBAE3F6`.
2. Planted defect in `NearestFreeSiteIndex`: the skip condition `distances[i] > snapMeters` became
   `distances[i] >= snapMeters` (the plan's `<=` -> `<` on the accept side, expressed on the skip
   side actually used in this implementation). Build: succeeded, `0 Warning(s)`, `0 Error(s)`.
3. Reasoned from the code: `NearestFreeSiteIndex([1000f], [true], 1000f)` now skips its only
   candidate (`1000 >= 1000` is true) and returns -1 instead of 0 — the case
   **"one free site just inside `snapMeters`"** fails (expected 0, got -1). The other four cases are
   untouched: none of them sits exactly on the boundary, so `>` and `>=` agree on all of them.
4. Restored `distances[i] > snapMeters`. SHA256 after restore:
   `DE3B211AD95FB7E46B2D520DEAC2E2AAA9547E4D459918BCF20C3C327EBAE3F6` — byte-identical to step 1.
   Rebuilt: `0 Warning(s)`, `0 Error(s)`.

### T7 mine spawn-path trace

`grep -n "SpawnMine(" Economy/CommanderEconomyService.cs Economy/CommanderEconomyServiceEnemy.cs`:
- `Economy/CommanderEconomyService.cs:643` — the player click path, `TryPlaceBuildingFromWorld`,
  calls `SpawnMine(hq, position)`.
- `Economy/CommanderEconomyServiceEnemy.cs:425` — `TryBuildEnemyMine` calls
  `SpawnMine(hq, site, randomRotation: true)` (as of T7; T8 changes how `site` is found, not this
  call).
- `Economy/CommanderEconomyService.cs:830` — `SpawnMine` itself: snaps the target through
  `CommanderStrategicPointService.TrySnapMineSite`, then calls `SpawnBuilding(hq, site, ...)`, which
  calls `preview.IsSiteAllowed(definition, position, hq, out _)` before ever spawning anything.
  Every mine, from both commanders, therefore passes through `TrySnapMineSite` and `IsSiteAllowed`
  exactly once, with no other path to a spawned mine.

### T9 fail-proof record (garrison targets)

1. SHA256 of `Ai/CommanderEnemyCommanderGarrison.cs` before planting:
   `09E5265BCD803369B19EB63AEF59CD50A4E5BB34DA1897E5F3377E79D2896661`.
2. Planted defect in `SelectGarrisonTargets`: the loop condition `result.Count < maxPoints` became
   `result.Count < maxPoints + 1`. Build: succeeded, `0 Warning(s)`, `0 Error(s)`.
3. Reasoned from the code: with six candidates all inside reach and `maxPoints = 3`, the cap now
   effectively admits 4 entries — the case **"six inside reach, cap holds to the three nearest"**
   fails (result is `[0,1,2,3]`, not `[0,1,2]`). The other three cases are unaffected because none
   of them has more in-reach candidates than the (now off-by-one) cap: the five-candidate case has
   only 3 in reach, the boundary case has only 1 candidate total, and the no-candidates case stays
   empty regardless of the cap.
4. Restored `result.Count < maxPoints`. SHA256 after restore:
   `09E5265BCD803369B19EB63AEF59CD50A4E5BB34DA1897E5F3377E79D2896661` — byte-identical to step 1.
   Rebuilt: `0 Warning(s)`, `0 Error(s)`.

### T10 surviving `LogInfo` lines without an HQ

`grep -n "LogInfo(" Ai/*.cs Economy/CommanderEconomyServiceEnemy.cs Units/CommanderRepairService.cs`
plus a direct check of `Economy/CommanderEconomyService.cs` (dock prefab resolution) — every survivor
is a roster/prefab/category resolution line with no per-decision `FactionHQ` to hang a tab on, or a
UI toggle notice, never an AI decision:

- `Ai/CommanderCaptureService.cs:410` — `"Vehicles that can capture ({hq.faction.name}): {names}."`,
  a one-time roster dump, not a decision.
- `Ai/CommanderEnemyCommanderAir.cs:310` — `"Air roster ({hq.faction.name}): ..."`, same shape.
- `Ai/CommanderPlayerCommanderService.cs:218` — `"Player commander switched ON/OFF for {faction}."`,
  the manual toggle notice from `Toggle()`, not an AI-authored line.
- `Economy/CommanderEconomyServiceEnemy.cs:312` — `"No {category} structure..."` /
  `"Commanders will build {structure} for {category} ({cost})."`, resolved once per mission, before
  any commander has decided anything.
- `Economy/CommanderEconomyService.cs:1123` — the naval dock prefab pick, same one-off resolution
  shape, no `hq` in scope.

Separately: `grep -rn "CommanderLabel" --include=*.cs .` returns **2** hits, not the 3 the plan
expected (definition + `CommanderAiLog.Note`) — there is no third, doc-comment hit anywhere in the
current codebase; `Ai/CommanderPlayerCommanderService.cs`'s `IsCommanded` doc comment does not
mention `CommanderLabel` by name. Recorded as a deviation (grep count) below; nothing was added or
removed to manufacture a third hit.

### Deviations from plan or design

1. **`Evaluate`'s new mine-siting rule is gated on `hq != null` (ledger row 1).** The row's literal
   wording has `Evaluate` call `TrySnapMineSite` for every mine definition regardless of `hq`. But
   T3's own discovery code calls `economy.IsSiteAllowed(mineDefinition, candidate, null, …)` to test
   whether a *candidate* position is legal ground for a *new* site — before that site is registered.
   If the new site-snap rule ran unconditionally, that very call would require a resource site to
   already exist near the candidate, which is circular: the first site on a map could never be
   found, because none is registered yet to snap to. The existing code already treats `hq == null`
   as "this call is a geometry/site-finding probe, not a real build" — it is exactly what turns off
   the radius check at `CommanderBuildPreview.cs`'s `if (hq != null && …)` line, which the plan's own
   T3 text cites as the reason the radius check is off during discovery. Extending that same
   convention to the new mine-siting rule (`if (mine && hq != null) { … }`) resolves the circularity
   without forking the rule: real build/click/AI paths always pass a real `hq` and get the new
   gating; discovery's `hq: null` probes get the old geometry-only behaviour, which is what
   discovery needs. Evidence: `Economy/CommanderBuildPreview.cs` `Evaluate`; the guard comment there
   cites this reasoning directly.
2. **T1's "18 Points entries" is 19.** The task text says "add the 18 `Points` entries" but then
   lists 19 names (it includes `MineSnapMeters` in the same sentence as the other 18). All 19 named
   settings were added — `FillGridMeters` through `MineSnapMeters` — matching the full constants
   table earlier in the plan. This is a miscount in the plan's prose, not an instruction to omit
   `MineSnapMeters`; nothing was left out.
3. **Ledger row 4 (the `IsSiteAllowed`/`MineDefinition` forwarding members on
   `CommanderEconomyService`) was added in T3, not T7.** T3's own task text calls
   `economy.IsSiteAllowed(mineDefinition, candidate, null, …)` and
   `CommanderEconomyService.Instance?.MineDefinition` before T7 (which is where "apply rows 1-4
   exactly" is written) ever runs. Row 4 is small, self-contained and has no dependency on rows 1-3
   (which need `TrySnapMineSite`/`IsInsideGarrisonedPointReach`, added in T7), so it was implemented
   early to satisfy T3's own forward reference. No behaviour differs from the ledger row's
   description; only which task's build first contains it.
4. **The T7 fail-proof plants `>=` where the plan's prose describes the boundary as `distances[i] <=
   snapMeters` -> `<`.** The actual implementation's skip condition is the logical complement,
   `distances[i] > snapMeters` (skip when strictly greater, i.e. keep when `<=`). Flipping the
   accept side from `<=` to `<` is written on the skip side as `>` becoming `>=`; the observable
   defect (the boundary case no longer counts as inside) and the named failing case are exactly what
   the plan asks for. See the T7 fail-proof record.
5. **T12's diamond/triangle/square markers are drawn as rotated line segments (`GUIUtility.RotateAroundPivot`
   per edge) rather than literal "stepping" axis-aligned bars.** The plan's wording ("a diamond is
   four short bars stepping diagonally, a triangle three, a square the four edges") describes the
   general shape-from-bars approach without giving exact coordinates; a true diagonal edge cannot be
   drawn with an axis-aligned `Bar` alone. The implementation still uses exactly 4 (diamond/square)
   or 3 (triangle) `CommanderUiTheme.Bar` calls per shape, in an outline (never filled, matching the
   existing `DrawWorldMarker` bracket style), with the GUI matrix saved and restored around each
   edge so it never leaks into the label draw that follows. Purely cosmetic, not covered by any
   self-check; flagged here per the plan's instruction to record every deviation, however small.
6. **`grep -rn "CommanderLabel(" --include=*.cs .` returns 2 hits, not the 3 the plan's T17 checklist
   expects.** Checked directly: no doc comment anywhere in the current codebase (including
   `Ai/CommanderPlayerCommanderService.cs`'s `IsCommanded` remarks) mentions `CommanderLabel` by
   name. The plan's expectation of a third hit does not match the code as it stands; nothing was
   added to manufacture one. See the T10 notes section for the same finding.
7. **`Ai/CommanderEnemyCommanderGarrison.cs`'s `garrisonPointUnits`, `garrisonTargetPoints`,
   `garrisonTargetDistances`, `garrisonTargetIndices`, `garrisonTargets` and `garrisonPosts` are
   additional scratch fields beyond the two the plan names (`garrisonCandidates`, `staleGarrison`).**
   Both named fields are used exactly as specified (recruitment candidates and prune/release
   scratch, mirroring `defenceCandidates`/`staleDefenders`). The extra fields are needed to hold the
   per-review target-selection working set (candidate points, their distances, the indices
   `SelectGarrisonTargets` returns, the resolved target list) and the per-point ring-post cache; none
   of them changes behaviour, they only avoid reallocating small lists every 10 s review.
   `garrisonPosts` (the one dictionary among them) is explicitly cleared in `ResetSession` even
   though the plan's row 12 only names the two list scratch fields, because it is keyed on
   `CommanderStrategicPoint` object identity and those objects do not survive a mission reload
   (discovery rebuilds the whole list from scratch) — leaving it uncleared would only grow with
   entries nothing can ever look up again.
8. **The village/hilltop hold-change log line (T5) and the garrison-fill log line (T9) route through
   `CommanderAiLog.Note` with the exact text the plan gives them** (`"{Label} is neutral."` /
   `"{Label} held by {faction}."` / `"garrisons {Label} with {n} unit(s)."`), which means the final
   console line carries the commander-label prefix `Note` always adds (e.g.
   `"Enemy commander (RANDIA) VILLAGE 3 held by RANDIA."`), naming the faction twice. The plan's T10
   text says these two lines "become `Note` too" without asking for new wording, so the literal text
   was kept; the redundancy is cosmetic (BepInEx console output only) and not covered by any
   self-check.

Everything else in the ledger, the constants table and the four originally recorded departures
(resource site placement beside its anchor building, the POINTS settings tab, the focused-point
selection-bar card, and `Points/MineSnapMeters`) was implemented exactly as written.

### Fix cycle 2 (execution evaluation)

An opus evaluator's execution-evaluation FAIL returned three fix items; all three applied.

1. **Missing/incomplete `<summary>`s on class-level constants.** `UI/CommanderAiLogUi.cs:16-18`
   (`WindowId`, `RefreshIntervalSeconds`, `RowHeight`) had no `<summary>` at all; added one to each,
   checked truthfully against `UI/CommanderUnitListUi.cs`: `WindowId = 0x434F4D41` (ASCII `COMA`)
   differs from that window's `0x434F4D4C` (`COML`) only in its last byte, which is all `GUI.Window`
   needs to tell them apart; `RefreshIntervalSeconds = 0.5f` matches
   `CommanderUnitListUi.RefreshIntervalSeconds` exactly (same constant, same value, same reason);
   `RowHeight = 26f` matches the literal `26f` row pitch `CommanderUnitListUi.DrawBattleLog` uses
   for its own battle-log rows (lines 152/161 there), not that window's own `RowHeight` constant
   (`30f`, which sizes its unit rows instead). Also extended
   `Points/CommanderStrategicPointMarkers.cs:13` (`WorldMarkerSize = 30f`, summary previously said
   only what it was) with why: checked `Units/CommanderWorldMarkerRenderer.cs`'s own world-marker
   sizes (`26f` standard, `34f` capture-progress ring, plus `44f`/`48f`/`14f`/`22f` for other
   specific markers) and recorded that `30f` sits between the `26f` and `34f` pair specifically, the
   two closest in kind to a strategic point marker. Also added the missing `<summary>` on
   `UI/CommanderAiLog.cs:20` (`logs`).
2. **`CHANGELOG.md:233-234` omitted departure 4's snap distance and config key.** The "Gold mines
   can only be built on a resource site" bullet said the ghost "snaps to the nearest free site
   within reach" with no number or setting name; amended to
   "(`Points/MineSnapMeters`, 1 km by default)", matching `README.md:640-641`'s existing wording.
3. **Cross-group point spacing never checked resource sites against villages/hilltops
   (`Points/CommanderStrategicPointDiscovery.cs`).** `ApplySpacing` (now ~line 684) always started
   its `kept` list empty, so the villages/hilltops pass (`StepBases`, ~line 539) never saw the sites
   the sites pass (`StepSites`, ~line 127) had already accepted — a village or hilltop could sit
   arbitrarily close to a resource site, which design.md section 1's Caps ("no two [non-base points]
   within 1.5 km") forbids. Gave `ApplySpacing` an optional `preKept` parameter (default `null`, one
   definition, Reuse rule 4): candidates are still blocked by the exclusion centres and by `kept` (the
   candidates this same call has accepted so far) exactly as before, and now also by `preKept` when
   given — checked, but never added to `kept` and never counted against `cap`, so the existing
   "earlier wins, newer dropped" ordering and the `MaxNonBasePoints`/site-cap semantics are
   unchanged. `StepBases`'s villages/hilltops call now passes `points` as `preKept` — at that point
   in discovery `points` holds only the already-accepted resource sites (bases are appended after
   this call, villages/hilltops are not added until after it either). The sites-vs-sites call in
   `StepSites` is unchanged (nothing exists yet to seed it with).

   Extended `CheckDiscoverySpacing` in `Points/CommanderStrategicPointService.cs` with two new named
   cases exercising `preKept` directly: **"a candidate within minSpacing of a pre-kept point is
   rejected"** (a pre-kept site 500 m from a candidate village, `minSpacing = 1500f`, `cap = 3`:
   candidate is dropped) and **"the same candidate is kept when there is no pre-kept list"** (the
   identical candidate, same call with `preKept` omitted: candidate is kept). Note: because of a
   concurrent, unrelated fix landing in this same file mid-cycle (see below), the villages/hilltops
   cap argument at the `StepBases` call site is `CommanderSettings.PointsMaxNonBasePoints` (a
   dedicated cap) rather than the `Mathf.Max(0, PointsMaxNonBasePoints - points.Count)` this task
   started from — orthogonal to this fix, so left as found.

   **Fail-proof record (Testing rule 5):**
   1. SHA256 of `Points/CommanderStrategicPointDiscovery.cs` with the fix in place, immediately
      before planting: `D025707145D384EAFE4BFD2D1F94554C2EA07D76E398F54AD80F26A899ECF9A3`.
   2. Planted defect in `ApplySpacing`: `if (preKept != null)` became `if (preKept != null && false)`
      — the pre-kept check compiles but never runs, i.e. the seeding is ignored and the pass
      behaves as if `kept` started empty, exactly as it did before this fix. Build: succeeded,
      `0 Warning(s)`, `0 Error(s)` (invisible to the compiler, as expected).
   3. Reasoned from the code with the defect in place, against the two new cases' synthetic input
      (a pre-kept site at `(20000,0,0)`, a candidate village at `(20500,0,0)`, `minSpacing = 1500f`):
      with the pre-kept branch disabled, `blocked` is never set by it, and `kept` starts empty for
      this call, so the candidate is not blocked by anything and is added — the case **"a candidate
      within minSpacing of a pre-kept point is rejected"** fails (`withPreKept.Contains(...)` is
      `true`, expected `false`). The sibling case **"the same candidate is kept when there is no
      pre-kept list"** is unaffected — it never passes a `preKept` argument, so this defect (which
      only short-circuits the `preKept != null` branch) changes nothing on that path, and it
      continues to pass. The original `CheckDiscoverySpacing` cases ("cap holds", "newer of a close
      pair is dropped", "nothing inside the airbase exclusion", "kept points respect spacing") are
      likewise untouched: none of their candidates pass a `preKept` argument either. This is a
      reasoned derivation from the code, not an observation from the running game.
   4. Restored `if (preKept != null)`. SHA256 after restore:
      `D025707145D384EAFE4BFD2D1F94554C2EA07D76E398F54AD80F26A899ECF9A3` — byte-identical to step 1.
      Rebuilt: `0 Warning(s)`, `0 Error(s)`.

   Note on the hash pair above: an earlier attempt at this same fail-proof, started right after this
   fix was first landed, recorded a "before" hash that a concurrent, unrelated edit from another
   agent working the same track (the cap-semantics change noted just above, in this same file) had
   already invalidated by the time of the "after restore" hash — the mismatch was a real
   label-rename (`SITE {i + 1}` -> `RESOURCE SITE {i + 1}`) landing mid-cycle, not an error in the
   restore. The record above is a second, tight attempt (hash immediately before planting, defect,
   build, revert, hash immediately after) that completed byte-identical.

The In-game acceptance line for point spacing (`no two are on top of each other`) needed no
caveat added or removed: no "a square or triangle may legitimately appear close to a diamond" text
was present in this file to remove, and this fix makes that scenario impossible going forward (see
fix 3), so the line stands as written, now true across both spacing passes rather than only within
each.

### Departure 5 — spacing picks farthest-first, not "newer dropped" (main session, 2026-09-13)

Recorded after the loop closed, at the team lead's instruction to fold the main session's in-game
fixes into this track as-is.

design.md section 1 states the spacing rule as "Minimum spacing ~2 km between sites; newer one
dropped on conflict" — a first-come rule where the earlier candidate wins every conflict and the cap
truncates whatever is left. In game on Ground Control Duel that produced a badly skewed map: 29 of
30 resource sites and 30 of 30 hilltops landed in the southern half, because the fill and hilltop
scans walk cells south to north and the cap ran out before the scan reached the north. The cap was
behaving as "how far north", not "how many".

`ApplySpacing` (`Points/CommanderStrategicPointDiscovery.cs`) now selects farthest-first: the first
eligible candidate seeds the kept set, and every later pick is the eligible candidate whose nearest
kept neighbour is farthest away, subject to the same minimum spacing. The exclusion pass and the
`preKept` cross-group seed (fix cycle 2) run first and are unchanged, so both keep their original
first-come, order-preserving behaviour.

Why this is a departure and not a refinement: the design's rule is order-dependent and this one is
not. Under the new rule the candidate that survives a close pair is whichever one the spread favours,
which is usually but not always the earlier one. The observable guarantee the design actually cared
about — no two points closer than the minimum spacing, and no more than the cap — is preserved
exactly. What changes is which points fill the cap.

Self-check: `CheckDiscoverySpacing` gained "spread: cap of three still holds" and "spread: the far
end of the map is reached" — eleven candidates in a line 1.6 km apart with a cap of three, where a
first-come rule keeps the three nearest the start and farthest-first must reach the far end. That is
the case that would have caught the southern-half clustering. The four original cases and the two
cross-group cases still pass unchanged under the new ordering (verified by hand: the line scenario
keeps x = 0, 16000 and 8000; the pre-kept village at 500 m from a kept site is still rejected in
pass 1, which farthest-first never sees).

Also folded in from the main session: resource sites and control points now have separate caps
(`Points/MaxResourceSites` and `Points/MaxNonBasePoints`) because one shared cap let a full set of
sites starve the villages and hilltops to zero; point labels read `RESOURCE SITE n` and `HILLTOP n`;
discovery retries up to three times at half-minute intervals when it finds no resource sites at all
(a hot reload can race the height map and return an empty result in 0.0 s); and while no sites
exist, the pre-points "build anywhere inside your base radius" rule stays in force for the player
ghost and both AI commanders, so a failed discovery pass can never lock a faction out of building a
mine. CHANGELOG.md and README.md were updated to describe the retry and the fallback, which had
shipped undocumented, and two stale README sentences (the village building count, and the single
non-base cap) were corrected.

USER TO DECIDE, not fixed here: the village and hilltop thresholds were lowered in design.md and in
the shipped defaults (cluster 300 -> 400 m, minimum buildings 4 -> 3, prominence 60 -> 30 m) after
the duel map produced zero of each at the approved numbers. Those new numbers have not been played.
