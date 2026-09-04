using BepInEx.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal static class CommanderSettings
{
    private static ConfigFile? config;
    private static readonly Dictionary<string, ConfigEntryBase> entries = new();

    internal static float UiScale { get; set; } = 1.5f;
    internal static bool ModEnabled { get => Get("General", "Enabled", true); set => Set("General", "Enabled", value); }
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

    internal static string AirCommandMode { get => Get("Air Command", "MissionMode", "AirGuard"); set => Set("Air Command", "MissionMode", value); }
    internal static string AirLoadoutBalance { get => Get("Air Command", "LoadoutBalance", "Primary"); set => Set("Air Command", "LoadoutBalance", value); }
    internal static float AirTargetAltitude { get => Get("Air Command", "TargetAltitude", 0f); set => Set("Air Command", "TargetAltitude", value); }
    internal static bool AirGuardTargetOrdnance { get => Get("Air Command", "AirGuardTargetOrdnance", false); set => Set("Air Command", "AirGuardTargetOrdnance", value); }
    internal static bool AradSaturationAttack { get => Get("Air Command", "AradSaturationAttack", false); set => Set("Air Command", "AradSaturationAttack", value); }
    internal static bool AirIncludeInternalCannons { get => Get("Air Command", "IncludeInternalCannons", true); set => Set("Air Command", "IncludeInternalCannons", value); }
    internal static float AwacsRadiusKm { get => Get("Air Command", "AwacsRadiusKm", 60f); set => Set("Air Command", "AwacsRadiusKm", value); }
    internal static float CasRadiusKm { get => Get("Air Command", "CasRadiusKm", 20f); set => Set("Air Command", "CasRadiusKm", value); }
    internal static float AirGuardRadiusKm { get => Get("Air Command", "AirGuardRadiusKm", 30f); set => Set("Air Command", "AirGuardRadiusKm", value); }
    internal static float AradRadiusKm { get => Get("Air Command", "AradRadiusKm", 50f); set => Set("Air Command", "AradRadiusKm", value); }
    internal static float StrikeRadiusKm { get => Get("Air Command", "StrikeRadiusKm", 80f); set => Set("Air Command", "StrikeRadiusKm", value); }

    internal static void Initialize(ConfigFile configFile)
    {
        config = configFile;
        _ = ModEnabled;
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
        _ = TacticalMapSize;
        _ = MapDragSpeed;
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
