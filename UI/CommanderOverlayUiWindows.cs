using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderOverlayUi
{
    /// <summary>The building whose DESTROY button is armed, so a stray click cannot level a mine.</summary>
    private Unit? demolishArmedUnit;

    private void DrawPinnedWindow(int windowId)
    {
        bool hasManualPins = selectionService.PinnedUnits.Count > 0;
        bool hasMissions = selectionService.MissionUnits.Count > 0;
        bool hasSamSites = selectionService.SamSiteUnits.Count > 0;
        if ((pinnedTab == 0 && !hasManualPins)
            || (pinnedTab == 1 && !hasMissions)
            || (pinnedTab == 2 && !hasSamSites))
        {
            pinnedTab = hasMissions ? 1 : hasSamSites ? 2 : 0;
        }

        CommanderUiTheme.DrawHelpButton(pinnedWindowRect.width, ref pinnedHelpVisible);
        float y = pinnedHelpVisible ? 106f : 36f;
        if (pinnedHelpVisible)
        {
            CommanderUiTheme.DrawHelpOverlay(new Rect(12f, 34f, pinnedWindowRect.width - 24f, 62f),
                "PINS contains manual pins. MISSIONS tracks Supply and Air Command aircraft. SAM SITES tracks active site cores. Click to select; X removes only the list entry, not the unit.");
        }

        int tabCount = (hasManualPins ? 1 : 0) + (hasMissions ? 1 : 0) + (hasSamSites ? 1 : 0);
        float tabWidth = (pinnedWindowRect.width - 24f - Mathf.Max(0, tabCount - 1) * 6f) / Mathf.Max(1, tabCount);
        float tabX = 12f;
        if (hasManualPins && GUI.Button(new Rect(tabX, y, tabWidth, 30f), "PINS",
            pinnedTab == 0 ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            pinnedTab = 0;
            pinnedScroll = Vector2.zero;
        }
        if (hasManualPins) tabX += tabWidth + 6f;
        if (hasMissions && GUI.Button(new Rect(tabX, y, tabWidth, 30f), "MISSIONS",
            pinnedTab == 1 ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            pinnedTab = 1;
            pinnedScroll = Vector2.zero;
        }
        if (hasMissions) tabX += tabWidth + 6f;
        if (hasSamSites && GUI.Button(new Rect(tabX, y, tabWidth, 30f), "SAM SITES",
            pinnedTab == 2 ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            pinnedTab = 2;
            pinnedScroll = Vector2.zero;
        }
        y += 38f;

        if (pinnedTab == 1)
        {
            float filterWidth = (pinnedWindowRect.width - 30f) * 0.5f;
            showSupplyMissions = GUI.Toggle(new Rect(12f, y, filterWidth, 26f),
                showSupplyMissions, "SUPPLY", CommanderUiTheme.Toggle);
            showAirCommandMissions = GUI.Toggle(new Rect(18f + filterWidth, y, filterWidth, 26f),
                showAirCommandMissions, "AIR COMMAND", CommanderUiTheme.Toggle);
            y += 32f;
        }

        List<Unit> visibleUnits = new();
        IReadOnlyList<Unit> source = pinnedTab == 1
            ? selectionService.MissionUnits
            : pinnedTab == 2
                ? selectionService.SamSiteUnits
                : selectionService.PinnedUnits;
        for (int i = 0; i < source.Count; i++)
        {
            Unit unit = source[i];
            if (pinnedTab != 1)
            {
                visibleUnits.Add(unit);
                continue;
            }
            CommanderSelectionService.MissionPinInfo info = selectionService.GetMissionInfo(unit);
            if ((showSupplyMissions && info.Source == "SUPPLY")
                || (showAirCommandMissions && info.Source == "AIR COMMAND"))
            {
                visibleUnits.Add(unit);
            }
        }

        Rect view = new(10f, y, pinnedWindowRect.width - 20f, pinnedWindowRect.height - y - 12f);
        float rowHeight = pinnedTab == 1 ? 58f : 40f;
        Rect inner = new(0f, 0f, view.width - 18f, Mathf.Max(view.height, visibleUnits.Count * rowHeight + 4f));
        pinnedScroll = GUI.BeginScrollView(view, pinnedScroll, inner);
        for (int i = 0; i < visibleUnits.Count; i++)
        {
            Unit unit = visibleUnits[i];
            float rowY = 2f + i * rowHeight;
            if (GUI.Button(new Rect(4f, rowY, inner.width - 44f, rowHeight - 6f), string.Empty,
                pinnedTab == 1 ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                selectionService.SelectPinnedUnit(unit);
            }
            string unitLabel = pinnedTab == 2
                ? selectionService.GetSamSiteLabel(unit)
                : CommanderGameAccess.GetUnitLabel(unit);
            GUI.Label(new Rect(12f, rowY + 5f, inner.width - 64f, 22f), unitLabel, CommanderUiTheme.Header);
            if (pinnedTab == 1)
            {
                CommanderSelectionService.MissionPinInfo info = selectionService.GetMissionInfo(unit);
                GUI.Label(new Rect(12f, rowY + 28f, inner.width - 64f, 20f),
                    $"{info.Source}  |  {info.Mission}", CommanderUiTheme.MutedLabel);
            }
            if (GUI.Button(new Rect(inner.width - 36f, rowY, 32f, rowHeight - 6f), "X", CommanderUiTheme.DangerButton))
            {
                selectionService.RemovePinnedUnit(unit);
                break;
            }
        }
        GUI.EndScrollView();
        GUI.DragWindow(new Rect(0f, 0f, pinnedWindowRect.width - 44f, 28f));
    }

    private void DrawRadarWindow(int windowId)
    {
        if (!TryGetUnitSystemsTarget(out Unit focusedUnit, out CommanderRadarService.RadarState? state))
        {
            return;
        }

        CommanderUiTheme.DrawHelpButton(radarWindowRect.width, ref radarHelpVisible);
        if (radarHelpVisible)
        {
            CommanderUiTheme.DrawHelpOverlay(new Rect(10f, 32f, radarWindowRect.width - 20f, 108f),
                samSiteService.IsConstructionCore(focusedUnit)
                    ? "Build defenses from stored supply. Logistics routes from nearby airbases are planned once from terrain and faction influence, then reused. Show Route displays the selected cached route. Automatic deliveries prefer safer viable routes."
                    : state?.IsCommandTruck == true
                    ? "Counts cover the fire-control network around this command truck. Radar controls affect only the selected unit's local emitter. Enemy-unit controls are disabled."
                    : mobileEmplacementService.IsMoveableTrailer(focusedUnit)
                        ? "Relocate this static trailer with an idle HLT/MSV Tractor or Flatbed within 300 m. The hauler is reserved during loading, travel and deployment."
                    : repairService.IsRepairUnit(focusedUnit)
                        ? "Basegame repair targeting weighs damage, structure value and distance. NEAREST REPAIR instead targets the closest damaged friendly repairable structure on each Basegame repair scan."
                    : focusedUnit is Ship
                        ? "Request a paid Basegame UH-90K naval-supply run for this ship. Purchased airframes are refunded after a successful return. Enemy ships cannot request supply."
                    : CommanderEconomyService.IsCommanderBuilt(focusedUnit)
                        ? "A building this commander put down. Upgrade it here or from the BUILD window; DESTROY levels it with no refund and asks for a second click first."
                    : "Switch the selected unit's local radar emissions. Aircraft use the Basegame networked radar toggle; enemy-unit controls are disabled.");
        }
        float y = radarHelpVisible ? 146f : 38f;
        bool friendly = CommanderGameAccess.IsFriendlyUnit(focusedUnit, CommanderGameAccess.GetLocalHq());
        if (!friendly)
        {
            GUI.Label(new Rect(12f, y, radarWindowRect.width - 24f, 24f), "ENEMY UNIT  |  CONTROLS UNAVAILABLE", CommanderUiTheme.MutedLabel);
            y += 30f;
        }
        if (state?.IsCommandTruck == true)
        {
            GUI.Label(new Rect(12f, y, radarWindowRect.width - 24f, 22f),
                $"NEARBY  {state.NearbyRadarCount} RADAR   /   {state.NearbyLauncherCount} LAUNCHERS", CommanderUiTheme.Header);
            y += 30f;
        }
        bool oldEnabled = GUI.enabled;
        if (samSiteService.IsConstructionCore(focusedUnit))
        {
            y = DrawSamSiteLogistics(focusedUnit, friendly, oldEnabled, y);
        }

        if (state != null)
        {
            GUI.enabled = oldEnabled && friendly && state.HasRadar;
            if (GUI.Button(new Rect(12f, y, 126f, 34f),
                state.HasRadar ? (state.IsRadarOnline ? "RDR ONLINE" : "RDR OFFLINE") : "NO LOCAL RDR",
                state.IsRadarOnline ? CommanderUiTheme.SelectedButton : CommanderUiTheme.DangerButton))
            {
                radarService.ToggleRadar();
            }
            GUI.enabled = oldEnabled;
            GUI.Label(new Rect(148f, y, radarWindowRect.width - 160f, 34f), radarService.StatusText, CommanderUiTheme.MutedLabel);
            y += 42f;
        }

        if (friendly && (state?.HasRadar == true || samSiteService.IsConstructionCore(focusedUnit)))
        {
            GlobalPosition coveragePosition = focusedUnit.GlobalPosition();
            if (samSiteService.TryGetConstructionRadarPosition(
                focusedUnit,
                out GlobalPosition siteRadarPosition))
            {
                coveragePosition = siteRadarPosition;
            }
            bool matches = samSiteAnalyzerService.CoverageMatches(focusedUnit);
            bool building = matches && samSiteAnalyzerService.CoverageOverlayBuilding;
            string coverageLabel = building
                ? $"GENERATING  {samSiteAnalyzerService.CoverageOverlayProgress:P0}"
                : matches && samSiteAnalyzerService.CoverageOverlayReady
                    ? "SHOW RADAR COVERAGE"
                    : "GENERATE RADAR COVERAGE";
            GUI.enabled = oldEnabled && !building;
            if (GUI.Button(
                new Rect(12f, y, radarWindowRect.width - 24f, 36f),
                coverageLabel,
                matches && samSiteAnalyzerService.CoverageOverlayReady
                    ? CommanderUiTheme.SelectedButton
                    : CommanderUiTheme.PrimaryButton))
            {
                if (matches && samSiteAnalyzerService.CoverageOverlayReady)
                {
                    CommanderTacticalMapService.Instance?.ShowCoverageFullscreen();
                }
                else
                {
                    samSiteAnalyzerService.GenerateCoverageOverlay(focusedUnit, coveragePosition);
                }
            }
            GUI.enabled = oldEnabled;
            y += 42f;
            if (matches)
            {
                float altitude = samSiteAnalyzerService.CoverageTargetAltitude;
                GUI.Label(
                    new Rect(12f, y, radarWindowRect.width - 24f, 24f),
                    $"TARGET ALTITUDE  {altitude:0} m AGL",
                    CommanderUiTheme.MutedLabel);
                float selectedAltitude = GUI.HorizontalSlider(
                    new Rect(12f, y + 26f, radarWindowRect.width - 24f, 22f),
                    altitude,
                    0f,
                    2000f);
                samSiteAnalyzerService.SetCoverageTargetAltitude(selectedAltitude);
                y += 52f;
            }
            if (building)
            {
                GUI.HorizontalSlider(
                    new Rect(12f, y, radarWindowRect.width - 24f, 16f),
                    samSiteAnalyzerService.CoverageOverlayProgress,
                    0f,
                    1f);
                y += 20f;
            }
        }

        if (repairService.IsRepairUnit(focusedUnit))
        {
            GUI.enabled = oldEnabled && friendly;
            bool nearest = repairService.UsesNearestTarget(focusedUnit);
            if (GUI.Button(
                new Rect(12f, y, radarWindowRect.width - 24f, 36f),
                nearest ? "REPAIR: NEAREST" : "REPAIR: PRIORITY",
                nearest ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                repairService.ToggleNearestTarget(focusedUnit);
            }
            GUI.enabled = oldEnabled;
            y += 40f;
            GUI.Label(new Rect(12f, y, radarWindowRect.width - 24f, 38f), repairService.StatusText, CommanderUiTheme.MutedLabel);
            y += 42f;
        }

        if (CommanderEconomyService.IsCommanderBuilt(focusedUnit))
        {
            y = DrawStructureControls(focusedUnit, friendly, oldEnabled, y);
        }

        if (focusedUnit is Ship ship)
        {
            GUI.enabled = oldEnabled && friendly;
            if (GUI.Button(new Rect(12f, y, radarWindowRect.width - 24f, 36f),
                supplyHeliService.GetNavalSupplyButtonLabel(ship), CommanderUiTheme.PrimaryButton))
            {
                supplyHeliService.RequestNavalSupply(ship);
            }
            GUI.enabled = oldEnabled;
            y += 40f;
            GUI.Label(new Rect(12f, y, radarWindowRect.width - 24f, 38f), supplyHeliService.StatusText, CommanderUiTheme.MutedLabel);
        }
        else if (mobileEmplacementService.IsMoveableTrailer(focusedUnit))
        {
            const string relocationRequirement = "Idle Tractor or Flatbed required within 300 m.";
            bool relocating = mobileEmplacementService.IsRelocating(focusedUnit);
            bool haulerAvailable = mobileEmplacementService.HasAvailableHauler(focusedUnit);
            Rect relocateButtonRect = new(12f, y, radarWindowRect.width - 24f, 36f);
            GUI.enabled = oldEnabled && friendly && !relocating && haulerAvailable;
            if (GUI.Button(relocateButtonRect,
                new GUIContent(relocating ? "RELOCATION ACTIVE" : "RELOCATE TRAILER", relocationRequirement),
                CommanderUiTheme.PrimaryButton))
            {
                mobileEmplacementService.BeginRelocation();
            }
            GUI.enabled = oldEnabled;
            y += 42f;
            GUI.Label(new Rect(12f, y, radarWindowRect.width - 24f, 42f), mobileEmplacementService.StatusText, CommanderUiTheme.MutedLabel);
            if (relocateButtonRect.Contains(Event.current.mousePosition))
            {
                Rect tooltipRect = new(12f, Mathf.Max(34f, relocateButtonRect.y - 48f), radarWindowRect.width - 24f, 42f);
                GUI.Box(tooltipRect, string.Empty, CommanderUiTheme.Panel);
                GUI.Label(new Rect(tooltipRect.x + 8f, tooltipRect.y + 5f, tooltipRect.width - 16f, tooltipRect.height - 10f),
                    relocationRequirement, CommanderUiTheme.Label);
            }
        }
        GUI.DragWindow(new Rect(0f, 0f, radarWindowRect.width - 44f, 28f));
    }

    private float DrawSamSiteLogistics(
        Unit focusedUnit,
        bool friendly,
        bool oldEnabled,
        float y)
    {
        if (!ReferenceEquals(siteUiTarget, focusedUnit))
        {
            siteUiTarget = focusedUnit;
            siteAirbaseDropdownOpen = false;
            siteThresholdDropdownOpen = false;
        }

        float contentWidth = radarWindowRect.width - 24f;
        GUI.Label(
            new Rect(12f, y, contentWidth, 26f),
            $"{samSiteService.GetConstructionSiteSupply(focusedUnit)}"
            + $"     QUEUE  {samSiteService.GetConstructionQueueCount(focusedUnit)}",
            CommanderUiTheme.Header);
        y += 34f;

        float buttonWidth = (contentWidth - 8f) / 3f;
        GUI.enabled = oldEnabled
            && friendly
            && samSiteService.CanQueueConstruction(
                focusedUnit,
                CommanderSamSiteService.SiteBuildType.SamBattery);
        if (GUI.Button(
            new Rect(12f, y, buttonWidth, 36f),
            "SAM 40K",
            CommanderUiTheme.PrimaryButton))
        {
            samSiteService.QueueConstruction(
                focusedUnit,
                CommanderSamSiteService.SiteBuildType.SamBattery);
        }
        GUI.enabled = oldEnabled
            && friendly
            && samSiteService.CanQueueConstruction(
                focusedUnit,
                CommanderSamSiteService.SiteBuildType.Irm);
        if (GUI.Button(
            new Rect(16f + buttonWidth, y, buttonWidth, 36f),
            "IR 2K",
            CommanderUiTheme.Button))
        {
            samSiteService.QueueConstruction(
                focusedUnit,
                CommanderSamSiteService.SiteBuildType.Irm);
        }
        GUI.enabled = oldEnabled
            && friendly
            && samSiteService.CanQueueConstruction(
                focusedUnit,
                CommanderSamSiteService.SiteBuildType.Gun23mm);
        if (GUI.Button(
            new Rect(20f + buttonWidth * 2f, y, buttonWidth, 36f),
            "23MM 2K",
            CommanderUiTheme.Button))
        {
            samSiteService.QueueConstruction(
                focusedUnit,
                CommanderSamSiteService.SiteBuildType.Gun23mm);
        }
        GUI.enabled = oldEnabled;
        y += 48f;

        IReadOnlyList<CommanderSupplyHeliService.SamSiteAirbaseOption> airbases =
            samSiteService.GetConstructionSiteAirbases(focusedUnit);
        Airbase? selectedAirbase = samSiteService.GetConstructionSiteAirbase(focusedUnit);
        CommanderSupplyHeliService.SamSiteAirbaseOption? selectedOption = null;
        for (int i = 0; i < airbases.Count; i++)
        {
            if (ReferenceEquals(airbases[i].Airbase, selectedAirbase))
            {
                selectedOption = airbases[i];
                break;
            }
        }

        GUI.Label(new Rect(12f, y, contentWidth, 20f), "LOGISTICS AIRBASE", CommanderUiTheme.MutedLabel);
        y += 22f;
        string selectedLabel = selectedOption != null
            ? $"{selectedOption.Label}  ({selectedOption.Distance / 1000f:0.0} km)"
            : "NO COMPATIBLE AIRBASE";
        if (GUI.Button(
            new Rect(12f, y, contentWidth, 34f),
            selectedLabel,
            siteAirbaseDropdownOpen ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            siteAirbaseDropdownOpen = !siteAirbaseDropdownOpen;
            siteThresholdDropdownOpen = false;
        }
        y += 38f;

        if (siteAirbaseDropdownOpen)
        {
            for (int i = 0; i < airbases.Count; i++)
            {
                CommanderSupplyHeliService.SamSiteAirbaseOption option = airbases[i];
                string capability = option.SupportsSupply && option.SupportsJacknife
                    ? "SUPPLY + JACKNIFE"
                    : option.SupportsSupply ? "SUPPLY" : "JACKNIFE";
                string safety = option.Safe ? "SAFE" : "FORWARD";
                if (GUI.Button(
                    new Rect(12f, y, contentWidth, 30f),
                    $"{option.Label}  |  {option.Distance / 1000f:0.0} km  |  {capability}  |  {safety} {option.Risk:P0}",
                    ReferenceEquals(option.Airbase, selectedAirbase)
                        ? CommanderUiTheme.SelectedButton
                        : CommanderUiTheme.Button))
                {
                    samSiteService.SelectConstructionSiteAirbase(focusedUnit, i);
                    siteAirbaseDropdownOpen = false;
                }
                y += 32f;
            }
        }

        float halfWidth = (contentWidth - 6f) * 0.5f;
        bool automaticSupply = samSiteService.GetAutomaticSupplyEnabled(focusedUnit);
        GUI.enabled = oldEnabled && friendly;
        if (GUI.Button(
            new Rect(12f, y, halfWidth, 34f),
            automaticSupply ? "AUTO SUPPLY: ON" : "AUTO SUPPLY: OFF",
            automaticSupply ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            samSiteService.ToggleAutomaticSupply(focusedUnit);
        }
        float threshold = samSiteService.GetAutomaticSupplyThreshold(focusedUnit);
        if (GUI.Button(
            new Rect(18f + halfWidth, y, halfWidth, 34f),
            $"BELOW {threshold:0}",
            siteThresholdDropdownOpen ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            siteThresholdDropdownOpen = !siteThresholdDropdownOpen;
            siteAirbaseDropdownOpen = false;
        }
        GUI.enabled = oldEnabled;
        y += 38f;

        bool customRoute = samSiteService.GetConstructionCustomRouteEnabled(focusedUnit);
        bool routeVisible = samSiteService.IsConstructionSupplyRouteVisible(focusedUnit);
        GUI.enabled = oldEnabled && friendly;
        if (GUI.Button(
            new Rect(12f, y, halfWidth, 32f),
            customRoute ? "CUSTOM ROUTE: ON" : "CUSTOM ROUTE: OFF",
            customRoute ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            samSiteService.ToggleConstructionCustomRoute(focusedUnit);
        }
        GUI.enabled = oldEnabled
            && friendly
            && customRoute
            && samSiteService.CanShowConstructionSupplyRoute(focusedUnit);
        if (GUI.Button(
            new Rect(18f + halfWidth, y, halfWidth, 32f),
            routeVisible ? "HIDE SUPPLY ROUTE" : "SHOW SUPPLY ROUTE",
            routeVisible ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            samSiteService.ToggleConstructionSupplyRoute(focusedUnit);
        }
        GUI.enabled = oldEnabled;
        y += 36f;

        if (siteThresholdDropdownOpen)
        {
            float optionWidth = contentWidth / CommanderSamSiteService.SupplyThresholdOptions.Length;
            for (int i = 0; i < CommanderSamSiteService.SupplyThresholdOptions.Length; i++)
            {
                float option = CommanderSamSiteService.SupplyThresholdOptions[i];
                if (GUI.Button(
                    new Rect(12f + optionWidth * i, y, optionWidth - 2f, 30f),
                    option >= 1000f ? $"{option / 1000f:0}K" : $"{option:0}",
                    Mathf.Approximately(option, threshold)
                        ? CommanderUiTheme.SelectedButton
                        : CommanderUiTheme.Button))
                {
                    samSiteService.SetAutomaticSupplyThreshold(focusedUnit, option);
                    siteThresholdDropdownOpen = false;
                }
            }
            y += 34f;
        }

        GUI.enabled = oldEnabled
            && friendly
            && samSiteService.CanRequestConstructionSupply(focusedUnit);
        if (GUI.Button(
            new Rect(12f, y, contentWidth, 36f),
            "REQUEST SUPPLY",
            CommanderUiTheme.PrimaryButton))
        {
            samSiteService.RequestConstructionSupply(focusedUnit);
        }
        GUI.enabled = oldEnabled;
        y += 46f;

        int incomingJacknifes = samSiteService.GetIncomingConstructionJacknifes(focusedUnit);
        string incomingLabel = incomingJacknifes > 0 ? $"  +{incomingJacknifes} INBOUND" : string.Empty;
        GUI.Label(
            new Rect(12f, y, halfWidth, 34f),
            $"JACKNIFE  {samSiteService.GetConstructionSiteJacknifes(focusedUnit)}/2{incomingLabel}",
            CommanderUiTheme.Header);
        GUI.enabled = oldEnabled
            && friendly
            && samSiteService.CanRequestConstructionJacknife(focusedUnit);
        if (GUI.Button(
            new Rect(18f + halfWidth, y, halfWidth, 34f),
            "REQUEST JACKNIFE",
            CommanderUiTheme.Button))
        {
            samSiteService.RequestConstructionJacknife(focusedUnit);
        }
        GUI.enabled = oldEnabled;
        y += 44f;

        GUI.Label(
            new Rect(12f, y, contentWidth, 42f),
            samSiteService.GetConstructionJacknifeStatus(focusedUnit),
            CommanderUiTheme.Label);
        y += 44f;

        GUI.Label(
            new Rect(12f, y, contentWidth, 70f),
            samSiteService.GetConstructionSiteStatus(focusedUnit),
            CommanderUiTheme.MutedLabel);
        return y + 74f;
    }

    /// <summary>
    /// Level and demolition for a building this commander put down. Upgrading here is the same
    /// call the BUILD window makes; DESTROY arms on the first click and fires on the second,
    /// because there is no undo and no refund.
    /// </summary>
    private float DrawStructureControls(Unit building, bool friendly, bool oldEnabled, float y)
    {
        float width = radarWindowRect.width - 24f;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float funds = hq != null ? hq.factionFunds : 0f;

        if (economyService.IsBuiltMine(building))
        {
            int level = economyService.GetMineLevel(building);
            float cost = CommanderEconomyService.GetMineUpgradeCost(level);
            GUI.Label(
                new Rect(12f, y, width, 22f),
                $"GOLD MINE   LVL {level}/{CommanderEconomyService.MaxLevel}   "
                    + $"+{CommanderEconomyService.FundsLabel(CommanderEconomyService.GetMineIncomePerMinute(level))}/min",
                CommanderUiTheme.Header);
            y += 26f;
            y = DrawUpgradeButton(width, y, level, cost, funds, friendly, oldEnabled,
                () => economyService.UpgradeMine(building));
        }
        else if (economyService.IsBuiltNavalDock(building))
        {
            int level = economyService.GetNavalDockLevel(building);
            float cost = CommanderEconomyService.GetNavalDockUpgradeCost(level);
            GUI.Label(
                new Rect(12f, y, width, 22f),
                $"NAVAL DOCK   LVL {level}/{CommanderEconomyService.MaxLevel}   "
                    + CommanderNavalPurchaseService.GetLevelUnlockLabel(level).ToUpperInvariant(),
                CommanderUiTheme.Header);
            y += 26f;
            y = DrawUpgradeButton(width, y, level, cost, funds, friendly, oldEnabled,
                () => economyService.UpgradeNavalDock(building));
        }
        else if (building.TryGetComponent(out Factory factory) && factory.ProductionUnit != null)
        {
            int level = economyService.GetFactoryLevel(factory);
            float cost = CommanderEconomyService.GetFactoryUpgradeCost(level);
            GUI.Label(
                new Rect(12f, y, width, 22f),
                $"{factory.ProductionUnit.code} FACTORY   LVL {level}/{CommanderEconomyService.MaxLevel}"
                    + $"   {level} PER RUN",
                CommanderUiTheme.Header);
            y += 24f;
            // "1/cycle" said nothing without knowing how long a cycle is, so the interval and the
            // time left on the current one are spelled out here rather than left to the log.
            GUI.Label(
                new Rect(12f, y, width, 20f),
                $"NEXT {level} x {factory.ProductionUnit.code} IN {FormatCountdown(factory)}"
                    + $"   (EVERY {FormatDuration(factory.ProductionInterval)})",
                CommanderUiTheme.MutedLabel);
            y += 24f;
            y = DrawUpgradeButton(width, y, level, cost, funds, friendly, oldEnabled,
                () => economyService.UpgradeFactory(factory));
        }

        bool armed = ReferenceEquals(demolishArmedUnit, building);
        GUI.enabled = oldEnabled && friendly;
        if (GUI.Button(
            new Rect(12f, y, width, 36f),
            armed ? "CONFIRM DEMOLITION" : "DESTROY BUILDING",
            CommanderUiTheme.DangerButton))
        {
            if (armed)
            {
                economyService.Demolish(building);
                demolishArmedUnit = null;
            }
            else
            {
                demolishArmedUnit = building;
            }
        }
        GUI.enabled = oldEnabled;
        y += 40f;

        GUI.Label(
            new Rect(12f, y, width, 38f),
            armed ? "No refund. Click again to level it." : economyService.StatusText,
            CommanderUiTheme.MutedLabel);
        return y + 42f;
    }

    /// <summary>
    /// Time left on the factory's current production run. <c>Factory.GetNextProduction(true)</c> is
    /// the game's own answer, off the same <c>lastProductionTime</c> SyncVar the production
    /// SlowUpdate writes, so this stays right on a client as well as on the host.
    /// </summary>
    private static string FormatCountdown(Factory factory)
    {
        if (factory.ProductionInterval <= 0f)
        {
            return "--:--";
        }

        return FormatDuration(Mathf.Max(factory.GetNextProduction(true), 0f));
    }

    private static string FormatDuration(float seconds)
    {
        int total = Mathf.Max(Mathf.RoundToInt(seconds), 0);
        return $"{total / 60}:{total % 60:00}";
    }

    private static float DrawUpgradeButton(
        float width,
        float y,
        int level,
        float cost,
        float funds,
        bool friendly,
        bool oldEnabled,
        Action upgrade)
    {
        if (level >= CommanderEconomyService.MaxLevel)
        {
            GUI.Label(new Rect(12f, y, width, 24f), "FULLY UPGRADED", CommanderUiTheme.MutedLabel);
            return y + 30f;
        }

        GUI.enabled = oldEnabled && friendly && funds >= cost;
        if (GUI.Button(new Rect(12f, y, width, 36f), $"UPGRADE   {cost:0}", CommanderUiTheme.PrimaryButton))
        {
            upgrade();
        }
        GUI.enabled = oldEnabled;
        return y + 42f;
    }

    private bool TryGetUnitSystemsTarget(out Unit unit, out CommanderRadarService.RadarState? state)
    {
        unit = selectionService.FocusedSelection!;
        state = null;
        if (unit == null || unit.disabled)
        {
            return false;
        }

        if (radarService.TryGetFocusedState(out CommanderRadarService.RadarState radarState)
            && ReferenceEquals(radarState.Unit, unit))
        {
            state = radarState;
        }

        return state != null
            || unit is Ship
            || repairService.IsRepairUnit(unit)
            || mobileEmplacementService.IsMoveableTrailer(unit)
            || samSiteService.IsConstructionCore(unit)
            || CommanderEconomyService.IsCommanderBuilt(unit);
    }

    private void DrawReserveWindow(int windowId)
    {
        if (CommanderUiTheme.DrawHelpButton(reserveWindowRect.width, ref reserveHelpVisible))
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 34f, reserveWindowRect.width - 24f, 78f),
                "Factory output is read directly from friendly Basegame factories. Category HOLD intercepts automatic deployment for that output category; Unit HOLD affects only one vehicle type. Counts show vehicles currently retained for manual depot spawning.");
        }
        if (GUI.Button(new Rect(reserveWindowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.DangerButton))
        {
            reserveWindowVisible = false;
            return;
        }

        float y = reserveHelpVisible ? 122f : 38f;
        GUI.Label(new Rect(12f, y, reserveWindowRect.width - 24f, 30f),
            $"FUNDS  {spawnService.GetFactionFundsLabel()}    |    VEHICLES IN RESERVE  {spawnService.GetProductionReserveTotal()}", CommanderUiTheme.Header);
        y += 38f;

        float modeWidth = (reserveWindowRect.width - 30f) * 0.5f;
        if (GUI.Button(new Rect(12f, y, modeWidth, 34f), "CATEGORIES",
            reserveShowsUnits ? CommanderUiTheme.Button : CommanderUiTheme.SelectedButton))
        {
            reserveShowsUnits = false;
            reserveScroll = Vector2.zero;
        }
        if (GUI.Button(new Rect(18f + modeWidth, y, modeWidth, 34f), "INDIVIDUAL UNITS",
            reserveShowsUnits ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            reserveShowsUnits = true;
            reserveScroll = Vector2.zero;
        }
        y += 44f;

        GUI.Label(new Rect(12f, y, reserveWindowRect.width - 24f, 22f),
            reserveShowsUnits ? "FACTORY OUTPUT BY UNIT" : "FACTORY OUTPUT BY CATEGORY", CommanderUiTheme.MutedLabel);
        y += 24f;
        Rect view = new(12f, y, reserveWindowRect.width - 24f, reserveWindowRect.height - y - 14f);
        if (reserveShowsUnits)
        {
            IReadOnlyList<VehicleDefinition> definitions = spawnService.GetProductionVehicleDefinitions();
            Rect inner = new(0f, 0f, view.width - 20f, Mathf.Max(view.height, definitions.Count * 40f + 6f));
            reserveScroll = GUI.BeginScrollView(view, reserveScroll, inner);
            for (int i = 0; i < definitions.Count; i++)
            {
                VehicleDefinition definition = definitions[i];
                string category = CommanderGameAccess.GetVehicleCategoryLabel(definition);
                bool categoryHeld = spawnService.IsCategoryHeld(category);
                bool individuallyHeld = spawnService.IsVehicleHeld(definition);
                Rect row = new(4f, 3f + i * 40f, inner.width - 8f, 36f);
                GUI.Box(row, string.Empty, CommanderUiTheme.Panel);

                if (categoryHeld)
                {
                    GUI.Label(new Rect(row.x + 8f, row.y + 8f, 104f, 20f), "CATEGORY HOLD", CommanderUiTheme.MutedLabel);
                }
                else
                {
                    bool updatedHeld = GUI.Toggle(new Rect(row.x + 8f, row.y + 7f, 64f, 22f), individuallyHeld, "HOLD", CommanderUiTheme.Toggle);
                    if (updatedHeld != individuallyHeld)
                    {
                        spawnService.ToggleHeldVehicle(definition);
                    }
                }

                GUI.Label(new Rect(row.x + 120f, row.y + 6f, row.width - 255f, 24f),
                    CommanderGameAccess.GetVehicleLabel(definition), CommanderUiTheme.Label);
                GUI.Label(new Rect(row.xMax - 126f, row.y + 6f, 118f, 24f),
                    $"RESERVE {spawnService.GetReserveCount(definition)}", CommanderUiTheme.MutedLabel);
            }
            GUI.EndScrollView();

            if (definitions.Count == 0)
            {
                GUI.Label(view, "No friendly vehicle factories are currently active.", CommanderUiTheme.Label);
            }
        }
        else
        {
            IReadOnlyList<string> categories = spawnService.GetProductionCategories();
            Rect inner = new(0f, 0f, view.width - 20f, Mathf.Max(view.height, categories.Count * 46f + 6f));
            reserveScroll = GUI.BeginScrollView(view, reserveScroll, inner);
            for (int i = 0; i < categories.Count; i++)
            {
                string category = categories[i];
                bool held = spawnService.IsCategoryHeld(category);
                Rect row = new(4f, 3f + i * 46f, inner.width - 8f, 42f);
                GUI.Box(row, string.Empty, CommanderUiTheme.Panel);
                bool updatedHeld = GUI.Toggle(new Rect(row.x + 10f, row.y + 10f, 64f, 22f), held, "HOLD", CommanderUiTheme.Toggle);
                if (updatedHeld != held)
                {
                    spawnService.ToggleHeldCategory(category);
                }
                GUI.Label(new Rect(row.x + 94f, row.y + 8f, row.width - 230f, 26f), category, CommanderUiTheme.Header);
                GUI.Label(new Rect(row.xMax - 126f, row.y + 8f, 118f, 26f),
                    $"RESERVE {spawnService.GetProductionCategoryReserveCount(category)}", CommanderUiTheme.MutedLabel);
            }
            GUI.EndScrollView();

            if (categories.Count == 0)
            {
                GUI.Label(view, "No friendly vehicle factories are currently active.", CommanderUiTheme.Label);
            }
        }
        GUI.DragWindow(new Rect(0f, 0f, reserveWindowRect.width - 72f, 28f));
    }
}
