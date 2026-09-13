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

- [ ] **T1 — Points settings.** `Core/CommanderSettings.cs`: after `SamScanQueriesPerFrame` (line 103)
  add the 18 `Points` entries from the table (section `"Points"`, keys exactly as in the table:
  `FillGridMeters`, `SiteMinSpacingMeters`, `VillageClusterMeters`, `VillageMinBuildings` (int),
  `VillageRadiusMeters`, `HilltopGridMeters`, `HilltopRingMeters`, `HilltopProminenceMeters`,
  `HilltopRadiusMeters`, `HilltopVillageExclusionMeters`, `MaxNonBasePoints` (int),
  `PointMinSpacingMeters`, `AirbaseExclusionMeters`, `MinGarrison` (int), `HoldSeconds`,
  `BaseIncomePerMinute`, `VillageIncomePerMinute`, `HilltopIncomePerMinute`, `MineSnapMeters`) as
  `Get`/`Set` pairs in the style of line 88, each group under a one-line comment saying what it
  tunes (discovery spacing is config-file-only; garrison/hold/income are on the POINTS tab). Add
  `_ = …;` touches for all 18 after line 210 (`_ = SamScanQueriesPerFrame;`). Build.

- [ ] **T2 — Data type, service skeleton, registration.** Create `Points/CommanderStrategicPoint.cs`:
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

- [ ] **T3 — Discovery, part 1: resource sites (existing industry + grid fill) and the spacing filter,
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

- [ ] **T4 — Discovery, part 2: villages, hilltops (budgeted), bases, the table.**
  - `Villages`: from the same `Building[]` (kept from T3 in a field, cleared at `Done`), take
    `BuildingType.CIV` not `IsCommanderBuilt`; single-link clustering: for each building, join the
    first cluster whose *any* member is within `VillageClusterMeters` (O(n²) once; n is a few
    hundred — say so in a comment). Clusters with ≥ `VillageMinBuildings` → a `Village` at the
    centroid (`SnapToTerrain` once), `Radius = VillageRadiusMeters`. → `Hilltops`.
  - `Hilltops`: sample grid `HilltopGridMeters` over `mapSize` using
    `TryGetStrategicTerrainHeight`; a sample is a hilltop when `h > 1f`, it is ≥ every one of 8 ring
    samples at `HilltopRingMeters` (compass + diagonals) **and** `h − mean(ring) ≥
    HilltopProminenceMeters` (copy the shape of `SampleAverageHeight`,
    `…Sampling.cs:148-165`, widened to 8 points); skip a hit inside any airbase's
    `SavedAirbase.CaptureRange` (`Ai/CommanderCaptureService.cs:186`) or within
    `HilltopVillageExclusionMeters` of a village. **Budget**: `HilltopSamplesPerFrame = 256` (const,
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
  Build. No new self-check (the invariants are T3's; hilltop and village rules read the live map).

- [ ] **T5 — Hold state machine (pure) + the 5 s ring count, with self-check and fail-proof.** In
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

- [ ] **T6 — Income: bases, villages, hilltops on the mines' tick, with self-check and fail-proof.**
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

- [ ] **T7 — Mines only on sites; reach = base or garrisoned point (ledger rows 1-4).**
  In `Points/CommanderStrategicPointService.cs`: `internal bool TrySnapMineSite(GlobalPosition
  target, out GlobalPosition site)` — nearest `Site` with `Mine == null || Mine.disabled` within
  `MineSnapMeters` (horizontal), `site = point.Position`; `internal void AttachMine(GlobalPosition
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

- [ ] **T8 — Enemy mine step picks the nearest free reachable site (ledger rows 7-9).** Apply the
  rows. In `TryBuildEnemyMine` keep the log line; the text becomes `built a gold mine on
  {point.Label}.` — so add `internal bool TryPickFreeReachableSite(FactionHQ hq, out
  CommanderStrategicPoint point)` as the primary overload and make the `GlobalPosition` one call
  it. Update the `GetEnemyBuildReserve` summary (150-159) with one sentence: a mine is only wanted
  while a site is in reach, otherwise the commander saves for the next thing. Build. Grep
  `TryFindEnemyBuildSite(hq, mine` — expect no hits.

- [ ] **T9 — Garrison partial (ledger rows 10-14).** Create `Ai/CommanderEnemyCommanderGarrison.cs`,
  `internal sealed partial class CommanderEnemyCommanderService`, class-part `<summary>` (what a
  garrison is, that it uses the home guard's pinning mechanism and why — cite the Defence remarks
  12-28 in a `<see cref>`), `<remarks>` "ponytail:" the garrison stands still and shoots for itself,
  no patrol, no reaction to raids (later tracks). Constants with summaries: `GarrisonReachMeters =
  12000f`, `MaxGarrisonedPoints = 3`, `GarrisonArrivedMeters = 150f` (same as `DefenceArrivedMeters`
  at `Defence.cs:53` and for the same RPC reason — but a *second* identical constant is Reuse rule 4's
  tell, so **reuse `DefenceArrivedMeters` directly** and do not declare a new one). Scratch lists
  `garrisonCandidates`, `staleGarrison` (cleared in `ResetSession`, row 12).
  - `internal static bool IsGarrisonUnit(Unit? unit)` — the `IsDefendingUnit` shape
    (`Defence.cs:85-102`) over `state.Garrison`.
  - `private void ReviewGarrisons(FactionHQ localHq)` — the `ReviewDefences` loop shape
    (`Defence.cs:121-141`), per HQ `ReviewGarrison(hq, state)`.
  - `ReviewGarrison(hq, state)`: (a) prune: drop entries whose unit is null/disabled/other HQ/
    `HasPlayerOrder` (comment as `Defence.cs:306-310`: leave `commandedDestination` alone); (b) target
    points: Village/Hilltop points within `GarrisonReachMeters` of any base `hq` holds, sorted by
    distance to `GetTerritoryCenter(hq)`, first `MaxGarrisonedPoints` — a point another faction
    currently holds is still a target (taking it is the point); (c) for each target short of
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

- [ ] **T10 — One log funnel: `CommanderAiLog.Note` (ledger rows 15-16).** Create `UI/CommanderAiLog.cs`,
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

- [ ] **T11 — COMMANDER LOG window (ledger rows 20-21).** Create `UI/CommanderAiLogUi.cs`,
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

- [ ] **T12 — Points on the world and the tactical map (ledger row 23).** Create
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

- [ ] **T13 — Click a point to read it (ledger rows 24-26, departure 3).** In the markers partial:
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

- [ ] **T14 — POINTS settings tab (ledger row 22, departure 2).** `UI/CommanderOverlayUiSettings.cs`:
  row 22, then `private void DrawPointsSettings(float y)`: one `Panel` box, header `STRATEGIC POINTS`,
  then six slider rows on a 38 px pitch using the existing helpers — `MinGarrison` via
  `DrawCameraSlider(y, "Minimum garrison", value, 1f, 6f, "0", " vehicles")` rounded to an int;
  `HoldSeconds` via `DrawCameraSlider(…, 15f, 300f, "0", " s")` snapped to 5 s; the four income rates
  (`BaseIncomePerMinute` 0-100, `VillageIncomePerMinute` 0-50, `HilltopIncomePerMinute` 0-50,
  `GoldMineIncomePerMinute` 0-100) via `DrawCameraSlider(…, "0", " /min")` snapped to whole numbers.
  A muted footnote: `Discovery spacing lives in the config file, Points section.` Arithmetic in a
  comment: `y ≤ 162`, box 10 + 32 + 6×38 + 30 = 300 → 462 < 790. Build.

- [ ] **T15 — Verification environment: BepInEx console on (ledger row 28; not repo code).** Edit
  `I:\SteamLibrary\steamapps\common\Nuclear Option\BepInEx\config\BepInEx.cfg` line 48 under
  `[Logging.Console]`: `Enabled = false` → `Enabled = true`. Nothing else in the file changes.
  Verification: `Select-String -Path <cfg> -Pattern '^Enabled = true'` shows exactly one hit in the
  `[Logging.Console]` block (the file has several `Enabled` keys; quote the surrounding two lines in
  the notes). Tell the user in the notes that the console window will now open with the game and
  how to turn it back off.

- [ ] **T16 — CHANGELOG and README (ledger row 27).** `CHANGELOG.md`: new bullet at the top of the
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

- [ ] **T17 — Final build, greps, notes.** `dotnet build GroundControlRts.csproj -c Release`; paste the
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

_(paste the last 5 lines of the build and the greps here)_

### T3 fail-proof record (Testing rule 5)

_(SHA256 before, the planted defect, the named failing cases, SHA256 after — byte-identical)_

### T5 fail-proof record

### T6 fail-proof record

### T7 mine spawn-path trace

### T10 surviving `LogInfo` lines without an HQ

### Deviations from plan or design

_(every one, with the reason; "none" is an acceptable answer only after the greps above)_
