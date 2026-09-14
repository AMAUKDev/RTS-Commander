using System.Collections.Generic;
using RoadPathfinding;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Runs once per mission, host only, one state advanced per <c>TickPersistent</c> call — the SAM
/// analyzer's own Waiting/Sampling/Coverage shape
/// (<see cref="CommanderSamSiteAnalyzerService"/>). Finds every resource site,
/// village and hilltop and appends every airbase, then never runs again until the next
/// <c>ResetSession</c>.
/// </summary>
internal sealed partial class CommanderStrategicPointService
{
    /// <summary>
    /// Slope the discovery grid-fill treats as buildable ground. A normal's Y component of 1 is
    /// dead flat; 0.94 is about 20 degrees, loose enough that a gentle rise still gets a fill site
    /// but steep enough that a hillside never does.
    /// </summary>
    private const float FlatNormalY = 0.94f;

    /// <summary>
    /// Grid cells the fill pass walks per <c>TickPersistent</c> call. Each cell tries up to
    /// <see cref="CommanderEconomyService.EnemySiteAttempts"/> random probes and each probe is a
    /// handful of height-map reads plus (on a pass) one <c>Evaluate</c> physics overlap, so four
    /// cells is a bounded slice of a frame rather than a fixed raycast count.
    /// </summary>
    private const int FillCellsPerFrame = 4;

    /// <summary>
    /// A resource site has no garrison ring — this is only the footprint the marker and the
    /// selection card measure against, so it does not need a config entry of its own.
    /// </summary>
    private const float SiteRadiusMeters = 50f;

    private Building[]? industryBuildings;
    private bool sitesExistingIndustryDone;
    private int fillCellIndex;
    private int fillColumns;
    private int fillRows;
    private Vector2 mapSize;
    private float discoveryStartedAt;

    private readonly List<CommanderStrategicPoint> siteCandidates = new();
    private readonly List<string> droppedSiteBuildingLabels = new();

    /// <summary>
    /// Discovery runs again if it found no resource sites at all. A map with no sites is a map
    /// where nobody can build a mine, and the one time it happened in play the cause was a
    /// hot-reload race (the pass ran in 0.0 s against a height map that was not really there yet).
    /// Three tries, half a minute apart, before the result is accepted as "this map has none".
    /// </summary>
    private const int MaxDiscoveryAttempts = 3;
    private const float DiscoveryRetrySeconds = 30f;
    private int discoveryAttempts;
    private float discoveryRetryAt;

    private void StepWaiting()
    {
        // The strategic height map is baked by the SAM analyzer
        // (SamSites/Analysis/CommanderSamSiteAnalyzerSampling.cs:TryStart); this service never
        // starts a bake of its own, only waits for the one already running.
        if (!MissionManager.IsRunning
            || Time.realtimeSinceStartup < discoveryRetryAt
            || !CommanderSamSiteAnalyzerService.TryGetStrategicHeightMapSize(out Vector2 size)
            || CommanderEconomyService.Instance?.MineDefinition == null)
        {
            return;
        }

        mapSize = size;
        discoveryStartedAt = Time.realtimeSinceStartup;
        discoveryAttempts++;
        discovery = DiscoveryState.Sites;
    }

    /// <summary>Throws away a finished pass and queues another one. Everything the passes write
    /// is reset here; the service-level state (<c>points</c>, focus, log) is cleared alongside.</summary>
    private void RestartDiscovery()
    {
        points.Clear();
        siteCandidates.Clear();
        droppedSiteBuildingLabels.Clear();
        villageCandidates.Clear();
        hilltopCandidates.Clear();
        outpostCandidates.Clear();
        crossroadsCandidates.Clear();
        roadsideCandidates.Clear();
        roadPointLists.Clear();
        junctionNodes.Clear();
        roadsIndex = 0;
        roadsSub = RoadsSubState.CollectAndMergeEndpoints;
        industryBuildings = null;
        sitesExistingIndustryDone = false;
        fillCellIndex = 0;
        hilltopIndex = 0;
        discoveryRetryAt = Time.realtimeSinceStartup + DiscoveryRetrySeconds;
        discovery = DiscoveryState.Waiting;
    }

    private void StepSites()
    {
        CommanderEconomyService? economy = CommanderEconomyService.Instance;
        BuildingDefinition? mineDefinition = economy?.MineDefinition;
        if (economy == null || mineDefinition == null)
        {
            return;
        }

        if (!sitesExistingIndustryDone)
        {
            StepExistingIndustrySites(economy, mineDefinition);
            fillColumns = Mathf.Max(1, Mathf.CeilToInt(mapSize.x / CommanderSettings.PointsFillGridMeters));
            fillRows = Mathf.Max(1, Mathf.CeilToInt(mapSize.y / CommanderSettings.PointsFillGridMeters));
            fillCellIndex = 0;
            sitesExistingIndustryDone = true;
            return;
        }

        int totalCells = fillColumns * fillRows;
        int end = Mathf.Min(fillCellIndex + FillCellsPerFrame, totalCells);
        for (; fillCellIndex < end; fillCellIndex++)
        {
            StepFillCell(economy, mineDefinition, fillCellIndex);
        }

        if (fillCellIndex < totalCells)
        {
            return;
        }

        // Existing-industry sites were added first, so a spacing conflict always drops the
        // generated fill site and keeps the real building's site.
        ApplySpacing(
            siteCandidates,
            CommanderSettings.PointsSiteMinSpacingMeters,
            CollectAirbaseCentres(),
            CommanderSettings.PointsAirbaseExclusionMeters,
            CommanderSettings.PointsMaxResourceSites);
        for (int i = 0; i < siteCandidates.Count; i++)
        {
            siteCandidates[i].Label = $"RESOURCE SITE {i + 1}";
            points.Add(siteCandidates[i]);
        }

        discovery = DiscoveryState.Villages;
    }

    /// <summary>Departure 1: a site anchored on an existing industrial building sits in a ring
    /// beside it, never on top of it — <c>Evaluate</c> refuses any footprint over a live unit.</summary>
    private void StepExistingIndustrySites(CommanderEconomyService economy, BuildingDefinition mineDefinition)
    {
        industryBuildings = Object.FindObjectsOfType<Building>();
        for (int i = 0; i < industryBuildings.Length; i++)
        {
            Building building = industryBuildings[i];
            if (building == null
                || building.disabled
                || building.definition is not BuildingDefinition d
                || (d.buildingType != BuildingType.FAC && d.buildingType != BuildingType.AMMO)
                || CommanderEconomyService.IsCommanderBuilt(building))
            {
                continue;
            }

            if (TryFindSiteRing(economy, mineDefinition, building.transform.GlobalPosition(), out GlobalPosition site))
            {
                siteCandidates.Add(new CommanderStrategicPoint(StrategicPointKind.Site, site, SiteRadiusMeters, string.Empty));
            }
            else
            {
                droppedSiteBuildingLabels.Add(CommanderGameAccess.GetUnitLabel(building));
            }
        }
    }

    /// <summary>
    /// The same 150-350 m ring <c>TryPickEnemyBuildSite</c> throws around an owned building
    /// (<c>Economy/CommanderEconomyServiceEnemy.cs</c>), tried up to
    /// <see cref="CommanderEconomyService.EnemySiteAttempts"/> times for the first candidate that
    /// passes the one siting rule with no faction attached (radius check off, per
    /// <c>CommanderBuildPreview.Evaluate</c>'s <c>hq == null</c> branch) and is not underwater.
    /// </summary>
    private static bool TryFindSiteRing(
        CommanderEconomyService economy, BuildingDefinition mineDefinition, GlobalPosition anchor, out GlobalPosition site)
    {
        for (int attempt = 0; attempt < CommanderEconomyService.EnemySiteAttempts; attempt++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(150f, 350f);
            GlobalPosition candidate = new(
                anchor.x + Mathf.Cos(angle) * distance,
                anchor.y,
                anchor.z + Mathf.Sin(angle) * distance);
            if (!economy.IsSiteAllowed(mineDefinition, candidate, null, out _))
            {
                continue;
            }

            GlobalPosition snapped = CommanderGameAccess.SnapToTerrain(candidate);
            if (CommanderGameAccess.IsBelowSeaLevel(snapped))
            {
                continue;
            }

            site = snapped;
            return true;
        }

        site = default;
        return false;
    }

    /// <summary>One grid cell of the fill pass: skip a cell an existing site already sits in, else
    /// probe it up to <see cref="CommanderEconomyService.EnemySiteAttempts"/> times for flat, clear,
    /// off-road ground.</summary>
    private void StepFillCell(CommanderEconomyService economy, BuildingDefinition mineDefinition, int cellIndex)
    {
        float grid = CommanderSettings.PointsFillGridMeters;
        int cellX = cellIndex % fillColumns;
        int cellZ = cellIndex / fillColumns;
        float minX = -mapSize.x * 0.5f + cellX * grid;
        float minZ = -mapSize.y * 0.5f + cellZ * grid;
        float maxX = Mathf.Min(minX + grid, mapSize.x * 0.5f);
        float maxZ = Mathf.Min(minZ + grid, mapSize.y * 0.5f);

        for (int i = 0; i < siteCandidates.Count; i++)
        {
            GlobalPosition existing = siteCandidates[i].Position;
            if (existing.x >= minX && existing.x < maxX && existing.z >= minZ && existing.z < maxZ)
            {
                return;
            }
        }

        for (int attempt = 0; attempt < CommanderEconomyService.EnemySiteAttempts; attempt++)
        {
            float x = Random.Range(minX, maxX);
            float z = Random.Range(minZ, maxZ);
            if (!CommanderSamSiteAnalyzerService.TryGetStrategicTerrainHeight(x, z, out float height) || height <= 1f)
            {
                continue;
            }

            if (CommanderSamSiteAnalyzerService.EstimateStrategicTerrainNormalY(x, z, 40f) < FlatNormalY)
            {
                continue;
            }

            GlobalPosition candidate = new(x, height, z);
            if (!economy.IsSiteAllowed(mineDefinition, candidate, null, out _))
            {
                continue;
            }

            siteCandidates.Add(new CommanderStrategicPoint(StrategicPointKind.Site, candidate, SiteRadiusMeters, string.Empty));
            return;
        }
    }

    private static List<GlobalPosition> CollectAirbaseCentres()
    {
        List<GlobalPosition> centres = new();
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase != null && !airbase.disabled && airbase.center != null)
            {
                centres.Add(airbase.center.GlobalPosition());
            }
        }

        return centres;
    }

    /// <summary>
    /// Height-map sample grid cells the hilltop scan walks per <c>TickPersistent</c> call. Each
    /// sample is 9 height-map reads (the point plus an 8-point ring) and no raycasts, so this is
    /// far cheaper per-cell than the site fill: a 200 km-square map is about 40 000 samples, so
    /// roughly 160 frames, about 3 s at 60 fps.
    /// </summary>
    private const int HilltopSamplesPerFrame = 256;

    /// <summary>Compass points (plus diagonals) the hilltop scan rings a candidate with, widened
    /// from <c>SampleAverageHeight</c>'s 4-point cross so a ridge running exactly N-S or E-W cannot
    /// hide its own prominence from the two points that would have caught it.</summary>
    private const int HilltopRingSamples = 8;

    private readonly List<CommanderStrategicPoint> villageCandidates = new();
    private readonly List<CommanderStrategicPoint> hilltopCandidates = new();

    /// <summary>A civilian cluster too small to qualify as a village (design §1: 1 or 2 buildings
    /// at the default <c>VillageMinBuildings = 3</c>) becomes an outpost candidate instead of being
    /// dropped — flat farmland otherwise has nothing at all near its farmsteads.</summary>
    private readonly List<CommanderStrategicPoint> outpostCandidates = new();

    /// <summary>Counts before spacing and caps, for the results line: they tell a threshold that
    /// found nothing apart from a cap that threw everything away.</summary>
    private int candidateVillages;
    private int candidateHilltops;
    private int candidateOutposts;
    private int candidateCrossroads;
    private int candidateRoadside;
    private int civilianBuildingCount;
    private int hilltopIndex;
    private int hilltopColumns;
    private int hilltopRows;

    private void StepVillages()
    {
        List<GlobalPosition> members = new();
        if (industryBuildings != null)
        {
            for (int i = 0; i < industryBuildings.Length; i++)
            {
                Building building = industryBuildings[i];
                if (building == null
                    || building.disabled
                    || building.definition is not BuildingDefinition d
                    || d.buildingType != BuildingType.CIV
                    || CommanderEconomyService.IsCommanderBuilt(building))
                {
                    continue;
                }

                members.Add(building.transform.GlobalPosition());
            }
        }

        // Single-link clustering over a few hundred buildings at most: an O(n^2) union-find pass,
        // once, is cheaper to write and to verify than any spatial index would be worth here.
        int n = members.Count;
        civilianBuildingCount = n;
        int[] parent = new int[n];
        for (int i = 0; i < n; i++)
        {
            parent[i] = i;
        }

        float clusterMeters = CommanderSettings.PointsVillageClusterMeters;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (HorizontalDistance(members[i], members[j]) <= clusterMeters)
                {
                    UnionClusters(parent, i, j);
                }
            }
        }

        Dictionary<int, List<int>> clusters = new();
        for (int i = 0; i < n; i++)
        {
            int root = FindCluster(parent, i);
            if (!clusters.TryGetValue(root, out List<int> indices))
            {
                indices = new List<int>();
                clusters[root] = indices;
            }

            indices.Add(i);
        }

        foreach (List<int> indices in clusters.Values)
        {
            float sumX = 0f, sumY = 0f, sumZ = 0f;
            for (int i = 0; i < indices.Count; i++)
            {
                GlobalPosition p = members[indices[i]];
                sumX += p.x;
                sumY += p.y;
                sumZ += p.z;
            }

            GlobalPosition centroid = new(sumX / indices.Count, sumY / indices.Count, sumZ / indices.Count);
            GlobalPosition snapped = CommanderGameAccess.SnapToTerrain(centroid);
            if (QualifiesAsVillage(indices.Count, CommanderSettings.PointsVillageMinBuildings))
            {
                villageCandidates.Add(new CommanderStrategicPoint(
                    StrategicPointKind.Village, snapped, CommanderSettings.PointsVillageRadiusMeters, string.Empty));
            }
            else
            {
                // Too small for a village, but still a cluster of real buildings — an outpost
                // rather than nothing at all (design §1, 2026-09-13 add).
                outpostCandidates.Add(new CommanderStrategicPoint(
                    StrategicPointKind.Outpost, snapped, CommanderSettings.PointsOutpostRadiusMeters, string.Empty));
            }
        }

        discovery = DiscoveryState.Hilltops;
    }

    private static int FindCluster(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }

        return i;
    }

    private static void UnionClusters(int[] parent, int a, int b)
    {
        int rootA = FindCluster(parent, a);
        int rootB = FindCluster(parent, b);
        if (rootA != rootB)
        {
            parent[rootA] = rootB;
        }
    }

    private void StepHilltops()
    {
        if (hilltopColumns == 0)
        {
            hilltopColumns = Mathf.Max(1, Mathf.CeilToInt(mapSize.x / CommanderSettings.PointsHilltopGridMeters));
            hilltopRows = Mathf.Max(1, Mathf.CeilToInt(mapSize.y / CommanderSettings.PointsHilltopGridMeters));
            hilltopIndex = 0;
        }

        int total = hilltopColumns * hilltopRows;
        int end = Mathf.Min(hilltopIndex + HilltopSamplesPerFrame, total);
        for (; hilltopIndex < end; hilltopIndex++)
        {
            StepHilltopSample(hilltopIndex);
        }

        if (hilltopIndex < total)
        {
            return;
        }

        discovery = DiscoveryState.Roads;
    }

    private void StepHilltopSample(int index)
    {
        float grid = CommanderSettings.PointsHilltopGridMeters;
        int cellX = index % hilltopColumns;
        int cellZ = index / hilltopColumns;
        float x = -mapSize.x * 0.5f + (cellX + 0.5f) * grid;
        float z = -mapSize.y * 0.5f + (cellZ + 0.5f) * grid;

        if (!CommanderSamSiteAnalyzerService.TryGetStrategicTerrainHeight(x, z, out float height) || height <= 1f)
        {
            return;
        }

        float ring = CommanderSettings.PointsHilltopRingMeters;
        float ringSum = 0f;
        int ringCount = 0;
        bool isHighestInRing = true;
        for (int i = 0; i < HilltopRingSamples; i++)
        {
            float angle = i * (Mathf.PI * 2f / HilltopRingSamples);
            if (!CommanderSamSiteAnalyzerService.TryGetStrategicTerrainHeight(
                    x + Mathf.Cos(angle) * ring, z + Mathf.Sin(angle) * ring, out float ringHeight))
            {
                continue;
            }

            ringSum += ringHeight;
            ringCount++;
            if (ringHeight > height)
            {
                isHighestInRing = false;
            }
        }

        float ringMean = ringCount == 0 ? height : ringSum / ringCount;
        if (!QualifiesAsHilltop(height, ringMean, CommanderSettings.PointsHilltopProminenceMeters, isHighestInRing))
        {
            return;
        }

        // The grid sample only says "there is a hill about here". Climb to its actual top before
        // recording it, or the marker lands wherever the 1 km grid happened to fall — half way up
        // the slope, in play.
        ClimbToPeak(ref x, ref z, ref height);

        GlobalPosition candidate = new(x, height, z);
        if (IsInsideAnyAirbase(candidate) || IsNearAnyVillage(candidate))
        {
            return;
        }

        // Several grid samples on one hill all climb to the same summit; the spacing pass merges
        // them, but an exact duplicate costs it nothing to skip here.
        for (int i = 0; i < hilltopCandidates.Count; i++)
        {
            if (HorizontalDistance(hilltopCandidates[i].Position, candidate) < PeakMergeMeters)
            {
                return;
            }
        }

        GlobalPosition snapped = CommanderGameAccess.SnapToTerrain(candidate);
        hilltopCandidates.Add(new CommanderStrategicPoint(
            StrategicPointKind.Hilltop, snapped, CommanderSettings.PointsHilltopRadiusMeters, string.Empty));
    }

    /// <summary>First step of the summit climb: an eighth of the sample grid, so the climb can
    /// cross the whole cell the sample came from in a handful of moves.</summary>
    private const float PeakClimbStartMeters = 125f;

    /// <summary>The climb stops refining below this step; the strategic height map is 20 m per
    /// pixel, so finer steps read the same pixel twice.</summary>
    private const float PeakClimbStopMeters = 20f;

    /// <summary>Upper bound on climb moves, so a long ridge cannot walk a sample across the map.</summary>
    private const int PeakClimbMaxSteps = 40;

    /// <summary>Two summits closer than this are one hill.</summary>
    private const float PeakMergeMeters = 200f;

    /// <summary>
    /// Steepest-ascent walk on the strategic height map: from the sample, step to the highest of
    /// eight neighbours while any is higher, halving the step when none is, until the step is
    /// below the height map's own resolution. Cheap (nine reads per move) and deterministic.
    /// </summary>
    private static void ClimbToPeak(ref float x, ref float z, ref float height)
    {
        float step = PeakClimbStartMeters;
        int moves = 0;
        while (step >= PeakClimbStopMeters && moves < PeakClimbMaxSteps)
        {
            float bestX = x;
            float bestZ = z;
            float bestHeight = height;
            for (int i = 0; i < 8; i++)
            {
                float angle = i * (Mathf.PI * 0.25f);
                float nx = x + Mathf.Cos(angle) * step;
                float nz = z + Mathf.Sin(angle) * step;
                if (CommanderSamSiteAnalyzerService.TryGetStrategicTerrainHeight(nx, nz, out float h) && h > bestHeight)
                {
                    bestX = nx;
                    bestZ = nz;
                    bestHeight = h;
                }
            }

            if (bestHeight > height)
            {
                x = bestX;
                z = bestZ;
                height = bestHeight;
                moves++;
            }
            else
            {
                step *= 0.5f;
            }
        }
    }

    private static bool IsInsideAnyAirbase(GlobalPosition position)
    {
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null || airbase.disabled || airbase.center == null || airbase.SavedAirbase == null)
            {
                continue;
            }

            if (FastMath.InRange(position, airbase.center.GlobalPosition(), airbase.SavedAirbase.CaptureRange))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsNearAnyVillage(GlobalPosition position)
    {
        float exclusion = CommanderSettings.PointsHilltopVillageExclusionMeters;
        for (int i = 0; i < villageCandidates.Count; i++)
        {
            if (HorizontalDistance(position, villageCandidates[i].Position) < exclusion)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Roads (or junction nodes, in the degree-computation phase) processed per <c>TickPersistent</c>
    /// call while discovering crossroads and roadside candidates — the same bounded-slice shape as
    /// <see cref="HilltopSamplesPerFrame"/>. A junction-degree lookup compares one node against
    /// every cached road, so this also bounds <see cref="StepRoadsComputeDegrees"/>'s per-node cost
    /// on a level with a large road network.
    /// </summary>
    private const int RoadsPerFrame = 20;

    /// <summary>Every road on the level's road network with at least two points, cached once per
    /// discovery pass so the degree-computation and roadside-walk phases do not re-read the network
    /// a second and third time. Only <c>roadNetwork</c> — sea lanes are a different network and
    /// carry no roadside traffic to speak of.</summary>
    private readonly List<List<GlobalPosition>> roadPointLists = new();

    /// <summary>Merged road-endpoint nodes, one per distinct junction (within
    /// <c>RoadJunctionMergeMeters</c>) — candidates for a crossroads once their degree is known.</summary>
    private readonly List<GlobalPosition> junctionNodes = new();

    private readonly List<CommanderStrategicPoint> crossroadsCandidates = new();
    private readonly List<CommanderStrategicPoint> roadsideCandidates = new();
    private readonly List<GlobalPosition> roadsideScratch = new();


    private enum RoadsSubState
    {
        CollectAndMergeEndpoints,
        ComputeDegrees,
        WalkRoadside,
    }

    private RoadsSubState roadsSub;
    private int roadsIndex;

    /// <summary>
    /// Third road-network pass (design §1, 2026-09-13 add), between the hilltop scan and bases: one
    /// road-collection sub-pass builds <see cref="roadPointLists"/> and <see cref="junctionNodes"/>,
    /// one computes each node's <see cref="JunctionDegree"/> and keeps the ones at or above
    /// <c>CrossroadsMinRoads</c> as crossroads candidates, and one walks every road emitting a
    /// roadside candidate every <c>RoadsideSpacingMeters</c>. A level with no road network (or an
    /// empty one) skips straight to bases.
    /// </summary>
    private void StepRoads()
    {
        RoadNetwork? network = NetworkSceneSingleton<LevelInfo>.i?.roadNetwork;
        if (network == null || !network.Exists())
        {
            discovery = DiscoveryState.Bases;
            return;
        }

        switch (roadsSub)
        {
            case RoadsSubState.CollectAndMergeEndpoints:
                StepRoadsCollect(network);
                break;
            case RoadsSubState.ComputeDegrees:
                StepRoadsComputeDegrees();
                break;
            case RoadsSubState.WalkRoadside:
                StepRoadsWalk();
                break;
        }
    }

    private void StepRoadsCollect(RoadNetwork network)
    {
        int end = Mathf.Min(roadsIndex + RoadsPerFrame, network.roads.Count);
        for (; roadsIndex < end; roadsIndex++)
        {
            Road road = network.roads[roadsIndex];
            if (road?.points == null || road.points.Count < 2)
            {
                continue;
            }

            List<GlobalPosition> points = new(road.points);
            roadPointLists.Add(points);
            MergeJunctionNode(points[0]);
            MergeJunctionNode(points[points.Count - 1]);
        }

        if (roadsIndex < network.roads.Count)
        {
            return;
        }

        roadsIndex = 0;
        roadsSub = RoadsSubState.ComputeDegrees;
    }

    /// <summary>Folds a road endpoint into an existing junction node within
    /// <c>RoadJunctionMergeMeters</c>, or starts a new one. Order-dependent (the first road through
    /// a junction plants the node every later road merges into), same as every other spacing/merge
    /// pass in this file.</summary>
    private void MergeJunctionNode(GlobalPosition endpoint)
    {
        float merge = CommanderSettings.PointsRoadJunctionMergeMeters;
        for (int i = 0; i < junctionNodes.Count; i++)
        {
            if (HorizontalDistance(junctionNodes[i], endpoint) <= merge)
            {
                return;
            }
        }

        junctionNodes.Add(endpoint);
    }

    private void StepRoadsComputeDegrees()
    {
        float merge = CommanderSettings.PointsRoadJunctionMergeMeters;
        int minRoads = CommanderSettings.PointsCrossroadsMinRoads;
        int end = Mathf.Min(roadsIndex + RoadsPerFrame, junctionNodes.Count);
        for (; roadsIndex < end; roadsIndex++)
        {
            GlobalPosition node = junctionNodes[roadsIndex];
            if (JunctionDegree(node, roadPointLists, merge) < minRoads)
            {
                continue;
            }

            GlobalPosition snapped = CommanderGameAccess.SnapToTerrain(node);
            crossroadsCandidates.Add(new CommanderStrategicPoint(
                StrategicPointKind.Crossroads, snapped, CommanderSettings.PointsCrossroadsRadiusMeters, string.Empty));
        }

        if (roadsIndex < junctionNodes.Count)
        {
            return;
        }

        roadsIndex = 0;
        roadsSub = RoadsSubState.WalkRoadside;
    }

    private void StepRoadsWalk()
    {
        int end = Mathf.Min(roadsIndex + RoadsPerFrame, roadPointLists.Count);
        for (; roadsIndex < end; roadsIndex++)
        {
            EmitRoadsidePoints(roadPointLists[roadsIndex], CommanderSettings.PointsRoadsideSpacingMeters, roadsideScratch);
            for (int i = 0; i < roadsideScratch.Count; i++)
            {
                TryAddRoadsideCandidate(roadsideScratch[i]);
            }
        }

        if (roadsIndex < roadPointLists.Count)
        {
            return;
        }

        discovery = DiscoveryState.Bases;
    }

    /// <summary>Numbers and appends one kind's accepted points, in the order the spacing pass kept them.</summary>
    private void AddLabelled(List<CommanderStrategicPoint> accepted, string kindLabel)
    {
        for (int i = 0; i < accepted.Count; i++)
        {
            accepted[i].Label = $"{kindLabel} {i + 1}";
            points.Add(accepted[i]);
        }
    }

    /// <summary>Skips a roadside candidate inside an airbase; otherwise snaps it to terrain and
    /// keeps it. Distance from crossroads is left to <see cref="ApplySpacing"/> against the
    /// crossroads actually kept: filtering against every junction candidate here threw away all but
    /// eight road points on a map with 263 junctions, most of which never became crossroads.</summary>
    private void TryAddRoadsideCandidate(GlobalPosition point)
    {
        if (IsInsideAnyAirbase(point))
        {
            return;
        }

        GlobalPosition snapped = CommanderGameAccess.SnapToTerrain(point);
        roadsideCandidates.Add(new CommanderStrategicPoint(
            StrategicPointKind.Roadside, snapped, CommanderSettings.PointsRoadsideRadiusMeters, string.Empty));
    }

    /// <summary>
    /// Junction degree at <paramref name="node"/>: one road counts once, whether it ends there
    /// (within <paramref name="mergeMeters"/> of one of its own endpoints) or merely passes through
    /// (a segment of it within <paramref name="mergeMeters"/>, checked only for a road that does not
    /// already end there). Pure, for the self-check: three roads meeting at one point is a
    /// crossroads candidate at the default <c>CrossroadsMinRoads = 3</c>; two is not.
    /// </summary>
    internal static int JunctionDegree(GlobalPosition node, IReadOnlyList<List<GlobalPosition>> roads, float mergeMeters)
    {
        int degree = 0;
        float mergeSquared = mergeMeters * mergeMeters;
        for (int r = 0; r < roads.Count; r++)
        {
            List<GlobalPosition> points = roads[r];
            if (points.Count < 2)
            {
                continue;
            }

            if (HorizontalDistance(points[0], node) <= mergeMeters
                || HorizontalDistance(points[points.Count - 1], node) <= mergeMeters)
            {
                degree++;
                continue;
            }

            for (int i = 1; i < points.Count; i++)
            {
                if (SegmentDistanceSquared(node, points[i - 1], points[i]) <= mergeSquared)
                {
                    degree++;
                    break;
                }
            }
        }

        return degree;
    }

    /// <summary>
    /// Points along a road's length at every <paramref name="spacingMeters"/>, arc length
    /// accumulated across the road's original points rather than endpoint-to-endpoint, so a curved
    /// road is measured the way it is actually driven. Pure, for the self-check: a 20 km straight
    /// road at 6 km spacing emits points at 6, 12 and 18 km — three, never a fourth past the road's
    /// own end.
    /// </summary>
    internal static void EmitRoadsidePoints(IReadOnlyList<GlobalPosition> points, float spacingMeters, List<GlobalPosition> result)
    {
        result.Clear();
        if (points.Count < 2 || spacingMeters <= 0f)
        {
            return;
        }

        // The road network is a graph: each road object is one edge between two junctions, and
        // most edges are shorter than the spacing. Walking marks along each edge alone gave eight
        // candidates on a map with 263 junctions. A short edge contributes its midpoint instead,
        // and the spacing pass thins the result to the configured density.
        float total = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            total += HorizontalDistance(points[i - 1], points[i]);
        }

        if (total < spacingMeters)
        {
            if (total >= MinRoadForMidpointMeters)
            {
                result.Add(PointAlong(points, total * 0.5f));
            }

            return;
        }

        float cumulative = 0f;
        float nextMark = spacingMeters;
        for (int i = 1; i < points.Count; i++)
        {
            GlobalPosition a = points[i - 1];
            GlobalPosition b = points[i];
            float segmentLength = HorizontalDistance(a, b);
            while (cumulative + segmentLength >= nextMark)
            {
                float t = segmentLength <= 0f ? 0f : (nextMark - cumulative) / segmentLength;
                result.Add(Lerp(a, b, t));
                nextMark += spacingMeters;
            }

            cumulative += segmentLength;
        }
    }

    /// <summary>A road shorter than this gets no midpoint: a 300 m spur between two junctions is
    /// not a stretch of road anyone holds, and its junctions are already candidates.</summary>
    private const float MinRoadForMidpointMeters = 500f;

    /// <summary>The point <paramref name="distance"/> metres along the road's arc length.</summary>
    private static GlobalPosition PointAlong(IReadOnlyList<GlobalPosition> points, float distance)
    {
        float cumulative = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            float segment = HorizontalDistance(points[i - 1], points[i]);
            if (cumulative + segment >= distance)
            {
                float t = segment <= 0f ? 0f : (distance - cumulative) / segment;
                return Lerp(points[i - 1], points[i], t);
            }

            cumulative += segment;
        }

        return points[points.Count - 1];
    }

    private static GlobalPosition Lerp(GlobalPosition a, GlobalPosition b, float t)
    {
        return new GlobalPosition(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
    }

    /// <summary>Horizontal distance squared from a point to a segment — the same measure
    /// <c>CommanderBuildPreview.SegmentDistanceSquared</c> uses for its own road/runway clearance
    /// checks. That one is private to its class, so this was a second, identical definition rather
    /// than a shared one (reuse rule 4 would otherwise ask for one); noted here so the duplication
    /// was a deliberate one, not a miss. <c>internal</c> (T14, ledger row 14 addendum): the
    /// offensive assault's en-route flip test is a third caller, so this one is now shared rather
    /// than tripled.</summary>
    internal static float SegmentDistanceSquared(GlobalPosition point, GlobalPosition a, GlobalPosition b)
    {
        float abX = b.x - a.x;
        float abZ = b.z - a.z;
        float lengthSquared = abX * abX + abZ * abZ;
        float t = lengthSquared <= 0.0001f
            ? 0f
            : Mathf.Clamp01(((point.x - a.x) * abX + (point.z - a.z) * abZ) / lengthSquared);
        float dx = point.x - (a.x + abX * t);
        float dz = point.z - (a.z + abZ * t);
        return dx * dx + dz * dz;
    }

    private void StepBases()
    {
        List<CommanderStrategicPoint> baseCandidates = new();
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null || airbase.center == null || airbase.SavedAirbase == null)
            {
                continue;
            }

            CommanderStrategicPoint point = new(
                StrategicPointKind.Base,
                airbase.center.GlobalPosition(),
                airbase.SavedAirbase.CaptureRange,
                CommanderCaptureService.GetAirbaseLabel(airbase))
            {
                Airbase = airbase,
            };
            baseCandidates.Add(point);
        }

        // Control points have their own cap, shared across three staged passes rather than one:
        // sharing it with the sites (which are already capped and come first) left this pass a cap
        // of zero on every map with a full set of sites.
        candidateVillages = villageCandidates.Count;
        candidateHilltops = hilltopCandidates.Count;
        candidateOutposts = outpostCandidates.Count;
        candidateCrossroads = crossroadsCandidates.Count;
        candidateRoadside = roadsideCandidates.Count;

        List<GlobalPosition> airbaseCentres = CollectAirbaseCentres();
        int cap = CommanderSettings.PointsMaxNonBasePoints;
        // At this point `points` holds only the resource sites StepSites already accepted (bases
        // are added below, after this call) — every stage's preKept starts from them, so no stage
        // can land a candidate within PointMinSpacingMeters of a site, which design.md's Caps
        // section forbids for any two non-base points.
        List<CommanderStrategicPoint> acceptedSoFar = new(points);

        // Five staged passes over one allowance, most valuable kind first, each with its own
        // ceiling so no single kind can eat the budget: the first cut of this ran one pass for
        // villages, crossroads and outposts together and the duel map's 263 road junctions took 44
        // slots, left 15 for hilltops and none for road points. Every stage's preKept is everything
        // accepted before it, so the spacing rule holds across kinds.
        float spacing = CommanderSettings.PointsPointMinSpacingMeters;
        float exclusion = CommanderSettings.PointsAirbaseExclusionMeters;
        int remaining = cap;

        // Road points are the fill for empty stretches; hold their share back so hilltops cannot
        // spend it, but never more than there are candidates for.
        int roadReserve = Mathf.Min(CommanderSettings.PointsMaxRoadPoints, roadsideCandidates.Count);

        List<CommanderStrategicPoint> villages = new(villageCandidates);
        ApplySpacing(villages, spacing, airbaseCentres, exclusion, remaining, acceptedSoFar);
        acceptedSoFar.AddRange(villages);
        remaining -= villages.Count;

        List<CommanderStrategicPoint> outposts = new(outpostCandidates);
        ApplySpacing(outposts, spacing, airbaseCentres, exclusion,
            Mathf.Min(remaining, CommanderSettings.PointsMaxOutposts), acceptedSoFar);
        acceptedSoFar.AddRange(outposts);
        remaining -= outposts.Count;

        // Crossroads keep a wider spacing of their own: junctions come in clusters wherever roads
        // do, and at the general spacing every hamlet's T-junction was becoming one.
        List<CommanderStrategicPoint> crossroads = new(crossroadsCandidates);
        ApplySpacing(crossroads, Mathf.Max(spacing, CommanderSettings.PointsCrossroadsSpacingMeters), airbaseCentres, exclusion,
            Mathf.Min(remaining, CommanderSettings.PointsMaxCrossroads), acceptedSoFar);
        acceptedSoFar.AddRange(crossroads);
        remaining -= crossroads.Count;

        List<CommanderStrategicPoint> hilltops = new(hilltopCandidates);
        ApplySpacing(hilltops, spacing, airbaseCentres, exclusion,
            Mathf.Max(0, remaining - roadReserve), acceptedSoFar);
        acceptedSoFar.AddRange(hilltops);
        remaining -= hilltops.Count;

        List<CommanderStrategicPoint> stageC = new(roadsideCandidates);
        ApplySpacing(stageC, spacing, airbaseCentres, exclusion,
            Mathf.Max(0, Mathf.Min(remaining, CommanderSettings.PointsMaxRoadPoints)), acceptedSoFar);

        AddLabelled(villages, "VILLAGE");
        AddLabelled(outposts, "OUTPOST");
        AddLabelled(crossroads, "CROSSROADS");
        AddLabelled(hilltops, "HILLTOP");

        AddLabelled(stageC, "ROAD POINT");

        points.AddRange(baseCandidates);

        LogDiscoveryResults();
        if (!HasResourceSites && discoveryAttempts < MaxDiscoveryAttempts)
        {
            CommanderPlugin.Log.LogWarning(
                $"Strategic points: no resource sites found on attempt {discoveryAttempts} of {MaxDiscoveryAttempts} "
                    + $"(map {mapSize.x:0}x{mapSize.y:0} m, fill grid {fillColumns}x{fillRows}); retrying in {DiscoveryRetrySeconds:0} s. "
                    + "Gold mines may be built anywhere in reach until sites exist.");
            RestartDiscovery();
            return;
        }

        if (!HasResourceSites)
        {
            CommanderPlugin.Log.LogWarning(
                "Strategic points: this map yielded no resource sites after every attempt. "
                    + "Gold mines fall back to the old rule: anywhere within reach of a held base.");
        }

        industryBuildings = null;
        villageCandidates.Clear();
        hilltopCandidates.Clear();
        outpostCandidates.Clear();
        crossroadsCandidates.Clear();
        roadsideCandidates.Clear();
        // roadPointLists is the one candidate cache that is KEPT past discovery: the picket-insertion
        // track's road-distance gate (design.md, heli-picket-insertion_20260913, Decision 7) measures
        // a control point's distance to the nearest road through it. The real resets still clear it —
        // RestartDiscovery (the retry path) and ResetSession (a mission reload), after which discovery
        // rebuilds it — so retention needs no lifecycle of its own.
        junctionNodes.Clear();
        siteCandidates.Clear();
        discovery = DiscoveryState.Done;
    }

    private void LogDiscoveryResults()
    {
        float duration = Time.realtimeSinceStartup - discoveryStartedAt;
        int sites = 0, villages = 0, hilltops = 0, bases = 0, outposts = 0, crossroads = 0, roadside = 0;
        for (int i = 0; i < points.Count; i++)
        {
            switch (points[i].Kind)
            {
                case StrategicPointKind.Site:
                    sites++;
                    break;
                case StrategicPointKind.Village:
                    villages++;
                    break;
                case StrategicPointKind.Hilltop:
                    hilltops++;
                    break;
                case StrategicPointKind.Outpost:
                    outposts++;
                    break;
                case StrategicPointKind.Crossroads:
                    crossroads++;
                    break;
                case StrategicPointKind.Roadside:
                    roadside++;
                    break;
                case StrategicPointKind.Base:
                    bases++;
                    break;
            }
        }

        int retainedRoadPoints = 0;
        for (int i = 0; i < roadPointLists.Count; i++)
        {
            retainedRoadPoints += roadPointLists[i].Count;
        }

        CommanderPlugin.Log.LogInfo(
            $"Strategic points discovered: {sites} sites, {villages} villages, {hilltops} hilltops, "
                + $"{outposts} outposts, {crossroads} crossroads, {roadside} road points, {bases} bases "
                + $"in {duration:0.0}s (attempt {discoveryAttempts}, map {mapSize.x:0}x{mapSize.y:0} m, "
                + $"fill grid {fillColumns}x{fillRows}; before spacing/caps: {candidateVillages} villages, "
                + $"{candidateHilltops} hilltops, {candidateOutposts} outposts, {candidateCrossroads} crossroads, "
                + $"{candidateRoadside} road points, {civilianBuildingCount} civilian buildings; "
                + $"roads retained: {roadPointLists.Count} roads, {retainedRoadPoints} points).");

        for (int i = 0; i < droppedSiteBuildingLabels.Count; i++)
        {
            CommanderPlugin.Log.LogInfo(
                $"Strategic point dropped: industrial building {droppedSiteBuildingLabels[i]} has no clear ground within 350 m.");
        }

        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            CommanderPlugin.Log.LogInfo(
                $"Strategic point {point.Label}: {point.Kind} at ({point.Position.x:0},{point.Position.z:0}) "
                    + $"r={point.Radius:0} m, nearest base {NearestBaseDistanceKm(point.Position):0.0} km.");
        }
    }

    private static float NearestBaseDistanceKm(GlobalPosition position)
    {
        float best = float.MaxValue;
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null || airbase.center == null)
            {
                continue;
            }

            float distance = HorizontalDistance(position, airbase.center.GlobalPosition());
            if (distance < best)
            {
                best = distance;
            }
        }

        return best == float.MaxValue ? 0f : best / 1000f;
    }

    /// <summary>A cluster of civilian buildings is a village once it has enough of them to be worth
    /// fighting over rather than a single farmstead. Pure, for the self-check.</summary>
    internal static bool QualifiesAsVillage(int buildingCount, int minBuildings) => buildingCount >= minBuildings;

    /// <summary>
    /// A height-map sample is a hilltop when nothing in its ring stands higher and it clears the
    /// ring's mean height by at least the configured prominence — the boundary itself counts as
    /// qualifying, so a sample exactly <paramref name="prominenceMeters"/> above the mean passes.
    /// Pure, for the self-check.
    /// </summary>
    internal static bool QualifiesAsHilltop(
        float sampleHeight, float ringMeanHeight, float prominenceMeters, bool isHighestInRing)
        => isHighestInRing && sampleHeight - ringMeanHeight >= prominenceMeters;

    private static float HorizontalDistance(GlobalPosition a, GlobalPosition b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Discovery spacing filter, pure: drops any candidate within <paramref name="exclusionRadius"/>
    /// of an exclusion centre or within <paramref name="minSpacing"/> of a <paramref name="preKept"/>
    /// point, then keeps up to <paramref name="cap"/> of the rest farthest-first — seeded with the
    /// first survivor, each later pick the candidate farthest from everything kept, never one inside
    /// <paramref name="minSpacing"/> of a kept one. The caller's order still decides the seed, which
    /// is why existing industry sites and villages are listed before generated fill and hilltops;
    /// after the seed, distance decides, so the cap spreads across the map instead of filling from
    /// whichever corner the scan started in. <paramref name="preKept"/> lets a second, later pass (the
    /// village/hilltop pass in <c>StepBases</c>, seeded with the sites the first pass already
    /// accepted) honour the 1.5 km cross-group minimum design.md's Caps section requires, without
    /// counting those pre-kept points against <paramref name="cap"/> a second time — <paramref
    /// name="cap"/> still bounds only how many new candidates this call may keep. Horizontal
    /// distance only: map coordinates carry no useful height difference.
    /// </summary>
    internal static void ApplySpacing(
        List<CommanderStrategicPoint> candidates,
        float minSpacing,
        IReadOnlyList<GlobalPosition> exclusionCentres,
        float exclusionRadius,
        int cap,
        IReadOnlyList<CommanderStrategicPoint>? preKept = null)
    {
        // Pass 1: drop anything an exclusion centre or a pre-kept point already rules out. Order
        // is preserved, so the first survivor is still whoever the caller put first.
        List<CommanderStrategicPoint> eligible = new(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            CommanderStrategicPoint candidate = candidates[i];
            bool blocked = false;
            for (int e = 0; e < exclusionCentres.Count; e++)
            {
                if (HorizontalDistance(candidate.Position, exclusionCentres[e]) < exclusionRadius)
                {
                    blocked = true;
                    break;
                }
            }

            if (preKept != null)
            {
                for (int p = 0; !blocked && p < preKept.Count; p++)
                {
                    if (HorizontalDistance(candidate.Position, preKept[p].Position) < minSpacing)
                    {
                        blocked = true;
                    }
                }
            }

            if (!blocked)
            {
                eligible.Add(candidate);
            }
        }

        // Pass 2: farthest-first. The first eligible candidate seeds the set; every later pick is
        // the candidate farthest from everything kept so far, and nothing inside minSpacing is
        // ever picked. First-come used to decide the cap, and because the fill and hilltop scans
        // walk the map south to north, 29 of 30 sites and every hilltop landed in the southern
        // half of the first map this ran on. Spreading by distance is what makes the cap a
        // "how many" instead of a "how far north".
        List<CommanderStrategicPoint> kept = new(Mathf.Min(eligible.Count, Mathf.Max(cap, 0)));
        List<float> nearestKept = new(eligible.Count);
        for (int i = 0; i < eligible.Count; i++)
        {
            nearestKept.Add(float.MaxValue);
        }

        int next = eligible.Count > 0 && cap > 0 ? 0 : -1;
        while (next >= 0 && kept.Count < cap)
        {
            CommanderStrategicPoint picked = eligible[next];
            kept.Add(picked);
            nearestKept[next] = -1f;

            int best = -1;
            float bestDistance = 0f;
            for (int i = 0; i < eligible.Count; i++)
            {
                if (nearestKept[i] < 0f)
                {
                    continue;
                }

                float distance = HorizontalDistance(eligible[i].Position, picked.Position);
                if (distance < nearestKept[i])
                {
                    nearestKept[i] = distance;
                }

                if (nearestKept[i] >= minSpacing && nearestKept[i] > bestDistance)
                {
                    bestDistance = nearestKept[i];
                    best = i;
                }
            }

            next = best;
        }

        candidates.Clear();
        candidates.AddRange(kept);
    }
}
