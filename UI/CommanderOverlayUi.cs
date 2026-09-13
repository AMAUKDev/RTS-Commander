using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderOverlayUi : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive
{
    internal static CommanderOverlayUi? Instance { get; private set; }

    private const int WindowId = 0x434F4D4D;
    private const int ReserveWindowId = 0x434F4D52;
    private const int PinnedWindowId = 0x434F4D50;
    private const int RadarWindowId = 0x434F4D44;
    private const int SettingsWindowId = 0x434F4D53;

    private readonly CommanderSelectionService selectionService;
    private readonly CommanderMoveService moveService;
    private readonly CommanderGroupService groupService;
    private readonly CommanderSpawnService spawnService;
    private readonly CommanderRadarService radarService;
    private readonly CommanderMobileEmplacementService mobileEmplacementService;
    private readonly CommanderRepairService repairService;
    private readonly CommanderDirectPathService directPathService;
    private readonly CommanderSupplyHeliService supplyHeliService;
    private readonly CommanderAirCommandService airCommandService;
    private readonly CommanderNavalPurchaseService navalPurchaseService;
    private readonly CommanderSamSiteAnalyzerService samSiteAnalyzerService;
    private readonly CommanderSamSiteService samSiteService;
    private readonly CommanderEconomyService economyService;
    private readonly CommanderSupplyHeliUi supplyHeliUi;
    private readonly CommanderAirCommandUi airCommandUi;
    private readonly CommanderNavalPurchaseUi navalPurchaseUi;
    private readonly CommanderSamSiteAnalyzerUi samSiteAnalyzerUi;
    private readonly CommanderEconomyUi economyUi;
    private readonly CommanderDepotUi depotUi;
    private readonly CommanderUnitListUi unitListUi;
    private readonly CommanderAiLogUi aiLogUi;
    private readonly CommanderWorldMarkerRenderer worldMarkerRenderer;
    private readonly Action unlockAdvancedFeatures;
    private readonly Action exitCommander;

    private bool panelVisible;
    private bool reserveWindowVisible;
    private bool panelHelpVisible;
    private bool reserveHelpVisible;
    private bool pinnedHelpVisible;
    private bool radarHelpVisible;
    private bool selectionHelpVisible;
    private bool settingsVisible;
    private bool settingsHelpVisible;
    private bool advancedUnlockConfirmation;
    private int settingsTab;
    private string? bindingCapture;
    private bool pinnedWindowVisible = true;
    private int pinnedTab = 1;
    private bool siteAirbaseDropdownOpen;
    private bool siteThresholdDropdownOpen;
    private bool showSupplyMissions = true;
    private bool showAirCommandMissions = true;
    private readonly Dictionary<Canvas, bool> screenshotCanvasStates = new();
    private int screenshotUiStage;
    private bool reopenTacticalMapAfterScreenshot;
    private bool showCommandButton = CommanderSettings.ShowCommandButton;
    private bool showFactionMoney = CommanderSettings.ShowFactionMoney;
    private bool showTacticalMap = CommanderSettings.ShowTacticalMap;
    private bool showSelectionBar = CommanderSettings.ShowSelectionBar;
    private bool showPinnedUnits = CommanderSettings.ShowPinnedUnits;
    private bool showUnitSystems = CommanderSettings.ShowUnitSystems;
    private bool showDepotUi = CommanderSettings.ShowDepotUi;
    private bool showSupplyUi = CommanderSettings.ShowSupplyUi;
    private bool showAirCommandUi = CommanderSettings.ShowAirCommandUi;
    private bool showNavalUi = CommanderSettings.ShowNavalUi;
    private bool showSamAnalyzerUi = CommanderSettings.ShowSamAnalyzerUi;
    private bool showWorldMarkers = CommanderSettings.ShowWorldMarkers;
    private bool showUnitListUi = CommanderSettings.ShowUnitListUi;
    private bool showBuildUi = CommanderSettings.ShowBuildUi;
    private bool reserveShowsUnits;
    private bool positionsInitialized;
    private Rect launcherRect;
    private Rect moneyRect;
    private Rect enemyPlanRect;
    private Rect playerPlanRect;
    private Rect panelRect;
    private Rect reserveWindowRect;
    private Rect selectionBarRect;
    private readonly List<string> chipTypes = new();
    private readonly List<int> chipCounts = new();
    private readonly List<string> chipLabels = new();
    private int chipRevision = -1;
    private int chipCount = -1;
    private readonly List<CommanderShortcutReference.Entry> shortcutEntries = new();
    private Vector2 shortcutScroll;
    private Rect pinnedWindowRect;
    private Rect radarWindowRect;
    private Rect selectionHelpRect;
    private Rect settingsWindowRect;
    private Rect pinnedLauncherRect;
    private Vector2 reserveScroll;
    private Vector2 pinnedScroll;
    private GUIStyle? ghostCommandStyle;
    private Unit? siteUiTarget;
    private string selectionCardText = string.Empty;
    private float nextSelectionCardAt;
    private const float LoadoutRowHeight = 22f;
    private readonly List<string> loadoutRows = new();
    private readonly List<string> loadoutNames = new();
    private readonly List<int> loadoutAmmo = new();
    private readonly List<int> loadoutFullAmmo = new();
    private Unit? loadoutUnit;
    private float nextLoadoutAt;

    internal CommanderOverlayUi(
        CommanderSelectionService selectionService,
        CommanderMoveService moveService,
        CommanderGroupService groupService,
        CommanderSpawnService spawnService,
        CommanderRadarService radarService,
        CommanderMobileEmplacementService mobileEmplacementService,
        CommanderRepairService repairService,
        CommanderDirectPathService directPathService,
        CommanderSupplyHeliService supplyHeliService,
        CommanderAirCommandService airCommandService,
        CommanderNavalPurchaseService navalPurchaseService,
        CommanderSamSiteAnalyzerService samSiteAnalyzerService,
        CommanderSamSiteService samSiteService,
        CommanderEconomyService economyService,
        Action unlockAdvancedFeatures,
        Action exitCommander)
    {
        Instance = this;
        this.selectionService = selectionService;
        this.moveService = moveService;
        this.groupService = groupService;
        this.spawnService = spawnService;
        this.radarService = radarService;
        this.mobileEmplacementService = mobileEmplacementService;
        this.repairService = repairService;
        this.directPathService = directPathService;
        this.supplyHeliService = supplyHeliService;
        this.airCommandService = airCommandService;
        this.navalPurchaseService = navalPurchaseService;
        this.samSiteAnalyzerService = samSiteAnalyzerService;
        this.samSiteService = samSiteService;
        this.economyService = economyService;
        this.unlockAdvancedFeatures = unlockAdvancedFeatures;
        this.exitCommander = exitCommander;
        supplyHeliUi = new CommanderSupplyHeliUi(supplyHeliService);
        airCommandUi = new CommanderAirCommandUi(airCommandService);
        navalPurchaseUi = new CommanderNavalPurchaseUi(navalPurchaseService);
        samSiteAnalyzerUi = new CommanderSamSiteAnalyzerUi(
            samSiteAnalyzerService,
            samSiteService,
            supplyHeliService);
        economyUi = new CommanderEconomyUi(economyService, repairService);
        depotUi = new CommanderDepotUi(spawnService);
        unitListUi = new CommanderUnitListUi(selectionService, groupService);
        aiLogUi = new CommanderAiLogUi();
        worldMarkerRenderer = new CommanderWorldMarkerRenderer(
            selectionService,
            moveService,
            spawnService,
            supplyHeliService,
            samSiteAnalyzerService,
            samSiteService);
    }

    public void Activate()
    {
        panelVisible = false;
        reserveWindowVisible = false;
        panelHelpVisible = false;
        reserveHelpVisible = false;
        supplyHeliUi.Hide();
        airCommandUi.Hide();
        navalPurchaseUi.Hide();
        samSiteAnalyzerUi.Hide();
        economyUi.Hide();
        depotUi.Reset();
        unitListUi.Hide();
        aiLogUi.Hide();
        ResetScreenshotUi();
        settingsVisible = false;
        bindingCapture = null;
        advancedUnlockConfirmation = false;
    }

    public void Deactivate()
    {
        ResetScreenshotUi();
        panelVisible = false;
        reserveWindowVisible = false;
        settingsVisible = false;
        bindingCapture = null;
        advancedUnlockConfirmation = false;
        supplyHeliUi.Hide();
        airCommandUi.Hide();
        navalPurchaseUi.Hide();
        samSiteAnalyzerUi.Hide();
        economyUi.Hide();
        depotUi.Reset();
        unitListUi.Hide();
        aiLogUi.Hide();

        // Fast-forward is a commander-view affordance. Leaving RTS mode — including on a scene
        // change or shutdown, which also land here — puts the clock back, so nobody ends up flying
        // at 4x or dropping into the next mission already sped up.
        if (!UnityEngine.Mathf.Approximately(UnityEngine.Time.timeScale, 1f))
        {
            TimeScaleManager.Scale = 1f;
        }
    }

    public void TickActive()
    {
        CommanderUiTheme.Ensure();
        if (screenshotUiStage == 2)
        {
            MaintainAllUiHidden();
        }
        // Air Command places its mission areas on this map, so it keeps it up whatever the
        // Tactical map setting says — closing it here would fight that window's own reopen.
        if (!showTacticalMap && !airCommandUi.Visible && CommanderTacticalMapService.Instance?.IsOpen == true)
        {
            CommanderTacticalMapService.Instance.Close();
        }
        float centerY = CommanderUiScale.Height * 0.5f;
        launcherRect = new Rect(10f, centerY - 42f, 52f, 84f);
        moneyRect = new Rect((CommanderUiScale.Width - 250f) * 0.5f, 10f, 250f, 38f);
        enemyPlanRect = new Rect(moneyRect.x, moneyRect.yMax + 4f, 250f, 30f);
        playerPlanRect = new Rect(enemyPlanRect.x, enemyPlanRect.yMax + 4f, 250f, 30f);

        if (!positionsInitialized)
        {
            float panelHeight = Mathf.Min(760f, CommanderUiScale.Height - 24f);
            panelRect = new Rect(74f, Mathf.Max(12f, centerY - panelHeight * 0.5f), 400f, panelHeight);
            float reserveWidth = Mathf.Min(590f, CommanderUiScale.Width - 24f);
            float reserveHeight = Mathf.Min(610f, CommanderUiScale.Height - 24f);
            reserveWindowRect = new Rect(
                Mathf.Max(12f, CommanderUiScale.Width - reserveWidth - 12f),
                Mathf.Max(58f, CommanderUiScale.Height - reserveHeight - 12f),
                reserveWidth,
                reserveHeight);
            pinnedWindowRect = new Rect(
                Mathf.Max(12f, CommanderUiScale.Width - 354f),
                Mathf.Clamp(CommanderUiScale.Height * 0.66f - 170f, 58f, CommanderUiScale.Height - 352f),
                342f,
                340f);
            radarWindowRect = new Rect(
                Mathf.Max(12f, CommanderUiScale.Width - 442f),
                Mathf.Clamp(CommanderUiScale.Height * 0.66f - 530f, 58f, CommanderUiScale.Height - 620f),
                430f,
                608f);
            float settingsWidth = Mathf.Min(680f, CommanderUiScale.Width - 24f);
            float settingsHeight = Mathf.Min(790f, CommanderUiScale.Height - 24f);
            settingsWindowRect = new Rect(
                Mathf.Max(12f, (CommanderUiScale.Width - settingsWidth) * 0.5f),
                Mathf.Max(12f, (CommanderUiScale.Height - settingsHeight) * 0.5f),
                settingsWidth,
                settingsHeight);
            positionsInitialized = true;
        }
        else
        {
            panelRect.height = Mathf.Min(760f, CommanderUiScale.Height - 24f);
            reserveWindowRect.width = Mathf.Min(590f, CommanderUiScale.Width - 24f);
            reserveWindowRect.height = Mathf.Min(610f, CommanderUiScale.Height - 24f);
            settingsWindowRect.width = Mathf.Min(680f, CommanderUiScale.Width - 24f);
            settingsWindowRect.height = Mathf.Min(790f, CommanderUiScale.Height - 24f);
        }
        panelRect = CommanderUiTheme.ClampWindow(panelRect);
        reserveWindowRect = CommanderUiTheme.ClampWindow(reserveWindowRect);
        pinnedWindowRect = CommanderUiTheme.ClampWindow(pinnedWindowRect);
        settingsWindowRect = CommanderUiTheme.ClampWindow(settingsWindowRect);
        pinnedLauncherRect = new Rect(
            Mathf.Min(CommanderUiScale.Width - 70f, pinnedWindowRect.xMax + 6f),
            pinnedWindowRect.y,
            62f,
            28f);
        bool samSiteFocused = samSiteService.IsConstructionCore(selectionService.FocusedSelection);
        radarWindowRect.width = Mathf.Min(
            samSiteFocused ? 430f : 380f,
            CommanderUiScale.Width - 24f);
        radarWindowRect.height = Mathf.Min(
            samSiteFocused ? 734f : 450f,
            CommanderUiScale.Height - 24f);
        radarWindowRect = CommanderUiTheme.ClampWindow(radarWindowRect);
        // A mixed selection grows the bar upward to fit the per-type chip row; a single unit grows
        // it to fit its loadout instead, two weapons per row.
        RefreshLoadout();
        float loadoutHeight = loadoutRows.Count > 0 ? (loadoutRows.Count + 1) / 2 * LoadoutRowHeight + 4f : 0f;
        // No selection but a focused strategic point draws the same card shape as a single unit
        // without a loadout — see DrawFocusedPointCard.
        float selectionBarHeight = (selectionService.SelectedUnits.Count > 1
            ? 112f
            : selectionService.SelectedUnits.Count == 1
                ? 74f + loadoutHeight
                : 74f) + 44f;
        selectionBarRect = new Rect(
            Mathf.Max(12f, (CommanderUiScale.Width - 680f) * 0.5f),
            CommanderUiScale.Height - 84f - selectionBarHeight,
            Mathf.Min(680f, CommanderUiScale.Width - 24f),
            selectionBarHeight);
        selectionHelpRect = new Rect(selectionBarRect.x, selectionBarRect.y - 138f, selectionBarRect.width, 130f);
        if (showUnitListUi)
        {
            unitListUi.Tick();
        }
        aiLogUi.Tick();
        if (CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            supplyHeliUi.Tick();
            airCommandUi.Tick();
            depotUi.Tick();
        }
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screenPoint);
        bool advanced = CommanderFeatureGate.AdvancedFeaturesEnabled;
        if (screenshotUiHidden)
        {
            return false;
        }
        if (airCommandUi.Visible)
        {
            return (advanced && showAirCommandUi && airCommandUi.ContainsScreenPoint(screenPoint))
                || (settingsVisible && settingsWindowRect.Contains(guiPoint));
        }
        return CommanderAlertUi.Instance?.ContainsScreenPoint(screenPoint) == true
            || launcherRect.Contains(guiPoint)
            || (advanced && showFactionMoney
                && (moneyRect.Contains(guiPoint) || enemyPlanRect.Contains(guiPoint) || playerPlanRect.Contains(guiPoint)))
            || (panelVisible && panelRect.Contains(guiPoint))
            || (advanced && reserveWindowVisible && reserveWindowRect.Contains(guiPoint))
            || (showSelectionBar
                && (selectionService.SelectedUnits.Count > 0 || CommanderStrategicPointService.Instance?.FocusedPoint != null)
                && selectionBarRect.Contains(guiPoint))
            || (selectionHelpVisible && selectionHelpRect.Contains(guiPoint))
            || (showPinnedUnits && HasPinEntries && (pinnedLauncherRect.Contains(guiPoint) || (pinnedWindowVisible && pinnedWindowRect.Contains(guiPoint))))
            || (advanced && showUnitSystems && TryGetUnitSystemsTarget(out _, out _) && radarWindowRect.Contains(guiPoint))
            || (showUnitListUi && unitListUi.ContainsScreenPoint(screenPoint))
            || aiLogUi.ContainsScreenPoint(screenPoint)
            || (advanced && showDepotUi && depotUi.ContainsScreenPoint(screenPoint))
            || (advanced && showSupplyUi && supplyHeliUi.ContainsScreenPoint(screenPoint))
            || (advanced && showAirCommandUi && airCommandUi.ContainsScreenPoint(screenPoint))
            || (advanced && showNavalUi && navalPurchaseUi.ContainsScreenPoint(screenPoint))
            || (advanced && showSamAnalyzerUi && samSiteAnalyzerUi.ContainsScreenPoint(screenPoint))
            || (advanced && showBuildUi && economyUi.ContainsScreenPoint(screenPoint))
            || (settingsVisible && settingsWindowRect.Contains(guiPoint));
    }

    internal void DrawInactiveLauncher(Action activateCommander)
    {
        CommanderUiTheme.Ensure();
        float centerY = CommanderUiScale.Height * 0.5f;
        launcherRect = new Rect(10f, centerY - 42f, 52f, 84f);
        EventType activationEvent = Event.current.type;
        if (GUI.Button(launcherRect, "CMD", CommanderUiTheme.PrimaryButton)
            && activationEvent == EventType.MouseUp)
        {
            GUI.FocusControl(null);
            activateCommander();
            panelVisible = true;
        }
    }

    internal void Draw()
    {
        if (screenshotUiHidden)
        {
            return;
        }
        CommanderUiTheme.Ensure();
        bool advanced = CommanderFeatureGate.AdvancedFeaturesEnabled;
        if (showWorldMarkers)
        {
            worldMarkerRenderer.Draw(supplyHeliUi.Visible && supplyHeliUi.ShowLz);
        }
        if (advanced && airCommandUi.Visible)
        {
            if (showAirCommandUi) airCommandUi.Draw();
            DrawSettingsWindowIfVisible();
            return;
        }
        GUIStyle commandStyle = showCommandButton
            ? (panelVisible ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton)
            : GetGhostCommandStyle();
        if (GUI.Button(launcherRect, "CMD", commandStyle))
        {
            panelVisible = !panelVisible;
        }

        if (advanced && showFactionMoney)
        {
            GUI.Box(moneyRect, $"FACTION FUNDS   {spawnService.GetFactionFundsLabel()}", CommanderUiTheme.Money);
            // The enemy plan is shown openly on purpose: countering it is the game, and a plan
            // you cannot see is a plan you cannot answer.
            string enemyStatus = CommanderEnemyCommanderService.Instance?.StatusLine ?? string.Empty;
            if (enemyStatus.Length > 0)
            {
                GUI.Box(enemyPlanRect, $"ENEMY   {enemyStatus}", CommanderUiTheme.Money);
            }

            // Only ever non-empty while the player commander switch is on, so this row appears and
            // disappears with the switch without the overlay having to read the setting itself.
            string playerStatus = CommanderEnemyCommanderService.Instance?.PlayerStatusLine ?? string.Empty;
            if (playerStatus.Length > 0)
            {
                GUI.Box(playerPlanRect, $"YOU   {playerStatus}", CommanderUiTheme.Money);
            }
        }

        if (panelVisible)
        {
            panelRect = GUI.Window(WindowId, panelRect, DrawPanelWindow, "COMMANDER", CommanderUiTheme.Window);
        }
        if (advanced && reserveWindowVisible)
        {
            reserveWindowRect = GUI.Window(ReserveWindowId, reserveWindowRect, DrawReserveWindow, "FACTION RESERVE", CommanderUiTheme.Window);
        }

        if (showPinnedUnits && HasPinEntries)
        {
            if (GUI.Button(pinnedLauncherRect, pinnedWindowVisible ? "PINS <" : "PINS >", CommanderUiTheme.Button))
            {
                pinnedWindowVisible = !pinnedWindowVisible;
            }
            if (pinnedWindowVisible)
            {
                pinnedWindowRect = GUI.Window(PinnedWindowId, pinnedWindowRect, DrawPinnedWindow, "UNIT LIST", CommanderUiTheme.Window);
            }
        }
        if (advanced && showUnitSystems && TryGetUnitSystemsTarget(out _, out _))
        {
            string title = samSiteService.IsConstructionCore(selectionService.FocusedSelection)
                ? "SAM SITE LOGISTICS"
                : "UNIT SYSTEMS";
            radarWindowRect = GUI.Window(
                RadarWindowId,
                radarWindowRect,
                DrawRadarWindow,
                title,
                CommanderUiTheme.Window);
        }

        if (showUnitListUi) unitListUi.Draw();
        aiLogUi.Draw();
        if (advanced && showDepotUi) depotUi.Draw();
        if (advanced && showSupplyUi) supplyHeliUi.Draw();
        if (advanced && showAirCommandUi) airCommandUi.Draw();
        if (advanced && showNavalUi) navalPurchaseUi.Draw();
        if (advanced && showSamAnalyzerUi) samSiteAnalyzerUi.Draw();
        if (advanced && showBuildUi) economyUi.Draw();
        if (showSelectionBar) DrawSelectionBar();
        DrawSettingsWindowIfVisible();
    }

    private bool screenshotUiHidden => screenshotUiStage != 0;
    internal bool CommanderUiHidden => screenshotUiHidden;
    internal bool ShowTacticalMapUi => CommanderFeatureGate.AdvancedFeaturesEnabled
        && (showTacticalMap || airCommandUi.Visible)
        && !screenshotUiHidden;
    internal void ToggleScreenshotUi()
    {
        if (screenshotUiStage == 0)
        {
            screenshotUiStage = 1;
            reopenTacticalMapAfterScreenshot = CommanderTacticalMapService.Instance?.IsOpen == true;
            if (reopenTacticalMapAfterScreenshot)
            {
                CommanderTacticalMapService.Instance?.Close();
            }
            return;
        }

        if (screenshotUiStage == 1)
        {
            screenshotUiStage = 2;
            MaintainAllUiHidden();
            return;
        }

        RestoreBaseUi();
        screenshotUiStage = 0;
        if (reopenTacticalMapAfterScreenshot && showTacticalMap)
        {
            CommanderTacticalMapService.Instance?.Open();
        }
        reopenTacticalMapAfterScreenshot = false;
    }

    private void MaintainAllUiHidden()
    {
        Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
        for (int i = 0; i < canvases.Length; i++)
        {
            Canvas canvas = canvases[i];
            if (!screenshotCanvasStates.ContainsKey(canvas))
            {
                screenshotCanvasStates.Add(canvas, canvas.enabled);
            }
            canvas.enabled = false;
        }
    }

    private void RestoreBaseUi()
    {
        foreach (KeyValuePair<Canvas, bool> entry in screenshotCanvasStates)
        {
            if (entry.Key != null)
            {
                entry.Key.enabled = entry.Value;
            }
        }
        screenshotCanvasStates.Clear();
    }

    private void ResetScreenshotUi()
    {
        RestoreBaseUi();
        screenshotUiStage = 0;
        reopenTacticalMapAfterScreenshot = false;
    }
    private bool HasPinEntries => selectionService.PinnedUnits.Count > 0
        || selectionService.MissionUnits.Count > 0
        || selectionService.SamSiteUnits.Count > 0;

}
