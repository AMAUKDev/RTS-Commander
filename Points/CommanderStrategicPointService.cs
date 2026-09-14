using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Every resource site, village, hilltop and base on the map, what each one pays and who holds
/// it. A site pays only once a mine stands on it; a village or hilltop pays whoever keeps at
/// least <c>MinGarrison</c> ground vehicles alone in its ring for <c>HoldSeconds</c>; a base pays
/// its live holder. Discovery runs once per mission, host only, budgeted across frames the way the
/// SAM analyzer bakes its height map; the hold check and the income tick both run on top of that
/// one discovered list for the rest of the match.
/// </summary>
/// <remarks>
/// ponytail: owners are not persisted across a mission reload — a village flips back to neutral
/// and a site's mine (which is itself not persisted) simply is not there any more. Same stance as
/// mine levels, <see cref="CommanderEconomyService"/> remarks.
/// </remarks>
internal sealed partial class CommanderStrategicPointService : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>How often the village/hilltop ring is counted. 5 s: fast enough that a garrison
    /// wipe is noticed well inside a 60 s hold window, slow enough that it costs nothing next to
    /// the 15 s income tick it shares a service with.</summary>
    private const float HoldCheckSeconds = 5f;

    /// <summary>Every discovered point, in the order discovery found it.</summary>
    private readonly List<CommanderStrategicPoint> points = new();

    /// <summary>Public read of <see cref="points"/> for markers, the AI and the settings/log UI.</summary>
    internal IReadOnlyList<CommanderStrategicPoint> Points => points;

    /// <summary>
    /// The registry order the village/hilltop hold indices refer to, refreshed at the top of every
    /// hold tick. A <see cref="HoldState"/> index is only valid for the snapshot it was set against
    /// — see <c>TickHold</c>.
    /// </summary>
    private readonly List<FactionHQ> hqOrder = new();

    /// <summary>Discovery state machine, one state advanced per <c>TickPersistent</c> call while
    /// discovery is running (the SAM analyzer's Waiting/Sampling/Coverage shape).</summary>
    private enum DiscoveryState
    {
        Waiting,
        Sites,
        Villages,
        Hilltops,
        Roads,
        Bases,
        Done,
    }

    private DiscoveryState discovery = DiscoveryState.Waiting;

    private float nextHoldAt = CommanderScheduler.Stagger("points.hold", HoldCheckSeconds);

    /// <summary>Per-faction ring counts, reused across every point in one hold tick rather than
    /// allocated per point; resized only when the HQ registry's membership changes.</summary>
    private int[] holdCounts = System.Array.Empty<int>();

    internal static CommanderStrategicPointService? Instance { get; private set; }

    /// <summary>The point a click focused, for the selection-bar card (T13). Null clears the card.</summary>
    internal CommanderStrategicPoint? FocusedPoint { get; private set; }

    internal CommanderStrategicPointService()
    {
        Instance = this;
    }

    /// <summary>Live HQ at a hold-state index, or null for -1 or a stale index (the registry
    /// changed since; the next hold tick will notice and reset). The one place a village/hilltop
    /// index turns back into a <see cref="FactionHQ"/>.</summary>
    internal FactionHQ? HqAt(int index)
    {
        return index >= 0 && index < hqOrder.Count ? hqOrder[index] : null;
    }

    /// <summary>This HQ's ground-vehicle count in <paramref name="point"/>'s ring as of the last
    /// hold tick — the UI-facing wrapper around <see cref="CommanderStrategicPoint.PresentCount"/>,
    /// which needs the HQ-order snapshot only this service holds.</summary>
    internal int GetPresentCount(CommanderStrategicPoint point, FactionHQ hq) => point.PresentCount(hq, hqOrder);

    public void TickPersistent()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null || !localHq.IsServer)
        {
            return;
        }

        if (discovery != DiscoveryState.Done)
        {
            StepDiscovery();
            return;
        }

        if (CommanderScheduler.IsDue(ref nextHoldAt, HoldCheckSeconds))
        {
            TickHold();
        }
    }

    /// <summary>
    /// True once discovery has produced at least one resource site. While false — before
    /// discovery finishes, or on a map that genuinely has none — the mine rule falls back to
    /// "anywhere within reach", because a mine that can be built nowhere is a broken economy, not
    /// a design.
    /// </summary>
    internal bool HasResourceSites
    {
        get
        {
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].Kind == StrategicPointKind.Site)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void ResetSession()
    {
        points.Clear();
        hqOrder.Clear();
        discovery = DiscoveryState.Waiting;
        discoveryAttempts = 0;
        discoveryRetryAt = 0f;
        nextHoldAt = CommanderScheduler.Stagger("points.hold", HoldCheckSeconds);
        FocusedPoint = null;
        // The road-network pass (outposts, crossroads, roadside points) keeps state across several
        // TickPersistent calls the way the fill/hilltop passes already do, but unlike those it has
        // its own sub-state machine — reset it explicitly so a new mission does not resume the last
        // one's junction/roadside walk part way through.
        outpostCandidates.Clear();
        crossroadsCandidates.Clear();
        roadsideCandidates.Clear();
        roadPointLists.Clear();
        junctionNodes.Clear();
        roadsIndex = 0;
        roadsSub = RoadsSubState.CollectAndMergeEndpoints;
        // Every point (and therefore every hold/garrison log line) is about to be rediscovered
        // from scratch, so last mission's decisions are no longer about anything on the map.
        CommanderAiLog.Clear();
    }

    private void StepDiscovery()
    {
        switch (discovery)
        {
            case DiscoveryState.Waiting:
                StepWaiting();
                break;
            case DiscoveryState.Sites:
                StepSites();
                break;
            case DiscoveryState.Villages:
                StepVillages();
                break;
            case DiscoveryState.Hilltops:
                StepHilltops();
                break;
            case DiscoveryState.Roads:
                StepRoads();
                break;
            case DiscoveryState.Bases:
                StepBases();
                break;
        }
    }

    private void TickHold()
    {
        RefreshHqOrder();
        if (holdCounts.Length != hqOrder.Count)
        {
            holdCounts = new int[hqOrder.Count];
        }

        for (int p = 0; p < points.Count; p++)
        {
            CommanderStrategicPoint point = points[p];
            if (point.Kind == StrategicPointKind.Site)
            {
                if (point.Mine != null && point.Mine.disabled)
                {
                    // Design SS2: "Mine destroyed -> site free."
                    point.Mine = null;
                }

                continue;
            }

            if (!StrategicPointKinds.IsControlPoint(point.Kind))
            {
                continue;
            }

            for (int h = 0; h < hqOrder.Count; h++)
            {
                holdCounts[h] = CountPresent(hqOrder[h], point);
            }

            int qualifying = QualifyingFaction(holdCounts, CommanderSettings.PointsMinGarrison, out bool contested);
            int previousOwner = point.Hold.OwnerIndex;
            Step(ref point.Hold, qualifying, contested, HoldCheckSeconds, CommanderSettings.PointsHoldSeconds);
            point.GarrisonCounts = (int[])holdCounts.Clone();

            if (point.Hold.OwnerIndex != previousOwner)
            {
                FactionHQ? newOwner = HqAt(point.Hold.OwnerIndex);
                if (newOwner != null)
                {
                    // The new owner's tab is the one that cares it just took this point.
                    CommanderAiLog.Note(newOwner, $"{point.Label} held by {newOwner.faction.name}.");
                }
                else
                {
                    // Gone neutral: the faction that just lost it is the one whose tab should say
                    // so. No previous owner at all (a stale index reset, never a real capture) has
                    // no faction to file it under, so it stays a plain LogInfo.
                    string text = $"{point.Label} is neutral.";
                    FactionHQ? previous = HqAt(previousOwner);
                    if (previous != null)
                    {
                        CommanderAiLog.Note(previous, text);
                    }
                    else
                    {
                        CommanderPlugin.Log.LogInfo(text);
                    }
                }
            }
        }
    }

    /// <summary>Ground vehicles of <paramref name="hq"/> standing inside <paramref name="point"/>'s
    /// ring right now. Aircraft are excluded by the type test alone (design SS2: "any ground vehicle
    /// (including mobile AA)" counts, nothing that flies does).</summary>
    private static int CountPresent(FactionHQ hq, CommanderStrategicPoint point)
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
                && unit is GroundVehicle
                && !unit.disabled
                && FastMath.InRange(unit.transform.GlobalPosition(), point.Position, point.Radius))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Refreshes the HQ-order snapshot control-point hold indices refer to. A changed
    /// membership (a faction eliminated, a rejoin) invalidates every outstanding index, so every
    /// control point is reset to neutral rather than risk one faction's index now meaning
    /// another's.</summary>
    private void RefreshHqOrder()
    {
        bool changed = false;
        int index = 0;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || hq.faction == null)
            {
                continue;
            }

            if (index >= hqOrder.Count || !ReferenceEquals(hqOrder[index], hq))
            {
                changed = true;
            }

            index++;
        }

        if (index != hqOrder.Count)
        {
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        hqOrder.Clear();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq != null && hq.faction != null)
            {
                hqOrder.Add(hq);
            }
        }

        for (int i = 0; i < points.Count; i++)
        {
            if (StrategicPointKinds.IsControlPoint(points[i].Kind))
            {
                points[i].Hold = new HoldState { OwnerIndex = -1, CandidateIndex = -1, Progress = 0f, Contested = false };
            }
        }
    }

    /// <summary>
    /// Which faction, if any, qualifies to hold a point: the single faction present with at least
    /// <paramref name="minGarrison"/> ground vehicles, alone. Two or more factions present at all
    /// (regardless of count) is a contest, which always wins over qualifying — pure, for the
    /// self-check.
    /// </summary>
    internal static int QualifyingFaction(IReadOnlyList<int> counts, int minGarrison, out bool contested)
    {
        int presentFactions = 0;
        int qualifyingIndex = -1;
        int qualifyingFactions = 0;
        for (int i = 0; i < counts.Count; i++)
        {
            if (counts[i] > 0)
            {
                presentFactions++;
            }

            if (counts[i] >= minGarrison)
            {
                qualifyingIndex = i;
                qualifyingFactions++;
            }
        }

        contested = presentFactions >= 2;
        return !contested && qualifyingFactions == 1 ? qualifyingIndex : -1;
    }

    /// <summary>
    /// Horizontal distance from <paramref name="position"/> to the nearest segment of any of
    /// <paramref name="roads"/>' polylines — the picket-insertion road gate's measure (design.md,
    /// heli-picket-insertion_20260913, Decision 7). <c>float.MaxValue</c> when the map kept no
    /// roads, which the gate reads as off-road, so a roadless map flies every picket. Pure, for the
    /// self-check; the shared <see cref="SegmentDistanceSquared"/> (discovery's own) is the
    /// segment measure.
    /// </summary>
    internal static float NearestRoadDistanceMeters(
        IReadOnlyList<List<GlobalPosition>> roads, GlobalPosition position)
    {
        return TryNearestRoadPoint(roads, position, float.MaxValue, out _, out float distance)
            ? distance
            : float.MaxValue;
    }

    /// <summary>
    /// The nearest point ON a road to <paramref name="position"/>, not merely how far away it is —
    /// the same single walk of the retained polylines, generalised to hand back WHERE the road is
    /// (Reuse rule 5). The ground-tactics screen reads it: a screen belongs on the approach it
    /// watches, and an approach is a road. False when the map kept no roads or the nearest one is
    /// past <paramref name="maxMeters"/>, which the callers read as "there is no road here".
    /// Pure, for the self-check.
    /// </summary>
    internal static bool TryNearestRoadPoint(
        IReadOnlyList<List<GlobalPosition>> roads,
        GlobalPosition position,
        float maxMeters,
        out GlobalPosition closest,
        out float distanceMeters)
    {
        closest = default;
        float bestSquared = float.MaxValue;
        for (int r = 0; r < roads.Count; r++)
        {
            List<GlobalPosition> points = roads[r];
            for (int i = 1; i < points.Count; i++)
            {
                float segment = SegmentDistanceSquared(position, points[i - 1], points[i]);
                if (segment < bestSquared)
                {
                    bestSquared = segment;
                    closest = ClosestPointOnSegment(position, points[i - 1], points[i]);
                }
            }
        }

        distanceMeters = bestSquared == float.MaxValue ? float.MaxValue : Mathf.Sqrt(bestSquared);
        return distanceMeters <= maxMeters;
    }

    /// <summary>
    /// The point on segment a–b closest to <paramref name="point"/>, horizontally, with its height
    /// interpolated along the segment. The projection <see cref="SegmentDistanceSquared"/> measures
    /// but does not return; that one is left exactly as it is because it is on the discovery walk's
    /// hot path and every caller of it wants only the distance.
    /// </summary>
    private static GlobalPosition ClosestPointOnSegment(GlobalPosition point, GlobalPosition a, GlobalPosition b)
    {
        float abX = b.x - a.x;
        float abZ = b.z - a.z;
        float lengthSquared = abX * abX + abZ * abZ;
        float travel = lengthSquared <= 0.0001f
            ? 0f
            : Mathf.Clamp01(((point.x - a.x) * abX + (point.z - a.z) * abZ) / lengthSquared);
        return new GlobalPosition(a.x + abX * travel, a.y + (b.y - a.y) * travel, a.z + abZ * travel);
    }

    /// <summary>The live wrapper the insertion gate reads: the road polylines discovery retained
    /// (<c>roadPointLists</c>, built in <c>Points/CommanderStrategicPointDiscovery.cs</c>).</summary>
    internal float NearestRoadDistanceMeters(GlobalPosition position)
    {
        return NearestRoadDistanceMeters(roadPointLists, position);
    }

    /// <summary>The live wrapper the ground-tactics screen reads, over the same retained
    /// polylines.</summary>
    internal bool TryNearestRoadPoint(GlobalPosition position, float maxMeters, out GlobalPosition closest)
    {
        return TryNearestRoadPoint(roadPointLists, position, maxMeters, out closest, out _);
    }

    /// <summary>
    /// The take-and-hold state machine, pure and driven once per <see cref="HoldCheckSeconds"/>
    /// tick. Order of the rules matters: contested freezes everything first; then neutral or below
    /// minimum resets to neutral; then the current owner staying qualified resets any stray
    /// candidate; only then does a new candidate accumulate <see cref="HoldState.Progress"/> toward
    /// taking the point. Pure, for the self-check.
    /// </summary>
    internal static void Step(ref HoldState state, int qualifying, bool contested, float deltaSeconds, float holdSeconds)
    {
        state.Contested = contested;
        if (contested)
        {
            return;
        }

        if (qualifying < 0)
        {
            state.OwnerIndex = -1;
            state.CandidateIndex = -1;
            state.Progress = 0f;
            return;
        }

        if (qualifying == state.OwnerIndex)
        {
            state.CandidateIndex = -1;
            state.Progress = 0f;
            return;
        }

        if (qualifying != state.CandidateIndex)
        {
            state.CandidateIndex = qualifying;
            state.Progress = 0f;
        }

        state.Progress += deltaSeconds;
        if (state.Progress >= holdSeconds)
        {
            state.OwnerIndex = qualifying;
            state.CandidateIndex = -1;
            state.Progress = 0f;
        }
    }

    /// <summary>True when a point currently pays its owner: it has one and nobody is contesting it.
    /// Pure, for the self-check.</summary>
    internal static bool Pays(in HoldState state) => state.OwnerIndex >= 0 && !state.Contested;

    private readonly List<CommanderStrategicPoint> siteScratch = new();
    private readonly List<float> siteScratchDistances = new();
    private readonly List<bool> siteScratchFree = new();

    /// <summary>
    /// The mine-snap rule, pure: the nearest free entry within <paramref name="snapMeters"/>, or -1.
    /// A distance exactly at <paramref name="snapMeters"/> counts as inside — the boundary is
    /// inclusive, the convention every reach test in the mod uses (also
    /// <c>Operations.CommanderOperationsService.IsFrontPoint</c>'s front-range test).
    /// One definition; <see cref="TrySnapMineSite"/> is its only caller. Pure, for the self-check.
    /// </summary>
    internal static int NearestFreeSiteIndex(IReadOnlyList<float> distances, IReadOnlyList<bool> free, float snapMeters)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < distances.Count; i++)
        {
            if (!free[i] || distances[i] > snapMeters)
            {
                continue;
            }

            if (distances[i] < bestDistance)
            {
                bestDistance = distances[i];
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// The nearest free resource site to <paramref name="target"/>, within
    /// <c>Points/MineSnapMeters</c> — the one place a mine's target position turns into "which
    /// site", called from both the ghost (<c>CommanderBuildPreview.Tick</c>/<c>Evaluate</c>) and the
    /// spawn path (<c>CommanderEconomyService.SpawnMine</c>).
    /// </summary>
    internal bool TrySnapMineSite(GlobalPosition target, out GlobalPosition site)
    {
        siteScratch.Clear();
        siteScratchDistances.Clear();
        siteScratchFree.Clear();
        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            if (point.Kind != StrategicPointKind.Site)
            {
                continue;
            }

            siteScratch.Add(point);
            siteScratchDistances.Add(HorizontalDistance(target, point.Position));
            siteScratchFree.Add(point.Mine == null || point.Mine.disabled);
        }

        int index = NearestFreeSiteIndex(siteScratchDistances, siteScratchFree, CommanderSettings.PointsMineSnapMeters);
        if (index < 0)
        {
            site = default;
            return false;
        }

        site = siteScratch[index].Position;
        return true;
    }

    /// <summary>Marks the site at (within a metre of) <paramref name="site"/> as occupied by
    /// <paramref name="mine"/>. Called once, right after <c>SpawnBuilding</c> succeeds.</summary>
    internal void AttachMine(GlobalPosition site, Unit mine)
    {
        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            if (point.Kind == StrategicPointKind.Site && HorizontalDistance(point.Position, site) <= 1f)
            {
                point.Mine = mine;
                return;
            }
        }
    }

    /// <summary>
    /// Reattaches a mine whose <c>Unit</c> reference survived a hot reload (see
    /// <see cref="CommanderEconomyService"/>'s persistence) to the nearest free resource site
    /// within <paramref name="maxDistanceMeters"/> of it, so the site reads as taken again instead
    /// of free — design §Section 3. Unlike <see cref="AttachMine"/> (called once, right at the
    /// exact site position <c>SpawnBuilding</c> just placed the mine at) this searches by the
    /// mine's own current position, since a restored mine's exact placement offset is not saved.
    /// Returns false when no site is within range, which the caller logs as a drop rather than a
    /// hard failure — the mine level itself is still restored either way.
    /// </summary>
    internal bool AttachNearestMine(Unit mine, float maxDistanceMeters)
    {
        GlobalPosition minePosition = mine.GlobalPosition();
        CommanderStrategicPoint? nearest = null;
        float nearestDistance = maxDistanceMeters;
        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            if (point.Kind != StrategicPointKind.Site || (point.Mine != null && !point.Mine.disabled))
            {
                continue;
            }

            float distance = HorizontalDistance(point.Position, minePosition);
            if (distance <= nearestDistance)
            {
                nearestDistance = distance;
                nearest = point;
            }
        }

        if (nearest == null)
        {
            return false;
        }

        AttachMine(nearest.Position, mine);
        return true;
    }

    /// <summary>True when a control point <paramref name="hq"/> currently holds is within
    /// <paramref name="radiusKm"/> of <paramref name="target"/> — the second way into a mine's
    /// build reach, design SS2, on top of an ordinary held base.</summary>
    internal bool IsInsideGarrisonedPointReach(FactionHQ hq, GlobalPosition target, float radiusKm)
    {
        float radius = Mathf.Max(radiusKm, 0.1f) * 1000f;
        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            if (!StrategicPointKinds.IsControlPoint(point.Kind))
            {
                continue;
            }

            if (!Pays(point.Hold) || !ReferenceEquals(point.GetOwner(), hq))
            {
                continue;
            }

            if (HorizontalDistance(target, point.Position) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The nearest free, reachable resource site to <paramref name="hq"/>'s territory —
    /// what the enemy commander's mine step wants (T8). "Reachable" is the same test
    /// <c>Evaluate</c> uses: inside the build radius of a held base, or of a currently garrisoned
    /// control point.</summary>
    internal bool TryPickFreeReachableSite(FactionHQ hq, out CommanderStrategicPoint point)
    {
        GlobalPosition territoryCenter = CommanderCaptureService.GetTerritoryCenter(hq);
        float bestDistance = float.MaxValue;
        CommanderStrategicPoint? best = null;
        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint candidate = points[i];
            if (candidate.Kind != StrategicPointKind.Site || (candidate.Mine != null && !candidate.Mine.disabled))
            {
                continue;
            }

            if (!CommanderBuildPreview.IsInsideBuildRadius(hq, candidate.Position, CommanderSettings.BuildRadiusKm)
                && !IsInsideGarrisonedPointReach(hq, candidate.Position, CommanderSettings.BuildRadiusKm))
            {
                continue;
            }

            float distance = HorizontalDistance(territoryCenter, candidate.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        point = best!;
        return best != null;
    }

    /// <summary>Position-only overload for callers (the build-reserve gate) that only need to know
    /// whether a site is in reach, not which one.</summary>
    internal bool TryPickFreeReachableSite(FactionHQ hq, out GlobalPosition site)
    {
        bool found = TryPickFreeReachableSite(hq, out CommanderStrategicPoint point);
        site = found ? point.Position : default;
        return found;
    }

    /// <summary>Per-minute rate for every point kind that pays through this table — a site is not
    /// among them, since it pays through the mine standing on it (<c>mineLevels</c>). One struct
    /// instead of an ever-growing parameter list on <see cref="IncomePerMinute"/> and
    /// <see cref="SumIncomePerMinute"/>, both kept pure so the self-check can retune the ladder
    /// without a live <see cref="FactionHQ"/>.</summary>
    internal readonly struct PointIncomeRates
    {
        internal PointIncomeRates(
            float baseRate, float villageRate, float hilltopRate, float outpostRate, float crossroadsRate, float roadsideRate)
        {
            Base = baseRate;
            Village = villageRate;
            Hilltop = hilltopRate;
            Outpost = outpostRate;
            Crossroads = crossroadsRate;
            Roadside = roadsideRate;
        }

        internal float Base { get; }
        internal float Village { get; }
        internal float Hilltop { get; }
        internal float Outpost { get; }
        internal float Crossroads { get; }
        internal float Roadside { get; }

        /// <summary>The live config values, read once per income tick.</summary>
        internal static PointIncomeRates FromSettings() => new(
            CommanderSettings.PointsBaseIncomePerMinute,
            CommanderSettings.PointsVillageIncomePerMinute,
            CommanderSettings.PointsHilltopIncomePerMinute,
            CommanderSettings.PointsOutpostIncomePerMinute,
            CommanderSettings.PointsCrossroadsIncomePerMinute,
            CommanderSettings.PointsRoadsideIncomePerMinute);
    }

    /// <summary>How many of each kind an HQ holds right now — bases plus every control-point kind —
    /// so <see cref="GetPointIncomePerMinute"/> can hand the COMMANDER LOG header one struct instead
    /// of an out parameter per kind.</summary>
    internal struct PointCounts
    {
        internal int Bases;
        internal int Villages;
        internal int Hilltops;
        internal int Outposts;
        internal int Crossroads;
        internal int Roadside;
    }

    /// <summary>What one point of this kind pays its holder per minute. A site is 0: the mine
    /// standing on it pays through <c>mineLevels</c>, not this table. Pure, for the self-check.</summary>
    internal static float IncomePerMinute(StrategicPointKind kind, in PointIncomeRates rates)
    {
        return kind switch
        {
            StrategicPointKind.Base => rates.Base,
            StrategicPointKind.Village => rates.Village,
            StrategicPointKind.Hilltop => rates.Hilltop,
            StrategicPointKind.Outpost => rates.Outpost,
            StrategicPointKind.Crossroads => rates.Crossroads,
            StrategicPointKind.Roadside => rates.Roadside,
            _ => 0f,
        };
    }

    /// <summary>Total per-minute income across held bases and every control-point kind. Pure, for
    /// the self-check.</summary>
    internal static float SumIncomePerMinute(in PointCounts counts, in PointIncomeRates rates)
    {
        return counts.Bases * rates.Base
            + counts.Villages * rates.Village
            + counts.Hilltops * rates.Hilltop
            + counts.Outposts * rates.Outpost
            + counts.Crossroads * rates.Crossroads
            + counts.Roadside * rates.Roadside;
    }

    /// <summary>
    /// Pays every server-side faction its bases and every control-point kind on the shared income
    /// tick. Called from <c>CommanderEconomyService.PayIncome</c> ahead of the mine payout, because
    /// a faction with no mines yet still holds bases (Testing rule 3 — same guard shape as
    /// <c>PayIncome</c>'s own mine loop).
    /// </summary>
    internal void PayPointIncome(float share)
    {
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || !hq.IsServer)
            {
                continue;
            }

            hq.AddFunds(GetPointIncomePerMinute(hq, out _) * share);
        }
    }

    /// <summary>Per-minute income this HQ earns from bases and every control-point kind right now,
    /// and how many of each — the same counts the COMMANDER LOG header shows (T11).</summary>
    internal float GetPointIncomePerMinute(FactionHQ hq, out PointCounts counts)
    {
        counts = default;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled && airbase.center != null)
            {
                counts.Bases++;
            }
        }

        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            if (!StrategicPointKinds.IsControlPoint(point.Kind))
            {
                continue;
            }

            if (!Pays(point.Hold) || !ReferenceEquals(point.GetOwner(), hq))
            {
                continue;
            }

            switch (point.Kind)
            {
                case StrategicPointKind.Village:
                    counts.Villages++;
                    break;
                case StrategicPointKind.Hilltop:
                    counts.Hilltops++;
                    break;
                case StrategicPointKind.Outpost:
                    counts.Outposts++;
                    break;
                case StrategicPointKind.Crossroads:
                    counts.Crossroads++;
                    break;
                case StrategicPointKind.Roadside:
                    counts.Roadside++;
                    break;
            }
        }

        return SumIncomePerMinute(counts, PointIncomeRates.FromSettings());
    }

    /// <summary>
    /// One runnable check on the pure discovery/hold/income/siting rules, run once from
    /// <see cref="CommanderPlugin"/> at load. Every case here is added by the task that introduces
    /// the rule it guards; see the plan's Executor notes for which task added which.
    /// </summary>
    internal static void SelfCheck()
    {
        List<string> failures = new();
        CheckDiscoverySpacing(failures);
        CheckDiscoveryThresholds(failures);
        CheckHoldStateMachine(failures);
        CheckIncome(failures);
        CheckMineSnap(failures);
        CheckControlPointKinds(failures);
        CheckRoadJunctions(failures);
        CheckRoadsideEmission(failures);
        CheckNearestRoadDistance(failures);

        if (failures.Count == 0)
        {
            CommanderPlugin.Log.LogDebug("Strategic points self-check passed.");
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Strategic points self-check FAILED: {failures[i]}");
        }
    }

    /// <summary>
    /// Six points along a 1000 m line plus one 500 m from an airbase, spaced and capped at 3:
    /// exercises the cap, the pairwise drop, the airbase exclusion and the spacing invariant in one
    /// pass, the way <c>ApplySpacing</c> is actually called from discovery. A second, separate
    /// scenario below proves the <c>preKept</c> seed <c>StepBases</c> relies on to keep a village or
    /// hilltop out of a resource site's spacing ring (design.md's Caps section): a candidate too
    /// close to a pre-kept point is rejected, and the identical candidate is accepted when there is
    /// no pre-kept list.
    /// </summary>
    private static void CheckDiscoverySpacing(List<string> failures)
    {
        CommanderStrategicPoint line0 = new(StrategicPointKind.Site, new GlobalPosition(0f, 0f, 0f), 50f, string.Empty);
        CommanderStrategicPoint line1 = new(StrategicPointKind.Site, new GlobalPosition(1000f, 0f, 0f), 50f, string.Empty);
        CommanderStrategicPoint baseAdjacent =
            new(StrategicPointKind.Site, new GlobalPosition(100500f, 0f, 0f), 50f, string.Empty);
        CommanderStrategicPoint line2 = new(StrategicPointKind.Site, new GlobalPosition(2500f, 0f, 0f), 50f, string.Empty);
        CommanderStrategicPoint line3 = new(StrategicPointKind.Site, new GlobalPosition(4000f, 0f, 0f), 50f, string.Empty);
        CommanderStrategicPoint line4 = new(StrategicPointKind.Site, new GlobalPosition(5500f, 0f, 0f), 50f, string.Empty);
        CommanderStrategicPoint line5 = new(StrategicPointKind.Site, new GlobalPosition(7000f, 0f, 0f), 50f, string.Empty);
        List<CommanderStrategicPoint> candidates = new() { line0, line1, baseAdjacent, line2, line3, line4, line5 };
        List<GlobalPosition> airbaseCentres = new() { new GlobalPosition(100000f, 0f, 0f) };

        ApplySpacing(candidates, 1500f, airbaseCentres, 2000f, 3);

        Expect(failures, "cap holds", candidates.Count, 3);
        Expect(failures, "newer of a close pair is dropped", candidates.Contains(line1), false);
        Expect(failures, "nothing inside the airbase exclusion", candidates.Contains(baseAdjacent), false);

        bool spaced = true;
        for (int i = 0; i < candidates.Count; i++)
        {
            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (HorizontalDistance(candidates[i].Position, candidates[j].Position) < 1500f)
                {
                    spaced = false;
                }
            }
        }

        Expect(failures, "kept points respect spacing", spaced, true);

        // Spread: eleven candidates in a line 1.6 km apart, cap 3. First-come would keep the three
        // nearest the start; farthest-first has to reach the far end. This is the case that would
        // have caught every hilltop landing in one corner of the map.
        List<CommanderStrategicPoint> line = new();
        for (int i = 0; i < 11; i++)
        {
            line.Add(new CommanderStrategicPoint(StrategicPointKind.Hilltop, new GlobalPosition(i * 1600f, 0f, 0f), 300f, string.Empty));
        }

        CommanderStrategicPoint farEnd = line[10];
        ApplySpacing(line, 1500f, new List<GlobalPosition>(), 2000f, 3);
        Expect(failures, "spread: cap of three still holds", line.Count, 3);
        Expect(failures, "spread: the far end of the map is reached", line.Contains(farEnd), true);

        // Cross-group spacing: a village/hilltop candidate must be rejected if it is too close to
        // a site the sites pass already kept, even though the two never shared one ApplySpacing
        // call — this is the seam StepBases uses `preKept` for.
        CommanderStrategicPoint acceptedSite =
            new(StrategicPointKind.Site, new GlobalPosition(20000f, 0f, 0f), 50f, string.Empty);
        List<CommanderStrategicPoint> preKept = new() { acceptedSite };

        CommanderStrategicPoint tooCloseVillage =
            new(StrategicPointKind.Village, new GlobalPosition(20500f, 0f, 0f), 50f, string.Empty);
        List<CommanderStrategicPoint> withPreKept = new() { tooCloseVillage };
        ApplySpacing(withPreKept, 1500f, new List<GlobalPosition>(), 2000f, 3, preKept);
        Expect(
            failures,
            "a candidate within minSpacing of a pre-kept point is rejected",
            withPreKept.Contains(tooCloseVillage),
            false);

        CommanderStrategicPoint sameVillage =
            new(StrategicPointKind.Village, new GlobalPosition(20500f, 0f, 0f), 50f, string.Empty);
        List<CommanderStrategicPoint> withoutPreKept = new() { sameVillage };
        ApplySpacing(withoutPreKept, 1500f, new List<GlobalPosition>(), 2000f, 3);
        Expect(
            failures,
            "the same candidate is kept when there is no pre-kept list",
            withoutPreKept.Contains(sameVillage),
            true);
    }

    /// <summary>
    /// The two threshold comparisons discovery gates a village and a hilltop on, at the default
    /// constants (<c>VillageMinBuildings = 4</c>, <c>HilltopProminenceMeters = 60</c>). The
    /// clustering and ring-sampling around them are left out on purpose: both read live map and
    /// building data a synthetic check cannot construct without re-implementing the terrain query
    /// it exists to guard.
    /// </summary>
    private static void CheckDiscoveryThresholds(List<string> failures)
    {
        Expect(failures, "a cluster of VillageMinBuildings - 1 does not qualify", QualifiesAsVillage(3, 4), false);
        Expect(failures, "a cluster of VillageMinBuildings qualifies", QualifiesAsVillage(4, 4), true);
        Expect(
            failures,
            "a sample prominenceMeters - 1 above the ring mean fails",
            QualifiesAsHilltop(159f, 100f, 60f, true),
            false);
        Expect(
            failures,
            "a sample exactly at prominenceMeters above the ring mean passes",
            QualifiesAsHilltop(160f, 100f, 60f, true),
            true);
        Expect(
            failures,
            "a sample that is not the highest in its ring fails regardless of prominence",
            QualifiesAsHilltop(1000f, 100f, 60f, false),
            false);
    }

    /// <summary>
    /// <see cref="Step"/> and <see cref="QualifyingFaction"/> at the default
    /// <c>HoldSeconds = 60</c>, ticking at <c>HoldCheckSeconds = 5</c>: neutral to held, a lapse
    /// restarting progress from zero rather than resuming it, a contest freezing everything
    /// (including <see cref="Pays"/>) even with an owner in place, and the minimum-garrison rule.
    /// </summary>
    private static void CheckHoldStateMachine(List<string> failures)
    {
        const float dt = 5f;
        const float holdSeconds = 60f;

        HoldState state = new() { OwnerIndex = -1, CandidateIndex = -1, Progress = 0f, Contested = false };
        for (int i = 0; i < 11; i++)
        {
            Step(ref state, 0, false, dt, holdSeconds);
        }

        Expect(failures, "neutral to held after HoldSeconds: still neutral after 11 steps", state.OwnerIndex, -1);
        Step(ref state, 0, false, dt, holdSeconds);
        Expect(failures, "neutral to held after HoldSeconds: held on the 12th step", state.OwnerIndex, 0);

        Step(ref state, -1, false, dt, holdSeconds);
        Expect(failures, "lapse resets: owner drops immediately", state.OwnerIndex, -1);
        for (int i = 0; i < 6; i++)
        {
            Step(ref state, 0, false, dt, holdSeconds);
        }

        Step(ref state, -1, false, dt, holdSeconds);
        for (int i = 0; i < 11; i++)
        {
            Step(ref state, 0, false, dt, holdSeconds);
        }

        Expect(
            failures,
            "lapse resets: progress restarted from zero, not resumed, still neutral after 11 more steps",
            state.OwnerIndex,
            -1);

        HoldState contestedState = new() { OwnerIndex = -1, CandidateIndex = -1, Progress = 0f, Contested = false };
        for (int i = 0; i < 12; i++)
        {
            Step(ref contestedState, 0, false, dt, holdSeconds);
        }

        for (int i = 0; i < 6; i++)
        {
            Step(ref contestedState, 1, false, dt, holdSeconds);
        }

        Expect(failures, "contested freezes: progress is 30 before the contest", contestedState.Progress, 30f);
        Step(ref contestedState, 1, true, dt, holdSeconds);
        Expect(failures, "contested freezes: progress unchanged", contestedState.Progress, 30f);
        Expect(failures, "contested freezes: owner unchanged", contestedState.OwnerIndex, 0);
        Expect(failures, "contested freezes: pays false even with an owner in place", Pays(contestedState), false);

        Expect(failures, "minimum garrison respected: below minimum", QualifyingFaction(new[] { 1, 0 }, 2, out bool c1), -1);
        Expect(failures, "minimum garrison respected: below minimum is not contested", c1, false);
        Expect(failures, "minimum garrison respected: at minimum, alone", QualifyingFaction(new[] { 2, 0 }, 2, out bool c2), 0);
        Expect(failures, "minimum garrison respected: at minimum, alone is not contested", c2, false);
        Expect(
            failures, "minimum garrison respected: one at minimum, one short is still contested",
            QualifyingFaction(new[] { 2, 1 }, 2, out bool c3), -1);
        Expect(failures, "minimum garrison respected: one at minimum, one short is contested", c3, true);
        Expect(
            failures, "minimum garrison respected: both at minimum is contested",
            QualifyingFaction(new[] { 3, 3 }, 2, out bool c4), -1);
        Expect(failures, "minimum garrison respected: both at minimum sets contested", c4, true);
    }

    /// <summary>
    /// The income table sums correctly across every kind, a site pays nothing through it, and the
    /// live defaults keep the ladder base &gt; village = crossroads &gt; hilltop = outpost &gt;
    /// roadside &gt; 0 — the retune-into-nonsense guard Testing rule 1 asks for every threshold
    /// table.
    /// </summary>
    private static void CheckIncome(List<string> failures)
    {
        PointCounts counts = new() { Bases = 2, Villages = 1, Hilltops = 3, Outposts = 4, Crossroads = 1, Roadside = 2 };
        PointIncomeRates rates = new(30f, 10f, 5f, 5f, 10f, 3f);
        Expect(failures, "income sums", SumIncomePerMinute(counts, rates), 2 * 30f + 1 * 10f + 3 * 5f + 4 * 5f + 1 * 10f + 2 * 3f);
        Expect(failures, "a site pays only through its mine", IncomePerMinute(StrategicPointKind.Site, rates), 0f);

        float baseRate = CommanderSettings.PointsBaseIncomePerMinute;
        float villageRate = CommanderSettings.PointsVillageIncomePerMinute;
        float hilltopRate = CommanderSettings.PointsHilltopIncomePerMinute;
        float outpostRate = CommanderSettings.PointsOutpostIncomePerMinute;
        float crossroadsRate = CommanderSettings.PointsCrossroadsIncomePerMinute;
        float roadsideRate = CommanderSettings.PointsRoadsideIncomePerMinute;
        Expect(
            failures,
            "income ladder is base > village = crossroads > hilltop = outpost > roadside > 0; "
                + "check the Points section of the config",
            baseRate > villageRate
                && villageRate == crossroadsRate
                && villageRate > hilltopRate
                && hilltopRate == outpostRate
                && hilltopRate > roadsideRate
                && roadsideRate > 0f,
            true);
    }

    /// <summary>Every control-point kind reports true, a site and a base report false — the
    /// truth table <see cref="StrategicPointKinds.IsControlPoint"/> replaces the scattered
    /// <c>!= Village &amp;&amp; != Hilltop</c> checks with.</summary>
    private static void CheckControlPointKinds(List<string> failures)
    {
        Expect(failures, "a site is not a control point", StrategicPointKinds.IsControlPoint(StrategicPointKind.Site), false);
        Expect(failures, "a village is a control point", StrategicPointKinds.IsControlPoint(StrategicPointKind.Village), true);
        Expect(failures, "a hilltop is a control point", StrategicPointKinds.IsControlPoint(StrategicPointKind.Hilltop), true);
        Expect(failures, "an outpost is a control point", StrategicPointKinds.IsControlPoint(StrategicPointKind.Outpost), true);
        Expect(failures, "a crossroads is a control point", StrategicPointKinds.IsControlPoint(StrategicPointKind.Crossroads), true);
        Expect(failures, "a roadside point is a control point", StrategicPointKinds.IsControlPoint(StrategicPointKind.Roadside), true);
        Expect(failures, "a base is not a control point", StrategicPointKinds.IsControlPoint(StrategicPointKind.Base), false);
    }

    /// <summary>
    /// <see cref="JunctionDegree"/>: three roads meeting at one point is a crossroads candidate at
    /// the default <c>CrossroadsMinRoads = 3</c>, two is not, and a fourth road that only passes
    /// near the node without ending there still counts once toward the same degree.
    /// </summary>
    private static void CheckRoadJunctions(List<string> failures)
    {
        GlobalPosition node = new(0f, 0f, 0f);
        List<GlobalPosition> roadA = new() { new GlobalPosition(-1000f, 0f, 0f), node };
        List<GlobalPosition> roadB = new() { new GlobalPosition(1000f, 0f, 0f), node };
        List<GlobalPosition> roadC = new() { new GlobalPosition(0f, 0f, -1000f), node };
        List<List<GlobalPosition>> three = new() { roadA, roadB, roadC };
        List<List<GlobalPosition>> two = new() { roadA, roadB };

        Expect(failures, "three roads meeting at one point: degree 3", JunctionDegree(node, three, 60f), 3);
        Expect(
            failures,
            "two roads meeting at one point: degree 2, below the default CrossroadsMinRoads",
            JunctionDegree(node, two, 60f),
            2);

        // A through-road: its endpoints are far from the node, but a mid-span point of it passes
        // within the merge distance — it still counts once toward degree, even though it never
        // ends there.
        List<GlobalPosition> throughRoad = new()
        {
            new GlobalPosition(-2000f, 0f, 500f), new GlobalPosition(0f, 0f, 10f), new GlobalPosition(2000f, 0f, 500f),
        };
        List<List<GlobalPosition>> withThrough = new() { roadA, roadB, throughRoad };
        Expect(
            failures,
            "a road passing near the node without ending there still counts once",
            JunctionDegree(node, withThrough, 60f),
            3);
    }

    /// <summary><see cref="EmitRoadsidePoints"/>: a 20 km straight road at 6 km spacing emits three
    /// points (6, 12 and 18 km along it), never a fourth just past the road's own end.</summary>
    private static void CheckRoadsideEmission(List<string> failures)
    {
        List<GlobalPosition> straightRoad = new() { new GlobalPosition(0f, 0f, 0f), new GlobalPosition(0f, 0f, 20000f) };
        List<GlobalPosition> emitted = new();
        EmitRoadsidePoints(straightRoad, 6000f, emitted);
        Expect(failures, "a 20 km road at 6 km spacing emits 3 points", emitted.Count, 3);

        // Most road-graph edges are shorter than the spacing; those contribute one midpoint, and a
        // stub too short to hold contributes nothing.
        List<GlobalPosition> shortRoad = new() { new GlobalPosition(0f, 0f, 0f), new GlobalPosition(0f, 0f, 3000f) };
        EmitRoadsidePoints(shortRoad, 6000f, emitted);
        Expect(failures, "a 3 km road at 6 km spacing emits its midpoint", emitted.Count, 1);
        Expect(failures, "that midpoint is half way along", emitted.Count == 1 && Mathf.Abs(emitted[0].z - 1500f) < 1f, true);
        List<GlobalPosition> stub = new() { new GlobalPosition(0f, 0f, 0f), new GlobalPosition(0f, 0f, 300f) };
        EmitRoadsidePoints(stub, 6000f, emitted);
        Expect(failures, "a 300 m stub emits nothing", emitted.Count, 0);
    }

    /// <summary>The mine-snap boundary and tie-breaking: nothing in range, a free site exactly at
    /// the snap radius, the nearer of two free sites, the free one when the nearest is occupied,
    /// and a free site just outside the radius.</summary>
    private static void CheckMineSnap(List<string> failures)
    {
        Expect(failures, "nothing within range", NearestFreeSiteIndex(new[] { 1500f }, new[] { true }, 1000f), -1);
        Expect(
            failures,
            "one free site just inside snapMeters",
            NearestFreeSiteIndex(new[] { 1000f }, new[] { true }, 1000f),
            0);
        Expect(
            failures,
            "two free sites in range: the nearer one",
            NearestFreeSiteIndex(new[] { 900f, 400f }, new[] { true, true }, 1000f),
            1);
        Expect(
            failures,
            "the nearest site occupied and a free one further but still in range: the free one",
            NearestFreeSiteIndex(new[] { 300f, 700f }, new[] { false, true }, 1000f),
            1);
        Expect(
            failures,
            "a free site just outside: -1",
            NearestFreeSiteIndex(new[] { 1000.1f }, new[] { true }, 1000f),
            -1);
    }

    /// <summary>
    /// The insertion road gate's measure over one synthetic road, at the default
    /// <c>OperationsHeliInsertionOffRoadMeters</c> (the boundary itself lives in
    /// <see cref="CommanderOperationsService"/>'s own self-check; here the measure is what is
    /// proven): a point off the road's end, a point beside its middle, and the no-road case.
    /// </summary>
    private static void CheckNearestRoadDistance(List<string> failures)
    {
        // One straight 10 km road along Z, so the nearest-segment measure is plain coordinate maths.
        List<GlobalPosition> road = new()
        {
            new GlobalPosition(0f, 0f, 0f),
            new GlobalPosition(0f, 0f, 10000f),
        };
        List<List<GlobalPosition>> roads = new() { road };

        Expect(failures, "a roadless map reports no distance at all",
            NearestRoadDistanceMeters(new List<List<GlobalPosition>>(), new GlobalPosition(500f, 0f, 500f)),
            float.MaxValue);
        Expect(failures, "a point 2500 m beside a road reads 2500",
            NearestRoadDistanceMeters(roads, new GlobalPosition(2500f, 0f, 5000f)), 2500f);
        Expect(failures, "a point 3000 m past a road's end measures to the endpoint, not the line",
            NearestRoadDistanceMeters(roads, new GlobalPosition(0f, 0f, 13000f)), 3000f);
    }

    private static void ExpectSequence(List<string> failures, string name, List<int> actual, params int[] expected)
    {
        bool equal = actual.Count == expected.Length;
        for (int i = 0; equal && i < expected.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                equal = false;
            }
        }

        if (!equal)
        {
            failures.Add($"{name}: expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]");
        }
    }

    private static void Expect(List<string> failures, string name, float actual, float expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, int actual, int expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }
}
