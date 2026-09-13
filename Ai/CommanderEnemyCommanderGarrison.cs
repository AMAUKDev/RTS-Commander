using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// A small standing garrison on the control points nearest each base this commander holds — the
/// same take-and-hold economy the player is asked to play, applied to the AI. Garrisoning uses the
/// home guard's own pinning mechanism
/// (<see cref="CommanderEnemyCommanderDefence"/> remarks): a control point is worth nothing if the
/// vehicle parked on it walks off toward the nearest tracked enemy the instant it arrives.
/// </summary>
/// <remarks>
/// ponytail: a garrison stands still and shoots for itself once posted — no patrol, no reaction to
/// a raid beyond whatever its own turret does. Upgrade only if that reads as obviously wrong in
/// play; platoon behaviour generally is a later track.
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>How far from a held base a control point is still worth garrisoning. Wider than the
    /// mine build radius: a garrison is cheap (one spare vehicle) and the point pays income the
    /// moment it is taken, so it is worth reaching further for than a building is.</summary>
    private const float GarrisonReachMeters = 12000f;

    /// <summary>Control points garrisoned at once, per commander, so the home guard — which shares
    /// the same faction's idle vehicles — is never starved down to nothing.</summary>
    private const int MaxGarrisonedPoints = 3;

    private readonly List<Unit> garrisonCandidates = new();
    private readonly List<Unit> staleGarrison = new();
    private readonly List<CommanderStrategicPoint> garrisonTargetPoints = new();
    private readonly List<float> garrisonTargetDistances = new();
    private readonly List<int> garrisonTargetIndices = new();
    private readonly List<CommanderStrategicPoint> garrisonTargets = new();
    private readonly List<Unit> garrisonPointUnits = new();

    /// <summary>Ring stations per garrisoned point, built once and kept — a point does not move, so
    /// there is nothing to rebuild it for.</summary>
    private readonly Dictionary<CommanderStrategicPoint, List<GlobalPosition>> garrisonPosts = new();

    /// <summary>
    /// True while <paramref name="unit"/> is standing on one of this commander's garrisons. Read by
    /// the home guard's own recruiter and by <see cref="CommanderCaptureService"/> so neither one
    /// pulls a vehicle the other has already pinned. Same shape as
    /// <see cref="CommanderEnemyCommanderDefence.IsDefendingUnit"/>.
    /// </summary>
    internal static bool IsGarrisonUnit(Unit? unit)
    {
        CommanderEnemyCommanderService? service = Instance;
        if (service == null || unit == null)
        {
            return false;
        }

        foreach (KeyValuePair<FactionHQ, CommanderState> entry in service.states)
        {
            if (entry.Value.Garrison.ContainsKey(unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The nearest <paramref name="maxPoints"/> candidates within <paramref name="reachMeters"/>,
    /// nearest first, into <paramref name="result"/> (cleared first). A distance exactly at
    /// <paramref name="reachMeters"/> counts as in reach — the same inclusive-boundary convention
    /// <c>NearestFreeSiteIndex</c> (T7) uses. Pure, for the self-check; the reach test and the cap
    /// are one definition shared by every commander's garrison review.
    /// </summary>
    internal static void SelectGarrisonTargets(
        IReadOnlyList<float> distances, float reachMeters, int maxPoints, List<int> result)
    {
        result.Clear();
        List<int> inReach = new(distances.Count);
        for (int i = 0; i < distances.Count; i++)
        {
            if (distances[i] <= reachMeters)
            {
                inReach.Add(i);
            }
        }

        inReach.Sort((a, b) => distances[a].CompareTo(distances[b]));
        for (int i = 0; i < inReach.Count && result.Count < maxPoints; i++)
        {
            result.Add(inReach[i]);
        }
    }

    private void ReviewGarrisons(FactionHQ localHq)
    {
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null
                || !CommanderPlayerCommanderService.IsCommanded(hq, localHq)
                || !hq.IsServer
                || hq.faction == null)
            {
                continue;
            }

            if (!states.TryGetValue(hq, out CommanderState state))
            {
                state = new CommanderState();
                states[hq] = state;
            }

            ReviewGarrison(hq, state);
        }
    }

    private void ReviewGarrison(FactionHQ hq, CommanderState state)
    {
        PruneGarrison(hq, state);

        IReadOnlyList<CommanderStrategicPoint>? allPoints = CommanderStrategicPointService.Instance?.Points;
        garrisonTargetPoints.Clear();
        garrisonTargetDistances.Clear();
        if (allPoints != null)
        {
            for (int i = 0; i < allPoints.Count; i++)
            {
                CommanderStrategicPoint point = allPoints[i];
                if (!StrategicPointKinds.IsControlPoint(point.Kind))
                {
                    continue;
                }

                float distance = NearestHeldBaseDistanceMeters(hq, point.Position);
                if (distance < 0f)
                {
                    // No base at all: nothing counts as "reachable" (a commander with no base has
                    // lost, and the buy loop has bigger problems than an empty village).
                    continue;
                }

                garrisonTargetPoints.Add(point);
                garrisonTargetDistances.Add(distance);
            }
        }

        garrisonTargetIndices.Clear();
        SelectGarrisonTargets(garrisonTargetDistances, GarrisonReachMeters, MaxGarrisonedPoints, garrisonTargetIndices);

        // A point another faction currently holds is still a target - taking it is the point - so
        // membership here is purely reach and the cap, never who (if anyone) holds it right now.
        garrisonTargets.Clear();
        for (int i = 0; i < garrisonTargetIndices.Count; i++)
        {
            garrisonTargets.Add(garrisonTargetPoints[garrisonTargetIndices[i]]);
        }

        ReleaseGarrisonOutsideTargets(state);

        int garrisonSize = CommanderSettings.PointsMinGarrison + 1;
        for (int i = 0; i < garrisonTargets.Count; i++)
        {
            CommanderStrategicPoint point = garrisonTargets[i];
            int current = CountGarrison(state, point);
            if (current >= garrisonSize)
            {
                continue;
            }

            bool wasEmpty = current == 0;
            RecruitGarrison(hq, state, point, garrisonSize - current);
            int filled = CountGarrison(state, point);
            if (wasEmpty && filled > 0)
            {
                CommanderAiLog.Note(hq, $"garrisons {point.Label} with {filled} unit(s).");
            }
        }

        DriveGarrisonToPosts(state);
    }

    /// <summary>Drops entries whose unit is gone, disabled, changed faction, or under a player
    /// order — same filter as <see cref="PruneDefenders"/>, and the same reason:
    /// <c>commandedDestination</c> is left alone, because the player's own order owns it now.</summary>
    private void PruneGarrison(FactionHQ hq, CommanderState state)
    {
        staleGarrison.Clear();
        foreach (KeyValuePair<Unit, CommanderStrategicPoint> entry in state.Garrison)
        {
            if (entry.Key == null
                || entry.Key.disabled
                || entry.Key.NetworkHQ != hq
                || CommanderMoveService.Instance?.HasPlayerOrder(entry.Key) == true)
            {
                staleGarrison.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleGarrison.Count; i++)
        {
            state.Garrison.Remove(staleGarrison[i]);
        }
    }

    /// <summary>Releases every unit garrisoned on a point that is no longer a target — the
    /// <see cref="ReleaseSurplusDefenders"/> way: clear the pin, leave everything else alone.</summary>
    private void ReleaseGarrisonOutsideTargets(CommanderState state)
    {
        staleGarrison.Clear();
        foreach (KeyValuePair<Unit, CommanderStrategicPoint> entry in state.Garrison)
        {
            if (!garrisonTargets.Contains(entry.Value))
            {
                staleGarrison.Add(entry.Key);
            }
        }

        for (int i = 0; i < staleGarrison.Count; i++)
        {
            Unit unit = staleGarrison[i];
            state.Garrison.Remove(unit);
            if (unit is GroundVehicle vehicle)
            {
                CommandedDestinationRef(vehicle) = false;
            }
        }
    }

    private static int CountGarrison(CommanderState state, CommanderStrategicPoint point)
    {
        int count = 0;
        foreach (KeyValuePair<Unit, CommanderStrategicPoint> entry in state.Garrison)
        {
            if (ReferenceEquals(entry.Value, point))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Recruits up to <paramref name="need"/> more idle combat vehicles onto
    /// <paramref name="point"/>'s garrison, nearest first. The filter is
    /// <see cref="RecruitDefenders"/>'s, plus never a vehicle the home guard or another garrison
    /// already holds and never a recon vehicle (<c>Ground.cs</c>'s own reason: recon has its own
    /// job).</summary>
    private void RecruitGarrison(FactionHQ hq, CommanderState state, CommanderStrategicPoint point, int need)
    {
        garrisonCandidates.Clear();
        if (hq.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is GroundVehicle
                && !unit.disabled
                && unit.definition is VehicleDefinition definition
                && IsCombatVehicle(definition)
                && definition.vehicleType != VehicleType.RDR
                && CommanderMoveService.Instance?.HasPlayerOrder(unit) != true
                && !state.Defenders.ContainsKey(unit)
                && !state.Garrison.ContainsKey(unit))
            {
                garrisonCandidates.Add(unit);
            }
        }

        garrisonCandidates.Sort((left, right) =>
            HorizontalDistanceSquared(left.transform.GlobalPosition(), point.Position)
                .CompareTo(HorizontalDistanceSquared(right.transform.GlobalPosition(), point.Position)));

        for (int i = 0; i < garrisonCandidates.Count && need > 0; i++)
        {
            state.Garrison[garrisonCandidates[i]] = point;
            need--;
        }
    }

    private void DriveGarrisonToPosts(CommanderState state)
    {
        for (int t = 0; t < garrisonTargets.Count; t++)
        {
            CommanderStrategicPoint point = garrisonTargets[t];
            garrisonPointUnits.Clear();
            foreach (KeyValuePair<Unit, CommanderStrategicPoint> entry in state.Garrison)
            {
                if (ReferenceEquals(entry.Value, point))
                {
                    garrisonPointUnits.Add(entry.Key);
                }
            }

            if (garrisonPointUnits.Count == 0)
            {
                continue;
            }

            List<GlobalPosition> posts = EnsureGarrisonPosts(point);
            if (posts.Count == 0)
            {
                continue;
            }

            // A stable order so the same unit tends to keep the same post rather than swapping
            // with another garrison member every review.
            garrisonPointUnits.Sort(static (a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            for (int i = 0; i < garrisonPointUnits.Count; i++)
            {
                Unit unit = garrisonPointUnits[i];
                GlobalPosition post = posts[i % posts.Count];
                if (FastMath.InRange(unit.transform.GlobalPosition(), post, DefenceArrivedMeters))
                {
                    continue;
                }

                // playerCommand: true is the whole mechanism - see the remarks on this class's
                // Defence partial.
                CommanderGameAccess.GetUnitCommand(unit)?.SetDestination(post, true);
            }
        }
    }

    /// <summary>Ring posts around a point, <c>MinGarrison + 1</c> of them (one spare), at 0.6 of
    /// its radius — inside the control ring itself, not standing on its edge. Same shape as
    /// <see cref="EnsureDefencePosts"/>: snapped to terrain, sea-level posts skipped.</summary>
    private List<GlobalPosition> EnsureGarrisonPosts(CommanderStrategicPoint point)
    {
        if (garrisonPosts.TryGetValue(point, out List<GlobalPosition> posts))
        {
            return posts;
        }

        posts = new List<GlobalPosition>();
        int slots = CommanderSettings.PointsMinGarrison + 1;
        float ring = point.Radius * 0.6f;
        for (int i = 0; i < slots; i++)
        {
            float angle = i * (Mathf.PI * 2f / slots);
            GlobalPosition candidate = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
                point.Position.x + Mathf.Cos(angle) * ring,
                point.Position.y,
                point.Position.z + Mathf.Sin(angle) * ring));
            if (!CommanderGameAccess.IsBelowSeaLevel(candidate))
            {
                posts.Add(candidate);
            }
        }

        garrisonPosts[point] = posts;
        return posts;
    }

    private static float NearestHeldBaseDistanceMeters(FactionHQ hq, GlobalPosition position)
    {
        float best = -1f;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = HorizontalDistanceSquared(position, airbase.center.GlobalPosition());
            distance = Mathf.Sqrt(distance);
            if (best < 0f || distance < best)
            {
                best = distance;
            }
        }

        return best;
    }

    private static float HorizontalDistanceSquared(GlobalPosition a, GlobalPosition b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }
}
