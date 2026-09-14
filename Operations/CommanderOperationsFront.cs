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
        float best = float.MaxValue;
        IReadOnlyList<CommanderStrategicPoint>? points = CommanderStrategicPointService.Instance?.Points;
        if (points == null)
        {
            return best;
        }

        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            FactionHQ? owner = point.GetOwner();
            if (owner == null || ReferenceEquals(owner, hq))
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

    /// <summary>Hold posts per point, built once and kept — a point does not move. Cleared in
    /// <see cref="ResetSession"/> for the reason the garrison step's own clear gave: the point
    /// objects a stale cache keys on do not survive a mission reload.</summary>
    private readonly Dictionary<CommanderStrategicPoint, List<GlobalPosition>> holdPosts = new();

    /// <summary>How far inside a point's own control ring hold posts sit — inside the ring, not
    /// standing on its edge. The value the garrison step used (moved here, Reuse rule 3).</summary>
    private const float HoldRingFraction = 0.6f;

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
    /// Ring posts for a point: <paramref name="slots"/> of them, evenly spaced at
    /// <c>Radius * HoldRingFraction</c>, cached per point (a point does not move). Ledger row 19:
    /// cut out of the points track's original garrison-post builder (Reuse rule 3), which forwarded
    /// here from T7 and was deleted along with the rest of its file in T11.
    /// </summary>
    internal List<GlobalPosition> EnsureHoldPosts(CommanderStrategicPoint point, int slots)
    {
        if (holdPosts.TryGetValue(point, out List<GlobalPosition> posts))
        {
            return posts;
        }

        posts = BuildHoldRing(point.Position, point.Radius * HoldRingFraction, slots);
        holdPosts[point] = posts;
        return posts;
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
    private void DriveMembersToPosts(
        IReadOnlyList<Unit> members, List<GlobalPosition> posts, Dictionary<Unit, GlobalPosition>? issued = null)
    {
        if (posts.Count == 0)
        {
            return;
        }

        holdMembersScratch.Clear();
        holdMembersScratch.AddRange(members);
        holdMembersScratch.Sort(static (a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
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

    private void DriveToHoldPosts(
        CommanderStrategicPoint point, IReadOnlyList<Unit> members, Dictionary<Unit, GlobalPosition>? issued = null)
    {
        DriveMembersToPosts(members, EnsureHoldPosts(point, Mathf.Max(1, members.Count)), issued);
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

                platoon.Mission = mission;
                platoon.State = CommanderPlatoonState.Moving;
                platoon.Objective = mission.Point.Position;
                mission.Assigned.Add(platoon);
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

                platoon.Mission = mission;
                platoon.State = CommanderPlatoonState.Attacking;
                mission.Assigned.Add(platoon);
            }
        }

        // T11: whatever the forward-base share leaves becomes the reserve — the home guard's old
        // job (design SS1: "the home guard becomes the base's reserve platoon(s)").
        CommanderOperationsMission reserve = FindOrCreateReserveMission(state);
        GlobalPosition territoryCenter = CommanderCaptureService.GetTerritoryCenter(hq);
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (IsAvailableForMission(platoon))
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

        // Every full platoon the pool can field forms this review, plus at most one partial one.
        // One per review used to read better in the log, but a hot reload hands the whole roster
        // back as a pool — 69 vehicles on one side — and at one platoon per 30 s that force stood
        // idle for six minutes while the game's own brain pulled at it.
        for (int formed = 0; formed < MaxPlatoonsFormedPerReview && state.Pool.Count > 0; formed++)
        {
            CommanderPlatoon? platoon = TryFormPlatoon(hq, state, CommanderSettings.OperationsPlatoonSize);
            if (platoon == null || platoon.Members.Count < platoon.Establishment)
            {
                break;
            }
        }
    }

    /// <summary>Ceiling on platoons formed in one review, so a reload's 60-vehicle pool is back
    /// under orders inside two reviews while a runaway fill loop can never spin.</summary>
    private const int MaxPlatoonsFormedPerReview = 6;

    /// <summary>At most this share of a commander's platoons sit in forward bases (design SS2); at
    /// least one always holds, so a commander with a single platoon still holds its best point.
    /// Pure, for the self-check.</summary>
    internal static int MaxForwardBases(int platoonCount, float fobShare)
    {
        return Mathf.Max(1, Mathf.FloorToInt(platoonCount * Mathf.Clamp01(fobShare)));
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

    /// <summary>Disbands a forward base's platoon(s) back into the free pool and turns the mission
    /// into a picket (design SS2: "a FOB whose point turns rear thins to a picket, releasing the
    /// surplus members back to the pool"). The now-picket mission claims two of those members back
    /// for itself in <see cref="FillPickets"/>; the rest are free for the next review's reserve or
    /// reinforcement.</summary>
    private static void DemoteForwardBaseToPicket(OperationsState state, CommanderOperationsMission mission)
    {
        for (int i = mission.Assigned.Count - 1; i >= 0; i--)
        {
            CommanderPlatoon platoon = mission.Assigned[i];
            state.Pool.AddRange(platoon.Members);
            platoon.Members.Clear();
            platoon.Mission = null;
            state.Platoons.Remove(platoon);
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

        int cap = MaxForwardBases(Mathf.Max(1, state.Platoons.Count), CommanderSettings.OperationsFobShare);
        int forwardBaseCount = 0;
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind != CommanderMissionKind.ForwardBase)
            {
                continue;
            }

            forwardBaseCount++;
            bool stillFront = !TryGetRanked(state, mission.Point, out CommanderRankedPoint ranked) || ranked.IsFront;
            if (forwardBaseCount > cap || !stillFront)
            {
                DemoteForwardBaseToPicket(state, mission);
            }
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

        FillPickets(state);
    }

    /// <summary>Tops up every picket to <c>PointsMinGarrison</c> with the cheapest free-pool
    /// vehicles by <c>definition.value</c>, then drives them to the point's hold posts.</summary>
    /// <summary>
    /// True while the pool's vehicles are spoken for by platoons: a platoon is still short of its
    /// recipe, or the commander has fewer platoons than its standing reserve. Pickets take from the
    /// pool only when this is false — otherwise every bought vehicle went to hold a rear crossroads
    /// two at a time and the first platoon sat at 3/6 forever. Pure, for the self-check.
    /// </summary>
    internal static bool PlatoonsNeedThePool(int platoonCount, int reservePlatoons, bool anyPlatoonUnderStrength)
    {
        return anyPlatoonUnderStrength || platoonCount < reservePlatoons;
    }

    private void FillPickets(OperationsState state)
    {
        bool underStrength = false;
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (platoon.Members.Count < platoon.Establishment)
            {
                underStrength = true;
                break;
            }
        }

        bool poolReserved = PlatoonsNeedThePool(
            state.Platoons.Count, CommanderSettings.OperationsReservePlatoons, underStrength);

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind != CommanderMissionKind.Picket || mission.Point == null)
            {
                continue;
            }

            int wanted = CommanderSettings.PointsMinGarrison;
            while (!poolReserved && mission.PicketMembers.Count < wanted && state.Pool.Count > 0)
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

            DriveToHoldPosts(mission.Point, mission.PicketMembers);
        }
    }

    /// <summary>The forward-base share cap at its named boundaries (design SS2), and the picket
    /// pool guard: platoons come before pickets until the reserve exists and every platoon is full.</summary>
    private static void CheckForwardBaseShare(List<string> failures)
    {
        Expect(failures, "a full platoon moves out at once", IsReadyToMoveOut(6, 6, 0f, 180f), true);
        Expect(failures, "a short platoon waits", IsReadyToMoveOut(4, 6, 60f, 180f), false);
        Expect(failures, "a short platoon moves out once it has waited long enough", IsReadyToMoveOut(4, 6, 180f, 180f), true);
        Expect(failures, "pickets wait while a platoon is short", PlatoonsNeedThePool(3, 2, true), true);
        Expect(failures, "pickets wait while the reserve is short", PlatoonsNeedThePool(1, 2, false), true);
        Expect(failures, "pickets may fill once platoons are full and the reserve exists", PlatoonsNeedThePool(2, 2, false), false);
        Expect(failures, "half of six platoons may sit in forward bases", MaxForwardBases(6, 0.5f), 3);
        Expect(failures, "a commander with one platoon still holds its best point", MaxForwardBases(1, 0.5f), 1);
        Expect(failures, "the floor holds even at a zero share", MaxForwardBases(6, 0f), 1);
        Expect(failures, "a full share lets every platoon hold", MaxForwardBases(6, 1f), 6);
        Expect(failures, "the share rounds down, so the reserve is never short", MaxForwardBases(7, 0.5f), 3);
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
