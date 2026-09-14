using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace GroundControlRts;

internal static class CommanderGameAccess
{
    private static readonly FieldInfo? UnitMarkerField = AccessTools.Field(typeof(CombatHUD), "unitMarker");
    private static readonly FieldInfo? OnFollowingUnitSetField = AccessTools.Field(typeof(CameraStateManager), "onFollowingUnitSet");
    private static readonly FieldInfo? VehicleDepotSpawnTransformField = AccessTools.Field(typeof(VehicleDepot), "spawnTransform");
    private static readonly List<VehicleDefinition> RepairVehicleScratch = new();

    internal static FactionHQ? GetLocalHq()
    {
        return GameManager.GetLocalHQ(out FactionHQ localHq) ? localHq : null;
    }

    internal static FactionHQ? GetPlayerHq()
    {
        return GameManager.GetLocalPlayer<Player>(out Player player) && player != null ? player.HQ : null;
    }

    internal static FactionHQ? GetDynamicMapHq()
    {
        return SceneSingleton<DynamicMap>.i?.HQ;
    }

    internal static FactionHQ? GetSupplyHq()
    {
        FactionHQ? localHq = GetLocalHq();
        if (localHq?.VehicleSupply != null && localHq.VehicleSupply.Count > 0)
        {
            return localHq;
        }

        FactionHQ? dynamicMapHq = GetDynamicMapHq();
        if (dynamicMapHq?.VehicleSupply != null && dynamicMapHq.VehicleSupply.Count > 0)
        {
            return dynamicMapHq;
        }

        FactionHQ? playerHq = GetPlayerHq();
        if (playerHq?.VehicleSupply != null && playerHq.VehicleSupply.Count > 0)
        {
            return playerHq;
        }

        return localHq ?? dynamicMapHq ?? playerHq;
    }

    internal static bool IsFriendlyUnit(Unit? unit, FactionHQ? localHq)
    {
        if (unit == null || localHq == null || unit.disabled)
        {
            return false;
        }

        FactionHQ? unitHq = unit.NetworkHQ;
        return ReferenceEquals(unitHq, localHq) || ReferenceEquals(unit.MapHQ, localHq);
    }

    internal static bool ShouldTrackUnit(Unit? unit, FactionHQ? localHq)
    {
        if (unit == null || unit.disabled || localHq == null)
        {
            return false;
        }

        // Mission-authored factories are everywhere and would bury the marker layer, but one this
        // commander paid for is a thing you need to click on to upgrade.
        if (unit is Building
            && unit.GetComponent<Factory>() != null
            && !CommanderEconomyService.IsCommanderBuilt(unit))
        {
            return false;
        }

        if (IsFriendlyUnit(unit, localHq))
        {
            return IsCommanderMarkerUnit(unit, localHq);
        }

        if (!IsCommanderMarkerUnit(unit, localHq))
        {
            return false;
        }

        TrackingInfo? tracking = localHq.GetTrackingData(unit.persistentID);
        return tracking != null && Time.timeSinceLevelLoad - tracking.lastSpottedTime <= 8f;
    }

    internal static bool ShouldAllowCommanderSelection(Unit? unit, FactionHQ? localHq)
    {
        if (unit == null || unit.disabled || localHq == null)
        {
            return false;
        }

        if (IsFriendlyUnit(unit, localHq))
        {
            return IsCommanderMarkerUnit(unit, localHq);
        }

        if (!IsCommanderMarkerUnit(unit, localHq))
        {
            return false;
        }

        TrackingInfo? tracking = localHq.GetTrackingData(unit.persistentID);
        return tracking != null && Time.timeSinceLevelLoad - tracking.lastSpottedTime <= 8f;
    }

    internal static bool ShouldRetainCommanderMarker(Unit? unit, FactionHQ? localHq)
    {
        if (unit == null || unit.disabled || localHq == null)
        {
            return false;
        }

        if (IsFriendlyUnit(unit, localHq))
        {
            return true;
        }

        TrackingInfo? tracking = localHq.GetTrackingData(unit.persistentID);
        return tracking != null && Time.timeSinceLevelLoad - tracking.lastSpottedTime <= 8f;
    }

    private static bool IsCommanderMarkerUnit(Unit unit, FactionHQ localHq)
    {
        if (unit is GroundVehicle || unit is Ship || unit is Aircraft || unit is Missile)
        {
            return true;
        }

        if (HasFriendlyDepot(unit, localHq)
            || CommanderSamSiteCoreRegistry.IsTrackedSiteUnit(unit)
            || CommanderEconomyService.IsCommanderBuilt(unit))
        {
            return true;
        }

        if (unit is not Building || unit.GetComponent<Factory>() != null)
        {
            return false;
        }

        return unit.weaponStations.Count > 0
            || unit.GetComponentInChildren<Rearmer>(true) != null;
    }

    internal static bool HasFriendlyDepot(Unit? unit, FactionHQ? localHq)
    {
        return GetFriendlyDepotFromUnit(unit, localHq) != null;
    }

    internal static Transform? GetMarkerParent()
    {
        GameplayUI? gameplayUi = SceneSingleton<GameplayUI>.i;
        if (gameplayUi?.gameplayCanvas != null)
        {
            return gameplayUi.gameplayCanvas.transform;
        }

        return gameplayUi != null ? gameplayUi.transform : null;
    }

    internal static Image? CreateMarkerImage(Transform parent)
    {
        CombatHUD? combatHud = SceneSingleton<CombatHUD>.i;
        GameObject? prefab = combatHud != null ? UnitMarkerField?.GetValue(combatHud) as GameObject : null;
        GameObject markerObject;

        if (prefab != null)
        {
            markerObject = Object.Instantiate(prefab, parent);
        }
        else
        {
            markerObject = new GameObject("CommanderMarker", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            markerObject.transform.SetParent(parent, false);
        }

        Image? image = markerObject.GetComponent<Image>();
        RectTransform? rectTransform = markerObject.transform as RectTransform;
        if (image == null || rectTransform == null)
        {
            Object.Destroy(markerObject);
            return null;
        }

        rectTransform.SetParent(parent, false);
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.zero;
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        image.raycastTarget = false;
        image.enabled = false;
        return image;
    }

    internal static Sprite? GetFriendlySprite(Unit unit)
    {
        return unit.definition != null && unit.definition.friendlyIcon != null
            ? unit.definition.friendlyIcon
            : GameAssets.i != null
                ? GameAssets.i.targetUnitSpriteFriendly
                : null;
    }

    internal static Sprite? GetHostileSprite(Unit unit)
    {
        return unit.definition != null && unit.definition.hostileIcon != null
            ? unit.definition.hostileIcon
            : GameAssets.i != null
                ? GameAssets.i.targetUnitSprite
                : null;
    }

    internal static Color GetFriendlyColor()
    {
        return GameAssets.i != null ? GameAssets.i.HUDFriendly : Color.green;
    }

    internal static Color GetSelectedFriendlyColor()
    {
        return GameAssets.i != null ? GameAssets.i.HUDFriendlySelected : Color.cyan;
    }

    internal static Color GetHostileColor()
    {
        return GameAssets.i != null ? GameAssets.i.HUDHostile : Color.red;
    }

    internal static Color GetSelectedHostileColor()
    {
        return GameAssets.i != null ? GameAssets.i.HUDHostileSelected : new Color(1f, 0.45f, 0.25f);
    }

    internal static float GetBaseScale(Unit unit)
    {
        float iconSize = unit.definition != null && unit.definition.iconSize > 0f ? unit.definition.iconSize : 1f;
        float userScale = PlayerSettings.hmdIconSize > 0f ? PlayerSettings.hmdIconSize : 1f;
        return iconSize * userScale;
    }

    internal static bool TryGetWorldMarkerState(Unit unit, Camera camera, out Vector3 screenPosition, out float scale)
    {
        return TryGetWorldMarkerState(unit.GlobalPosition(), camera, out screenPosition, out scale);
    }

    internal static bool TryGetWorldMarkerState(GlobalPosition unitPosition, Camera camera, out Vector3 screenPosition, out float scale)
    {
        screenPosition = default;
        scale = 1f;
        Vector3 worldPosition = GlobalPositionExtensions.ToLocalPosition(unitPosition);
        Vector3 cameraPosition = camera.transform.position;
        Vector3 direction = worldPosition - cameraPosition;
        if (Vector3.Dot(direction, camera.transform.forward) <= 0f)
        {
            return false;
        }

        Vector3 worldToScreen = camera.WorldToScreenPoint(worldPosition);
        if (worldToScreen.z <= 0f)
        {
            return false;
        }

        if (worldToScreen.x < 0f || worldToScreen.x > Screen.width || worldToScreen.y < 0f || worldToScreen.y > Screen.height)
        {
            return false;
        }

        float distance = Vector3.Distance(cameraPosition, worldPosition);
        float distanceFactor = Mathf.Clamp01(distance * 0.00004f - 0.5f);
        scale = Mathf.Lerp(1f, 0.45f, distanceFactor);
        screenPosition = new Vector3(worldToScreen.x, worldToScreen.y, 0f);
        return true;
    }

    internal static bool TryRaycastSelectableUnit(Vector2 screenPosition, out Unit unit)
    {
        unit = null!;

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return false;
        }

        Ray ray = camera.ScreenPointToRay(screenPosition);
        RaycastHit[] hits = Physics.RaycastAll(ray, 500000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        if (hits.Length == 0)
        {
            return false;
        }

        System.Array.Sort(hits, static (a, b) => a.distance.CompareTo(b.distance));
        FactionHQ? localHq = GetLocalHq();
        for (int i = 0; i < hits.Length; i++)
        {
            VehicleDepot? hitDepot = hits[i].collider.GetComponentInParent<VehicleDepot>();
            if (hitDepot != null)
            {
                Unit? depotOwner = GetDepotOwner(hitDepot);
                if (ShouldAllowCommanderSelection(depotOwner, localHq))
                {
                    unit = depotOwner!;
                    return true;
                }
            }

            Unit? hitUnit = hits[i].collider.GetComponentInParent<Unit>();
            if (hitUnit == null)
            {
                hitUnit = hits[i].collider.GetComponentInParent<UnitPart>()?.parentUnit;
            }
            hitUnit = CommanderSamSiteCoreRegistry.ResolveSelection(hitUnit);
            if (ShouldAllowCommanderSelection(hitUnit, localHq))
            {
                unit = hitUnit!;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Any hostile unit under the cursor, including structures and objectives that are not
    /// eligible for Commander selection. Used to turn a right click into an attack order.
    /// </summary>
    internal static bool TryRaycastHostileUnit(Vector2 screenPosition, out Unit unit)
    {
        unit = null!;
        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        FactionHQ? localHq = GetLocalHq();
        if (camera == null || localHq == null)
        {
            return false;
        }

        Ray ray = camera.ScreenPointToRay(screenPosition);
        RaycastHit[] hits = Physics.RaycastAll(ray, 500000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        if (hits.Length == 0)
        {
            return false;
        }

        System.Array.Sort(hits, static (a, b) => a.distance.CompareTo(b.distance));
        for (int i = 0; i < hits.Length; i++)
        {
            Unit? hitUnit = hits[i].collider.GetComponentInParent<Unit>()
                ?? hits[i].collider.GetComponentInParent<UnitPart>()?.parentUnit;
            if (hitUnit == null || hitUnit.disabled || hitUnit.NetworkHQ == null)
            {
                continue;
            }

            if (!IsFriendlyUnit(hitUnit, localHq))
            {
                unit = hitUnit;
                return true;
            }
        }

        return false;
    }

    internal static bool TryRaycastWorldPosition(Vector2 screenPosition, out GlobalPosition position)
    {
        position = default;
        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return false;
        }

        Ray ray = camera.ScreenPointToRay(screenPosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, 500000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        position = GlobalPositionExtensions.ToGlobalPosition(hit.point);
        return true;
    }

    /// <summary>
    /// Drops a position onto the terrain surface, probing a small ring of offsets so a point that
    /// lands inside a building or on water still finds ground nearby. Only hits carrying the
    /// terrain material count: a roof is not ground.
    /// </summary>
    internal static GlobalPosition SnapToTerrain(GlobalPosition target)
    {
        Vector2[] offsets =
        {
            Vector2.zero,
            new(3f, 0f),
            new(-3f, 0f),
            new(0f, 3f),
            new(0f, -3f),
            new(6f, 0f),
            new(-6f, 0f),
            new(0f, 6f),
            new(0f, -6f),
            new(8f, 8f),
            new(-8f, 8f),
            new(8f, -8f),
            new(-8f, -8f)
        };
        for (int i = 0; i < offsets.Length; i++)
        {
            Vector3 local = new GlobalPosition(
                target.x + offsets[i].x,
                target.y,
                target.z + offsets[i].y).ToLocalPosition();
            Vector3 origin = new(local.x, Datum.LocalSeaY + 10000f, local.z);
            if (GameAssets.i == null)
            {
                continue;
            }

            RaycastHit[] hits = Physics.RaycastAll(
                origin,
                Vector3.down,
                20000f,
                PhysicsLayers.StaticsMask,
                QueryTriggerInteraction.Ignore);
            float highestTerrainY = float.MinValue;
            Vector3 terrainPoint = default;
            for (int hitIndex = 0; hitIndex < hits.Length; hitIndex++)
            {
                RaycastHit hit = hits[hitIndex];
                if (hit.collider != null
                    && hit.collider.sharedMaterial == GameAssets.i.terrainMaterial
                    && hit.point.y > highestTerrainY)
                {
                    highestTerrainY = hit.point.y;
                    terrainPoint = hit.point;
                }
            }
            if (highestTerrainY > float.MinValue)
            {
                return terrainPoint.ToGlobalPosition();
            }
        }

        return target;
    }

    internal static bool TryRaycastWaterPosition(Vector2 screenPosition, out GlobalPosition position)
    {
        position = default;
        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return false;
        }

        Ray ray = camera.ScreenPointToRay(screenPosition);
        if (!Datum.WaterPlane().Raycast(ray, out float distance) || distance < 0f || distance > 500000f)
        {
            return false;
        }

        position = GlobalPositionExtensions.ToGlobalPosition(ray.GetPoint(distance));
        return true;
    }

    /// <summary>
    /// True when a global position is under water. Sea level in the <see cref="GlobalPosition"/>
    /// frame is a flat <c>y = 0</c> (<c>Datum.SeaLevel</c>); <c>Datum.LocalSeaY</c> is the same
    /// plane expressed in the *local* frame, and the floating origin shifts that on every axis
    /// including y once the camera climbs past 1024 m. Comparing a global y against
    /// <c>LocalSeaY</c> therefore reads "underwater" as "below the camera", which is how the naval
    /// dock's shoreline probes stopped finding water the moment the RTS camera gained any altitude.
    /// </summary>
    internal static bool IsBelowSeaLevel(GlobalPosition position)
    {
        return position.y < 0f;
    }

    internal static bool ShouldAllowCommanderMove(Unit? unit)
    {
        FactionHQ? localHq = GetLocalHq();
        return IsFriendlyUnit(unit, localHq)
            && (unit is GroundVehicle || unit is Ship)
            && !CommanderMobileEmplacementService.IsReservedHauler(unit)
            && !CommanderSamSiteService.IsReservedConstructionJacknife(unit);
    }

    internal static bool IsFriendlyDepot(VehicleDepot? depot)
    {
        return IsFriendlyDepot(depot, GetLocalHq());
    }

    internal static bool IsFriendlyDepot(VehicleDepot? depot, FactionHQ? localHq)
    {
        return depot != null && IsFriendlyUnit(GetDepotOwner(depot), localHq);
    }

    internal static Unit? GetDepotOwner(VehicleDepot depot)
    {
        return depot.GetComponentInParent<Unit>();
    }

    internal static VehicleDepot? GetFriendlyDepotFromUnit(Unit? unit, FactionHQ? localHq)
    {
        if (unit == null)
        {
            return null;
        }

        if (unit is VehicleDepot directDepot && IsFriendlyDepot(directDepot, localHq))
        {
            return directDepot;
        }

        VehicleDepot? childDepot = unit.GetComponentInChildren<VehicleDepot>();
        return IsFriendlyDepot(childDepot, localHq) ? childDepot : null;
    }

    internal static UnitCommand? GetUnitCommand(Unit unit)
    {
        return unit switch
        {
            GroundVehicle groundVehicle => groundVehicle.UnitCommand,
            Ship ship => ship.UnitCommand,
            _ => null
        };
    }

    internal static bool SetUnitHoldPosition(Unit unit, bool hold)
    {
        switch (unit)
        {
            case GroundVehicle groundVehicle:
                groundVehicle.SetHoldPosition(hold);
                return true;
            case Ship ship:
                ship.SetHoldPosition(hold);
                return true;
            default:
                return false;
        }
    }

    internal static bool TryGetCurrentCommandPosition(Unit unit, out GlobalPosition position)
    {
        position = default;
        UnitCommand? unitCommand = GetUnitCommand(unit);
        if (unitCommand == null)
        {
            return false;
        }

        UnitCommand.Command command = unitCommand.GetCommandCached();
        position = command.position;
        return command.time > 0f || command.player != null || !command.position.Equals(default(GlobalPosition));
    }

    internal static bool TrySetDestination(Unit unit, GlobalPosition destination)
    {
        UnitCommand? unitCommand = GetUnitCommand(unit);
        if (unitCommand == null)
        {
            return false;
        }

        unitCommand.SetDestination(destination, true);
        return true;
    }

    internal static void CollectFriendlySurfaceUnits(List<Unit> units)
    {
        units.Clear();
        FactionHQ? localHq = GetLocalHq();
        if (localHq?.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID unitId in localHq.factionUnits)
        {
            if (!unitId.TryGetUnit(out Unit unit) || !ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            units.Add(unit);
        }
    }

    internal static Transform? GetDepotSpawnTransform(VehicleDepot depot)
    {
        return VehicleDepotSpawnTransformField?.GetValue(depot) as Transform;
    }

    internal static Vector3 GetDepotSpawnPosition(VehicleDepot depot)
    {
        Transform? spawnTransform = GetDepotSpawnTransform(depot);
        return spawnTransform != null ? spawnTransform.position : depot.transform.position;
    }

    /// <summary>
    /// Condition 0-1, taken from the worst-off part on the unit. Parts start at 100 hit points,
    /// so this is the same number the Basegame damage model works from.
    /// </summary>
    internal static float GetUnitCondition(Unit? unit)
    {
        if (unit == null || unit.disabled)
        {
            return 0f;
        }

        float worst = 1f;
        List<DamageablePart> parts = unit.damageables;
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Removed)
            {
                return 0f;
            }

            if (parts[i].Damageable is UnitPart part)
            {
                worst = Mathf.Min(worst, Mathf.Clamp01(part.hitPoints * 0.01f));
            }
        }

        return worst;
    }

    /// <summary>Ammo 0-1 across every weapon station, or -1 when the unit carries no weapons.</summary>
    internal static float GetUnitAmmo(Unit? unit)
    {
        if (unit == null || unit.disabled || unit.weaponStations == null || unit.weaponStations.Count == 0)
        {
            return -1f;
        }

        return Mathf.Clamp01(unit.GetAmmoLevel());
    }

    /// <summary>
    /// Fuel 0-1, or -1 for anything that carries no fuel tanks (every ground vehicle and ship —
    /// only <see cref="Aircraft"/> burns fuel in this game). Tanks only drain on the client that
    /// simulates the airframe, so on a pure multiplayer client a remote aircraft reads its spawn
    /// level rather than a live one.
    /// </summary>
    internal static float GetUnitFuel(Unit? unit)
    {
        if (unit is not Aircraft aircraft || aircraft.disabled)
        {
            return -1f;
        }

        return Mathf.Clamp01(aircraft.GetFuelLevel());
    }

    internal static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    internal static bool ApproximatelyEqual(GlobalPosition a, GlobalPosition b, float toleranceMeters)
    {
        return HorizontalDistance(a.AsVector3(), b.AsVector3()) <= toleranceMeters;
    }

    internal static bool IsSpawnableVehicleDefinition(VehicleDefinition? definition)
    {
        if (definition?.unitPrefab == null)
        {
            return false;
        }

        return definition.unitPrefab.GetComponent<GroundVehicle>() != null;
    }

    /// <summary>
    /// True when <paramref name="definition"/>'s prefab carries both a <c>RearmVehicleAI</c> and a
    /// <c>Rearmer</c> — the same pair <see cref="CommanderMoveService.TryDetachFromRearmLogistics"/>
    /// already treats as "this is a rearm vehicle". Departure 3: <c>VehicleType.TRUCK</c> is too
    /// broad (every truck, rearm or not) and there is no vehicle-definition-level rearm flag
    /// anywhere in the mod, so a munitions truck is identified by what its prefab carries instead.
    /// </summary>
    internal static bool IsMunitionsTruckDefinition(VehicleDefinition? definition)
    {
        if (definition?.unitPrefab == null)
        {
            return false;
        }

        return definition.unitPrefab.GetComponentInChildren<RearmVehicleAI>(true) != null
            && definition.unitPrefab.GetComponentInChildren<Rearmer>(true) != null;
    }

    internal static bool TryGetLocalVehicleDefinitions(List<VehicleDefinition> buffer)
    {
        if (buffer == null)
        {
            return false;
        }

        buffer.Clear();

        VehicleDefinition[] allDefinitions = Resources.FindObjectsOfTypeAll<VehicleDefinition>();
        for (int i = 0; i < allDefinitions.Length; i++)
        {
            VehicleDefinition definition = allDefinitions[i];
            if (IsSpawnableVehicleDefinition(definition) && !buffer.Contains(definition))
            {
                buffer.Add(definition);
            }
        }

        return buffer.Count > 0;
    }

    internal static bool TryGetLocalProductionVehicleDefinitions(List<VehicleDefinition> buffer)
    {
        if (buffer == null)
        {
            return false;
        }

        buffer.Clear();
        FactionHQ? hq = GetSupplyHq();
        if (hq == null)
        {
            return false;
        }

        Factory[] factories = UnityEngine.Object.FindObjectsOfType<Factory>();
        for (int i = 0; i < factories.Length; i++)
        {
            Factory factory = factories[i];
            if (factory == null
                || factory.attachedUnit == null
                || !IsFriendlyUnit(factory.attachedUnit, hq)
                || factory.ProductionUnit is not VehicleDefinition definition
                || !IsSpawnableVehicleDefinition(definition)
                || buffer.Contains(definition))
            {
                continue;
            }

            buffer.Add(definition);
        }

        return buffer.Count > 0;
    }

    internal static void CollectFactionVehicleDefinitions(List<VehicleDefinition> buffer)
    {
        CollectFactionVehicleDefinitions(buffer, GetSupplyHq());
    }

    /// <summary>The ground units <paramref name="hq"/>'s faction fields, from its convoy groups.</summary>
    internal static void CollectFactionVehicleDefinitions(List<VehicleDefinition> buffer, FactionHQ? hq)
    {
        buffer.Clear();
        if (hq?.faction == null)
        {
            return;
        }

        List<Faction.ConvoyGroup> convoyGroups = hq.faction.GetConvoyGroups();
        for (int groupIndex = 0; groupIndex < convoyGroups.Count; groupIndex++)
        {
            List<Faction.ConvoyUnit> constituents = convoyGroups[groupIndex].Constituents;
            for (int unitIndex = 0; unitIndex < constituents.Count; unitIndex++)
            {
                if (constituents[unitIndex].Type is VehicleDefinition definition
                    && IsSpawnableVehicleDefinition(definition)
                    && !buffer.Contains(definition))
                {
                    buffer.Add(definition);
                }
            }
        }
    }

    /// <summary>
    /// The faction's repair vehicle — the Jacknife on every stock faction. Matched by name first
    /// and by an actual <see cref="Repairer"/> component second, because a faction that renames
    /// the truck still has to carry the component for the Basegame repair loop to work at all.
    /// </summary>
    internal static VehicleDefinition? FindRepairVehicleDefinition(FactionHQ? hq = null)
    {
        CollectFactionVehicleDefinitions(RepairVehicleScratch, hq ?? GetSupplyHq());
        for (int i = 0; i < RepairVehicleScratch.Count; i++)
        {
            VehicleDefinition definition = RepairVehicleScratch[i];
            if (definition?.unitPrefab == null)
            {
                continue;
            }

            string identity = $"{definition.unitName} {definition.code} {definition.jsonKey}";
            if (identity.IndexOf("jacknife", System.StringComparison.OrdinalIgnoreCase) >= 0
                || identity.IndexOf("jackknife", System.StringComparison.OrdinalIgnoreCase) >= 0
                || definition.unitPrefab.GetComponentInChildren<Repairer>(true) != null)
            {
                return definition;
            }
        }

        return null;
    }

    internal static string GetUnitLabel(Unit? unit)
    {
        if (unit == null)
        {
            return "Unknown unit";
        }

        if (!string.IsNullOrWhiteSpace(unit.UniqueName))
        {
            return unit.UniqueName;
        }

        if (!string.IsNullOrWhiteSpace(unit.unitName))
        {
            return unit.unitName;
        }

        return unit.name;
    }

    internal static string GetVehicleLabel(VehicleDefinition? definition)
    {
        if (definition == null)
        {
            return "Unknown vehicle";
        }

        if (!string.IsNullOrWhiteSpace(definition.unitName))
        {
            return definition.unitName;
        }

        if (!string.IsNullOrWhiteSpace(definition.code))
        {
            return definition.code;
        }

        return definition.name;
    }

    internal static string GetVehicleCategoryLabel(VehicleDefinition? definition)
    {
        if (definition == null)
        {
            return "Other";
        }

        if (IsUncrewedGroundVehicleDefinition(definition))
        {
            return "UGV";
        }

        if (IsTrailerVehicleDefinition(definition))
        {
            return "Trailer";
        }

        return definition.vehicleType switch
        {
            VehicleType.TRUCK => "Truck",
            VehicleType.UGV => "UGV",
            VehicleType.LCV => "Light Vehicle",
            VehicleType.AFV => "AFV",
            VehicleType.MBT => "MBT",
            VehicleType.ART => "Artillery",
            VehicleType.AAA => "AAA",
            VehicleType.IR_SAM => "IR SAM",
            VehicleType.R_SAM => "Radar SAM",
            VehicleType.RDR => "Radar",
            _ => "Other",
        };
    }

    internal static bool IsTrailerVehicleDefinition(VehicleDefinition? definition)
    {
        return definition != null
            && definition.manpower == 0
            && !IsUncrewedGroundVehicleDefinition(definition);
    }

    private static bool IsUncrewedGroundVehicleDefinition(VehicleDefinition definition)
    {
        return definition.vehicleType == VehicleType.UGV
            || (!string.IsNullOrWhiteSpace(definition.code)
                && definition.code.StartsWith("UGV", System.StringComparison.OrdinalIgnoreCase));
    }

    internal static void RaiseFollowingUnitSet(Unit? unit)
    {
        if (OnFollowingUnitSetField == null)
        {
            return;
        }

        object? eventOwner = OnFollowingUnitSetField.IsStatic ? null : SceneSingleton<CameraStateManager>.i;
        System.Action<Unit>? callback = OnFollowingUnitSetField.GetValue(eventOwner) as System.Action<Unit>;
        callback?.Invoke(unit!);
    }
}
