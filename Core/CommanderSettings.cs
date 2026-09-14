using BepInEx.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal static class CommanderSettings
{
    private static ConfigFile? config;
    private static readonly Dictionary<string, ConfigEntryBase> entries = new();

    // The one plain static here: derived from the screen size by CommanderUiScale, not from the
    // config file, so it is recomputed every launch and on every window resize.
    internal static float AutomaticUiScale { get; set; } = 1.5f;
    internal static float UiScale => CommanderUiScale.Resolve(UiScaleOverride, AutomaticUiScale);
    internal static bool ModEnabled { get => Get("General", "Enabled", true); set => Set("General", "Enabled", value); }
    // Off by default: this writes a JSON file to disk on every mission tick and is only worth the
    // cost while actively developing the mod, hot-reloading mid-match with build-dev.bat. See
    // CommanderStateStore for the write/read cycle and the session guard that keeps a stale file
    // from an earlier match from ever being replayed into a new one.
    // Key renamed from PersistStateAcrossReload when the default flipped to on: BepInEx keeps the
    // value already in the cfg, so a default change under the old key would never reach anyone.
    internal static bool PersistStateAcrossReload { get => Get("Developer", "KeepStateAcrossHotReload", true); set => Set("Developer", "KeepStateAcrossHotReload", value); }
    internal static bool LimitToFactoryVehicles { get => Get("Gameplay", "LimitToFactoryVehicles", false); set => Set("Gameplay", "LimitToFactoryVehicles", value); }
    internal static bool ShowCommandButton { get => Get("UI", "ShowCommandButton", true); set => Set("UI", "ShowCommandButton", value); }
    internal static bool ShowFactionMoney { get => Get("UI", "ShowFactionMoney", true); set => Set("UI", "ShowFactionMoney", value); }
    internal static bool ShowTacticalMap { get => Get("UI", "ShowTacticalMap", true); set => Set("UI", "ShowTacticalMap", value); }
    internal static bool ShowSelectionBar { get => Get("UI", "ShowSelectionBar", true); set => Set("UI", "ShowSelectionBar", value); }
    internal static bool ShowPinnedUnits { get => Get("UI", "ShowPinnedUnits", true); set => Set("UI", "ShowPinnedUnits", value); }
    internal static bool ShowUnitSystems { get => Get("UI", "ShowUnitSystems", true); set => Set("UI", "ShowUnitSystems", value); }
    internal static bool ShowDepotUi { get => Get("UI", "ShowDepotUi", true); set => Set("UI", "ShowDepotUi", value); }
    internal static bool ShowSupplyUi { get => Get("UI", "ShowSupplyUi", true); set => Set("UI", "ShowSupplyUi", value); }
    internal static bool ShowAirCommandUi { get => Get("UI", "ShowAirCommandUi", true); set => Set("UI", "ShowAirCommandUi", value); }
    internal static bool ShowNavalUi { get => Get("UI", "ShowNavalUi", true); set => Set("UI", "ShowNavalUi", value); }
    internal static bool ShowSamAnalyzerUi { get => Get("UI", "ShowSamAnalyzerUi", true); set => Set("UI", "ShowSamAnalyzerUi", value); }
    internal static bool ShowWorldMarkers { get => Get("UI", "ShowWorldMarkers", true); set => Set("UI", "ShowWorldMarkers", value); }
    internal static bool ShowUnitListUi { get => Get("UI", "ShowUnitListUi", true); set => Set("UI", "ShowUnitListUi", value); }
    internal static bool ShowBuildUi { get => Get("UI", "ShowBuildUi", true); set => Set("UI", "ShowBuildUi", value); }
    internal static float TacticalMapSize { get => Get("UI", "TacticalMapSize", 675f); set => Set("UI", "TacticalMapSize", value); }
    // Renamed from MapDragSensitivity because the meaning changed: the pan is now driven by real
    // cursor pixels against the map's on-screen scale, so 1.0 means the map sticks to the cursor.
    // A rename is the only way to reissue a default, since BepInEx keeps whatever is in the file.
    internal static float MapDragSpeed { get => Get("UI", "MapDragSpeed", 1f); set => Set("UI", "MapDragSpeed", value); }
    // 0 means automatic: the resolution preset in CommanderUiScale decides. Any positive value is
    // a manual multiplier the player set on the UI scale slider, and it wins over the preset so a
    // window resize can never undo a choice the player made by hand.
    internal static float UiScaleOverride { get => Get("UI", "UiScaleOverride", 0f); set => Set("UI", "UiScaleOverride", value); }
    internal static bool AutoFollowSelection { get => Get("Camera", "AutoFollowSelection", true); set => Set("Camera", "AutoFollowSelection", value); }
    // Selecting a unit attaches the follow but must not yank a camera the player just aimed, so
    // the camera only travels when the unit is off screen, near an edge, or too far to read.
    internal static bool AutoFrameSelection { get => Get("Camera", "AutoFrameOffscreenSelection", true); set => Set("Camera", "AutoFrameOffscreenSelection", value); }
    internal static float CameraPanSpeed { get => Get("Camera", "PanSpeed", 300f); set => Set("Camera", "PanSpeed", value); }
    internal static float CameraZoomSpeed { get => Get("Camera", "ZoomSpeed", 1f); set => Set("Camera", "ZoomSpeed", value); }
    // The RTS camera keeps its own look feel instead of borrowing PlayerSettings.viewSensitivity
    // and viewSmoothing, which are tuned for a pilot's head in a cockpit and read as lag here.
    internal static float CameraLookSensitivity { get => Get("Camera", "LookSensitivity", 1f); set => Set("Camera", "LookSensitivity", value); }
    internal static float CameraSmoothing { get => Get("Camera", "Smoothing", 0.05f); set => Set("Camera", "Smoothing", value); }
    internal static bool CameraHeightScaledSpeed { get => Get("Camera", "HeightScaledSpeed", true); set => Set("Camera", "HeightScaledSpeed", value); }
    internal static bool CameraEdgeScroll { get => Get("Camera", "EdgeScroll", false); set => Set("Camera", "EdgeScroll", value); }
    internal static bool CameraOrbitLook { get => Get("Camera", "OrbitLook", true); set => Set("Camera", "OrbitLook", value); }
    // Follow copies a smoothed anchor rather than the unit's exact per-frame movement, so an
    // aircraft's jitter does not become camera shake. Lead is off by default: it is a taste knob.
    internal static float FollowSmoothing { get => Get("Camera", "FollowSmoothing", 0.1f); set => Set("Camera", "FollowSmoothing", value); }
    internal static float FollowLeadSeconds { get => Get("Camera", "FollowLeadSeconds", 0f); set => Set("Camera", "FollowLeadSeconds", value); }
    internal static bool GroupHotkeys { get => Get("Gameplay", "GroupHotkeys", true); set => Set("Gameplay", "GroupHotkeys", value); }
    internal static bool CameraBookmarks { get => Get("Gameplay", "CameraBookmarks", true); set => Set("Gameplay", "CameraBookmarks", value); }
    internal static bool OrderFeedback { get => Get("Gameplay", "OrderFeedback", true); set => Set("Gameplay", "OrderFeedback", value); }
    internal static bool RetargetAfterKill { get => Get("Gameplay", "RetargetAfterKill", true); set => Set("Gameplay", "RetargetAfterKill", value); }
    internal static bool AttackMoveIntoRange { get => Get("Gameplay", "AttackMoveIntoRange", true); set => Set("Gameplay", "AttackMoveIntoRange", value); }
    internal static bool AttackMoveRoutes { get => Get("Gameplay", "AttackMoveRoutes", true); set => Set("Gameplay", "AttackMoveRoutes", value); }
    internal static bool GuardOrders { get => Get("Gameplay", "GuardOrders", true); set => Set("Gameplay", "GuardOrders", value); }
    internal static bool AutoRetreatDamaged { get => Get("Gameplay", "AutoRetreatDamaged", false); set => Set("Gameplay", "AutoRetreatDamaged", value); }
    internal static float RetreatConditionPercent { get => Get("Gameplay", "RetreatConditionPercent", 40f); set => Set("Gameplay", "RetreatConditionPercent", value); }
    internal static float WaypointHoldSeconds { get => Get("Gameplay", "WaypointHoldSeconds", 60f); set => Set("Gameplay", "WaypointHoldSeconds", value); }
    internal static int FormationShape { get => Get("Gameplay", "FormationShape", 0); set => Set("Gameplay", "FormationShape", value); }
    internal static float FormationCohesionMeters { get => Get("Gameplay", "FormationCohesionMeters", 300f); set => Set("Gameplay", "FormationCohesionMeters", value); }
    internal static bool CombatAlerts { get => Get("Gameplay", "CombatAlerts", true); set => Set("Gameplay", "CombatAlerts", value); }
    internal static int EnemyCommanderMode { get => Get("Gameplay", "EnemyCommanderMode", 0); set => Set("Gameplay", "EnemyCommanderMode", value); }
    // Off by default: the same commander AI that runs the enemy also runs your own faction, which
    // is a different game from the one the player opened the mission expecting. You keep command
    // while it is on - see CommanderPlayerCommanderService.
    internal static bool PlayerCommanderEnabled { get => Get("Gameplay", "PlayerCommanderEnabled", false); set => Set("Gameplay", "PlayerCommanderEnabled", value); }
    // How long the player commander keeps its hands off a unit after the player gives it an order,
    // in game minutes. "Until it arrives" was not enough: a platoon parked on a hill by hand was
    // being re-recruited into the home guard the moment it stopped moving.
    internal static float PlayerCommanderHandsOffMinutes { get => Get("Gameplay", "PlayerCommanderHandsOffMinutes", 10f); set => Set("Gameplay", "PlayerCommanderHandsOffMinutes", value); }
    // Economy prices are mission-relative: faction balances are authored per mission (about 1000
    // at the start of Escalation), so these are knobs, not constants.
    internal static float GoldMineCost { get => Get("Economy", "GoldMineCost", 250f); set => Set("Economy", "GoldMineCost", value); }
    internal static float GoldMineIncomePerMinute { get => Get("Economy", "GoldMineIncomePerMinute", 20f); set => Set("Economy", "GoldMineIncomePerMinute", value); }
    internal static float FactoryUpgradeCost { get => Get("Economy", "FactoryUpgradeCost", 300f); set => Set("Economy", "FactoryUpgradeCost", value); }
    internal static float FactoryBuildCost { get => Get("Economy", "FactoryBuildCost", 500f); set => Set("Economy", "FactoryBuildCost", value); }
    internal static float FactoryProductionSeconds { get => Get("Economy", "FactoryProductionSeconds", 240f); set => Set("Economy", "FactoryProductionSeconds", value); }
    // A catalogue building is priced off its own encyclopedia value, so a radar costs what a
    // radar is worth without the mod carrying a price table that a game patch would invalidate.
    internal static float BuildingCostMultiplier { get => Get("Economy", "BuildingCostMultiplier", 1f); set => Set("Economy", "BuildingCostMultiplier", value); }
    internal static float RepairCrewCost { get => Get("Economy", "RepairCrewCost", 150f); set => Set("Economy", "RepairCrewCost", value); }
    // Both commanders may only build within this distance of an airbase their faction holds.
    internal static float BuildRadiusKm { get => Get("Economy", "BuildRadiusKm", 2.5f); set => Set("Economy", "BuildRadiusKm", value); }
    // A naval dock has to reach the coast, which is usually further out than the base perimeter,
    // so it gets its own (larger) radius instead of loosening the rule for every building. 12 km
    // because 7 was not enough on the duel map: the nearest usable shoreline to a duel base is
    // further out than the stock missions' sea-level objects suggested, so the dock could not be
    // placed at all while standing on the beach.
    internal static float NavalDockRadiusKm { get => Get("Economy", "NavalDockRadiusKm", 12f); set => Set("Economy", "NavalDockRadiusKm", value); }
    // How far from the water's edge a dock may sit. A shoreline is a band, not a line.
    internal static float NavalDockShoreMeters { get => Get("Economy", "NavalDockShoreMeters", 90f); set => Set("Economy", "NavalDockShoreMeters", value); }
    internal static float NavalDockCost { get => Get("Economy", "NavalDockCost", 400f); set => Set("Economy", "NavalDockCost", value); }
    internal static float NavalDockUpgradeCost { get => Get("Economy", "NavalDockUpgradeCost", 450f); set => Set("Economy", "NavalDockUpgradeCost", value); }
    // What one aircraft parked inside a capture ring is worth. Aircraft carry no capture strength
    // of their own unless they are holding a troop pod, so this is the mod granting it - roughly a
    // light vehicle's worth, so a base still wants a few airframes or a ground squad.
    internal static float AircraftCaptureStrength { get => Get("Gameplay", "AircraftCaptureStrength", 2f); set => Set("Gameplay", "AircraftCaptureStrength", value); }
    internal static int SamScanQueriesPerFrame { get => Get("SAM Analyzer", "RaycastsPerFrame", 64); set => Set("SAM Analyzer", "RaycastsPerFrame", value); }

    // Discovery spacing (config-file-only: retuning these is a map-authoring decision, not a
    // player taste knob, so there is no slider for them).
    // One generated resource site per cell this large that has no existing industrial building in
    // it, so a map with no industry at all still gets an even spread instead of nothing at all.
    internal static float PointsFillGridMeters { get => Get("Points", "FillGridMeters", 8000f); set => Set("Points", "FillGridMeters", value); }
    // Two resource sites (existing or generated) never sit closer than this; the later one in
    // discovery order is dropped so the earlier (existing-building) site always wins a conflict.
    internal static float PointsSiteMinSpacingMeters { get => Get("Points", "SiteMinSpacingMeters", 2000f); set => Set("Points", "SiteMinSpacingMeters", value); }
    // Civilian buildings within this of another cluster member chain into the same village.
    internal static float PointsVillageClusterMeters { get => Get("Points", "VillageClusterMeters", 400f); set => Set("Points", "VillageClusterMeters", value); }
    // A cluster smaller than this is a farmstead, not a village worth fighting over.
    internal static int PointsVillageMinBuildings { get => Get("Points", "VillageMinBuildings", 3); set => Set("Points", "VillageMinBuildings", value); }
    // Control ring radius around a village's building centroid.
    internal static float PointsVillageRadiusMeters { get => Get("Points", "VillageRadiusMeters", 400f); set => Set("Points", "VillageRadiusMeters", value); }
    // Height-map sample spacing for the hilltop scan; finer than this buys little (the strategic
    // height map itself is 20 m/px) and costs more per-frame samples.
    internal static float PointsHilltopGridMeters { get => Get("Points", "HilltopGridMeters", 1000f); set => Set("Points", "HilltopGridMeters", value); }
    // A hilltop sample must be the highest point within this ring to count as a local high point.
    internal static float PointsHilltopRingMeters { get => Get("Points", "HilltopRingMeters", 1500f); set => Set("Points", "HilltopRingMeters", value); }
    // …and at least this far above the ring's mean height, or every gentle rise on a map would
    // qualify as a hilltop. Lowered from 15 to 8 (2026-09-13, alongside outposts/crossroads/roadside
    // points): hilltops were already the rarest kind of point, and adding three more kinds to the
    // same spacing budget only made a high threshold worse.
    internal static float PointsHilltopProminenceMeters { get => Get("Points", "HilltopMinProminenceMeters", 8f); set => Set("Points", "HilltopMinProminenceMeters", value); }
    // Control ring radius around a hilltop.
    internal static float PointsHilltopRadiusMeters { get => Get("Points", "HilltopRadiusMeters", 300f); set => Set("Points", "HilltopRadiusMeters", value); }
    // No hilltop within this of a village: the village is already the point of interest there.
    internal static float PointsHilltopVillageExclusionMeters { get => Get("Points", "HilltopVillageExclusionMeters", 1000f); set => Set("Points", "HilltopVillageExclusionMeters", value); }
    // Control ring radius around an outpost — a civilian cluster too small to be a village.
    internal static float PointsOutpostRadiusMeters { get => Get("Points", "OutpostRadiusMeters", 300f); set => Set("Points", "OutpostRadiusMeters", value); }
    // Control ring radius around a road-network junction.
    internal static float PointsCrossroadsRadiusMeters { get => Get("Points", "CrossroadsRadiusMeters", 300f); set => Set("Points", "CrossroadsRadiusMeters", value); }
    // Control ring radius around a roadside point — smaller than the others: it is a waypoint on an
    // otherwise empty stretch of road, not a place with much to stand around in.
    internal static float PointsRoadsideRadiusMeters { get => Get("Points", "RoadsideRadiusMeters", 250f); set => Set("Points", "RoadsideRadiusMeters", value); }
    // Distance along a road between generated roadside points. Long enough that a road already
    // carrying a village, hilltop or crossroads every few kilometres does not also collect a
    // roadside point on top of them.
    internal static float PointsRoadsideSpacingMeters { get => Get("Points", "RoadsideSpacingMeters", 6000f); set => Set("Points", "RoadsideSpacingMeters", value); }
    // A road-network junction node needs at least this many roads meeting (or passing through) it
    // to be worth calling a crossroads rather than an ordinary bend or a dead end.
    internal static int PointsCrossroadsMinRoads { get => Get("Points", "CrossroadsMinRoads", 3); set => Set("Points", "CrossroadsMinRoads", value); }
    // Per-kind ceilings inside MaxControlPoints. Without them the first stage ate the whole
    // allowance: the duel map has 263 road junctions, so crossroads took 44 slots, hilltops got 15
    // and road points none. Hilltops take whatever these leave; a kind with few candidates on a map
    // simply hands its share on.
    internal static int PointsMaxCrossroads { get => Get("Points", "MaxCrossroads", 24); set => Set("Points", "MaxCrossroads", value); }
    internal static int PointsMaxOutposts { get => Get("Points", "MaxOutposts", 24); set => Set("Points", "MaxOutposts", value); }
    internal static int PointsMaxRoadPoints { get => Get("Points", "MaxRoadPoints", 30); set => Set("Points", "MaxRoadPoints", value); }
    // Junctions are dense wherever roads are, so crossroads keep a wider spacing than other control
    // points or every hamlet's T-junction becomes one; 2.5 km reads as "the next crossroads along".
    internal static float PointsCrossroadsSpacingMeters { get => Get("Points", "CrossroadsSpacingMeters", 2500f); set => Set("Points", "CrossroadsSpacingMeters", value); }
    // Road endpoints (or a road segment passing near another road's endpoint) within this of each
    // other merge into the same junction node — wide enough that a junction authored as two
    // close-together forks in the road data still merges into one crossroads candidate.
    internal static float PointsRoadJunctionMergeMeters { get => Get("Points", "RoadJunctionMergeMeters", 60f); set => Set("Points", "RoadJunctionMergeMeters", value); }
    // Cap on control points (villages, hilltops, outposts, crossroads, roadside points) per map, so
    // a huge map does not drown the tactical map (or the AI's garrison review) in markers. Resource
    // sites have their own cap below: one shared cap let 30 sites use up the whole allowance and
    // every control point was dropped. Raised from 60 to 120 (2026-09-13): three more kinds now
    // share the same allowance, and 60 left the lowest-priority stage (roadside points) with
    // nothing to spend.
    // Key renamed from MaxNonBasePoints (and ControlPointSpacingMeters, HilltopMinProminenceMeters
    // likewise) on 2026-09-13 so the new defaults reach existing installs: BepInEx keeps whatever
    // value is already in the file, and a hot reload was re-saving the old numbers over hand edits.
    internal static int PointsMaxNonBasePoints { get => Get("Points", "MaxControlPoints", 120); set => Set("Points", "MaxControlPoints", value); }
    internal static int PointsMaxResourceSites { get => Get("Points", "MaxResourceSites", 30); set => Set("Points", "MaxResourceSites", value); }
    // No two non-base points closer than this; the earlier one in discovery order wins. Lowered
    // from 1500 to 800 (2026-09-13, alongside outposts/crossroads/roadside points): the old spacing
    // was tuned for a map with only villages and hilltops on it, and left flat farmland almost as
    // empty as before once three more kinds were competing for the same allowance.
    internal static float PointsPointMinSpacingMeters { get => Get("Points", "ControlPointSpacingMeters", 800f); set => Set("Points", "ControlPointSpacingMeters", value); }
    // No non-base point within this of an airbase centre — a base is already worth holding on its
    // own and a point crowding it would be redundant and hard to read on the map.
    internal static float PointsAirbaseExclusionMeters { get => Get("Points", "AirbaseExclusionMeters", 2000f); set => Set("Points", "AirbaseExclusionMeters", value); }

    // Garrison, hold and income (POINTS settings tab).
    // Ground vehicles a single faction needs inside a ring, alone, to count as present at all.
    internal static int PointsMinGarrison { get => Get("Points", "MinGarrison", 2); set => Set("Points", "MinGarrison", value); }
    // Cumulative seconds a faction must hold a point alone with at least MinGarrison before it
    // flips; short enough to reward a fast platoon, long enough that a driving-through raid does
    // not flip it by accident.
    internal static float PointsHoldSeconds { get => Get("Points", "HoldSeconds", 60f); set => Set("Points", "HoldSeconds", value); }
    // Per airbase held, paid on the shared 15 s income tick.
    internal static float PointsBaseIncomePerMinute { get => Get("Points", "BaseIncomePerMinute", 30f); set => Set("Points", "BaseIncomePerMinute", value); }
    // Per village held. Below a base's rate: a village is worth less than the airbase that lets you
    // build there, but still worth a platoon's time.
    internal static float PointsVillageIncomePerMinute { get => Get("Points", "VillageIncomePerMinute", 10f); set => Set("Points", "VillageIncomePerMinute", value); }
    // Per hilltop held; lowest of the three because a hilltop has no buildings to defend, only the
    // ground itself.
    internal static float PointsHilltopIncomePerMinute { get => Get("Points", "HilltopIncomePerMinute", 5f); set => Set("Points", "HilltopIncomePerMinute", value); }
    // Per outpost held; same rate as a hilltop — a cluster too small to be a village has no more to
    // defend than open ground does.
    internal static float PointsOutpostIncomePerMinute { get => Get("Points", "OutpostIncomePerMinute", 5f); set => Set("Points", "OutpostIncomePerMinute", value); }
    // Per crossroads held; same rate as a village — a junction is worth fighting over on its own,
    // not merely as a shortcut through it.
    internal static float PointsCrossroadsIncomePerMinute { get => Get("Points", "CrossroadsIncomePerMinute", 10f); set => Set("Points", "CrossroadsIncomePerMinute", value); }
    // Per roadside point held; the lowest rate of the six — a generated waypoint with nothing else
    // to recommend it, there only so an empty stretch of road is not empty of anything to fight over.
    internal static float PointsRoadsideIncomePerMinute { get => Get("Points", "RoadsideIncomePerMinute", 3f); set => Set("Points", "RoadsideIncomePerMinute", value); }
    // How far the mine-placement ghost snaps to the nearest free resource site. Design (§2) does
    // not give a number for this; without one the ghost could jump to a site many kilometres away.
    // 1 km keeps the snap feeling local while still forgiving imprecise clicking near a site.
    internal static float PointsMineSnapMeters { get => Get("Points", "MineSnapMeters", 1000f); set => Set("Points", "MineSnapMeters", value); }

    // Platoon recipe (config-file-only: a map-authoring / balance decision, not a player taste
    // knob, so there is no slider — see Operations/CommanderPlatoon.cs "Which vehicle is which
    // role"). The three slot counts must add up to PlatoonSize; the operations self-check says so
    // at load if a hand edit breaks that.
    // One BepInEx log line per commander per 30 s review describing the whole operations state
    // (pool, every platoon and its state, every mission, front/rear counts, pressure, order book),
    // plus a line per platoon state change and per depot claim. On while the doctrine is being
    // tuned so a match can be debugged from LogOutput.log alone; the COMMANDER LOG window shows
    // decisions, not this machinery.
    internal static bool OperationsDebugLog { get => Get("Operations", "DebugLog", true); set => Set("Operations", "DebugLog", value); }
    // Full platoons a commander keeps wanting with no job assigned. This is what keeps the order
    // book open (and the buyer forming platoons) before the first mission exists. One since the
    // pickets-first doctrine (2026-09-14): a platoon now forms only for a forward base on a front
    // point, an attack, or this reserve, so the reserve is the whole of the commander's spare
    // ground force rather than a floor under a force that grew on its own. Two kept a second full
    // platoon parked at the base that the picket line would rather have had as six vehicles.
    // Key renamed from ReservePlatoons when the default dropped 2 -> 1 (pickets-first, 2026-09-14):
    // BepInEx keeps the value already in the cfg, so a default change under the old key never lands.
    internal static int OperationsReservePlatoons { get => Get("Operations", "ReservePlatoonCount", 1); set => Set("Operations", "ReservePlatoonCount", value); }
    // Operations/MaxPlatoons was deleted with the pickets-first doctrine (DECISION-013). The cap it
    // held never bound — any open requisition lifted it and the order book never emptied — and what
    // replaced it is GroundBuyingBookOnly: a commander buys ground vehicles only for lines on its
    // order book. The key is left orphaned in existing config files on purpose; nothing reads it.
    internal static int OperationsPlatoonSize { get => Get("Operations", "PlatoonSize", 6); set => Set("Operations", "PlatoonSize", value); }
    internal static int OperationsRecipeArmour { get => Get("Operations", "RecipeArmour", 3); set => Set("Operations", "RecipeArmour", value); }
    internal static int OperationsRecipeCarrier { get => Get("Operations", "RecipeCarrier", 1); set => Set("Operations", "RecipeCarrier", value); }
    internal static int OperationsRecipeAirDefence { get => Get("Operations", "RecipeAirDefence", 2); set => Set("Operations", "RecipeAirDefence", value); }

    // Front line, forward bases and the pressure clock (POINTS tab, OPERATIONS box).
    // A point within this of the nearest enemy-held point or base is front line; equal to the home
    // guard's ThreatRadiusMeters on purpose (Ai/CommanderEnemyCommanderDefence.cs:60) — the same
    // "it can see it" range.
    internal static float OperationsFrontRangeMeters { get => Get("Operations", "FrontRangeMeters", 15000f); set => Set("Operations", "FrontRangeMeters", value); }
    // At most this share of a commander's platoons sit in forward bases; the rest are reserve or
    // offensive. Guards against a commander that only ever garrisons.
    internal static float OperationsFobShare { get => Get("Operations", "FobShare", 0.5f); set => Set("Operations", "FobShare", value); }
    // Minutes of pressure before the commander attacks with whatever it has. Guards against a
    // commander that never attacks.
    internal static float OperationsPressureIntervalMinutes { get => Get("Operations", "PressureIntervalMinutes", 12f); set => Set("Operations", "PressureIntervalMinutes", value); }
    // Share of the pot the buy review spends while an attack requisition is open, in place of the
    // 0.25 / 0.45 tempo knob the buyer normally uses.
    internal static float OperationsOffensiveSpendFraction { get => Get("Operations", "OffensiveSpendFraction", 0.5f); set => Set("Operations", "OffensiveSpendFraction", value); }
    // Minutes after losing an airframe over an objective before the commander buys another for that
    // same objective (doubled while the objective's ring shows at least two tracked hostile
    // air-defence units). Stops the commander feeding CAS one airframe at a time into a SAM line.
    internal static float CasLossCooldownMinutes { get => Get("Operations", "CasLossCooldownMinutes", 2f); set => Set("Operations", "CasLossCooldownMinutes", value); }
    // Aircraft a commander's faction may have in the world at once, on every mission: the bought
    // wing (CAP and CAS), the wing's transports, the picket-insertion helicopters and anything the
    // player or a stock mission put up — it counts every faction aircraft, so insertion flights and
    // the player's own AIR-window launches eat into it too. Was the duel-only DuelAirborneLimit
    // (8); raised to 12 on 2026-09-13 because 8 was the binding limiter in play (the commander sat
    // at it with money in hand), and a CAP-first wing — one fighter per active objective plus CAS —
    // does not fit under eight once three objectives are live.
    // 20 (user decision 2026-09-13): a safety stop, not the limiter — the air fund, hull prices and
    // rearm cycles are what should size the wing. Counts every live aircraft of the faction.
    internal static int AirborneCeiling { get => Get("Operations", "AirborneCeiling", 20); set => Set("Operations", "AirborneCeiling", value); }

    // The smarter air wing (design.md, smarter-air-wing_20260914; user decision 2026-09-14).
    // Config-file-only: doctrine knobs, not player taste, so there is no slider for them.
    // Seconds a package waits at its form-up orbit after the first airframe reaches it before it
    // goes in with whoever is there. 180 (user decision 2026-09-14): runway restrictions launch a
    // four-aircraft demand one airframe at a time, roughly a minute apart, so a wait shorter than
    // three minutes never assembles a package at all — and a longer one leaves the ground attack
    // that is holding for CAS standing at its release point past its own 240 s form-up timeout.
    internal static float PackageFormUpSeconds { get => Get("Operations", "PackageFormUpSeconds", 180f); set => Set("Operations", "PackageFormUpSeconds", value); }
    // How far from an objective a pad or strip may be and still launch the attack helicopters that
    // cover it. 40 km (user decision 2026-09-14): at the rotary transit speed the wing already uses
    // (80 m/s) that is about eight minutes each way, which a helicopter's fuel and a forward base's
    // patience both carry. Past it the sortie falls back to a jet.
    internal static float RotaryCasRangeMeters { get => Get("Operations", "RotaryCasRangeMeters", 40000f); set => Set("Operations", "RotaryCasRangeMeters", value); }
    // Tracked hostile air-defence vehicles that have to sit inside one 5 km cluster before the wing
    // opens an anti-radiation sortie on it. 3 (user decision 2026-09-14): one or two vehicles is the
    // ordinary air-defence a platoon carries and CAS is expected to survive; three together is a
    // prepared belt, which is what kills CAS one airframe at a time.
    internal static int AradClusterMinimum { get => Get("Operations", "AradClusterMinimum", 3); set => Set("Operations", "AradClusterMinimum", value); }

    // Picket insertion by transport helicopter (design.md, heli-picket-insertion_20260913).
    // Master toggle: visible AI spend, killable like every doctrine feature.
    internal static bool OperationsHeliInsertionEnabled { get => Get("Operations", "HeliInsertionEnabled", true); set => Set("Operations", "HeliInsertionEnabled", value); }
    // A picket point farther than this from the nearest road is flown in rather than driven (the
    // user's gate, 2026-09-13). Within it the drive is a short cross-country hop; beyond it the
    // crawl into the ring costs the point minutes of income. POINTS tab, OPERATIONS box.
    internal static float OperationsHeliInsertionOffRoadMeters { get => Get("Operations", "HeliInsertionOffRoadMeters", 2000f); set => Set("Operations", "HeliInsertionOffRoadMeters", value); }
    // Insertion flights airborne per HQ at once (config-only: a balance knob in the recipe's
    // company). Transports deliver, they do not win fights — the same reasoning as the enemy
    // commander's TransportLimit. Three since the pickets-first doctrine (2026-09-14): pickets are
    // now the capture mechanic on every point away from the front, and a mountain map has more
    // roadless hilltops than one flight at a time can ever garrison — a lift takes minutes, so at
    // one flight the commander was still on its second hilltop when the match turned.
    // Key renamed from HeliInsertionLimit when the default rose 1 -> 3 (pickets-first, 2026-09-14), same reason as ReservePlatoonCount.
    internal static int OperationsHeliInsertionLimit { get => Get("Operations", "HeliInsertionFlights", 3); set => Set("Operations", "HeliInsertionFlights", value); }
    // Minutes after losing an insertion flight before the same point asks again (config-only;
    // confirmed by the user, 2026-09-13). An identical loss on retry a minute later is a waste;
    // an hour is cowardice.
    internal static float OperationsHeliInsertionCooldownMinutes { get => Get("Operations", "HeliInsertionCooldownMinutes", 10f); set => Set("Operations", "HeliInsertionCooldownMinutes", value); }

    // Ground tactics (design.md, ground-tactics_20260914; user decision 2026-09-14, DECISION-012).
    // Config-file-only: doctrine knobs, not player taste, so there is no slider for them.
    // How far beyond its hold ring a garrison's tanks and IFVs push when the point comes under
    // attack. 400 m: far enough that the armour meets an attack in front of the vehicles that keep
    // the point paying rather than on top of them, and inside the 2.5 km at which the contact is
    // noticed at all, so the arc forms before the shooting starts. On a point whose own radius is
    // smaller than this the standoff is cut to that radius (CommanderOperationsService
    // .DefenceArcDistanceMeters), so a tiny point never throws its armour a whole radius away.
    internal static float DefenceArcStandoffMeters { get => Get("Operations", "DefenceArcStandoffMeters", 400f); set => Set("Operations", "DefenceArcStandoffMeters", value); }
    // How far an attacking platoon advances in one cross-country bound once it has left its release
    // point. 800 m: about one movement-tick-and-a-half of driving for a tank, short enough that the
    // platoon re-forms its line several times on the way in rather than arriving strung out, and
    // long enough that a 5 km approach is a handful of bounds and not a crawl. Reduce to 400 if the
    // game's ground AI is seen snapping an 800 m off-road leg back onto the road network.
    internal static float BoundMeters { get => Get("Operations", "BoundMeters", 800f); set => Set("Operations", "BoundMeters", value); }
    // Within this of a tracked enemy every ground destination is issued as a bound rather than as a
    // long path the game's own routing may take down a road. 1000 m (the user's rule, 2026-09-14):
    // inside a kilometre of a known enemy a road is an ambush, and the last kilometre is the part
    // worth driving across country.
    internal static float OffRoadRangeMeters { get => Get("Operations", "OffRoadRangeMeters", 1000f); set => Set("Operations", "OffRoadRangeMeters", value); }

    // The commander priority ladder (design.md, commander-priorities_20260914; user decision
    // 2026-09-14): every commanded HQ spends from ONE pot per review, in a fixed order — home CAP
    // first and strict, then platoons and their air, pickets, buildings sharing what is left by a
    // weighted draw. Config-file-only: these are doctrine knobs, not player taste, so there is no
    // slider for them.
    // Home-CAP fighters the commander keeps over its own airbases before it will spend on anything
    // else (user decision 2026-09-14: "strict" — while short, nothing below it is bought).
    internal static int HomeCapBaseline { get => Get("Commander", "HomeCapBaseline", 2); set => Set("Commander", "HomeCapBaseline", value); }
    // One more home-CAP fighter per this many tracked enemy aircraft inside 30 km of the commander's
    // airbases. Two: a raid is a pair, and the wing scales with what is actually spotted, not with
    // what might exist.
    internal static int HomeCapPerEnemyAircraft { get => Get("Commander", "HomeCapPerEnemyAircraft", 2); set => Set("Commander", "HomeCapPerEnemyAircraft", value); }
    // Ceiling on home-CAP fighters wanted. 4 (user decision 2026-09-14, replacing "no limit"): each
    // side's home CAP counted as enemy aircraft for the other, so 2 + 1 per 2 fed itself and both
    // sides bought CAP faster and faster. Threats beyond the ceiling are met by platoons requesting
    // CAP over themselves when enemy aircraft appear near them, not by more fighters over the base.
    internal static int HomeCapMax { get => Get("Commander", "HomeCapMax", 4); set => Set("Commander", "HomeCapMax", value); }
    // Relative weights of the three lower rungs in each review's draw (platoons and their air /
    // air-delivered pickets / buildings). Platoons at 60: "the map is huge, so there are nearly
    // always more platoons that could be built" (user decision 2026-09-14) — the draw adds
    // variability per review, not per commander, and the rung floor below keeps the other two
    // always reachable.
    internal static float LadderPlatoonWeight { get => Get("Commander", "LadderPlatoonWeight", 60f); set => Set("Commander", "LadderPlatoonWeight", value); }
    internal static float LadderPicketWeight { get => Get("Commander", "LadderPicketWeight", 20f); set => Set("Commander", "LadderPicketWeight", value); }
    internal static float LadderBuildingWeight { get => Get("Commander", "LadderBuildingWeight", 20f); set => Set("Commander", "LadderBuildingWeight", value); }
    // Percent of the post-CAP remainder reserved for every rung that has open demand, so the weighted
    // draw cannot starve pickets or buildings however heavy the platoon weight is. Ten percent of the
    // remainder per demanding rung; money nobody spends stays in the balance.
    internal static float LadderRungFloorPercent { get => Get("Commander", "LadderRungFloorPercent", 10f); set => Set("Commander", "LadderRungFloorPercent", value); }

    internal static KeyboardShortcut PrimaryAction { get => GetShortcut("PrimaryAction", KeyCode.Mouse0, "Select units and place world targets."); set => Set("Keybinds", "PrimaryAction", value); }
    internal static KeyboardShortcut SecondaryAction { get => GetShortcut("SecondaryAction", KeyCode.Mouse1, "Issue move orders."); set => Set("Keybinds", "SecondaryAction", value); }
    internal static KeyboardShortcut AddToSelection { get => GetShortcut("AddToSelection", KeyCode.LeftShift, "Hold while selecting to add units."); set => Set("Keybinds", "AddToSelection", value); }
    internal static KeyboardShortcut RepeatDeployment { get => GetShortcut("RepeatDeployment", KeyCode.LeftShift, "Hold while placing a supply target to repeat the deployment."); set => Set("Keybinds", "RepeatDeployment", value); }
    internal static KeyboardShortcut DeleteUnitModifier { get => GetShortcut("DeleteUnitModifier", KeyCode.LeftAlt, "Hold to turn PIN into DEL."); set => Set("Keybinds", "DeleteUnitModifier", value); }
    internal static KeyboardShortcut CameraCenterFollow { get => GetShortcut("CameraCenterFollow", KeyCode.Space, "Tap to center; hold to center and follow."); set => Set("Keybinds", "CameraCenterFollow", value); }
    internal static KeyboardShortcut ToggleUi { get => GetShortcut("ToggleUi", KeyCode.H, "Cycle visible, RTS UI hidden, and all UI hidden."); set => Set("Keybinds", "ToggleUi", value); }
    internal static KeyboardShortcut CameraForward { get => GetShortcut("CameraForward", KeyCode.W, "Move the RTS camera forward."); set => Set("Keybinds", "CameraForward", value); }
    internal static KeyboardShortcut CameraBackward { get => GetShortcut("CameraBackward", KeyCode.S, "Move the RTS camera backward."); set => Set("Keybinds", "CameraBackward", value); }
    internal static KeyboardShortcut CameraLeft { get => GetShortcut("CameraLeft", KeyCode.A, "Move the RTS camera left."); set => Set("Keybinds", "CameraLeft", value); }
    internal static KeyboardShortcut CameraRight { get => GetShortcut("CameraRight", KeyCode.D, "Move the RTS camera right."); set => Set("Keybinds", "CameraRight", value); }
    internal static KeyboardShortcut CameraUp { get => GetShortcut("CameraUp", KeyCode.Q, "Move the RTS camera upward."); set => Set("Keybinds", "CameraUp", value); }
    internal static KeyboardShortcut CameraDown { get => GetShortcut("CameraDown", KeyCode.E, "Move the RTS camera downward."); set => Set("Keybinds", "CameraDown", value); }
    internal static KeyboardShortcut QueueWaypoint { get => GetShortcut("QueueWaypoint", KeyCode.LeftShift, "Hold while ordering to append a travel point instead of replacing the route."); set => Set("Keybinds", "QueueWaypoint", value); }
    internal static KeyboardShortcut AssignGroupModifier { get => GetShortcut("AssignGroupModifier", KeyCode.LeftControl, "Hold with 1-9 to store the selection as that group."); set => Set("Keybinds", "AssignGroupModifier", value); }
    internal static KeyboardShortcut StopOrder { get => GetShortcut("StopOrder", KeyCode.X, "Cancel RTS orders and hold the selection where it stands."); set => Set("Keybinds", "StopOrder", value); }
    internal static KeyboardShortcut SelectSameType { get => GetShortcut("SelectSameType", KeyCode.LeftControl, "Hold while clicking a unit to select every unit of that type the faction owns."); set => Set("Keybinds", "SelectSameType", value); }
    internal static KeyboardShortcut CycleIdleUnit { get => GetShortcut("CycleIdleUnit", KeyCode.Period, "Select and jump to the next friendly ground/naval unit with no RTS order."); set => Set("Keybinds", "CycleIdleUnit", value); }
    internal static KeyboardShortcut CameraFreeLook { get => GetShortcut("CameraFreeLook", KeyCode.Mouse2, "Hold while moving the mouse to look around in RTS mode."); set => Set("Keybinds", "CameraFreeLook", value); }
    internal static KeyboardShortcut CameraBoost { get => GetShortcut("CameraBoost", KeyCode.LeftShift, "Hold for faster RTS camera movement."); set => Set("Keybinds", "CameraBoost", value); }
    internal static KeyboardShortcut MapBoxSelect { get => GetShortcut("MapBoxSelect", KeyCode.LeftControl, "Hold while dragging on the map to draw a selection box; a plain drag pans the map."); set => Set("Keybinds", "MapBoxSelect", value); }
    internal static KeyboardShortcut TogglePlayerCommander { get => GetShortcut("TogglePlayerCommander", KeyCode.None, "Toggle the AI commander for your own faction."); set => Set("Keybinds", "TogglePlayerCommander", value); }

    internal static string AirCommandMode { get => Get("Air Command", "MissionMode", "AirGuard"); set => Set("Air Command", "MissionMode", value); }
    internal static string AirLoadoutBalance { get => Get("Air Command", "LoadoutBalance", "Primary"); set => Set("Air Command", "LoadoutBalance", value); }
    internal static float AirTargetAltitude { get => Get("Air Command", "TargetAltitude", 0f); set => Set("Air Command", "TargetAltitude", value); }
    internal static bool AirGuardTargetOrdnance { get => Get("Air Command", "AirGuardTargetOrdnance", false); set => Set("Air Command", "AirGuardTargetOrdnance", value); }
    internal static bool AradSaturationAttack { get => Get("Air Command", "AradSaturationAttack", false); set => Set("Air Command", "AradSaturationAttack", value); }
    // Off = a commander-launched AI airframe enters the map already airborne over its base (the
    // default since the AI pilot was seen ejecting on highway-strip taxi and takeoff). On = it
    // spawns in a hangar and taxis out like a mission-authored aircraft. Applies to every
    // commander: the player's AIR window, the player-side AI and the enemy AI alike.
    internal static bool AiAircraftLaunchFromHangar { get => Get("Gameplay", "AiAircraftLaunchFromHangar", false); set => Set("Gameplay", "AiAircraftLaunchFromHangar", value); }
    // Off by default, and applied to the AI commanders' loadouts too: a pilot with cannon rounds left
    // counts them as ordnance and keeps making gun runs instead of returning when the real weapons
    // are spent, which is what made RTB look ignored on gun-armed airframes.
    internal static bool AirIncludeInternalCannons { get => Get("Air Command", "IncludeInternalCannons", false); set => Set("Air Command", "IncludeInternalCannons", value); }
    internal static float AwacsRadiusKm { get => Get("Air Command", "AwacsRadiusKm", 60f); set => Set("Air Command", "AwacsRadiusKm", value); }
    internal static float CasRadiusKm { get => Get("Air Command", "CasRadiusKm", 20f); set => Set("Air Command", "CasRadiusKm", value); }
    internal static float AirGuardRadiusKm { get => Get("Air Command", "AirGuardRadiusKm", 30f); set => Set("Air Command", "AirGuardRadiusKm", value); }
    internal static float AradRadiusKm { get => Get("Air Command", "AradRadiusKm", 50f); set => Set("Air Command", "AradRadiusKm", value); }
    internal static float StrikeRadiusKm { get => Get("Air Command", "StrikeRadiusKm", 80f); set => Set("Air Command", "StrikeRadiusKm", value); }

    internal static void Initialize(ConfigFile configFile)
    {
        config = configFile;
        _ = ModEnabled;
        _ = PersistStateAcrossReload;
        _ = LimitToFactoryVehicles;
        _ = ShowCommandButton;
        _ = PrimaryAction;
        _ = SecondaryAction;
        _ = AddToSelection;
        _ = RepeatDeployment;
        _ = DeleteUnitModifier;
        _ = CameraCenterFollow;
        _ = ToggleUi;
        _ = CameraForward;
        _ = CameraBackward;
        _ = CameraLeft;
        _ = CameraRight;
        _ = CameraUp;
        _ = CameraDown;
        _ = CameraFreeLook;
        _ = CameraBoost;
        _ = MapBoxSelect;
        _ = QueueWaypoint;
        _ = AssignGroupModifier;
        _ = StopOrder;
        _ = SelectSameType;
        _ = CycleIdleUnit;
        _ = AutoFollowSelection;
        _ = GroupHotkeys;
        _ = CameraBookmarks;
        _ = OrderFeedback;
        _ = RetargetAfterKill;
        _ = AttackMoveIntoRange;
        _ = AttackMoveRoutes;
        _ = GuardOrders;
        _ = AutoRetreatDamaged;
        _ = RetreatConditionPercent;
        _ = WaypointHoldSeconds;
        _ = FormationShape;
        _ = FormationCohesionMeters;
        _ = CombatAlerts;
        _ = EnemyCommanderMode;
        _ = PlayerCommanderEnabled;
        _ = PlayerCommanderHandsOffMinutes;
        _ = AiAircraftLaunchFromHangar;
        _ = TacticalMapSize;
        _ = MapDragSpeed;
        _ = UiScaleOverride;
        _ = AutoFrameSelection;
        _ = CameraPanSpeed;
        _ = CameraZoomSpeed;
        _ = CameraLookSensitivity;
        _ = CameraSmoothing;
        _ = CameraHeightScaledSpeed;
        _ = CameraEdgeScroll;
        _ = CameraOrbitLook;
        _ = FollowSmoothing;
        _ = FollowLeadSeconds;
        _ = ShowBuildUi;
        _ = GoldMineCost;
        _ = GoldMineIncomePerMinute;
        _ = FactoryUpgradeCost;
        _ = FactoryBuildCost;
        _ = FactoryProductionSeconds;
        _ = BuildingCostMultiplier;
        _ = RepairCrewCost;
        _ = BuildRadiusKm;
        _ = NavalDockRadiusKm;
        _ = NavalDockShoreMeters;
        _ = NavalDockCost;
        _ = NavalDockUpgradeCost;
        _ = AircraftCaptureStrength;
        _ = SamScanQueriesPerFrame;
        _ = PointsFillGridMeters;
        _ = PointsSiteMinSpacingMeters;
        _ = PointsVillageClusterMeters;
        _ = PointsVillageMinBuildings;
        _ = PointsVillageRadiusMeters;
        _ = PointsHilltopGridMeters;
        _ = PointsHilltopRingMeters;
        _ = PointsHilltopProminenceMeters;
        _ = PointsHilltopRadiusMeters;
        _ = PointsHilltopVillageExclusionMeters;
        _ = PointsOutpostRadiusMeters;
        _ = PointsCrossroadsRadiusMeters;
        _ = PointsRoadsideRadiusMeters;
        _ = PointsRoadsideSpacingMeters;
        _ = PointsCrossroadsMinRoads;
        _ = PointsRoadJunctionMergeMeters;
        _ = PointsMaxNonBasePoints;
        _ = PointsMaxResourceSites;
        _ = PointsMaxCrossroads;
        _ = PointsMaxOutposts;
        _ = PointsMaxRoadPoints;
        _ = PointsCrossroadsSpacingMeters;
        _ = PointsPointMinSpacingMeters;
        _ = PointsAirbaseExclusionMeters;
        _ = PointsMinGarrison;
        _ = PointsHoldSeconds;
        _ = PointsBaseIncomePerMinute;
        _ = PointsVillageIncomePerMinute;
        _ = PointsHilltopIncomePerMinute;
        _ = PointsOutpostIncomePerMinute;
        _ = PointsCrossroadsIncomePerMinute;
        _ = PointsRoadsideIncomePerMinute;
        _ = PointsMineSnapMeters;
        _ = OperationsPlatoonSize;
        _ = OperationsDebugLog;
        _ = OperationsReservePlatoons;
        _ = OperationsRecipeArmour;
        _ = OperationsRecipeCarrier;
        _ = OperationsRecipeAirDefence;
        _ = OperationsFrontRangeMeters;
        _ = OperationsFobShare;
        _ = OperationsPressureIntervalMinutes;
        _ = OperationsOffensiveSpendFraction;
        _ = CasLossCooldownMinutes;
        _ = AirborneCeiling;
        _ = PackageFormUpSeconds;
        _ = RotaryCasRangeMeters;
        _ = AradClusterMinimum;
        _ = OperationsHeliInsertionEnabled;
        _ = OperationsHeliInsertionOffRoadMeters;
        _ = OperationsHeliInsertionLimit;
        _ = OperationsHeliInsertionCooldownMinutes;
        _ = DefenceArcStandoffMeters;
        _ = BoundMeters;
        _ = OffRoadRangeMeters;
        _ = HomeCapBaseline;
        _ = HomeCapPerEnemyAircraft;
        _ = HomeCapMax;
        _ = LadderPlatoonWeight;
        _ = LadderPicketWeight;
        _ = LadderBuildingWeight;
        _ = LadderRungFloorPercent;
        _ = AirCommandMode;
        _ = AwacsRadiusKm;
        _ = CasRadiusKm;
        _ = AirGuardRadiusKm;
        _ = AradRadiusKm;
        _ = StrikeRadiusKm;
    }

    private static KeyboardShortcut GetShortcut(string key, KeyCode defaultKey, string description)
    {
        if (config == null) return new KeyboardShortcut(defaultKey);
        string lookup = "Keybinds/" + key;
        if (entries.TryGetValue(lookup, out ConfigEntryBase existing))
        {
            return ((ConfigEntry<KeyboardShortcut>)existing).Value;
        }

        ConfigEntry<KeyboardShortcut> created = config.Bind(
            "Keybinds",
            key,
            new KeyboardShortcut(defaultKey),
            new ConfigDescription(description + " Set the main key to None to disable it."));
        entries.Add(lookup, created);
        return created.Value;
    }

    private static T Get<T>(string section, string key, T defaultValue)
    {
        ConfigEntry<T>? entry = GetEntry(section, key, defaultValue);
        return entry == null ? defaultValue : entry.Value;
    }

    private static void Set<T>(string section, string key, T value)
    {
        ConfigEntry<T>? entry = GetEntry(section, key, value);
        if (entry != null) entry.Value = value;
    }

    private static ConfigEntry<T>? GetEntry<T>(string section, string key, T defaultValue)
    {
        if (config == null) return null;
        string lookup = section + "/" + key;
        if (entries.TryGetValue(lookup, out ConfigEntryBase existing)) return (ConfigEntry<T>)existing;
        ConfigEntry<T> created = config.Bind(section, key, defaultValue);
        entries.Add(lookup, created);
        return created;
    }
}
