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
    /// A resource site's capture ring. Sites became held-by-presence control points on 2026-09-14
    /// (two vehicles inside the ring for the hold time take them); at the old 50 m footprint a
    /// second vehicle parked a few lengths from the first read as outside and the site sat at
    /// "free 1/2" for good. Doubled to 500 m on 2026-09-14 (user: "we were running a fine line of
    /// having units on the edge of the ring to allow room but if they're slightly over it, it won't
    /// be captured"): the garrison now stands at SiteHoldRingFraction of this, well inside the ring
    /// and well clear of the mine at the centre; mine siting itself uses the point position, not this.
    /// </summary>
    private const float SiteRadiusMeters = 500f;

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
        levelledNudged = 0;
        levelledDropped = 0;
        droppedWooded = 0;
        droppedOffRoad = 0;
        discovery = DiscoveryState.Sites;
    }

    /// <summary>
    /// Compass probes (four cardinals plus four diagonals) the levelness test rings a candidate
    /// with, the same eight <see cref="HilltopRingSamples"/> uses: a four-point cross lets a gully
    /// or a spur running exactly between two probes escape the test entirely.
    /// </summary>
    private const int LevelnessRingSamples = 8;

    /// <summary>
    /// Step between rings of the level-ground search. 20 m is the strategic height map's own
    /// resolution, so a finer step re-reads the same pixel and finds the same answer.
    /// </summary>
    private const float LevelSearchStepMeters = 20f;

    /// <summary>
    /// How far the level-ground search may move a point from where discovery put it. 150 m keeps
    /// the point recognisably on the same village, summit or industrial site; past that it is a
    /// different piece of ground and the honest answer is to drop the candidate.
    /// </summary>
    private const float LevelSearchMaxRadiusMeters = 150f;

    /// <summary>Level-search offsets, centre first then outward by ring, built once from the
    /// constants above (they are compile-time, so the list never needs rebuilding).</summary>
    private static readonly List<Vector2> levelSearchOffsets = new();

    /// <summary>Ring heights for one levelness probe, reused so the test allocates nothing.</summary>
    private static readonly List<float> levelRingScratch = new();

    /// <summary>Counts for the discovery log line: how many accepted points the level search had to
    /// move, and how many candidate positions it rejected outright for want of level ground.</summary>
    private int levelledNudged;
    private int levelledDropped;

    /// <summary>
    /// The levelness rule, pure for the self-check. Ground is level enough for a point when the
    /// whole probe (centre plus ring) spans no more than <paramref name="maxSpreadMeters"/> of
    /// height AND the mean of the slopes from the centre out to each probe is no steeper than
    /// <paramref name="maxSlopeDegrees"/>. Both boundaries count as passing. A probe that returned
    /// no ring samples at all (off the height map) is not level ground: unproven is not level.
    /// </summary>
    internal static bool IsGroundLevelEnough(
        float centreHeight,
        IReadOnlyList<float> ringHeights,
        float probeRadiusMeters,
        float maxSpreadMeters,
        float maxSlopeDegrees)
    {
        if (ringHeights.Count == 0)
        {
            return false;
        }

        float minimum = centreHeight;
        float maximum = centreHeight;
        float slopeSum = 0f;
        float radius = Mathf.Max(probeRadiusMeters, 0.01f);
        for (int i = 0; i < ringHeights.Count; i++)
        {
            float height = ringHeights[i];
            if (height < minimum)
            {
                minimum = height;
            }

            if (height > maximum)
            {
                maximum = height;
            }

            slopeSum += Mathf.Atan(Mathf.Abs(height - centreHeight) / radius) * Mathf.Rad2Deg;
        }

        if (maximum - minimum > maxSpreadMeters)
        {
            return false;
        }

        return slopeSum / ringHeights.Count <= maxSlopeDegrees;
    }

    /// <summary>
    /// Offsets the level search tries, in order: the candidate itself, then every compass point of
    /// a ring <paramref name="stepMeters"/> out, then the next ring, out to
    /// <paramref name="maxRadiusMeters"/>. Nearest-first, so the search always returns the level
    /// spot closest to where discovery meant the point to be. Pure, for the self-check.
    /// </summary>
    internal static void EmitLevelSearchOffsets(float stepMeters, float maxRadiusMeters, List<Vector2> result)
    {
        result.Clear();
        result.Add(Vector2.zero);
        if (stepMeters <= 0f)
        {
            return;
        }

        for (float radius = stepMeters; radius <= maxRadiusMeters; radius += stepMeters)
        {
            for (int i = 0; i < LevelnessRingSamples; i++)
            {
                float angle = i * (Mathf.PI * 2f / LevelnessRingSamples);
                result.Add(new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius));
            }
        }
    }

    /// <summary>
    /// Walks <see cref="EmitLevelSearchOffsets"/> from a candidate position and returns the first
    /// offset whose ground passes <see cref="IsGroundLevelEnough"/> and stands at least
    /// <paramref name="minHeightMeters"/> high (the hilltop caller's way of saying "still the top of
    /// this hill, not a level shelf part way down it"). Reads the strategic height map only — nine
    /// samples per offset, no raycasts.
    /// </summary>
    private static bool TryFindLevelGround(
        float x, float z, float minHeightMeters, out GlobalPosition levelled, out bool moved)
    {
        if (levelSearchOffsets.Count == 0)
        {
            EmitLevelSearchOffsets(LevelSearchStepMeters, LevelSearchMaxRadiusMeters, levelSearchOffsets);
        }

        float probeRadius = CommanderSettings.PointsLevelnessProbeRadiusMeters;
        float maxSpread = CommanderSettings.PointsMaxPointHeightSpreadMeters;
        float maxSlope = CommanderSettings.PointsMaxPointSlopeDegrees;
        for (int i = 0; i < levelSearchOffsets.Count; i++)
        {
            Vector2 offset = levelSearchOffsets[i];
            float probeX = x + offset.x;
            float probeZ = z + offset.y;
            if (!CommanderSamSiteAnalyzerService.TryGetStrategicTerrainHeight(probeX, probeZ, out float height)
                || height < minHeightMeters)
            {
                continue;
            }

            levelRingScratch.Clear();
            for (int s = 0; s < LevelnessRingSamples; s++)
            {
                float angle = s * (Mathf.PI * 2f / LevelnessRingSamples);
                if (CommanderSamSiteAnalyzerService.TryGetStrategicTerrainHeight(
                        probeX + Mathf.Cos(angle) * probeRadius,
                        probeZ + Mathf.Sin(angle) * probeRadius,
                        out float ringHeight))
                {
                    levelRingScratch.Add(ringHeight);
                }
            }

            if (!IsGroundLevelEnough(height, levelRingScratch, probeRadius, maxSpread, maxSlope))
            {
                continue;
            }

            levelled = new GlobalPosition(probeX, height, probeZ);
            moved = i > 0;
            return true;
        }

        levelled = default;
        moved = false;
        return false;
    }

    /// <summary>
    /// <see cref="TryFindLevelGround"/> for a caller outside discovery that wants somewhere level to
    /// put a building rather than somewhere level to put a point — the FOB's three structures
    /// (<c>Economy/CommanderFobBuilder.cs</c>, fob-construction_20260914). No minimum height, since
    /// a depot is not a hilltop, and the "moved" flag is discovery's own bookkeeping and means
    /// nothing here. Exists so there is ONE levelness rule in the mod (Reuse rule 3) rather than a
    /// second probe loop written beside the buildings.
    /// </summary>
    internal static bool TryFindLevelBuildingSpot(GlobalPosition candidate, out GlobalPosition levelled)
    {
        return TryFindLevelGround(candidate.x, candidate.z, float.MinValue, out levelled, out _);
    }

    /// <summary>
    /// <see cref="TryFindLevelGround"/> with the discovery log's counters attached. Every non-road
    /// kind (site, village, hilltop, outpost) goes through this before it becomes a candidate;
    /// crossroads and road points do not, because a road is level ground by definition of being a
    /// road (user decision, 2026-09-14). Runs before <see cref="ApplySpacing"/> everywhere, so
    /// spacing measures the positions the points actually end up at.
    /// </summary>
    private bool TryLevelPoint(GlobalPosition original, float minHeightMeters, out GlobalPosition levelled)
    {
        if (!TryFindLevelGround(original.x, original.z, minHeightMeters, out levelled, out bool moved))
        {
            levelledDropped++;
            return false;
        }

        if (moved)
        {
            levelledNudged++;
        }

        return true;
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

            if (TryFindSiteRing(economy, mineDefinition, building.transform.GlobalPosition(), out GlobalPosition site)
                && TryLevelSite(economy, mineDefinition, site, out GlobalPosition levelSite))
            {
                siteCandidates.Add(new CommanderStrategicPoint(StrategicPointKind.Site, levelSite, SiteRadiusMeters, string.Empty));
            }
            else
            {
                droppedSiteBuildingLabels.Add(CommanderGameAccess.GetUnitLabel(building));
            }
        }
    }

    /// <summary>
    /// Nudges a resource site onto level ground and re-proves the siting rule there. Moving a site
    /// can walk it onto a road, a building or a runway, none of which <c>IsSiteAllowed</c> would
    /// have passed at the original position, so a moved site is checked again; an unmoved one was
    /// already checked by the caller and is taken as it stands.
    /// </summary>
    private bool TryLevelSite(
        CommanderEconomyService economy,
        BuildingDefinition mineDefinition,
        GlobalPosition site,
        out GlobalPosition levelSite)
    {
        if (!TryLevelPoint(site, float.MinValue, out GlobalPosition levelled))
        {
            levelSite = default;
            return false;
        }

        if (HorizontalDistance(levelled, site) < 1f)
        {
            levelSite = site;
            return true;
        }

        GlobalPosition snapped = CommanderGameAccess.SnapToTerrain(levelled);
        if (!economy.IsSiteAllowed(mineDefinition, snapped, null, out _) || CommanderGameAccess.IsBelowSeaLevel(snapped))
        {
            levelSite = default;
            return false;
        }

        levelSite = snapped;
        return true;
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

            // Level ground before the siting rule: nine height-map reads cost less than the physics
            // overlap IsSiteAllowed runs, and a nudged site has to be proven allowed where it lands
            // rather than where the random probe happened to fall.
            if (!TryLevelPoint(new GlobalPosition(x, height, z), float.MinValue, out GlobalPosition candidate))
            {
                continue;
            }

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

            // A cluster's centroid can land in the stream bed the hamlet was built either side of.
            // Nudge it to level ground within 150 m; a cluster with no level ground that close is
            // not somewhere a garrison can sit, so it becomes no point at all (2026-09-14).
            if (!TryLevelPoint(centroid, float.MinValue, out GlobalPosition levelled))
            {
                continue;
            }

            GlobalPosition snapped = CommanderGameAccess.SnapToTerrain(levelled);
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

        // The summit itself is often a knife edge. Search out from it for the nearest level patch
        // that is still within the hill's own prominence of the peak — the level top of the hill,
        // not a shelf half way down it — and drop the candidate if there is none (2026-09-14).
        if (!TryLevelPoint(
                new GlobalPosition(x, height, z),
                height - CommanderSettings.PointsHilltopProminenceMeters,
                out GlobalPosition candidate))
        {
            return;
        }

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
            // A ship's deck (AttachedAirbase) is an airbase to the game but not a base point here:
            // it moves, it stands in the sea, and nothing should picket it or defend it from the
            // shore (user, 2026-09-14: "ground units being tasked with defending airbase ships").
            if (airbase == null || airbase.center == null || airbase.SavedAirbase == null || CommanderGameAccess.IsShipAirbase(airbase))
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

        // Validity BEFORE the caps (user decision 2026-09-14): a wooded or off-road candidate is
        // thrown away here so the allowance below is spent only on points a platoon can actually
        // reach. `points` holds nothing but the resource sites StepSites already accepted, so this
        // is where a site is tested too — the site cap ran during the sites pass, before the road
        // network existed, so a site is filtered after its own cap rather than before it.
        DropInvalidCandidates(points);
        DropInvalidCandidates(villageCandidates);
        DropInvalidCandidates(outpostCandidates);
        DropInvalidCandidates(crossroadsCandidates);
        DropInvalidCandidates(hilltopCandidates);
        DropInvalidCandidates(roadsideCandidates);

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

    /// <summary>How many candidates the validity pass threw away, split by which rule refused them,
    /// for the discovery log line. Reset with the levelness counters when a pass starts.</summary>
    private int droppedWooded;
    private int droppedOffRoad;

    /// <summary>
    /// Whether a discovered point is worth keeping, pure (user decision 2026-09-14: "make heavily
    /// wooded control points invalid, limit to within some km of a road"). A point is kept when its
    /// own footprint is not woodland AND the nearest road is no farther than
    /// <paramref name="maxRoadMeters"/>. Both boundaries count as passing, the convention the rest
    /// of discovery uses. A base is always kept — it is the map's own, not the mod's to drop.
    /// </summary>
    internal static bool PointIsValid(
        StrategicPointKind kind, bool wooded, float roadDistanceMeters, float maxRoadMeters)
    {
        return kind == StrategicPointKind.Base || (!wooded && roadDistanceMeters <= maxRoadMeters);
    }

    /// <summary>
    /// The two kinds discovery builds ON the road network itself: a crossroads is a junction of two
    /// polylines and a roadside point is a position measured along one. Their distance to the
    /// nearest road is zero by construction, so the validity pass below never measures it — which
    /// also keeps the road walk (every retained polyline, per candidate) off the longest candidate
    /// list discovery produces.
    /// </summary>
    private static bool IsRoadDerivedKind(StrategicPointKind kind)
    {
        return kind == StrategicPointKind.Crossroads || kind == StrategicPointKind.Roadside;
    }

    /// <summary>
    /// Stamps woodland on every candidate in <paramref name="candidates"/> and throws away the ones
    /// <see cref="PointIsValid"/> refuses, logging one line per drop.
    /// <para>This is the woodland stamp that used to run at the END of discovery (MarkWoodedPoints,
    /// removed 2026-09-14) moved to the front of the caps pass. It has to run before the caps
    /// because the cap now chooses among VALID points: stamping afterwards let a wooded hilltop
    /// spend one of the forty-eight slots and pushed a reachable one out of the list. A wooded point
    /// used to be kept and merely flagged, so that the picket delivery rule would airdrop onto it
    /// rather than try to land; the user's 2026-09-14 decision drops it instead, because a point a
    /// transport cannot land on and a platoon cannot drive to is not worth a slot on the map.</para>
    /// <para>Bases are never offered to this pass — <c>baseCandidates</c> joins the point list after
    /// the caps — but the kind check is kept so the rule reads the same here as it does in the
    /// self-check.</para>
    /// </summary>
    private void DropInvalidCandidates(List<CommanderStrategicPoint> candidates)
    {
        float maxRoad = CommanderSettings.PointsMaxRoadDistanceMeters;
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            CommanderStrategicPoint point = candidates[i];
            if (point.Kind == StrategicPointKind.Base)
            {
                continue;
            }

            point.Wooded = CommanderSupplyHeliService.IsWoodedFootprint(
                point.Position, Mathf.Max(1f, point.Radius));
            float roadDistance = IsRoadDerivedKind(point.Kind)
                ? 0f
                : NearestRoadDistanceMeters(point.Position);
            if (PointIsValid(point.Kind, point.Wooded, roadDistance, maxRoad))
            {
                continue;
            }

            // Candidates are labelled only after the caps pass has chosen them, so a drop names the
            // kind and the ground it stood on instead. A resource site is the one kind already
            // carrying its label by this point, and keeps it.
            string label = point.Label.Length > 0
                ? point.Label
                : $"{point.Kind} at ({point.Position.x:0},{point.Position.z:0})";
            if (point.Wooded)
            {
                droppedWooded++;
                CommanderPlugin.Log.LogInfo($"Strategic point dropped: {label}: wooded.");
            }
            else
            {
                droppedOffRoad++;
                CommanderPlugin.Log.LogInfo(
                    $"Strategic point dropped: {label}: {roadDistance:0} m from the nearest road.");
            }

            candidates.RemoveAt(i);
        }
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
                + $"levelled: {levelledNudged} nudged, {levelledDropped} dropped; "
                + $"dropped {droppedWooded} wooded, {droppedOffRoad} off-road; "
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

    /// <summary>
    /// The levelness rule and the order the level search tries ground in. Both are pure, so this
    /// runs at plugin load with no height map and no mission (added 2026-09-14 with the rule).
    /// </summary>
    /// <summary>
    /// The validity rule at its named boundaries (user decision 2026-09-14). Every case is a point
    /// the discovery pass would keep or throw away, so a retune of
    /// <c>PointMaxRoadDistanceMeters</c> into nonsense says so at load.
    /// </summary>
    private static void CheckPointValidity(List<string> failures)
    {
        // Config guard (2026-09-14): the crossroads and road-point ceilings together must leave the
        // hilltop stage at least a third of the cap, or a lower cap produces a map of roads only.
        int cap = CommanderSettings.PointsMaxNonBasePoints;
        int roadKinds = CommanderSettings.PointsMaxCrossroads + CommanderSettings.PointsMaxRoadPoints;
        Expect(
            failures,
            "the crossroads and road-point ceilings leave hilltops a third of the control-point cap; check the Points section of the config",
            roadKinds <= cap - cap / 3,
            true);
        Expect(failures, "a wooded hilltop is invalid", PointIsValid(StrategicPointKind.Hilltop, true, 100f, 2000f), false);
        Expect(failures, "a hilltop's capture ring is 1.5 times its placement ring", CommanderStrategicPoint.CaptureRadiusFor(StrategicPointKind.Hilltop, 300f), 450f);
        Expect(failures, "a site's capture ring is its own radius", CommanderStrategicPoint.CaptureRadiusFor(StrategicPointKind.Site, 500f), 500f);
        Expect(
            failures,
            "an open hilltop 3 km from a road is invalid",
            PointIsValid(StrategicPointKind.Hilltop, false, 3000f, 2000f),
            false);
        Expect(
            failures,
            "an open hilltop at the road limit is valid",
            PointIsValid(StrategicPointKind.Hilltop, false, 2000f, 2000f),
            true);
        Expect(
            failures,
            "an open hilltop one metre past the road limit is invalid",
            PointIsValid(StrategicPointKind.Hilltop, false, 2001f, 2000f),
            false);
        Expect(
            failures,
            "a wooded resource site is invalid too",
            PointIsValid(StrategicPointKind.Site, true, 100f, 2000f),
            false);
        Expect(failures, "a base is never dropped", PointIsValid(StrategicPointKind.Base, true, 9000f, 2000f), true);
        Expect(
            failures,
            "a point with no road on the map at all is invalid",
            PointIsValid(StrategicPointKind.Village, false, float.MaxValue, 2000f),
            false);
    }

    private static void CheckLevelness(List<string> failures)
    {
        // Spread rule, probed at the 60 m default where the spread is what binds: 60 m out, a 6 m
        // step is only 5.7 degrees, well inside the 8 degree mean-slope limit.
        List<float> flat = new() { 100f, 100f, 100f, 100f, 100f, 100f, 100f, 100f };
        Expect(failures, "dead flat ground is level", IsGroundLevelEnough(100f, flat, 60f, 6f, 8f), true);

        List<float> atSpread = new() { 106f, 100f, 100f, 100f, 100f, 100f, 100f, 100f };
        Expect(
            failures,
            "a spread of exactly MaxPointHeightSpread is level",
            IsGroundLevelEnough(100f, atSpread, 60f, 6f, 8f),
            true);

        List<float> overSpread = new() { 107f, 100f, 100f, 100f, 100f, 100f, 100f, 100f };
        Expect(
            failures,
            "one metre over the spread is not level",
            IsGroundLevelEnough(100f, overSpread, 60f, 6f, 8f),
            false);

        // Slope rule, probed at 20 m where the spread rule cannot bind: the mean slope crosses 8
        // degrees at a 2.811 m step, so 2.7 m passes and 2.9 m fails while both stay well under the
        // 6 m spread limit. This is the case that catches a uniform tilt the spread rule waves
        // through on a narrow probe ring.
        List<float> gentleTilt = new() { 97.3f, 97.3f, 97.3f, 97.3f, 97.3f, 97.3f, 97.3f, 97.3f };
        Expect(
            failures,
            "a mean slope inside MaxPointSlopeDegrees is level",
            IsGroundLevelEnough(100f, gentleTilt, 20f, 6f, 8f),
            true);

        List<float> steepTilt = new() { 97.1f, 97.1f, 97.1f, 97.1f, 97.1f, 97.1f, 97.1f, 97.1f };
        Expect(
            failures,
            "a mean slope past MaxPointSlopeDegrees is not level",
            IsGroundLevelEnough(100f, steepTilt, 20f, 6f, 8f),
            false);

        Expect(
            failures,
            "ground with no ring samples at all is not level",
            IsGroundLevelEnough(100f, new List<float>(), 60f, 6f, 8f),
            false);

        // Search order: the candidate itself first, then whole rings outward, so the first level
        // spot found is always the nearest one to where discovery put the point.
        List<Vector2> offsets = new();
        EmitLevelSearchOffsets(LevelSearchStepMeters, LevelSearchMaxRadiusMeters, offsets);
        Expect(failures, "level search tries the centre then seven rings of eight", offsets.Count, 57);
        Expect(failures, "level search tries the candidate itself first", offsets[0].sqrMagnitude, 0f);

        bool outward = true;
        for (int i = 2; i < offsets.Count; i++)
        {
            if (offsets[i].magnitude < offsets[i - 1].magnitude - 0.01f)
            {
                outward = false;
            }
        }

        Expect(failures, "level search never steps back inward", outward, true);
        Expect(
            failures,
            "the first ring sits one step out",
            Mathf.Abs(offsets[1].magnitude - LevelSearchStepMeters) < 0.01f,
            true);
        Expect(
            failures,
            "the ninth offset starts the second ring",
            Mathf.Abs(offsets[9].magnitude - (LevelSearchStepMeters * 2f)) < 0.01f,
            true);
    }

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
