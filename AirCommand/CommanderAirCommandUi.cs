using UnityEngine;
using System.Collections.Generic;

namespace GroundControlRts;

internal sealed class CommanderAirCommandUi
{
    private const int WindowId = 0x434F4143;
    private const int MissionWindowId = 0x434F414D;

    private readonly CommanderAirCommandService service;
    private Rect windowRect = new(0f, 0f, 820f, 760f);
    private Vector2 aircraftScroll;
    private Vector2 hardpointScroll;
    private Vector2 loadoutDropdownScroll;
    private Vector2 airbaseScroll;
    private int openHardpointGroup = -1;
    private bool altitudeDropdownOpen;
    private bool positionInitialized;
    private bool helpVisible;
    private readonly List<Aircraft> missionAircraft = new();
    private readonly List<Aircraft> idleAircraft = new();
    private bool showIdleAircraft;
    private Rect missionWindowRect;
    private Vector2 missionScroll;
    private bool missionPositionInitialized;
    private bool openedMap;
    private string hoverTooltip = string.Empty;

    internal static CommanderAirCommandUi? Instance { get; private set; }

    internal CommanderAirCommandUi(CommanderAirCommandService service)
    {
        this.service = service;
        Instance = this;
    }

    internal bool Visible { get; private set; }

    internal void Toggle()
    {
        if (Visible) Hide();
        else Show();
    }

    internal void Show()
    {
        if (Visible) return;
        Visible = true;
        service.SetUiVisible(true);
        helpVisible = false;
        aircraftScroll = Vector2.zero;
        hardpointScroll = Vector2.zero;
        airbaseScroll = Vector2.zero;
        openHardpointGroup = -1;
        altitudeDropdownOpen = false;
        // The mod's own map, not the game's. Air Command used to swap the whole screen for the
        // fullscreen map and swap it back on close, which is what made picking a mission area feel
        // like leaving the RTS layer and coming back to it.
        openedMap = CommanderTacticalMapService.Instance?.OpenForPlacement() == true;
    }

    internal void Hide()
    {
        if (service.AwaitingAreaSelection) service.CancelAreaSelection();
        Visible = false;
        service.SetUiVisible(false);
        service.ClearMissionAircraftSelection();
        CommanderSelectionService.Instance?.DeselectAll();
        // Only put the map away if this window is what put it up, and only when the player was not
        // running the tactical map anyway.
        if (openedMap && !CommanderSettings.ShowTacticalMap)
        {
            CommanderTacticalMapService.Instance?.Close();
        }
        openedMap = false;
    }

    internal bool HandleMapKey()
    {
        if (!Visible) return false;
        Hide();
        return true;
    }

    internal void ResetPosition()
    {
        positionInitialized = false;
        missionPositionInitialized = false;
    }

    internal void Tick()
    {
        if (Visible && CommanderTacticalMapService.Instance?.IsOpen != true)
        {
            openedMap |= CommanderTacticalMapService.Instance?.Open() == true;
        }
        float width = Mathf.Min(760f, CommanderUiScale.Width - 390f);
        float height = Mathf.Min(900f, CommanderUiScale.Height - 32f);
        if (!positionInitialized)
        {
            windowRect = new Rect(
                74f,
                Mathf.Max(12f, (CommanderUiScale.Height - height) * 0.5f),
                width,
                height);
            positionInitialized = true;
        }
        else
        {
            windowRect.width = width;
            windowRect.height = height;
            windowRect = CommanderUiTheme.ClampWindow(windowRect);
        }
        if (!missionPositionInitialized)
        {
            // Beside the AIR COMMAND window rather than against the right edge: the tactical map
            // lives in the top-right corner and the mission list used to sit on top of it.
            missionWindowRect = new Rect(
                Mathf.Min(windowRect.xMax + 8f, Mathf.Max(12f, CommanderUiScale.Width - 342f)),
                16f,
                326f,
                Mathf.Min(520f, CommanderUiScale.Height - 32f));
            missionPositionInitialized = true;
        }
        else
        {
            missionWindowRect.width = 326f;
            missionWindowRect.height = Mathf.Min(520f, CommanderUiScale.Height - 32f);
            missionWindowRect = CommanderUiTheme.ClampWindow(missionWindowRect);
        }
        service.CollectMissionAircraft(missionAircraft);
        if (Visible)
        {
            // Scans every faction unit, so keep it to the frames the list is actually shown.
            service.CollectIdleAircraft(idleAircraft);
        }
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screenPoint);
        return Visible && (windowRect.Contains(guiPoint) || missionWindowRect.Contains(guiPoint));
    }

    internal void Draw()
    {
        if (!Visible)
        {
            return;
        }

        service.SetAreaSelectionBlockingRects(windowRect, missionWindowRect);
        windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "AIR COMMAND", CommanderUiTheme.Window);
        missionWindowRect = GUI.Window(MissionWindowId, missionWindowRect, DrawMissionWindow, "AIR MISSIONS", CommanderUiTheme.Window);
        service.SetAreaSelectionBlockingRects(windowRect, missionWindowRect);
    }

    private void DrawWindow(int windowId)
    {
        hoverTooltip = string.Empty;
        CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible);
        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.DangerButton))
        {
            if (service.AwaitingAreaSelection) service.CancelAreaSelection();
            Hide();
            return;
        }

        float y = 36f;
        if (helpVisible)
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, y, windowRect.width - 24f, 86f),
                "Select a mission and loadout, then choose a departure airbase from the list or directly on the map. Only airbases that can currently spawn the aircraft are shown. Place the mission area on the tactical map beside this window, or in the 3D world. Active aircraft and RTB are listed on the right.");
            y += 94f;
        }

        bool dropdownOpen = openHardpointGroup >= 0 || altitudeDropdownOpen;
        float modeWidth = (windowRect.width - 34f) * 0.2f;
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && !dropdownOpen && !service.AwaitingAreaSelection;
        DrawModeButton(
            CommanderAirCommandService.AirCommandMode.AwacsJammer,
            "AWACS / JAM",
            12f,
            y,
            modeWidth,
            "STATION AREA (BLUE) | Stays inside the circle while using radar and jammer systems. Jammers may affect visible emitters outside the circle within pod range.");
        DrawModeButton(
            CommanderAirCommandService.AirCommandMode.Cas,
            "CAS",
            14f + modeWidth,
            y,
            modeWidth,
            "TARGET AREA (ORANGE) | Attacks tracked hostile ground vehicles, ships and buildings inside the circle with conventional anti-surface weapons.");
        DrawModeButton(
            CommanderAirCommandService.AirCommandMode.AirGuard,
            "AIR SUPERIORITY",
            16f + modeWidth * 2f,
            y,
            modeWidth,
            "STATION AREA (BLUE) | Returns to and remains inside the circle, but may engage valid hostile aircraft outside it whenever they are within weapon range. TARGET ORDNANCE additionally permits attacks on hostile missiles.");
        DrawModeButton(
            CommanderAirCommandService.AirCommandMode.Arad,
            "ARAD",
            18f + modeWidth * 3f,
            y,
            modeWidth,
            "TARGET AREA (ORANGE) | Uses anti-radiation weapons against emitting ground, ship and building targets inside the circle. SATURATION empties the missile station in one salvo.");
        DrawModeButton(
            CommanderAirCommandService.AirCommandMode.StrategicStrike,
            "STRIKE [EXP]",
            20f + modeWidth * 4f,
            y,
            modeWidth,
            "EXPERIMENTAL / UNFINISHED | Uses weapons marked Strategic against tracked ground, ship and building targets inside the orange circle. Target priority and strike sequencing are not final.");
        y += 44f;

        GUI.enabled = oldEnabled;
        Rect altitudePanel = default;
        if (service.SupportsTargetAltitude())
        {
            altitudePanel = new Rect(12f, y, windowRect.width - 24f, 38f);
            GUI.Box(altitudePanel, string.Empty, CommanderUiTheme.Panel);
            GUI.Label(new Rect(altitudePanel.x + 12f, altitudePanel.y + 8f, 180f, 24f), "TARGET ALTITUDE", CommanderUiTheme.Header);
        GUI.enabled = oldEnabled && openHardpointGroup < 0 && !service.AwaitingAreaSelection;
            string altitude = service.SelectedTargetAltitude <= 0f ? "STANDARD" : $"{service.SelectedTargetAltitude:0} m";
            if (GUI.Button(new Rect(altitudePanel.x + 200f, altitudePanel.y + 5f, 180f, 28f), altitude + "   v",
                altitudeDropdownOpen ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                altitudeDropdownOpen = !altitudeDropdownOpen;
            }
            GUI.enabled = oldEnabled;
            y += 46f;
        }

        Rect radiusPanel = new(12f, y, windowRect.width - 24f, 38f);
        GUI.Box(radiusPanel, string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(radiusPanel.x + 12f, radiusPanel.y + 8f, 180f, 24f), "MISSION RADIUS", CommanderUiTheme.Header);
        GUI.enabled = oldEnabled && !dropdownOpen && !service.AwaitingAreaSelection;
        if (GUI.Button(new Rect(radiusPanel.x + 200f, radiusPanel.y + 5f, 34f, 28f), "-", CommanderUiTheme.Button)) service.StepMissionRadius(-5f);
        GUI.Label(new Rect(radiusPanel.x + 242f, radiusPanel.y + 8f, 100f, 24f), $"{service.SelectedMissionRadiusKm:0} km", CommanderUiTheme.Header);
        if (GUI.Button(new Rect(radiusPanel.x + 344f, radiusPanel.y + 5f, 34f, 28f), "+", CommanderUiTheme.Button)) service.StepMissionRadius(5f);
        GUI.enabled = oldEnabled;
        y += 46f;

        if (service.SelectedMode == CommanderAirCommandService.AirCommandMode.AirGuard)
        {
            bool oldOrdnance = GUI.enabled;
            GUI.enabled = oldOrdnance && !dropdownOpen && !service.AwaitingAreaSelection;
            service.TargetOrdnance = GUI.Toggle(
                new Rect(12f, y, windowRect.width - 24f, 32f),
                service.TargetOrdnance,
                "TARGET ORDNANCE  |  include hostile missiles",
                CommanderUiTheme.Toggle);
            GUI.enabled = oldOrdnance;
            y += 38f;
        }

        if (service.SelectedMode == CommanderAirCommandService.AirCommandMode.Arad)
        {
            bool oldSaturation = GUI.enabled;
            GUI.enabled = oldSaturation && !dropdownOpen && !service.AwaitingAreaSelection;
            service.SaturationAttack = GUI.Toggle(
                new Rect(12f, y, windowRect.width - 24f, 32f),
                service.SaturationAttack,
                "SATURATION ATTACK  |  launch all ARAD missiles",
                CommanderUiTheme.Toggle);
            GUI.enabled = oldSaturation;
            y += 38f;
        }

        {
            bool oldHangar = GUI.enabled;
            GUI.enabled = oldHangar && !dropdownOpen && !service.AwaitingAreaSelection;
            service.LaunchFromHangar = GUI.Toggle(
                new Rect(12f, y, windowRect.width - 24f, 32f),
                service.LaunchFromHangar,
                "HANGAR LAUNCH  |  taxi and take off instead of spawning airborne",
                CommanderUiTheme.Toggle);
            GUI.enabled = oldHangar;
            y += 38f;
        }

        if (service.SelectedMode == CommanderAirCommandService.AirCommandMode.StrategicStrike)
        {
            GUI.Box(new Rect(12f, y, windowRect.width - 24f, 34f), string.Empty, CommanderUiTheme.Panel);
            GUI.Label(new Rect(22f, y + 5f, windowRect.width - 44f, 24f),
                "EXPERIMENTAL  |  strike behavior and target priority are unfinished",
                CommanderUiTheme.MutedLabel);
            y += 40f;
        }

        GUI.Label(new Rect(12f, y, windowRect.width - 24f, 24f), "MISSION LOADOUT", CommanderUiTheme.MutedLabel);
        y += 24f;
        float loadoutHeight = 208f;
        Rect loadoutPanel = new(12f, y, windowRect.width - 24f, loadoutHeight);
        GUI.Box(loadoutPanel, string.Empty, CommanderUiTheme.Panel);
        GUI.enabled = oldEnabled && !dropdownOpen && !service.AwaitingAreaSelection;
        DrawLoadoutEditor(loadoutPanel);
        GUI.enabled = oldEnabled;
        y += loadoutHeight + 8f;

        GUI.enabled = oldEnabled && !dropdownOpen && !service.AwaitingAreaSelection;
        GUI.Label(new Rect(12f, y, windowRect.width - 24f, 24f), "AIRCRAFT", CommanderUiTheme.MutedLabel);
        y += 24f;
        float aircraftHeight = 150f;
        Rect aircraftView = new(12f, y, windowRect.width - 24f, aircraftHeight);
        float aircraftInnerHeight = Mathf.Max(aircraftView.height, service.Options.Count * 58f + 4f);
        aircraftScroll = GUI.BeginScrollView(aircraftView, aircraftScroll, new Rect(0f, 0f, aircraftView.width - 18f, aircraftInnerHeight));
        for (int i = 0; i < service.Options.Count; i++)
        {
            CommanderAirCommandService.AirMissionOption option = service.Options[i];
            if (GUI.Button(new Rect(2f, 2f + i * 58f, aircraftView.width - 24f, 52f),
                service.GetOptionLabel(option),
                i == service.SelectedOptionIndex ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                service.SelectOption(i);
                airbaseScroll = Vector2.zero;
                hardpointScroll = Vector2.zero;
                openHardpointGroup = -1;
            }
        }
        GUI.EndScrollView();
        y += aircraftHeight + 8f;

        GUI.enabled = oldEnabled && !dropdownOpen && !service.AwaitingAreaSelection;
        GUI.Label(new Rect(12f, y, windowRect.width - 24f, 24f), "DEPARTURE AIRBASE", CommanderUiTheme.MutedLabel);
        y += 24f;
        float footerHeight = 112f;
        float airbaseHeight = Mathf.Max(72f, windowRect.height - y - footerHeight);
        Rect airbaseView = new(12f, y, windowRect.width - 24f, airbaseHeight);
        float airbaseInnerHeight = Mathf.Max(airbaseView.height, service.Airbases.Count * 40f + 4f);
        airbaseScroll = GUI.BeginScrollView(airbaseView, airbaseScroll, new Rect(0f, 0f, airbaseView.width - 18f, airbaseInnerHeight));
        for (int i = 0; i < service.Airbases.Count; i++)
        {
            CommanderAirCommandService.AirbaseOption airbase = service.Airbases[i];
            if (GUI.Button(new Rect(2f, 2f + i * 40f, airbaseView.width - 24f, 34f),
                service.GetAirbaseLabel(airbase),
                i == service.SelectedAirbaseIndex ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                service.SelectAirbase(i);
            }
        }
        GUI.EndScrollView();
        y += airbaseHeight + 6f;

        GUI.enabled = oldEnabled && (service.AwaitingAreaSelection || (!dropdownOpen && service.CanLaunchSelected));
        if (GUI.Button(new Rect(12f, y, windowRect.width - 24f, 38f),
            service.AwaitingAreaSelection ? "CANCEL AREA SELECTION" : "REQUEST MISSION",
            service.AwaitingAreaSelection ? CommanderUiTheme.DangerButton : CommanderUiTheme.PrimaryButton))
        {
            if (service.AwaitingAreaSelection) service.CancelAreaSelection();
            else service.BeginAreaSelection();
        }
        GUI.enabled = oldEnabled;
        y += 42f;

        string status = service.AwaitingAreaSelection
            ? "Click the tactical map or 3D terrain. The game's Cancel binding cancels."
            : service.StatusText;
        GUI.Label(new Rect(12f, y, windowRect.width - 24f, 42f),
            $"ACTIVE {service.ActiveMissionCount}  |  {status}", CommanderUiTheme.MutedLabel);
        DrawLoadoutDropdown(loadoutPanel);
        DrawAltitudeDropdown(altitudePanel);
        if (!string.IsNullOrEmpty(hoverTooltip))
        {
            Vector2 mouse = Event.current.mousePosition;
            float tooltipWidth = Mathf.Min(460f, windowRect.width - 24f);
            const float tooltipHeight = 76f;
            Rect tooltipRect = new(
                Mathf.Clamp(mouse.x + 12f, 8f, windowRect.width - tooltipWidth - 8f),
                Mathf.Clamp(mouse.y + 12f, 30f, windowRect.height - tooltipHeight - 8f),
                tooltipWidth,
                tooltipHeight);
            GUI.Box(tooltipRect, string.Empty, CommanderUiTheme.Panel);
            GUI.Label(new Rect(tooltipRect.x + 10f, tooltipRect.y + 8f, tooltipRect.width - 20f, tooltipRect.height - 16f),
                hoverTooltip, CommanderUiTheme.Label);
        }
        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 44f, 28f));
    }

    private void DrawLoadoutEditor(Rect panel)
    {
        float half = (panel.width - 36f) * 0.5f;
        float balanceWidth = (panel.width - 28f) * 0.5f;
        DrawBalanceButton(CommanderAirCommandService.LoadoutBalance.Primary, "PRIMARY FIRST", panel.x + 12f, panel.y + 8f, balanceWidth);
        DrawBalanceButton(CommanderAirCommandService.LoadoutBalance.Mixed, "MIX 75 / 25", panel.x + 16f + balanceWidth, panel.y + 8f, balanceWidth);
        GUI.Label(new Rect(panel.x + 12f, panel.y + 44f, half, 22f), "PRIMARY WEAPON  [REQUIRED]", CommanderUiTheme.Header);
        GUI.Label(new Rect(panel.x + 24f + half, panel.y + 44f, half, 22f), "SECONDARY WEAPON  [OPTIONAL]", CommanderUiTheme.Header);
        bool popupOpen = openHardpointGroup >= 0 || altitudeDropdownOpen;
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && !popupOpen;
        string primary = service.SelectedPrimaryWeapon == null
            ? "SELECT PRIMARY   v"
            : service.GetMissionWeaponLabel(service.SelectedPrimaryWeapon) + "   v";
        if (GUI.Button(new Rect(panel.x + 12f, panel.y + 70f, half, 58f), primary,
            service.SelectedPrimaryWeapon == null ? CommanderUiTheme.DangerButton : CommanderUiTheme.SelectedButton))
        {
            openHardpointGroup = 0;
            loadoutDropdownScroll = Vector2.zero;
        }
        string secondary = service.SelectedSecondaryWeapon == null
            ? "NONE   v"
            : service.GetMissionWeaponLabel(service.SelectedSecondaryWeapon) + "   v";
        if (GUI.Button(new Rect(panel.x + 24f + half, panel.y + 70f, half, 58f), secondary,
            service.SelectedSecondaryWeapon == null ? CommanderUiTheme.Button : CommanderUiTheme.SelectedButton))
        {
            openHardpointGroup = 1;
            loadoutDropdownScroll = Vector2.zero;
        }
        GUI.enabled = oldEnabled;
        GUI.Label(new Rect(panel.x + 12f, panel.y + 136f, panel.width - 24f, 28f),
            "Balance controls automatic station allocation. Mirrored hardpoints and bay conflicts are applied automatically.", CommanderUiTheme.MutedLabel);
        Rect cannonToggle = new(panel.x + 12f, panel.y + 168f, panel.width - 24f, 30f);
        service.IncludeInternalCannons = GUI.Toggle(
            cannonToggle,
            service.IncludeInternalCannons,
            "INTERNAL CANNONS",
            CommanderUiTheme.Toggle);
        if (cannonToggle.Contains(Event.current.mousePosition))
        {
            hoverTooltip = "Equips built-in cannons when available. Aircraft may not automatically RTB after missiles and bombs are depleted while cannon ammunition remains.";
        }
    }

    private void DrawBalanceButton(CommanderAirCommandService.LoadoutBalance balance, string label, float x, float y, float width)
    {
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && openHardpointGroup < 0 && !altitudeDropdownOpen;
        if (GUI.Button(new Rect(x, y, width - 4f, 28f), label,
            service.SelectedLoadoutBalance == balance ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            service.SelectLoadoutBalance(balance);
        }
        GUI.enabled = oldEnabled;
    }

    private void DrawAltitudeDropdown(Rect altitudePanel)
    {
        if (!altitudeDropdownOpen || altitudePanel.width <= 0f)
        {
            return;
        }

        float[] altitudes = { 0f, 250f, 500f, 1000f, 1500f, 2000f };
        Rect popup = new(altitudePanel.x + 200f, altitudePanel.y + 36f, 180f, altitudes.Length * 34f + 8f);
        GUI.Box(popup, string.Empty, CommanderUiTheme.Window);
        for (int i = 0; i < altitudes.Length; i++)
        {
            string label = altitudes[i] <= 0f ? "STANDARD" : $"{altitudes[i]:0} m";
            if (GUI.Button(new Rect(popup.x + 4f, popup.y + 4f + i * 34f, popup.width - 8f, 30f), label,
                Mathf.Approximately(service.SelectedTargetAltitude, altitudes[i]) ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                service.SetTargetAltitude(altitudes[i]);
                altitudeDropdownOpen = false;
            }
        }
    }

    private void DrawLoadoutDropdown(Rect loadoutPanel)
    {
        if (openHardpointGroup < 0 || openHardpointGroup > 1)
        {
            return;
        }

        bool secondary = openHardpointGroup == 1;
        Rect popup = new(loadoutPanel.x + 50f, loadoutPanel.y + 12f, loadoutPanel.width - 100f, 360f);
        GUI.Box(popup, string.Empty, CommanderUiTheme.Window);
        GUI.Label(new Rect(popup.x + 12f, popup.y + 8f, popup.width - 52f, 24f),
            secondary ? "SELECT SECONDARY WEAPON" : "SELECT PRIMARY WEAPON", CommanderUiTheme.Header);
        if (GUI.Button(new Rect(popup.xMax - 34f, popup.y + 7f, 24f, 24f), "X", CommanderUiTheme.DangerButton))
        {
            openHardpointGroup = -1;
            return;
        }

        Rect view = new(popup.x + 10f, popup.y + 38f, popup.width - 20f, popup.height - 48f);
        int extraRows = secondary ? 1 : 0;
        Rect inner = new(0f, 0f, view.width - 20f,
            Mathf.Max(view.height, (service.WeaponOptions.Count + extraRows + 2) * 38f + 4f));
        loadoutDropdownScroll = GUI.BeginScrollView(view, loadoutDropdownScroll, inner);
        float y = 2f;
        if (secondary && GUI.Button(new Rect(4f, y, inner.width - 8f, 34f), "NONE", CommanderUiTheme.DangerButton))
        {
            service.SelectSecondaryWeapon(-1);
            openHardpointGroup = -1;
            altitudeDropdownOpen = false;
        }
        if (secondary) y += 38f;
        bool separatorDrawn = false;
        for (int i = 0; i < service.WeaponOptions.Count; i++)
        {
            WeaponMount mount = service.WeaponOptions[i];
            if (!separatorDrawn && !service.IsWeaponSuitable(mount))
            {
                GUI.Label(new Rect(4f, y, inner.width - 8f, 30f), "-- OTHER --", CommanderUiTheme.MutedLabel);
                y += 32f;
                separatorDrawn = true;
            }
            if (GUI.Button(new Rect(4f, y, inner.width - 8f, 34f),
                service.GetMissionWeaponLabel(mount), CommanderUiTheme.Button))
            {
                if (secondary) service.SelectSecondaryWeapon(i);
                else service.SelectPrimaryWeapon(i);
                openHardpointGroup = -1;
            }
            y += 38f;
        }
        GUI.EndScrollView();
    }

    private void DrawModeButton(
        CommanderAirCommandService.AirCommandMode mode,
        string label,
        float x,
        float y,
        float width,
        string description)
    {
        Rect rect = new(x, y, width - 4f, 36f);
        if (GUI.Button(rect, label,
            service.SelectedMode == mode ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            service.SelectMode(mode);
            aircraftScroll = Vector2.zero;
            hardpointScroll = Vector2.zero;
            airbaseScroll = Vector2.zero;
            openHardpointGroup = -1;
            altitudeDropdownOpen = false;
        }
        if (rect.Contains(Event.current.mousePosition))
        {
            hoverTooltip = description;
        }
    }

    private void DrawMissionWindow(int windowId)
    {
        float y = 34f;
        float tabWidth = (missionWindowRect.width - 26f) * 0.5f;
        if (GUI.Button(new Rect(10f, y, tabWidth, 26f), $"ACTIVE ({missionAircraft.Count})",
            showIdleAircraft ? CommanderUiTheme.Button : CommanderUiTheme.SelectedButton))
        {
            showIdleAircraft = false;
            missionScroll = Vector2.zero;
        }
        if (GUI.Button(new Rect(16f + tabWidth, y, tabWidth, 26f), $"IDLE ({idleAircraft.Count})",
            showIdleAircraft ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            showIdleAircraft = true;
            missionScroll = Vector2.zero;
        }
        y += 32f;

        if (showIdleAircraft)
        {
            DrawIdleAircraft(y);
            GUI.DragWindow(new Rect(0f, 0f, missionWindowRect.width, 28f));
            return;
        }

        Aircraft? selectedAircraft = null;
        for (int i = 0; i < missionAircraft.Count; i++)
        {
            if (service.IsMissionAircraftSelected(missionAircraft[i]))
            {
                selectedAircraft = missionAircraft[i];
                break;
            }
        }
        float footerHeight = selectedAircraft != null ? 88f : 0f;
        Rect view = new(10f, y, missionWindowRect.width - 20f, missionWindowRect.height - y - 12f - footerHeight);
        Rect inner = new(0f, 0f, view.width - 18f, Mathf.Max(view.height, missionAircraft.Count * 58f + 4f));
        missionScroll = GUI.BeginScrollView(view, missionScroll, inner);
        bool oldEnabled = GUI.enabled;
        for (int i = 0; i < missionAircraft.Count; i++)
        {
            Aircraft aircraft = missionAircraft[i];
            float rowY = 2f + i * 58f;
            if (GUI.Button(new Rect(2f, rowY, inner.width - 120f, 52f),
                service.GetMissionAircraftLabel(aircraft),
                service.IsMissionAircraftSelected(aircraft) ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                service.ToggleMissionAircraft(aircraft);
            }
            GUI.enabled = oldEnabled && !service.AwaitingAreaSelection;
            if (GUI.Button(new Rect(inner.width - 114f, rowY, 54f, 52f), "AREA", CommanderUiTheme.Button))
            {
                service.BeginMissionAreaEdit(aircraft);
            }
            if (GUI.Button(new Rect(inner.width - 56f, rowY, 54f, 52f), "RTB", CommanderUiTheme.DangerButton))
            {
                service.RequestReturnToBase(aircraft);
            }
            GUI.enabled = oldEnabled;
        }
        GUI.EndScrollView();

        if (selectedAircraft != null)
        {
            float footerY = view.yMax + 6f;
            GUI.Box(new Rect(10f, footerY, missionWindowRect.width - 20f, 78f), string.Empty, CommanderUiTheme.Panel);
            GUI.Label(new Rect(20f, footerY + 7f, 104f, 24f), "AREA RADIUS", CommanderUiTheme.MutedLabel);
            GUI.enabled = oldEnabled && !service.AwaitingAreaSelection;
            if (GUI.Button(new Rect(126f, footerY + 5f, 32f, 28f), "-", CommanderUiTheme.Button))
            {
                service.StepMissionRadius(selectedAircraft, -5f);
            }
            GUI.Label(new Rect(164f, footerY + 8f, 74f, 24f),
                $"{service.GetMissionRadiusKm(selectedAircraft):0} km",
                CommanderUiTheme.Header);
            if (GUI.Button(new Rect(240f, footerY + 5f, 32f, 28f), "+", CommanderUiTheme.Button))
            {
                service.StepMissionRadius(selectedAircraft, 5f);
            }
            if (GUI.Button(
                new Rect(20f, footerY + 39f, missionWindowRect.width - 40f, 32f),
                "MOVE AREA CENTER",
                CommanderUiTheme.PrimaryButton))
            {
                service.BeginMissionAreaEdit(selectedAircraft);
            }
            GUI.enabled = oldEnabled;
        }
        GUI.DragWindow(new Rect(0f, 0f, missionWindowRect.width, 28f));
    }

    /// <summary>
    /// Aircraft that are already in the world and still flying their own orders. Tasking one
    /// places a mission area for it exactly like a freshly spawned mission.
    /// </summary>
    private void DrawIdleAircraft(float y)
    {
        GUI.Label(new Rect(10f, y, missionWindowRect.width - 20f, 34f),
            "Pick the mission type on the left, then TASK an aircraft and place its area on the map.",
            CommanderUiTheme.MutedLabel);
        y += 38f;

        Rect view = new(10f, y, missionWindowRect.width - 20f, missionWindowRect.height - y - 12f);
        Rect inner = new(0f, 0f, view.width - 18f, Mathf.Max(view.height, idleAircraft.Count * 42f + 4f));
        missionScroll = GUI.BeginScrollView(view, missionScroll, inner);
        bool oldEnabled = GUI.enabled;
        for (int i = 0; i < idleAircraft.Count; i++)
        {
            Aircraft aircraft = idleAircraft[i];
            float rowY = 2f + i * 42f;
            if (GUI.Button(new Rect(2f, rowY, inner.width - 70f, 36f),
                CommanderGameAccess.GetUnitLabel(aircraft),
                CommanderUiTheme.Button))
            {
                CommanderSelectionService.Instance?.SelectUnit(aircraft, false);
                CommanderCameraFollowService.Instance?.FocusSelection();
            }
            GUI.enabled = oldEnabled && !service.AwaitingAreaSelection;
            if (GUI.Button(new Rect(inner.width - 66f, rowY, 64f, 36f), "TASK", CommanderUiTheme.PrimaryButton))
            {
                service.BeginAdoption(aircraft);
            }
            GUI.enabled = oldEnabled;
        }
        GUI.EndScrollView();
    }
}
