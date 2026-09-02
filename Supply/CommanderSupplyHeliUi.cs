using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderSupplyHeliUi
{
    private const int WindowId = 0x434F4D48;

    private readonly CommanderSupplyHeliService service;
    private Rect windowRect = new(0f, 0f, 800f, 650f);
    private Vector2 aircraftScroll;
    private Vector2 airbaseScroll;
    private Vector2 dropdownScroll;
    private bool positionInitialized;
    private bool helpVisible;
    private bool terrainClearanceDropdownOpen;
    private int step;
    private int openCargoSlot = -1;

    internal CommanderSupplyHeliUi(CommanderSupplyHeliService service)
    {
        this.service = service;
    }

    internal bool Visible { get; private set; }
    internal bool ShowLz { get; private set; } = true;

    internal void ResetPosition()
    {
        positionInitialized = false;
    }

    internal void Toggle()
    {
        Visible = !Visible;
        service.SetUiVisible(Visible);
        if (!Visible)
        {
            openCargoSlot = -1;
            terrainClearanceDropdownOpen = false;
            return;
        }

        step = 0;
        helpVisible = false;
        openCargoSlot = -1;
        terrainClearanceDropdownOpen = false;
        service.RefreshOptions();
    }

    internal void Hide()
    {
        Visible = false;
        service.SetUiVisible(false);
        openCargoSlot = -1;
    }

    internal void Tick()
    {
        float width = Mathf.Min(800f, CommanderUiScale.Width - 32f);
        float height = Mathf.Min(650f, CommanderUiScale.Height - 32f);
        if (!positionInitialized)
        {
            windowRect = new Rect(
                74f,
                Mathf.Clamp(CommanderUiScale.Height * 0.66f - height * 0.5f, 12f, CommanderUiScale.Height - height - 12f),
                width,
                height);
            positionInitialized = true;
        }
        else
        {
            windowRect.width = width;
            windowRect.height = height;
        }

        windowRect = CommanderUiTheme.ClampWindow(windowRect);
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screenPoint);
        return Visible && windowRect.Contains(guiPoint);
    }

    internal void Draw()
    {
        if (!Visible)
        {
            return;
        }

        CommanderUiTheme.Ensure();
        string title = step switch
        {
            0 => "SUPPLY RUN  1/3  AIRCRAFT",
            1 => "SUPPLY RUN  2/3  LOADOUT",
            _ => "SUPPLY RUN  3/3  DEPLOYMENT"
        };
        windowRect = GUI.Window(WindowId, windowRect, DrawWindow, title, CommanderUiTheme.Window);
    }

    private void DrawWindow(int windowId)
    {
        CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible);
        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.DangerButton))
        {
            Hide();
            return;
        }

        switch (step)
        {
            case 0:
                DrawAircraftStep();
                break;
            case 1:
                DrawCargoStep();
                break;
            default:
                DrawDeploymentStep();
                break;
        }

        DrawFooter();
        if (helpVisible)
        {
            CommanderUiTheme.DrawHelpOverlay(new Rect(18f, 34f, windowRect.width - 36f, 94f), GetHelpText());
        }

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
    }

    private void DrawAircraftStep()
    {
        GUI.Label(new Rect(18f, 38f, windowRect.width - 36f, 24f),
            "Select a cargo-capable aircraft. Selection immediately opens its loadout.", CommanderUiTheme.MutedLabel);

        IReadOnlyList<CommanderSupplyHeliService.CargoAircraftOption> aircraft = service.AircraftOptions;
        Rect view = new(18f, 70f, windowRect.width - 36f, windowRect.height - 138f);
        float cardHeight = 66f;
        Rect inner = new(0f, 0f, view.width - 20f, Mathf.Max(view.height, aircraft.Count * cardHeight + 8f));
        aircraftScroll = GUI.BeginScrollView(view, aircraftScroll, inner);
        for (int i = 0; i < aircraft.Count; i++)
        {
            CommanderSupplyHeliService.CargoAircraftOption option = aircraft[i];
            Rect card = new(4f, 4f + i * cardHeight, inner.width - 8f, cardHeight - 6f);
            if (GUI.Button(card, string.Empty, CommanderUiTheme.Button))
            {
                service.SelectAircraft(i);
                step = 1;
                openCargoSlot = -1;
            }

            DrawSprite(new Rect(card.x + 10f, card.y + 8f, 44f, 44f), option.Definition.mapIcon ?? option.Definition.friendlyIcon);
            GUI.Label(new Rect(card.x + 64f, card.y + 7f, card.width - 76f, 24f), option.Label, CommanderUiTheme.Header);
            GUI.Label(new Rect(card.x + 64f, card.y + 31f, card.width - 76f, 22f),
                service.GetAircraftButtonLabel(option), CommanderUiTheme.MutedLabel);
        }
        GUI.EndScrollView();

        if (aircraft.Count == 0)
        {
            GUI.Label(view, "No cargo-capable aircraft definitions were found.", CommanderUiTheme.Label);
        }
    }

    private void DrawCargoStep()
    {
        CommanderSupplyHeliService.CargoAircraftOption? aircraft = service.SelectedAircraft;
        if (aircraft == null)
        {
            GUI.Label(new Rect(18f, 44f, windowRect.width - 36f, 30f), "No aircraft selected.", CommanderUiTheme.Label);
            return;
        }

        GUI.Box(new Rect(18f, 38f, windowRect.width - 36f, 50f), string.Empty, CommanderUiTheme.Panel);
        DrawSprite(new Rect(28f, 43f, 40f, 40f), aircraft.Definition.mapIcon ?? aircraft.Definition.friendlyIcon);
        GUI.Label(new Rect(78f, 42f, windowRect.width - 108f, 24f), aircraft.Label, CommanderUiTheme.Header);
        GUI.Label(new Rect(78f, 65f, windowRect.width - 108f, 18f), "Configure cargo bays and optional defensive equipment.", CommanderUiTheme.MutedLabel);

        float optionsWidth = 220f;
        float cargoWidth = windowRect.width - optionsWidth - 48f;
        Rect cargoPanel = new(18f, 98f, cargoWidth, windowRect.height - 168f);
        Rect optionsPanel = new(cargoPanel.xMax + 12f, 98f, optionsWidth, cargoPanel.height);
        bool cargoPopupOpen = openCargoSlot >= 0 && openCargoSlot < aircraft.CargoSlots.Count;
        GUI.Box(cargoPanel, string.Empty, CommanderUiTheme.Panel);
        GUI.Box(optionsPanel, string.Empty, CommanderUiTheme.Panel);

        GUI.Label(new Rect(cargoPanel.x + 12f, cargoPanel.y + 8f, cargoPanel.width - 24f, 22f), "CARGO BAYS", CommanderUiTheme.Header);
        float half = (cargoPanel.width - 36f) * 0.5f;
        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && !cargoPopupOpen;
        if (GUI.Button(new Rect(cargoPanel.x + 12f, cargoPanel.y + 36f, half, 30f), "Randomize", CommanderUiTheme.Button))
        {
            service.RandomizeSelectedCargo();
            openCargoSlot = -1;
        }
        if (GUI.Button(new Rect(cargoPanel.x + 24f + half, cargoPanel.y + 36f, half, 30f), "Clear", CommanderUiTheme.DangerButton))
        {
            service.ClearSelectedCargo();
            openCargoSlot = -1;
        }

        float y = cargoPanel.y + 76f;
        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            CommanderSupplyHeliService.CargoSlotOption slot = aircraft.CargoSlots[i];
            if (!slot.IsCombinedBay)
            {
                continue;
            }

            DrawCargoButton(new Rect(cargoPanel.x + 12f, y, cargoPanel.width - 24f, 54f), i, slot);
            y += 62f;
        }

        int splitColumn = 0;
        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            CommanderSupplyHeliService.CargoSlotOption slot = aircraft.CargoSlots[i];
            if (slot.IsCombinedBay)
            {
                continue;
            }

            float x = splitColumn == 0 ? cargoPanel.x + 12f : cargoPanel.x + 24f + half;
            DrawCargoButton(new Rect(x, y, half, 54f), i, slot);
            splitColumn++;
            if (splitColumn == 2)
            {
                splitColumn = 0;
                y += 62f;
            }
        }

        GUI.Label(new Rect(optionsPanel.x + 12f, optionsPanel.y + 8f, optionsPanel.width - 24f, 22f), "SUPPORT", CommanderUiTheme.Header);
        service.IncludeEcm = GUI.Toggle(new Rect(optionsPanel.x + 12f, optionsPanel.y + 42f, optionsPanel.width - 24f, 28f),
            service.IncludeEcm, "ECM if available", CommanderUiTheme.Toggle);
        service.IncludeCountermeasures = GUI.Toggle(new Rect(optionsPanel.x + 12f, optionsPanel.y + 76f, optionsPanel.width - 24f, 28f),
            service.IncludeCountermeasures, "Countermeasures", CommanderUiTheme.Toggle);
        service.FillRemainingHardpoints = GUI.Toggle(new Rect(optionsPanel.x + 12f, optionsPanel.y + 110f, optionsPanel.width - 24f, 44f),
            service.FillRemainingHardpoints, "Fill remaining hardpoints\n(Basegame loadout rules)", CommanderUiTheme.Toggle);
        GUI.Label(new Rect(optionsPanel.x + 12f, optionsPanel.y + 168f, optionsPanel.width - 24f, 90f),
            "Combined cargo bays conflict with their forward and rear sections. Selecting one automatically clears incompatible slots.", CommanderUiTheme.MutedLabel);
        GUI.enabled = previousEnabled;

        DrawCargoDropdown(aircraft, cargoPanel);
    }

    private void DrawDeploymentStep()
    {
        CommanderSupplyHeliService.CargoAircraftOption? aircraft = service.SelectedAircraft;
        float panelTop = 42f;
        float panelHeight = windowRect.height - 112f;
        float leftWidth = (windowRect.width - 48f) * 0.54f;
        Rect airfieldPanel = new(18f, panelTop, leftWidth, panelHeight);
        Rect missionPanel = new(airfieldPanel.xMax + 12f, panelTop, windowRect.width - airfieldPanel.xMax - 30f, panelHeight);
        GUI.Box(airfieldPanel, string.Empty, CommanderUiTheme.Panel);
        GUI.Box(missionPanel, string.Empty, CommanderUiTheme.Panel);

        GUI.Label(new Rect(airfieldPanel.x + 12f, airfieldPanel.y + 10f, airfieldPanel.width - 24f, 22f), "AIRFIELD", CommanderUiTheme.Header);
        GUI.Label(new Rect(airfieldPanel.x + 12f, airfieldPanel.y + 34f, airfieldPanel.width - 24f, 40f),
            "Nearest to the camera first. WAIT airfields support the aircraft but have no free compatible hangar.", CommanderUiTheme.MutedLabel);

        IReadOnlyList<CommanderSupplyHeliService.AirbaseOption> airbases = service.AirbaseOptions;
        Rect view = new(airfieldPanel.x + 10f, airfieldPanel.y + 78f, airfieldPanel.width - 20f, airfieldPanel.height - 128f);
        Rect inner = new(0f, 0f, view.width - 20f, Mathf.Max(view.height, airbases.Count * 42f + 8f));
        airbaseScroll = GUI.BeginScrollView(view, airbaseScroll, inner);
        for (int i = 0; i < airbases.Count; i++)
        {
            CommanderSupplyHeliService.AirbaseOption option = airbases[i];
            GUIStyle style = ReferenceEquals(option.Airbase, service.SelectedAirbase)
                ? CommanderUiTheme.SelectedButton
                : CommanderUiTheme.Button;
            if (GUI.Button(new Rect(4f, 4f + i * 42f, inner.width - 8f, 36f), service.GetAirbaseButtonLabel(option), style))
            {
                service.SelectAirbase(i);
            }
        }
        GUI.EndScrollView();
        service.UseOtherAirfields = GUI.Toggle(new Rect(airfieldPanel.x + 12f, airfieldPanel.yMax - 42f, airfieldPanel.width - 24f, 28f),
            service.UseOtherAirfields, "Use other friendly airfields if selected airfield is busy", CommanderUiTheme.Toggle);

        GUI.Label(new Rect(missionPanel.x + 12f, missionPanel.y + 10f, missionPanel.width - 24f, 22f), "MISSION", CommanderUiTheme.Header);
        GUI.Label(new Rect(missionPanel.x + 12f, missionPanel.y + 38f, missionPanel.width - 24f, 42f),
            aircraft?.Label ?? "No aircraft selected", CommanderUiTheme.Label);

        if (GUI.Toggle(new Rect(missionPanel.x + 12f, missionPanel.y + 92f, missionPanel.width - 24f, 28f),
            !service.AirdropDelivery, "Land and unload", CommanderUiTheme.Toggle))
        {
            service.AirdropDelivery = false;
        }

        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && service.SelectedCargoSupportsAirdrop;
        if (GUI.Toggle(new Rect(missionPanel.x + 12f, missionPanel.y + 124f, missionPanel.width - 24f, 28f),
            service.AirdropDelivery, "Airdrop cargo [PARA]", CommanderUiTheme.Toggle))
        {
            service.AirdropDelivery = true;
        }
        GUI.enabled = oldEnabled;

        service.HighTerrainClearance = GUI.Toggle(
            new Rect(missionPanel.x + 12f, missionPanel.y + 166f, missionPanel.width - 24f, 28f),
            service.HighTerrainClearance,
            "TERRAIN CLEARANCE",
            CommanderUiTheme.Toggle);

        GUI.enabled = oldEnabled && service.HighTerrainClearance;
        string clearanceLabel = service.TerrainClearanceMeters <= 0f
            ? "STANDARD   v"
            : $"{service.TerrainClearanceMeters:0} m   v";
        if (GUI.Button(
            new Rect(missionPanel.x + 12f, missionPanel.y + 198f, missionPanel.width - 24f, 30f),
            clearanceLabel,
            terrainClearanceDropdownOpen ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            terrainClearanceDropdownOpen = !terrainClearanceDropdownOpen;
        }
        GUI.enabled = oldEnabled;

        GUI.Label(new Rect(missionPanel.x + 12f, missionPanel.y + 240f, missionPanel.width - 24f, 100f),
            "After confirming, click the destination in the 3D world. A busy locked airfield keeps the mission queued until a compatible hangar is free.", CommanderUiTheme.MutedLabel);
        GUI.Label(new Rect(missionPanel.x + 12f, missionPanel.y + 340f, missionPanel.width - 24f, 24f),
            $"Queued supply runs: {service.QueuedSpawnCount}", CommanderUiTheme.Label);

        if (GUI.Button(new Rect(missionPanel.xMax - 112f, missionPanel.yMax - 42f, 100f, 26f),
            ShowLz ? "LZ ON" : "SHOW LZ", ShowLz ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            ShowLz = !ShowLz;
        }

        if (airbases.Count == 0)
        {
            GUI.Label(view, "No friendly airfield supports this aircraft.", CommanderUiTheme.Label);
        }

        DrawTerrainClearanceDropdown(missionPanel);
    }

    private void DrawTerrainClearanceDropdown(Rect missionPanel)
    {
        if (!terrainClearanceDropdownOpen || !service.HighTerrainClearance)
        {
            return;
        }

        float[] clearances = { 0f, 20f, 35f, 50f, 75f, 100f, 150f, 200f, 250f };
        float rowHeight = 30f;
        Rect popup = new(
            missionPanel.x + 30f,
            missionPanel.y + 230f,
            missionPanel.width - 60f,
            clearances.Length * rowHeight + 8f);
        GUI.Box(popup, string.Empty, CommanderUiTheme.Window);
        for (int i = 0; i < clearances.Length; i++)
        {
            float value = clearances[i];
            string label = value <= 0f ? "STANDARD" : $"{value:0} m";
            if (GUI.Button(
                new Rect(popup.x + 4f, popup.y + 4f + i * rowHeight, popup.width - 8f, rowHeight - 2f),
                label,
                Mathf.Approximately(service.TerrainClearanceMeters, value)
                    ? CommanderUiTheme.SelectedButton
                    : CommanderUiTheme.Button))
            {
                service.TerrainClearanceMeters = value;
                terrainClearanceDropdownOpen = false;
            }
        }
    }

    private void DrawFooter()
    {
        float y = windowRect.height - 56f;
        if (step > 0 && GUI.Button(new Rect(18f, y, 92f, 34f), "BACK", CommanderUiTheme.Button))
        {
            service.CancelDeploymentSelection();
            step--;
            openCargoSlot = -1;
        }

        if (step == 2)
        {
            bool oldEnabled = GUI.enabled;
            bool canStart = service.SelectedAirbase != null && service.HasSelectedCargo && !service.AwaitingTargetSelection;
            GUI.enabled = oldEnabled && canStart;
            if (GUI.Button(new Rect(116f, y, 210f, 34f),
                service.AirdropDelivery ? "SELECT AIRDROP TARGET" : "SELECT DELIVERY TARGET", CommanderUiTheme.PrimaryButton))
            {
                service.BeginSelectedCargoRun();
            }
            GUI.enabled = oldEnabled;
        }

        if (step == 1)
        {
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && service.HasSelectedCargo;
            if (GUI.Button(new Rect(windowRect.width - 128f, y, 110f, 34f), "NEXT", CommanderUiTheme.PrimaryButton))
            {
                step = 2;
                openCargoSlot = -1;
            }
            GUI.enabled = oldEnabled;
        }

        float statusX = step == 2 ? 336f : 120f;
        GUI.Label(new Rect(statusX, y, windowRect.width - statusX - 18f, 36f), service.StatusText, CommanderUiTheme.MutedLabel);
    }

    private void DrawCargoButton(Rect rect, int slotIndex, CommanderSupplyHeliService.CargoSlotOption slot)
    {
        GUIStyle style = slot.SelectedMount != null ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button;
        if (GUI.Button(rect, service.GetCargoSlotButtonLabel(slot) + "   v", style))
        {
            openCargoSlot = openCargoSlot == slotIndex ? -1 : slotIndex;
            dropdownScroll = Vector2.zero;
        }
    }

    private void DrawCargoDropdown(CommanderSupplyHeliService.CargoAircraftOption aircraft, Rect cargoPanel)
    {
        if (openCargoSlot < 0 || openCargoSlot >= aircraft.CargoSlots.Count)
        {
            return;
        }

        CommanderSupplyHeliService.CargoSlotOption slot = aircraft.CargoSlots[openCargoSlot];
        float height = Mathf.Min(330f, cargoPanel.height - 56f);
        Rect popup = new(cargoPanel.x + 28f, cargoPanel.y + 72f, cargoPanel.width - 56f, height);
        GUI.Box(popup, string.Empty, CommanderUiTheme.Window);
        GUI.Label(new Rect(popup.x + 12f, popup.y + 8f, popup.width - 52f, 24f), slot.Label, CommanderUiTheme.Header);
        if (GUI.Button(new Rect(popup.xMax - 34f, popup.y + 7f, 24f, 24f), "X", CommanderUiTheme.DangerButton))
        {
            openCargoSlot = -1;
            return;
        }

        Rect view = new(popup.x + 10f, popup.y + 38f, popup.width - 20f, popup.height - 48f);
        Rect inner = new(0f, 0f, view.width - 20f, Mathf.Max(view.height, (slot.Mounts.Count + 1) * 36f + 4f));
        dropdownScroll = GUI.BeginScrollView(view, dropdownScroll, inner);
        if (GUI.Button(new Rect(4f, 2f, inner.width - 8f, 32f), "NONE", CommanderUiTheme.DangerButton))
        {
            service.SelectCargoMount(openCargoSlot, -1);
            openCargoSlot = -1;
        }
        for (int i = 0; i < slot.Mounts.Count; i++)
        {
            if (GUI.Button(new Rect(4f, 38f + i * 36f, inner.width - 8f, 32f),
                service.GetCargoMountLabel(slot.Mounts[i]), CommanderUiTheme.Button))
            {
                service.SelectCargoMount(openCargoSlot, i);
                openCargoSlot = -1;
            }
        }
        GUI.EndScrollView();
    }

    private string GetHelpText()
    {
        return step switch
        {
            0 => "Choose any helicopter or aircraft exposing Basegame cargo hardpoints. Cards show faction availability or purchase cost. Selecting an aircraft advances to its loadout.",
            1 => "Combined bays exclude conflicting forward/rear bays. [Airdrop] marks cargo with a Basegame parachute. ECM, countermeasures and Fill Rest use compatible remaining hardpoints. BACK cancels the current deployment setup.",
            _ => "READY has a free compatible hangar; WAIT queues the run. Other Airfields allows fallback instead of waiting at the selected base. Terrain Clearance raises Basegame terrain-follow altitude to the selected minimum. Select a target in 3D; hold Shift while clicking to repeat the configured mission. SHOW LZ controls only its marker."
        };
    }

    private static void DrawSprite(Rect rect, Sprite? sprite)
    {
        if (sprite == null || sprite.texture == null)
        {
            return;
        }

        Rect textureRect = sprite.textureRect;
        Texture2D texture = sprite.texture;
        Rect uv = new(
            textureRect.x / texture.width,
            textureRect.y / texture.height,
            textureRect.width / texture.width,
            textureRect.height / texture.height);
        GUI.DrawTextureWithTexCoords(rect, texture, uv, alphaBlend: true);
    }
}
