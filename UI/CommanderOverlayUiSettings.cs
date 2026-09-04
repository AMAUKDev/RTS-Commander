using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderOverlayUi
{
    private void DrawSettingsWindowIfVisible()
    {
        if (settingsVisible)
        {
            settingsWindowRect = GUI.Window(
                SettingsWindowId,
                settingsWindowRect,
                DrawSettingsWindow,
                "COMMANDER SETTINGS",
                CommanderUiTheme.Window);
        }
    }

    private void DrawSettingsWindow(int windowId)
    {
        CaptureBindingInput();
        if (CommanderUiTheme.DrawHelpButton(settingsWindowRect.width, ref settingsHelpVisible))
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 34f, settingsWindowRect.width - 24f, 74f),
                "Settings are saved in the BepInEx configuration. RTS camera bindings are read only while RTS mode is active and do not alter aircraft controls.");
        }
        if (GUI.Button(new Rect(settingsWindowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.Button))
        {
            settingsVisible = false;
            bindingCapture = null;
        }

        float y = settingsHelpVisible ? 118f : 38f;
        float tabWidth = (settingsWindowRect.width - 30f) / 5f;
        DrawSettingsTab(new Rect(12f, y, tabWidth, 32f), "GAMEPLAY", 0);
        DrawSettingsTab(new Rect(12f + tabWidth, y, tabWidth, 32f), "UI / HIDE", 1);
        DrawSettingsTab(new Rect(12f + tabWidth * 2f, y, tabWidth, 32f), "CONTROLS", 2);
        DrawSettingsTab(new Rect(12f + tabWidth * 3f, y, tabWidth, 32f), "SHORTCUTS", 3);
        DrawSettingsTab(new Rect(12f + tabWidth * 4f, y, tabWidth, 32f), "CAMERA", 4);
        y += 44f;

        if (settingsTab == 0)
        {
            DrawGameplaySettings(y);
        }
        else if (settingsTab == 1)
        {
            DrawUiSettings(y);
        }
        else if (settingsTab == 3)
        {
            DrawShortcutList(y);
        }
        else if (settingsTab == 4)
        {
            DrawCameraSettings(y);
        }
        else
        {
            DrawControlSettings(y);
        }

        GUI.DragWindow(new Rect(0f, 0f, settingsWindowRect.width - 72f, 28f));
    }

    /// <summary>
    /// Read-only reference of every shortcut, including the ones that are not remappable
    /// (control groups, camera bookmarks, double-click). Rebuilt on layout events only, so the
    /// per-entry strings are not rebuilt twice a frame while the tab sits open.
    /// </summary>
    private void DrawShortcutList(float y)
    {
        if (Event.current.type == EventType.Layout)
        {
            CommanderShortcutReference.Collect(shortcutEntries);
        }

        float width = settingsWindowRect.width - 24f;
        float height = settingsWindowRect.height - y - 16f;
        GUI.Box(new Rect(12f, y, width, height), string.Empty, CommanderUiTheme.Panel);

        Rect view = new(16f, y + 8f, width - 8f, height - 16f);
        float contentHeight = 0f;
        for (int i = 0; i < shortcutEntries.Count; i++)
        {
            contentHeight += shortcutEntries[i].IsSection ? 34f : 30f;
        }

        Rect inner = new(0f, 0f, view.width - 20f, Mathf.Max(view.height, contentHeight + 4f));
        float keyColumn = inner.width * 0.34f;
        float noteColumn = inner.width * 0.30f;
        shortcutScroll = GUI.BeginScrollView(view, shortcutScroll, inner);
        float rowY = 0f;
        for (int i = 0; i < shortcutEntries.Count; i++)
        {
            CommanderShortcutReference.Entry entry = shortcutEntries[i];
            if (entry.IsSection)
            {
                GUI.Label(new Rect(4f, rowY + 8f, inner.width - 8f, 24f), entry.Action, CommanderUiTheme.Header);
                rowY += 34f;
                continue;
            }

            float actionWidth = inner.width - keyColumn - noteColumn - 16f;
            GUI.Label(new Rect(8f, rowY, actionWidth, 28f), entry.Action, CommanderUiTheme.Label);
            GUI.Label(new Rect(8f + actionWidth, rowY, keyColumn, 28f), entry.Keys, CommanderUiTheme.Header);
            if (entry.Note.Length > 0)
            {
                GUI.Label(new Rect(12f + actionWidth + keyColumn, rowY, noteColumn, 28f), entry.Note, CommanderUiTheme.MutedLabel);
            }
            rowY += 30f;
        }
        GUI.EndScrollView();
    }

    private void DrawSettingsTab(Rect rect, string label, int tab)
    {
        if (GUI.Button(rect, label, settingsTab == tab ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            settingsTab = tab;
            bindingCapture = null;
        }
    }

    private void DrawGameplaySettings(float y)
    {
        GUI.Box(new Rect(12f, y, settingsWindowRect.width - 24f, 92f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, y + 10f, settingsWindowRect.width - 48f, 22f), "SPAWN RESTRICTIONS", CommanderUiTheme.Header);
        CommanderSettings.LimitToFactoryVehicles = GUI.Toggle(
            new Rect(24f, y + 42f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.LimitToFactoryVehicles,
            "Limit to vehicles from factories",
            CommanderUiTheme.Toggle);

        float commandY = y + 104f;
        GUI.Box(new Rect(12f, commandY, settingsWindowRect.width - 24f, 512f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, commandY + 10f, settingsWindowRect.width - 48f, 22f), "COMMAND", CommanderUiTheme.Header);
        CommanderSettings.GroupHotkeys = GUI.Toggle(
            new Rect(24f, commandY + 40f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.GroupHotkeys,
            "Control group hotkeys 1-9",
            CommanderUiTheme.Toggle);
        CommanderSettings.AttackMoveIntoRange = GUI.Toggle(
            new Rect(24f, commandY + 74f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.AttackMoveIntoRange,
            "Attack orders stop at weapon range",
            CommanderUiTheme.Toggle);
        CommanderSettings.RetargetAfterKill = GUI.Toggle(
            new Rect(24f, commandY + 108f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.RetargetAfterKill,
            "Keep attacking after the target dies, then hold the ground",
            CommanderUiTheme.Toggle);
        CommanderSettings.CameraBookmarks = GUI.Toggle(
            new Rect(24f, commandY + 142f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.CameraBookmarks,
            "Camera bookmarks F1-F4 (assign key + F1-F4 stores)",
            CommanderUiTheme.Toggle);
        CommanderSettings.OrderFeedback = GUI.Toggle(
            new Rect(24f, commandY + 176f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.OrderFeedback,
            "Flash a marker where an order was given",
            CommanderUiTheme.Toggle);
        CommanderSettings.AttackMoveRoutes = GUI.Toggle(
            new Rect(24f, commandY + 210f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.AttackMoveRoutes,
            "Attack-move: Free Fire units engage hostiles they pass",
            CommanderUiTheme.Toggle);
        CommanderSettings.GuardOrders = GUI.Toggle(
            new Rect(24f, commandY + 244f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.GuardOrders,
            "Right click a friendly unit to guard it",
            CommanderUiTheme.Toggle);
        CommanderSettings.AutoRetreatDamaged = GUI.Toggle(
            new Rect(24f, commandY + 278f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.AutoRetreatDamaged,
            $"Retreat to repair below {CommanderSettings.RetreatConditionPercent:0}% condition",
            CommanderUiTheme.Toggle);
        CommanderSettings.CombatAlerts = GUI.Toggle(
            new Rect(24f, commandY + 312f, settingsWindowRect.width - 48f, 30f),
            CommanderSettings.CombatAlerts,
            "Alert when units are attacked or lost",
            CommanderUiTheme.Toggle);

        int enemySetting = CommanderSettings.EnemyCommanderMode;
        int enemyMode = CommanderEnemyCommanderService.EffectiveMode;
        if (GUI.Button(
            new Rect(24f, commandY + 350f, settingsWindowRect.width - 48f, 32f),
            $"ENEMY COMMANDER: {CommanderEnemyCommanderService.GetModeLabel(enemyMode)}"
                + (enemySetting == CommanderEnemyCommanderService.ModeOff && enemyMode != CommanderEnemyCommanderService.ModeOff
                    ? "  (SET BY MISSION)"
                    : string.Empty)
                + (enemyMode > 0 ? $"   ({CommanderEnemyCommanderService.Instance?.TotalPurchases ?? 0} bought)" : string.Empty),
            enemyMode > 0 ? CommanderUiTheme.DangerButton : CommanderUiTheme.Button))
        {
            CommanderSettings.EnemyCommanderMode =
                enemySetting >= CommanderEnemyCommanderService.ModeMission ? 0 : enemySetting + 1;
        }

        // Both radii are here rather than only in the config file because both are map-dependent:
        // how tight a base perimeter feels, and whether a faction can reach the coast at all, are
        // answers you only get by looking at the map you are on.
        CommanderSettings.BuildRadiusKm = DrawRadiusSlider(
            commandY + 390f,
            "Build radius",
            CommanderSettings.BuildRadiusKm,
            1f,
            15f);
        CommanderSettings.NavalDockRadiusKm = DrawRadiusSlider(
            commandY + 428f,
            "Naval dock radius",
            CommanderSettings.NavalDockRadiusKm,
            1f,
            25f);

        // How much an aircraft parked in a capture ring is worth. It is a balance number the mod
        // invents - the base game gives an aeroplane no capture strength at all - so it belongs
        // where it can be turned down, or off, without editing a config file.
        float capture = CommanderSettings.AircraftCaptureStrength;
        GUI.Label(
            new Rect(24f, commandY + 466f, 250f, 24f),
            $"Aircraft capture strength   {capture:0.#}",
            CommanderUiTheme.Label);
        CommanderSettings.AircraftCaptureStrength = Mathf.Round(
            Mathf.Clamp(
                GUI.HorizontalSlider(
                    new Rect(280f, commandY + 472f, settingsWindowRect.width - 304f, 20f),
                    capture,
                    0f,
                    10f),
                0f,
                10f) * 2f) * 0.5f;
    }

    /// <summary>A labelled kilometre slider, snapped to a half kilometre so the readout is honest.</summary>
    private float DrawRadiusSlider(float y, string label, float value, float min, float max)
    {
        GUI.Label(
            new Rect(24f, y, 220f, 24f),
            $"{label}   {value:0.#} km",
            CommanderUiTheme.Label);
        float slid = GUI.HorizontalSlider(
            new Rect(250f, y + 6f, settingsWindowRect.width - 274f, 20f),
            value,
            min,
            max);
        return Mathf.Round(Mathf.Clamp(slid, min, max) * 2f) * 0.5f;
    }

    /// <summary>
    /// Everything about how the camera feels. It is a whole tab rather than a few config lines
    /// because a camera is tuned by moving it, not by reading numbers: every value here wants to
    /// be dragged while the game is running.
    /// </summary>
    private void DrawCameraSettings(float y)
    {
        float width = settingsWindowRect.width - 24f;
        GUI.Box(new Rect(12f, y, width, 268f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, y + 10f, width - 24f, 22f), "MOVEMENT", CommanderUiTheme.Header);
        CommanderSettings.CameraPanSpeed = DrawCameraSlider(
            y + 40f, "Pan speed", CommanderSettings.CameraPanSpeed, 50f, 1200f, "0", " m/s");
        CommanderSettings.CameraZoomSpeed = DrawCameraSlider(
            y + 78f, "Zoom speed", CommanderSettings.CameraZoomSpeed, 0.2f, 3f, "0.0#", "x");
        CommanderSettings.CameraLookSensitivity = DrawCameraSlider(
            y + 116f, "Look sensitivity", CommanderSettings.CameraLookSensitivity, 0.1f, 4f, "0.0#", "x");
        CommanderSettings.CameraSmoothing = DrawCameraSlider(
            y + 154f, "Smoothing", CommanderSettings.CameraSmoothing, 0f, 0.4f, "0.00", " s");
        CommanderSettings.CameraHeightScaledSpeed = GUI.Toggle(
            new Rect(24f, y + 190f, width - 48f, 30f),
            CommanderSettings.CameraHeightScaledSpeed,
            "Pan speed scales with height above ground",
            CommanderUiTheme.Toggle);
        CommanderSettings.CameraEdgeScroll = GUI.Toggle(
            new Rect(24f, y + 224f, width - 48f, 30f),
            CommanderSettings.CameraEdgeScroll,
            "Edge scrolling (push the cursor into a screen edge)",
            CommanderUiTheme.Toggle);

        float lookY = y + 280f;
        GUI.Box(new Rect(12f, lookY, width, 116f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, lookY + 10f, width - 24f, 22f), "LOOKING AROUND", CommanderUiTheme.Header);
        CommanderSettings.CameraOrbitLook = GUI.Toggle(
            new Rect(24f, lookY + 40f, width - 48f, 30f),
            CommanderSettings.CameraOrbitLook,
            "Hold look to orbit the point under the cursor",
            CommanderUiTheme.Toggle);
        GUI.Label(
            new Rect(24f, lookY + 74f, width - 48f, 34f),
            $"Hold {CommanderSettings.CameraFreeLook} and move the mouse. Off, the camera turns in place and "
                + "whatever you were watching slides off screen. The wheel zooms toward the cursor.",
            CommanderUiTheme.MutedLabel);

        float followY = lookY + 128f;
        GUI.Box(new Rect(12f, followY, width, 152f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, followY + 10f, width - 24f, 22f), "SELECTION AND FOLLOW", CommanderUiTheme.Header);
        CommanderSettings.AutoFollowSelection = GUI.Toggle(
            new Rect(24f, followY + 40f, width - 48f, 30f),
            CommanderSettings.AutoFollowSelection,
            "Follow the camera on the selected unit",
            CommanderUiTheme.Toggle);
        CommanderSettings.AutoFrameSelection = GUI.Toggle(
            new Rect(24f, followY + 74f, width - 48f, 30f),
            CommanderSettings.AutoFrameSelection,
            "Travel to a selected unit only when it is off screen",
            CommanderUiTheme.Toggle);
        GUI.Label(
            new Rect(24f, followY + 108f, width - 48f, 34f),
            $"Selecting something you can already see leaves the camera alone. {CommanderSettings.CameraCenterFollow} "
                + "always centres on the selection immediately.",
            CommanderUiTheme.MutedLabel);

        float mapY = followY + 164f;
        GUI.Box(new Rect(12f, mapY, width, 82f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(24f, mapY + 10f, width - 24f, 22f), "TACTICAL MAP", CommanderUiTheme.Header);
        CommanderSettings.MapDragSpeed = DrawCameraSlider(
            mapY + 40f, "Map drag speed", CommanderSettings.MapDragSpeed, 0.25f, 4f, "0.0#", "x");
    }

    /// <summary>A labelled slider that shows the value it is about to write.</summary>
    private float DrawCameraSlider(float y, string label, float value, float min, float max, string format, string suffix)
    {
        GUI.Label(
            new Rect(24f, y, 230f, 24f),
            $"{label}   {value.ToString(format)}{suffix}",
            CommanderUiTheme.Label);
        float slid = GUI.HorizontalSlider(
            new Rect(260f, y + 6f, settingsWindowRect.width - 284f, 20f),
            value,
            min,
            max);
        return Mathf.Clamp(slid, min, max);
    }

    private void DrawUiSettings(float y)
    {
        GUI.Box(new Rect(12f, y, settingsWindowRect.width - 24f, 344f), string.Empty, CommanderUiTheme.Panel);
        float left = 28f;
        float right = settingsWindowRect.width * 0.5f + 10f;
        float width = settingsWindowRect.width * 0.5f - 40f;
        showCommandButton = GUI.Toggle(new Rect(left, y + 16f, width, 28f), showCommandButton, "Command button", CommanderUiTheme.Toggle);
        showFactionMoney = GUI.Toggle(new Rect(right, y + 16f, width, 28f), showFactionMoney, "Faction funds", CommanderUiTheme.Toggle);
        showTacticalMap = GUI.Toggle(new Rect(left, y + 50f, width, 28f), showTacticalMap, "Tactical map", CommanderUiTheme.Toggle);
        showSelectionBar = GUI.Toggle(new Rect(right, y + 50f, width, 28f), showSelectionBar, "Selection bar", CommanderUiTheme.Toggle);
        showPinnedUnits = GUI.Toggle(new Rect(left, y + 84f, width, 28f), showPinnedUnits, "Unit / mission list", CommanderUiTheme.Toggle);
        showUnitSystems = GUI.Toggle(new Rect(right, y + 84f, width, 28f), showUnitSystems, "Unit systems", CommanderUiTheme.Toggle);
        showDepotUi = GUI.Toggle(new Rect(left, y + 118f, width, 28f), showDepotUi, "Depot UI", CommanderUiTheme.Toggle);
        showSupplyUi = GUI.Toggle(new Rect(right, y + 118f, width, 28f), showSupplyUi, "Supply UI", CommanderUiTheme.Toggle);
        showAirCommandUi = GUI.Toggle(new Rect(left, y + 152f, width, 28f), showAirCommandUi, "Air Command UI", CommanderUiTheme.Toggle);
        showNavalUi = GUI.Toggle(new Rect(right, y + 152f, width, 28f), showNavalUi, "Naval UI", CommanderUiTheme.Toggle);
        showWorldMarkers = GUI.Toggle(new Rect(left, y + 186f, width, 28f), showWorldMarkers, "World markers", CommanderUiTheme.Toggle);
        showSamAnalyzerUi = GUI.Toggle(new Rect(right, y + 186f, width, 28f), showSamAnalyzerUi, "SAM analyzer UI", CommanderUiTheme.Toggle);
        showUnitListUi = GUI.Toggle(new Rect(left, y + 220f, width, 28f), showUnitListUi, "Order of battle", CommanderUiTheme.Toggle);
        showBuildUi = GUI.Toggle(new Rect(right, y + 220f, width, 28f), showBuildUi, "Build UI", CommanderUiTheme.Toggle);

        SaveUiVisibilitySettings();
        GUI.Label(
            new Rect(28f, y + 260f, settingsWindowRect.width - 56f, 20f),
            $"Automatic UI scale for {Screen.width} x {Screen.height}: {CommanderSettings.UiScale:0.##}x",
            CommanderUiTheme.MutedLabel);
        GUI.Label(
            new Rect(28f, y + 284f, settingsWindowRect.width - 56f, 20f),
            $"{CommanderSettings.ToggleUi} cycles visible, RTS UI hidden, and all UI hidden.",
            CommanderUiTheme.MutedLabel);
        if (GUI.Button(new Rect(28f, y + 308f, settingsWindowRect.width - 56f, 30f), "RESET UI LAYOUT", CommanderUiTheme.Button))
        {
            ResetUiLayout();
        }
    }

    private void SaveUiVisibilitySettings()
    {
        CommanderSettings.ShowCommandButton = showCommandButton;
        CommanderSettings.ShowFactionMoney = showFactionMoney;
        CommanderSettings.ShowTacticalMap = showTacticalMap;
        CommanderSettings.ShowSelectionBar = showSelectionBar;
        CommanderSettings.ShowPinnedUnits = showPinnedUnits;
        CommanderSettings.ShowUnitSystems = showUnitSystems;
        CommanderSettings.ShowDepotUi = showDepotUi;
        CommanderSettings.ShowSupplyUi = showSupplyUi;
        CommanderSettings.ShowAirCommandUi = showAirCommandUi;
        CommanderSettings.ShowNavalUi = showNavalUi;
        CommanderSettings.ShowSamAnalyzerUi = showSamAnalyzerUi;
        CommanderSettings.ShowWorldMarkers = showWorldMarkers;
        CommanderSettings.ShowUnitListUi = showUnitListUi;
        CommanderSettings.ShowBuildUi = showBuildUi;
    }

    private void DrawControlSettings(float y)
    {
        GUI.Box(new Rect(12f, y, settingsWindowRect.width - 24f, 480f), string.Empty, CommanderUiTheme.Panel);
        GUI.Label(
            new Rect(24f, y + 8f, settingsWindowRect.width - 48f, 32f),
            "Bindings are active only in RTS mode. Click one, then press a keyboard or mouse button. Escape cancels.",
            CommanderUiTheme.MutedLabel);

        float columnWidth = (settingsWindowRect.width - 66f) * 0.5f;
        float left = 24f;
        float right = 42f + columnWidth;
        GUI.Label(new Rect(left, y + 42f, columnWidth, 22f), "CAMERA", CommanderUiTheme.Header);
        GUI.Label(new Rect(right, y + 42f, columnWidth, 22f), "COMMANDER ACTIONS", CommanderUiTheme.Header);
        float rowY = y + 68f;
        DrawBinding(new Rect(left, rowY, columnWidth, 30f), "Forward", "forward");
        DrawBinding(new Rect(left, rowY + 32f, columnWidth, 30f), "Backward", "backward");
        DrawBinding(new Rect(left, rowY + 64f, columnWidth, 30f), "Move left", "left");
        DrawBinding(new Rect(left, rowY + 96f, columnWidth, 30f), "Move right", "right");
        DrawBinding(new Rect(left, rowY + 128f, columnWidth, 30f), "Move up", "up");
        DrawBinding(new Rect(left, rowY + 160f, columnWidth, 30f), "Move down", "down");
        DrawBinding(new Rect(left, rowY + 192f, columnWidth, 30f), "Free look", "look");
        DrawBinding(new Rect(left, rowY + 224f, columnWidth, 30f), "Speed boost", "boost");
        Rect centerFollowRect = new(left, rowY + 256f, columnWidth, 30f);
        DrawBinding(centerFollowRect, "Center / follow", "center_follow");

        DrawBinding(new Rect(right, rowY, columnWidth, 30f), "Select / place", "primary");
        DrawBinding(new Rect(right, rowY + 32f, columnWidth, 30f), "Move / order", "secondary");
        DrawBinding(new Rect(right, rowY + 64f, columnWidth, 30f), "Add selection", "add_selection");
        DrawBinding(new Rect(right, rowY + 96f, columnWidth, 30f), "Repeat deploy", "repeat_deploy");
        DrawBinding(new Rect(right, rowY + 128f, columnWidth, 30f), "Delete modifier", "delete_modifier");
        DrawBinding(new Rect(right, rowY + 160f, columnWidth, 30f), "UI cycle", "toggle_ui");
        DrawBinding(new Rect(right, rowY + 192f, columnWidth, 30f), "Queue point", "queue_waypoint");
        DrawBinding(new Rect(right, rowY + 224f, columnWidth, 30f), "Assign group", "assign_group");
        DrawBinding(new Rect(right, rowY + 256f, columnWidth, 30f), "Stop order", "stop_order");
        DrawBinding(new Rect(right, rowY + 288f, columnWidth, 30f), "Same type", "same_type");
        DrawBinding(new Rect(right, rowY + 320f, columnWidth, 30f), "Cycle idle", "cycle_idle");
        DrawBinding(new Rect(right, rowY + 352f, columnWidth, 30f), "Map box select", "map_box_select");

        if (centerFollowRect.Contains(Event.current.mousePosition))
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(left, rowY + 292f, columnWidth, 68f),
                "Press briefly to center on the selected unit. Hold to center and follow it.");
        }

        if (GUI.Button(new Rect(left, y + 432f, columnWidth, 32f), "RESET CAMERA", CommanderUiTheme.Button))
        {
            ResetCameraBindings();
            bindingCapture = null;
        }
        if (GUI.Button(new Rect(right, y + 432f, columnWidth, 32f), "RESET ACTIONS", CommanderUiTheme.Button))
        {
            ResetActionBindings();
            bindingCapture = null;
        }
    }

    private void DrawBinding(Rect rect, string label, string binding)
    {
        float labelWidth = Mathf.Min(94f, rect.width * 0.36f);
        GUI.Label(new Rect(rect.x, rect.y, labelWidth, rect.height), label, CommanderUiTheme.Label);
        string buttonText = bindingCapture == binding ? "PRESS KEY..." : GetBinding(binding).ToString();
        if (GUI.Button(
            new Rect(rect.x + labelWidth, rect.y, rect.width - labelWidth - 34f, rect.height),
            buttonText,
            bindingCapture == binding ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            bindingCapture = binding;
        }
        if (GUI.Button(new Rect(rect.xMax - 28f, rect.y, 28f, rect.height), "X", CommanderUiTheme.Button))
        {
            SetBinding(binding, new KeyboardShortcut(KeyCode.None));
            bindingCapture = null;
        }
    }

    private void CaptureBindingInput()
    {
        if (bindingCapture == null)
        {
            return;
        }

        Event current = Event.current;
        KeyCode key;
        if (current.type == EventType.KeyDown)
        {
            if (current.keyCode == KeyCode.Escape)
            {
                bindingCapture = null;
                current.Use();
                return;
            }
            key = current.keyCode;
            if (key == KeyCode.None)
            {
                return;
            }
        }
        else if (current.type == EventType.MouseDown)
        {
            key = (KeyCode)((int)KeyCode.Mouse0 + current.button);
        }
        else
        {
            return;
        }

        List<KeyCode> modifiers = new();
        if (current.shift && key != KeyCode.LeftShift && key != KeyCode.RightShift) modifiers.Add(KeyCode.LeftShift);
        if (current.control && key != KeyCode.LeftControl && key != KeyCode.RightControl) modifiers.Add(KeyCode.LeftControl);
        if (current.alt && key != KeyCode.LeftAlt && key != KeyCode.RightAlt) modifiers.Add(KeyCode.LeftAlt);
        SetBinding(bindingCapture, new KeyboardShortcut(key, modifiers.ToArray()));
        bindingCapture = null;
        current.Use();
    }

    private static KeyboardShortcut GetBinding(string binding)
    {
        return binding switch
        {
            "forward" => CommanderSettings.CameraForward,
            "backward" => CommanderSettings.CameraBackward,
            "left" => CommanderSettings.CameraLeft,
            "right" => CommanderSettings.CameraRight,
            "up" => CommanderSettings.CameraUp,
            "down" => CommanderSettings.CameraDown,
            "look" => CommanderSettings.CameraFreeLook,
            "boost" => CommanderSettings.CameraBoost,
            "primary" => CommanderSettings.PrimaryAction,
            "secondary" => CommanderSettings.SecondaryAction,
            "add_selection" => CommanderSettings.AddToSelection,
            "repeat_deploy" => CommanderSettings.RepeatDeployment,
            "delete_modifier" => CommanderSettings.DeleteUnitModifier,
            "center_follow" => CommanderSettings.CameraCenterFollow,
            "toggle_ui" => CommanderSettings.ToggleUi,
            "queue_waypoint" => CommanderSettings.QueueWaypoint,
            "assign_group" => CommanderSettings.AssignGroupModifier,
            "stop_order" => CommanderSettings.StopOrder,
            "same_type" => CommanderSettings.SelectSameType,
            "cycle_idle" => CommanderSettings.CycleIdleUnit,
            "map_box_select" => CommanderSettings.MapBoxSelect,
            _ => new KeyboardShortcut(KeyCode.None)
        };
    }

    private static void SetBinding(string binding, KeyboardShortcut shortcut)
    {
        switch (binding)
        {
            case "forward": CommanderSettings.CameraForward = shortcut; break;
            case "backward": CommanderSettings.CameraBackward = shortcut; break;
            case "left": CommanderSettings.CameraLeft = shortcut; break;
            case "right": CommanderSettings.CameraRight = shortcut; break;
            case "up": CommanderSettings.CameraUp = shortcut; break;
            case "down": CommanderSettings.CameraDown = shortcut; break;
            case "look": CommanderSettings.CameraFreeLook = shortcut; break;
            case "boost": CommanderSettings.CameraBoost = shortcut; break;
            case "primary": CommanderSettings.PrimaryAction = shortcut; break;
            case "secondary": CommanderSettings.SecondaryAction = shortcut; break;
            case "add_selection": CommanderSettings.AddToSelection = shortcut; break;
            case "repeat_deploy": CommanderSettings.RepeatDeployment = shortcut; break;
            case "delete_modifier": CommanderSettings.DeleteUnitModifier = shortcut; break;
            case "center_follow": CommanderSettings.CameraCenterFollow = shortcut; break;
            case "toggle_ui": CommanderSettings.ToggleUi = shortcut; break;
            case "queue_waypoint": CommanderSettings.QueueWaypoint = shortcut; break;
            case "assign_group": CommanderSettings.AssignGroupModifier = shortcut; break;
            case "stop_order": CommanderSettings.StopOrder = shortcut; break;
            case "same_type": CommanderSettings.SelectSameType = shortcut; break;
            case "cycle_idle": CommanderSettings.CycleIdleUnit = shortcut; break;
            case "map_box_select": CommanderSettings.MapBoxSelect = shortcut; break;
        }
    }

    private static void ResetCameraBindings()
    {
        CommanderSettings.CameraForward = new KeyboardShortcut(KeyCode.W);
        CommanderSettings.CameraBackward = new KeyboardShortcut(KeyCode.S);
        CommanderSettings.CameraLeft = new KeyboardShortcut(KeyCode.A);
        CommanderSettings.CameraRight = new KeyboardShortcut(KeyCode.D);
        CommanderSettings.CameraUp = new KeyboardShortcut(KeyCode.Q);
        CommanderSettings.CameraDown = new KeyboardShortcut(KeyCode.E);
        CommanderSettings.CameraFreeLook = new KeyboardShortcut(KeyCode.Mouse2);
        CommanderSettings.CameraBoost = new KeyboardShortcut(KeyCode.LeftShift);
        CommanderSettings.CameraCenterFollow = new KeyboardShortcut(KeyCode.Space);
    }

    private static void ResetActionBindings()
    {
        CommanderSettings.PrimaryAction = new KeyboardShortcut(KeyCode.Mouse0);
        CommanderSettings.SecondaryAction = new KeyboardShortcut(KeyCode.Mouse1);
        CommanderSettings.AddToSelection = new KeyboardShortcut(KeyCode.LeftShift);
        CommanderSettings.RepeatDeployment = new KeyboardShortcut(KeyCode.LeftShift);
        CommanderSettings.DeleteUnitModifier = new KeyboardShortcut(KeyCode.LeftAlt);
        CommanderSettings.ToggleUi = new KeyboardShortcut(KeyCode.H);
        CommanderSettings.QueueWaypoint = new KeyboardShortcut(KeyCode.LeftShift);
        CommanderSettings.AssignGroupModifier = new KeyboardShortcut(KeyCode.LeftControl);
        CommanderSettings.StopOrder = new KeyboardShortcut(KeyCode.X);
        CommanderSettings.SelectSameType = new KeyboardShortcut(KeyCode.LeftControl);
        CommanderSettings.CycleIdleUnit = new KeyboardShortcut(KeyCode.Period);
        CommanderSettings.MapBoxSelect = new KeyboardShortcut(KeyCode.LeftControl);
    }

    private void ResetUiLayout()
    {
        positionsInitialized = false;
        supplyHeliUi.ResetPosition();
        airCommandUi.ResetPosition();
        navalPurchaseUi.ResetPosition();
        samSiteAnalyzerUi.ResetPosition();
        economyUi.ResetPosition();
        depotUi.ResetPosition();
        unitListUi.ResetPosition();
        CommanderAlertUi.Instance?.ResetPosition();
        CommanderTacticalMapService.Instance?.ResetLayoutPosition();
    }
}
