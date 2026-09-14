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
            // Before the removal, not after: a dead member is only visible to the loss stamp while
            // it still sits in the list. The stamp is what makes a holding platoon that lost a
            // vehicle count as in contact (addendum 2026-09-14 §2).
            if (HasCombatLoss(hq, state.Platoons[i].Members))
            {
                state.Platoons[i].LastLossAt = Time.time;
            }

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
    /// Pure, for the self-check. True when the ground buyer must buy nothing this review: the
    /// operations service owns this commander's ground force, so every vehicle is bought to order,
    /// and the order book has no open line to buy for (DECISION-013).
    /// <para>This replaces a platoon cap that never bound. The cap lifted the moment any requisition
    /// was open and the book never emptied — the standing reserve alone kept a line open — so the
    /// plan buyer went on buying vehicles nobody had asked for, and the player side reached 27
    /// platoons spread over a map with no enemy near most of them. Counting platoons was the wrong
    /// question: what matters is whether anything asked for the vehicle. Picket shortfalls,
    /// forward-base trucks, platoon replacements, reinforcements and the standing reserve all post
    /// lines, so a commander that needs a vehicle still gets one.</para>
    /// </summary>
    internal static bool GroundBuyingBookOnly(bool ownsGroundForce, bool hasOpenRequisition)
    {
        return ownsGroundForce && !hasOpenRequisition;
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
    /// lines it was losing "largest line" to), then the roles a purpose FACING THE ENEMY is waiting
    /// on, then everything else — which is where a picket's pair sits.
    /// <paramref name="boughtThisReview"/> is subtracted so five purchases in one review do not all
    /// chase the same line: the depot claim that credits a line only lands once the vehicle exists.
    /// The buyer takes the first role it can afford, and only falls back to its plan when it can
    /// afford none of them — before, one unaffordable "largest" role sent the whole purchase to the
    /// plan, which bought a cheap vehicle the recipe could not use.
    /// <para>The urgency split is the 2026-09-14 fix. Sorted by size alone, sixteen rear pickets'
    /// air-defence and carrier lines outweighed the armour a forward base in contact was asking
    /// for, so every vehicle bought went to a quiet crossroads while the front stayed one platoon
    /// deep.</para>
    /// </summary>
    internal static void OpenRolesByPriority(FactionHQ hq, int[] boughtThisReview, List<CommanderPlatoonRole> roles)
    {
        roles.Clear();
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        int[] urgent = new int[RoleCount];
        int[] other = new int[RoleCount];
        SumOrderBookByUrgency(state, urgent, other);

        // What has already been bought this review pays off the urgent lines first, for the same
        // reason they are bought first: the vehicle on its way belongs to the threatened purpose.
        for (int i = 0; i < RoleCount && i < boughtThisReview.Length; i++)
        {
            int bought = Mathf.Max(0, boughtThisReview[i]);
            int fromUrgent = Mathf.Min(urgent[i], bought);
            urgent[i] -= fromUrgent;
            other[i] = Mathf.Max(0, other[i] - (bought - fromUrgent));
        }

        OrderOpenRoles(urgent, other, roles);
    }

    /// <summary>
    /// The buy order for one purchase, from the two halves of the order book: an open
    /// <c>Truck</c> line first whichever half it is in, then the urgent roles largest line first,
    /// then every other open role largest line first. Each role appears once, at its best position.
    /// Pure, for the self-check.
    /// </summary>
    internal static void OrderOpenRoles(
        IReadOnlyList<int> urgentTotals, IReadOnlyList<int> otherTotals, List<CommanderPlatoonRole> roles)
    {
        roles.Clear();
        bool[] taken = new bool[RoleCount];
        if (OpenTotal(urgentTotals, CommanderPlatoonRole.Truck) + OpenTotal(otherTotals, CommanderPlatoonRole.Truck) > 0)
        {
            roles.Add(CommanderPlatoonRole.Truck);
            taken[(int)CommanderPlatoonRole.Truck] = true;
        }

        AppendLargestFirst(urgentTotals, taken, roles);
        AppendLargestFirst(otherTotals, taken, roles);
    }

    /// <summary>One role's open total out of a totals array, bounds-checked and never negative.</summary>
    private static int OpenTotal(IReadOnlyList<int> totals, CommanderPlatoonRole role)
    {
        int index = (int)role;
        return index >= 0 && index < totals.Count ? Mathf.Max(0, totals[index]) : 0;
    }

    /// <summary>Appends every role with an open line in <paramref name="totals"/>, largest first,
    /// skipping the ones an earlier pass already placed. First index wins a tie, so the answer is
    /// stable review to review — the <see cref="LargestOpenRole(IReadOnlyList{int})"/> convention.</summary>
    private static void AppendLargestFirst(IReadOnlyList<int> totals, bool[] taken, List<CommanderPlatoonRole> roles)
    {
        while (true)
        {
            int best = -1;
            for (int i = 0; i < totals.Count && i < taken.Length; i++)
            {
                if (taken[i] || totals[i] <= 0)
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
            taken[best] = true;
        }
    }

    /// <summary>
    /// Splits the order book into the lines a purpose facing the enemy is waiting on and everything
    /// else. A line with no mission of its own — the standing reserve and the platoon top-ups — is
    /// never urgent: it is what the commander would like, not what the enemy is forcing.
    /// </summary>
    private static void SumOrderBookByUrgency(OperationsState state, int[] urgent, int[] other)
    {
        for (int i = 0; i < urgent.Length && i < other.Length; i++)
        {
            urgent[i] = 0;
            other[i] = 0;
        }

        for (int i = 0; i < state.Requisitions.Count; i++)
        {
            CommanderRequisition requisition = state.Requisitions[i];
            int role = (int)requisition.Role;
            if (role < 0 || role >= urgent.Length)
            {
                continue;
            }

            int open = Mathf.Max(0, requisition.Wanted - requisition.Filled);
            if (IsThreatenedPurpose(state, requisition.Mission))
            {
                urgent[role] += open;
            }
            else
            {
                other[role] += open;
            }
        }
    }

    /// <summary>A requisition raised by a purpose facing the enemy: any attack, or a forward base on
    /// a front point under a threat mark or in contact. One definition of "facing the enemy" shared
    /// with the pool order (<c>IsThreatenedFrontPoint</c>, Reuse rule 4).</summary>
    private static bool IsThreatenedPurpose(OperationsState state, CommanderOperationsMission? mission)
    {
        if (mission == null)
        {
            return false;
        }

        return mission.Kind == CommanderMissionKind.Attack
            || (mission.Kind == CommanderMissionKind.ForwardBase && IsThreatenedFrontPoint(state, mission));
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

    /// <summary>
    /// Pure, for the self-check. How a picket's shortfall is written on the order book
    /// (DECISION-013: pickets are the capture mechanic, so the buyer must be able to buy picket
    /// vehicles when the pool is empty). The insertion load's own doctrine, in role form: the first
    /// vehicle is air defence — a point away from the front is threatened by aircraft, not armour —
    /// and every further one is a carrier, the cheapest non-air-defence combat role in the recipe
    /// and the only one that can actually take a point, which is the whole of a picket's job. The
    /// buyer's <c>ChooseForRole</c> still picks the cheapest definition inside the role.
    /// </summary>
    internal static void PicketRequisitionRoles(int shortfall, out int airDefence, out int carrier)
    {
        int wanted = Mathf.Max(0, shortfall);
        airDefence = Mathf.Min(1, wanted);
        carrier = wanted - airDefence;
    }

    /// <summary>A picket short of <c>PointsMinGarrison</c> posts its shortfall so the buyer fills it
    /// when the pool has nothing to give (design Section 1). A filled picket posts a zero want,
    /// which clears the line outright — the truck line's own convention.</summary>
    private static void PostPicketRequisitions(OperationsState state, CommanderOperationsMission mission)
    {
        int shortfall = Mathf.Max(0, CommanderSettings.PointsMinGarrison - mission.PicketMembers.Count);
        PicketRequisitionRoles(shortfall, out int airDefence, out int carrier);
        SetRequisition(state, mission, CommanderPlatoonRole.AirDefence, airDefence);
        SetRequisition(state, mission, CommanderPlatoonRole.Carrier, carrier);
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

            // A picket's line is a standing want, not a one-off request: it is re-posted a few lines
            // below with a fresh clock for as long as the point is short. Saying "proceeds with what
            // it has" every five minutes for every empty picket on the map is noise nobody reads.
            if (mission.Kind == CommanderMissionKind.Picket)
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
            else if (mission.Kind == CommanderMissionKind.Picket)
            {
                PostPicketRequisitions(state, mission);
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
        Expect(failures, "an owned ground force with an empty order book buys nothing", GroundBuyingBookOnly(true, false), true);
        Expect(failures, "an owned ground force with an open line buys", !GroundBuyingBookOnly(true, true), true);
        Expect(failures, "a force this service does not own still runs the plan buyer", !GroundBuyingBookOnly(false, false), true);
        Expect(failures, "a force this service does not own is never held by the book either", !GroundBuyingBookOnly(false, true), true);

        PicketRequisitionRoles(2, out int picketAirDefence, out int picketCarrier);
        Expect(failures, "an empty picket orders one air-defence vehicle", picketAirDefence, 1);
        Expect(failures, "an empty picket orders one carrier beside it", picketCarrier, 1);
        PicketRequisitionRoles(1, out picketAirDefence, out picketCarrier);
        Expect(failures, "a picket one short orders the air-defence vehicle first", picketAirDefence, 1);
        Expect(failures, "a picket one short orders no carrier", picketCarrier, 0);
        PicketRequisitionRoles(0, out picketAirDefence, out picketCarrier);
        Expect(failures, "a filled picket orders no air defence", picketAirDefence, 0);
        Expect(failures, "a filled picket orders no carrier", picketCarrier, 0);
        PicketRequisitionRoles(-1, out picketAirDefence, out picketCarrier);
        Expect(failures, "an over-filled picket never orders a negative number of vehicles", picketAirDefence + picketCarrier, 0);
        PicketRequisitionRoles(4, out picketAirDefence, out picketCarrier);
        Expect(failures, "a larger garrison setting still orders exactly one air-defence vehicle", picketAirDefence, 1);
        Expect(failures, "a larger garrison setting orders the rest as carriers", picketCarrier, 3);
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

        CheckBuyOrder(failures);
    }

    /// <summary>
    /// The buy order the 2026-09-14 fix installed: truck, then the roles a threatened purpose is
    /// waiting on, then the picket and reserve lines — however much larger those are.
    /// </summary>
    private static void CheckBuyOrder(List<string> failures)
    {
        int[] urgent = new int[RoleCount];
        int[] quiet = new int[RoleCount];
        List<CommanderPlatoonRole> order = new();

        // A forward base in contact wants three tanks; sixteen rear pickets want sixteen of
        // everything else. The tanks must still be bought first.
        urgent[(int)CommanderPlatoonRole.Armour] = 3;
        quiet[(int)CommanderPlatoonRole.AirDefence] = 16;
        quiet[(int)CommanderPlatoonRole.Carrier] = 16;
        OrderOpenRoles(urgent, quiet, order);
        Expect(failures, "a threatened purpose's armour outranks every picket line", order.Count > 0 ? (int)order[0] : -1, (int)CommanderPlatoonRole.Armour);
        Expect(failures, "the picket lines are still bought, behind the threatened purpose", order.Count, 3);

        // The truck leads whichever half of the book it sits in.
        quiet[(int)CommanderPlatoonRole.Truck] = 1;
        OrderOpenRoles(urgent, quiet, order);
        Expect(failures, "an open truck line still leads the whole order", order.Count > 0 ? (int)order[0] : -1, (int)CommanderPlatoonRole.Truck);
        Expect(failures, "the threatened purpose follows the truck", order.Count > 1 ? (int)order[1] : -1, (int)CommanderPlatoonRole.Armour);

        // Nothing threatened: the book falls back to largest line first, as it always did.
        System.Array.Clear(urgent, 0, urgent.Length);
        quiet[(int)CommanderPlatoonRole.Truck] = 0;
        OrderOpenRoles(urgent, quiet, order);
        Expect(failures, "with nothing threatened the largest line leads", order.Count > 0 ? (int)order[0] : -1, (int)CommanderPlatoonRole.Carrier);

        // A role wanted by both halves appears once, at its urgent position.
        urgent[(int)CommanderPlatoonRole.Carrier] = 1;
        OrderOpenRoles(urgent, quiet, order);
        Expect(failures, "a role wanted by both halves leads once", order.Count > 0 ? (int)order[0] : -1, (int)CommanderPlatoonRole.Carrier);
        Expect(failures, "a role wanted by both halves is never listed twice", order.Count, 2);

        System.Array.Clear(urgent, 0, urgent.Length);
        System.Array.Clear(quiet, 0, quiet.Length);
        OrderOpenRoles(urgent, quiet, order);
        Expect(failures, "an empty book asks the buyer for no role at all", order.Count, 0);
    }
}
