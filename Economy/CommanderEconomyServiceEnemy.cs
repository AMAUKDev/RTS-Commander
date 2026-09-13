using System.Collections.Generic;
using RoadPathfinding;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The enemy commander's half of the economy: it builds and upgrades the same mines and factories
/// the player does, at the same prices, out of its own faction funds. One purchase per review, and
/// never below a reserve multiple, so it does not starve the unit spender it shares the pot with.
/// </summary>
internal sealed partial class CommanderEconomyService
{
    /// <summary>Sites tried per enemy build before it gives up and waits for the next review. Also
    /// the ring-probe budget discovery uses to seed a resource site beside an industrial building
    /// (departure 1) — one definition, both callers.</summary>
    internal const int EnemySiteAttempts = 12;

    /// <summary>Step taken walking inland from a sea lane looking for the water's edge, and how
    /// far to keep walking. Each step is a terrain probe, so this is the cost knob.</summary>
    private const float ShoreWalkStepMeters = 50f;
    private const int ShoreWalkSteps = 8;

    /// <summary>
    /// Sea lane points examined per dock search, and the stride through each lane's point list.
    /// Both are here so one review cannot eat a frame: the search runs every 30 s until a dock
    /// exists, and every probe it makes is a raycast. Striding spreads the budget along the whole
    /// coast rather than spending it all on the first lane.
    /// </summary>
    private const int ShoreLanePointBudget = 32;
    private const int ShoreLanePointStride = 3;

    /// <summary>Defensive structures a commander keeps once its economy is running.</summary>
    private const int EnemyDefenceBuildingTarget = 3;

    /// <summary>A base counts as having radar cover with a radar building this close to it.</summary>
    private const float RadarCoverageMeters = 3000f;

    /// <summary>Ring a base-adjacent structure is dropped into: near enough to be part of the base,
    /// far enough not to be on the apron.</summary>
    private const float BaseSiteMinMeters = 200f;
    private const float BaseSiteMaxMeters = 900f;

    /// <summary>HQs already told, once, that this map gives them nowhere to put a dock.</summary>
    private readonly HashSet<FactionHQ> shoreSearchReported = new();

    /// <summary>The structure each building category resolves to, decided once per mission.</summary>
    private readonly Dictionary<BuildingType, BuildingDefinition?> categoryDefinitions = new();

    private void ReviewEnemies()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null
                || !CommanderPlayerCommanderService.IsCommanded(hq, localHq)
                || !hq.IsServer
                || hq.faction == null)
            {
                continue;
            }

            ReviewEnemy(hq);
        }
    }

    /// <summary>One economy purchase per review: a new mine while short of the target, else an upgrade.</summary>
    private void ReviewEnemy(FactionHQ hq)
    {
        // Keeping what it owns comes before buying more of it: a bombed refinery that stays bombed
        // costs the enemy commander its income, which is the same trade the player is making.
        if (hq.factionFunds >= CommanderRepairService.CrewCost * EnemyReserveMultiple
            && CommanderRepairService.Instance?.TrySendEnemyRepairCrew(hq) == true)
        {
            return;
        }

        // The price, not a multiple of it. GetEnemyBuildReserve holds this much back from the unit
        // spender, so the balance is allowed to climb to it — see that method for why the multiple
        // meant the commander only ever built mines.
        float wanted = GetEnemyBuildReserve(hq);
        if (wanted > 0f && hq.factionFunds >= wanted && TryBuildEnemyEconomy(hq))
        {
            return;
        }

        int dockLevel = GetNavalDockLevel(hq);

        if (dockLevel > 0 && dockLevel < MaxLevel && TryUpgradeEnemyNavalDock(hq))
        {
            return;
        }

        foreach (KeyValuePair<Unit, int> entry in mineLevels)
        {
            if (entry.Value >= MaxLevel
                || entry.Key == null
                || entry.Key.disabled
                || entry.Key.NetworkHQ != hq)
            {
                continue;
            }

            float cost = GetMineUpgradeCost(entry.Value);
            if (hq.factionFunds < cost * EnemyReserveMultiple)
            {
                continue;
            }

            hq.AddFunds(-cost);
            mineLevels[entry.Key] = entry.Value + 1;
            CommanderAiLog.Note(hq, $"upgraded a gold mine to level {entry.Value + 1}.");
            return;
        }

        TryUpgradeEnemyFactory(hq);
    }

    private void TryUpgradeEnemyFactory(FactionHQ hq)
    {
        Factory[] factories = UnityEngine.Object.FindObjectsOfType<Factory>();
        for (int i = 0; i < factories.Length; i++)
        {
            Factory factory = factories[i];
            Unit? attached = factory == null ? null : factory.attachedUnit;
            if (factory == null
                || attached == null
                || attached.disabled
                || attached.NetworkHQ != hq
                || factory.ProductionUnit == null)
            {
                continue;
            }

            int level = GetFactoryLevel(factory);
            float cost = GetFactoryUpgradeCost(level);
            if (level >= MaxLevel || hq.factionFunds < cost * EnemyReserveMultiple)
            {
                continue;
            }

            hq.AddFunds(-cost);
            factoryLevels[attached] = level + 1;
            CommanderAiLog.Note(
                hq, $"upgraded {CommanderGameAccess.GetUnitLabel(attached)} to level {level + 1}.");
            return;
        }
    }

    /// <summary>
    /// What this commander is saving up for, or 0 when its economy is finished. Two spenders share
    /// one <c>factionFunds</c> pool — this service and <see cref="CommanderEnemyCommanderService"/>
    /// — and the unit spender takes a fixed fraction of the balance every review, so the balance
    /// never climbed to a factory's price: the enemy built its opening mines out of the duel head
    /// start and then bought convoys with everything for the rest of the match. The unit spender
    /// now subtracts this from the pot it may touch, which is the same "save for it" fix
    /// <c>AccrueFund</c> is for airframes and hulls, and the build gates below ask for the plain
    /// price instead of a multiple of it, because a reserved price is already protected. A mine is
    /// only wanted while a resource site is in reach; short of one, the commander saves for the
    /// next thing instead of piling funds toward a mine it cannot site.
    /// </summary>
    internal static float GetEnemyBuildReserve(FactionHQ hq)
    {
        CommanderEconomyService? service = Instance;
        if (service == null || hq == null || hq.faction == null)
        {
            return 0f;
        }

        // A base that cannot see the attack coming outranks income. Without a radar building the
        // commander's tracking database holds only what its parked units happen to see, and the
        // whole defence posture reads that database.
        BuildingDefinition? radar = service.ResolveCategoryDefinition(BuildingType.RDR, preferDearest: true);
        if (radar != null && TryGetUncoveredBase(hq, BuildingType.RDR, RadarCoverageMeters, out _))
        {
            return GetStructureCost(radar);
        }

        bool duel = CommanderEnemyCommanderService.IsDuelMission;
        if (service.CountMines(hq) < (duel ? DuelEnemyMineTarget : EnemyMineTarget)
            && CommanderStrategicPointService.Instance?.TryPickFreeReachableSite(hq, out GlobalPosition _) == true)
        {
            return MineBuildCost;
        }

        if (service.CountFactories(hq) < (duel ? DuelEnemyFactoryTarget : EnemyFactoryTarget))
        {
            return FactoryBuildCost;
        }

        BuildingDefinition? defence = service.ResolveCategoryDefinition(BuildingType.DEF, preferDearest: false);
        if (defence != null && CountBuildings(hq, BuildingType.DEF) < EnemyDefenceBuildingTarget)
        {
            return GetStructureCost(defence);
        }

        // A commander whose map gave it no coast hands the money back rather than reserving for a
        // dock it can never site — the same reason AccrueFund caps the air and naval funds.
        return GetNavalDockLevel(hq) <= 0 && !service.shoreSearchReported.Contains(hq)
            ? NavalDockBuildCost
            : 0f;
    }

    /// <summary>Builds whatever <see cref="GetEnemyBuildReserve"/> said this commander wants next.</summary>
    private bool TryBuildEnemyEconomy(FactionHQ hq)
    {
        // Same order as GetEnemyBuildReserve, because that method is what saved up for this one.
        if (TryBuildEnemyRadar(hq))
        {
            return true;
        }

        bool duel = CommanderEnemyCommanderService.IsDuelMission;
        if (CountMines(hq) < (duel ? DuelEnemyMineTarget : EnemyMineTarget)
            && CommanderStrategicPointService.Instance?.TryPickFreeReachableSite(hq, out GlobalPosition _) == true)
        {
            return TryBuildEnemyMine(hq);
        }

        if (CountFactories(hq) < (duel ? DuelEnemyFactoryTarget : EnemyFactoryTarget))
        {
            return TryBuildEnemyFactory(hq);
        }

        if (TryBuildEnemyDefence(hq))
        {
            return true;
        }

        return TryBuildEnemyNavalDock(hq);
    }

    /// <summary>
    /// One radar building per base, rebuilt when the last one is bombed. Sited beside the base it
    /// covers rather than wherever a dart lands: the thing being answered is "this base has no
    /// radar", and a second mast next to the first one answers nothing.
    /// </summary>
    private bool TryBuildEnemyRadar(FactionHQ hq)
    {
        BuildingDefinition? radar = ResolveCategoryDefinition(BuildingType.RDR, preferDearest: true);
        if (radar == null
            || !TryGetUncoveredBase(hq, BuildingType.RDR, RadarCoverageMeters, out GlobalPosition anchor)
            || !TryFindSiteNear(hq, radar, anchor, out GlobalPosition site)
            || SpawnBuilding(hq, site, radar, GetStructureLabel(radar), randomRotation: true) == null)
        {
            return false;
        }

        hq.AddFunds(-GetStructureCost(radar));
        CommanderAiLog.Note(hq, $"built a {GetStructureLabel(radar)} at a base with no radar cover.");
        return true;
    }

    /// <summary>Hardens a base with whatever the encyclopedia files under DEFENCE.</summary>
    private bool TryBuildEnemyDefence(FactionHQ hq)
    {
        BuildingDefinition? defence = ResolveCategoryDefinition(BuildingType.DEF, preferDearest: false);
        if (defence == null || CountBuildings(hq, BuildingType.DEF) >= EnemyDefenceBuildingTarget)
        {
            return false;
        }

        GlobalPosition site = default;
        if (!TryFindEnemyBuildSite(hq, defence, ref site)
            || SpawnBuilding(hq, site, defence, GetStructureLabel(defence), randomRotation: true) == null)
        {
            return false;
        }

        hq.AddFunds(-GetStructureCost(defence));
        CommanderAiLog.Note(hq, $"built a {GetStructureLabel(defence)} to defend its base.");
        return true;
    }

    /// <summary>
    /// The structure a commander uses for a whole building category, resolved off the live
    /// encyclopedia rather than a name table: <c>BuildingType</c> is authored in the game's own
    /// assets, so a patch that adds a radar mast files itself and a patch that removes one degrades
    /// to "no radar" instead of to a null prefab. Logged once, because a choice a game patch can
    /// invalidate should not be a silent one.
    /// </summary>
    private BuildingDefinition? ResolveCategoryDefinition(BuildingType type, bool preferDearest)
    {
        if (categoryDefinitions.TryGetValue(type, out BuildingDefinition? cached))
        {
            return cached;
        }

        IReadOnlyList<BuildingDefinition> entries = Catalog;
        if (entries.Count == 0)
        {
            // The encyclopedia is not up yet. Ask again next review rather than caching a null.
            return null;
        }

        BuildingDefinition? best = null;
        for (int i = 0; i < entries.Count; i++)
        {
            BuildingDefinition candidate = entries[i];
            if (candidate.buildingType != type)
            {
                continue;
            }

            if (best == null || (preferDearest ? candidate.value > best.value : candidate.value < best.value))
            {
                best = candidate;
            }
        }

        categoryDefinitions[type] = best;
        CommanderPlugin.Log.LogInfo(best == null
            ? $"No {GetCategoryLabel(type)} structure in the encyclopedia, so no commander will build one."
            : $"Commanders will build {GetStructureLabel(best)} for {GetCategoryLabel(type)} "
                + $"({GetStructureCost(best):0}).");
        return best;
    }

    /// <summary>The first base this faction holds with no building of that category near it.</summary>
    private static bool TryGetUncoveredBase(
        FactionHQ hq,
        BuildingType type,
        float coverage,
        out GlobalPosition center)
    {
        center = default;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            GlobalPosition candidate = airbase.center.GlobalPosition();
            if (!HasBuildingNear(hq, type, candidate, coverage))
            {
                center = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool HasBuildingNear(FactionHQ hq, BuildingType type, GlobalPosition position, float radius)
    {
        if (hq.factionUnits == null)
        {
            return false;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit != null
                && !unit.disabled
                && unit is Building
                && unit.definition is BuildingDefinition definition
                && definition.buildingType == type
                && FastMath.InRange(unit.transform.GlobalPosition(), position, radius))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountBuildings(FactionHQ hq, BuildingType type)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit != null
                && !unit.disabled
                && unit is Building
                && unit.definition is BuildingDefinition definition
                && definition.buildingType == type)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>A legal site on a ring around one point, through the same shared rule as every
    /// other build in the mod.</summary>
    private bool TryFindSiteNear(
        FactionHQ hq,
        BuildingDefinition definition,
        GlobalPosition anchor,
        out GlobalPosition site)
    {
        for (int attempt = 0; attempt < EnemySiteAttempts; attempt++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(BaseSiteMinMeters, BaseSiteMaxMeters);
            GlobalPosition candidate = new(
                anchor.x + Mathf.Cos(angle) * distance,
                anchor.y,
                anchor.z + Mathf.Sin(angle) * distance);
            if (preview.IsSiteAllowed(definition, candidate, hq, out _))
            {
                site = candidate;
                return true;
            }
        }

        site = default;
        return false;
    }

    /// <summary>Builds on the nearest free resource site this commander can reach.</summary>
    private bool TryBuildEnemyMine(FactionHQ hq)
    {
        CommanderStrategicPointService? pointService = CommanderStrategicPointService.Instance;
        if (pointService == null || !pointService.HasResourceSites)
        {
            // No sites on this map (yet): the pre-points rule, a spot beside something the faction
            // owns. Same fallback the player's ghost takes in CommanderBuildPreview.Evaluate.
            GlobalPosition fallback = default;
            BuildingDefinition? mine = ResolveDefinition(CommanderBuildKind.Mine);
            if (!TryFindEnemyBuildSite(hq, mine, ref fallback) || SpawnMine(hq, fallback, randomRotation: true) == null)
            {
                return false;
            }

            hq.AddFunds(-MineBuildCost);
            CommanderAiLog.Note(hq, "built a gold mine (no resource sites on this map).");
            return true;
        }

        if (!pointService.TryPickFreeReachableSite(hq, out CommanderStrategicPoint point)
            || SpawnMine(hq, point.Position, randomRotation: true) == null)
        {
            return false;
        }

        hq.AddFunds(-MineBuildCost);
        CommanderAiLog.Note(hq, $"built a gold mine on {point.Label}.");
        return true;
    }

    /// <summary>The enemy's first factory, producing something its own faction fields.</summary>
    private bool TryBuildEnemyFactory(FactionHQ hq)
    {
        CommanderGameAccess.CollectFactionVehicleDefinitions(enemyProductionOptions, hq);
        if (enemyProductionOptions.Count == 0)
        {
            return false;
        }

        VehicleDefinition production = enemyProductionOptions[Random.Range(0, enemyProductionOptions.Count)];
        GlobalPosition site = default;
        BuildingDefinition? plant = ResolveDefinition(CommanderBuildKind.Factory);
        if (!TryFindEnemyBuildSite(hq, plant, ref site) || SpawnFactory(hq, site, production, randomRotation: true) == null)
        {
            return false;
        }

        hq.AddFunds(-FactoryBuildCost);
        CommanderAiLog.Note(hq, $"built a {production.unitName} factory.");
        return true;
    }

    /// <summary>
    /// Puts the enemy's dock on the coast nearest a base it holds. The search starts from the map's
    /// own sea lanes rather than throwing darts at the ground: a sea lane point is water by
    /// definition, so walking inland from one until the terrain climbs above the sea plane finds the
    /// shoreline in a handful of probes. Darts would have to sample the whole build radius to find
    /// the coast at all, and most maps are mostly land.
    /// </summary>
    private bool TryBuildEnemyNavalDock(FactionHQ hq)
    {
        BuildingDefinition? dock = ResolveDefinition(CommanderBuildKind.NavalDock);
        if (dock == null || !TryFindShoreSite(hq, dock, out GlobalPosition site))
        {
            // Say it once. An enemy with no navy on a map whose coast it cannot reach looks exactly
            // like an enemy that forgot to build one, and only the console can tell them apart.
            if (dock != null && shoreSearchReported.Add(hq))
            {
                CommanderAiLog.Note(
                    hq,
                    $"found no shoreline within {CommanderSettings.NavalDockRadiusKm:0.#} km of a base it holds, "
                        + "so it has no navy. Raise Economy/NavalDockRadiusKm if this map keeps its coast further out.");
            }

            return false;
        }

        if (SpawnNavalDock(hq, site, randomRotation: true) == null)
        {
            return false;
        }

        hq.AddFunds(-NavalDockBuildCost);
        CommanderAiLog.Note(hq, "built a naval dock.");
        return true;
    }

    private bool TryUpgradeEnemyNavalDock(FactionHQ hq)
    {
        foreach (KeyValuePair<Unit, int> entry in dockLevels)
        {
            if (entry.Value >= MaxLevel
                || entry.Key == null
                || entry.Key.disabled
                || entry.Key.NetworkHQ != hq)
            {
                continue;
            }

            float cost = GetNavalDockUpgradeCost(entry.Value);
            if (hq.factionFunds < cost * EnemyReserveMultiple)
            {
                continue;
            }

            hq.AddFunds(-cost);
            dockLevels[entry.Key] = entry.Value + 1;
            CommanderAiLog.Note(
                hq,
                $"upgraded its naval dock to level {entry.Value + 1}: "
                    + $"{CommanderNavalPurchaseService.GetLevelUnlockLabel(entry.Value + 1)}.");
            return true;
        }

        return false;
    }

    private bool TryFindShoreSite(FactionHQ hq, BuildingDefinition dock, out GlobalPosition site)
    {
        site = default;
        RoadNetwork? seaLanes = NetworkSceneSingleton<LevelInfo>.i?.seaLanes;
        if (seaLanes == null || !seaLanes.Exists())
        {
            return false;
        }

        float reach = Mathf.Max(CommanderSettings.NavalDockRadiusKm, 0.1f) * 1000f;
        int examined = 0;
        foreach (Road lane in seaLanes.roads)
        {
            if (lane?.points == null)
            {
                continue;
            }

            for (int i = 0; i < lane.points.Count; i += ShoreLanePointStride)
            {
                if (examined >= ShoreLanePointBudget)
                {
                    return false;
                }

                GlobalPosition water = lane.points[i];
                if (!TryGetNearestBase(hq, water, reach, out GlobalPosition anchor))
                {
                    continue;
                }

                examined++;
                Vector3 inland = anchor.AsVector3() - water.AsVector3();
                inland.y = 0f;
                if (inland.sqrMagnitude < 1f)
                {
                    continue;
                }

                inland.Normalize();
                for (int step = 1; step <= ShoreWalkSteps; step++)
                {
                    Vector3 probe = water.AsVector3() + inland * (step * ShoreWalkStepMeters);
                    GlobalPosition candidate = new(probe.x, probe.y, probe.z);
                    if (CommanderGameAccess.IsBelowSeaLevel(CommanderGameAccess.SnapToTerrain(candidate)))
                    {
                        continue;
                    }

                    // Above the waterline. The shared site rule has the final say, exactly as it
                    // does for the player's ghost.
                    if (preview.IsSiteAllowed(dock, candidate, hq, out _))
                    {
                        site = candidate;
                        return true;
                    }

                    break;
                }
            }
        }

        return false;
    }

    private static bool TryGetNearestBase(FactionHQ hq, GlobalPosition from, float reach, out GlobalPosition anchor)
    {
        anchor = default;
        float best = reach;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            GlobalPosition center = airbase.center.GlobalPosition();
            float distance = FastMath.Distance(from, center);
            if (distance <= best)
            {
                best = distance;
                anchor = center;
            }
        }

        return best < reach;
    }

    private int CountFactories(FactionHQ hq)
    {
        int count = 0;
        Factory[] factories = UnityEngine.Object.FindObjectsOfType<Factory>();
        for (int i = 0; i < factories.Length; i++)
        {
            Unit? attached = factories[i] == null ? null : factories[i].attachedUnit;
            if (attached != null && !attached.disabled && attached.NetworkHQ == hq)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// A spot beside something that faction already owns, so a build lands in its own rear. Each
    /// candidate is put through the same <see cref="CommanderBuildPreview.IsSiteAllowed"/> the
    /// player's ghost uses, so the enemy cannot drop a factory across the highway or inside
    /// another building — which is what left its own convoys stuck and its aircraft taxiing into
    /// walls. Several tries, because one throw of the dart lands on a road often enough.
    /// </summary>
    private bool TryFindEnemyBuildSite(FactionHQ hq, BuildingDefinition? definition, ref GlobalPosition site)
    {
        for (int attempt = 0; attempt < EnemySiteAttempts; attempt++)
        {
            if (!TryPickEnemyBuildSite(hq, ref site))
            {
                return false;
            }

            if (definition == null || preview.IsSiteAllowed(definition, site, hq, out _))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryPickEnemyBuildSite(FactionHQ hq, ref GlobalPosition site)
    {
        if (hq.factionUnits == null)
        {
            return false;
        }

        Unit? anchor = null;
        int seen = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit == null || unit.disabled || unit is not Building)
            {
                continue;
            }

            // Reservoir sample so builds spread across the faction's buildings instead of always
            // stacking on whichever one happens to be first in the list.
            seen++;
            if (anchor == null || Random.Range(0, seen) == 0)
            {
                anchor = unit;
            }
        }

        if (anchor == null)
        {
            return false;
        }

        float angle = Random.Range(0f, Mathf.PI * 2f);
        float distance = Random.Range(150f, 350f);
        GlobalPosition anchorPosition = anchor.transform.GlobalPosition();
        site = new GlobalPosition(
            anchorPosition.x + Mathf.Cos(angle) * distance,
            anchorPosition.y,
            anchorPosition.z + Mathf.Sin(angle) * distance);
        return true;
    }
}
