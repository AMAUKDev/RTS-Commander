using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The BUILD window. Three tabs, because one column could not hold all of it: ECONOMY is the mine
/// and factory business, STRUCTURES is the encyclopedia catalogue, REPAIR is the damaged buildings
/// and the crews you pay to fix them.
/// </summary>
internal sealed class CommanderEconomyUi
{
    private const int WindowId = 0x434F4D42;
    private const float RowHeight = 44f;
    private const float FooterHeight = 62f;

    private enum Tab
    {
        Economy,
        Structures,
        Repair,
    }

    private readonly CommanderEconomyService service;
    private readonly CommanderRepairService repairService;
    private bool visible;
    private bool helpVisible;
    private bool positionInitialized;
    private Tab tab;
    private Rect windowRect;
    private Vector2 scroll;

    internal CommanderEconomyUi(CommanderEconomyService service, CommanderRepairService repairService)
    {
        this.service = service;
        this.repairService = repairService;
    }

    internal bool Visible => visible;

    internal void Toggle()
    {
        visible = !visible;
        helpVisible = false;
        EnsurePosition();
    }

    internal void Hide()
    {
        visible = false;
        helpVisible = false;
        service.CancelBuild();
    }

    internal void Draw()
    {
        if (!visible)
        {
            return;
        }

        EnsurePosition();
        windowRect = CommanderUiTheme.ClampWindow(windowRect);
        windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "BUILD", CommanderUiTheme.Window);
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        return visible && windowRect.Contains(CommanderUiScale.ScreenToGui(screenPoint));
    }

    internal void ResetPosition()
    {
        positionInitialized = false;
        EnsurePosition();
    }

    private void DrawWindow(int windowId)
    {
        if (CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible))
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 34f, windowRect.width - 24f, 150f),
                "ECONOMY builds gold mines and factories and upgrades both, three levels each. "
                    + "STRUCTURES sells every other building the game ships, priced off what the game "
                    + "itself thinks it is worth: a radar sees, a depot supplies, a hangar services "
                    + "aircraft. REPAIR hires a crew to drive out and rebuild a damaged building. "
                    + "Everything comes out of faction funds, and the enemy commander builds, upgrades "
                    + "and repairs under exactly the same rules, so all of it is worth bombing. A "
                    + "see-through preview follows the cursor while you site a building: green means "
                    + "the ground is clear, red means it is on a road or on top of something. Hold "
                    + "the repeat key while siting to stay in placement mode; right-click backs out. "
                    + "Anything you build can be clicked or boxed like a vehicle, and its panel has "
                    + "the upgrade and the demolition button.");
        }
        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.DangerButton))
        {
            Hide();
            return;
        }

        float y = helpVisible ? 194f : 38f;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float funds = hq != null ? hq.factionFunds : 0f;
        GUI.Label(
            new Rect(12f, y, windowRect.width - 24f, 22f),
            $"FUNDS {CommanderEconomyService.FundsLabel(funds)}"
                + $"   |   MINE INCOME +{CommanderEconomyService.FundsLabel(service.FriendlyIncomePerMinute)}/MIN",
            CommanderUiTheme.Header);
        y += 28f;

        y = DrawTabs(y);

        switch (tab)
        {
            case Tab.Structures:
                DrawStructures(y, funds);
                break;
            case Tab.Repair:
                DrawRepair(y, funds);
                break;
            default:
                DrawEconomy(y, funds);
                break;
        }

        string status = service.PlacementStatus;
        if (tab == Tab.Repair && !string.IsNullOrWhiteSpace(repairService.StatusText))
        {
            status = repairService.StatusText;
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            GUI.Label(
                new Rect(14f, windowRect.height - 56f, windowRect.width - 28f, 40f),
                status,
                CommanderUiTheme.MutedLabel);
        }

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
    }

    private float DrawTabs(float y)
    {
        float width = (windowRect.width - 24f) / 3f;
        DrawTab(new Rect(12f, y, width - 4f, 26f), Tab.Economy, "ECONOMY");
        DrawTab(new Rect(12f + width, y, width - 4f, 26f), Tab.Structures, "STRUCTURES");
        DrawTab(new Rect(12f + width * 2f, y, width - 4f, 26f), Tab.Repair,
            repairService.DamagedBuildings.Count > 0
                ? $"REPAIR ({repairService.DamagedBuildings.Count})"
                : "REPAIR");
        return y + 34f;
    }

    private void DrawTab(Rect rect, Tab value, string label)
    {
        if (GUI.Button(rect, label, tab == value ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button)
            && tab != value)
        {
            tab = value;
            scroll = Vector2.zero;
            service.CancelBuild();
        }
    }

    private void DrawEconomy(float y, float funds)
    {
        DrawBuildButton(
            new Rect(12f, y, windowRect.width - 24f, 36f),
            CommanderBuildKind.Mine,
            $"BUILD GOLD MINE   {CommanderEconomyService.MineBuildCost:0}",
            funds);
        y += 42f;

        VehicleDefinition? production = service.SelectedProduction;
        bool oldEnabled = GUI.enabled;
        GUI.Label(new Rect(12f, y + 4f, 78f, 22f), "PRODUCES", CommanderUiTheme.MutedLabel);
        GUI.enabled = oldEnabled && production != null;
        if (GUI.Button(new Rect(92f, y, 28f, 28f), "<", CommanderUiTheme.Button))
        {
            service.CycleProduction(-1);
        }
        GUI.Label(
            new Rect(126f, y + 4f, windowRect.width - 168f, 22f),
            production != null ? CommanderGameAccess.GetVehicleLabel(production) : "Nothing available",
            CommanderUiTheme.Label);
        if (GUI.Button(new Rect(windowRect.width - 40f, y, 28f, 28f), ">", CommanderUiTheme.Button))
        {
            service.CycleProduction(1);
        }
        GUI.enabled = oldEnabled;
        y += 34f;

        DrawBuildButton(
            new Rect(12f, y, windowRect.width - 24f, 36f),
            CommanderBuildKind.Factory,
            $"BUILD FACTORY   {CommanderEconomyService.FactoryBuildCost:0}"
                + $"   1 / {CommanderEconomyService.FactoryProductionSeconds:0}s",
            funds);
        y += 42f;

        DrawBuildButton(
            new Rect(12f, y, windowRect.width - 24f, 36f),
            CommanderBuildKind.NavalDock,
            $"BUILD NAVAL DOCK   {CommanderEconomyService.NavalDockBuildCost:0}",
            funds);
        y += 44f;

        int rows = service.Mines.Count + service.Factories.Count + service.NavalDocks.Count + 3;
        Rect viewRect = BeginList(y, rows);
        float rowY = 4f;

        rowY = DrawSection(viewRect, rowY, "GOLD MINES", service.Mines.Count, "None built.");
        for (int i = 0; i < service.Mines.Count; i++)
        {
            Unit mine = service.Mines[i];
            int level = service.GetMineLevel(mine);
            DrawUpgradeRow(
                new Rect(4f, rowY, viewRect.width - 8f, RowHeight - 6f),
                $"GOLD MINE   LVL {level}/{CommanderEconomyService.MaxLevel}   "
                    + $"+{CommanderEconomyService.FundsLabel(CommanderEconomyService.GetMineIncomePerMinute(level))}/min",
                level,
                CommanderEconomyService.GetMineUpgradeCost(level),
                funds,
                () => service.UpgradeMine(mine));
            rowY += RowHeight;
        }

        rowY = DrawSection(viewRect, rowY, "FACTORIES", service.Factories.Count, "No friendly factories.");
        for (int i = 0; i < service.Factories.Count; i++)
        {
            Factory factory = service.Factories[i];
            int level = service.GetFactoryLevel(factory);
            string produces = factory.ProductionUnit != null ? factory.ProductionUnit.code : "?";
            DrawUpgradeRow(
                new Rect(4f, rowY, viewRect.width - 8f, RowHeight - 6f),
                $"{produces} FACTORY   LVL {level}/{CommanderEconomyService.MaxLevel}   {level}/cycle",
                level,
                CommanderEconomyService.GetFactoryUpgradeCost(level),
                funds,
                () => service.UpgradeFactory(factory));
            rowY += RowHeight;
        }

        rowY = DrawSection(
            viewRect,
            rowY,
            "NAVAL DOCKS",
            service.NavalDocks.Count,
            "None built. A dock has to stand on the shore; without one no ships can be bought.");
        for (int i = 0; i < service.NavalDocks.Count; i++)
        {
            Unit dock = service.NavalDocks[i];
            int level = service.GetNavalDockLevel(dock);
            DrawUpgradeRow(
                new Rect(4f, rowY, viewRect.width - 8f, RowHeight - 6f),
                $"NAVAL DOCK   LVL {level}/{CommanderEconomyService.MaxLevel}   "
                    + CommanderNavalPurchaseService.GetLevelUnlockLabel(level),
                level,
                CommanderEconomyService.GetNavalDockUpgradeCost(level),
                funds,
                () => service.UpgradeNavalDock(dock));
            rowY += RowHeight;
        }

        GUI.EndScrollView();
    }

    /// <summary>
    /// The catalogue, grouped by the category the game itself files each building under. Rows are
    /// drawn straight from the definition list, so a game patch that adds a building adds a row.
    /// </summary>
    private void DrawStructures(float y, float funds)
    {
        System.Collections.Generic.IReadOnlyList<BuildingDefinition> catalog = service.Catalog;
        if (catalog.Count == 0)
        {
            GUI.Label(
                new Rect(12f, y + 6f, windowRect.width - 24f, 40f),
                "No buildings available. The encyclopedia has not loaded yet.",
                CommanderUiTheme.MutedLabel);
            return;
        }

        // One extra row per category header; the list is sorted by category, so counting the
        // changes is the same walk the drawing loop makes.
        int headers = 1;
        for (int i = 1; i < catalog.Count; i++)
        {
            if (catalog[i].buildingType != catalog[i - 1].buildingType)
            {
                headers++;
            }
        }

        Rect viewRect = BeginList(y, catalog.Count + headers);
        float rowY = 4f;
        BuildingType? section = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            BuildingDefinition definition = catalog[i];
            if (section != definition.buildingType)
            {
                section = definition.buildingType;
                GUI.Label(
                    new Rect(8f, rowY, viewRect.width - 16f, 20f),
                    CommanderEconomyService.GetCategoryLabel(definition.buildingType),
                    CommanderUiTheme.MutedLabel);
                rowY += RowHeight;
            }

            DrawStructureRow(new Rect(4f, rowY, viewRect.width - 8f, RowHeight - 6f), definition, funds);
            rowY += RowHeight;
        }

        GUI.EndScrollView();
    }

    private void DrawStructureRow(Rect rect, BuildingDefinition definition, float funds)
    {
        float cost = CommanderEconomyService.GetStructureCost(definition);
        bool pending = service.PendingBuild == CommanderBuildKind.Structure
            && ReferenceEquals(service.PendingStructure, definition);
        const float buttonWidth = 118f;

        GUI.Label(
            new Rect(rect.x + 6f, rect.y + 8f, rect.width - buttonWidth - 16f, 22f),
            CommanderEconomyService.GetStructureLabel(definition),
            CommanderUiTheme.Label);

        Rect buttonRect = new(rect.xMax - buttonWidth, rect.y + 3f, buttonWidth - 4f, rect.height - 6f);
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && (pending || funds >= cost);
        if (GUI.Button(
            buttonRect,
            pending ? "CANCEL" : $"BUILD {cost:0}",
            pending ? CommanderUiTheme.DangerButton : CommanderUiTheme.Button))
        {
            if (pending)
            {
                service.CancelBuild();
            }
            else
            {
                service.BeginStructureBuild(definition);
            }
        }
        GUI.enabled = oldEnabled;
    }

    private void DrawRepair(float y, float funds)
    {
        GUI.Label(
            new Rect(12f, y, windowRect.width - 24f, 22f),
            $"REPAIR CREW   {CommanderRepairService.CrewCost:0} PER DISPATCH",
            CommanderUiTheme.MutedLabel);
        y += 28f;

        System.Collections.Generic.IReadOnlyList<Unit> damaged = repairService.DamagedBuildings;
        Rect viewRect = BeginList(y, Mathf.Max(1, damaged.Count));
        float rowY = 4f;
        if (damaged.Count == 0)
        {
            GUI.Label(
                new Rect(12f, rowY + 4f, viewRect.width - 24f, 20f),
                "Nothing of yours needs repair.",
                CommanderUiTheme.MutedLabel);
        }

        for (int i = 0; i < damaged.Count; i++)
        {
            Unit building = damaged[i];
            DrawRepairRow(new Rect(4f, rowY, viewRect.width - 8f, RowHeight - 6f), building, funds);
            rowY += RowHeight;
        }

        GUI.EndScrollView();
    }

    private void DrawRepairRow(Rect rect, Unit building, float funds)
    {
        const float buttonWidth = 128f;
        float condition = CommanderGameAccess.GetUnitCondition(building) * 100f;
        GUI.Label(
            new Rect(rect.x + 6f, rect.y + 8f, rect.width - buttonWidth - 16f, 22f),
            $"{CommanderGameAccess.GetUnitLabel(building)}   {condition:0}%",
            CommanderUiTheme.Label);

        Rect buttonRect = new(rect.xMax - buttonWidth, rect.y + 3f, buttonWidth - 4f, rect.height - 6f);
        if (repairService.HasCrewAssigned(building))
        {
            GUI.Label(buttonRect, "CREW EN ROUTE", CommanderUiTheme.MutedLabel);
            return;
        }

        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && funds >= CommanderRepairService.CrewCost;
        if (GUI.Button(buttonRect, "SEND CREW", CommanderUiTheme.PrimaryButton))
        {
            repairService.SendRepairCrew(building);
        }
        GUI.enabled = oldEnabled;
    }

    private Rect BeginList(float y, int rows)
    {
        Rect listRect = new(12f, y, windowRect.width - 24f, windowRect.height - y - FooterHeight);
        GUI.Box(listRect, string.Empty, CommanderUiTheme.Panel);
        float contentHeight = Mathf.Max(listRect.height - 8f, rows * RowHeight + 8f);
        Rect viewRect = new(0f, 0f, listRect.width - 22f, contentHeight);
        scroll = GUI.BeginScrollView(listRect, scroll, viewRect);
        return viewRect;
    }

    private static float DrawSection(Rect viewRect, float rowY, string title, int count, string emptyText)
    {
        GUI.Label(new Rect(8f, rowY, viewRect.width - 16f, 20f), title, CommanderUiTheme.MutedLabel);
        rowY += RowHeight;
        if (count == 0)
        {
            GUI.Label(new Rect(12f, rowY - 18f, viewRect.width - 24f, 20f), emptyText, CommanderUiTheme.MutedLabel);
        }

        return rowY;
    }

    private void DrawBuildButton(Rect rect, CommanderBuildKind kind, string label, float funds)
    {
        bool pending = service.PendingBuild == kind;
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && (pending || funds >= CommanderEconomyService.GetBuildCost(kind));
        if (GUI.Button(
            rect,
            pending ? "CANCEL PLACEMENT" : label,
            pending ? CommanderUiTheme.DangerButton : CommanderUiTheme.PrimaryButton))
        {
            if (pending)
            {
                service.CancelBuild();
            }
            else
            {
                service.BeginBuild(kind);
            }
        }
        GUI.enabled = oldEnabled;
    }

    private static void DrawUpgradeRow(
        Rect rect,
        string label,
        int level,
        float cost,
        float funds,
        System.Action upgrade)
    {
        float buttonWidth = 118f;
        GUI.Label(new Rect(rect.x + 6f, rect.y + 8f, rect.width - buttonWidth - 16f, 22f), label, CommanderUiTheme.Label);
        Rect buttonRect = new(rect.xMax - buttonWidth, rect.y + 3f, buttonWidth - 4f, rect.height - 6f);
        if (level >= CommanderEconomyService.MaxLevel)
        {
            GUI.Label(buttonRect, "MAX", CommanderUiTheme.MutedLabel);
            return;
        }

        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && funds >= cost;
        if (GUI.Button(buttonRect, $"UPGRADE {cost:0}", CommanderUiTheme.Button))
        {
            upgrade();
        }
        GUI.enabled = oldEnabled;
    }

    private void EnsurePosition()
    {
        if (positionInitialized)
        {
            return;
        }

        float width = Mathf.Min(520f, CommanderUiScale.Width - 24f);
        float height = Mathf.Min(620f, CommanderUiScale.Height - 24f);
        windowRect = new Rect(
            74f,
            Mathf.Max(12f, (CommanderUiScale.Height - height) * 0.5f),
            width,
            height);
        positionInitialized = true;
    }
}
