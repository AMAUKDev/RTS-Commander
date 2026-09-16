using System;
using System.Collections.Generic;
using NuclearOption.Effects;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The landing-zone scout: whether a chosen spot on a control point is somewhere a transport
/// helicopter can actually put its wheels down, and where the nearest spot that is.
/// <para>
/// Written for the user report of 2026-09-14 — "air insertion of pickets sometimes is sent to land
/// in tree covered areas - it cannot land, units aren't dropped, stuck". What the decompile says
/// about that, so nobody re-derives it:
/// </para>
/// <list type="bullet">
/// <item><b>Scatter trees are not colliders.</b> <c>TerrainScatter.GenerateScatters</c> writes tree
/// positions into a binary <c>TextAsset</c> and <c>NuclearOption.Effects.TreeRenderer</c> draws them
/// GPU-instanced from a <c>GraphicsBuffer</c>. There is no tree collider type in
/// <c>Assembly-CSharp</c>, no tree layer in <c>PhysicsLayers</c>, and <c>Aircraft.CheckRadarAlt</c>
/// line-casts <c>Statics|Ships</c> only. A physics probe therefore CANNOT see woodland, and woodland
/// cannot physically stop a helicopter.</item>
/// <item><b>The game's own landing search is blind to woodland too and rejects slope.</b>
/// <c>AIHeloTransportState.TransportDestination.UpdateTouchdownPoint</c> line-casts straight down on
/// <c>StaticsMask</c> and accepts a point only when the surface normal is under
/// <see cref="CommanderOperationsService.LzMaxSlopeDegrees"/> (20°), above sea, and inside the HQ's
/// drop-zone register. On ground where no sample inside its search radius passes, its <c>slope</c>
/// stays at its 90° seed, the touchdown point is never refined, and the transport hovers over the
/// spot the mod handed it. That is the behaviour the report describes, and dense woodland in this
/// game sits on hill flanks — which is exactly the ground the slope rule refuses.</item>
/// </list>
/// <para>
/// So the scout measures three things and the pure rule
/// (<see cref="CommanderOperationsService.IsLandingZoneClear"/>) judges them: how many scatter trees
/// stand in the clear radius (read from the game's own tree data, the only source that exists),
/// how many real static colliders stand there (buildings and mission scenery, which the physics
/// probe CAN see), and how steep the ground is by the game's own 20° rule.
/// </para>
/// </summary>
internal sealed partial class CommanderSupplyHeliService
{
    /// <summary>
    /// Edge of one tree-index cell, in metres. 25 m is the strategic height map's own working
    /// resolution and comfortably finer than the 40 m clear radius the index is queried with, so a
    /// clear-radius query reads a genuine disc of cells rather than one blunt square.
    /// </summary>
    private const float TreeCellSizeMeters = 25f;

    /// <summary>
    /// Ceiling on the tree index's cell count before the cell size is doubled. Eight million cells
    /// is 8 MB at one byte per cell, which at 25 m covers a 70 km square map; a larger map gets
    /// coarser cells rather than a hundred-megabyte allocation. One byte per cell is enough because
    /// the rule only asks whether a handful of trees stand in the radius, never how many hundreds.
    /// </summary>
    private const int TreeIndexMaxCells = 8_000_000;

    /// <summary>
    /// Step between rings of the clear-ground search, in metres. 50 m moves the landing zone by more
    /// than one rotor span per ring, so successive rings are genuinely different ground, and the
    /// whole 400 m search is eight rings rather than the twenty a 20 m step would cost.
    /// </summary>
    private const float LzSearchStepMeters = 50f;

    /// <summary>Scratch for the static-collider probe, sized for the overlap count the rule cares
    /// about — anything past sixteen colliders in a 40 m circle is emphatically not a landing
    /// zone.</summary>
    private static readonly Collider[] lzOverlapScratch = new Collider[16];

    /// <summary>Clear-ground search offsets, centre first then outward by ring. Shares the levelness
    /// nudge's own emitter (<see cref="CommanderStrategicPointService.EmitLevelSearchOffsets"/>,
    /// Reuse rule 4) so the two searches walk ground in the same order.</summary>
    private static readonly List<Vector2> lzSearchOffsets = new();

    // ---- the tree index -------------------------------------------------------------------

    /// <summary>Tree counts per cell, clamped at 255; null until the index has been built.</summary>
    private static byte[]? treeCells;
    private static int treeCellColumns;
    private static int treeCellRows;
    private static float treeCellSize = TreeCellSizeMeters;
    private static float treeIndexOriginX;
    private static float treeIndexOriginZ;
    private static int treeIndexCount;
    private static int treeIndexAttempts;

    /// <summary>
    /// How many times the index may fail to find the map's tree data before it stops trying: five.
    /// The scatter renderer and the strategic height map both come up during mission load, and the
    /// first insertion can be asked for before either is ready, so one miss must not mean a whole
    /// match with no woodland detection. Five attempts is cheap (each is one scene search) and
    /// spans several minutes of review cadence.
    /// </summary>
    private const int TreeIndexMaxAttempts = 5;

    /// <summary>Drops the tree index so the next mission rebuilds it against its own map. Called
    /// from <c>ResetSession</c>, beside the rest of this service's per-session state.</summary>
    private static void ResetLandingZoneScout()
    {
        treeCells = null;
        treeCellColumns = 0;
        treeCellRows = 0;
        treeIndexCount = 0;
        treeIndexAttempts = 0;
        lzSearchOffsets.Clear();
    }

    /// <summary>
    /// Builds the tree index once per mission from the game's own scatter data: every
    /// <see cref="TreeRenderer"/> in the scene carries a <c>PositionData</c> text asset of raw
    /// <c>Vector3</c>s (twelve bytes each — <c>ScatterLoad.BYTES_PER_TREE</c>) in the same
    /// datum-relative space <c>GlobalPosition</c> uses, because <c>TerrainScatter</c> wrote them as
    /// <c>GlobalPosition</c> in the first place. A map with no tree renderer, or one whose data has
    /// not loaded, leaves the index empty, and every clear-ground test then reports no trees — the
    /// honest answer when the game has told us nothing, and the same answer the feature gave before
    /// this file existed.
    /// </summary>
    private static bool EnsureTreeIndex()
    {
        if (treeCells != null)
        {
            return true;
        }

        if (treeIndexAttempts >= TreeIndexMaxAttempts)
        {
            return false;
        }

        treeIndexAttempts++;
        if (!CommanderSamSiteAnalyzerService.TryGetStrategicHeightMapSize(out Vector2 mapSize)
            || mapSize.x <= 0f
            || mapSize.y <= 0f)
        {
            return false;
        }

        TreeRenderer[] renderers = UnityEngine.Object.FindObjectsOfType<TreeRenderer>();
        if (renderers == null || renderers.Length == 0)
        {
            if (treeIndexAttempts >= TreeIndexMaxAttempts)
            {
                CommanderPlugin.Log.LogWarning(
                    "Landing-zone scout: no tree renderer in this mission after "
                        + $"{TreeIndexMaxAttempts} attempts; wooded landing zones cannot be detected.");
            }

            return false;
        }

        treeCellSize = TreeCellSizeMeters;
        treeCellColumns = Mathf.Max(1, Mathf.CeilToInt(mapSize.x / treeCellSize));
        treeCellRows = Mathf.Max(1, Mathf.CeilToInt(mapSize.y / treeCellSize));
        while ((long)treeCellColumns * treeCellRows > TreeIndexMaxCells)
        {
            treeCellSize *= 2f;
            treeCellColumns = Mathf.Max(1, Mathf.CeilToInt(mapSize.x / treeCellSize));
            treeCellRows = Mathf.Max(1, Mathf.CeilToInt(mapSize.y / treeCellSize));
        }

        treeIndexOriginX = -mapSize.x * 0.5f;
        treeIndexOriginZ = -mapSize.y * 0.5f;
        byte[] cells = new byte[treeCellColumns * treeCellRows];
        int counted = 0;
        for (int r = 0; r < renderers.Length; r++)
        {
            TextAsset? data = renderers[r] != null ? renderers[r].PositionData : null;
            byte[]? bytes = data != null ? data.bytes : null;
            if (bytes == null || bytes.Length < 12)
            {
                continue;
            }

            int trees = bytes.Length / 12;
            for (int i = 0; i < trees; i++)
            {
                int offset = i * 12;
                float x = BitConverter.ToSingle(bytes, offset);
                float z = BitConverter.ToSingle(bytes, offset + 8);
                int column = Mathf.FloorToInt((x - treeIndexOriginX) / treeCellSize);
                int row = Mathf.FloorToInt((z - treeIndexOriginZ) / treeCellSize);
                if (column < 0 || row < 0 || column >= treeCellColumns || row >= treeCellRows)
                {
                    continue;
                }

                int index = row * treeCellColumns + column;
                if (cells[index] < byte.MaxValue)
                {
                    cells[index]++;
                }
                counted++;
            }
        }

        treeCells = cells;
        treeIndexCount = counted;
        CommanderPlugin.Log.LogInfo(
            $"Landing-zone scout: indexed {counted} trees into {treeCellColumns}x{treeCellRows} cells "
                + $"of {treeCellSize:0} m.");
        return true;
    }

    /// <summary>
    /// Trees standing within <paramref name="radiusMeters"/> of <paramref name="centre"/>. A cell
    /// counts when its own centre lies inside the radius, so the measured area is a disc of cells
    /// rather than the enclosing square — at 25 m cells and a 40 m radius that is the thirteen cells
    /// covering the circle. Zero when the index could not be built, which never blocks a landing.
    /// </summary>
    internal static int CountTreesNear(GlobalPosition centre, float radiusMeters)
    {
        if (!EnsureTreeIndex() || treeCells == null)
        {
            return 0;
        }

        float x = (float)centre.x;
        float z = (float)centre.z;
        int minColumn = Mathf.Max(0, Mathf.FloorToInt((x - radiusMeters - treeIndexOriginX) / treeCellSize));
        int maxColumn = Mathf.Min(treeCellColumns - 1, Mathf.FloorToInt((x + radiusMeters - treeIndexOriginX) / treeCellSize));
        int minRow = Mathf.Max(0, Mathf.FloorToInt((z - radiusMeters - treeIndexOriginZ) / treeCellSize));
        int maxRow = Mathf.Min(treeCellRows - 1, Mathf.FloorToInt((z + radiusMeters - treeIndexOriginZ) / treeCellSize));
        float radiusSquared = radiusMeters * radiusMeters;
        int total = 0;
        for (int row = minRow; row <= maxRow; row++)
        {
            float cellZ = treeIndexOriginZ + (row + 0.5f) * treeCellSize;
            float dz = cellZ - z;
            for (int column = minColumn; column <= maxColumn; column++)
            {
                float cellX = treeIndexOriginX + (column + 0.5f) * treeCellSize;
                float dx = cellX - x;
                if (dx * dx + dz * dz > radiusSquared)
                {
                    continue;
                }

                total += treeCells[row * treeCellColumns + column];
            }
        }

        return total;
    }

    /// <summary>
    /// Static colliders — buildings, rocks, mission scenery — standing within
    /// <paramref name="radiusMeters"/> of <paramref name="centre"/>, ignoring the terrain itself.
    /// This is the half of the obstacle picture physics CAN answer; the tree half above is the half
    /// it cannot.
    /// </summary>
    private static int CountStaticObstaclesNear(GlobalPosition centre, float radiusMeters)
    {
        Vector3 probe = centre.ToLocalPosition() + Vector3.up * 2f;
        int hits = Physics.OverlapSphereNonAlloc(
            probe,
            radiusMeters,
            lzOverlapScratch,
            PhysicsLayers.StaticsMask,
            QueryTriggerInteraction.Ignore);
        PhysicMaterial? terrainMaterial = GameAssets.i != null ? GameAssets.i.terrainMaterial : null;
        int obstacles = 0;
        for (int i = 0; i < hits; i++)
        {
            Collider collider = lzOverlapScratch[i];
            if (collider == null || IsTerrainSurface(collider, terrainMaterial))
            {
                continue;
            }

            obstacles++;
        }

        return obstacles;
    }

    /// <summary>
    /// Whether one static collider IS the ground rather than something standing on it. The primary
    /// test is the game's own: <c>TerrainScatter.GenerateScatter</c> recognises terrain by comparing
    /// a hit collider's physic material against <c>GameAssets.terrainMaterial</c>, so the same
    /// comparison is used here (Reuse rule 4). The type-name fallback covers a mission whose ground
    /// carries no material — naming the type rather than referencing it, because
    /// <c>TerrainCollider</c> lives in a Unity module this plugin does not otherwise need.
    /// </summary>
    private static bool IsTerrainSurface(Collider collider, PhysicMaterial? terrainMaterial)
    {
        if (terrainMaterial != null && collider.sharedMaterial == terrainMaterial)
        {
            return true;
        }

        return collider.GetType().Name == "TerrainCollider";
    }

    /// <summary>
    /// How steep the ground is at <paramref name="centre"/>, in degrees, by the same strategic
    /// height map the point discovery levelness pass reads. Zero when the map is not ready, so an
    /// unanswered question never blocks a flight.
    /// </summary>
    private static float GroundSlopeDegrees(GlobalPosition centre)
    {
        float x = (float)centre.x;
        float z = (float)centre.z;
        if (!CommanderSamSiteAnalyzerService.TryGetStrategicTerrainHeight(x, z, out _))
        {
            return 0f;
        }

        float normalY = Mathf.Clamp01(CommanderSamSiteAnalyzerService.EstimateStrategicTerrainNormalY(x, z));
        return Mathf.Acos(normalY) * Mathf.Rad2Deg;
    }

    /// <summary>What the scout found at one spot: the three measurements and the pure rule's
    /// verdict, plus the phrase the log line quotes.</summary>
    internal readonly struct LandingZoneReport
    {
        internal LandingZoneReport(int trees, int obstacles, float slopeDegrees, bool clear)
        {
            Trees = trees;
            Obstacles = obstacles;
            SlopeDegrees = slopeDegrees;
            Clear = clear;
        }

        internal int Trees { get; }
        internal int Obstacles { get; }
        internal float SlopeDegrees { get; }
        internal bool Clear { get; }

        /// <summary>Why this spot was refused, in the words the AI log prints. Empty when clear.</summary>
        internal string Reason
        {
            get
            {
                if (Clear)
                {
                    return string.Empty;
                }

                if (Trees > CommanderOperationsService.LzMaxTreesInClearRadius)
                {
                    return $"trees ({Trees} within {CommanderSettings.OperationsLzClearRadiusMeters:0} m)";
                }

                if (Obstacles > 0)
                {
                    return $"scenery ({Obstacles} obstacle"
                        + (Obstacles == 1 ? string.Empty : "s")
                        + $" within {CommanderSettings.OperationsLzClearRadiusMeters:0} m)";
                }

                return $"ground too steep ({SlopeDegrees:0}°, the game lands on {CommanderOperationsService.LzMaxSlopeDegrees:0}° or less)";
            }
        }
    }

    /// <summary>
    /// Measures one candidate landing zone and applies the pure rule. The one place the three
    /// probes meet; every caller — the request, the ring search and the point-discovery wooded flag
    /// — goes through it.
    /// </summary>
    internal static LandingZoneReport ScoutLandingZone(GlobalPosition lz)
    {
        float radius = Mathf.Max(1f, CommanderSettings.OperationsLzClearRadiusMeters);
        int trees = CountTreesNear(lz, radius);
        int obstacles = CountStaticObstaclesNear(lz, radius);
        float slope = GroundSlopeDegrees(lz);
        bool clear = CommanderOperationsService.IsLandingZoneClear(
            trees,
            obstacles,
            slope,
            CommanderOperationsService.LzMaxTreesInClearRadius,
            CommanderOperationsService.LzMaxSlopeDegrees);
        return new LandingZoneReport(trees, obstacles, slope, clear);
    }

    /// <summary>
    /// The nearest spot to <paramref name="desired"/> that the scout calls clear, searched ring by
    /// ring out to <c>OperationsLzSearchRadiusMeters</c> — the levelness nudge's own nearest-first
    /// walk over the same offsets, so a relocated landing zone is always the closest clear ground
    /// rather than the first one a different order happened to try. False when the whole search
    /// radius is blocked; the caller then declines and the picket drives.
    /// </summary>
    internal static bool TryFindClearLandingZone(GlobalPosition desired, out GlobalPosition clear)
    {
        clear = desired;
        float searchRadius = Mathf.Max(0f, CommanderSettings.OperationsLzSearchRadiusMeters);
        if (lzSearchOffsets.Count == 0)
        {
            CommanderStrategicPointService.EmitLevelSearchOffsets(
                LzSearchStepMeters, searchRadius, lzSearchOffsets);
        }

        for (int i = 0; i < lzSearchOffsets.Count; i++)
        {
            Vector2 offset = lzSearchOffsets[i];
            GlobalPosition candidate = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
                desired.x + offset.x,
                desired.y,
                desired.z + offset.y));
            if (CommanderGameAccess.IsBelowSeaLevel(candidate))
            {
                continue;
            }

            if (ScoutLandingZone(candidate).Clear)
            {
                clear = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a control point's own footprint is woodland — the flag point discovery stamps on a
    /// candidate so the operations side knows, before it ever plans a flight, that this point's
    /// pickets will have to be airdropped or driven. Measured over the point's ring rather than the
    /// 40 m clear radius, because the question is about the whole piece of ground, not one landing
    /// spot on it.
    /// </summary>
    internal static bool IsWoodedFootprint(GlobalPosition centre, float radiusMeters)
    {
        float radius = Mathf.Max(1f, radiusMeters);
        // The clear-radius threshold scaled by area: a ring ten times the radius of the landing
        // spot has a hundred times the ground, and calling it woodland on the same four trees would
        // mark half the map.
        float areaRatio = (radius * radius)
            / Mathf.Max(1f, CommanderSettings.OperationsLzClearRadiusMeters * CommanderSettings.OperationsLzClearRadiusMeters);
        int threshold = Mathf.CeilToInt(CommanderOperationsService.LzMaxTreesInClearRadius * areaRatio);
        return CountTreesNear(centre, radius) > threshold;
    }

    /// <summary>
    /// Turns a live insertion flight that cannot land into an airdrop: the stall path's preferred
    /// answer (user, 2026-09-14 — "we either need to check for that and do an airdrop (preferred)").
    /// Refuses unless every cargo still aboard has a parachute, because the game drops cargo the
    /// same way whether it has one or not and a parachute-less vehicle dropped from 200 m is a
    /// destroyed vehicle.
    /// <para>The altitude, the run-in and the release are all the game's own: with its private
    /// <c>airdrop</c> flag set — which <c>OverrideTransportTarget</c> already mirrors from the
    /// mission every cycle — <c>AIHeloTransportState.FixedUpdateState</c> holds
    /// <see cref="CommanderOperationsService.AirdropAltitudeMeters"/> (200 m, the game's own
    /// hard-coded value, verified in the decompile) and releases inside four seconds' flying time of
    /// the drop point. Nothing here sets an altitude.</para>
    /// </summary>
    /// <param name="flightId">Which ONE flight of a lift stalled, or
    /// <see cref="CommanderCargoFlightSlot.Unslotted"/> for the first flight standing on the point
    /// (lift-wave_20260916). Without it a wave of three would convert whichever transport the
    /// dictionary happened to walk first, not the one that could not get down.</param>
    internal bool TryConvertInsertionToAirdrop(
        FactionHQ hq, CommanderStrategicPoint point, int flightId = CommanderCargoFlightSlot.Unslotted)
    {
        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            CargoMission mission = entry.Value;
            Aircraft aircraft = entry.Key;
            if (mission.InsertionPoint == null
                || mission.Airdrop
                || mission.Cancelled
                || !ReferenceEquals(mission.Hq, hq)
                || !ReferenceEquals(mission.InsertionPoint, point)
                || !CommanderCargoFlightSlot.Matches(mission.InsertionFlightId, flightId)
                || aircraft == null
                || aircraft.disabled)
            {
                continue;
            }

            if (!LoadedCargoSupportsAirdrop(aircraft))
            {
                return false;
            }

            mission.Airdrop = true;
            // The destination fields were seeded for a landing; let the next override cycle build
            // the drop run from scratch rather than inherit a touchdown point and a landing timer.
            mission.Initialized = false;
            mission.LandingUnloadAt = 0f;
            mission.CargoClearancePending = false;
            CloseCargoDoors(mission);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether every piece of cargo still loaded on <paramref name="aircraft"/> can be dropped under
    /// a parachute. The runtime twin of <c>CargoMountSupportsAirdrop</c>, which asks the same
    /// question of a catalog mount before the flight exists; this asks it of the weapons actually
    /// hanging on the aircraft (Reuse rule 4 — one parachute field, read the same way in both).
    /// </summary>
    private static bool LoadedCargoSupportsAirdrop(Aircraft aircraft)
    {
        bool sawCargo = false;
        for (int s = 0; s < aircraft.weaponStations.Count; s++)
        {
            WeaponStation station = aircraft.weaponStations[s];
            if (station?.WeaponInfo == null || !station.WeaponInfo.cargo)
            {
                continue;
            }

            for (int w = 0; w < station.Weapons.Count; w++)
            {
                if (station.Weapons[w] is not MountedCargo cargo || cargo.GetAmmoLoaded() <= 0)
                {
                    continue;
                }

                sawCargo = true;
                Unit? unit = cargo.cargo?.unitPrefab != null
                    ? cargo.cargo.unitPrefab.GetComponent<Unit>()
                    : null;
                if (unit is GroundVehicle groundVehicle)
                {
                    if (GroundVehicleParachuteField?.GetValue(groundVehicle) == null)
                    {
                        return false;
                    }
                }
                else if (unit is Container container)
                {
                    if (ContainerParachuteField?.GetValue(container) == null)
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
        }

        return sawCargo;
    }
}
