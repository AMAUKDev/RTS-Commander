using HarmonyLib;
using System.Reflection;
using UnityEngine;
using System.Collections.Generic;
using NuclearOption.Networking;

namespace GroundControlRts;

/// <summary>Per-unit engagement stance. Applies whether or not the unit holds an order.</summary>
internal enum CommanderStance
{
    /// <summary>Shoots freely and, while travelling, diverts to hostiles that come into range.</summary>
    FreeEngage,
    /// <summary>Turrets acquire nothing and the unit never diverts. Used to stay dark.</summary>
    HoldFire,
    /// <summary>Holds the ground it stands on and ignores travel orders, but still shoots.</summary>
    HoldPosition,
}

/// <summary>Action a unit performs on arrival at a travel point.</summary>
internal enum CommanderWaypointAction
{
    None,
    Hold,
    RadarOff,
    RadarOn,
}

/// <summary>
/// Owns every Commander-issued ground/ship order: multi-point travel routes, patrol loops,
/// attack and guard orders, stances and the stop/resume states. Aircraft orders are forwarded
/// to <see cref="CommanderAirCommandService"/>, which drives the AI pilot instead.
/// </summary>
internal sealed class CommanderMoveService : ICommanderTickPersistent, ICommanderResetSession
{
    private const float GroundFormationSpacingMeters = 25f;
    private const float ShipFormationSpacingMeters = 80f;
    private const float RouteTickIntervalSeconds = 0.25f;
    private const float StuckTimeoutSeconds = 60f;
    private const float GuardArrivalMeters = 120f;
    private const float SpotStaleSeconds = 12f;
    private const float EngagementLeash = 1.6f;
    private const float RetreatTimeoutSeconds = 240f;

    private static readonly MethodInfo? RearmVehicleWaitMethod = AccessTools.Method(typeof(RearmVehicleAI), "Wait");
    private static readonly MethodInfo? RearmVehicleRestockMethod = AccessTools.Method(typeof(RearmVehicleAI), "DriveToRestock");

    private readonly CommanderSelectionService selectionService;
    private readonly HashSet<Unit> stoppedUnits = new();
    private readonly Dictionary<Unit, GlobalPosition> playerDestinations = new();
    private readonly Dictionary<Unit, UnitOrder> orders = new();
    private readonly Dictionary<Unit, CommanderStance> stances = new();
    private readonly List<Unit> staleOrders = new();
    private readonly List<Unit> hostiles = new();
    private readonly List<Unit> staleStances = new();
    private float nextRouteTickAt;
    private int routeTick;

    internal static CommanderMoveService? Instance { get; private set; }

    internal bool HasCommandableSelection
    {
        get
        {
            for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
            {
                if (CommanderGameAccess.ShouldAllowCommanderMove(selectionService.SelectedUnits[i]))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>True while at least one unit holds an attack order, so the turret focus-fire patch can bail out cheaply.</summary>
    internal bool HasAttackOrders { get; private set; }

    /// <summary>True while at least one unit is told to hold fire, so the turret patch can bail out cheaply.</summary>
    internal bool HasHoldFireUnits { get; private set; }

    /// <summary>Action attached to the next travel point the player places.</summary>
    internal CommanderWaypointAction PendingWaypointAction { get; private set; }

    internal CommanderFormationShape Formation
    {
        get => (CommanderFormationShape)Mathf.Clamp(CommanderSettings.FormationShape, 0, 3);
        set => CommanderSettings.FormationShape = (int)value;
    }

    internal CommanderMoveService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
    }

    /// <summary>
    /// Resolves a screen click into a travel point, into an attack order when a hostile unit
    /// sits under the cursor, or into a guard order on a friendly one, and applies it to the
    /// whole selection.
    /// </summary>
    internal void TryIssueMoveOrder(Vector2 screenPosition)
    {
        if (selectionService.SelectedUnits.Count == 0)
        {
            return;
        }

        bool append = CommanderSettings.QueueWaypoint.IsPressed();
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();

        // The world marker is the only thing you can actually hit when the target is a
        // distant contact or an aircraft: its collider is a few pixels wide at command
        // altitude, so a raycast alone made attack orders feel like they did nothing.
        Unit? hovered = CommanderMarkerService.Instance?.TryGetMarkerUnitAt(screenPosition, out Unit markerUnit) == true
            ? markerUnit
            : null;
        if (hovered == null && CommanderGameAccess.TryRaycastHostileUnit(screenPosition, out Unit clickedUnit))
        {
            hovered = clickedUnit;
        }

        Unit? attackTarget = hovered != null && !CommanderGameAccess.IsFriendlyUnit(hovered, localHq)
            ? hovered
            : null;

        if (attackTarget == null
            && CommanderSettings.GuardOrders
            && (hovered ?? (CommanderGameAccess.TryRaycastSelectableUnit(screenPosition, out Unit friendlyUnit) ? friendlyUnit : null)) is Unit guardCandidate
            && CommanderGameAccess.IsFriendlyUnit(guardCandidate, localHq)
            && IssueGuardOrder(guardCandidate))
        {
            return;
        }

        bool hasGroundDestination = CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition groundDestination);
        bool hasWaterDestination = CommanderGameAccess.TryRaycastWaterPosition(screenPosition, out GlobalPosition waterDestination);
        if (attackTarget != null)
        {
            groundDestination = attackTarget.GlobalPosition();
            waterDestination = groundDestination;
            hasGroundDestination = true;
            hasWaterDestination = true;
        }

        GlobalPosition ground = groundDestination;
        GlobalPosition water = waterDestination;
        bool groundValid = hasGroundDestination;
        bool waterValid = hasWaterDestination;
        CommanderCaptureService.CaptureTarget target = default;
        bool capture = attackTarget == null
            && groundValid
            && TryClaimCapturePoint(ref ground, out target);
        if (capture)
        {
            water = ground;
        }

        ApplyOrder(
            unit => unit is Ship ? (waterValid, water) : (groundValid, ground),
            attackTarget,
            append,
            capture ? target : null);
        if (capture)
        {
            CommanderCaptureService.Instance?.AnnounceCaptureOrder(target, selectionService.SelectedUnits[0]);
        }
    }

    /// <summary>Same as the world order, but from a tactical-map coordinate.</summary>
    internal void IssueOrderAt(GlobalPosition point, Unit? attackTarget, bool append)
    {
        if (selectionService.SelectedUnits.Count == 0)
        {
            return;
        }

        GlobalPosition destination = attackTarget != null ? attackTarget.GlobalPosition() : point;
        CommanderCaptureService.CaptureTarget target = default;
        bool capture = attackTarget == null
            && TryClaimCapturePoint(ref destination, out target);
        ApplyOrder(_ => (true, destination), attackTarget, append, capture ? target : null);
        if (capture)
        {
            CommanderCaptureService.Instance?.AnnounceCaptureOrder(target, selectionService.SelectedUnits[0]);
        }
    }

    /// <summary>
    /// An order that lands on a base this faction could take is a capture order: the destination
    /// snaps to the middle of the ring, so the units stop somewhere that actually captures rather
    /// than wherever the cursor was. Both order paths ask, so a capture works the same from the 3D
    /// view and from the map, on its own or as the last of a queued string of travel points.
    /// </summary>
    private static bool TryClaimCapturePoint(
        ref GlobalPosition point, out CommanderCaptureService.CaptureTarget target)
    {
        CommanderCaptureService? capture = CommanderCaptureService.Instance;
        if (capture == null)
        {
            target = default;
            return false;
        }

        return capture.TryResolveCaptureOrder(ref point, out target);
    }

    /// <summary>
    /// An aircraft's half of an order. Three cases, in order: an order that resolved as a capture
    /// puts the aircraft down inside the ring and holds the base; an order dropped on a field the
    /// faction already holds is a rearm run; anything else is an ordinary travel or attack point.
    /// </summary>
    /// <remarks>
    /// The capture case exists because an aircraft handed the ground squad's hold point simply flew
    /// over the airfield and went home — a travel point is a place to be, not a place to land. The
    /// resupply case is the same order read the other way round, which is what the player expects
    /// when they right-click their own base with a fighter selected.
    /// </remarks>
    private void IssueAircraftOrder(
        Aircraft aircraft,
        GlobalPosition point,
        Unit? attackTarget,
        bool append,
        CommanderCaptureService.CaptureTarget? capture)
    {
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null)
        {
            return;
        }

        if (attackTarget == null
            && capture.HasValue
            && capture.Value.Airbase != null
            && airCommand.SetAircraftLandingOrder(
                aircraft,
                capture.Value.Airbase,
                CommanderAirCommandService.LandingIntent.Capture,
                append))
        {
            return;
        }

        if (attackTarget == null
            && !capture.HasValue
            && CommanderCaptureService.TryResolveOwnedAirfield(point, aircraft.NetworkHQ, out Airbase home)
            && airCommand.SetAircraftLandingOrder(
                aircraft,
                home,
                CommanderAirCommandService.LandingIntent.Resupply,
                append))
        {
            return;
        }

        airCommand.SetAircraftOrder(aircraft, point, attackTarget, append);
    }

    /// <summary>Sends every aircraft in the selection to the nearest field its faction holds.</summary>
    internal int ResupplySelectedAircraft()
    {
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null)
        {
            return 0;
        }

        int sent = 0;
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        for (int i = 0; i < selection.Count; i++)
        {
            if (selection[i] is Aircraft aircraft && airCommand.ResupplyAircraft(aircraft))
            {
                sent++;
            }
        }

        return sent;
    }

    /// <summary>True when the selection holds at least one aircraft the commander can task.</summary>
    internal bool HasSelectedAircraft
    {
        get
        {
            IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
            for (int i = 0; i < selection.Count; i++)
            {
                if (selection[i] is Aircraft aircraft && CommanderAirCommandService.IsTaskableAircraft(aircraft))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Escort order: the selection follows <paramref name="guardTarget"/> in formation and
    /// engages whatever shoots at it, using the same re-tracking as an attack order.
    /// </summary>
    internal bool IssueGuardOrder(Unit guardTarget)
    {
        int slot = 0;
        bool issued = false;
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (ReferenceEquals(unit, guardTarget) || !CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            UnitOrder order = new()
            {
                GuardTarget = guardTarget,
                FormationSlot = ++slot,
                Spacing = unit is Ship ? ShipFormationSpacingMeters : GroundFormationSpacingMeters,
                Shape = Formation == CommanderFormationShape.Ring ? CommanderFormationShape.Wedge : Formation,
            };
            orders[unit] = order;
            stoppedUnits.Remove(unit);
            playerDestinations.Remove(unit);
            CommanderGameAccess.SetUnitHoldPosition(unit, false);
            issued = true;
        }

        if (issued)
        {
            CommanderOrderPing.Show(guardTarget.GlobalPosition(), false);
            RefreshOrderFlags();
            AdvanceRoutes(force: true);
        }
        return issued;
    }

    private void ApplyOrder(
        System.Func<Unit, (bool valid, GlobalPosition point)> resolvePoint,
        Unit? attackTarget,
        bool append,
        CommanderCaptureService.CaptureTarget? capture = null)
    {
        if (selectionService.SelectedUnits.Count > 0)
        {
            (bool valid, GlobalPosition point) preview = resolvePoint(selectionService.SelectedUnits[0]);
            if (preview.valid)
            {
                CommanderOrderPing.Show(preview.point, attackTarget != null);
            }
        }

        CommanderWaypointAction action = PendingWaypointAction;
        PendingWaypointAction = CommanderWaypointAction.None;
        CommanderFormationShape shape = Formation;
        float heading = ResolveHeading(resolvePoint);
        OrderGroup group = new();
        int groundSlot = 0;
        int shipSlot = 0;
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (unit is Aircraft aircraft)
            {
                IssueAircraftOrder(aircraft, resolvePoint(unit).point, attackTarget, append, capture);
                continue;
            }

            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            (bool valid, GlobalPosition point) = resolvePoint(unit);
            if (!valid)
            {
                continue;
            }

            bool ship = unit is Ship;
            int slot = ship ? shipSlot++ : groundSlot++;
            float spacing = ship ? ShipFormationSpacingMeters : GroundFormationSpacingMeters;

            if (!append || !orders.TryGetValue(unit, out UnitOrder order))
            {
                order = new UnitOrder();
                orders[unit] = order;
            }

            order.FormationSlot = slot;
            order.Spacing = spacing;
            order.Shape = shape;
            order.Heading = heading;
            order.Group = group;
            order.AttackTarget = attackTarget;
            order.GuardTarget = null;
            order.EngagedTarget = null;
            order.Retreating = false;
            order.Waypoints.Add(point);
            order.Actions.Add(action);
            order.ResetProgress();

            stoppedUnits.Remove(unit);
            playerDestinations.Remove(unit);
            CommanderGameAccess.SetUnitHoldPosition(unit, false);
        }

        RefreshOrderFlags();
        AdvanceRoutes(force: true);
    }

    /// <summary>Direction the group travels in, so line, wedge and column face the right way.</summary>
    private float ResolveHeading(System.Func<Unit, (bool valid, GlobalPosition point)> resolvePoint)
    {
        Vector3 centre = Vector3.zero;
        int count = 0;
        Vector3 destination = Vector3.zero;
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            (bool valid, GlobalPosition point) = resolvePoint(unit);
            if (!valid)
            {
                continue;
            }

            centre += unit.transform.position;
            destination = point.ToLocalPosition();
            count++;
        }

        if (count == 0)
        {
            return 0f;
        }

        Vector3 travel = destination - centre / count;
        travel.y = 0f;
        return travel.sqrMagnitude < 1f ? 0f : Quaternion.LookRotation(travel).eulerAngles.y;
    }

    public void TickPersistent()
    {
        stoppedUnits.RemoveWhere(static unit => unit == null || unit.disabled);
        List<Unit>? staleDestinations = null;
        foreach (KeyValuePair<Unit, GlobalPosition> entry in playerDestinations)
        {
            if (entry.Key != null
                && !entry.Key.disabled
                && CommanderGameAccess.HorizontalDistance(entry.Key.transform.position, entry.Value.ToLocalPosition()) > 25f)
            {
                continue;
            }

            staleDestinations ??= new List<Unit>();
            staleDestinations.Add(entry.Key!);
        }
        if (staleDestinations != null)
        {
            for (int i = 0; i < staleDestinations.Count; i++)
            {
                playerDestinations.Remove(staleDestinations[i]);
            }
        }

        AdvanceRoutes(force: false);

        if (stoppedUnits.Count == 0)
        {
            return;
        }
        foreach (Unit unit in stoppedUnits)
        {
            if (unit.rb == null)
            {
                continue;
            }

            unit.rb.velocity = Vector3.zero;
            unit.rb.angularVelocity = Vector3.zero;
        }
    }

    private void AdvanceRoutes(bool force)
    {
        if (!force && !CommanderScheduler.IsDue(ref nextRouteTickAt, RouteTickIntervalSeconds))
        {
            return;
        }

        PruneStances();
        if (orders.Count == 0)
        {
            return;
        }

        routeTick++;
        RefreshHostiles();
        UpdateGroupCohesion();

        staleOrders.Clear();
        bool targetsChanged = false;
        foreach (KeyValuePair<Unit, UnitOrder> entry in orders)
        {
            Unit unit = entry.Key;
            UnitOrder order = entry.Value;
            if (unit == null || unit.disabled || stoppedUnits.Contains(unit))
            {
                staleOrders.Add(unit!);
                continue;
            }

            if (GetStance(unit) == CommanderStance.HoldPosition)
            {
                HoldInPlace(unit);
                staleOrders.Add(unit);
                continue;
            }

            if (order.AttackTarget != null && order.AttackTarget.disabled)
            {
                order.AttackTarget = CommanderSettings.RetargetAfterKill ? FindTargetInRange(unit, 0f) : null;
                order.ResetProgress();
                targetsChanged = true;
                if (order.AttackTarget == null && order.Index >= order.Waypoints.Count && order.GuardTarget == null)
                {
                    // Target destroyed and nothing else in reach: hold the ground that was just
                    // taken instead of letting Basegame tasking drive the unit back out of it.
                    HoldInPlace(unit);
                    staleOrders.Add(unit);
                    continue;
                }
            }

            if (order.Retreating)
            {
                if (RunRetreat(unit, order))
                {
                    continue;
                }
                targetsChanged = true;
            }
            else if (ShouldRetreat(unit, order) && BeginRetreat(unit, order))
            {
                continue;
            }

            if (order.GuardTarget != null)
            {
                if (order.GuardTarget.disabled)
                {
                    order.GuardTarget = null;
                    staleOrders.Add(unit);
                    continue;
                }

                RunGuard(unit, order);
                continue;
            }

            if (RunOpportunityEngagement(unit, order, ref targetsChanged))
            {
                continue;
            }

            if (order.Index < order.Waypoints.Count)
            {
                if (Time.unscaledTime < order.HoldUntil)
                {
                    HoldQuietly(unit, order);
                    continue;
                }

                GlobalPosition waypoint = CommanderDestinationFormation.ApplyOffset(
                    order.Waypoints[order.Index], order.FormationSlot, order.Spacing, order.Shape, order.Heading);
                float distance = CommanderGameAccess.HorizontalDistance(
                    unit.transform.position, waypoint.ToLocalPosition());
                if (distance <= ArrivalRadius(unit) || order.IsStuck(distance))
                {
                    RunWaypointAction(unit, order);
                    order.Index++;
                    order.ResetProgress();
                    if (order.Index >= order.Waypoints.Count && order.Loop && order.Waypoints.Count > 1)
                    {
                        // Patrol: the route restarts instead of expiring, so it keeps
                        // running while the player is away flying.
                        order.Index = 0;
                    }

                    if (order.Index < order.Waypoints.Count)
                    {
                        // Route continues. Falling through to the attack-target test below is
                        // what used to drop the order the moment a unit reached its first
                        // travel point, which stranded every multi-point route.
                        continue;
                    }

                    if (order.AttackTarget == null)
                    {
                        staleOrders.Add(unit);
                        continue;
                    }
                }
                else
                {
                    if (order.Group.ShouldWait(order.Remaining))
                    {
                        // Arrive together: anything well ahead of the rearmost unit waits
                        // instead of letting the group string out along the route.
                        HoldQuietly(unit, order);
                        continue;
                    }

                    Issue(unit, order, waypoint);
                    continue;
                }
            }

            if (order.AttackTarget == null)
            {
                staleOrders.Add(unit);
                continue;
            }

            Issue(unit, order, GetAttackStandoff(unit, order.AttackTarget));
        }

        for (int i = 0; i < staleOrders.Count; i++)
        {
            orders.Remove(staleOrders[i]);
        }
        if (staleOrders.Count > 0 || targetsChanged)
        {
            RefreshOrderFlags();
        }
    }

    /// <summary>Records how far each unit still has to travel and the worst case per order group.</summary>
    private void UpdateGroupCohesion()
    {
        foreach (KeyValuePair<Unit, UnitOrder> entry in orders)
        {
            Unit unit = entry.Key;
            UnitOrder order = entry.Value;
            if (unit == null || unit.disabled || order.Index >= order.Waypoints.Count)
            {
                order.Remaining = 0f;
                continue;
            }

            GlobalPosition waypoint = CommanderDestinationFormation.ApplyOffset(
                order.Waypoints[order.Index], order.FormationSlot, order.Spacing, order.Shape, order.Heading);
            order.Remaining = CommanderGameAccess.HorizontalDistance(
                unit.transform.position, waypoint.ToLocalPosition());
            order.Group.Accumulate(routeTick, order.Remaining);
        }
    }

    private void RefreshHostiles()
    {
        hostiles.Clear();
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return;
        }

        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            if (Time.timeSinceLevelLoad - entry.Value.lastSpottedTime > SpotStaleSeconds
                || !entry.Key.TryGetUnit(out Unit candidate)
                || candidate == null
                || candidate.disabled
                || candidate.NetworkHQ == null
                || CommanderGameAccess.IsFriendlyUnit(candidate, hq))
            {
                continue;
            }

            hostiles.Add(candidate);
        }
    }

    /// <summary>Nearest tracked hostile inside <paramref name="range"/>; 0 uses the unit's own weapon range.</summary>
    private Unit? FindTargetInRange(Unit unit, float range)
    {
        if (range <= 0f)
        {
            range = unit.GetMaxRange();
            if (range <= 0f)
            {
                range = 2000f;
            }
        }

        Unit? best = null;
        float bestDistance = range;
        for (int i = 0; i < hostiles.Count; i++)
        {
            Unit candidate = hostiles[i];
            if (candidate == null || candidate.disabled)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                unit.transform.position, candidate.transform.position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Attack-move. A unit travelling on Free Engage diverts to a hostile that comes inside its
    /// weapon range and resumes the route once the target is dead or has broken contact.
    /// </summary>
    private bool RunOpportunityEngagement(Unit unit, UnitOrder order, ref bool targetsChanged)
    {
        if (order.EngagedTarget != null)
        {
            float leash = Mathf.Max(unit.GetMaxRange(), 500f) * EngagementLeash;
            if (order.EngagedTarget.disabled
                || CommanderGameAccess.HorizontalDistance(
                    unit.transform.position, order.EngagedTarget.transform.position) > leash)
            {
                order.EngagedTarget = null;
                order.ResetProgress();
                targetsChanged = true;
                return false;
            }

            Issue(unit, order, GetAttackStandoff(unit, order.EngagedTarget));
            return true;
        }

        if (!CommanderSettings.AttackMoveRoutes
            || order.AttackTarget != null
            || order.Index >= order.Waypoints.Count
            || GetStance(unit) != CommanderStance.FreeEngage)
        {
            return false;
        }

        Unit? threat = FindTargetInRange(unit, 0f);
        if (threat == null)
        {
            return false;
        }

        order.EngagedTarget = threat;
        order.ResetProgress();
        targetsChanged = true;
        Issue(unit, order, GetAttackStandoff(unit, threat));
        return true;
    }

    /// <summary>Escort: sit in formation on the guarded unit and shoot back on its behalf.</summary>
    private void RunGuard(Unit unit, UnitOrder order)
    {
        Unit guarded = order.GuardTarget!;
        Unit? threat = order.EngagedTarget;
        if (threat != null && threat.disabled)
        {
            threat = null;
        }

        if (threat == null && GetStance(unit) != CommanderStance.HoldFire)
        {
            threat = CommanderAlertService.GetRecentAttacker(guarded) ?? CommanderAlertService.GetRecentAttacker(unit);
            if (threat != null
                && CommanderGameAccess.HorizontalDistance(
                    unit.transform.position, threat.transform.position) > Mathf.Max(unit.GetMaxRange(), 500f) * EngagementLeash)
            {
                threat = null;
            }
        }

        order.EngagedTarget = threat;
        if (threat != null)
        {
            Issue(unit, order, GetAttackStandoff(unit, threat));
            return;
        }

        float heading = guarded.transform.eulerAngles.y;
        GlobalPosition station = CommanderDestinationFormation.ApplyOffset(
            guarded.GlobalPosition(), order.FormationSlot, order.Spacing, order.Shape, heading);
        if (CommanderGameAccess.HorizontalDistance(unit.transform.position, station.ToLocalPosition())
            <= Mathf.Max(GuardArrivalMeters, ArrivalRadius(unit)))
        {
            return;
        }

        order.Heading = heading;
        Issue(unit, order, station);
    }

    private static bool ShouldRetreat(Unit unit, UnitOrder order)
    {
        return CommanderSettings.AutoRetreatDamaged
            && order.GuardTarget == null
            && CommanderGameAccess.GetUnitCondition(unit) <= CommanderSettings.RetreatConditionPercent * 0.01f;
    }

    /// <summary>Sends the unit to the nearest friendly repair or rearm source it can reach.</summary>
    private bool BeginRetreat(Unit unit, UnitOrder order)
    {
        Unit? support = FindNearestSupportUnit(unit);
        if (support == null)
        {
            return false;
        }

        order.Retreating = true;
        order.RetreatTarget = support;
        order.RetreatStartedAt = Time.unscaledTime;
        order.AttackTarget = null;
        order.EngagedTarget = null;
        order.ResetProgress();
        Issue(unit, order, support.GlobalPosition());
        return true;
    }

    /// <summary>True while the retreat is still running.</summary>
    private bool RunRetreat(Unit unit, UnitOrder order)
    {
        Unit? support = order.RetreatTarget;
        if (support == null || support.disabled)
        {
            support = FindNearestSupportUnit(unit);
            order.RetreatTarget = support;
        }

        // Only Building is IRepairable in the Basegame, so a vehicle's condition never climbs
        // back. The retreat therefore ends on arrival plus a restock, or on a timeout, rather
        // than on the health bar recovering.
        float ammo = CommanderGameAccess.GetUnitAmmo(unit);
        bool recovered = CommanderGameAccess.GetUnitCondition(unit)
            >= (CommanderSettings.RetreatConditionPercent + 25f) * 0.01f;
        bool arrived = support != null
            && CommanderGameAccess.HorizontalDistance(unit.transform.position, support.transform.position) <= 200f;
        bool restocked = ammo < 0f || ammo >= 0.95f;
        if (support == null
            || recovered
            || (arrived && restocked)
            || Time.unscaledTime - order.RetreatStartedAt > RetreatTimeoutSeconds)
        {
            order.Retreating = false;
            order.RetreatTarget = null;
            order.ResetProgress();
            return false;
        }

        Issue(unit, order, support.GlobalPosition());
        return true;
    }

    /// <summary>Nearest friendly unit that can repair or rearm this one.</summary>
    private static Unit? FindNearestSupportUnit(Unit unit)
    {
        FactionHQ? hq = unit.NetworkHQ ?? CommanderGameAccess.GetLocalHq();
        if (hq?.factionUnits == null)
        {
            return null;
        }

        Unit? best = null;
        float bestDistance = float.MaxValue;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit candidate)
                || candidate == null
                || candidate.disabled
                || ReferenceEquals(candidate, unit)
                || (candidate.GetComponentInChildren<Repairer>(true) == null
                    && candidate.GetComponentInChildren<Rearmer>(true) == null))
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                unit.transform.position, candidate.transform.position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    private void RunWaypointAction(Unit unit, UnitOrder order)
    {
        switch (order.ActionAt(order.Index))
        {
            case CommanderWaypointAction.Hold:
                order.HoldUntil = Time.unscaledTime + CommanderSettings.WaypointHoldSeconds;
                break;
            case CommanderWaypointAction.RadarOff:
                CommanderRadarService.Instance?.SetUnitRadar(unit, false);
                break;
            case CommanderWaypointAction.RadarOn:
                CommanderRadarService.Instance?.SetUnitRadar(unit, true);
                break;
        }
    }

    /// <summary>Parks the unit without cancelling its order, for waypoint holds and cohesion waits.</summary>
    private static void HoldQuietly(Unit unit, UnitOrder order)
    {
        Issue(unit, order, unit.transform.GlobalPosition());
    }

    private void HoldInPlace(Unit unit)
    {
        CommanderGameAccess.SetUnitHoldPosition(unit, true);
        CommanderGameAccess.GetUnitCommand(unit)?.SetDestination(unit.transform.GlobalPosition(), false);
        stoppedUnits.Add(unit);
        playerDestinations.Remove(unit);
    }

    private static void Issue(Unit unit, UnitOrder order, GlobalPosition destination)
    {
        // Re-issuing every tick would spam the networked command RPC, so only send when the
        // wanted destination actually moved (route advance, or a target that is driving away).
        if (order.HasIssuedDestination
            && CommanderGameAccess.ApproximatelyEqual(order.IssuedDestination, destination, 40f))
        {
            return;
        }

        order.IssuedDestination = destination;
        order.HasIssuedDestination = true;
        CommanderGameAccess.GetUnitCommand(unit)?.SetDestination(destination, true);
    }

    /// <summary>
    /// Stops just short of the target so the unit's own fire control can engage instead of
    /// driving on top of it.
    /// </summary>
    private static GlobalPosition GetAttackStandoff(Unit unit, Unit target)
    {
        GlobalPosition targetPosition = target.GlobalPosition();
        if (!CommanderSettings.AttackMoveIntoRange)
        {
            return targetPosition;
        }

        float standoff = unit.GetMaxRange() > 0f
            ? Mathf.Clamp(unit.GetMaxRange() * 0.7f, 150f, 6000f)
            : 150f;
        Vector3 offset = unit.GlobalPosition() - targetPosition;
        offset.y = 0f;
        if (offset.sqrMagnitude < 1f || offset.magnitude <= standoff)
        {
            // Already inside the engagement envelope; hold this spot instead of closing further.
            return unit.GlobalPosition();
        }

        return targetPosition + offset.normalized * standoff;
    }

    private static float ArrivalRadius(Unit unit)
    {
        return unit is Ship
            ? Mathf.Max(150f, unit.maxRadius * 4f)
            : Mathf.Max(45f, unit.maxRadius * 4f);
    }

    private void RefreshOrderFlags()
    {
        HasAttackOrders = false;
        foreach (UnitOrder order in orders.Values)
        {
            if (order.EffectiveTarget != null)
            {
                HasAttackOrders = true;
                break;
            }
        }

        HasHoldFireUnits = false;
        foreach (CommanderStance stance in stances.Values)
        {
            if (stance == CommanderStance.HoldFire)
            {
                HasHoldFireUnits = true;
                break;
            }
        }
    }

    /// <summary>True while the unit is held in place by a STOP order.</summary>
    internal bool IsStopped(Unit unit) => stoppedUnits.Contains(unit);

    internal CommanderStance GetStance(Unit? unit)
    {
        return unit != null && stances.TryGetValue(unit, out CommanderStance stance)
            ? stance
            : CommanderStance.FreeEngage;
    }

    /// <summary>Stance of the focused unit, used to label the selection-bar button.</summary>
    internal CommanderStance SelectionStance => GetStance(selectionService.FocusedSelection);

    internal void SetSelectionStance(CommanderStance stance)
    {
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            if (stance == CommanderStance.FreeEngage)
            {
                stances.Remove(unit);
            }
            else
            {
                stances[unit] = stance;
            }

            if (stance == CommanderStance.HoldPosition)
            {
                orders.Remove(unit);
                HoldInPlace(unit);
            }
            else
            {
                stoppedUnits.Remove(unit);
                CommanderGameAccess.SetUnitHoldPosition(unit, false);
            }
        }

        RefreshOrderFlags();
    }

    internal void CycleSelectionStance()
    {
        SetSelectionStance(SelectionStance switch
        {
            CommanderStance.FreeEngage => CommanderStance.HoldFire,
            CommanderStance.HoldFire => CommanderStance.HoldPosition,
            _ => CommanderStance.FreeEngage,
        });
    }

    internal void CycleFormation()
    {
        Formation = Formation switch
        {
            CommanderFormationShape.Ring => CommanderFormationShape.Line,
            CommanderFormationShape.Line => CommanderFormationShape.Column,
            CommanderFormationShape.Column => CommanderFormationShape.Wedge,
            _ => CommanderFormationShape.Ring,
        };
    }

    internal void CyclePendingWaypointAction()
    {
        PendingWaypointAction = PendingWaypointAction switch
        {
            CommanderWaypointAction.None => CommanderWaypointAction.Hold,
            CommanderWaypointAction.Hold => CommanderWaypointAction.RadarOff,
            CommanderWaypointAction.RadarOff => CommanderWaypointAction.RadarOn,
            _ => CommanderWaypointAction.None,
        };
    }

    /// <summary>Turns the selection's routes into patrols, or back into one-shot routes.</summary>
    internal void ToggleSelectionPatrol()
    {
        bool enable = !IsSelectionPatrolling;
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            if (orders.TryGetValue(selectionService.SelectedUnits[i], out UnitOrder order)
                && order.Waypoints.Count > 1)
            {
                order.Loop = enable;
            }
        }
    }

    internal bool IsSelectionPatrolling
    {
        get
        {
            for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
            {
                if (orders.TryGetValue(selectionService.SelectedUnits[i], out UnitOrder order) && order.Loop)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>True when at least one selected unit has a route long enough to patrol.</summary>
    internal bool CanSelectionPatrol
    {
        get
        {
            for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
            {
                if (orders.TryGetValue(selectionService.SelectedUnits[i], out UnitOrder order)
                    && order.Waypoints.Count > 1)
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal void RetreatSelectedUnits()
    {
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            if (!orders.TryGetValue(unit, out UnitOrder order))
            {
                order = new UnitOrder();
                orders[unit] = order;
            }

            order.Waypoints.Clear();
            order.Actions.Clear();
            order.Index = 0;
            order.Loop = false;
            order.GuardTarget = null;
            stoppedUnits.Remove(unit);
            CommanderGameAccess.SetUnitHoldPosition(unit, false);
            if (!BeginRetreat(unit, order))
            {
                orders.Remove(unit);
            }
        }

        RefreshOrderFlags();
    }

    private void PruneStances()
    {
        if (stances.Count == 0)
        {
            return;
        }

        staleStances.Clear();
        foreach (KeyValuePair<Unit, CommanderStance> entry in stances)
        {
            if (entry.Key == null || entry.Key.disabled)
            {
                staleStances.Add(entry.Key!);
                continue;
            }

            if (entry.Value == CommanderStance.HoldPosition && !stoppedUnits.Contains(entry.Key))
            {
                // Hold Position is sticky: reassert it if anything else moved the unit.
                HoldInPlace(entry.Key);
            }
        }

        for (int i = 0; i < staleStances.Count; i++)
        {
            stances.Remove(staleStances[i]);
        }
        if (staleStances.Count > 0)
        {
            RefreshOrderFlags();
        }
    }

    /// <summary>True when the unit was told to hold fire, so its turrets acquire nothing.</summary>
    internal static bool IsHoldingFire(Unit? unit)
    {
        return unit != null
            && Instance != null
            && Instance.stances.TryGetValue(unit, out CommanderStance stance)
            && stance == CommanderStance.HoldFire;
    }

    /// <summary>True when <paramref name="candidate"/> is the unit that <paramref name="shooter"/> was ordered to attack.</summary>
    internal static bool IsOrderedTarget(Unit? shooter, Unit? candidate)
    {
        return shooter != null
            && candidate != null
            && Instance != null
            && Instance.orders.TryGetValue(shooter, out UnitOrder order)
            && ReferenceEquals(order.EffectiveTarget, candidate);
    }

    internal bool TryGetOrder(Unit unit, out IReadOnlyList<GlobalPosition> waypoints, out int index, out Unit? attackTarget)
    {
        if (orders.TryGetValue(unit, out UnitOrder order))
        {
            waypoints = order.Waypoints;
            index = order.Index;
            attackTarget = order.EffectiveTarget;
            return true;
        }

        waypoints = System.Array.Empty<GlobalPosition>();
        index = 0;
        attackTarget = null;
        return false;
    }

    /// <summary>One-line order summary for the unit cards.</summary>
    internal string GetOrderLabel(Unit? unit)
    {
        if (unit == null)
        {
            return string.Empty;
        }

        if (!orders.TryGetValue(unit, out UnitOrder order))
        {
            return stoppedUnits.Contains(unit) ? "HOLDING" : string.Empty;
        }

        if (order.Retreating)
        {
            return "RETREATING";
        }
        if (order.GuardTarget != null)
        {
            return $"GUARDING {CommanderGameAccess.GetUnitLabel(order.GuardTarget)}";
        }
        if (order.EffectiveTarget != null)
        {
            return $"ATTACKING {CommanderGameAccess.GetUnitLabel(order.EffectiveTarget)}";
        }
        if (order.Waypoints.Count == 0)
        {
            return "HOLDING";
        }
        return order.Loop
            ? $"PATROL {Mathf.Min(order.Index + 1, order.Waypoints.Count)}/{order.Waypoints.Count}"
            : $"MOVING {Mathf.Min(order.Index + 1, order.Waypoints.Count)}/{order.Waypoints.Count}";
    }

    internal void ClearOrder(Unit unit)
    {
        if (orders.Remove(unit))
        {
            RefreshOrderFlags();
        }
    }

    public void ResetSession()
    {
        orders.Clear();
        stances.Clear();
        stoppedUnits.Clear();
        playerDestinations.Clear();
        hostiles.Clear();
        CommanderOrderPing.Clear();
        PendingWaypointAction = CommanderWaypointAction.None;
        HasAttackOrders = false;
        HasHoldFireUnits = false;
    }

    internal void StopSelectedUnits()
    {
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (unit is Aircraft aircraft)
            {
                CommanderAirCommandService.Instance?.ClearAircraftRoute(aircraft);
                continue;
            }

            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            HoldInPlace(unit);
            orders.Remove(unit);
        }
        RefreshOrderFlags();
    }

    internal void ResumeAiForSelectedUnits()
    {
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            CommanderGameAccess.SetUnitHoldPosition(unit, false);
            stoppedUnits.Remove(unit);
            playerDestinations.Remove(unit);
            orders.Remove(unit);
            stances.Remove(unit);
            if (TryReturnToBasegameLogistics(unit))
            {
                continue;
            }
            if (MissionPosition.TryGetClosestPosition(unit, out GlobalPosition destination))
            {
                CommanderGameAccess.GetUnitCommand(unit)?.SetDestination(destination, false);
            }
        }
        RefreshOrderFlags();
    }

    private static bool TryReturnToBasegameLogistics(Unit unit)
    {
        if (!unit.TryGetComponent(out RearmVehicleAI rearmAi)
            || !unit.TryGetComponent(out Rearmer rearmer))
        {
            return false;
        }

        RearmMissionController? controller = unit.NetworkHQ?.RearmMissionController;
        if (controller != null)
        {
            for (int i = controller.Missions.Count - 1; i >= 0; i--)
            {
                RearmMissionController.RearmMission mission = controller.Missions[i];
                if (ReferenceEquals(mission.Rearmer, rearmer))
                {
                    mission.AssignRearmer(null);
                }
            }
        }

        rearmAi.AssignMission(null!);
        bool needsRestock = rearmer.GetMaxCapacity() > 0f
            && rearmer.Capacity < rearmer.GetMaxCapacity() * 0.5f;
        MethodInfo? stateMethod = needsRestock ? RearmVehicleRestockMethod : RearmVehicleWaitMethod;
        try
        {
            stateMethod?.Invoke(rearmAi, null);
        }
        catch (System.Exception exception)
        {
            CommanderPlugin.Log.LogWarning($"Failed to return {unit.unitName} to Basegame rearm AI: {exception.Message}");
            if (unit is GroundVehicle vehicle)
            {
                vehicle.StopImmediately();
            }
            rearmer.AvailableForMission = !needsRestock;
        }
        return true;
    }

    internal bool TryGetPlayerDestination(Unit unit, out GlobalPosition destination)
    {
        return playerDestinations.TryGetValue(unit, out destination);
    }

    internal static void NotifyPlayerDestination(UnitCommand command, GlobalPosition destination, Player? player)
    {
        if (player == null || Instance == null)
        {
            return;
        }

        Unit? unit = command.GetComponent<Unit>();
        if (unit == null || !CommanderGameAccess.ShouldAllowCommanderMove(unit))
        {
            return;
        }

        Instance.stoppedUnits.Remove(unit);
        Instance.playerDestinations[unit] = destination;
    }

    /// <summary>Shared by every unit ordered in one click, so the group can wait for stragglers.</summary>
    private sealed class OrderGroup
    {
        private int tick = -1;
        private float pending;
        private float laggardDistance;

        internal void Accumulate(int currentTick, float remaining)
        {
            if (tick != currentTick)
            {
                tick = currentTick;
                laggardDistance = pending;
                pending = 0f;
            }

            if (remaining > pending)
            {
                pending = remaining;
            }
        }

        /// <summary>True when this unit is far enough ahead of the rearmost member to wait for it.</summary>
        internal bool ShouldWait(float remaining)
        {
            float tolerance = CommanderSettings.FormationCohesionMeters;
            return tolerance > 0f && laggardDistance - remaining > tolerance;
        }
    }

    private sealed class UnitOrder
    {
        internal readonly List<GlobalPosition> Waypoints = new();
        internal readonly List<CommanderWaypointAction> Actions = new();
        internal int Index;
        internal bool Loop;
        internal Unit? AttackTarget;
        internal Unit? EngagedTarget;
        internal Unit? GuardTarget;
        internal Unit? RetreatTarget;
        internal bool Retreating;
        internal float RetreatStartedAt;
        internal int FormationSlot;
        internal float Spacing;
        internal CommanderFormationShape Shape;
        internal float Heading;
        internal float HoldUntil;
        internal float Remaining;
        internal OrderGroup Group = new();
        internal GlobalPosition IssuedDestination;
        internal bool HasIssuedDestination;

        private float bestDistance = float.MaxValue;
        private float lastProgressAt;

        /// <summary>A commanded attack target outranks a target picked up while travelling.</summary>
        internal Unit? EffectiveTarget => AttackTarget ?? EngagedTarget;

        internal CommanderWaypointAction ActionAt(int index)
        {
            return index >= 0 && index < Actions.Count ? Actions[index] : CommanderWaypointAction.None;
        }

        internal void ResetProgress()
        {
            bestDistance = float.MaxValue;
            lastProgressAt = Time.unscaledTime;
            HasIssuedDestination = false;
        }

        /// <summary>Lets a blocked unit skip a waypoint instead of parking on the route forever.</summary>
        internal bool IsStuck(float distance)
        {
            if (distance < bestDistance - 5f)
            {
                bestDistance = distance;
                lastProgressAt = Time.unscaledTime;
                return false;
            }

            return Time.unscaledTime - lastProgressAt > StuckTimeoutSeconds;
        }
    }
}
