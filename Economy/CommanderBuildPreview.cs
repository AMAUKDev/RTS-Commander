using System.Collections.Generic;
using RoadPathfinding;
using UnityEngine;
using UnityEngine.Rendering;

namespace GroundControlRts;

/// <summary>
/// The see-through building that follows the cursor while a placement is armed, green where the
/// site is legal and red where it is not.
/// </summary>
/// <remarks>
/// ponytail: the ghost is drawn with <see cref="Graphics.DrawMesh"/> straight off the prefab's own
/// meshes rather than by instantiating the prefab. Instantiating a building prefab runs its
/// <c>Awake</c>, which registers a networked <c>Unit</c> with the server — so the safe preview is
/// the one that never creates a GameObject at all. The cost is that a building whose look comes
/// from something other than a MeshRenderer (a particle effect, a light) does not show in the
/// preview, which for static structures is nothing.
/// </remarks>
internal sealed class CommanderBuildPreview
{
    /// <summary>Clear space demanded around a road centreline, on top of the building's own size.</summary>
    private const float RoadClearanceMeters = 8f;

    /// <summary>
    /// Clear space demanded around a runway or taxiway, on top of half the runway's own width and
    /// the building's footprint. Tighter than a road's because the whole build radius sits on top
    /// of an airfield — this has to keep structures off the tarmac without banning building at the
    /// base at all.
    /// </summary>
    private const float RunwayClearanceMeters = 30f;

    /// <summary>Extra reach past the capture ring within which an airbase's surfaces are checked at
    /// all. A cheap early-out so a build site nowhere near an airfield costs one distance test.</summary>
    private const float AirfieldCheckMarginMeters = 2000f;

    /// <summary>Smallest footprint a definition is treated as having, for definitions that author none.</summary>
    private const float MinimumFootprintMeters = 10f;

    /// <summary>Compass points probed around a dock site looking for water. Eight is enough to
    /// find a coastline; a finer sweep only costs raycasts to answer the same question.</summary>
    private static readonly Vector2[] ShoreProbeDirections =
    {
        new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
        new(0.7071f, 0.7071f), new(-0.7071f, 0.7071f), new(0.7071f, -0.7071f), new(-0.7071f, -0.7071f),
    };

    private static readonly Color ValidTint = new(0.30f, 1f, 0.45f, 0.40f);
    private static readonly Color BlockedTint = new(1f, 0.28f, 0.22f, 0.40f);

    private readonly Dictionary<BuildingDefinition, List<Part>> parts = new();
    private readonly Dictionary<BuildingDefinition, Vector3> footprints = new();
    private readonly Dictionary<Material, Material> ghostMaterials = new();
    private readonly List<Material> ownedMaterials = new();
    private readonly Collider[] overlapHits = new Collider[32];
    private readonly MaterialPropertyBlock tintBlock = new();

    /// <summary>Padded road bounding boxes, rebuilt when the level's road network changes.</summary>
    private RoadNetwork? boundsNetwork;
    private RoadBounds[] roadBounds = System.Array.Empty<RoadBounds>();

    private bool hasSite;
    private GlobalPosition site;

    /// <summary>The site under the cursor is legal to build on.</summary>
    internal bool SiteValid { get; private set; }

    /// <summary>Why the site is blocked, or empty when it is not.</summary>
    internal string BlockedReason { get; private set; } = string.Empty;

    internal void Hide()
    {
        hasSite = false;
        SiteValid = false;
        BlockedReason = string.Empty;
    }

    /// <summary>Drops every cached mesh list and the materials cloned for them.</summary>
    internal void Clear()
    {
        Hide();
        parts.Clear();
        footprints.Clear();
        boundsNetwork = null;
        roadBounds = System.Array.Empty<RoadBounds>();
        ghostMaterials.Clear();
        for (int i = 0; i < ownedMaterials.Count; i++)
        {
            Object.Destroy(ownedMaterials[i]);
        }
        ownedMaterials.Clear();
    }

    /// <summary>
    /// Per frame while a placement is armed: re-aim at the cursor, re-check the site and draw.
    /// The caller passes the armed definition every frame rather than arming the preview
    /// separately, so a new way to start a build cannot forget to switch the ghost with it.
    /// </summary>
    internal void Tick(BuildingDefinition definition, FactionHQ? hq, Vector2 screenPosition)
    {
        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition ground))
        {
            hasSite = false;
            SiteValid = false;
            BlockedReason = "No ground under the cursor.";
            return;
        }

        site = CommanderGameAccess.SnapToTerrain(ground);
        // A mine's ghost shows where the mine will actually land: on the nearest free resource
        // site, not wherever the cursor happens to be hovering. Evaluate re-snaps idempotently (a
        // site position snaps to itself), so this is purely so the ghost is not lying about it.
        if (CommanderEconomyService.IsMineDefinition(definition)
            && CommanderStrategicPointService.Instance?.TrySnapMineSite(site, out GlobalPosition snapped) == true)
        {
            site = snapped;
        }

        hasSite = true;
        SiteValid = Evaluate(definition, site, hq, out string reason);
        BlockedReason = reason;
        Draw(definition, site);
    }

    /// <summary>Stops drawing for this frame without disarming the placement.</summary>
    internal void Suspend()
    {
        hasSite = false;
    }

    /// <summary>True when a placement click at <paramref name="target"/> should be allowed.</summary>
    internal bool IsSiteAllowed(
        BuildingDefinition candidate, GlobalPosition target, FactionHQ? hq, out string reason)
    {
        return Evaluate(candidate, CommanderGameAccess.SnapToTerrain(target), hq, out reason);
    }

    /// <summary>
    /// A site is blocked by another unit's footprint, by a road, or by being outside the build
    /// radius of every airbase the faction holds, and by nothing else. Trees, rocks and terrain
    /// clutter carry no <see cref="Unit"/>, so they are ignored on purpose — clearing scenery to
    /// build is normal, bulldozing the highway is not. A gold mine carries one more rule on top:
    /// it must stand on a resource site (<see cref="CommanderStrategicPointService.TrySnapMineSite"/>),
    /// reachable from a held base or a currently garrisoned control point.
    /// </summary>
    private bool Evaluate(
        BuildingDefinition candidate, GlobalPosition target, FactionHQ? hq, out string reason)
    {
        // A naval dock has to reach the sea, which is usually outside the base perimeter, so it
        // gets its own radius and its own shoreline rule rather than loosening either for every
        // building. Everything else is the ordinary build radius.
        bool dock = CommanderEconomyService.IsNavalDockDefinition(candidate);
        bool mine = CommanderEconomyService.IsMineDefinition(candidate);
        float radiusKm = dock ? CommanderSettings.NavalDockRadiusKm : CommanderSettings.BuildRadiusKm;

        // Discovery and the enemy build-site search both call IsSiteAllowed with hq null purely to
        // ask "is this patch of ground clear", never to place a mine for real (see
        // CommanderStrategicPointDiscovery.TryFindSiteRing) — exactly the existing hq == null
        // convention below that already turns off the radius check for the same reason. Gating the
        // site/reach rule the same way keeps that convention single instead of forking it: without
        // this, discovery's own "is this candidate a legal site" probe would recurse into "is this
        // near an already-registered site", which is never true for the very first site on a map.
        // The site rule only binds once the map actually has sites. Before discovery finishes, or
        // on a map that produced none, a mine goes anywhere in reach as it always did — see
        // CommanderStrategicPointService.HasResourceSites.
        CommanderStrategicPointService? pointService = CommanderStrategicPointService.Instance;
        if (mine && hq != null && pointService != null && pointService.HasResourceSites)
        {
            if (pointService.TrySnapMineSite(target, out GlobalPosition site))
            {
                target = site;
            }
            else
            {
                reason = "Blocked: a gold mine has to stand on a resource site.";
                return false;
            }
        }

        // Building is tied to ground the faction actually holds, so a commander cannot drop a
        // refinery in the enemy's rear. Same rule for the enemy commander, which reaches this
        // through IsSiteAllowed. A mine gets one more way in: a currently garrisoned control point
        // counts as reach too, exactly as design SS2 asks.
        if (hq != null
            && !IsInsideBuildRadius(hq, target, radiusKm)
            && !(mine
                && CommanderStrategicPointService.Instance?.IsInsideGarrisonedPointReach(hq, target, radiusKm) == true))
        {
            reason = $"Blocked: more than {radiusKm:0.#} km from a captured base.";
            return false;
        }

        if (dock && !IsShoreline(target))
        {
            reason = "Blocked: a naval dock has to stand on dry land at the water's edge.";
            return false;
        }

        Vector3 footprint = GetFootprint(candidate);
        Vector3 center = target.ToLocalPosition() + Vector3.up * footprint.y;
        Vector3 extents = new(footprint.x, Mathf.Max(footprint.y, 5f), footprint.z);

        int hits = Physics.OverlapBoxNonAlloc(
            center,
            extents,
            overlapHits,
            Quaternion.identity,
            ~0,
            QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits; i++)
        {
            Collider hit = overlapHits[i];
            Unit? occupant = hit == null ? null : hit.GetComponentInParent<Unit>();
            if (occupant != null && !occupant.disabled)
            {
                reason = $"Blocked by {CommanderGameAccess.GetUnitLabel(occupant)}.";
                return false;
            }
        }

        float span = Mathf.Max(footprint.x, footprint.z);
        if (IsOnRoad(target, span + RoadClearanceMeters))
        {
            reason = "Blocked: too close to a road.";
            return false;
        }

        if (IsOnAirfieldSurface(target, span + RunwayClearanceMeters))
        {
            reason = "Blocked: that is a runway or taxiway.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// True when the site sits on an airbase's operating surface — a runway or a taxiway, for any
    /// airbase on the map, not only the ones this faction holds.
    /// </summary>
    /// <remarks>
    /// Runways and taxiways are terrain, not <see cref="Unit"/>s, so the overlap test above never
    /// saw them and neither did the road test: an airfield's taxi network is its own
    /// <c>RoadNetwork</c>, not part of <c>LevelInfo.roadNetwork</c>. That is how a gold mine ended
    /// up on the landing strip, which then had nowhere to land. Every airbase counts, because a
    /// base you are about to capture is one you are about to want to fly from.
    /// </remarks>
    private static bool IsOnAirfieldSurface(GlobalPosition target, float clearance)
    {
        if (FactionRegistry.airbaseLookup == null)
        {
            return false;
        }

        Vector3 local = target.ToLocalPosition();
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float reach = airbase.GetRadius() + AirfieldCheckMarginMeters;
            Vector3 offset = airbase.center.position - local;
            offset.y = 0f;
            if (offset.sqrMagnitude > reach * reach)
            {
                continue;
            }

            Airbase.Runway[]? runways = airbase.runways;
            for (int i = 0; runways != null && i < runways.Length; i++)
            {
                Airbase.Runway runway = runways[i];
                if (runway?.Start == null || runway.End == null)
                {
                    continue;
                }

                float limit = clearance + runway.GetWidth() * 0.5f;
                if (SegmentDistanceSquared(
                        target,
                        runway.Start.position.ToGlobalPosition(),
                        runway.End.position.ToGlobalPosition())
                    <= limit * limit)
                {
                    return true;
                }
            }

            RoadNetwork? taxi = airbase.GetTaxiNetwork();
            if (taxi != null && taxi.Exists() && IsOnNetwork(taxi, target, clearance))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Segment test against any road network, with no bounding-box cache. Taxi networks
    /// are a handful of short roads inside one airfield, so the cache the level network needs would
    /// cost more than it saves.</summary>
    private static bool IsOnNetwork(RoadNetwork network, GlobalPosition target, float clearance)
    {
        float clearanceSquared = clearance * clearance;
        for (int roadIndex = 0; roadIndex < network.roads.Count; roadIndex++)
        {
            Road road = network.roads[roadIndex];
            if (road?.points == null || road.points.Count < 2)
            {
                continue;
            }

            for (int i = 1; i < road.points.Count; i++)
            {
                if (SegmentDistanceSquared(target, road.points[i - 1], road.points[i]) <= clearanceSquared)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when the site sits within the build radius of an airbase this faction holds. A faction
    /// that holds no airbase can build nothing — which is also the point at which it has lost.
    /// </summary>
    internal static bool IsInsideBuildRadius(FactionHQ hq, GlobalPosition target)
    {
        return IsInsideBuildRadius(hq, target, CommanderSettings.BuildRadiusKm);
    }

    internal static bool IsInsideBuildRadius(FactionHQ hq, GlobalPosition target, float radiusKm)
    {
        float radius = Mathf.Max(radiusKm, 0.1f) * 1000f;
        float radiusSquared = radius * radius;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            Vector3 offset = airbase.center.position - target.ToLocalPosition();
            offset.y = 0f;
            if (offset.sqrMagnitude <= radiusSquared)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True on dry land with navigable water within reach: the site itself has to sit above the sea
    /// plane, and at least one probe around it has to come back below it. Ground height comes from
    /// <see cref="CommanderGameAccess.SnapToTerrain"/>, so over water it returns the seabed and
    /// <see cref="CommanderGameAccess.IsBelowSeaLevel"/> is the whole water test — no water
    /// collider needed.
    /// </summary>
    internal static bool IsShoreline(GlobalPosition target)
    {
        if (CommanderGameAccess.IsBelowSeaLevel(CommanderGameAccess.SnapToTerrain(target)))
        {
            return false;
        }

        float reach = Mathf.Max(20f, CommanderSettings.NavalDockShoreMeters);
        for (int i = 0; i < ShoreProbeDirections.Length; i++)
        {
            Vector2 direction = ShoreProbeDirections[i];
            GlobalPosition probe = new(
                target.x + direction.x * reach,
                target.y,
                target.z + direction.y * reach);
            if (CommanderGameAccess.IsBelowSeaLevel(CommanderGameAccess.SnapToTerrain(probe)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Half-extents of the building. The authored <c>width</c>/<c>length</c>/<c>height</c> are the
    /// numbers the game shows in its encyclopedia, and for most buildings they are either far
    /// smaller than the structure or not authored at all — which is how a refinery whose authored
    /// footprint fell back to the 10 m minimum was allowed to straddle a highway. So the prefab's
    /// own meshes get a vote: whichever is larger wins. Cached per definition; a prefab does not
    /// change size.
    /// </summary>
    private Vector3 GetFootprint(BuildingDefinition candidate)
    {
        if (footprints.TryGetValue(candidate, out Vector3 cached))
        {
            return cached;
        }

        Vector3 footprint = new(
            Mathf.Max(candidate.width, MinimumFootprintMeters) * 0.5f,
            Mathf.Max(candidate.height, MinimumFootprintMeters) * 0.5f,
            Mathf.Max(candidate.length, MinimumFootprintMeters) * 0.5f);

        List<Part> meshes = GetParts(candidate);
        bool any = false;
        Bounds bounds = default;
        for (int i = 0; i < meshes.Count; i++)
        {
            Bounds local = meshes[i].Mesh.bounds;
            // Eight corners rather than the centre and extents: the part matrix can rotate.
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = local.center + Vector3.Scale(
                    local.extents,
                    new Vector3(
                        (corner & 1) == 0 ? -1f : 1f,
                        (corner & 2) == 0 ? -1f : 1f,
                        (corner & 4) == 0 ? -1f : 1f));
                point = meshes[i].Local.MultiplyPoint3x4(point);
                if (any)
                {
                    bounds.Encapsulate(point);
                }
                else
                {
                    bounds = new Bounds(point, Vector3.zero);
                    any = true;
                }
            }
        }

        if (any)
        {
            footprint = Vector3.Max(footprint, bounds.extents);
        }

        footprints[candidate] = footprint;
        return footprint;
    }

    /// <summary>
    /// Distance to the nearest road centreline, measured against the segments between road points
    /// rather than the points themselves: road points are tens of metres apart, so a point test
    /// lets a building straddle the tarmac between two of them.
    /// </summary>
    private bool IsOnRoad(GlobalPosition target, float clearance)
    {
        RoadNetwork? network = NetworkSceneSingleton<LevelInfo>.i?.roadNetwork;
        if (network == null)
        {
            return false;
        }

        EnsureRoadBounds(network);
        float clearanceSquared = clearance * clearance;
        for (int roadIndex = 0; roadIndex < network.roads.Count; roadIndex++)
        {
            Road road = network.roads[roadIndex];
            // Road.InBounds is the road's own bounding box with no margin at all, so a straight
            // road has a box one point wide and every site beside it passed the test without the
            // clearance check ever running. That is how the enemy commander parked a refinery on
            // its own supply road. Same early-out, grown by the clearance being asked about.
            if (road == null
                || road.points.Count < 2
                || roadIndex >= roadBounds.Length
                || !roadBounds[roadIndex].Contains(target, clearance))
            {
                continue;
            }

            for (int i = 1; i < road.points.Count; i++)
            {
                if (SegmentDistanceSquared(target, road.points[i - 1], road.points[i]) <= clearanceSquared)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Caches one horizontal bounding box per road. Roads are level data and never move, so this
    /// runs once per level; the alternative is scanning every point of every road on every frame
    /// the build ghost is up.
    /// </summary>
    private void EnsureRoadBounds(RoadNetwork network)
    {
        if (ReferenceEquals(boundsNetwork, network) && roadBounds.Length == network.roads.Count)
        {
            return;
        }

        boundsNetwork = network;
        roadBounds = new RoadBounds[network.roads.Count];
        for (int roadIndex = 0; roadIndex < network.roads.Count; roadIndex++)
        {
            Road road = network.roads[roadIndex];
            if (road == null || road.points.Count == 0)
            {
                continue;
            }

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < road.points.Count; i++)
            {
                GlobalPosition point = road.points[i];
                minX = Mathf.Min(minX, point.x);
                maxX = Mathf.Max(maxX, point.x);
                minZ = Mathf.Min(minZ, point.z);
                maxZ = Mathf.Max(maxZ, point.z);
            }

            roadBounds[roadIndex] = new RoadBounds(minX, maxX, minZ, maxZ);
        }
    }

    /// <summary>Horizontal distance squared from a point to a segment. Height is irrelevant here.</summary>
    private static float SegmentDistanceSquared(GlobalPosition point, GlobalPosition a, GlobalPosition b)
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

    private void Draw(BuildingDefinition candidate, GlobalPosition target)
    {
        if (!hasSite)
        {
            return;
        }

        List<Part> meshes = GetParts(candidate);
        if (meshes.Count == 0)
        {
            return;
        }

        Color tint = SiteValid ? ValidTint : BlockedTint;
        tintBlock.SetColor("_BaseColor", tint);
        tintBlock.SetColor("_Color", tint);

        Matrix4x4 root = Matrix4x4.TRS(
            target.ToLocalPosition() + candidate.spawnOffset,
            Quaternion.identity,
            Vector3.one);
        for (int i = 0; i < meshes.Count; i++)
        {
            Part part = meshes[i];
            Matrix4x4 matrix = root * part.Local;
            for (int sub = 0; sub < part.Materials.Length; sub++)
            {
                if (part.Materials[sub] == null || sub >= part.Mesh.subMeshCount)
                {
                    continue;
                }

                Graphics.DrawMesh(part.Mesh, matrix, part.Materials[sub], 0, null, sub, tintBlock);
            }
        }
    }

    private List<Part> GetParts(BuildingDefinition candidate)
    {
        if (parts.TryGetValue(candidate, out List<Part> cached))
        {
            return cached;
        }

        cached = new List<Part>();
        parts[candidate] = cached;
        GameObject prefab = candidate.unitPrefab;
        if (prefab == null)
        {
            return cached;
        }

        // Every LOD past the first draws the same building again at lower detail, so drawing them
        // all would render the structure three times over on top of itself.
        HashSet<Renderer> lowDetail = new();
        LODGroup[] groups = prefab.GetComponentsInChildren<LODGroup>(true);
        for (int i = 0; i < groups.Length; i++)
        {
            LOD[] levels = groups[i].GetLODs();
            for (int level = 1; level < levels.Length; level++)
            {
                Renderer[] renderers = levels[level].renderers;
                for (int r = 0; r < renderers.Length; r++)
                {
                    if (renderers[r] != null)
                    {
                        lowDetail.Add(renderers[r]);
                    }
                }
            }
        }

        Transform root = prefab.transform;
        MeshFilter[] filters = prefab.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter filter = filters[i];
            if (filter.sharedMesh == null
                || !filter.TryGetComponent(out MeshRenderer renderer)
                || lowDetail.Contains(renderer))
            {
                continue;
            }

            Material[] source = renderer.sharedMaterials;
            Material?[] ghost = new Material?[source.Length];
            for (int m = 0; m < source.Length; m++)
            {
                ghost[m] = GetGhostMaterial(source[m]);
            }

            cached.Add(new Part(
                filter.sharedMesh,
                ghost,
                root.worldToLocalMatrix * filter.transform.localToWorldMatrix));
        }

        return cached;
    }

    /// <summary>
    /// A see-through clone of the building's own material. Both the URP and the Standard property
    /// names are written because a property a shader does not have is silently ignored; if this
    /// game's shaders honour neither, the ghost simply renders as a solid green or red building,
    /// which still says what it needs to say.
    /// </summary>
    private Material? GetGhostMaterial(Material? source)
    {
        if (source == null)
        {
            return null;
        }

        if (ghostMaterials.TryGetValue(source, out Material existing))
        {
            return existing;
        }

        Material ghost = new(source)
        {
            renderQueue = (int)RenderQueue.Transparent,
        };
        ghost.SetFloat("_Surface", 1f);
        ghost.SetFloat("_Mode", 3f);
        ghost.SetFloat("_Blend", 0f);
        ghost.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        ghost.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        ghost.SetFloat("_ZWrite", 0f);
        ghost.DisableKeyword("_ALPHATEST_ON");
        ghost.EnableKeyword("_ALPHABLEND_ON");
        ghost.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

        ghostMaterials[source] = ghost;
        ownedMaterials.Add(ghost);
        return ghost;
    }

    /// <summary>
    /// The road early-out is the whole reason a building could sit on a road, so it gets the one
    /// runnable check. A road running due north has a bounding box with no width at all, and the
    /// margin is what has to make it reach the site beside it.
    /// </summary>
    internal static void SelfCheck()
    {
        RoadBounds straight = new(100f, 100f, 0f, 500f);
        GlobalPosition beside = new(120f, 0f, 250f);
        if (!straight.Contains(beside, 30f))
        {
            CommanderPlugin.Log.LogError(
                "Build site self-check FAILED: a site 20 m off a straight road is not caught by a "
                    + "30 m clearance, so buildings can be placed on roads.");
        }

        if (straight.Contains(beside, 10f))
        {
            CommanderPlugin.Log.LogError(
                "Build site self-check FAILED: the road bounds margin is not being applied.");
        }

        // The runway rule is the same segment measure. A point beside the middle of a runway is the
        // case the point-only test used to miss, and it is what put a gold mine on a landing strip.
        GlobalPosition threshold = new(0f, 0f, 0f);
        GlobalPosition farEnd = new(0f, 0f, 2000f);
        GlobalPosition onTheStrip = new(10f, 0f, 1000f);
        if (SegmentDistanceSquared(onTheStrip, threshold, farEnd) > 40f * 40f)
        {
            CommanderPlugin.Log.LogError(
                "Build site self-check FAILED: a point 10 m off the middle of a 2 km runway does not "
                    + "measure as being on it, so buildings can be placed on runways.");
        }

        GlobalPosition wellClear = new(200f, 0f, 1000f);
        if (SegmentDistanceSquared(wellClear, threshold, farEnd) <= 40f * 40f)
        {
            CommanderPlugin.Log.LogError(
                "Build site self-check FAILED: the runway measure is not bounded, so nothing can be "
                    + "built anywhere near an airfield.");
        }
    }

    /// <summary>A road's horizontal extent, tested with a margin the caller supplies.</summary>
    private readonly struct RoadBounds
    {
        private readonly float minX;
        private readonly float maxX;
        private readonly float minZ;
        private readonly float maxZ;

        internal RoadBounds(float minX, float maxX, float minZ, float maxZ)
        {
            this.minX = minX;
            this.maxX = maxX;
            this.minZ = minZ;
            this.maxZ = maxZ;
        }

        internal bool Contains(GlobalPosition point, float margin)
        {
            return point.x >= minX - margin
                && point.x <= maxX + margin
                && point.z >= minZ - margin
                && point.z <= maxZ + margin;
        }
    }

    private readonly struct Part
    {
        internal Part(Mesh mesh, Material?[] materials, Matrix4x4 local)
        {
            Mesh = mesh;
            Materials = materials;
            Local = local;
        }

        internal Mesh Mesh { get; }

        internal Material?[] Materials { get; }

        internal Matrix4x4 Local { get; }
    }
}
