using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderDepotUi
{
    private const int WindowId = 0x434F4D44;
    private const int CategoriesPerRow = 5;

    private readonly CommanderSpawnService spawnService;
    private VehicleDepot? trackedDepot;
    private Rect windowRect;
    private Vector2 vehicleScroll;
    private Vector2 queueScroll;
    private string selectedCategory = "AAA";
    private bool reserveOnly;
    private bool visible;
    private bool positionInitialized;
    private bool queueCollapsed;
    private bool helpVisible;

    internal CommanderDepotUi(CommanderSpawnService spawnService)
    {
        this.spawnService = spawnService;
    }

    internal bool Visible => visible;

    internal void ResetPosition()
    {
        positionInitialized = false;
    }

    internal void Reset()
    {
        trackedDepot = null;
        visible = false;
        helpVisible = false;
    }

    internal void Tick()
    {
        VehicleDepot? selectedDepot = spawnService.SelectedDepot;
        if (!ReferenceEquals(selectedDepot, trackedDepot))
        {
            trackedDepot = selectedDepot;
            visible = selectedDepot != null;
            helpVisible = false;
        }
        else if (selectedDepot == null)
        {
            visible = false;
        }

        float width = Mathf.Min(510f, CommanderUiScale.Width - 24f);
        float height = Mathf.Min(720f, CommanderUiScale.Height - 24f);
        if (!positionInitialized)
        {
            windowRect = new Rect(74f, Mathf.Max(12f, (CommanderUiScale.Height - height) * 0.5f), width, height);
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
        return visible && windowRect.Contains(guiPoint);
    }

    internal void Draw()
    {
        if (!visible || spawnService.SelectedDepot == null)
        {
            return;
        }

        CommanderUiTheme.Ensure();
        windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "DEPOT CONTROL", CommanderUiTheme.Window);
        windowRect = CommanderUiTheme.ClampWindow(windowRect);
    }

    private void DrawWindow(int windowId)
    {
        if (CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible))
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 34f, windowRect.width - 24f, 92f),
                "Click vehicles to stage them in order; click staged entries to remove them. SPAWN transfers the list to the expandable depot queue, while CLEAR empties it. Reserve Only lists retained factory output. Rally can be set from map or 3D and is applied after the Basegame depot exit order. REINFORCE GROUP puts every unit this depot builds straight into that control group.");
        }
        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.DangerButton))
        {
            visible = false;
            return;
        }

        float y = helpVisible ? 136f : 34f;
        GUI.Label(new Rect(12f, y, windowRect.width - 24f, 22f), spawnService.GetSelectedDepotLabel(), CommanderUiTheme.Header);
        y += 24f;
        GUI.Label(new Rect(12f, y, windowRect.width - 24f, 20f), $"Rally: {spawnService.GetRallyLabel()}", CommanderUiTheme.MutedLabel);
        y += 26f;

        float rallyButtonWidth = (windowRect.width - 32f) * 0.5f;
        if (GUI.Button(new Rect(12f, y, rallyButtonWidth, 32f), "SET RALLY", CommanderUiTheme.Button))
        {
            spawnService.BeginRallyPointSelection();
        }
        if (GUI.Button(new Rect(20f + rallyButtonWidth, y, rallyButtonWidth, 32f), "CLEAR RALLY", CommanderUiTheme.Button))
        {
            spawnService.ClearRallyPoint();
        }
        y += 36f;

        int reinforceGroup = spawnService.GetReinforceGroup();
        if (GUI.Button(new Rect(12f, y, windowRect.width - 24f, 30f),
            reinforceGroup == 0 ? "REINFORCE GROUP: OFF" : $"REINFORCE GROUP: {reinforceGroup}",
            reinforceGroup == 0 ? CommanderUiTheme.Button : CommanderUiTheme.PrimaryButton))
        {
            spawnService.CycleReinforceGroup();
        }
        y += 38f;

        y = DrawCategories(y);
        y += 8f;

        float queueHeight = queueCollapsed ? 30f : 142f;
        float statusHeight = string.IsNullOrWhiteSpace(spawnService.StatusText) ? 0f : 34f;
        float vehicleHeight = Mathf.Max(96f, windowRect.height - y - queueHeight - statusHeight - 68f);
        DrawVehicleList(y, vehicleHeight);
        y += vehicleHeight + 8f;

        if (GUI.Button(new Rect(12f, y, windowRect.width - 24f, 36f), "SPAWN", CommanderUiTheme.PrimaryButton))
        {
            spawnService.CommitSpawnList();
        }
        y += 44f;
        DrawQueue(y);

        if (!string.IsNullOrWhiteSpace(spawnService.StatusText))
        {
            GUI.Label(new Rect(12f, windowRect.height - 42f, windowRect.width - 24f, 28f), spawnService.StatusText, CommanderUiTheme.MutedLabel);
        }

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
    }

    private float DrawCategories(float y)
    {
        GUI.Label(new Rect(12f, y, 160f, 20f), "CATEGORY", CommanderUiTheme.MutedLabel);
        y += 22f;

        string[] categories = spawnService.BuildCategories();
        bool categoryExists = false;
        for (int i = 0; i < categories.Length; i++)
        {
            if (string.Equals(categories[i], selectedCategory, StringComparison.Ordinal))
            {
                categoryExists = true;
                break;
            }
        }
        if (!categoryExists)
        {
            selectedCategory = "All";
        }

        float gap = 5f;
        float buttonWidth = (windowRect.width - 24f - gap * (CategoriesPerRow - 1)) / CategoriesPerRow;
        int itemIndex = 0;

        for (int i = 0; i < categories.Length; i++)
        {
            string category = categories[i];
            DrawCategoryButton(category, string.Equals(category, selectedCategory, StringComparison.Ordinal), ref itemIndex, ref y, buttonWidth, () => selectedCategory = category);
        }
        DrawCategoryButton("RESERVE ONLY", reserveOnly, ref itemIndex, ref y, buttonWidth, () => reserveOnly = !reserveOnly);

        return itemIndex % CategoriesPerRow == 0 ? y : y + 30f;
    }

    private void DrawCategoryButton(string label, bool active, ref int itemIndex, ref float y, float width, Action action)
    {
        int column = itemIndex % CategoriesPerRow;
        if (column == 0 && itemIndex > 0)
        {
            y += 30f;
        }

        float x = 12f + column * (width + 5f);
        GUIStyle style = active ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button;
        if (GUI.Button(new Rect(x, y, width, 26f), label, style))
        {
            action();
        }
        itemIndex++;
    }

    private void DrawVehicleList(float y, float height)
    {
        List<VehicleDefinition> vehicles = spawnService.GetFilteredVehicleDefinitions(selectedCategory, reserveOnly);
        Rect view = new(12f, y, windowRect.width - 24f, height);
        Rect inner = new(0f, 0f, view.width - 20f, Mathf.Max(height, vehicles.Count * 34f + 6f));
        vehicleScroll = GUI.BeginScrollView(view, vehicleScroll, inner);
        for (int i = 0; i < vehicles.Count; i++)
        {
            VehicleDefinition definition = vehicles[i];
            if (GUI.Button(new Rect(4f, 3f + i * 34f, inner.width - 8f, 30f), spawnService.GetVehicleSpawnLabel(definition), CommanderUiTheme.Button))
            {
                spawnService.AddVehicleToSpawnList(definition);
            }
        }
        GUI.EndScrollView();
    }

    private void DrawQueue(float y)
    {
        CommanderSpawnService.DepotSpawnQueue? queue = spawnService.GetSelectedQueue();
        string label = queueCollapsed ? "+  SPAWN QUEUE" : "-  SPAWN QUEUE";
        float clearWidth = 92f;
        if (GUI.Button(new Rect(12f, y, windowRect.width - 30f - clearWidth, 26f), label, CommanderUiTheme.Button))
        {
            queueCollapsed = !queueCollapsed;
        }
        if (GUI.Button(new Rect(windowRect.width - 12f - clearWidth, y, clearWidth, 26f), "CLEAR", CommanderUiTheme.Button))
        {
            spawnService.ClearSpawnList();
        }
        if (queueCollapsed)
        {
            return;
        }

        y += 30f;
        List<string> pending = spawnService.GetPendingSummaryLines();
        int stagedCount = queue?.StagedDefinitions.Count ?? 0;
        float viewHeight = 112f;
        float innerHeight = Mathf.Max(viewHeight, stagedCount * 30f + pending.Count * 20f + 28f);
        Rect view = new(12f, y, windowRect.width - 24f, viewHeight);
        Rect inner = new(0f, 0f, view.width - 20f, innerHeight);
        queueScroll = GUI.BeginScrollView(view, queueScroll, inner);
        float itemY = 2f;
        if (queue == null || stagedCount == 0)
        {
            GUI.Label(new Rect(5f, itemY, inner.width - 10f, 22f), "Queue is empty", CommanderUiTheme.MutedLabel);
            itemY += 24f;
        }
        else
        {
            for (int i = 0; i < queue.StagedDefinitions.Count; i++)
            {
                GUI.Label(new Rect(5f, itemY, inner.width - 74f, 26f), CommanderGameAccess.GetVehicleLabel(queue.StagedDefinitions[i]), CommanderUiTheme.Label);
                if (GUI.Button(new Rect(inner.width - 66f, itemY, 62f, 26f), "REMOVE", CommanderUiTheme.Button))
                {
                    spawnService.RemoveStagedVehicleAt(i);
                    break;
                }
                itemY += 30f;
            }
        }
        for (int i = 0; i < pending.Count; i++)
        {
            GUI.Label(new Rect(8f, itemY, inner.width - 12f, 20f), pending[i], CommanderUiTheme.MutedLabel);
            itemY += 20f;
        }
        GUI.EndScrollView();
    }
}
