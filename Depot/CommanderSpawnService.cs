using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderSpawnService : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive, ICommanderTickPersistent, ICommanderResetSession
{
    private const float DepotRefreshIntervalSeconds = 5f;
    private const float SpawnUpdateIntervalSeconds = 0.3f;
    private const float DepotSpawnDetectionRadius = 180f;
    private const float RallyExitToleranceMeters = 12f;
    private const float RallyFormationSpacingMeters = 25f;

    /// <summary>How far past the depot exit newly bought vehicles park when no rally point is set.</summary>
    private const float DefaultStagingDistanceMeters = 150f;
    private const float StatusDurationSeconds = 4f;

    private readonly CommanderSelectionService selectionService;
    private readonly CommanderFactionVehicleService factionVehicleService;
    private readonly CommanderTacticalMapService tacticalMapService;
    private readonly CommanderMapClickTracker rallyClickTracker = new();
    private readonly List<VehicleDepotEntry> depots = new();
    private readonly List<VehicleDefinition> vehicleDefinitions = new();
    private readonly List<VehicleDefinition> productionVehicleDefinitions = new();
    private readonly List<VehicleDefinition> factionVehicleDefinitions = new();
    private readonly List<VehicleDefinition> emptyVehicleDefinitions = new();
    private readonly List<VehicleDefinition> filteredVehicleDefinitions = new();
    private readonly Dictionary<string, List<VehicleDefinition>> definitionsByCategory = new(StringComparer.Ordinal);
    private readonly List<string> productionCategories = new();
    private readonly List<string> factionCategories = new();
    private readonly Dictionary<VehicleDepot, DepotSpawnQueue> depotQueues = new();
    private readonly Dictionary<Unit, Factory> productionFactories = new();
    private readonly List<Unit> friendlyUnitsScratch = new();
    private readonly List<VehicleDepot> queuesToRemove = new();

    private bool awaitingRallyPointSelection;
    private DepotSpawnQueue? rallySelectionQueue;
    private float nextDepotRefreshTime;
    private float nextSpawnUpdateTime;
    private FactionHQ? boundProductionHq;
    private bool productionCatalogDirty = true;
    private float statusUntil;
    private string statusText = string.Empty;
    private string[] categories = { "All" };

    internal static CommanderSpawnService? Instance { get; private set; }

    internal CommanderSpawnService(
        CommanderSelectionService selectionService,
        CommanderFactionVehicleService factionVehicleService,
        CommanderTacticalMapService tacticalMapService)
    {
        this.selectionService = selectionService;
        this.factionVehicleService = factionVehicleService;
        this.tacticalMapService = tacticalMapService;
        Instance = this;
    }

    internal VehicleDepot? SelectedDepot { get; private set; }
    internal bool AwaitingRallyPointSelection => awaitingRallyPointSelection;

    internal void CancelRallyPointSelection()
    {
        awaitingRallyPointSelection = false;
    }
    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;

    public void Activate()
    {
        awaitingRallyPointSelection = false;
        rallySelectionQueue = null;
        SelectedDepot = null;
        nextDepotRefreshTime = CommanderScheduler.Stagger("spawn.depots", DepotRefreshIntervalSeconds, 0.8f);
        nextSpawnUpdateTime = CommanderScheduler.Stagger("spawn.queues", SpawnUpdateIntervalSeconds);
        RefreshProductionBindings();
        statusText = string.Empty;
        productionCatalogDirty = true;
        if (vehicleDefinitions.Count == 0)
        {
            RefreshVehicleDefinitions();
        }

        RefreshDepots();
        SyncSelectedDepotFromSelection();
    }

    public void Deactivate()
    {
        UnbindProductionHq();
        awaitingRallyPointSelection = false;
        rallySelectionQueue = null;
        SelectedDepot = null;
        statusText = string.Empty;
        depots.Clear();
        rallyClickTracker.Reset();
    }

    public void ResetSession()
    {
        Deactivate();
        depotQueues.Clear();
        vehicleDefinitions.Clear();
        productionVehicleDefinitions.Clear();
        factionVehicleDefinitions.Clear();
        productionCategories.Clear();
        factionCategories.Clear();
        definitionsByCategory.Clear();
        categories = new[] { "All" };
        productionCatalogDirty = true;
    }

    public void TickActive()
    {
        if (CommanderScheduler.IsDue(ref nextDepotRefreshTime, DepotRefreshIntervalSeconds))
        {
            RefreshDepots();
            RefreshProductionBindings();
        }

        SyncSelectedDepotFromSelection();
        UpdateRallySelectionState();
        HandleRallyPointMapSelection();

    }

    public void TickPersistent()
    {
        if (CommanderScheduler.IsDue(ref nextSpawnUpdateTime, SpawnUpdateIntervalSeconds))
        {
            UpdateSpawnQueues();
        }
    }

    internal bool IsMapInteractionActive()
    {
        return DynamicMap.mapMaximized && !tacticalMapService.IsOpen;
    }

    internal void ToggleMap()
    {
        tacticalMapService.Toggle();
    }

    internal DepotSpawnQueue? GetSelectedQueue()
    {
        return SelectedDepot != null ? GetOrCreateQueue(SelectedDepot) : null;
    }

    internal bool TryGetSelectedRallyPoint(out GlobalPosition rallyPoint)
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue != null && queue.HasRallyPoint)
        {
            rallyPoint = queue.RallyPoint;
            return true;
        }

        rallyPoint = default;
        return false;
    }

    internal string GetSelectedDepotLabel()
    {
        VehicleDepotEntry? entry = GetSelectedDepotEntry();
        if (entry != null)
        {
            return entry.Label;
        }

        Unit? focusedUnit = selectionService.FocusedSelection;
        return focusedUnit != null ? CommanderGameAccess.GetUnitLabel(focusedUnit) : "None";
    }

    internal void SelectNearestDepot()
    {
        if (depots.Count == 0)
        {
            RefreshDepots();
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null || depots.Count == 0)
        {
            SetStatus("No friendly depot is available.");
            return;
        }

        VehicleDepotEntry? nearest = null;
        float nearestSqrDistance = float.MaxValue;
        Vector3 cameraPosition = camera.transform.position;
        for (int i = 0; i < depots.Count; i++)
        {
            VehicleDepotEntry entry = depots[i];
            float sqrDistance = (entry.Owner.transform.position - cameraPosition).sqrMagnitude;
            if (sqrDistance < nearestSqrDistance)
            {
                nearestSqrDistance = sqrDistance;
                nearest = entry;
            }
        }

        if (nearest != null)
        {
            selectionService.SelectUnit(nearest.Owner, false);
            SetStatus($"Selected nearest depot: {nearest.Label}");
        }
    }

    internal string[] BuildCategories()
    {
        if (vehicleDefinitions.Count == 0)
        {
            RefreshVehicleDefinitions();
        }

        if (!CommanderSettings.LimitToFactoryVehicles)
        {
            return categories;
        }

        RefreshFactionVehicleDefinitions();
        factionCategories.Clear();
        for (int i = 0; i < factionVehicleDefinitions.Count; i++)
        {
            string category = CommanderGameAccess.GetVehicleCategoryLabel(factionVehicleDefinitions[i]);
            if (!string.Equals(category, "Trailer", StringComparison.Ordinal)
                && !factionCategories.Contains(category))
            {
                factionCategories.Add(category);
            }
        }

        factionCategories.Sort(StringComparer.OrdinalIgnoreCase);
        factionCategories.Add("All");
        return factionCategories.ToArray();
    }

    internal List<VehicleDefinition> GetFilteredVehicleDefinitions(string selectedCategory, bool reserveOnly)
    {
        if (vehicleDefinitions.Count == 0)
        {
            RefreshVehicleDefinitions();
        }

        List<VehicleDefinition> source;
        if (string.IsNullOrWhiteSpace(selectedCategory)
            || string.Equals(selectedCategory, "All", StringComparison.Ordinal))
        {
            source = vehicleDefinitions;
        }
        else
        {
            if (!definitionsByCategory.TryGetValue(selectedCategory, out List<VehicleDefinition>? filtered))
            {
                return emptyVehicleDefinitions;
            }

            source = filtered;
        }

        bool factionOnly = CommanderSettings.LimitToFactoryVehicles;
        if (!reserveOnly && !factionOnly)
        {
            return source;
        }

        if (factionOnly)
        {
            RefreshFactionVehicleDefinitions();
        }

        filteredVehicleDefinitions.Clear();
        for (int i = 0; i < source.Count; i++)
        {
            VehicleDefinition definition = source[i];
            if ((!factionOnly || factionVehicleDefinitions.Contains(definition))
                && (!reserveOnly || factionVehicleService.GetReserveCount(definition) > 0))
            {
                filteredVehicleDefinitions.Add(definition);
            }
        }

        return filteredVehicleDefinitions;
    }

    internal bool IsCategoryHeld(string category)
    {
        return factionVehicleService.IsCategoryHeld(category);
    }

    internal void ToggleHeldCategory(string category)
    {
        factionVehicleService.ToggleCategory(category);
    }

    internal bool IsVehicleHeld(VehicleDefinition definition)
    {
        return factionVehicleService.IsDefinitionHeld(definition);
    }

    internal void ToggleHeldVehicle(VehicleDefinition definition)
    {
        factionVehicleService.ToggleDefinition(definition);
    }

    internal IReadOnlyList<VehicleDefinition> GetProductionVehicleDefinitions()
    {
        RefreshProductionVehicleDefinitions();
        return productionVehicleDefinitions;
    }

    internal IReadOnlyList<string> GetProductionCategories()
    {
        RefreshProductionVehicleDefinitions();
        return productionCategories;
    }

    internal int GetProductionReserveTotal()
    {
        RefreshProductionVehicleDefinitions();
        int total = 0;
        for (int i = 0; i < productionVehicleDefinitions.Count; i++)
        {
            total += factionVehicleService.GetReserveCount(productionVehicleDefinitions[i]);
        }

        return total;
    }

    internal int GetProductionCategoryReserveCount(string category)
    {
        RefreshProductionVehicleDefinitions();
        int total = 0;
        for (int i = 0; i < productionVehicleDefinitions.Count; i++)
        {
            VehicleDefinition definition = productionVehicleDefinitions[i];
            if (string.Equals(CommanderGameAccess.GetVehicleCategoryLabel(definition), category, StringComparison.Ordinal))
            {
                total += factionVehicleService.GetReserveCount(definition);
            }
        }

        return total;
    }

    internal string GetFactionFundsLabel()
    {
        return UnitConverter.ValueReading(factionVehicleService.FactionFunds) ?? factionVehicleService.FactionFunds.ToString("F1");
    }

    internal string GetVehicleSpawnLabel(VehicleDefinition definition)
    {
        int reserve = factionVehicleService.GetReserveCount(definition);
        string cost = UnitConverter.ValueReading(factionVehicleService.GetPurchaseCost(definition))
            ?? factionVehicleService.GetPurchaseCost(definition).ToString("F1");
        return $"{CommanderGameAccess.GetVehicleLabel(definition)}  |  Reserve {reserve}  |  {cost}";
    }

    internal int GetReserveCount(VehicleDefinition definition)
    {
        return factionVehicleService.GetReserveCount(definition);
    }

    internal void AddVehicleToSpawnList(VehicleDefinition definition)
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null)
        {
            SetStatus("Select a depot first.");
            return;
        }

        queue.StagedDefinitions.Add(definition);
        SetStatus($"Added {CommanderGameAccess.GetVehicleLabel(definition)} to spawn list.");
    }

    internal void RemoveStagedVehicleAt(int index)
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null || index < 0 || index >= queue.StagedDefinitions.Count)
        {
            return;
        }

        VehicleDefinition definition = queue.StagedDefinitions[index];
        queue.StagedDefinitions.RemoveAt(index);
        SetStatus($"Removed {CommanderGameAccess.GetVehicleLabel(definition)} from spawn list.");
    }

    internal void ClearSpawnList()
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null)
        {
            return;
        }

        queue.StagedDefinitions.Clear();
        SetStatus("Cleared spawn list.");
    }

    internal void CommitSpawnList()
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null)
        {
            SetStatus("Select a depot first.");
            return;
        }

        if (queue.StagedDefinitions.Count == 0)
        {
            SetStatus("Spawn list is empty.");
            return;
        }

        if (queue.PendingDefinitions.Count == 0
            && queue.ExpectedSpawnDefinitions.Count == 0
            && queue.PendingRallyUnits.Count == 0)
        {
            queue.NextRallySlot = 0;
        }

        int stagedCount = queue.StagedDefinitions.Count;
        for (int i = 0; i < queue.StagedDefinitions.Count; i++)
        {
            queue.PendingDefinitions.Enqueue(queue.StagedDefinitions[i]);
        }

        queue.PendingSummaryDirty = true;
        queue.StagedDefinitions.Clear();
        SetStatus($"Queued {stagedCount} unit{(stagedCount == 1 ? string.Empty : "s")} for spawning.");
    }

    internal void BeginRallyPointSelection()
    {
        if (SelectedDepot == null)
        {
            SetStatus("Select a depot first.");
            return;
        }

        if (!tacticalMapService.Open())
        {
            SetStatus("Could not open the map.");
            return;
        }

        awaitingRallyPointSelection = true;
        rallySelectionQueue = GetSelectedQueue();
        tacticalMapService.SuppressMapFollow = true;
        rallyClickTracker.Reset();
        SetStatus("Select a rally point on the tactical map or in the 3D world.");
    }

    internal void ClearRallyPoint()
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null)
        {
            return;
        }

        queue.HasRallyPoint = false;
        queue.PendingRallyUnits.Clear();
        queue.NextRallySlot = 0;
        ApplyDefaultRallyPoint(queue);
        SetStatus("Cleared rally point. New units stage beside the depot.");
    }

    internal bool TrySetRallyPointFromWorld(Vector2 screenPosition)
    {
        if (!awaitingRallyPointSelection || rallySelectionQueue == null)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition rallyPoint))
        {
            SetStatus("No valid rally position under the cursor.");
            return true;
        }

        CompleteRallyPointSelection(rallyPoint);
        return true;
    }

    /// <summary>Control group new arrivals from this depot join automatically; 0 is off.</summary>
    internal int GetReinforceGroup()
    {
        return GetSelectedQueue()?.ReinforceGroup ?? 0;
    }

    internal void CycleReinforceGroup()
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null)
        {
            SetStatus("Select a depot first.");
            return;
        }

        queue.ReinforceGroup = queue.ReinforceGroup >= CommanderGroupService.GroupCount
            ? 0
            : queue.ReinforceGroup + 1;
        SetStatus(queue.ReinforceGroup == 0
            ? "New units will not join a control group."
            : $"New units will join control group {queue.ReinforceGroup}.");
    }

    internal string GetRallyLabel()
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null || !queue.HasRallyPoint)
        {
            return "Not set";
        }

        return queue.IsDefaultRallyPoint ? "Staging beside depot" : queue.RallyPoint.ToString();
    }

    internal List<string> GetPendingSummaryLines()
    {
        DepotSpawnQueue? queue = GetSelectedQueue();
        if (queue == null)
        {
            return DepotSpawnQueue.EmptySummaryLines;
        }

        if (!queue.PendingSummaryDirty)
        {
            return queue.PendingSummaryLines;
        }

        queue.PendingSummaryCounts.Clear();
        queue.PendingSummaryLines.Clear();
        foreach (VehicleDefinition definition in queue.PendingDefinitions)
        {
            string label = CommanderGameAccess.GetVehicleLabel(definition);
            queue.PendingSummaryCounts.TryGetValue(label, out int count);
            queue.PendingSummaryCounts[label] = count + 1;
        }

        foreach (KeyValuePair<string, int> entry in queue.PendingSummaryCounts)
        {
            queue.PendingSummaryLines.Add($"{entry.Key} x{entry.Value}");
            if (queue.PendingSummaryLines.Count >= 6)
            {
                break;
            }
        }

        queue.PendingSummaryDirty = false;
        return queue.PendingSummaryLines;
    }

    private void UpdateRallySelectionState()
    {
        if (!awaitingRallyPointSelection)
        {
            return;
        }

        if (!DynamicMap.mapMaximized)
        {
            awaitingRallyPointSelection = false;
            rallySelectionQueue = null;
            tacticalMapService.SuppressMapFollow = false;
            rallyClickTracker.Reset();
            SetStatus("Rally point selection cancelled.");
        }
    }

    private void HandleRallyPointMapSelection()
    {
        if (!awaitingRallyPointSelection || !DynamicMap.mapMaximized)
        {
            return;
        }

        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null)
        {
            awaitingRallyPointSelection = false;
            SetStatus("Could not open the map.");
            return;
        }

        DepotSpawnQueue? queue = rallySelectionQueue;
        if (queue == null)
        {
            awaitingRallyPointSelection = false;
            rallySelectionQueue = null;
            SetStatus("No depot selected.");
            return;
        }

        if (!rallyClickTracker.Tick(dynamicMap, out GlobalPosition rallyPoint))
        {
            return;
        }

        CompleteRallyPointSelection(rallyPoint);
    }

    private void CompleteRallyPointSelection(GlobalPosition rallyPoint)
    {
        DepotSpawnQueue? queue = rallySelectionQueue;
        if (queue == null)
        {
            return;
        }

        queue.HasRallyPoint = true;
        queue.IsDefaultRallyPoint = false;
        queue.RallyPoint = rallyPoint;
        queue.NextRallySlot = 0;
        for (int i = 0; i < queue.PendingRallyUnits.Count; i++)
        {
            queue.PendingRallyUnits[i].RallySlot = queue.NextRallySlot++;
        }
        awaitingRallyPointSelection = false;
        rallySelectionQueue = null;
        tacticalMapService.SuppressMapFollow = false;
        rallyClickTracker.Reset();
        SetStatus("Rally point set.");
    }

    private void RefreshVehicleDefinitions()
    {
        vehicleDefinitions.Clear();
        CommanderGameAccess.TryGetLocalVehicleDefinitions(vehicleDefinitions);
        vehicleDefinitions.Sort(static (left, right) =>
        {
            int categoryCompare = string.Compare(
                CommanderGameAccess.GetVehicleCategoryLabel(left),
                CommanderGameAccess.GetVehicleCategoryLabel(right),
                StringComparison.OrdinalIgnoreCase);
            if (categoryCompare != 0)
            {
                return categoryCompare;
            }

            return string.Compare(
                CommanderGameAccess.GetVehicleLabel(left),
                CommanderGameAccess.GetVehicleLabel(right),
                StringComparison.OrdinalIgnoreCase);
        });

        RebuildVehicleCategoryCache();

    }

    private void RefreshDepots()
    {
        depots.Clear();

        VehicleDepot[] sceneDepots = UnityEngine.Object.FindObjectsOfType<VehicleDepot>();
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        for (int i = 0; i < sceneDepots.Length; i++)
        {
            VehicleDepot depot = sceneDepots[i];
            Unit? owner = CommanderGameAccess.GetDepotOwner(depot);
            if (owner == null || !CommanderGameAccess.IsFriendlyUnit(owner, localHq))
            {
                continue;
            }

            depots.Add(new VehicleDepotEntry(depot, owner, CommanderGameAccess.GetUnitLabel(owner)));
            GetOrCreateQueue(depot);
        }
    }

    private void RebuildVehicleCategoryCache()
    {
        definitionsByCategory.Clear();
        List<string> categoryNames = new();

        for (int i = 0; i < vehicleDefinitions.Count; i++)
        {
            VehicleDefinition definition = vehicleDefinitions[i];
            string category = CommanderGameAccess.GetVehicleCategoryLabel(definition);
            if (string.Equals(category, "Trailer", StringComparison.Ordinal))
            {
                continue;
            }
            if (!definitionsByCategory.TryGetValue(category, out List<VehicleDefinition> definitions))
            {
                definitions = new List<VehicleDefinition>();
                definitionsByCategory[category] = definitions;
                categoryNames.Add(category);
            }

            definitions.Add(definition);
        }

        categoryNames.Add("All");
        categories = categoryNames.ToArray();
    }

    private void RefreshProductionVehicleDefinitions()
    {
        RefreshProductionBindings();
        if (!productionCatalogDirty)
        {
            return;
        }

        productionCatalogDirty = false;
        productionVehicleDefinitions.Clear();
        foreach (Factory factory in productionFactories.Values)
        {
            if (factory == null
                || factory.attachedUnit == null
                || factory.attachedUnit.disabled
                || factory.attachedUnit.NetworkHQ != boundProductionHq
                || factory.ProductionUnit is not VehicleDefinition definition
                || !CommanderGameAccess.IsSpawnableVehicleDefinition(definition)
                || productionVehicleDefinitions.Contains(definition))
            {
                continue;
            }

            productionVehicleDefinitions.Add(definition);
        }
        productionVehicleDefinitions.Sort(static (left, right) =>
        {
            int categoryCompare = string.Compare(
                CommanderGameAccess.GetVehicleCategoryLabel(left),
                CommanderGameAccess.GetVehicleCategoryLabel(right),
                StringComparison.OrdinalIgnoreCase);
            return categoryCompare != 0
                ? categoryCompare
                : string.Compare(CommanderGameAccess.GetVehicleLabel(left), CommanderGameAccess.GetVehicleLabel(right), StringComparison.OrdinalIgnoreCase);
        });

        productionCategories.Clear();
        for (int i = 0; i < productionVehicleDefinitions.Count; i++)
        {
            string category = CommanderGameAccess.GetVehicleCategoryLabel(productionVehicleDefinitions[i]);
            if (!productionCategories.Contains(category))
            {
                productionCategories.Add(category);
            }
        }
    }

    internal static void NotifyFactoryChanged(Factory factory)
    {
        Instance?.OnFactoryChanged(factory);
    }

    private void RefreshProductionBindings()
    {
        FactionHQ? hq = CommanderGameAccess.GetSupplyHq();
        if (ReferenceEquals(hq, boundProductionHq))
        {
            return;
        }

        UnbindProductionHq();
        boundProductionHq = hq;
        if (boundProductionHq?.factionUnits == null)
        {
            return;
        }

        boundProductionHq.onRegisterUnit += OnProductionUnitRegistered;
        boundProductionHq.onRemoveUnit += OnProductionUnitRemoved;
        foreach (PersistentID unitId in boundProductionHq.factionUnits)
        {
            if (unitId.TryGetUnit(out Unit unit))
            {
                OnProductionUnitRegistered(unit);
            }
        }
    }

    private void UnbindProductionHq()
    {
        if (boundProductionHq != null)
        {
            boundProductionHq.onRegisterUnit -= OnProductionUnitRegistered;
            boundProductionHq.onRemoveUnit -= OnProductionUnitRemoved;
        }

        boundProductionHq = null;
        productionFactories.Clear();
        productionCatalogDirty = true;
    }

    private void OnProductionUnitRegistered(Unit unit)
    {
        if (unit == null || unit.disabled || unit.NetworkHQ != boundProductionHq || !unit.TryGetComponent(out Factory factory))
        {
            return;
        }

        productionFactories[unit] = factory;
        productionCatalogDirty = true;
    }

    private void OnProductionUnitRemoved(Unit unit)
    {
        if (productionFactories.Remove(unit))
        {
            productionCatalogDirty = true;
        }
    }

    private void OnFactoryChanged(Factory factory)
    {
        Unit? unit = factory.attachedUnit;
        if (unit == null || unit.NetworkHQ != boundProductionHq)
        {
            return;
        }

        productionFactories[unit] = factory;
        productionCatalogDirty = true;
    }

    private void RefreshFactionVehicleDefinitions()
    {
        CommanderGameAccess.CollectFactionVehicleDefinitions(factionVehicleDefinitions);
    }

    private void SyncSelectedDepotFromSelection()
    {
        Unit? focusedUnit = selectionService.FocusedSelection;
        VehicleDepot? resolvedDepot = null;

        if (focusedUnit != null)
        {
            for (int i = 0; i < depots.Count; i++)
            {
                VehicleDepotEntry entry = depots[i];
                if (ReferenceEquals(entry.Owner, focusedUnit))
                {
                    resolvedDepot = entry.Depot;
                    break;
                }
            }

            if (resolvedDepot == null)
            {
                resolvedDepot = CommanderGameAccess.GetFriendlyDepotFromUnit(focusedUnit, CommanderGameAccess.GetLocalHq());
            }
        }

        if (!ReferenceEquals(SelectedDepot, resolvedDepot))
        {
            SelectedDepot = resolvedDepot;
            if (SelectedDepot != null)
            {
                GetOrCreateQueue(SelectedDepot);
            }
        }
    }

    private void UpdateSpawnQueues()
    {
        queuesToRemove.Clear();
        foreach (KeyValuePair<VehicleDepot, DepotSpawnQueue> entry in depotQueues)
        {
            DepotSpawnQueue queue = entry.Value;
            if (queue.Depot == null || queue.Depot.disabled)
            {
                queuesToRemove.Add(entry.Key);
                continue;
            }

            TrackQueuedSpawnedUnits(queue);
            UpdatePendingRallyOrders(queue);

            if (queue.PendingDefinitions.Count == 0 || queue.ExpectedSpawnDefinitions.Count > 0)
            {
                continue;
            }

            VehicleDefinition definition = queue.PendingDefinitions.Peek();
            if (!factionVehicleService.CanAcquire(definition, out string reason))
            {
                SetStatus(reason);
                continue;
            }

            SnapshotKnownFriendlyUnits(queue);
            if (!queue.Depot.TrySpawnVehicle(definition))
            {
                continue;
            }

            factionVehicleService.CommitAcquisition(definition);
            queue.PendingDefinitions.Dequeue();
            queue.PendingSummaryDirty = true;
            queue.ExpectedSpawnDefinitions.Enqueue(definition);
        }

        for (int i = 0; i < queuesToRemove.Count; i++)
        {
            depotQueues.Remove(queuesToRemove[i]);
        }
    }

    private void TrackQueuedSpawnedUnits(DepotSpawnQueue queue)
    {
        if (queue.ExpectedSpawnDefinitions.Count == 0)
        {
            return;
        }

        Transform? spawnTransform = CommanderGameAccess.GetDepotSpawnTransform(queue.Depot);
        if (spawnTransform == null)
        {
            return;
        }

        CommanderGameAccess.CollectFriendlySurfaceUnits(friendlyUnitsScratch);
        VehicleDefinition expectedDefinition = queue.ExpectedSpawnDefinitions.Peek();
        for (int i = 0; i < friendlyUnitsScratch.Count; i++)
        {
            Unit unit = friendlyUnitsScratch[i];
            uint persistentId = unit.persistentID.Id;
            if (queue.SeenUnitIds.Contains(persistentId))
            {
                continue;
            }

            queue.SeenUnitIds.Add(persistentId);
            if (CommanderGameAccess.HorizontalDistance(unit.transform.position, spawnTransform.position) > DepotSpawnDetectionRadius
                || !MatchesDefinition(unit, expectedDefinition))
            {
                continue;
            }

            queue.ExpectedSpawnDefinitions.Dequeue();
            if (queue.ReinforceGroup > 0)
            {
                CommanderGroupService.Instance?.AddUnitToGroup(unit, queue.ReinforceGroup);
            }

            if (queue.HasRallyPoint)
            {
                queue.PendingRallyUnits.Add(new PendingRallyUnit(unit, queue.NextRallySlot++));
            }

            break;
        }
    }

    private void UpdatePendingRallyOrders(DepotSpawnQueue queue)
    {
        if (!queue.HasRallyPoint)
        {
            queue.PendingRallyUnits.Clear();
            return;
        }

        for (int i = queue.PendingRallyUnits.Count - 1; i >= 0; i--)
        {
            PendingRallyUnit pendingUnit = queue.PendingRallyUnits[i];
            Unit? unit = pendingUnit.Unit;
            if (unit == null || unit.disabled)
            {
                queue.PendingRallyUnits.RemoveAt(i);
                continue;
            }

            // A vehicle the player-side commander has claimed into a platoon belongs to the platoon.
            // This loop re-sends a pending vehicle to the depot rally point whenever its command
            // changes, and the platoon's form-up order is exactly such a change — so the two fought
            // over one vehicle and the platoon leader kept driving back toward the depot.
            if (CommanderOperationsService.IsPlatoonUnit(unit))
            {
                queue.PendingRallyUnits.RemoveAt(i);
                continue;
            }

            bool hasCurrentCommand = CommanderGameAccess.TryGetCurrentCommandPosition(unit, out GlobalPosition currentCommand);
            if (!pendingUnit.ExitCommandLocked)
            {
                if (!hasCurrentCommand)
                {
                    continue;
                }

                pendingUnit.ExitCommand = currentCommand;
                pendingUnit.ExitCommandLocked = true;
            }

            bool reachedExit = CommanderGameAccess.HorizontalDistance(
                unit.transform.position,
                pendingUnit.ExitCommand.ToLocalPosition()) <= RallyExitToleranceMeters;
            bool baseCommandChanged = hasCurrentCommand
                && !CommanderGameAccess.ApproximatelyEqual(currentCommand, pendingUnit.ExitCommand, RallyExitToleranceMeters);
            if (!reachedExit && !baseCommandChanged)
            {
                continue;
            }

            GlobalPosition rallyDestination = CommanderDestinationFormation.ApplyOffset(
                queue.RallyPoint,
                pendingUnit.RallySlot,
                RallyFormationSpacingMeters);
            if (!CommanderGameAccess.TrySetDestination(unit, rallyDestination))
            {
                continue;
            }

            queue.PendingRallyUnits.RemoveAt(i);
        }
    }

    private DepotSpawnQueue GetOrCreateQueue(VehicleDepot depot)
    {
        if (depotQueues.TryGetValue(depot, out DepotSpawnQueue queue))
        {
            return queue;
        }

        queue = new DepotSpawnQueue(depot);
        ApplyDefaultRallyPoint(queue);
        depotQueues[depot] = queue;
        SnapshotKnownFriendlyUnits(queue);
        return queue;
    }

    /// <summary>
    /// Parks a depot's output in a staging block beside the depot until the player orders it
    /// somewhere.
    /// </summary>
    /// <remarks>
    /// Left alone, a bought vehicle drives itself at the enemy. <c>GroundVehicle.CheckObstacles</c>
    /// re-targets any vehicle that is not holding position and has no <i>player</i> command onto the
    /// nearest mission objective or tracked enemy, and <c>VehicleDepot.TrySpawnVehicle</c>'s exit
    /// nudge is issued with <c>playerCommand: false</c> — so the unit is out of the gate and off to
    /// the front before the player has seen it. A default rally point is a real player command, which
    /// is what pins it: no auto-walk, no held-position anchoring, and the first move order the player
    /// gives simply overrides it. Setting a rally point of your own replaces this; clearing one puts
    /// it back rather than turning staging off.
    /// </remarks>
    private static void ApplyDefaultRallyPoint(DepotSpawnQueue queue)
    {
        Transform? spawnTransform = CommanderGameAccess.GetDepotSpawnTransform(queue.Depot);
        if (spawnTransform == null)
        {
            return;
        }

        queue.RallyPoint = (spawnTransform.position + spawnTransform.forward * DefaultStagingDistanceMeters)
            .ToGlobalPosition();
        queue.HasRallyPoint = true;
        queue.IsDefaultRallyPoint = true;
        queue.NextRallySlot = 0;
    }

    private void SnapshotKnownFriendlyUnits(DepotSpawnQueue queue)
    {
        CommanderGameAccess.CollectFriendlySurfaceUnits(friendlyUnitsScratch);
        for (int i = 0; i < friendlyUnitsScratch.Count; i++)
        {
            queue.SeenUnitIds.Add(friendlyUnitsScratch[i].persistentID.Id);
        }
    }

    private static bool MatchesDefinition(Unit unit, VehicleDefinition expectedDefinition)
    {
        if (unit.definition == expectedDefinition)
        {
            return true;
        }

        return unit.definition != null
            && string.Equals(unit.definition.code, expectedDefinition.code, StringComparison.OrdinalIgnoreCase)
            && string.Equals(unit.definition.unitName, expectedDefinition.unitName, StringComparison.OrdinalIgnoreCase);
    }

    private VehicleDepotEntry? GetSelectedDepotEntry()
    {
        if (SelectedDepot == null)
        {
            return null;
        }

        for (int i = 0; i < depots.Count; i++)
        {
            if (ReferenceEquals(depots[i].Depot, SelectedDepot))
            {
                return depots[i];
            }
        }

        return null;
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }

    private sealed class VehicleDepotEntry
    {
        internal VehicleDepotEntry(VehicleDepot depot, Unit owner, string label)
        {
            Depot = depot;
            Owner = owner;
            Label = label;
        }

        internal VehicleDepot Depot { get; }
        internal Unit Owner { get; }
        internal string Label { get; }
    }

    internal sealed class DepotSpawnQueue
    {
        internal static readonly List<string> EmptySummaryLines = new();

        internal DepotSpawnQueue(VehicleDepot depot)
        {
            Depot = depot;
        }

        internal VehicleDepot Depot { get; }
        internal Queue<VehicleDefinition> PendingDefinitions { get; } = new();
        internal Queue<VehicleDefinition> ExpectedSpawnDefinitions { get; } = new();
        internal List<VehicleDefinition> StagedDefinitions { get; } = new();
        internal HashSet<uint> SeenUnitIds { get; } = new();
        internal List<PendingRallyUnit> PendingRallyUnits { get; } = new();
        internal Dictionary<string, int> PendingSummaryCounts { get; } = new(StringComparer.Ordinal);
        internal List<string> PendingSummaryLines { get; } = new();
        internal bool PendingSummaryDirty { get; set; } = true;
        internal int ReinforceGroup { get; set; }
        internal bool HasRallyPoint { get; set; }

        /// <summary>True while the rally point is the automatic staging block, not one the player placed.</summary>
        internal bool IsDefaultRallyPoint { get; set; }
        internal GlobalPosition RallyPoint { get; set; }
        internal int NextRallySlot { get; set; }
    }

    internal sealed class PendingRallyUnit
    {
        internal PendingRallyUnit(Unit unit, int rallySlot)
        {
            Unit = unit;
            RallySlot = rallySlot;
        }

        internal Unit Unit { get; }
        internal int RallySlot { get; set; }
        internal GlobalPosition ExitCommand { get; set; }
        internal bool ExitCommandLocked { get; set; }
    }
}
