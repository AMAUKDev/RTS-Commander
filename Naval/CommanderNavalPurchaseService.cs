using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using RoadPathfinding;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderNavalPurchaseService : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive, ICommanderResetSession
{
    private const float MinimumEntryBandMeters = 2000f;
    private const float EntryBandMapFraction = 0.08f;
    private const float MaximumRallySnapMeters = 12000f;
    private const float StatusDurationSeconds = 7f;

    private readonly CommanderTacticalMapService tacticalMapService;
    private readonly CommanderMapClickTracker mapClickTracker = new();
    private readonly List<ShipDefinition> shipDefinitions = new();
    private readonly List<EntryCandidate> entryCandidates = new();

    private ShipDefinition? pendingDefinition;
    private int selectedIndex;
    private float statusUntil;
    private string statusText = string.Empty;
    private bool restoreTacticalMap;
    private Rect selectionBlockingRect;

    internal static CommanderNavalPurchaseService? Instance { get; private set; }

    internal CommanderNavalPurchaseService(CommanderTacticalMapService tacticalMapService)
    {
        this.tacticalMapService = tacticalMapService;
        Instance = this;
    }

    internal IReadOnlyList<ShipDefinition> ShipDefinitions => shipDefinitions;

    /// <summary>The dock level the local faction holds. Zero means no naval purchases at all.</summary>
    internal static int DockLevel => CommanderEconomyService.GetNavalDockLevel(CommanderGameAccess.GetLocalHq());

    /// <summary>
    /// The dock level a hull needs before anyone may buy it. This is the whole ladder, and it is
    /// keyed on <see cref="ShipType"/> rather than on a per-ship table so a patch that adds a hull
    /// files itself under the class it already belongs to instead of falling off the list.
    /// </summary>
    internal static int GetRequiredDockLevel(ShipDefinition? definition)
    {
        return definition == null ? CommanderEconomyService.MaxLevel : GetRequiredDockLevel(definition.shipType);
    }

    /// <summary>The ladder itself, in one place: the self-check below reads the same table the
    /// purchase gate does, or it would only ever be checking a copy of it.</summary>
    private static int GetRequiredDockLevel(ShipType type)
    {
        return type switch
        {
            // Small craft: what a quayside can service on day one.
            ShipType.PB or ShipType.LC => 1,
            // Escorts.
            ShipType.FFL or ShipType.FFG => 2,
            // Capital ships and amphibious warfare.
            _ => 3,
        };
    }

    internal static bool IsUnlocked(ShipDefinition? definition, int dockLevel)
    {
        return definition != null && dockLevel >= GetRequiredDockLevel(definition);
    }

    internal static string GetLevelUnlockLabel(int level)
    {
        return level switch
        {
            <= 0 => "nothing",
            1 => "patrol boats and landing craft",
            2 => "corvettes and frigates",
            _ => "destroyers, carriers and assault ships",
        };
    }

    /// <summary>
    /// Ship classes a dock at <paramref name="level"/> unlocks, for the economy self-check. The
    /// ladder has to start at nothing and end at everything, or the dock is either pointless or a
    /// hull nobody can ever buy.
    /// </summary>
    internal static int CountShipTypesAtLevel(int level)
    {
        int count = 0;
        foreach (ShipType type in System.Enum.GetValues(typeof(ShipType)))
        {
            if (level >= GetRequiredDockLevel(type))
            {
                count++;
            }
        }

        return count;
    }
    internal ShipDefinition? SelectedDefinition => shipDefinitions.Count == 0
        ? null
        : shipDefinitions[Mathf.Clamp(selectedIndex, 0, shipDefinitions.Count - 1)];
    internal bool AwaitingRallySelection => pendingDefinition != null;
    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;

    public void Activate()
    {
        RefreshDefinitions();
    }

    public void Deactivate()
    {
        CancelRallySelection(showStatus: false);
    }

    public void ResetSession()
    {
        pendingDefinition = null;
        shipDefinitions.Clear();
        entryCandidates.Clear();
        selectedIndex = 0;
        statusUntil = 0f;
        statusText = string.Empty;
        restoreTacticalMap = false;
        selectionBlockingRect = default;
        mapClickTracker.Reset();
    }

    public void TickActive()
    {
        if (!AwaitingRallySelection)
        {
            return;
        }

        if (CommanderGameInput.CancelDown)
        {
            CancelRallySelection(showStatus: true);
            return;
        }

        if (!DynamicMap.mapMaximized)
        {
            CancelRallySelection(showStatus: true);
            return;
        }

        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        if (map == null)
        {
            CancelRallySelection(showStatus: true);
            return;
        }

        Vector2 guiMouse = CommanderUiScale.ScreenToGui(Input.mousePosition);
        if (selectionBlockingRect.Contains(guiMouse))
        {
            mapClickTracker.Reset();
            return;
        }

        if (mapClickTracker.Tick(map, out GlobalPosition requestedRally))
        {
            CompletePurchase(requestedRally);
        }
    }

    internal void SetSelectionBlockingRect(Rect rect)
    {
        selectionBlockingRect = rect;
    }

    internal void SelectDefinition(int index)
    {
        if (index >= 0 && index < shipDefinitions.Count)
        {
            selectedIndex = index;
        }
    }

    internal string GetDefinitionLabel(ShipDefinition definition)
    {
        string cost = UnitConverter.ValueReading(definition.value) ?? definition.value.ToString("F0");
        string type = definition.shipType.ToString();
        return $"{definition.unitName}\n{type}  |  {cost}";
    }

    internal void BeginPurchase()
    {
        ShipDefinition? definition = SelectedDefinition;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (definition == null)
        {
            SetStatus("No purchasable ships are available.");
            return;
        }
        if (hq == null)
        {
            SetStatus("No friendly faction is available.");
            return;
        }
        int dockLevel = CommanderEconomyService.GetNavalDockLevel(hq);
        if (dockLevel <= 0)
        {
            SetStatus("No naval dock. Build one on the shore from the BUILD window before buying ships.");
            return;
        }
        if (!IsUnlocked(definition, dockLevel))
        {
            SetStatus($"{definition.unitName} needs a level {GetRequiredDockLevel(definition)} naval dock.");
            return;
        }
        if (hq.factionFunds < definition.value)
        {
            SetStatus($"Insufficient faction funds for {definition.unitName}.");
            return;
        }
        if (!HasNavalEntryNetwork())
        {
            SetStatus("This map has no valid naval reinforcement route.");
            return;
        }

        pendingDefinition = definition;
        // The mod's own map, like every other placement. restoreTacticalMap now means "it was
        // already open", so only a purchase that opened it puts it away again.
        restoreTacticalMap = tacticalMapService.IsOpen;
        tacticalMapService.OpenForPlacement();
        tacticalMapService.SuppressMapFollow = true;
        mapClickTracker.Reset();
        SetStatus("Select a water rally point on the tactical map. The ship will enter from a friendly map-edge sea lane.");
    }

    internal void CancelRallySelection()
    {
        CancelRallySelection(showStatus: true);
    }

    private void CompletePurchase(GlobalPosition requestedRally)
    {
        ShipDefinition? definition = pendingDefinition;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (definition == null || hq == null)
        {
            CancelRallySelection(showStatus: true);
            return;
        }

        RoadNetwork? seaLanes = NetworkSceneSingleton<LevelInfo>.i?.seaLanes;
        if (seaLanes == null
            || !seaLanes.Exists()
            || !seaLanes.TryGetNearestPoint(requestedRally, out GlobalPosition rallyPoint, out _)
            || FastMath.Distance(requestedRally, rallyPoint) > MaximumRallySnapMeters)
        {
            SetStatus("No sea lane is close enough to that rally point. Select open navigable water.");
            mapClickTracker.Reset();
            return;
        }

        if (!IsUnlocked(definition, CommanderEconomyService.GetNavalDockLevel(hq)))
        {
            SetStatus($"{definition.unitName} is no longer unlocked. Check the naval dock is still standing.");
            CancelRallySelection(showStatus: false);
            return;
        }

        if (hq.factionFunds < definition.value)
        {
            SetStatus($"Insufficient faction funds for {definition.unitName}.");
            CancelRallySelection(showStatus: false);
            return;
        }

        if (!TryFindEntry(definition, hq, rallyPoint, out GlobalPosition spawnPosition, out Quaternion rotation))
        {
            SetStatus("No clear naval reinforcement entry is currently available.");
            mapClickTracker.Reset();
            return;
        }

        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (spawner == null || NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            SetStatus("Ship purchasing is only available to the host.");
            CancelRallySelection(showStatus: false);
            return;
        }

        Ship? ship;
        try
        {
            ship = spawner.SpawnShip(
                definition.unitPrefab,
                spawnPosition,
                rotation,
                hq,
                null,
                1f,
                holdPosition: false);
        }
        catch (Exception exception)
        {
            CommanderPlugin.Log.LogError($"Naval reinforcement spawn failed: {exception}");
            SetStatus("The ship could not be spawned. No funds were deducted.");
            mapClickTracker.Reset();
            return;
        }

        if (ship == null)
        {
            SetStatus("The ship could not be spawned. No funds were deducted.");
            mapClickTracker.Reset();
            return;
        }

        hq.AddFunds(-definition.value);
        bool ordered = CommanderGameAccess.TrySetDestination(ship, rallyPoint);
        SetStatus(ordered
            ? $"{definition.unitName} purchased and dispatched to the selected rally point."
            : $"{definition.unitName} purchased. Select it to issue a destination.");
        FinishRallySelection();
    }

    /// <summary>
    /// Buys and launches a ship for a faction that is not the player's. Same sea-lane entry, same
    /// dock gate, same funds — the enemy commander calls this rather than carrying a second copy of
    /// the entry search, which is the part that knows where a hull can actually be put in the water.
    /// Returns what it spent, 0 when nothing was bought.
    /// </summary>
    internal float TryPurchaseForHq(FactionHQ hq, ShipDefinition definition, GlobalPosition rallyPoint)
    {
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (!hq.IsServer
            || spawner == null
            || hq.factionFunds < definition.value
            || !IsUnlocked(definition, CommanderEconomyService.GetNavalDockLevel(hq)))
        {
            return 0f;
        }

        RoadNetwork? seaLanes = NetworkSceneSingleton<LevelInfo>.i?.seaLanes;
        if (seaLanes == null
            || !seaLanes.Exists()
            || !seaLanes.TryGetNearestPoint(rallyPoint, out GlobalPosition lanePoint, out _)
            || !TryFindEntry(definition, hq, lanePoint, out GlobalPosition spawnPosition, out Quaternion rotation))
        {
            return 0f;
        }

        Ship? ship;
        try
        {
            ship = spawner.SpawnShip(definition.unitPrefab, spawnPosition, rotation, hq, null, 1f, holdPosition: false);
        }
        catch (Exception exception)
        {
            CommanderPlugin.Log.LogError($"Enemy naval reinforcement spawn failed: {exception}");
            return 0f;
        }

        if (ship == null)
        {
            return 0f;
        }

        float cost = Mathf.Max(0f, definition.value);
        hq.AddFunds(-cost);
        CommanderGameAccess.TrySetDestination(ship, lanePoint);
        return cost;
    }

    private bool TryFindEntry(
        ShipDefinition definition,
        FactionHQ hq,
        GlobalPosition rallyPoint,
        out GlobalPosition spawnPosition,
        out Quaternion rotation)
    {
        spawnPosition = default;
        rotation = Quaternion.identity;
        LevelInfo? levelInfo = NetworkSceneSingleton<LevelInfo>.i;
        RoadNetwork? seaLanes = levelInfo?.seaLanes;
        MapSettings? mapSettings = levelInfo?.LoadedMapSettings;
        if (seaLanes == null || mapSettings == null || !seaLanes.Exists())
        {
            return false;
        }

        float halfWidth = mapSettings.MapSize.x * 0.5f;
        float halfHeight = mapSettings.MapSize.y * 0.5f;
        float entryBand = Mathf.Max(
            MinimumEntryBandMeters,
            Mathf.Min(mapSettings.MapSize.x, mapSettings.MapSize.y) * EntryBandMapFraction);

        // A hull belongs beside the harbour that unlocked it. Without a dock there is nothing to
        // anchor to and the ship still has to come from somewhere, so it sails in off the map edge
        // the way it always did; with one, every sea lane on the map is a candidate and the nearest
        // to the dock wins. The edge band was what put a purchase on the far coast: it threw away
        // every lane point near the dock before the score was ever asked.
        bool hasDock = CommanderEconomyService.TryGetNavalDockPosition(hq, out GlobalPosition dock);

        entryCandidates.Clear();
        foreach (Road road in seaLanes.roads)
        {
            if (road?.points == null)
            {
                continue;
            }

            for (int i = 0; i < road.points.Count; i++)
            {
                GlobalPosition point = road.points[i];
                float edgeDistance = Mathf.Min(
                    halfWidth - Mathf.Abs(point.x),
                    halfHeight - Mathf.Abs(point.z));
                if (edgeDistance < 0f || (!hasDock && edgeDistance > entryBand))
                {
                    continue;
                }

                Vector3 direction = GetInwardRoadDirection(road, i, point);
                float score = hasDock
                    ? FastMath.Distance(point, dock) + FastMath.Distance(point, rallyPoint) * 0.15f
                    : GetFriendlyEntryScore(hq, point, rallyPoint, edgeDistance);
                entryCandidates.Add(new EntryCandidate(point, direction, score));
            }
        }

        entryCandidates.Sort(static (left, right) => left.Score.CompareTo(right.Score));
        for (int i = 0; i < entryCandidates.Count; i++)
        {
            EntryCandidate candidate = entryCandidates[i];
            Quaternion candidateRotation = Quaternion.LookRotation(candidate.Direction, Vector3.up);
            if (!TryValidateEntry(definition, candidate.Position, candidateRotation, out GlobalPosition validatedPosition))
            {
                continue;
            }

            spawnPosition = validatedPosition;
            rotation = candidateRotation;
            return true;
        }

        return false;
    }

    private static Vector3 GetInwardRoadDirection(Road road, int pointIndex, GlobalPosition point)
    {
        Vector3 direction = Vector3.forward;
        if (pointIndex + 1 < road.points.Count)
        {
            direction = FastMath.NormalizedDirection(point, road.points[pointIndex + 1]);
        }
        else if (pointIndex > 0)
        {
            direction = FastMath.NormalizedDirection(point, road.points[pointIndex - 1]);
        }

        direction.y = 0f;
        Vector3 towardMapCenter = new(-point.x, 0f, -point.z);
        if (direction.sqrMagnitude < 0.01f)
        {
            direction = towardMapCenter;
        }
        if (Vector3.Dot(direction, towardMapCenter) < 0f)
        {
            direction = -direction;
        }
        return direction.sqrMagnitude > 0.01f ? direction.normalized : Vector3.forward;
    }

    private static float GetFriendlyEntryScore(
        FactionHQ hq,
        GlobalPosition point,
        GlobalPosition rallyPoint,
        float edgeDistance)
    {
        float friendlyDistance = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled)
            {
                continue;
            }

            Transform anchor = airbase.center != null ? airbase.center : airbase.transform;
            friendlyDistance = Mathf.Min(
                friendlyDistance,
                FastMath.Distance(point, anchor.GlobalPosition()));
        }

        if (friendlyDistance == float.MaxValue)
        {
            friendlyDistance = FastMath.Distance(point, rallyPoint);
        }

        return friendlyDistance
            + FastMath.Distance(point, rallyPoint) * 0.15f
            + edgeDistance * 2f;
    }

    private static bool TryValidateEntry(
        ShipDefinition definition,
        GlobalPosition seaLanePoint,
        Quaternion rotation,
        out GlobalPosition spawnPosition)
    {
        Vector3 localPosition = seaLanePoint.ToLocalPosition();
        localPosition.y = Datum.LocalSeaY + definition.spawnOffset.y;
        spawnPosition = localPosition.ToGlobalPosition();

        float requiredDepth = Mathf.Max(8f, definition.height * 0.2f);
        Vector3 depthRayOrigin = new(localPosition.x, Datum.LocalSeaY + 5f, localPosition.z);
        if (Physics.Raycast(
            depthRayOrigin,
            Vector3.down,
            out RaycastHit seabedHit,
            2000f,
            PhysicsLayers.StaticsMask,
            QueryTriggerInteraction.Ignore)
            && Datum.LocalSeaY - seabedHit.point.y < requiredDepth)
        {
            return false;
        }

        Vector3 halfExtents = new(
            Mathf.Max(20f, definition.width * 0.65f),
            Mathf.Max(8f, definition.height * 0.35f),
            Mathf.Max(30f, definition.length * 0.65f));
        Vector3 overlapCenter = localPosition + Vector3.up * halfExtents.y;
        int obstructionMask = PhysicsLayers.StaticsMask | PhysicsLayers.ShipsMask;
        return !Physics.CheckBox(
            overlapCenter,
            halfExtents,
            rotation,
            obstructionMask,
            QueryTriggerInteraction.Ignore);
    }

    private static bool HasNavalEntryNetwork()
    {
        return NetworkSceneSingleton<LevelInfo>.i?.seaLanes?.Exists() == true;
    }

    private void RefreshDefinitions()
    {
        ShipDefinition? previouslySelected = SelectedDefinition;
        shipDefinitions.Clear();
        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia?.ships != null)
        {
            for (int i = 0; i < encyclopedia.ships.Count; i++)
            {
                ShipDefinition definition = encyclopedia.ships[i];
                if (definition != null
                    && definition.unitPrefab != null
                    && definition.IsAllowed(includeEventContent: false)
                    && definition.unitPrefab.GetComponent<Ship>() != null)
                {
                    shipDefinitions.Add(definition);
                }
            }
        }

        shipDefinitions.Sort(static (left, right) =>
        {
            int valueComparison = left.value.CompareTo(right.value);
            return valueComparison != 0
                ? valueComparison
                : string.Compare(left.unitName, right.unitName, StringComparison.OrdinalIgnoreCase);
        });
        int preservedIndex = previouslySelected == null ? -1 : shipDefinitions.IndexOf(previouslySelected);
        selectedIndex = preservedIndex >= 0
            ? preservedIndex
            : Mathf.Clamp(selectedIndex, 0, Mathf.Max(0, shipDefinitions.Count - 1));
    }

    private void FinishRallySelection()
    {
        pendingDefinition = null;
        tacticalMapService.SuppressMapFollow = false;
        mapClickTracker.Reset();
        if (!restoreTacticalMap)
        {
            tacticalMapService.Close();
        }
        restoreTacticalMap = false;
    }

    private void CancelRallySelection(bool showStatus)
    {
        if (pendingDefinition == null)
        {
            return;
        }

        pendingDefinition = null;
        tacticalMapService.SuppressMapFollow = false;
        mapClickTracker.Reset();
        if (!restoreTacticalMap)
        {
            tacticalMapService.Close();
        }
        restoreTacticalMap = false;
        if (showStatus)
        {
            SetStatus("Naval reinforcement purchase cancelled.");
        }
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }

    private readonly struct EntryCandidate
    {
        internal EntryCandidate(GlobalPosition position, Vector3 direction, float score)
        {
            Position = position;
            Direction = direction;
            Score = score;
        }

        internal GlobalPosition Position { get; }
        internal Vector3 Direction { get; }
        internal float Score { get; }
    }
}
