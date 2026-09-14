using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The front line: which control points count as front or rear, what each is worth to hold, and —
/// from T7 — the forward-base and picket missions that follow from that ranking.
/// </summary>
internal sealed partial class CommanderOperationsService
{
    /// <summary>
    /// How much being on the enemy's doorstep multiplies a point's income when missions are ordered.
    /// Design SS2 says "income × frontage factor (closer to enemy assets = higher)" and gives no
    /// curve; 1.0 means a point touching the enemy is worth double its income and a point at
    /// <c>FrontRangeMeters</c> is worth exactly its income. Planner-chosen.
    /// </summary>
    private const float FrontageWeight = 1f;

    /// <summary>
    /// A point's threat mark uses a ring this many times its own control radius — a 300 m ring seen
    /// from 1.2 km reads as "they are at the gate". Planner-chosen.
    /// </summary>
    private const float ThreatMarkRingMultiplier = 4f;

    /// <summary>A point ranked for mission planning: its distance to the nearest enemy asset,
    /// whether that puts it on the front, whether it currently carries a threat mark, and its
    /// value (design SS2).</summary>
    internal readonly struct CommanderRankedPoint
    {
        internal CommanderRankedPoint(
            CommanderStrategicPoint point, float distanceToEnemyMeters, bool isFront, bool hasThreatMark, float value)
        {
            Point = point;
            DistanceToEnemyMeters = distanceToEnemyMeters;
            IsFront = isFront;
            HasThreatMark = hasThreatMark;
            Value = value;
        }

        internal CommanderStrategicPoint Point { get; }
        internal float DistanceToEnemyMeters { get; }
        internal bool IsFront { get; }
        internal bool HasThreatMark { get; }
        internal float Value { get; }
    }

    /// <summary>A point within <paramref name="frontRangeMeters"/> of the nearest enemy-held point
    /// or base is front line; beyond it is rear. Pure, for the self-check. Inclusive boundary, the
    /// convention the rest of the mod uses.</summary>
    internal static bool IsFrontPoint(float distanceToNearestEnemyAssetMeters, float frontRangeMeters)
    {
        return distanceToNearestEnemyAssetMeters <= frontRangeMeters;
    }

    /// <summary>
    /// Income times a frontage factor that runs from double at the enemy's doorstep down to flat at
    /// (and beyond) the front range — design SS2's "income × frontage factor (closer to enemy assets
    /// = higher)". Pure, for the self-check.
    /// </summary>
    internal static float PointValue(
        float incomePerMinute, float distanceToNearestEnemyAssetMeters, float frontRangeMeters)
    {
        float closeness = Mathf.Clamp01(1f - distanceToNearestEnemyAssetMeters / Mathf.Max(1f, frontRangeMeters));
        return incomePerMinute * (1f + FrontageWeight * closeness);
    }

    /// <summary>
    /// Shortest horizontal distance from <paramref name="position"/> to any point (control point or
    /// base — <see cref="CommanderStrategicPoint.GetOwner"/> resolves both) another live HQ holds.
    /// <c>float.MaxValue</c> when the commander holds every point on the map, or none exist yet —
    /// which correctly makes every point rear on a map nobody has met an opponent on.
    /// </summary>
    private static float NearestEnemyAssetDistance(FactionHQ hq, GlobalPosition position)
    {
        return TryNearestEnemyAsset(hq, position, out _, out float distance) ? distance : float.MaxValue;
    }

    /// <summary>
    /// The nearest point or base another live HQ holds, and how far away it is — the walk
    /// <see cref="NearestEnemyAssetDistance"/> used to do inline, generalised to hand back WHERE
    /// that asset is (Reuse rule 5, behaviour-neutral). The ground-tactics screen reads the
    /// direction: with nothing tracked, the way the enemy lies is the most threatened approach.
    /// False when the commander holds every point on the map, or none exist yet.
    /// </summary>
    private static bool TryNearestEnemyAsset(
        FactionHQ hq, GlobalPosition position, out GlobalPosition asset, out float distance)
    {
        asset = default;
        distance = float.MaxValue;
        IReadOnlyList<CommanderStrategicPoint>? points = CommanderStrategicPointService.Instance?.Points;
        if (points == null)
        {
            return false;
        }

        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            FactionHQ? owner = point.GetOwner();
            if (owner == null || ReferenceEquals(owner, hq))
            {
                continue;
            }

            float candidate = CommanderGameAccess.HorizontalDistance(position.AsVector3(), point.Position.AsVector3());
            if (candidate < distance)
            {
                distance = candidate;
                asset = point.Position;
            }
        }

        return distance < float.MaxValue;
    }

    /// <summary>
    /// A hostile ground contact, seen recently, inside <paramref name="point"/>'s threat ring — the
    /// <c>IsUnderThreat</c> walk (<c>Ai/CommanderEnemyCommanderDefence.cs:197-228</c>) retargeted
    /// from "near my base" to "near this point", including its skip-buildings clause.
    /// </summary>
    private static bool HasThreatMark(FactionHQ hq, CommanderStrategicPoint point)
    {
        float now = Time.timeSinceLevelLoad;
        float radius = point.Radius * ThreatMarkRingMultiplier;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > CommanderEnemyCommanderService.ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq))
            {
                continue;
            }

            // Buildings sit in the tracking database forever once revealed; they are not an attack.
            if (unit is Building)
            {
                continue;
            }

            if (CommanderGameAccess.HorizontalDistance(point.Position.AsVector3(), info.lastKnownPosition.AsVector3()) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ranks every control point for this HQ: distance to the nearest enemy asset, front/rear,
    /// threat mark and value, sorted by value descending (ties broken by distance ascending, so the
    /// order used to fill missions is stable review to review).
    /// </summary>
    private void RankPoints(FactionHQ hq, OperationsState state)
    {
        state.RankedPoints.Clear();
        IReadOnlyList<CommanderStrategicPoint>? points = CommanderStrategicPointService.Instance?.Points;
        if (points == null)
        {
            return;
        }

        CommanderStrategicPointService.PointIncomeRates rates = CommanderStrategicPointService.PointIncomeRates.FromSettings();
        float frontRange = CommanderSettings.OperationsFrontRangeMeters;
        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            if (!StrategicPointKinds.IsControlPoint(point.Kind))
            {
                continue;
            }

            float distance = NearestEnemyAssetDistance(hq, point.Position);
            bool isFront = IsFrontPoint(distance, frontRange);
            bool hasThreat = HasThreatMark(hq, point);
            float income = CommanderStrategicPointService.IncomePerMinute(point.Kind, rates);
            float value = PointValue(income, distance, frontRange);
            state.RankedPoints.Add(new CommanderRankedPoint(point, distance, isFront, hasThreat, value));
        }

        state.RankedPoints.Sort(static (a, b) =>
        {
            int byValue = b.Value.CompareTo(a.Value);
            if (byValue != 0)
            {
                return byValue;
            }

            int byDistance = a.DistanceToEnemyMeters.CompareTo(b.DistanceToEnemyMeters);
            if (byDistance != 0)
            {
                return byDistance;
            }

            // A tie on both value and distance goes to the point overlooking its approach — design
            // SS2's "hilltops overlooking an approach" priority.
            bool aHigh = OverlooksApproach(a.Point.Position);
            bool bHigh = OverlooksApproach(b.Point.Position);
            return aHigh == bHigh ? 0 : aHigh ? -1 : 1;
        });
    }

    /// <summary>Local samples around <paramref name="point"/> spread this far apart — the
    /// <c>FindHighGround</c> sample shape (<c>Ai/CommanderEnemyCommanderGround.cs:141-158</c>),
    /// reused rather than re-sampled.</summary>
    private const float OverlookSampleSpreadMeters = 900f;

    private const int OverlookSampleCount = 4;

    /// <summary>True when <paramref name="point"/> sits higher than the mean of four compass
    /// samples around it — "hilltops overlooking an approach" (design SS2), one definition shared
    /// with nothing else because the height sampler it borrows the shape from answers a different
    /// question (the highest nearby point, not whether this one already is it).</summary>
    internal static bool OverlooksApproach(GlobalPosition point)
    {
        float sum = 0f;
        for (int i = 0; i < OverlookSampleCount; i++)
        {
            float angle = i * (Mathf.PI * 2f / OverlookSampleCount);
            GlobalPosition sample = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
                point.x + Mathf.Cos(angle) * OverlookSampleSpreadMeters,
                point.y,
                point.z + Mathf.Sin(angle) * OverlookSampleSpreadMeters));
            sum += sample.y;
        }

        return point.y > sum / OverlookSampleCount;
    }

    /// <summary>Hold posts per point, kept between ticks — a point does not move, and rebuilding a
    /// set snaps every post to the terrain. Rebuilt when the garrison's composition or the threat
    /// bearing changes (<see cref="EnsureHoldPostSet"/>). Cleared in
    /// <see cref="ResetSession"/> for the reason the garrison step's own clear gave: the point
    /// objects a stale cache keys on do not survive a mission reload.</summary>
    private readonly Dictionary<CommanderStrategicPoint, CommanderHoldPostSet> holdPosts = new();

    /// <summary>A member this close to its hold post is on station; re-ordering it only makes it
    /// shuffle, and every issue is a networked RPC (the same reasoning as
    /// <c>Ai/CommanderEnemyCommanderDefence.DefenceArrivedMeters</c>, independently declared here
    /// because that one is private to a different class).</summary>
    private const float HoldArrivedMeters = 150f;

    /// <summary>Ring posts around <paramref name="center"/>: <paramref name="slots"/> of them,
    /// evenly spaced at <paramref name="radius"/>, terrain-snapped, sea-level posts skipped. The
    /// shared ring-building shape both <see cref="EnsureHoldPosts"/> (a point's own ring) and the
    /// reserve mission's ring (T11, around the HQ's territory centre rather than a point) build
    /// from — Reuse rule 5, generalised rather than duplicated a second time.</summary>
    private static List<GlobalPosition> BuildHoldRing(GlobalPosition center, float radius, int slots)
    {
        List<GlobalPosition> posts = new();
        int slotCount = Mathf.Max(1, slots);
        for (int i = 0; i < slotCount; i++)
        {
            float angle = i * (Mathf.PI * 2f / slotCount);
            GlobalPosition candidate = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
                center.x + Mathf.Cos(angle) * radius,
                center.y,
                center.z + Mathf.Sin(angle) * radius));
            if (!CommanderGameAccess.IsBelowSeaLevel(candidate))
            {
                posts.Add(candidate);
            }
        }

        return posts;
    }

    /// <summary>
    /// The point's fighting ring as plain positions, for a caller that wants somewhere on the point
    /// to put vehicles and has no platoon composition to spread by (the picket insertion's landing
    /// posts, <c>Operations/CommanderOperationsInsertion.cs</c>). A ring a garrison has already
    /// planned is handed back as it stands rather than re-planned for this caller's slot count, so
    /// two callers with different needs cannot thrash the cache between them. Ledger row 19: cut out
    /// of the points track's original garrison-post builder (Reuse rule 3), which forwarded here
    /// from T7 and was deleted along with the rest of its file in T11; the ring moved from
    /// <c>Radius * HoldRingFraction</c> to the full radius with ground-tactics §1.
    /// </summary>
    internal List<GlobalPosition> EnsureHoldPosts(CommanderStrategicPoint point, int slots)
    {
        if (holdPosts.TryGetValue(point, out CommanderHoldPostSet cached) && cached.Outer.Count > 0)
        {
            return cached.Outer;
        }

        return EnsureHoldPostSet(point, 0f, Mathf.Max(1, slots), 0, 0).Outer;
    }

    /// <summary>Midpoint of the home guard's own ring clamp
    /// (<c>DefenceRingMinMeters</c>/<c>DefenceRingMaxMeters</c>, <c>Ai/CommanderEnemyCommanderDefence.cs:48-49</c>)
    /// — the reserve platoon stands roughly where the guard used to (T11). Planner-chosen.</summary>
    private const float ReserveRingMeters = 900f;

    /// <summary>Scratch for <see cref="DriveMembersToPosts"/>'s stable member sort.</summary>
    private readonly List<Unit> holdMembersScratch = new();

    /// <summary>
    /// Sends every member to a post on <paramref name="posts"/> — the <c>DriveGarrisonToPosts</c>
    /// shape: a stable sort by instance id so a member tends to keep the same post between reviews,
    /// and an on-station skip so a vehicle already at its post is not re-issued every movement tick.
    /// <paramref name="issued"/>, when given, records each member's assigned post the same way
    /// <see cref="CommanderMoveService.IssuePlatoonMove"/> records a formation slot — written even
    /// when the on-station skip fires, so <see cref="CountInCohesion"/> always measures a holding
    /// platoon against the post it actually holds, never a stale formation slot left over from the
    /// move that brought it there (the regression this fixes: without this, a platoon forced back to
    /// <c>Moving</c> next review looked "in cohesion" the moment it neared the old slot, flipped back
    /// to <c>Holding</c>, and was driven straight back out again — forever). Pickets pass no
    /// dictionary: a picket detachment is not a platoon and has no cohesion to measure.
    /// </summary>
    /// <param name="stableByInstanceId">True — the default — sorts the members by instance id so a
    /// member tends to keep the same post between reviews. False keeps the caller's own order,
    /// which is what a defence arc needs: its slot 0 is the centre of the line and has to be a tank
    /// (ground-tactics §2), so the caller has already ordered its members by
    /// <see cref="MarchRank"/>.</param>
    private void DriveMembersToPosts(
        IReadOnlyList<Unit> members,
        List<GlobalPosition> posts,
        Dictionary<Unit, GlobalPosition>? issued = null,
        bool stableByInstanceId = true)
    {
        if (posts.Count == 0)
        {
            return;
        }

        holdMembersScratch.Clear();
        holdMembersScratch.AddRange(members);
        if (stableByInstanceId)
        {
            holdMembersScratch.Sort(static (a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        }
        for (int i = 0; i < holdMembersScratch.Count; i++)
        {
            Unit unit = holdMembersScratch[i];
            if (unit == null || unit.disabled)
            {
                continue;
            }

            GlobalPosition post = posts[i % posts.Count];
            if (issued != null)
            {
                issued[unit] = post;
            }

            if (FastMath.InRange(unit.transform.GlobalPosition(), post, HoldArrivedMeters))
            {
                continue;
            }

            CommanderGameAccess.TrySetDestination(unit, post);
        }
    }

    /// <summary>
    /// Spreads a garrison over its point's whole ring by role (design §1): the umbrella on the
    /// inner pair facing the threat, the carrier and the truck on the inner pair opposite, the
    /// tanks on the fighting ring at the point's full radius. Each group goes through the same
    /// <see cref="DriveMembersToPosts"/> the single-ring version used, so the stable member sort,
    /// the on-station skip and the <paramref name="issued"/> write are unchanged.
    /// </summary>
    private void DriveToHoldPosts(
        FactionHQ hq,
        CommanderStrategicPoint point,
        IReadOnlyList<Unit> members,
        Dictionary<Unit, GlobalPosition>? issued = null)
    {
        SplitByHoldRole(members, holdRoleOuter, holdRoleAirDefence, holdRoleInner);
        CommanderHoldPostSet posts = EnsureHoldPostSet(
            point,
            ThreatBearingFor(hq, point),
            Mathf.Max(1, holdRoleOuter.Count),
            holdRoleAirDefence.Count,
            holdRoleInner.Count);
        DriveMembersToPosts(holdRoleOuter, posts.Outer, issued);
        DriveMembersToPosts(holdRoleAirDefence, PostsOrFallback(posts.AirDefence, posts.Outer), issued);
        DriveMembersToPosts(holdRoleInner, PostsOrFallback(posts.Inner, posts.Outer), issued);
    }

    /// <summary>The reserve platoon's ring: around the HQ's territory centre rather than a point,
    /// since the operations service cannot call the home guard's own <c>private static</c>
    /// <c>EnsureDefencePosts</c> (T11).</summary>
    private void DriveToReserveRing(FactionHQ hq, IReadOnlyList<Unit> members, Dictionary<Unit, GlobalPosition> issued)
    {
        GlobalPosition center = CommanderCaptureService.GetTerritoryCenter(hq);
        DriveMembersToPosts(members, BuildHoldRing(center, ReserveRingMeters, Mathf.Max(1, members.Count)), issued);
    }

    /// <summary>How far a member may be from its formation slot and still count as "with the
    /// platoon" for the arrival rule. Design SS1 names the constant and gives no number; a
    /// six-vehicle wedge at <c>GroundFormationSpacingMeters</c> spans about 75 m, so 250 m is
    /// "closed up" with room for terrain. Planner-chosen.</summary>
    private const float FormationCohesionMeters = 250f;

    private static int CountInCohesion(CommanderPlatoon platoon)
    {
        int count = 0;
        for (int i = 0; i < platoon.Members.Count; i++)
        {
            Unit unit = platoon.Members[i];
            if (unit == null
                || unit.disabled
                || !platoon.Issued.TryGetValue(unit, out GlobalPosition slot))
            {
                continue;
            }

            if (CommanderGameAccess.ApproximatelyEqual(unit.transform.GlobalPosition(), slot, FormationCohesionMeters))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Below half establishment (design SS1) — integer maths so the boundary is exact.</summary>
    private const int WithdrawStrengthNumerator = 1;
    private const int WithdrawStrengthDenominator = 2;

    /// <summary>Below 40% of establishment an attack has failed (design SS3) — integer maths so the
    /// boundary is exact.</summary>
    private const int FailStrengthNumerator = 2;
    private const int FailStrengthDenominator = 5;

    /// <summary>Pure, for the self-check. A platoon under half its establishment withdraws.</summary>
    internal static bool ShouldWithdraw(int strength, int establishment)
    {
        return strength * WithdrawStrengthDenominator < establishment * WithdrawStrengthNumerator;
    }

    /// <summary>
    /// Pure, for the self-check. The losses rule plus the teeth rule: a platoon whose recipe wants
    /// armour and that has none left is air defence and carriers driving at the enemy on their own,
    /// so it withdraws whatever its head count. A recipe with no armour slot never trips it.
    /// </summary>
    internal static bool ShouldWithdraw(int strength, int establishment, int armour, int armourWanted)
    {
        return ShouldWithdraw(strength, establishment) || (armourWanted > 0 && armour == 0 && strength > 0);
    }

    /// <summary>Live members of <paramref name="platoon"/> in <paramref name="role"/>.</summary>
    private static int CountRole(CommanderPlatoon platoon, CommanderPlatoonRole role)
    {
        int count = 0;
        for (int i = 0; i < platoon.Members.Count; i++)
        {
            Unit member = platoon.Members[i];
            if (member != null && !member.disabled
                && CommanderPlatoonRoles.Of(member.definition as VehicleDefinition) == role)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Pure, for the self-check. An attack under 40% of its establishment has failed.</summary>
    internal static bool AttackHasFailed(int strength, int establishment)
    {
        return strength * FailStrengthDenominator < establishment * FailStrengthNumerator;
    }

    /// <summary>One ForwardBase mission per front point not already carrying a mission, in ranked
    /// order, wanting two platoons under a threat mark and one otherwise (design SS2). The
    /// forward-base share cap (T8) and the truck requisition/siting (T9) are added on top of this.</summary>
    private void PlanForwardBases(FactionHQ hq, OperationsState state)
    {
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            // B6 fix: PlanPickets already tests ownership (below) before it will site anything on a
            // point; this loop did not, so an enemy-held front point got a forward-base mission of
            // its own, sitting on the same point an Attack mission would target. Copying PlanPickets'
            // test keeps a forward base off any point this HQ does not hold.
            // Held OR neutral-and-reachable (design SS2: "each front point held or reachable"). The
            // held-only version of this test meant no mission ever existed at match start, when every
            // control point is neutral, so nothing ever went out to take one. Enemy-held points are
            // attack targets, never forward bases.
            if (!ranked.IsFront || !IsHeldOrReachable(hq, ranked.Point) || HasMissionFor(state, ranked.Point))
            {
                continue;
            }

            state.Missions.Add(new CommanderOperationsMission
            {
                Kind = CommanderMissionKind.ForwardBase,
                Point = ranked.Point,
                WantedPlatoons = ranked.HasThreatMark ? 2 : 1,
                Label = ranked.Point.Label,
            });
        }
    }

    /// <summary>Where a forward base's truck parks — inside the platoon's own ring, where it is
    /// covered. Planner-chosen.</summary>
    private const float TruckRingFraction = 0.3f;

    /// <summary>
    /// Gives every forward base without one the cheapest free-pool munitions truck, detaching it
    /// from the game's own rearm AI (<c>allowRestock: false</c>, so it cannot be driven off the
    /// forward base the moment it registers below half capacity) and parking it on the ring at
    /// <c>Radius * TruckRingFraction</c>. None available -> logs once per mission (design SS2).
    /// </summary>
    private static void FillMissionTrucks(FactionHQ hq, OperationsState state)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind != CommanderMissionKind.ForwardBase || mission.Point == null)
            {
                continue;
            }

            if (mission.Truck != null && (mission.Truck.disabled || mission.Truck.NetworkHQ != hq))
            {
                mission.Truck = null;
            }

            if (mission.Truck != null)
            {
                continue;
            }

            Unit? truck = null;
            for (int p = 0; p < state.Pool.Count; p++)
            {
                if (CommanderPlatoonRoles.Of(state.Pool[p]?.definition as VehicleDefinition) == CommanderPlatoonRole.Truck)
                {
                    truck = state.Pool[p];
                    break;
                }
            }

            if (truck == null)
            {
                if (!mission.NoTruckLogged)
                {
                    mission.NoTruckLogged = true;
                    CommanderAiLog.Note(hq, $"{mission.Label}: no munitions truck available; the forward base runs dry.");
                }

                continue;
            }

            state.Pool.Remove(truck);
            CommanderMoveService.TryDetachFromRearmLogistics(truck, allowRestock: false);
            mission.Truck = truck;
            mission.NoTruckLogged = false;
            float ring = mission.Point.Radius * TruckRingFraction;
            GlobalPosition park = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
                mission.Point.Position.x + ring, mission.Point.Position.y, mission.Point.Position.z));
            CommanderGameAccess.TrySetDestination(truck, park);
        }
    }

    /// <summary>
    /// A point this commander may plan a hold or picket mission for: one it holds, or a neutral one
    /// within <c>FrontRangeMeters</c> of something it does hold (a base or a control point). That
    /// reach is what lets the line grow outward point by point instead of only ever staffing what
    /// it already owns. Enemy-held points are excluded — those are attack targets.
    /// </summary>
    private static bool IsHeldOrReachable(FactionHQ hq, CommanderStrategicPoint point)
    {
        FactionHQ? owner = point.GetOwner();
        if (ReferenceEquals(owner, hq))
        {
            return true;
        }

        if (owner != null)
        {
            return false;
        }

        return NearestHeldAssetDistance(hq, point.Position) <= CommanderSettings.OperationsFrontRangeMeters;
    }

    /// <summary>Shortest horizontal distance from <paramref name="position"/> to a base or control
    /// point this HQ holds; the mirror of <see cref="NearestEnemyAssetDistance"/>.</summary>
    private static float NearestHeldAssetDistance(FactionHQ hq, GlobalPosition position)
    {
        float best = float.MaxValue;
        IReadOnlyList<CommanderStrategicPoint>? points = CommanderStrategicPointService.Instance?.Points;
        if (points == null)
        {
            return best;
        }

        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            if (!ReferenceEquals(point.GetOwner(), hq))
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(position.AsVector3(), point.Position.AsVector3());
            if (distance < best)
            {
                best = distance;
            }
        }

        return best;
    }

    private static bool HasMissionFor(OperationsState state, CommanderStrategicPoint point)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            if (ReferenceEquals(state.Missions[i].Point, point))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The one <see cref="CommanderMissionKind.Reserve"/> mission per HQ — created once,
    /// then reused for the rest of the mission.</summary>
    private static CommanderOperationsMission FindOrCreateReserveMission(OperationsState state)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            if (state.Missions[i].Kind == CommanderMissionKind.Reserve)
            {
                return state.Missions[i];
            }
        }

        CommanderOperationsMission reserve = new()
        {
            Kind = CommanderMissionKind.Reserve,
            Point = null,
            WantedPlatoons = 0,
            Label = "RESERVE",
        };
        state.Missions.Add(reserve);
        return reserve;
    }

    /// <summary>
    /// The nearest friendly forward base to <paramref name="platoon"/> itself, not to the HQ's
    /// territory centre — a platoon withdrawing from a distant front is not helped by a rally point
    /// chosen for being close to home. Falls back to the territory centre with no live leader to
    /// measure from (about to be swept as stale anyway) or no forward base yet formed.
    /// </summary>
    private static GlobalPosition NearestFriendlyRallyPoint(FactionHQ hq, OperationsState state, CommanderPlatoon platoon)
    {
        GlobalPosition territory = CommanderCaptureService.GetTerritoryCenter(hq);
        GlobalPosition from = platoon.Leader != null && !platoon.Leader.disabled
            ? platoon.Leader.transform.GlobalPosition()
            : territory;
        float best = float.MaxValue;
        GlobalPosition bestPoint = territory;
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind != CommanderMissionKind.ForwardBase || mission.Point == null || mission.Assigned.Count == 0)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(from.AsVector3(), mission.Point.Position.AsVector3());
            if (distance < best)
            {
                best = distance;
                bestPoint = mission.Point.Position;
            }
        }

        // A held airbase is a rally point too: whichever is nearer, the forward base or the base.
        // Home is where fresh vehicles arrive, so a remnant that gets there is folded into the
        // next platoon formed rather than waiting alone for replacements.
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            GlobalPosition centre = airbase.center.GlobalPosition();
            float distance = CommanderGameAccess.HorizontalDistance(from.AsVector3(), centre.AsVector3());
            if (distance < best)
            {
                best = distance;
                bestPoint = centre;
            }
        }

        return bestPoint;
    }

    /// <summary>Distance from its rally point within which a withdrawing platoon counts as
    /// arrived and is folded away; wide enough to cover a base's apron and a control point's ring.</summary>
    private const float FoldArrivedMeters = 600f;

    /// <summary>
    /// Folds every withdrawing platoon that has reached its rally point into whatever is there: a
    /// forward-base platoon short of vehicles takes as many as it needs, and the rest go back to the
    /// free pool for the next platoon formed. The remnant dissolves. A withdrawn remnant used to
    /// sit at the rally point until the buyer topped it back up, which on a busy front could take
    /// the rest of the match.
    /// </summary>
    private void FoldWithdrawnPlatoons(FactionHQ hq, OperationsState state)
    {
        for (int i = state.Platoons.Count - 1; i >= 0; i--)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (platoon.State != CommanderPlatoonState.Withdrawing || platoon.Leader == null || platoon.Leader.disabled)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                platoon.Leader.transform.position, platoon.Objective.ToLocalPosition());
            if (distance > FoldArrivedMeters)
            {
                continue;
            }

            // A forward-base platoon at this rally point takes what it is short of.
            CommanderPlatoon? host = null;
            for (int m = 0; m < state.Missions.Count && host == null; m++)
            {
                CommanderOperationsMission mission = state.Missions[m];
                if (mission.Kind != CommanderMissionKind.ForwardBase || mission.Point == null
                    || CommanderGameAccess.HorizontalDistance(mission.Point.Position.AsVector3(), platoon.Objective.AsVector3()) > FoldArrivedMeters)
                {
                    continue;
                }

                for (int a = 0; a < mission.Assigned.Count; a++)
                {
                    if (mission.Assigned[a].Members.Count < mission.Assigned[a].Establishment)
                    {
                        host = mission.Assigned[a];
                        break;
                    }
                }
            }

            int merged = 0;
            for (int u = platoon.Members.Count - 1; u >= 0; u--)
            {
                Unit member = platoon.Members[u];
                platoon.Members.RemoveAt(u);
                platoon.Issued.Remove(member);
                if (member == null || member.disabled)
                {
                    continue;
                }

                if (host != null && host.Members.Count < host.Establishment)
                {
                    host.Members.Add(member);
                    OrderForMarch(host.Members);
                    merged++;
                }
                else
                {
                    state.Pool.Add(member);
                }
            }

            ReleaseFromMission(platoon);
            state.Platoons.RemoveAt(i);
            CommanderAiLog.Note(
                hq,
                host != null
                    ? $"{platoon.Name} folded into {host.Name} ({merged} vehicle(s)); the rest rejoin the pool."
                    : $"{platoon.Name} stood down at its rally point; its vehicles rejoin the pool.");
        }
    }

    private static void ReleaseFromMission(CommanderPlatoon platoon)
    {
        platoon.Mission?.Assigned.Remove(platoon);
        platoon.Mission = null;
        // Leaving the mission ends any reinforcement answer with it (addendum 2026-09-14 §3) —
        // the single choke point every departure path already goes through.
        platoon.ReinforcesLabel = string.Empty;
        // And with it every ground posture: a platoon whose job has changed under it must not
        // inherit the arc, bound or flank it was halfway through for the last one (ground-tactics).
        platoon.ClearGroundPosture();
    }

    /// <summary>
    /// Every platoon, every review: applies the losses rule (withdraw under half strength, dissolve
    /// at zero), matches whatever is left to open missions in pipeline order (forward bases first —
    /// the only mission kind this task knows about; T8/T12-T15 add pickets and offensives ahead of
    /// the reserve), flips a platoon that has closed up on its forward base to
    /// <see cref="CommanderPlatoonState.Holding"/>, and tops up under-strength platoons — or forms a
    /// new one — from whatever the pool has spare.
    /// </summary>
    private void AssignPlatoons(FactionHQ hq, OperationsState state)
    {
        for (int i = state.Platoons.Count - 1; i >= 0; i--)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            int strength = platoon.Members.Count;
            if (strength == 0)
            {
                ReleaseFromMission(platoon);
                state.Platoons.RemoveAt(i);
                continue;
            }

            // A platoon still forming is below half strength by definition until enough vehicles
            // have been bought; it is gathering, not retreating, so the losses rule does not apply
            // until it has left the form-up point. Without this a platoon formed on one vehicle
            // withdrew on its first review and never got to form.
            int armour = CountRole(platoon, CommanderPlatoonRole.Armour);
            int armourWanted = CommanderSettings.OperationsRecipeArmour;
            if (platoon.State != CommanderPlatoonState.Withdrawing
                && platoon.State != CommanderPlatoonState.Forming
                && ShouldWithdraw(strength, platoon.Establishment, armour, armourWanted))
            {
                platoon.State = CommanderPlatoonState.Withdrawing;
                platoon.Objective = NearestFriendlyRallyPoint(hq, state, platoon);
                ReleaseFromMission(platoon);
            }
            else if (platoon.State == CommanderPlatoonState.Withdrawing
                && !ShouldWithdraw(strength, platoon.Establishment, armour, armourWanted))
            {
                // B3 fix: Withdrawing was terminal — nothing ever set a platoon back once its
                // requisitioned replacements (see PostWithdrawingRequisitions) brought it back over
                // half establishment, so it sat at the rally point for the rest of the match. Forming
                // is the correct re-entry state: below establishment is normal for it, and the
                // matching loops below only skip a platoon still Withdrawing.
                platoon.State = CommanderPlatoonState.Forming;
                platoon.FormingSince = Time.time;
            }
        }

        // B4 fix: the reserve mission is the leftover bucket, not a trap. Releasing a reserve-assigned
        // platoon here, before the forward-base and attack loops below run, lets it compete for a
        // mission again instead of being permanently excluded by their `platoon.Mission != null` skip
        // (previously nothing but the losses rule, DemoteForwardBaseToPicket and ResolveAttack ever
        // cleared `Mission`, and none of those three ever ran for a platoon sitting in reserve).
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (platoon.Mission != null && platoon.Mission.Kind == CommanderMissionKind.Reserve)
            {
                ReleaseFromMission(platoon);
            }
        }

        for (int m = 0; m < state.Missions.Count; m++)
        {
            CommanderOperationsMission mission = state.Missions[m];
            if (mission.Kind != CommanderMissionKind.ForwardBase || mission.Point == null)
            {
                continue;
            }

            for (int i = 0; i < state.Platoons.Count && mission.Assigned.Count < mission.WantedPlatoons; i++)
            {
                CommanderPlatoon platoon = state.Platoons[i];
                if (!IsAvailableForMission(platoon))
                {
                    continue;
                }

                platoon.Objective = mission.Point.Position;
                AttachPlatoon(hq, platoon, mission, CommanderPlatoonState.Moving);
            }
        }

        // T14: offensives come after forward bases in the matching order (design SS2's pipeline).
        for (int m = 0; m < state.Missions.Count; m++)
        {
            CommanderOperationsMission mission = state.Missions[m];
            if (mission.Kind != CommanderMissionKind.Attack)
            {
                continue;
            }

            for (int i = 0; i < state.Platoons.Count && mission.Assigned.Count < mission.WantedPlatoons; i++)
            {
                CommanderPlatoon platoon = state.Platoons[i];
                if (!IsAvailableForMission(platoon))
                {
                    continue;
                }

                AttachPlatoon(hq, platoon, mission, CommanderPlatoonState.Attacking);
            }
        }

        // Addendum 2026-09-14 §3: a reinforcement request the released reserve platoons above
        // could not fill is answered by pulling a platoon that is Holding another forward base —
        // the most rear point first, never from an attack in progress. Whatever this cannot fill
        // stays on the requisition the mission's raised wanted count already posts, so the buyer
        // builds the rest.
        FillReinforcementsFromPosts(hq, state);

        // T11: whatever the forward-base share leaves becomes the reserve — the home guard's old
        // job (design SS1: "the home guard becomes the base's reserve platoon(s)").
        CommanderOperationsMission reserve = FindOrCreateReserveMission(state);
        GlobalPosition territoryCenter = CommanderCaptureService.GetTerritoryCenter(hq);
        int reserveWanted = Mathf.Max(0, CommanderSettings.OperationsReservePlatoons);
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (IsAvailableForMission(platoon) && reserve.Assigned.Count < reserveWanted)
            {
                platoon.Mission = reserve;
                reserve.Assigned.Add(platoon);
                // A platoon already holding the reserve ring is re-attached and left alone. The
                // release loop above clears every reserve platoon's mission each review so it can
                // compete for real work; forcing Moving here as well sent a settled platoon back to
                // the centre every 30 s, and it never read as arrived again because the ring posts
                // are not formation slots — so it thrashed between the ring and the centre forever.
                if (platoon.State != CommanderPlatoonState.Holding)
                {
                    platoon.State = CommanderPlatoonState.Moving;
                    platoon.Objective = territoryCenter;
                }
            }
        }

        // DECISION-013: a platoon whose purpose has resolved — its point lost or no longer front,
        // its attack over — and that the standing reserve does not want either has no reason to
        // exist. It dissolves here so its six vehicles become picket stock, which is what actually
        // takes ground on a map whose points are mostly away from the enemy. Backwards, because the
        // dissolve takes the platoon out of the list being walked. Forming and withdrawing platoons
        // are not "available" and are never touched by this.
        for (int i = state.Platoons.Count - 1; i >= 0; i--)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (!IsAvailableForMission(platoon))
            {
                continue;
            }

            CommanderAiLog.Note(hq, $"{platoon.Name} has no purpose left; its vehicles become picket stock.");
            DissolveToPool(state, platoon);
        }

        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (platoon.State != CommanderPlatoonState.Moving
                || platoon.Mission == null
                || (platoon.Mission.Kind != CommanderMissionKind.ForwardBase && platoon.Mission.Kind != CommanderMissionKind.Reserve)
                || platoon.Leader == null
                || platoon.Leader.disabled)
            {
                continue;
            }

            float objectiveRadius = platoon.Mission.Point != null ? platoon.Mission.Point.Radius : ReserveRingMeters;
            float leaderDistance = CommanderGameAccess.HorizontalDistance(
                platoon.Leader.transform.position, platoon.Objective.ToLocalPosition());
            int inCohesion = CountInCohesion(platoon);
            if (HasArrived(leaderDistance, objectiveRadius, inCohesion, platoon.Members.Count))
            {
                platoon.State = CommanderPlatoonState.Holding;
            }
        }

        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (platoon.Members.Count < platoon.Establishment)
            {
                ReinforcePlatoon(state, platoon);
            }
        }

        FormPlatoonsForPurpose(hq, state);
    }

    /// <summary>
    /// Pure, for the self-check. Platoons this commander has a reason to field (DECISION-013: "a
    /// platoon forms only for a purpose facing the enemy"): one per platoon a forward base on a
    /// FRONT point wants, one per platoon an attack wants, plus the standing reserve. A negative
    /// setting never subtracts from another purpose.
    /// </summary>
    internal static int PlatoonPurposeCount(int forwardBasePlatoonsWanted, int attackPlatoonsWanted, int reservePlatoons)
    {
        return Mathf.Max(0, forwardBasePlatoonsWanted)
            + Mathf.Max(0, attackPlatoonsWanted)
            + Mathf.Max(0, reservePlatoons);
    }

    /// <summary>This review's live purpose count broken out the way the review line reports it
    /// (<c>purposes=fob/attack/reserve</c>): platoons wanted by forward bases still on front points,
    /// by attacks, and by the standing reserve. One walk, two callers — the purpose count itself and
    /// the diagnostics line (Reuse rule 4).</summary>
    private static void CountPurposes(OperationsState state, out int forwardBases, out int attacks, out int reserve)
    {
        forwardBases = 0;
        attacks = 0;
        reserve = Mathf.Max(0, CommanderSettings.OperationsReservePlatoons);
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.ForwardBase
                && mission.Point != null
                // A forward base whose point has stopped being front is about to be demoted to a
                // picket; it is no longer a reason to build a platoon.
                && (!TryGetRanked(state, mission.Point, out CommanderRankedPoint ranked) || ranked.IsFront))
            {
                forwardBases += Mathf.Max(0, mission.WantedPlatoons);
            }
            else if (mission.Kind == CommanderMissionKind.Attack)
            {
                attacks += Mathf.Max(0, mission.WantedPlatoons);
            }
        }
    }

    /// <summary>This review's live purpose count: what the missions on the board ask for, plus the
    /// standing reserve.</summary>
    private static int PlatoonPurposeCount(OperationsState state)
    {
        CountPurposes(state, out int forwardBases, out int attacks, out int reserve);
        return PlatoonPurposeCount(forwardBases, attacks, reserve);
    }

    /// <summary>
    /// The purpose the next platoon is being formed for, phrased for the log — the first purpose
    /// the board has not yet filled, taken in the order <see cref="PlatoonPurposeCount(OperationsState)"/>
    /// sums them: an unfilled forward base on a front point, then an unfilled attack, then the
    /// standing reserve. The formation gate has already established that SOME purpose is unfilled,
    /// so the reserve is a true fallback rather than a guess.
    /// </summary>
    private static string NextPlatoonPurpose(OperationsState state)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.ForwardBase
                && mission.Point != null
                && mission.Assigned.Count < mission.WantedPlatoons
                && (!TryGetRanked(state, mission.Point, out CommanderRankedPoint ranked) || ranked.IsFront))
            {
                return $"for ForwardBase {mission.Label}";
            }
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.Attack && mission.Assigned.Count < mission.WantedPlatoons)
            {
                return $"for Attack {mission.Label}";
            }
        }

        return "as the reserve";
    }

    /// <summary>
    /// Forms platoons while the commander still has a purpose for one and the pickets do not want
    /// the pool first (DECISION-013). Before the pickets-first doctrine this formed every full
    /// platoon the pool could field, which is how a map with no enemy anywhere near most of it ended
    /// up carrying 27 platoons; <see cref="MaxPlatoonsFormedPerReview"/> stays as the runaway-loop
    /// guard, but what actually bounds the force now is the purpose count. A hot reload still hands
    /// the whole roster back as one pool, and that pool still goes under orders inside two reviews —
    /// as pickets first, then as many platoons as there are jobs for.
    /// </summary>
    private void FormPlatoonsForPurpose(FactionHQ hq, OperationsState state)
    {
        int openThreatened = CountOpenThreatenedPlatoons(state);
        if (PicketsNeedThePool(state.ShortPicketMissions, openThreatened))
        {
            return;
        }

        int allowed = PlatoonsAheadOfPickets(state.ShortPicketMissions, openThreatened, MaxPlatoonsFormedPerReview);
        for (int formed = 0; formed < allowed && state.Pool.Count > 0; formed++)
        {
            if (PlatoonPurposeCount(state) <= state.Platoons.Count)
            {
                ReportNoPlatoonPurpose(hq, state);
                return;
            }

            state.NoPurposeLoggedPool = -1;
            CommanderPlatoon? platoon = TryFormPlatoon(
                hq, state, CommanderSettings.OperationsPlatoonSize, NextPlatoonPurpose(state));
            if (platoon == null || platoon.Members.Count < platoon.Establishment)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Says that the pool is standing idle on purpose, not by accident (DECISION-013): with no
    /// forward base, attack or reserve short of a platoon, the vehicles left over are picket stock,
    /// and a commander that says nothing here reads as a commander that has stopped working. Logged
    /// once per change in the idle count, the <c>ReportInsertionDenial</c> convention — a review
    /// runs every 30 s and the same line every time is noise nobody reads.
    /// </summary>
    private static void ReportNoPlatoonPurpose(FactionHQ hq, OperationsState state)
    {
        if (state.Pool.Count == 0 || state.NoPurposeLoggedPool == state.Pool.Count)
        {
            return;
        }

        state.NoPurposeLoggedPool = state.Pool.Count;
        CommanderAiLog.Note(
            hq, $"no purpose for a new platoon; {state.Pool.Count} vehicles wait as picket stock.");
    }

    /// <summary>
    /// The one attach point for every site that assigns a platoon to a mission (Reuse rule 4):
    /// the forward-base and attack matching loops above, and the reinforcement strip below. Sets
    /// the platoon's reinforcement bookkeeping on the way — a platoon attached to a mission with an
    /// open request (addendum 2026-09-14 §3) is answering it, and says so once, here, rather than
    /// on every review it stays assigned; a mission with no request clears any stale answer.
    /// </summary>
    private static void AttachPlatoon(
        FactionHQ hq, CommanderPlatoon platoon, CommanderOperationsMission mission, CommanderPlatoonState state)
    {
        platoon.Mission = mission;
        platoon.State = state;
        mission.Assigned.Add(platoon);
        if (mission.ReinforcePlatoons > 0)
        {
            if (platoon.ReinforcesLabel != mission.Label)
            {
                platoon.ReinforcesLabel = mission.Label;
                CommanderAiLog.Note(hq, $"{platoon.Name} reinforces {mission.Label}.");
            }
        }
        else
        {
            platoon.ReinforcesLabel = string.Empty;
        }
    }

    /// <summary>
    /// Addendum 2026-09-14 §3's fill order, step two: a mission with an open reinforcement request
    /// the released reserve platoons could not satisfy takes platoons that are Holding other
    /// forward bases' posts — most rear point first (leaving the point nearest the enemy is the
    /// last thing a commander does), never a platoon already attacking, and only as many as the
    /// request itself asks for.
    /// </summary>
    private static void FillReinforcementsFromPosts(FactionHQ hq, OperationsState state)
    {
        for (int m = 0; m < state.Missions.Count; m++)
        {
            CommanderOperationsMission mission = state.Missions[m];
            if (mission.ReinforcePlatoons <= 0
                || (mission.Kind == CommanderMissionKind.ForwardBase && mission.Point == null))
            {
                continue;
            }

            int want = Mathf.Min(mission.WantedPlatoons - mission.Assigned.Count, mission.ReinforcePlatoons);
            while (want > 0)
            {
                CommanderPlatoon? donor = TakeRearmostHoldingPlatoon(state, mission);
                if (donor == null)
                {
                    break;
                }

                if (mission.Kind == CommanderMissionKind.Attack)
                {
                    AttachPlatoon(hq, donor, mission, CommanderPlatoonState.Attacking);
                }
                else
                {
                    donor.Objective = mission.Point!.Position;
                    AttachPlatoon(hq, donor, mission, CommanderPlatoonState.Moving);
                }

                want--;
            }
        }
    }

    /// <summary>
    /// The donor the strip step takes next: the first <see cref="CommanderPlatoonState.Holding"/>
    /// platoon of the forward base whose point sits FURTHEST from the enemy — "rear points first"
    /// (addendum 2026-09-14 §3). Platoons hold only front points or the reserve ring, so "rear" is
    /// read as the most rear of the held front points, measured by the ranked point's own distance
    /// to the enemy. Detached from its mission on the way out; never an attacking platoon, an
    /// attack mission's platoons are not even considered.
    /// </summary>
    private static CommanderPlatoon? TakeRearmostHoldingPlatoon(OperationsState state, CommanderOperationsMission forMission)
    {
        CommanderPlatoon? best = null;
        float bestDistance = -1f;
        for (int m = 0; m < state.Missions.Count; m++)
        {
            CommanderOperationsMission donor = state.Missions[m];
            if (donor.Kind != CommanderMissionKind.ForwardBase
                || ReferenceEquals(donor, forMission)
                || donor.Point == null
                // A base answering a request of its own is never a donor: two open requests
                // stripping each other's platoons would swap posts every review.
                || donor.ReinforcePlatoons > 0)
            {
                continue;
            }

            for (int i = 0; i < donor.Assigned.Count; i++)
            {
                CommanderPlatoon platoon = donor.Assigned[i];
                if (platoon.State != CommanderPlatoonState.Holding)
                {
                    continue;
                }

                // Furthest from the enemy wins; a point that left the ranked list (the least
                // knowable position) is stripped last.
                float distance = TryGetRanked(state, donor.Point, out CommanderRankedPoint ranked)
                    ? ranked.DistanceToEnemyMeters
                    : float.MaxValue;
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    best = platoon;
                }

                // One candidate per donor base: the first platoon holding its posts.
                break;
            }
        }

        if (best != null)
        {
            ReleaseFromMission(best);
        }

        return best;
    }

    /// <summary>Ceiling on platoons formed in one review, so a reload's 60-vehicle pool is back
    /// under orders inside two reviews while a runaway fill loop can never spin.</summary>
    private const int MaxPlatoonsFormedPerReview = 6;

    /// <summary>
    /// Forward bases the commander wants on its best front points whatever its roster looks like.
    /// Two, because one forward base is a single post the enemy walks around, and because a
    /// commander down to one platoon has to have somewhere to send the next two it builds — with a
    /// floor of one, a single platoon filled the only allowed base, the board showed one purpose,
    /// and one purpose never justified a second platoon. Planner-chosen.
    /// </summary>
    internal const int MinForwardBases = 2;

    /// <summary>
    /// How many forward bases may stand at once. DEMAND comes first and demand does not depend on
    /// how many platoons exist (fix, 2026-09-14): every front point the enemy is actually at wants
    /// its own forward base, and <see cref="MinForwardBases"/> more stand on the best-ranked front
    /// points regardless of the roster. The forward-base share (design SS2) survives only as an
    /// ASSIGNMENT limiter for a commander rich in platoons — it can raise the allowance above the
    /// demand floor, never cut below it.
    /// <para>The rule this replaced was <c>max(1, platoons x share)</c>, which made the demand a
    /// function of the platoon count: one platoon allowed one forward base, one forward base was
    /// one purpose, and the purpose count never rose above the platoon count, so the force could
    /// not grow however hard the enemy pressed. The 2026-09-14 match sat at one platoon and sixteen
    /// pickets for the whole match on exactly that loop.</para>
    /// Pure, for the self-check.
    /// </summary>
    internal static int MaxForwardBases(int platoonCount, float fobShare, int threatenedFrontPoints)
    {
        int demand = MinForwardBases + Mathf.Max(0, threatenedFrontPoints);
        int share = Mathf.FloorToInt(Mathf.Max(0, platoonCount) * Mathf.Clamp01(fobShare));
        return Mathf.Max(demand, share);
    }

    /// <summary>
    /// A front point the enemy is actually at: a threat mark inside its ring, or a contact stamp
    /// still running (a tracked hostile near it, or a vehicle lost on it — the same clock the
    /// contact-priority air demand reads). This is the test that makes forward-base demand
    /// independent of the platoon count, and the one the pool order and the order book both read,
    /// so all three can never disagree about which point is "facing the enemy" (Reuse rule 4).
    /// </summary>
    private static bool IsThreatenedFrontPoint(OperationsState state, CommanderOperationsMission mission)
    {
        if (mission.Point == null)
        {
            return false;
        }

        if (Time.time < mission.ContactUntil)
        {
            return true;
        }

        return TryGetRanked(state, mission.Point, out CommanderRankedPoint ranked) && ranked.IsFront && ranked.HasThreatMark;
    }

    /// <summary>Front points the enemy is at this review — what <see cref="MaxForwardBases"/> turns
    /// into forward-base demand. A point in contact whose threat mark has already timed out counts
    /// too: the contact stamp outlives the mark, and a point the enemy hit minutes ago is not a
    /// quiet point.</summary>
    private static int CountThreatenedFrontPoints(OperationsState state)
    {
        int count = 0;
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            if (ranked.IsFront && ranked.HasThreatMark)
            {
                count++;
            }
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.ForwardBase
                && mission.Point != null
                && Time.time < mission.ContactUntil
                && TryGetRanked(state, mission.Point, out CommanderRankedPoint ranked)
                && ranked.IsFront
                && !ranked.HasThreatMark)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Platoons the purposes FACING THE ENEMY are still short of: a forward base on a front point
    /// the enemy is at, and every attack. Zero means nothing is threatened, which is when the
    /// pickets keep first call on the pool (the user's doctrine); anything above zero is what the
    /// pool, the formation step and the order book all yield to.
    /// </summary>
    private static int CountOpenThreatenedPlatoons(OperationsState state)
    {
        int open = 0;
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.Attack
                || (mission.Kind == CommanderMissionKind.ForwardBase && IsThreatenedFrontPoint(state, mission)))
            {
                open += Mathf.Max(0, mission.WantedPlatoons - mission.Assigned.Count);
            }
        }

        return open;
    }

    private static bool TryGetRanked(OperationsState state, CommanderStrategicPoint? point, out CommanderRankedPoint ranked)
    {
        if (point != null)
        {
            for (int i = 0; i < state.RankedPoints.Count; i++)
            {
                if (ReferenceEquals(state.RankedPoints[i].Point, point))
                {
                    ranked = state.RankedPoints[i];
                    return true;
                }
            }
        }

        ranked = default;
        return false;
    }

    /// <summary>
    /// Returns every live member of <paramref name="platoon"/> to the free pool and takes the
    /// platoon off the board — the dissolve <see cref="DemoteForwardBaseToPicket"/> has always done
    /// inline, lifted out so the surplus-platoon step can use the same one (Reuse rule 5). Goes out
    /// through <see cref="ReleaseFromMission"/>, so the mission bookkeeping, any reinforcement
    /// answer and the ground posture all clear at the one choke point they already clear at. Dead
    /// members are dropped rather than pooled; the pool sweep dropped them on the next review
    /// anyway, and a platoon's formation slots go with it.
    /// </summary>
    private static void DissolveToPool(OperationsState state, CommanderPlatoon platoon)
    {
        for (int i = 0; i < platoon.Members.Count; i++)
        {
            Unit member = platoon.Members[i];
            if (member != null && !member.disabled)
            {
                state.Pool.Add(member);
            }
        }

        platoon.Members.Clear();
        platoon.Issued.Clear();
        ReleaseFromMission(platoon);
        state.Platoons.Remove(platoon);
    }

    /// <summary>Disbands a forward base's platoon(s) back into the free pool and turns the mission
    /// into a picket (design SS2: "a FOB whose point turns rear thins to a picket, releasing the
    /// surplus members back to the pool"). The now-picket mission claims two of those members back
    /// for itself in <see cref="FillPickets"/>; the rest are free for the next review's reserve or
    /// reinforcement.</summary>
    private static void DemoteForwardBaseToPicket(OperationsState state, CommanderOperationsMission mission)
    {
        for (int i = mission.Assigned.Count - 1; i >= 0; i--)
        {
            DissolveToPool(state, mission.Assigned[i]);
        }

        mission.Assigned.Clear();
        if (mission.Truck != null)
        {
            state.Pool.Add(mission.Truck);
            mission.Truck = null;
        }

        mission.NoTruckLogged = false;
        mission.Kind = CommanderMissionKind.Picket;
        mission.WantedPlatoons = 0;
        // A demoted base asks for no reinforcements (addendum 2026-09-14 §3): the request review
        // skips pickets, so a lingering request would never close and the marker flag never clear.
        mission.ReinforcePlatoons = 0;
        mission.ReinforceBelowSince = -1f;
    }

    /// <summary>
    /// Rear points the commander holds get a two-vehicle picket (design SS2: "so they keep
    /// paying"), filled from the cheapest free-pool vehicles rather than a platoon — a picket is a
    /// detachment, never a platoon, so it never competes for the forward-base share. Surplus front
    /// points beyond <see cref="MaxForwardBases"/> demote to a picket instead, and a picket whose
    /// point gains a threat mark promotes back to a forward base.
    /// </summary>
    private void PlanPickets(FactionHQ hq, OperationsState state)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            if (state.Missions[i].Kind == CommanderMissionKind.Picket)
            {
                // The picket mirror of the platoon loss stamp in SweepPool, for the same reason
                // (addendum 2026-09-14 §2): a picket that lost a vehicle is under attack.
                if (HasCombatLoss(hq, state.Missions[i].PicketMembers))
                {
                    state.Missions[i].LastLossAt = Time.time;
                }

                state.Missions[i].PicketMembers.RemoveAll(unit => IsStalePoolUnit(hq, unit));
            }
        }

        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            if (ranked.IsFront || !IsHeldOrReachable(hq, ranked.Point) || HasMissionFor(state, ranked.Point))
            {
                continue;
            }

            state.Missions.Add(new CommanderOperationsMission
            {
                Kind = CommanderMissionKind.Picket,
                Point = ranked.Point,
                WantedPlatoons = 0,
                Label = ranked.Point.Label,
            });
        }

        int allowance = MaxForwardBases(
            state.Platoons.Count, CommanderSettings.OperationsFobShare, CountThreatenedFrontPoints(state));
        OrderForwardBasesByRank(state);
        int kept = 0;
        for (int i = 0; i < forwardBasesByRank.Count; i++)
        {
            CommanderOperationsMission mission = forwardBasesByRank[i];
            bool stillFront = !TryGetRanked(state, mission.Point, out CommanderRankedPoint ranked) || ranked.IsFront;
            if (!stillFront)
            {
                DemoteForwardBaseToPicket(state, mission);
                continue;
            }

            // A front point the enemy is standing on keeps its forward base whatever the allowance
            // says — that is the whole of "demand does not depend on the platoon count". Everything
            // else competes for the allowance in ranked order.
            if (IsThreatenedFrontPoint(state, mission) || kept < allowance)
            {
                kept++;
                continue;
            }

            DemoteForwardBaseToPicket(state, mission);
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.Picket
                && TryGetRanked(state, mission.Point, out CommanderRankedPoint ranked)
                && ranked.HasThreatMark)
            {
                mission.Kind = CommanderMissionKind.ForwardBase;
                mission.WantedPlatoons = 2;
            }
        }

        // Order (a) before (b): the platoons a threatened purpose is waiting on are left vehicles in
        // the pool before any picket is topped up. With nothing threatened the hold-back is zero and
        // the pickets have the pool to themselves, exactly as the pickets-first doctrine says.
        // Attacks opened by THIS review's PlanOffensive are not on the board yet (it runs after this
        // step); one opened on any earlier review is, which is every attack that has actually
        // started forming.
        FillPickets(
            hq,
            state,
            PoolHeldForThreatenedPurposes(
                CountOpenThreatenedPlatoons(state),
                CommanderSettings.OperationsPlatoonSize,
                MaxPlatoonsFormedPerReview));
    }

    /// <summary>Scratch for the demotion walk's ranked order, reused across reviews rather than
    /// allocated each time (the hold-post scratch convention).</summary>
    private readonly List<CommanderOperationsMission> forwardBasesByRank = new();

    /// <summary>
    /// Fills <see cref="forwardBasesByRank"/> with every forward-base mission in RANKED order, not
    /// mission-creation order: the bases that keep their platoons are the best-ranked ones, and a
    /// base opened ten reviews ago on a point that has since fallen down the ranking has no claim
    /// over one opened this review on a better point. A base whose point has left the ranked list
    /// goes last — the least knowable position, so the first to thin, which is the tie-break
    /// <see cref="TakeRearmostHoldingPlatoon"/> already uses.
    /// </summary>
    private void OrderForwardBasesByRank(OperationsState state)
    {
        forwardBasesByRank.Clear();
        for (int r = 0; r < state.RankedPoints.Count; r++)
        {
            for (int m = 0; m < state.Missions.Count; m++)
            {
                CommanderOperationsMission mission = state.Missions[m];
                if (mission.Kind == CommanderMissionKind.ForwardBase
                    && ReferenceEquals(mission.Point, state.RankedPoints[r].Point))
                {
                    forwardBasesByRank.Add(mission);
                }
            }
        }

        for (int m = 0; m < state.Missions.Count; m++)
        {
            CommanderOperationsMission mission = state.Missions[m];
            if (mission.Kind == CommanderMissionKind.ForwardBase && !forwardBasesByRank.Contains(mission))
            {
                forwardBasesByRank.Add(mission);
            }
        }
    }

    /// <summary>
    /// True while a picket on a point away from the front is still short of its two vehicles, which
    /// is when the pool belongs to the pickets and NOT to a new platoon (DECISION-013: "pickets
    /// should be the primary capture mechanic, rather than spamming large platoons all over the
    /// map"). Pure, for the self-check.
    /// <para>This is the old <c>PlatoonsNeedThePool</c> guard pointed the other way. That one
    /// withheld the pool from pickets while any platoon was short of its recipe or the reserve was
    /// under strength — and since the buyer kept the book open forever, it was true almost always,
    /// so a rear crossroads never got its two vehicles while 27 platoons formed behind it.</para>
    /// <para>Amended 2026-09-14: the pickets keep first call only while NOTHING faces the enemy.
    /// The moment a forward base on a threatened front point or an attack is short of a platoon,
    /// the pool belongs to that platoon. Without this the guard was unconditional, so a commander in
    /// contact went on feeding quiet rear pickets and never fielded the platoon the contact was
    /// asking for — the deadlock the 2026-09-14 match sat in with one platoon and sixteen full
    /// pickets, review after review.</para>
    /// </summary>
    internal static bool PicketsNeedThePool(int shortPicketMissions, int openThreatenedPlatoons)
    {
        return shortPicketMissions > 0 && openThreatenedPlatoons <= 0;
    }

    /// <summary>
    /// How many platoons may form this review out of a pool the pickets are still short of: none
    /// while nothing faces the enemy, and otherwise exactly what the threatened purposes ask for.
    /// Never more, so the standing reserve — the last claim on the pool — stays behind the pickets
    /// rather than dressing itself up as a reason to raid them. Pure, for the self-check.
    /// </summary>
    internal static int PlatoonsAheadOfPickets(int shortPicketMissions, int openThreatenedPlatoons, int maxPerReview)
    {
        int ceiling = Mathf.Max(0, maxPerReview);
        return shortPicketMissions <= 0 ? ceiling : Mathf.Clamp(openThreatenedPlatoons, 0, ceiling);
    }

    /// <summary>
    /// Vehicles the pickets leave in the pool for the platoons a threatened purpose is waiting on —
    /// one platoon's worth each, bounded by what the formation step could actually build this
    /// review, since holding back more than that only idles vehicles nobody will use. Pure, for the
    /// self-check.
    /// </summary>
    internal static int PoolHeldForThreatenedPurposes(int openThreatenedPlatoons, int platoonSize, int maxPerReview)
    {
        int platoons = Mathf.Clamp(openThreatenedPlatoons, 0, Mathf.Max(0, maxPerReview));
        return platoons * Mathf.Max(1, platoonSize);
    }

    /// <summary>How far past the front line a rear point still counts as "next to the fight": one
    /// more front range, so a point the enemy could reach in the same push as the front line is
    /// filled before one deep in the commander's own territory. Planner-chosen.</summary>
    private const float PicketFrontAdjacentMultiplier = 2f;

    /// <summary>A rear point this close to the enemy is the next one they reach, so its picket takes
    /// pool vehicles before a quiet one deep in the rear (pool order (b) before (c)). Pure, for the
    /// self-check.</summary>
    internal static bool IsFrontAdjacentPicket(float distanceToEnemyMeters, float frontRangeMeters)
    {
        return distanceToEnemyMeters <= Mathf.Max(1f, frontRangeMeters) * PicketFrontAdjacentMultiplier;
    }

    /// <summary>
    /// Tops up every picket to <c>PointsMinGarrison</c> with the cheapest free-pool vehicles by
    /// <c>definition.value</c>, then drives them to the point's hold posts, and records how many
    /// pickets away from the front are still short for <see cref="PicketsNeedThePool"/>.
    /// <para>The fill runs in the pool's priority order (fix, 2026-09-14): pickets on front-adjacent
    /// points first, ranked by value, then the quiet rear, then any picket whose point has left the
    /// ranked list. <paramref name="heldForPlatoons"/> is the floor the pool is never taken below —
    /// what the platoons a threatened purpose is waiting on have first claim to.</para>
    /// </summary>
    private void FillPickets(FactionHQ hq, OperationsState state, int heldForPlatoons)
    {
        float frontRange = CommanderSettings.OperationsFrontRangeMeters;
        for (int pass = 0; pass < 2; pass++)
        {
            bool nearPass = pass == 0;
            for (int r = 0; r < state.RankedPoints.Count; r++)
            {
                CommanderRankedPoint ranked = state.RankedPoints[r];
                if (IsFrontAdjacentPicket(ranked.DistanceToEnemyMeters, frontRange) != nearPass)
                {
                    continue;
                }

                CommanderOperationsMission? mission = FindPicketMission(state, ranked.Point);
                if (mission != null)
                {
                    FillOnePicket(state, mission, heldForPlatoons);
                }
            }
        }

        int shortMissions = 0;
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind != CommanderMissionKind.Picket || mission.Point == null)
            {
                continue;
            }

            // A picket whose point is not in the ranked list at all gets its fill here, last of
            // everything: an unranked point is the least knowable one on the board.
            if (!TryGetRanked(state, mission.Point, out CommanderRankedPoint ranked))
            {
                FillOnePicket(state, mission, heldForPlatoons);
            }

            // Only a picket AWAY from the front withholds the pool from platoon formation. A picket
            // on a front point is a forward base the share cap demoted; the platoon that would form
            // instead is the better answer there, and holding the pool for it would deadlock the two
            // against each other.
            if (mission.PicketMembers.Count < CommanderSettings.PointsMinGarrison && !ranked.IsFront)
            {
                shortMissions++;
            }

            DriveToHoldPosts(hq, mission.Point, mission.PicketMembers);
        }

        state.ShortPicketMissions = shortMissions;
    }

    /// <summary>The picket mission standing on <paramref name="point"/>, or null when no picket is
    /// planned there this review.</summary>
    private static CommanderOperationsMission? FindPicketMission(OperationsState state, CommanderStrategicPoint point)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.Picket && ReferenceEquals(mission.Point, point))
            {
                return mission;
            }
        }

        return null;
    }

    /// <summary>One picket's top-up out of the pool's cheapest vehicles, never taking the pool below
    /// <paramref name="heldForPlatoons"/>.</summary>
    private static void FillOnePicket(OperationsState state, CommanderOperationsMission mission, int heldForPlatoons)
    {
        int wanted = CommanderSettings.PointsMinGarrison;
        while (mission.PicketMembers.Count < wanted && state.Pool.Count > Mathf.Max(0, heldForPlatoons))
        {
            Unit? cheapest = null;
            float cheapestValue = float.MaxValue;
            for (int p = 0; p < state.Pool.Count; p++)
            {
                Unit candidate = state.Pool[p];
                float value = candidate.definition is VehicleDefinition definition ? definition.value : float.MaxValue;
                if (value < cheapestValue)
                {
                    cheapestValue = value;
                    cheapest = candidate;
                }
            }

            if (cheapest == null)
            {
                break;
            }

            mission.PicketMembers.Add(cheapest);
            state.Pool.Remove(cheapest);
        }
    }

    /// <summary>The forward-base share cap at its named boundaries (design SS2), and the pool guard
    /// the pickets-first doctrine turned around (DECISION-013): a short picket comes before a new
    /// platoon, not the other way round.</summary>
    private static void CheckForwardBaseShare(List<string> failures)
    {
        Expect(failures, "a full platoon moves out at once", IsReadyToMoveOut(6, 6, 0f, 180f), true);
        Expect(failures, "a short platoon waits", IsReadyToMoveOut(4, 6, 60f, 180f), false);
        Expect(failures, "a short platoon moves out once it has waited long enough", IsReadyToMoveOut(4, 6, 180f, 180f), true);
        Expect(failures, "one short picket withholds the pool from platoon formation", PicketsNeedThePool(1, 0), true);
        Expect(failures, "several short pickets still withhold the pool", PicketsNeedThePool(4, 0), true);
        Expect(failures, "no short picket releases the pool to platoon formation", PicketsNeedThePool(0, 0), false);
        Expect(failures, "a count that never ran never withholds the pool", PicketsNeedThePool(-1, 0), false);
        Expect(
            failures,
            "a threatened purpose takes the pool back off the pickets",
            PicketsNeedThePool(4, 1),
            false);
        Expect(
            failures,
            "the pickets keep the pool while every purpose is quiet",
            PicketsNeedThePool(4, 0),
            true);
        Expect(
            failures,
            "a quiet map lets the formation step run to its per-review ceiling",
            PlatoonsAheadOfPickets(0, 0, MaxPlatoonsFormedPerReview),
            MaxPlatoonsFormedPerReview);
        Expect(
            failures,
            "short pickets and nothing threatened form no platoon at all",
            PlatoonsAheadOfPickets(3, 0, MaxPlatoonsFormedPerReview),
            0);
        Expect(
            failures,
            "short pickets and two threatened platoons form exactly those two",
            PlatoonsAheadOfPickets(3, 2, MaxPlatoonsFormedPerReview),
            2);
        Expect(
            failures,
            "a threatened purpose never breaks the per-review formation ceiling",
            PlatoonsAheadOfPickets(3, 99, MaxPlatoonsFormedPerReview),
            MaxPlatoonsFormedPerReview);
        Expect(
            failures,
            "a quiet commander holds nothing back from its pickets",
            PoolHeldForThreatenedPurposes(0, 6, MaxPlatoonsFormedPerReview),
            0);
        Expect(
            failures,
            "one threatened platoon holds one platoon's worth back from the pickets",
            PoolHeldForThreatenedPurposes(1, 6, MaxPlatoonsFormedPerReview),
            6);
        Expect(
            failures,
            "the hold-back never exceeds what one review could build",
            PoolHeldForThreatenedPurposes(99, 6, MaxPlatoonsFormedPerReview),
            MaxPlatoonsFormedPerReview * 6);
        Expect(
            failures,
            "a rear point one front range past the line is still front-adjacent",
            IsFrontAdjacentPicket(30000f, 15000f),
            true);
        Expect(
            failures,
            "a point deep in the rear is filled after the front-adjacent ones",
            IsFrontAdjacentPicket(30001f, 15000f),
            false);
        Expect(failures, "two forward bases, an attack and the reserve want four platoons", PlatoonPurposeCount(2, 1, 1), 4);
        Expect(failures, "a quiet map still wants its standing reserve", PlatoonPurposeCount(0, 0, 1), 1);
        Expect(failures, "no purpose anywhere and no reserve wants no platoon at all", PlatoonPurposeCount(0, 0, 0), 0);
        Expect(failures, "a purpose count at the platoon count forms nothing", PlatoonPurposeCount(1, 0, 1) > 2, false);
        Expect(failures, "a purpose count above the platoon count forms one", PlatoonPurposeCount(2, 0, 1) > 2, true);
        Expect(failures, "a negative setting never subtracts from another purpose", PlatoonPurposeCount(2, -5, 1), 3);
        Expect(failures, "half of six platoons may sit in forward bases", MaxForwardBases(6, 0.5f, 0), 3);
        Expect(failures, "a commander with one platoon still wants its two best points", MaxForwardBases(1, 0.5f, 0), 2);
        Expect(failures, "the demand floor holds even at a zero share", MaxForwardBases(6, 0f, 0), MinForwardBases);
        Expect(failures, "a commander with no platoons at all still wants the floor", MaxForwardBases(0, 1f, 0), MinForwardBases);
        Expect(failures, "a full share lets every platoon hold", MaxForwardBases(6, 1f, 0), 6);
        Expect(failures, "the share rounds down, so the reserve is never short", MaxForwardBases(7, 0.5f, 0), 3);
        Expect(failures, "every threatened front point adds a forward base to the floor", MaxForwardBases(1, 0.5f, 4), 6);
        Expect(
            failures,
            "the share never cuts the allowance below the threatened demand",
            MaxForwardBases(6, 0.5f, 4),
            6);

        // The deadlock this track's fix exists for: one platoon on the board and one front point the
        // enemy is standing on must leave MORE purposes than platoons, or the force never grows.
        int threatenedAllowance = MaxForwardBases(1, 0.5f, 1);
        Expect(failures, "one platoon and one threatened front point still allow three forward bases", threatenedAllowance, 3);
        // The threatened base wants two platoons (design SS2); the other two allowed bases want one each.
        Expect(
            failures,
            "one platoon under threat has a purpose for at least three platoons",
            PlatoonPurposeCount(2 + (threatenedAllowance - 1), 0, 1) >= 3,
            true);
    }

    /// <summary>Withdraw and fail thresholds (design SS1/SS3), and the ladder guard: a failed
    /// attack is always also one that should withdraw.</summary>
    private static void CheckStrength(List<string> failures)
    {
        Expect(failures, "exactly half a platoon holds", !ShouldWithdraw(3, 6), true);
        Expect(failures, "a third of a platoon withdraws", ShouldWithdraw(2, 6), true);
        Expect(failures, "a dead platoon withdraws", ShouldWithdraw(0, 6), true);
        Expect(failures, "a full platoon with no armour left withdraws", ShouldWithdraw(6, 6, 0, 3), true);
        Expect(failures, "a full platoon with one tank left holds", !ShouldWithdraw(6, 6, 1, 3), true);
        Expect(failures, "an armour-free recipe never trips the teeth rule", !ShouldWithdraw(6, 6, 0, 0), true);
        Expect(failures, "the teeth rule does not rescue a platoon below half strength", ShouldWithdraw(2, 6, 2, 3), true);
        Expect(failures, "exactly 40% of an attack is still an attack", !AttackHasFailed(4, 10), true);
        Expect(failures, "30% of an attack has failed", AttackHasFailed(3, 10), true);
        Expect(failures, "a full-strength attack has not failed", !AttackHasFailed(6, 6), true);

        bool ladderHolds = true;
        for (int strength = 0; strength <= 10; strength++)
        {
            if (AttackHasFailed(strength, 10) && !ShouldWithdraw(strength, 10))
            {
                ladderHolds = false;
            }
        }

        Expect(failures, "the fail threshold is below the withdraw threshold", ladderHolds, true);
    }

    /// <summary>Front/rear classification and the point-value ordering missions are filled in
    /// (design SS2), at the default 15 km front range.</summary>
    private static void CheckFront(List<string> failures)
    {
        const float frontRange = 15000f;
        Expect(failures, "a point exactly at the front range is front line", IsFrontPoint(15000f, frontRange), true);
        Expect(failures, "a point just inside the front range is front line", IsFrontPoint(14999f, frontRange), true);
        Expect(failures, "a point past the front range is rear", !IsFrontPoint(15001f, frontRange), true);
        Expect(failures, "a point touching the enemy is worth double its income", PointValue(10f, 0f, frontRange), 20f);
        Expect(failures, "a point at the front range is worth its income", PointValue(10f, frontRange, frontRange), 10f);
        Expect(failures, "a deep rear point never drops below its income", PointValue(10f, 30000f, frontRange), 10f);
        Expect(
            failures,
            "a cheap point near the enemy does not automatically outrank an expensive deep rear point",
            PointValue(5f, 1000f, frontRange) > PointValue(10f, 30000f, frontRange),
            false);
        Expect(
            failures,
            "of two points paying the same, the one nearer the enemy is filled first",
            PointValue(10f, 1000f, frontRange) > PointValue(10f, 14000f, frontRange),
            true);
    }
}
