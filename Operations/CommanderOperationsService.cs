using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Claims every ground vehicle an AI-commanded HQ owns into named six-vehicle platoons, sites them
/// as forward bases on the points nearest the enemy (with a munitions truck) and two-vehicle
/// pickets on the quiet points behind, and launches two- or three-axis offensives sized to what the
/// commander has actually tracked — replacing "buy a vehicle, the game's convoy brain drives it at
/// the nearest enemy" with a front line the commander forms, holds and pushes (design.md,
/// <c>platoon-operations_20260913</c>).
/// </summary>
/// <remarks>
/// ponytail: nothing here persists across a mission reload — a platoon roster, a mission's state
/// and the pressure clock are all rebuilt from the live vehicle roster the next time discovery
/// finishes, the same stance <see cref="CommanderStrategicPointService"/> takes on point ownership.
/// No player platoon tools, no truck money-on-arrival, no air or naval tasking: all later tracks
/// (design.md "Out of scope").
/// </remarks>
internal sealed partial class CommanderOperationsService : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>Mission re-planning cadence — matches the buy review
    /// (<c>Ai/CommanderEnemyCommanderService.cs:30</c>) so the buyer's order book is never a review
    /// behind the missions that filled it.</summary>
    private const float ReviewIntervalSeconds = 30f;

    /// <summary>Destination re-issue cadence. Fast enough that a platoon that loses its leader is
    /// re-formed inside one hold tick; slow enough that it is one networked RPC per vehicle per 5 s
    /// at worst.</summary>
    private const float MovementIntervalSeconds = 5f;

    /// <summary>
    /// Platoon display names, <c>"1ST PLATOON"</c> style. Design SS1 gives the format and no
    /// ceiling; twelve is <c>MaxPlatoonsPerAttack</c> (T12) twice over, and past the twelfth a
    /// commander wraps back to the first name with a numeric suffix (<see cref="TryFormPlatoon"/>) —
    /// planner-chosen.
    /// </summary>
    private static readonly string[] PlatoonNames =
    {
        "1ST", "2ND", "3RD", "4TH", "5TH", "6TH", "7TH", "8TH", "9TH", "10TH", "11TH", "12TH",
    };

    private readonly Dictionary<FactionHQ, OperationsState> states = new();

    /// <summary>Scratch for <see cref="BuildRecipeArrays"/>, reused across every platoon formed or
    /// reinforced in a review rather than allocated per platoon.</summary>
    private readonly List<CommanderPlatoonRole> recipeRoles = new();
    private readonly List<bool> recipePrefers = new();
    private readonly List<int> recipePicks = new();

    private float nextReviewAt = CommanderScheduler.Stagger("operations.review", ReviewIntervalSeconds);
    private float nextMovementAt = CommanderScheduler.Stagger("operations.movement", MovementIntervalSeconds);

    internal static CommanderOperationsService? Instance { get; private set; }

    internal CommanderOperationsService()
    {
        Instance = this;
    }

    /// <summary>Per-managed-HQ state: the vehicle pool, the platoons formed from it, the live
    /// missions and the pressure clock that eventually forces an attack.</summary>
    private sealed class OperationsState
    {
        /// <summary>Every claimed vehicle not yet, or no longer, a platoon member.</summary>
        internal readonly List<Unit> Pool = new();

        /// <summary>
        /// The staging post each pooled vehicle has been sent to, the same bookkeeping
        /// <c>CommanderPlatoon.Issued</c> keeps for a platoon's formation slots, so a vehicle
        /// already standing on its post is not re-issued every movement tick. Without a standing
        /// order of its own a pooled vehicle has no <c>commandedDestination</c>, and
        /// <c>GroundVehicle.CheckObstacles</c> walks it at the nearest objective on its own — see
        /// <see cref="StagePool"/>.
        /// </summary>
        internal readonly Dictionary<Unit, GlobalPosition> PoolIssued = new();

        internal readonly List<CommanderPlatoon> Platoons = new();

        internal readonly List<CommanderOperationsMission> Missions = new();

        /// <summary>This review's control points, ranked by value descending (T6's <c>RankPoints</c>).</summary>
        internal readonly List<CommanderRankedPoint> RankedPoints = new();

        /// <summary>The order book: every open (or recently closed) requisition line for this HQ
        /// (T9).</summary>
        internal readonly List<CommanderRequisition> Requisitions = new();

        /// <summary>Minutes of pressure accrued since the last attack (design SS3).</summary>
        internal float Pressure = 0f;

        /// <summary>Control points held as of the last review, so the next one can tell how many
        /// were lost in between (feeds the pressure clock).</summary>
        internal int LastPointsHeld = -1;

        /// <summary>Next index into <c>PlatoonNames</c>, wrapping with a numeric suffix past the
        /// end of the table.</summary>
        internal int NextPlatoonNumber = 0;

        /// <summary>
        /// B5 fix: per-target observed-enemy floor, keyed by the <see cref="CommanderStrategicPoint"/>
        /// or <see cref="Airbase"/> a failed attack was aimed at. <c>CountObserved</c> is a live read
        /// of <c>ThreatMemorySeconds</c>-old tracking contacts; withdrawing out of contact after a
        /// failed attack lets that tracking decay to near zero well before the next review, so
        /// without a floor the very next attempt on the same target would be sized as if the first
        /// attempt had never happened. Cleared with the rest of this HQ's state on a session reset
        /// (<see cref="states"/> itself is cleared in <c>ResetSession</c>), so a floor never survives
        /// into a new match.
        /// </summary>
        internal readonly Dictionary<object, int> ObservedFloors = new();

        /// <summary>
        /// Air support (design.md, air-support-tasking_20260913; CAP first, 2026-09-13): the live
        /// sorties over this HQ's objectives — each one objective's CAP wing plus its CAS — kept in
        /// priority order (attack, contact, threatened forward base).
        /// </summary>
        internal readonly List<CommanderAirSortie> AirSorties = new();

        /// <summary>
        /// Every airframe this commander's buy loop launched, added at the registration claim. Only
        /// these are ever tasked or retasked — the player's own Air Command launches are already
        /// mission-bound at registration, and stock-mission authored free aircraft carry no claim,
        /// so neither ever enters this set (decision 5).
        /// </summary>
        internal readonly HashSet<Aircraft> CommanderAirframes = new();

        /// <summary>The buy loop's launch just made, waiting for the airframe to register so it can
        /// be claimed into the hungry sortie; null when nothing is in flight from the buying call.</summary>
        internal CommanderPendingAirLaunch? PendingAirLaunch;

        /// <summary>The last task this commander issued per owned airframe, so a mission that
        /// moved for any other reason is read as the player's order and the airframe is let go —
        /// the air-side hands-off rule (decision 5).</summary>
        internal readonly Dictionary<Aircraft, IssuedAirTask> AirIssued = new();

        /// <summary>
        /// Picket insertion (design.md, heli-picket-insertion_20260913): the open insertion records
        /// — one per requested flight, bound to the picket mission it delivers for.
        /// </summary>
        internal readonly List<CommanderInsertion> Insertions = new();

        /// <summary>Scaled <c>Time.time</c> until which a point that lost an insertion flight will
        /// not ask for another, keyed by the point (the ObservedFloors key convention).</summary>
        internal readonly Dictionary<object, float> InsertionCooldownUntil = new();

        /// <summary>Last decline reason per point, so a declined insertion logs once per reason
        /// rather than once per 30 s review (the ReportAirDenial convention).</summary>
        internal readonly Dictionary<CommanderStrategicPoint, string> InsertionDenials = new();
    }

    public void TickPersistent()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null || !localHq.IsServer)
        {
            return;
        }

        if (CommanderScheduler.IsDue(ref nextReviewAt, ReviewIntervalSeconds))
        {
            Review();
        }

        if (CommanderScheduler.IsDue(ref nextMovementAt, MovementIntervalSeconds))
        {
            TickMovement();
        }
    }

    public void ResetSession()
    {
        states.Clear();
        // The point objects a stale hold-post cache keys on do not survive a mission reload
        // (discovery reruns from scratch), the same reason the garrison step's own cache was
        // cleared here.
        holdPosts.Clear();
        nextReviewAt = CommanderScheduler.Stagger("operations.review", ReviewIntervalSeconds);
        nextMovementAt = CommanderScheduler.Stagger("operations.movement", MovementIntervalSeconds);
    }

    private void Review()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return;
        }

        EnsureStates(localHq);
        foreach (KeyValuePair<FactionHQ, OperationsState> entry in states)
        {
            FactionHQ hq = entry.Key;
            OperationsState state = entry.Value;
            SweepPool(hq, state);
            FoldWithdrawnPlatoons(hq, state);
            RankPoints(hq, state);
            PlanForwardBases(hq, state);
            FillMissionTrucks(hq, state);
            PlanPickets(hq, state);
            // After PlanPickets so the picket missions it reads are this review's, and before the
            // offensive so an insertion never competes with an attack for the same review's pot.
            PlanInsertions(hq, state);
            PlanOffensive(hq, state);
            UpdatePressure(hq, state);
            // B1 fix: every mission this review's planning may have just opened (a forward base, a
            // picket or an attack, whether from PlanOffensive above or from the pressure clock inside
            // UpdatePressure) is manned here, before UpdateAttacks below ever tests an axis for
            // arrival. Matching platoons to missions was previously the LAST step of a review, so a
            // brand-new attack mission's axes were always empty on the very review UpdateAttacks first
            // saw them; a group with no platoon then read as vacuously "arrived", so the attack
            // launched with zero vehicles and stayed permanently Launched. The forward-base/picket/
            // offensive PLANNING order above (design SS2's pipeline) is untouched by this move.
            AssignPlatoons(hq, state);
            UpdateAttacks(hq, state);
            // After UpdateAttacks so this review's attack resolutions are already known when the
            // sortie list is derived, and its first-arrival stamps open the CAS demand (Approval 1).
            PlanAirSupport(hq, state);
            PostRequisitions(hq, state);
            // Prune again after the fills so the review line's staged= counts only vehicles still
            // in the pool, not the ones a platoon or picket took this review.
            PruneStagingPosts(state);
            LogReviewDiagnostics(hq, state);
        }

        PruneStates(localHq);
    }

    /// <summary>
    /// Design SS1's arrival rule: the leader is inside the objective ring and at least half the
    /// members are within <c>FormationCohesionMeters</c> of their slot. Pure, for the self-check.
    /// Inclusive on both boundaries — the convention the rest of the mod uses.
    /// </summary>
    internal static bool HasArrived(
        float leaderDistanceMeters, float objectiveRadiusMeters, int membersInCohesion, int memberCount)
    {
        return leaderDistanceMeters <= objectiveRadiusMeters && membersInCohesion * 2 >= memberCount;
    }

    // T7 extends the Holding branch below with each member's own hold post.
    private void TickMovement()
    {
        foreach (KeyValuePair<FactionHQ, OperationsState> entry in states)
        {
            List<CommanderPlatoon> platoons = entry.Value.Platoons;
            for (int i = 0; i < platoons.Count; i++)
            {
                CommanderPlatoon platoon = platoons[i];
                if (platoon.Members.Count == 0)
                {
                    continue;
                }

                // Re-elect when the leader is gone, was swept out of the platoon (a player order
                // hands the vehicle back to the player), or is driving under a player order this
                // instant: the marker and the formation anchor must follow a vehicle the platoon
                // still owns, not the one the player just drove off with.
                if (platoon.Leader == null
                    || platoon.Leader.disabled
                    || !platoon.Members.Contains(platoon.Leader)
                    || CommanderMoveService.Instance?.HasPlayerOrder(platoon.Leader) == true)
                {
                    platoon.Leader = FirstLiveMember(platoon);
                }

                if (platoon.State == CommanderPlatoonState.Holding)
                {
                    // Root cause of the reserve thrash: these must feed platoon.Issued too, the same
                    // as IssuePlatoonMove below, or CountInCohesion keeps comparing a holding
                    // platoon's real position against the stale formation slot it arrived on rather
                    // than the post it is actually standing on.
                    if (platoon.Mission?.Point != null)
                    {
                        DriveToHoldPosts(platoon.Mission.Point, platoon.Members, platoon.Issued);
                    }
                    else if (platoon.Mission?.Kind == CommanderMissionKind.Reserve)
                    {
                        DriveToReserveRing(entry.Key, platoon.Members, platoon.Issued);
                    }

                    continue;
                }

                if ((platoon.State == CommanderPlatoonState.Moving || platoon.State == CommanderPlatoonState.Attacking)
                    && ContactDrill(entry.Key, platoon))
                {
                    continue;
                }

                CommanderMoveService.IssuePlatoonMove(
                    platoon.Members, platoon.Objective, CommanderFormationShape.Wedge, platoon.Issued);
            }

            StagePool(entry.Key, entry.Value);
        }
    }

    /// <summary>
    /// Parks every vehicle in the free pool on the commander's reserve ring until a platoon, a
    /// forward base's truck slot or a picket takes it.
    /// </summary>
    /// <remarks>
    /// Claiming a vehicle into the pool is bookkeeping; it issues no order, so until this ran the
    /// pool was the one set of claimed vehicles with no <c>commandedDestination</c> of its own.
    /// <c>GroundVehicle.CheckObstacles</c> re-targets exactly those on the nearest mission
    /// objective or tracked enemy every four seconds (the engine behaviour the home guard exists
    /// for, <c>Ai/CommanderEnemyCommanderDefence.cs</c> remarks), so a freshly spawned vehicle drove
    /// off alone the moment it left the depot ramp and was still walking at the enemy when the next
    /// review tried to form a platoon on it. A unit factory makes that a permanent stream, and a
    /// factory producing munitions trucks makes it endless: the recipe fill never puts a
    /// <see cref="CommanderPlatoonRole.Truck"/> in a platoon, so those sit in the pool until a
    /// forward base wants one and walk at the enemy the whole time.
    /// <para>
    /// The reserve ring is reused rather than a staging point of its own (Reuse rule 4): it is
    /// already the commander's "nothing to do, stand in the rear" position, it is derived from the
    /// territory centre so it moves with the front, and <c>DriveMembersToPosts</c> already skips a
    /// vehicle that is on station, so this costs no extra network traffic once the pool has settled.
    /// </para>
    /// </remarks>
    private void StagePool(FactionHQ hq, OperationsState state)
    {
        if (state.Pool.Count == 0)
        {
            return;
        }

        int newlyStaged = 0;
        for (int i = 0; i < state.Pool.Count; i++)
        {
            Unit unit = state.Pool[i];
            if (unit != null && !unit.disabled && !state.PoolIssued.ContainsKey(unit))
            {
                newlyStaged++;
            }
        }

        DriveToReserveRing(hq, state.Pool, state.PoolIssued);
        LogStaged(hq, newlyStaged, state.Pool.Count);
    }

    /// <summary>
    /// Range at which a marching platoon counts a tracked hostile ground vehicle as contact and
    /// deploys. Inside the game's ground engagement ranges (tank guns and short-range missiles reach
    /// 2–4 km) so the line is formed before the shooting starts, outside the platoon's own formation
    /// spacing so a deployed line never triggers on its own dust.
    /// </summary>
    private const float ContactRangeMeters = 2500f;

    /// <summary>How long after the last tracked contact a deployed platoon stays in its line before
    /// resuming the march: one <c>ThreatMemorySeconds</c> is too long (a column re-forms only after
    /// the tracking has decayed), one movement tick too short (a contact blinking in and out of
    /// tracking would flip the platoon between line and column every 5 s).</summary>
    private const float ContactHoldSeconds = 20f;

    /// <summary>
    /// The contact drill. A platoon on the march travels the game's road graph in whatever order
    /// the pathing leaves it — effectively a column — and drove that column straight into any enemy
    /// it met. Now, while a tracked hostile ground vehicle is inside <see cref="ContactRangeMeters"/>
    /// of the leader, the platoon deploys where it stands into a line abreast facing the contact
    /// (tanks at the centre, slot 0), and holds that line for <see cref="ContactHoldSeconds"/> after
    /// the last contact before marching on. Returns true when it issued the line this tick.
    /// </summary>
    private bool ContactDrill(FactionHQ hq, CommanderPlatoon platoon)
    {
        Unit? leader = platoon.Leader;
        if (leader == null || leader.disabled)
        {
            return false;
        }

        GlobalPosition here = leader.transform.GlobalPosition();
        bool wasInContact = platoon.InContactUntil >= Time.time;
        if (TryNearestTrackedHostile(hq, here, ContactRangeMeters, out GlobalPosition hostile, out float distance))
        {
            platoon.InContactUntil = Time.time + ContactHoldSeconds;
            if (!wasInContact)
            {
                LogContact(hq, platoon, distance);
            }
        }
        else if (!wasInContact)
        {
            return false;
        }
        else
        {
            // Holding the line on memory: face where the contact last was, which is where the
            // platoon's own formation already points.
            hostile = platoon.ContactBearingAnchor;
        }

        platoon.ContactBearingAnchor = hostile;
        float facing = CommanderMoveService.HeadingDegrees(here.ToLocalPosition(), hostile.ToLocalPosition());
        CommanderMoveService.IssuePlatoonMove(
            platoon.Members, platoon.LineAnchor(here), CommanderFormationShape.Line, platoon.Issued, facing);
        return true;
    }

    /// <summary>
    /// Nearest tracked hostile ground vehicle to <paramref name="from"/> within <paramref name="range"/>,
    /// from the commander's own tracking database with the same freshness and unit filters as
    /// <c>CountObserved</c> (design SS3: the commander acts on what it has tracked, not the truth).
    /// </summary>
    private static bool TryNearestTrackedHostile(
        FactionHQ hq,
        GlobalPosition from,
        float range,
        out GlobalPosition position,
        out float distance)
    {
        position = default;
        distance = float.MaxValue;
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > CommanderEnemyCommanderService.ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not GroundVehicle
                || unit is Building
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq))
            {
                continue;
            }

            float d = CommanderGameAccess.HorizontalDistance(from.AsVector3(), info.lastKnownPosition.AsVector3());
            if (d <= range && d < distance)
            {
                distance = d;
                position = info.lastKnownPosition;
            }
        }

        return distance <= range;
    }

    /// <summary>First live member the player is not driving; failing that, any live member.</summary>
    private static Unit? FirstLiveMember(CommanderPlatoon platoon)
    {
        Unit? fallback = null;
        for (int i = 0; i < platoon.Members.Count; i++)
        {
            Unit member = platoon.Members[i];
            if (member == null || member.disabled)
            {
                continue;
            }

            if (CommanderMoveService.Instance?.HasPlayerOrder(member) != true)
            {
                return member;
            }

            fallback ??= member;
        }

        return fallback;
    }

    /// <summary>The arrival rule at its five named boundaries (design SS1).</summary>
    /// <summary>The march order the wedge relies on: armour at the point, air defence behind.</summary>
    private static void CheckMarchOrder(List<string> failures)
    {
        Expect(failures, "armour leads the march", MarchRank(CommanderPlatoonRole.Armour) == 0, true);
        Expect(
            failures,
            "carriers ride behind the armour",
            MarchRank(CommanderPlatoonRole.Carrier) > MarchRank(CommanderPlatoonRole.Armour),
            true);
        Expect(
            failures,
            "air defence rides behind the carriers",
            MarchRank(CommanderPlatoonRole.AirDefence) > MarchRank(CommanderPlatoonRole.Carrier),
            true);
        Expect(failures, "a truck brings up the rear", MarchRank(CommanderPlatoonRole.Truck) == MarchOrder.Length - 1, true);
    }

    private static void CheckArrival(List<string> failures)
    {
        Expect(failures, "a leader outside the objective ring has not arrived", !HasArrived(401f, 400f, 6, 6), true);
        Expect(failures, "a leader exactly on the ring edge counts as inside", HasArrived(400f, 400f, 6, 6), true);
        Expect(
            failures,
            "a leader on the objective with fewer than half the platoon closed up has not arrived",
            !HasArrived(100f, 400f, 2, 6),
            true);
        Expect(failures, "exactly half the platoon closed up counts as arrived", HasArrived(100f, 400f, 3, 6), true);
        Expect(failures, "a one-vehicle platoon arrives when its leader does", HasArrived(0f, 400f, 1, 1), true);
    }

    /// <summary>
    /// Fills a platoon's recipe from a candidate list, pure so the self-check can drive it with
    /// synthetic data. Pass 1 takes up to <paramref name="want"/>[role] candidates per role in
    /// Armour/Carrier/AirDefence order, preferring an entry whose <paramref name="prefers"/> is
    /// true (design SS1: a capture-capable carrier beats one that cannot take ground). Pass 2 fills
    /// any slot the recipe left open with any remaining non-<see cref="CommanderPlatoonRole.Truck"/>
    /// candidate (design SS1: "Unfillable slot -&gt; any combat vehicle"; a munitions truck is
    /// requisitioned separately and never fills a combat slot). Never exceeds
    /// <paramref name="platoonSize"/> picks and never repeats an index.
    /// </summary>
    internal static void FillRecipe(
        IReadOnlyList<CommanderPlatoonRole> roles,
        IReadOnlyList<bool> prefers,
        IReadOnlyList<int> want,
        int platoonSize,
        List<int> picks)
    {
        picks.Clear();
        bool[] taken = new bool[roles.Count];
        CommanderPlatoonRole[] recipeOrder =
        {
            CommanderPlatoonRole.Armour, CommanderPlatoonRole.Carrier, CommanderPlatoonRole.AirDefence,
        };

        for (int r = 0; r < recipeOrder.Length && picks.Count < platoonSize; r++)
        {
            CommanderPlatoonRole role = recipeOrder[r];
            int wanted = (int)role < want.Count ? want[(int)role] : 0;
            int filled = 0;

            for (int i = 0; i < roles.Count && filled < wanted && picks.Count < platoonSize; i++)
            {
                if (!taken[i] && roles[i] == role && i < prefers.Count && prefers[i])
                {
                    taken[i] = true;
                    picks.Add(i);
                    filled++;
                }
            }

            for (int i = 0; i < roles.Count && filled < wanted && picks.Count < platoonSize; i++)
            {
                if (!taken[i] && roles[i] == role)
                {
                    taken[i] = true;
                    picks.Add(i);
                    filled++;
                }
            }
        }

        for (int i = 0; i < roles.Count && picks.Count < platoonSize; i++)
        {
            if (!taken[i] && roles[i] != CommanderPlatoonRole.Truck)
            {
                taken[i] = true;
                picks.Add(i);
            }
        }
    }

    /// <summary>Role and capture-preference arrays for every free-pool vehicle, scratch for
    /// <see cref="FillRecipe"/>.</summary>
    private void BuildRecipeArrays(List<Unit> pool)
    {
        recipeRoles.Clear();
        recipePrefers.Clear();
        for (int i = 0; i < pool.Count; i++)
        {
            VehicleDefinition? definition = pool[i]?.definition as VehicleDefinition;
            CommanderPlatoonRole role = CommanderPlatoonRoles.Of(definition);
            recipeRoles.Add(role);
            recipePrefers.Add(role == CommanderPlatoonRole.Carrier && definition != null && definition.captureStrength > 0f);
        }
    }

    /// <summary>
    /// Forms a new platoon from the free pool, up to <paramref name="wantedSize"/> vehicles by
    /// recipe. Forms on whatever the recipe fill returns as long as at least one member came back
    /// (design SS1: a platoon forms on what it has and requisitions the rest — a platoon of one is
    /// <see cref="CommanderPlatoonState.Forming"/>, not a platoon that never exists).
    /// </summary>
    private CommanderPlatoon? TryFormPlatoon(FactionHQ hq, OperationsState state, int wantedSize)
    {
        BuildRecipeArrays(state.Pool);
        int[] want =
        {
            CommanderSettings.OperationsRecipeArmour,
            CommanderSettings.OperationsRecipeCarrier,
            CommanderSettings.OperationsRecipeAirDefence,
        };
        FillRecipe(recipeRoles, recipePrefers, want, wantedSize, recipePicks);
        if (recipePicks.Count == 0)
        {
            return null;
        }

        CommanderPlatoon platoon = new()
        {
            Name = $"{PlatoonNames[state.NextPlatoonNumber % PlatoonNames.Length]} PLATOON",
            Establishment = wantedSize,
            State = CommanderPlatoonState.Forming,
            FormingSince = Time.time,
        };
        state.NextPlatoonNumber++;

        TakePicksFromPool(state.Pool, recipePicks, platoon.Members);
        OrderForMarch(platoon.Members);
        platoon.Leader = platoon.Members.Count > 0 ? platoon.Members[0] : null;
        // A forming platoon gathers where its first vehicle stands, on clear ground off the road
        // and off the runway, and waits there for the rest. Without an objective of its own the
        // movement tick drove every forming platoon toward the default position: the map origin.
        platoon.Objective = platoon.Leader != null
            ? ChooseFormUpPoint(platoon.Leader.transform.GlobalPosition())
            : CommanderCaptureService.GetTerritoryCenter(hq);
        state.Platoons.Add(platoon);
        CommanderAiLog.Note(hq, $"forms {platoon.Name}: {platoon.Members.Count}/{wantedSize} vehicles.");
        return platoon;
    }

    /// <summary>
    /// How long a forming platoon waits for its missing vehicles before it moves out with what it
    /// has. Three minutes is six buy reviews: if the buyer has not filled the slot by then it is
    /// not going to soon, and a five-vehicle platoon at the front beats a full one at the depot.
    /// </summary>
    private const float FormUpTimeoutSeconds = 180f;

    /// <summary>
    /// Whether a platoon may take a mission: it has no mission, is not withdrawing, and has either
    /// finished forming (full) or waited out <see cref="FormUpTimeoutSeconds"/>. The gate is what
    /// makes a platoon leave the form-up point together instead of the first vehicle driving off
    /// alone the review it was bought. Pure, for the self-check.
    /// </summary>
    internal static bool IsReadyToMoveOut(int strength, int establishment, float formingSeconds, float timeoutSeconds)
    {
        return strength >= establishment || formingSeconds >= timeoutSeconds;
    }

    private static bool IsAvailableForMission(CommanderPlatoon platoon)
    {
        if (platoon.Mission != null || platoon.State == CommanderPlatoonState.Withdrawing)
        {
            return false;
        }

        return platoon.State != CommanderPlatoonState.Forming
            || IsReadyToMoveOut(platoon.Members.Count, platoon.Establishment, Time.time - platoon.FormingSince, FormUpTimeoutSeconds);
    }

    /// <summary>Ring the form-up spot is looked for in around the first vehicle: far enough to be
    /// off the depot apron and the road it drove out on, near enough that the rest of the platoon
    /// reaches it in under a minute.</summary>
    private const float FormUpMinMeters = 150f;
    private const float FormUpMaxMeters = 400f;

    /// <summary>
    /// A form-up spot near <paramref name="around"/> that the shared siting rule accepts with no
    /// faction attached (the same probe discovery uses for a resource site: clear of roads, runways
    /// and other units' footprints). Falls back to the anchor itself when every probe fails, which
    /// is still better than the origin.
    /// </summary>
    private static GlobalPosition ChooseFormUpPoint(GlobalPosition around)
    {
        CommanderEconomyService? economy = CommanderEconomyService.Instance;
        BuildingDefinition? probe = economy?.MineDefinition;
        if (economy == null || probe == null)
        {
            return around;
        }

        for (int attempt = 0; attempt < CommanderEconomyService.EnemySiteAttempts; attempt++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(FormUpMinMeters, FormUpMaxMeters);
            GlobalPosition candidate = new(
                around.x + Mathf.Cos(angle) * distance,
                around.y,
                around.z + Mathf.Sin(angle) * distance);
            if (economy.IsSiteAllowed(probe, candidate, null, out _))
            {
                return CommanderGameAccess.SnapToTerrain(candidate);
            }
        }

        return around;
    }

    /// <summary>
    /// Tops up a below-establishment platoon from the free pool, wanting only the roles it is
    /// still short of (so a platoon that already has its full complement of armour is not handed a
    /// fourth one while its carrier slot sits empty).
    /// </summary>
    private void ReinforcePlatoon(OperationsState state, CommanderPlatoon platoon)
    {
        int need = platoon.Establishment - platoon.Members.Count;
        if (need <= 0 || state.Pool.Count == 0)
        {
            return;
        }

        int[] have = new int[3];
        for (int i = 0; i < platoon.Members.Count; i++)
        {
            CommanderPlatoonRole role = CommanderPlatoonRoles.Of(platoon.Members[i]?.definition as VehicleDefinition);
            if ((int)role < have.Length)
            {
                have[(int)role]++;
            }
        }

        int[] want =
        {
            Mathf.Max(0, CommanderSettings.OperationsRecipeArmour - have[(int)CommanderPlatoonRole.Armour]),
            Mathf.Max(0, CommanderSettings.OperationsRecipeCarrier - have[(int)CommanderPlatoonRole.Carrier]),
            Mathf.Max(0, CommanderSettings.OperationsRecipeAirDefence - have[(int)CommanderPlatoonRole.AirDefence]),
        };

        BuildRecipeArrays(state.Pool);
        FillRecipe(recipeRoles, recipePrefers, want, need, recipePicks);
        if (recipePicks.Count == 0)
        {
            return;
        }

        TakePicksFromPool(state.Pool, recipePicks, platoon.Members);
        OrderForMarch(platoon.Members);
        // A tank bought to replace a lost one takes the point back from whoever held it meanwhile.
        platoon.Leader = platoon.Members.Count > 0 ? platoon.Members[0] : null;
    }

    /// <summary>
    /// March order of a platoon's roles, front to rear: the formation slot is the member's index
    /// (<c>CommanderDestinationFormation.ApplyOffset</c>, slot 0 the point of the wedge), so this
    /// is what puts the tanks in front and the air defence behind them. Anything unclassified
    /// rides between the carriers and the air defence; a truck, if one ever joins, brings up the rear.
    /// </summary>
    private static readonly CommanderPlatoonRole[] MarchOrder =
    {
        CommanderPlatoonRole.Armour,
        CommanderPlatoonRole.Carrier,
        CommanderPlatoonRole.Other,
        CommanderPlatoonRole.AirDefence,
        CommanderPlatoonRole.Truck,
    };

    /// <summary>
    /// Stable re-order of <paramref name="members"/> into <see cref="MarchOrder"/>. Stable so two
    /// tanks keep their relative slots between reviews and are not re-issued destinations for a
    /// swap. The self-check covers the rank table via <see cref="MarchRank"/>.
    /// </summary>
    internal static void OrderForMarch(List<Unit> members)
    {
        // Insertion sort: six vehicles, already mostly ordered, and List.Sort is not stable.
        for (int i = 1; i < members.Count; i++)
        {
            // A member slot can read null between a unit's death and the next review's sweep. The
            // sort carries such entries as rank-Other exactly as it did before the nullable pass
            // noticed — the `!` below is that behaviour kept, not a promise about the list.
            Unit? moving = members[i];
            int rank = MarchRank(CommanderPlatoonRoles.Of(moving?.definition as VehicleDefinition));
            int j = i - 1;
            while (j >= 0 && MarchRank(CommanderPlatoonRoles.Of(members[j]?.definition as VehicleDefinition)) > rank)
            {
                members[j + 1] = members[j];
                j--;
            }

            members[j + 1] = moving!;
        }
    }

    /// <summary>Position of <paramref name="role"/> in <see cref="MarchOrder"/>; unknown roles last.</summary>
    internal static int MarchRank(CommanderPlatoonRole role)
    {
        int index = System.Array.IndexOf(MarchOrder, role);
        return index < 0 ? MarchOrder.Length : index;
    }

    /// <summary>Moves the picked pool indices into <paramref name="destination"/>, highest index
    /// first so an earlier removal never invalidates a later index.</summary>
    private static void TakePicksFromPool(List<Unit> pool, List<int> picks, List<Unit> destination)
    {
        List<int> sortedPicks = new(picks);
        sortedPicks.Sort();
        List<Unit> chosen = new(sortedPicks.Count);
        for (int i = sortedPicks.Count - 1; i >= 0; i--)
        {
            int index = sortedPicks[i];
            chosen.Add(pool[index]);
            pool.RemoveAt(index);
        }

        chosen.Reverse();
        destination.AddRange(chosen);
    }

    /// <summary>
    /// Recipe fill with fallbacks, at the default 3 armour / 1 carrier / 2 air defence and
    /// <c>platoonSize = 6</c>.
    /// </summary>
    private static void CheckRecipe(List<string> failures)
    {
        List<int> picks = new();
        CommanderPlatoonRole[] noPreferenceRoles =
        {
            CommanderPlatoonRole.Armour, CommanderPlatoonRole.Armour, CommanderPlatoonRole.Armour,
            CommanderPlatoonRole.Carrier, CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.AirDefence,
        };
        bool[] noPreferences = { false, false, false, false, false, false };
        int[] defaultWant = { 3, 1, 2 };

        FillRecipe(noPreferenceRoles, noPreferences, defaultWant, 6, picks);
        ExpectSequence(failures, "a pool that matches the recipe fills it exactly", picks, 0, 1, 2, 3, 4, 5);

        FillRecipe(
            new[] { CommanderPlatoonRole.Carrier, CommanderPlatoonRole.Carrier },
            new[] { false, true },
            new[] { 0, 1, 0 },
            6,
            picks);
        ExpectSequence(failures, "a capture-capable carrier is taken ahead of one that cannot take ground", picks, 1, 0);

        FillRecipe(
            new[]
            {
                CommanderPlatoonRole.Armour, CommanderPlatoonRole.Armour, CommanderPlatoonRole.Armour,
                CommanderPlatoonRole.Armour, CommanderPlatoonRole.Armour, CommanderPlatoonRole.Armour,
            },
            noPreferences,
            defaultWant,
            6,
            picks);
        ExpectSequence(failures, "a pool of nothing but armour still forms a full platoon", picks, 0, 1, 2, 3, 4, 5);

        FillRecipe(
            new[] { CommanderPlatoonRole.Armour, CommanderPlatoonRole.AirDefence },
            new[] { false, false },
            defaultWant,
            6,
            picks);
        ExpectSequence(failures, "a short pool forms a short platoon rather than none", picks, 0, 1);

        FillRecipe(
            new[]
            {
                CommanderPlatoonRole.Armour, CommanderPlatoonRole.Truck, CommanderPlatoonRole.Truck,
                CommanderPlatoonRole.Truck, CommanderPlatoonRole.Truck, CommanderPlatoonRole.Truck,
            },
            noPreferences,
            defaultWant,
            6,
            picks);
        ExpectSequence(failures, "a munitions truck never fills a combat slot", picks, 0);

        // Read into locals first: CommanderSettings.CheckDefencePosture's reasoning applies just as
        // well to a config-file value as to a const — the failure branch has to survive a retune.
        int armour = CommanderSettings.OperationsRecipeArmour;
        int carrier = CommanderSettings.OperationsRecipeCarrier;
        int airDefence = CommanderSettings.OperationsRecipeAirDefence;
        int platoonSize = CommanderSettings.OperationsPlatoonSize;
        Expect(
            failures,
            "the platoon recipe adds up to the platoon size; check the Operations section of the config",
            armour + carrier + airDefence == platoonSize,
            true);
    }

    /// <summary>
    /// The COMMANDER LOG's OPERATIONS block for <paramref name="hq"/>: one summary line, then one
    /// line per live mission in design SS5's wording. ASCII only — the IMGUI font guarantees nothing
    /// else. Adds nothing when this HQ has no operations state (design SS1: host-only, and only once
    /// discovery has something to plan against).
    /// </summary>
    internal void DescribeOperations(FactionHQ hq, List<string> lines)
    {
        if (!states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        int fobs = 0;
        int orders = 0;
        for (int i = 0; i < state.Missions.Count; i++)
        {
            if (state.Missions[i].Kind == CommanderMissionKind.ForwardBase)
            {
                fobs++;
            }
        }

        for (int i = 0; i < state.Requisitions.Count; i++)
        {
            if (state.Requisitions[i].Filled < state.Requisitions[i].Wanted)
            {
                orders++;
            }
        }

        lines.Add(
            $"PRESSURE {state.Pressure:0}/{CommanderSettings.OperationsPressureIntervalMinutes:0}  "
                + $"PLATOONS {state.Platoons.Count}  FOBS {fobs}  ORDERS {orders}");

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            switch (mission.Kind)
            {
                case CommanderMissionKind.Attack:
                    lines.Add(DescribeAttack(hq, state, mission));
                    break;
                case CommanderMissionKind.ForwardBase:
                    lines.Add(DescribeForwardBase(mission));
                    break;
                case CommanderMissionKind.Picket:
                    lines.Add(DescribePicket(mission));
                    break;
            }
        }
    }

    /// <summary>
    /// Design SS5's exact wording (<c>"axes FOB Hilltop 12 + Crossroads 4"</c>): one label per
    /// distinct axis (deduplicated by release point, the way <see cref="AssignPlatoonsToAxes"/>
    /// shares one form-up/release pair across extra platoons on the same axis), and a
    /// platoons-forming-up count rather than a groups count. Previously this printed placeholder
    /// <c>AXIS 1</c>/<c>AXIS 2</c> labels and counted every <see cref="CommanderAssaultGroup"/> —
    /// including ones with no platoon yet — against the denominator.
    /// </summary>
    private static string DescribeAttack(FactionHQ hq, OperationsState state, CommanderOperationsMission mission)
    {
        List<GlobalPosition> seenReleases = new();
        List<string> axisLabels = new();
        for (int a = 0; a < mission.Axes.Count; a++)
        {
            GlobalPosition release = mission.Axes[a].ReleasePoint;
            bool found = false;
            for (int s = 0; s < seenReleases.Count; s++)
            {
                if (seenReleases[s].Equals(release))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                seenReleases.Add(release);
                axisLabels.Add(AxisLabel(hq, state, mission.Axes[a].FormUpPoint, axisLabels.Count + 1));
            }
        }

        string axesLabel = axisLabels.Count == 0 ? "none yet" : string.Join(" + ", axisLabels);

        int arrivedPlatoons = 0;
        int totalPlatoons = 0;
        for (int a = 0; a < mission.Axes.Count; a++)
        {
            if (mission.Axes[a].Platoon == null)
            {
                continue;
            }

            totalPlatoons++;
            if (mission.Axes[a].Arrived)
            {
                arrivedPlatoons++;
            }
        }

        return $"ATTACK {mission.Label}: {mission.Assigned.Count} platoons, axes {axesLabel}, "
            + $"forming up {arrivedPlatoons}/{totalPlatoons}";
    }

    /// <summary>An axis's own name: <c>"FOB " + label</c> for a live forward base of this HQ's,
    /// the airbase's label for a held base, or a numbered fallback for a flank point (design SS3)
    /// that is neither.</summary>
    private static string AxisLabel(FactionHQ hq, OperationsState state, GlobalPosition formUp, int fallbackIndex)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission candidate = state.Missions[i];
            if (candidate.Kind == CommanderMissionKind.ForwardBase
                && candidate.Point != null
                && candidate.Point.Position.Equals(formUp))
            {
                return "FOB " + candidate.Point.Label;
            }
        }

        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled && airbase.center != null
                && airbase.center.GlobalPosition().Equals(formUp))
            {
                return CommanderCaptureService.GetAirbaseLabel(airbase);
            }
        }

        return $"AXIS {fallbackIndex}";
    }

    private static string DescribeForwardBase(CommanderOperationsMission mission)
    {
        int platoons = mission.Assigned.Count;
        string truckNote = mission.Truck == null ? ", no truck" : string.Empty;
        return $"FOB {mission.Label}: {platoons}/{mission.WantedPlatoons} platoons{truckNote}";
    }

    private static string DescribePicket(CommanderOperationsMission mission)
    {
        return $"PICKET {mission.Label}: {mission.PicketMembers.Count}/{CommanderSettings.PointsMinGarrison}";
    }

    /// <summary>
    /// One runnable check at plugin load, next to every other service's. <c>ExpectSequence</c> and
    /// the three <c>Expect</c> overloads below are a second harness copied from
    /// <c>Points/CommanderStrategicPointService.cs:1060-1098</c> rather than shared — both are
    /// <c>private</c> to that class and both are load-time-only harnesses with no runtime caller,
    /// so sharing them would mean making a load-time implementation detail public for no second
    /// real use (Reuse rule 4 does not apply to two things that are not actually the same thing).
    /// </summary>
    internal static void SelfCheck()
    {
        List<string> failures = new();
        CheckRecipe(failures);
        CheckMarchOrder(failures);
        CheckArrival(failures);
        CheckFront(failures);
        CheckStrength(failures);
        CheckForwardBaseShare(failures);
        CheckOrderBook(failures);
        CheckSizing(failures);
        CheckObservedFloor(failures);
        CheckAxes(failures);
        CheckPressure(failures);
        CheckAirSupport(failures);
        CheckInsertion(failures);

        if (failures.Count == 0)
        {
            CommanderPlugin.Log.LogDebug("Operations self-check passed.");
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Operations self-check FAILED: {failures[i]}");
        }
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
