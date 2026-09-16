using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The building catalogue: every structure the game ships, buildable for a price. A gold mine and a
/// factory keep their own buttons because they carry economy behaviour the mod adds on top; the
/// catalogue is everything else, and each entry does whatever its own prefab does — a radar sees, a
/// depot supplies, a hangar repairs aircraft, a bunker soaks damage.
/// </summary>
/// <remarks>
/// ponytail: prices come from <c>UnitDefinition.value</c> times a config multiplier rather than a
/// hand-written table. The game already ranks its own buildings by worth, and a table would go stale
/// the first time a patch adds a building. Retune the multiplier if the ladder feels wrong.
/// </remarks>
internal sealed partial class CommanderEconomyService
{
    /// <summary>Nothing is free, however cheap the encyclopedia says a shed is.</summary>
    private const float MinimumStructureCost = 25f;

    private readonly List<BuildingDefinition> catalog = new();
    private bool catalogResolved;

    /// <summary>Every buildable structure, cheapest first inside each category.</summary>
    internal IReadOnlyList<BuildingDefinition> Catalog
    {
        get
        {
            RefreshCatalog();
            return catalog;
        }
    }

    internal BuildingDefinition? PendingStructure => pendingStructure;

    internal static float GetStructureCost(BuildingDefinition? definition)
    {
        return definition == null
            ? 0f
            : Mathf.Max(MinimumStructureCost, definition.value * Mathf.Max(0f, CommanderSettings.BuildingCostMultiplier));
    }

    internal static string GetCategoryLabel(BuildingType type)
    {
        return type switch
        {
            BuildingType.CIV => "CIVILIAN",
            BuildingType.FAC => "INDUSTRY",
            BuildingType.RDR => "RADAR",
            BuildingType.DEP => "DEPOT",
            BuildingType.HGR => "HANGAR",
            BuildingType.DEF => "DEFENCE",
            BuildingType.AMMO => "AMMUNITION",
            _ => "OTHER",
        };
    }

    internal static string GetStructureLabel(BuildingDefinition definition)
    {
        return string.IsNullOrWhiteSpace(definition.unitName) ? definition.jsonKey : definition.unitName;
    }

    internal void BeginStructureBuild(BuildingDefinition definition)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            StatusText = "No faction HQ.";
            return;
        }

        // Buildings are spawned through the server object manager, so a pure client cannot build.
        if (!hq.IsServer)
        {
            StatusText = "Only the host can construct buildings.";
            return;
        }

        if (hq.factionFunds < GetStructureCost(definition))
        {
            StatusText = $"Insufficient faction funds for a {GetStructureLabel(definition)}.";
            return;
        }

        pendingStructure = definition;
        pendingBuild = CommanderBuildKind.Structure;
        preview.ResetRotation();
        StatusText = $"Select the {GetStructureLabel(definition)} site in the 3D world.";
    }

    /// <summary>
    /// The catalogue is the encyclopedia minus the two buildings that already have their own button,
    /// so a player cannot buy the refinery prefab here and wonder why it pays no income.
    /// </summary>
    private void RefreshCatalog()
    {
        if (catalogResolved)
        {
            return;
        }

        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia == null || encyclopedia.buildings == null)
        {
            return;
        }

        BuildingDefinition? mine = ResolveDefinition(CommanderBuildKind.Mine);
        BuildingDefinition? factory = ResolveDefinition(CommanderBuildKind.Factory);
        // The dock prefab is an ordinary catalogue building wearing a rule: bought here it would
        // land anywhere and unlock nothing, which reads as the naval gate being broken.
        BuildingDefinition? dock = ResolveDefinition(CommanderBuildKind.NavalDock);
        catalog.Clear();
        for (int i = 0; i < encyclopedia.buildings.Count; i++)
        {
            BuildingDefinition candidate = encyclopedia.buildings[i];
            if (candidate == null
                || candidate.unitPrefab == null
                || !candidate.IsAllowed(includeEventContent: false)
                || ReferenceEquals(candidate, mine)
                || ReferenceEquals(candidate, factory)
                || ReferenceEquals(candidate, dock))
            {
                continue;
            }

            catalog.Add(candidate);
        }

        catalog.Sort((a, b) => a.buildingType != b.buildingType
            ? a.buildingType.CompareTo(b.buildingType)
            : a.value.CompareTo(b.value));
        catalogResolved = true;
    }

    /// <summary>
    /// Places a catalogue building. A prefab that happens to carry a <see cref="Factory"/> component
    /// is wired up like a bought factory rather than left inert, because an idle factory building is
    /// exactly the kind of silent dud that reads as a broken mod.
    /// </summary>
    private bool PlaceStructure(
        FactionHQ hq,
        GlobalPosition position,
        BuildingDefinition definition,
        float cost,
        Quaternion? rotation = null)
    {
        Unit? unit = SpawnBuilding(
            hq, position, definition, GetStructureLabel(definition), rotation: rotation);
        if (unit == null)
        {
            StatusText = $"{GetStructureLabel(definition)} could not be built.";
            return false;
        }

        hq.AddFunds(-cost);
        VehicleDefinition? production = SelectedProduction;
        if (unit.TryGetComponent(out Factory builtFactory) && production != null)
        {
            builtFactory.SetFactory(production.jsonKey, FactoryProductionSeconds);
            factoryLevels[unit] = 1;
            StatusText = $"{GetStructureLabel(definition)} built, producing "
                + $"{CommanderGameAccess.GetVehicleLabel(production)}.";
        }
        else
        {
            StatusText = $"{GetStructureLabel(definition)} built for {cost:0}.";
        }

        RefreshFriendlyLists();
        return true;
    }
}
