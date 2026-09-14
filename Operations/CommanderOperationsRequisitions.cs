using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The pool every AI-commanded HQ's ground vehicles are claimed into (design SS1: "the mod owns
/// every vehicle an AI commander buys"), and the order book that tells the buyer what to spend on
/// next (design SS4).
/// </summary>
internal sealed partial class CommanderOperationsService
{
    /// <summary>Number of <see cref="CommanderPlatoonRole"/> values — the width of a
    /// totals-by-role array.</summary>
    internal const int RoleCount = 5;

    /// <summary>A requisition unfilled this long proceeds with what it has, or dissolves if it has
    /// nothing (design SS4: 5 minutes).</summary>
    private const float RequisitionTimeoutSeconds = 300f;

    /// <summary>
    /// One open order line: <paramref name="Role"/> wants <paramref name="Wanted"/> vehicles and has
    /// <paramref name="Filled"/> so far, opened at <paramref name="OpenedAt"/> for the timeout, on
    /// behalf of <paramref name="Mission"/> (null for a synthetic self-check entry, or for
    /// <see cref="PostWithdrawingRequisitions"/>'s aggregate line — a withdrawing platoon has no
    /// mission of its own to key the line on; every withdrawing platoon's shortfall for a role
    /// collapses into that one null-mission line per role, which is fine since the order book only
    /// ever sums totals by role).
    /// </summary>
    internal struct CommanderRequisition
    {
        /// <summary>The role this line wants.</summary>
        internal CommanderPlatoonRole Role;

        /// <summary>Vehicles of that role this line still wants in total.</summary>
        internal int Wanted;

        /// <summary>Vehicles claimed against this line so far.</summary>
        internal int Filled;

        /// <summary>Scaled <c>Time.time</c> this line opened, for <see cref="RequisitionTimeoutSeconds"/>.</summary>
        internal float OpenedAt;

        /// <summary>The mission this line serves.</summary>
        internal CommanderOperationsMission? Mission;
    }

    private readonly List<FactionHQ> staleHqs = new();

    /// <summary>Scratch for <see cref="PruneStagingPosts"/>, so the prune never allocates.</summary>
    private readonly List<Unit> stalePoolPosts = new();

    /// <summary>
    /// The one-line forwarder shape (<c>AirCommand/CommanderAirCommandPilotHooks.cs:12-15</c>),
    /// wired into the shared <c>FactionHQ.RegisterFactionUnit</c> postfix (ledger row 5).
    /// </summary>
    internal static void NotifyFactionUnitRegistered(FactionHQ hq, Unit unit)
    {
        Instance?.TryClaim(hq, unit);
    }

    /// <summary>
    /// Claims a freshly registered ground vehicle into the free pool, for an HQ this service
    /// already manages. "Already manages" (<paramref name="hq"/> already has a state) is the gate
    /// rather than re-testing <c>IsCommanded</c>: a state exists only once the point list is
    /// non-empty (see <see cref="OwnsGroundForce"/>), so a vehicle that registers before discovery
    /// finishes is left for the home guard and the expansion drive, exactly what departures 6 and 8
    /// ask for.
    /// </summary>
    private void TryClaim(FactionHQ hq, Unit unit)
    {
        if (!states.TryGetValue(hq, out OperationsState state)
            || !hq.IsServer
            || unit == null
            || unit.disabled
            || unit is not GroundVehicle
            || unit.definition is not VehicleDefinition definition
            || definition.vehicleType == VehicleType.RDR
            || CommanderMoveService.Instance?.HasPlayerOrder(unit) == true)
        {
            return;
        }

        if (!IsClaimedVehicle(state, unit))
        {
            state.Pool.Add(unit);
            LogClaim(hq, unit, state.Pool.Count);
            FillOldestRequisition(hq, state, CommanderPlatoonRoles.Of(definition));
        }
    }

    /// <summary>Credits a freshly claimed vehicle's role against the oldest open requisition that
    /// wants it (design SS4: "the oldest matching open requisition"), notifying the mission once
    /// the line closes. Bookkeeping only — which vehicle actually ends up in which platoon is still
    /// the recipe fill's job.</summary>
    private static void FillOldestRequisition(FactionHQ hq, OperationsState state, CommanderPlatoonRole role)
    {
        int oldestIndex = -1;
        float oldestTime = float.MaxValue;
        for (int i = 0; i < state.Requisitions.Count; i++)
        {
            CommanderRequisition requisition = state.Requisitions[i];
            if (requisition.Role == role && requisition.Filled < requisition.Wanted && requisition.OpenedAt < oldestTime)
            {
                oldestTime = requisition.OpenedAt;
                oldestIndex = i;
            }
        }

        if (oldestIndex < 0)
        {
            return;
        }

        CommanderRequisition found = state.Requisitions[oldestIndex];
        found.Filled++;
        state.Requisitions[oldestIndex] = found;
        if (found.Filled >= found.Wanted && found.Mission != null)
        {
            CommanderAiLog.Note(hq, $"{found.Mission.Label}: requisition filled.");
        }
    }

    /// <summary>
    /// Ground truth for the pool, run at the top of every review: walks every ground vehicle the
    /// faction owns and adds anything the claim hook missed — chiefly the vehicles a mission was
    /// authored with, which register before this service has a state for their HQ — then drops
    /// pool and platoon members that are gone, disabled, changed faction, or under a player order
    /// (the <c>PruneDefenders</c> filter, <c>Ai/CommanderEnemyCommanderDefence.cs:297-320</c>,
    /// including its point about leaving <c>commandedDestination</c> alone for a vehicle the player
    /// has taken back).
    /// </summary>
    private void SweepPool(FactionHQ hq, OperationsState state)
    {
        if (hq.factionUnits != null)
        {
            foreach (PersistentID id in hq.factionUnits)
            {
                if (id.TryGetUnit(out Unit unit)
                    && unit != null
                    && !unit.disabled
                    && unit is GroundVehicle
                    && unit.definition is VehicleDefinition definition
                    && definition.vehicleType != VehicleType.RDR
                    && CommanderMoveService.Instance?.HasPlayerOrder(unit) != true
                    && !IsClaimedVehicle(state, unit))
                {
                    state.Pool.Add(unit);
                }
            }
        }

        state.Pool.RemoveAll(unit => IsStalePoolUnit(hq, unit));
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            state.Platoons[i].Members.RemoveAll(unit => IsStalePoolUnit(hq, unit));
        }

        PruneStagingPosts(state);
    }

    /// <summary>Drops the staging post (<c>StagePool</c>) of every vehicle that has left the pool —
    /// taken into a platoon, a picket or a truck slot, or swept out as stale — so the record does
    /// not keep a dead unit alive for the rest of the match.</summary>
    private void PruneStagingPosts(OperationsState state)
    {
        if (state.PoolIssued.Count == 0)
        {
            return;
        }

        stalePoolPosts.Clear();
        foreach (KeyValuePair<Unit, GlobalPosition> entry in state.PoolIssued)
        {
            if (!state.Pool.Contains(entry.Key))
            {
                stalePoolPosts.Add(entry.Key);
            }
        }

        for (int i = 0; i < stalePoolPosts.Count; i++)
        {
            state.PoolIssued.Remove(stalePoolPosts[i]);
        }
    }

    /// <summary>Gone, disabled, changed faction, or under a player order — never re-tested against
    /// <c>commandedDestination</c>, which the player's own order now owns.</summary>
    private static bool IsStalePoolUnit(FactionHQ hq, Unit? unit)
    {
        return unit == null
            || unit.disabled
            || unit.NetworkHQ != hq
            || CommanderMoveService.Instance?.HasPlayerOrder(unit) == true;
    }

    private static bool IsPlatoonMember(OperationsState state, Unit unit)
    {
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            if (state.Platoons[i].Members.Contains(unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One definition of "already spoken for" (Reuse rule 4), read by <see cref="TryClaim"/> and
    /// <see cref="SweepPool"/> (so neither re-adds a vehicle already claimed) and by
    /// <see cref="IsPlatoonUnit"/> (so the home guard's recruiter and the capture squad see the same
    /// answer). B2 fix: a picket's <c>PicketMembers</c> and a forward base's <c>Truck</c> are claimed
    /// vehicles too, even though neither sits in <c>Pool</c> or in a <see cref="CommanderPlatoon"/> —
    /// missing this walk let the sweep put a picket vehicle or a parked munitions truck straight back
    /// into the free pool for a platoon fill or another mission's truck fill to take, while the
    /// mission that already claimed it kept driving it to its post.
    /// </summary>
    private static bool IsClaimedVehicle(OperationsState state, Unit unit)
    {
        if (state.Pool.Contains(unit) || IsPlatoonMember(state, unit))
        {
            return true;
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (ReferenceEquals(mission.Truck, unit) || mission.PicketMembers.Contains(unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True while <paramref name="unit"/> is claimed by any managed HQ — in a free pool, serving in
    /// a platoon, parked as a forward base's truck or standing in a picket's ring
    /// (<see cref="IsClaimedVehicle"/>). The <c>IsDefendingUnit</c> shape
    /// (<c>Ai/CommanderEnemyCommanderDefence.cs:85-102</c>), read by the home guard's recruiter and
    /// the capture squad so neither pulls a vehicle operations has already claimed.
    /// </summary>
    internal static bool IsPlatoonUnit(Unit? unit)
    {
        CommanderOperationsService? service = Instance;
        if (service == null || unit == null)
        {
            return false;
        }

        foreach (KeyValuePair<FactionHQ, OperationsState> entry in service.states)
        {
            if (IsClaimedVehicle(entry.Value, unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True once this HQ has a state at all. A state is created only once
    /// <c>CommanderStrategicPointService.Instance?.Points</c> is non-empty, so before discovery
    /// finishes this is false and the home guard and the expansion drive still run untouched
    /// (departures 6 and 8).
    /// </summary>
    internal static bool OwnsGroundForce(FactionHQ hq)
    {
        return Instance != null && Instance.states.ContainsKey(hq);
    }

    /// <summary>Platoons this commander currently has, or 0 when it is not managed here.</summary>
    internal static int PlatoonCount(FactionHQ hq)
    {
        return Instance != null && Instance.states.TryGetValue(hq, out OperationsState state)
            ? state.Platoons.Count
            : 0;
    }

    /// <summary>
    /// Pure, for the self-check. The ground buyer stops buying on plan once the commander fields
    /// <paramref name="maxPlatoons"/> platoons and no requisition is open: vehicles cost 5–15 against
    /// an income of tens per minute, so an uncapped plan buyer fielded 800 vehicles and 80 platoons
    /// in one match — most of them parked. Replacements and reserve shortfalls still come through
    /// the order book, so a capped commander is never left unable to refill a platoon.
    /// </summary>
    internal static bool GroundBuyingCapped(bool ownsGroundForce, int platoonCount, int maxPlatoons, bool hasOpenRequisition)
    {
        return ownsGroundForce && !hasOpenRequisition && maxPlatoons > 0 && platoonCount >= maxPlatoons;
    }

    /// <summary>Per-HQ state lifecycle: created once this commander is managed and the point list
    /// is ready, dropped the moment it stops being commanded (the <c>PruneStates</c> way,
    /// <c>Ai/CommanderEnemyCommanderService.cs:626-644</c>).</summary>
    private void EnsureStates(FactionHQ localHq)
    {
        IReadOnlyList<CommanderStrategicPoint>? points = CommanderStrategicPointService.Instance?.Points;
        if (points == null || points.Count == 0)
        {
            return;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null
                || !CommanderPlayerCommanderService.IsCommanded(hq, localHq)
                || !hq.IsServer
                || hq.faction == null
                || states.ContainsKey(hq))
            {
                continue;
            }

            states[hq] = new OperationsState();
        }
    }

    private void PruneStates(FactionHQ localHq)
    {
        staleHqs.Clear();
        foreach (KeyValuePair<FactionHQ, OperationsState> entry in states)
        {
            if (entry.Key == null || !CommanderPlayerCommanderService.IsCommanded(entry.Key, localHq))
            {
                staleHqs.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleHqs.Count; i++)
        {
            states.Remove(staleHqs[i]);
        }
    }

    /// <summary>Zeroes and refills <paramref name="totalsByRole"/> with what each role's open lines
    /// (wanted minus filled, never negative — an over-filled line never subtracts from another
    /// role's total) add up to. Pure, for the self-check.</summary>
    internal static void SumOrderBook(IReadOnlyList<CommanderRequisition> requisitions, int[] totalsByRole)
    {
        for (int i = 0; i < totalsByRole.Length; i++)
        {
            totalsByRole[i] = 0;
        }

        for (int i = 0; i < requisitions.Count; i++)
        {
            CommanderRequisition requisition = requisitions[i];
            int role = (int)requisition.Role;
            if (role >= 0 && role < totalsByRole.Length)
            {
                totalsByRole[role] += Mathf.Max(0, requisition.Wanted - requisition.Filled);
            }
        }
    }

    /// <summary>Index of the largest open total, or -1 when every role's book is empty (which
    /// leaves the plan-based counter triangle alone). First index wins a tie, so the answer is
    /// stable across reviews — the <c>PickRichestIndex</c> convention. Pure, for the self-check.</summary>
    internal static int LargestOpenRole(IReadOnlyList<int> totalsByRole)
    {
        int best = -1;
        int bestValue = 0;
        for (int i = 0; i < totalsByRole.Count; i++)
        {
            if (totalsByRole[i] > bestValue)
            {
                bestValue = totalsByRole[i];
                best = i;
            }
        }

        return best;
    }

    /// <summary>Live wrapper the buyer reads: does this HQ have an open line on an
    /// <see cref="CommanderMissionKind.Attack"/> mission right now (design SS4: raises the buyer's
    /// spend tempo for the review)?</summary>
    internal static bool HasOpenAttackRequisition(FactionHQ hq)
    {
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return false;
        }

        for (int i = 0; i < state.Requisitions.Count; i++)
        {
            CommanderRequisition requisition = state.Requisitions[i];
            if (requisition.Mission?.Kind == CommanderMissionKind.Attack && requisition.Filled < requisition.Wanted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Live wrapper the buyer reads (T10): any open line at all for this HQ, of any role, on any
    /// mission — broader than <see cref="HasOpenAttackRequisition"/>, which only asks about the
    /// spend-tempo boost. This is the order-book precedence gate in the buy loop.
    /// </summary>
    internal static bool HasOpenRequisition(FactionHQ hq)
    {
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return false;
        }

        for (int i = 0; i < state.Requisitions.Count; i++)
        {
            CommanderRequisition requisition = state.Requisitions[i];
            if (requisition.Filled < requisition.Wanted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Live wrapper the buyer reads: the role with the largest open line for this HQ, or
    /// <see cref="CommanderPlatoonRole.Other"/> when the book is empty (the buyer's caller,
    /// <c>ChooseForRole</c>, only ever asks this after confirming the book is non-empty via
    /// <see cref="HasOpenAttackRequisition"/>).</summary>
    /// <summary>
    /// The roles the buyer should try this purchase, best first: an open <c>Truck</c> line always
    /// leads (a forward base without its truck runs dry, and one truck is cheap next to the vehicle
    /// lines it was losing "largest line" to), then the remaining open lines largest first.
    /// <paramref name="boughtThisReview"/> is subtracted so five purchases in one review do not all
    /// chase the same line: the depot claim that credits a line only lands once the vehicle exists.
    /// The buyer takes the first role it can afford, and only falls back to its plan when it can
    /// afford none of them — before, one unaffordable "largest" role sent the whole purchase to the
    /// plan, which bought a cheap vehicle the recipe could not use.
    /// </summary>
    internal static void OpenRolesByPriority(FactionHQ hq, int[] boughtThisReview, List<CommanderPlatoonRole> roles)
    {
        roles.Clear();
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        int[] totals = new int[RoleCount];
        SumOrderBook(state.Requisitions, totals);
        for (int i = 0; i < totals.Length && i < boughtThisReview.Length; i++)
        {
            totals[i] = Mathf.Max(0, totals[i] - boughtThisReview[i]);
        }

        if (totals[(int)CommanderPlatoonRole.Truck] > 0)
        {
            roles.Add(CommanderPlatoonRole.Truck);
        }

        while (true)
        {
            int best = -1;
            for (int i = 0; i < totals.Length; i++)
            {
                if (i == (int)CommanderPlatoonRole.Truck || totals[i] <= 0)
                {
                    continue;
                }

                if (best < 0 || totals[i] > totals[best])
                {
                    best = i;
                }
            }

            if (best < 0)
            {
                return;
            }

            roles.Add((CommanderPlatoonRole)best);
            totals[best] = 0;
        }
    }

    internal static CommanderPlatoonRole LargestOpenRole(FactionHQ hq)
    {
        if (Instance != null && Instance.states.TryGetValue(hq, out OperationsState state))
        {
            int[] totals = new int[RoleCount];
            SumOrderBook(state.Requisitions, totals);
            int role = LargestOpenRole(totals);
            if (role >= 0)
            {
                return (CommanderPlatoonRole)role;
            }
        }

        return CommanderPlatoonRole.Other;
    }

    /// <summary>Sets (or clears, if <paramref name="wanted"/> is zero) this mission's line for
    /// <paramref name="role"/>, keeping whatever it has already filled. <paramref name="mission"/> is
    /// null for <see cref="PostWithdrawingRequisitions"/>'s aggregate, mission-less line.</summary>
    private static void SetRequisition(
        OperationsState state, CommanderOperationsMission? mission, CommanderPlatoonRole role, int wanted)
    {
        for (int i = 0; i < state.Requisitions.Count; i++)
        {
            CommanderRequisition existing = state.Requisitions[i];
            if (ReferenceEquals(existing.Mission, mission) && existing.Role == role)
            {
                if (wanted <= 0)
                {
                    state.Requisitions.RemoveAt(i);
                    return;
                }

                existing.Wanted = wanted;
                state.Requisitions[i] = existing;
                return;
            }
        }

        if (wanted > 0)
        {
            state.Requisitions.Add(new CommanderRequisition
            {
                Role = role,
                Wanted = wanted,
                Filled = 0,
                OpenedAt = Time.time,
                Mission = mission,
            });
        }
    }

    /// <summary>A forward base short of its recipe posts one line per empty slot — counting both
    /// its assigned platoon(s)' empty slots and any platoon it is still waiting on
    /// <see cref="CommanderOperationsMission.WantedPlatoons"/> to form — plus one <c>Truck</c> line
    /// per mission (design SS2/SS4).</summary>
    private static void PostForwardBaseRequisitions(OperationsState state, CommanderOperationsMission mission)
    {
        int wantedArmour = 0;
        int wantedCarrier = 0;
        int wantedAirDefence = 0;
        for (int i = 0; i < mission.Assigned.Count; i++)
        {
            CommanderPlatoon platoon = mission.Assigned[i];
            int haveArmour = 0;
            int haveCarrier = 0;
            int haveAirDefence = 0;
            for (int m = 0; m < platoon.Members.Count; m++)
            {
                switch (CommanderPlatoonRoles.Of(platoon.Members[m]?.definition as VehicleDefinition))
                {
                    case CommanderPlatoonRole.Armour:
                        haveArmour++;
                        break;
                    case CommanderPlatoonRole.Carrier:
                        haveCarrier++;
                        break;
                    case CommanderPlatoonRole.AirDefence:
                        haveAirDefence++;
                        break;
                }
            }

            wantedArmour += Mathf.Max(0, CommanderSettings.OperationsRecipeArmour - haveArmour);
            wantedCarrier += Mathf.Max(0, CommanderSettings.OperationsRecipeCarrier - haveCarrier);
            wantedAirDefence += Mathf.Max(0, CommanderSettings.OperationsRecipeAirDefence - haveAirDefence);
        }

        int unformed = Mathf.Max(0, mission.WantedPlatoons - mission.Assigned.Count);
        wantedArmour += unformed * CommanderSettings.OperationsRecipeArmour;
        wantedCarrier += unformed * CommanderSettings.OperationsRecipeCarrier;
        wantedAirDefence += unformed * CommanderSettings.OperationsRecipeAirDefence;

        SetRequisition(state, mission, CommanderPlatoonRole.Armour, wantedArmour);
        SetRequisition(state, mission, CommanderPlatoonRole.Carrier, wantedCarrier);
        SetRequisition(state, mission, CommanderPlatoonRole.AirDefence, wantedAirDefence);
        // Wanted 0 once the mission has a truck clears the line outright (SetRequisition's own
        // zero-wanted branch), rather than leaving it open forever. Previously this posted `1`
        // unconditionally every review, so a truck parked by FillMissionTrucks straight from the pool
        // (never routed through FillOldestRequisition, which is the only place that increments
        // `Filled`) left the line open with 0/1 filled for the rest of the match — HasOpenRequisition
        // never went false, which kept the buyer at the raised offensive spend fraction permanently.
        SetRequisition(state, mission, CommanderPlatoonRole.Truck, mission.Truck == null ? 1 : 0);
    }

    /// <summary>An attack posts <c>WantedPlatoons × PlatoonSize</c> across the recipe, proportioned
    /// by the recipe's own slot counts, less whatever its assigned platoons already field (design
    /// SS4).</summary>
    private static void PostAttackRequisitions(OperationsState state, CommanderOperationsMission mission)
    {
        int platoonSize = Mathf.Max(1, CommanderSettings.OperationsPlatoonSize);
        int totalWanted = mission.WantedPlatoons * platoonSize;
        int assignedMembers = 0;
        for (int i = 0; i < mission.Assigned.Count; i++)
        {
            assignedMembers += mission.Assigned[i].Members.Count;
        }

        int shortfall = Mathf.Max(0, totalWanted - assignedMembers);
        int armour = Mathf.RoundToInt(shortfall * CommanderSettings.OperationsRecipeArmour / (float)platoonSize);
        int carrier = Mathf.RoundToInt(shortfall * CommanderSettings.OperationsRecipeCarrier / (float)platoonSize);
        int airDefence = Mathf.Max(0, shortfall - armour - carrier);

        SetRequisition(state, mission, CommanderPlatoonRole.Armour, armour);
        SetRequisition(state, mission, CommanderPlatoonRole.Carrier, carrier);
        SetRequisition(state, mission, CommanderPlatoonRole.AirDefence, airDefence);
    }

    /// <summary>
    /// Closes out requisitions older than <see cref="RequisitionTimeoutSeconds"/> (proceed with
    /// what the mission has, or dissolve an empty forward base outright), then refreshes every live
    /// mission's lines. Called once per HQ per review, after <c>AssignPlatoons</c> so a line reflects
    /// this review's actual assignment rather than the previous review's (B1 fix reordered
    /// <c>AssignPlatoons</c> ahead of <c>UpdateAttacks</c>, which moved this call after it too).
    /// </summary>
    private static void PostRequisitions(FactionHQ hq, OperationsState state)
    {
        for (int i = state.Requisitions.Count - 1; i >= 0; i--)
        {
            CommanderRequisition requisition = state.Requisitions[i];
            if (Time.time - requisition.OpenedAt < RequisitionTimeoutSeconds)
            {
                continue;
            }

            state.Requisitions.RemoveAt(i);
            CommanderOperationsMission? mission = requisition.Mission;
            if (mission == null)
            {
                continue;
            }

            if (mission.Kind == CommanderMissionKind.ForwardBase && mission.Assigned.Count == 0)
            {
                state.Missions.Remove(mission);
                CommanderAiLog.Note(hq, $"{mission.Label}: gives up waiting and stands the mission down.");
            }
            else
            {
                CommanderAiLog.Note(hq, $"{mission.Label}: proceeds with what it has.");
            }
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.ForwardBase)
            {
                PostForwardBaseRequisitions(state, mission);
            }
            else if (mission.Kind == CommanderMissionKind.Attack)
            {
                PostAttackRequisitions(state, mission);
            }
        }

        PostWithdrawingRequisitions(state);
    }

    /// <summary>
    /// Mission-less order-book lines: one aggregate line per role for every platoon's empty recipe
    /// slots (whatever its state — a withdrawing platoon has no mission, and a platoon that formed
    /// one short has a mission that never asked for the sixth vehicle) plus the standing reserve
    /// (<c>ReservePlatoons</c> full platoons with no job). <see cref="ReinforcePlatoon"/> is what
    /// physically hands a claimed vehicle to a specific platoon once one is bought; new vehicles
    /// beyond that sit in the pool until the recipe fill forms the next platoon from them.
    /// </summary>
    private static void PostWithdrawingRequisitions(OperationsState state)
    {
        int wantedArmour = 0;
        int wantedCarrier = 0;
        int wantedAirDefence = 0;
        int reservePlatoons = 0;
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            // Every platoon short of its recipe asks for the difference, whatever its state. The
            // withdrawing-only version left a platoon that formed at 5/6 (the pool ran dry one
            // vehicle short) at 5/6 for the rest of the match: nothing asked for the sixth.
            if (platoon.Mission == null || platoon.Mission.Kind == CommanderMissionKind.Reserve)
            {
                reservePlatoons++;
            }

            int haveArmour = 0;
            int haveCarrier = 0;
            int haveAirDefence = 0;
            for (int m = 0; m < platoon.Members.Count; m++)
            {
                switch (CommanderPlatoonRoles.Of(platoon.Members[m]?.definition as VehicleDefinition))
                {
                    case CommanderPlatoonRole.Armour:
                        haveArmour++;
                        break;
                    case CommanderPlatoonRole.Carrier:
                        haveCarrier++;
                        break;
                    case CommanderPlatoonRole.AirDefence:
                        haveAirDefence++;
                        break;
                }
            }

            wantedArmour += Mathf.Max(0, CommanderSettings.OperationsRecipeArmour - haveArmour);
            wantedCarrier += Mathf.Max(0, CommanderSettings.OperationsRecipeCarrier - haveCarrier);
            wantedAirDefence += Mathf.Max(0, CommanderSettings.OperationsRecipeAirDefence - haveAirDefence);
        }

        // A standing reserve: the commander always wants ReservePlatoons full platoons with no job
        // beyond the missions it has open, so the order book is never empty while it is under
        // strength and the buyer keeps forming platoons rather than falling back to the old
        // plan-based trickle. Vehicles already idle in the pool count toward it.
        int reserveShort = Mathf.Max(0, CommanderSettings.OperationsReservePlatoons - reservePlatoons);
        if (reserveShort > 0)
        {
            int pooled = state.Pool.Count;
            int perPlatoon = Mathf.Max(1, CommanderSettings.OperationsPlatoonSize);
            int reserveVehicles = Mathf.Max(0, reserveShort * perPlatoon - pooled);
            float share = reserveVehicles / (float)perPlatoon;
            wantedArmour += Mathf.CeilToInt(CommanderSettings.OperationsRecipeArmour * share);
            wantedCarrier += Mathf.CeilToInt(CommanderSettings.OperationsRecipeCarrier * share);
            wantedAirDefence += Mathf.CeilToInt(CommanderSettings.OperationsRecipeAirDefence * share);
        }

        SetRequisition(state, null, CommanderPlatoonRole.Armour, wantedArmour);
        SetRequisition(state, null, CommanderPlatoonRole.Carrier, wantedCarrier);
        SetRequisition(state, null, CommanderPlatoonRole.AirDefence, wantedAirDefence);
    }

    /// <summary>Order-book summing over five synthetic requisitions, plus a sixth over-filled line
    /// on top of the same set.</summary>
    private static void CheckOrderBook(List<string> failures)
    {
        Expect(failures, "eight platoons and an empty book cap the plan buyer", GroundBuyingCapped(true, 8, 8, false), true);
        Expect(failures, "an open requisition lifts the cap", !GroundBuyingCapped(true, 8, 8, true), true);
        Expect(failures, "seven platoons are under the cap", !GroundBuyingCapped(true, 7, 8, false), true);
        Expect(failures, "an unmanaged commander is never capped", !GroundBuyingCapped(false, 50, 8, false), true);
        Expect(failures, "a zero cap disables the rule", !GroundBuyingCapped(true, 50, 0, false), true);
        List<CommanderRequisition> requisitions = new()
        {
            new CommanderRequisition { Role = CommanderPlatoonRole.Armour, Wanted = 3, Filled = 1 },
            new CommanderRequisition { Role = CommanderPlatoonRole.AirDefence, Wanted = 2, Filled = 2 },
            new CommanderRequisition { Role = CommanderPlatoonRole.Carrier, Wanted = 1, Filled = 0 },
            new CommanderRequisition { Role = CommanderPlatoonRole.Armour, Wanted = 2, Filled = 0 },
            new CommanderRequisition { Role = CommanderPlatoonRole.Truck, Wanted = 1, Filled = 0 },
        };
        int[] totals = new int[RoleCount];
        SumOrderBook(requisitions, totals);
        Expect(failures, "two armour lines sum into one order", totals[(int)CommanderPlatoonRole.Armour], 4);
        Expect(failures, "a filled line is off the book", totals[(int)CommanderPlatoonRole.AirDefence], 0);
        Expect(failures, "every role keeps its own line (carrier)", totals[(int)CommanderPlatoonRole.Carrier], 1);
        Expect(failures, "every role keeps its own line (truck)", totals[(int)CommanderPlatoonRole.Truck], 1);
        Expect(
            failures,
            "the buyer fills the largest open line",
            LargestOpenRole(totals),
            (int)CommanderPlatoonRole.Armour);

        requisitions.Add(new CommanderRequisition { Role = CommanderPlatoonRole.Armour, Wanted = 1, Filled = 3 });
        SumOrderBook(requisitions, totals);
        Expect(
            failures,
            "an over-filled line never subtracts from another role's order",
            totals[(int)CommanderPlatoonRole.Armour],
            4);

        int[] emptyTotals = new int[RoleCount];
        SumOrderBook(new List<CommanderRequisition>(), emptyTotals);
        Expect(
            failures,
            "an empty book leaves the plan-based counter triangle alone",
            LargestOpenRole(emptyTotals),
            -1);
    }
}
