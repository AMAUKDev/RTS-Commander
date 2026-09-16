using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// One line per commander per review into the BepInEx log describing the whole operations state,
/// plus a line per platoon state change. The COMMANDER LOG window shows decisions; this shows the
/// machine behind them, so a match can be debugged from <c>LogOutput.log</c> alone without a
/// second run. Off by a Gameplay setting once the doctrine has settled.
/// </summary>
internal sealed partial class CommanderOperationsService
{
    /// <summary>Last state seen per platoon, so a transition is logged once, in one place, rather
    /// than at every site that writes <c>State</c>.</summary>
    private readonly Dictionary<CommanderPlatoon, CommanderPlatoonState> lastLoggedState = new();

    /// <summary>Platoons any commander's review has listed since the last prune, so a platoon that
    /// no longer exists anywhere can leave <see cref="lastLoggedState"/>.</summary>
    private readonly HashSet<CommanderPlatoon> diagnosticsLivePlatoons = new();

    /// <summary>Reviews between prunes of the state table: four, two commanders' reviews twice
    /// over, so a platoon is never dropped between one commander's review and the other's.</summary>
    private const int DiagnosticsPruneEveryReviews = 4;

    private int diagnosticsPruneCountdown;
    private readonly List<CommanderPlatoon> diagnosticsPruneScratch = new();
    private readonly StringBuilder diagnostics = new();
    private readonly int[] diagnosticsTotals = new int[RoleCount];

    private void LogReviewDiagnostics(FactionHQ hq, OperationsState state)
    {
        if (!CommanderSettings.OperationsDebugLog || hq.faction == null)
        {
            return;
        }

        string label = CommanderPlayerCommanderService.CommanderLabel(hq);

        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (lastLoggedState.TryGetValue(platoon, out CommanderPlatoonState previous) && previous != platoon.State)
            {
                CommanderPlugin.Log.LogInfo(
                    $"Ops {label}: {platoon.Name} {previous} -> {platoon.State} ({DescribeMission(platoon.Mission)}, "
                        + $"{platoon.Members.Count}/{platoon.Establishment}).");
            }

            lastLoggedState[platoon] = platoon.State;
            diagnosticsLivePlatoons.Add(platoon);
        }

        // Platoons that have been dissolved leave the table (fix, 2026-09-15): the table is shared by
        // every commander, so a platoon is dropped once neither commander's review has listed it.
        if (++diagnosticsPruneCountdown >= DiagnosticsPruneEveryReviews)
        {
            diagnosticsPruneCountdown = 0;
            diagnosticsPruneScratch.Clear();
            foreach (CommanderPlatoon platoon in lastLoggedState.Keys)
            {
                if (!diagnosticsLivePlatoons.Contains(platoon))
                {
                    diagnosticsPruneScratch.Add(platoon);
                }
            }

            for (int i = 0; i < diagnosticsPruneScratch.Count; i++)
            {
                lastLoggedState.Remove(diagnosticsPruneScratch[i]);
            }

            diagnosticsPruneScratch.Clear();
            diagnosticsLivePlatoons.Clear();
        }

        int front = 0, rear = 0, threat = 0;
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            if (ranked.IsFront) front++; else rear++;
            if (ranked.HasThreatMark) threat++;
        }

        SumOrderBook(state.Requisitions, diagnosticsTotals);

        diagnostics.Length = 0;
        diagnostics.Append("Ops ").Append(label)
            .Append(" review: pool=").Append(state.Pool.Count)
            .Append(" staged=").Append(state.PoolIssued.Count)
            // Insertion flights in the air for this commander (design.md,
            // heli-picket-insertion_20260913, Section 4). The air-support summary rides the end of
            // this same line via DescribeAir below; keep this field beside the ground counts.
            // Flights airborne over the airborne limit (pickets-first Section 4): the limit moved
            // from one to three, so the bare count no longer says whether the commander is holding
            // back because it is full or because nothing asked.
            .Append(" heli=").Append(CountInsertionsInFlight(state))
            .Append('/').Append(CommanderSettings.OperationsHeliInsertionLimit)
            .Append(" platoons=").Append(state.Platoons.Count);

        // The open lift order (fob-construction_20260914 Section 5; renamed from fob= when the order
        // gained a purpose, air-mobile-platoons_20260915 Section 4), beside the flight count it
        // shares an airborne limit with. Nothing at all when no order is open, so a quiet review
        // line stays as short as it was.
        string lift = DescribeFob(hq, state);
        if (lift.Length > 0)
        {
            diagnostics.Append(" lift=").Append(lift);
        }

        // How much of the board this commander can actually send ground vehicles to
        // (reach-and-points Section 2): points within DepotReachMeters of one of its own depots,
        // over the points it ranked at all. A number well below the total is what a forward
        // operating base is built to raise, so the two fields read together.
        diagnostics.Append(" reach=").Append(state.RankedPoints.Count - state.OutOfReach.Count)
            .Append('/').Append(state.RankedPoints.Count);

        // What the commander has a REASON to field, beside what it actually fields (fix,
        // 2026-09-14): a platoon count stuck below the purpose count is a commander that cannot
        // afford to grow, and one stuck at it is a commander that has no reason to — and the review
        // line could not tell the two apart, which is how a whole match went by with one platoon.
        CountPurposes(state, out int purposeForwardBases, out int purposeAttacks, out int purposeReserve);
        diagnostics.Append(" purposes=").Append(purposeForwardBases)
            .Append('/').Append(purposeAttacks)
            .Append('/').Append(purposeReserve)
            .Append(" [");
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (i > 0) diagnostics.Append("; ");
            diagnostics.Append(platoon.Name).Append(' ')
                .Append(platoon.Members.Count).Append('/').Append(platoon.Establishment).Append(' ');
            AppendComposition(diagnostics, platoon);
            diagnostics.Append(' ').Append(platoon.State);
            // ground-tactics §5: how the platoon is deployed, not only what it is for — a platoon
            // reading "Holding" and a platoon reading "Holding/DefenceArc" are standing on very
            // different ground. Ring is the ordinary case and says nothing.
            if (platoon.Posture != CommanderGroundPosture.Ring)
            {
                diagnostics.Append('/').Append(platoon.Posture);
            }

            diagnostics.Append('@').Append(DescribeMission(platoon.Mission));
        }

        diagnostics.Append("] missions=[");
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (i > 0) diagnostics.Append("; ");
            diagnostics.Append(mission.Kind).Append(' ').Append(mission.Label)
                .Append(' ').Append(mission.Assigned.Count).Append('/').Append(mission.WantedPlatoons);
            // Addendum 2026-09-14 §3: an open reinforcement request shows as its own wanted count,
            // so a mission reads "1/3 +2 reinf" while two of the three are the request.
            if (mission.ReinforcePlatoons > 0)
            {
                diagnostics.Append(" +").Append(mission.ReinforcePlatoons).Append(" reinf");
            }
            if (mission.Kind == CommanderMissionKind.Picket)
            {
                diagnostics.Append(" pickets=").Append(mission.PicketMembers.Count);
                // Reserved for the transport this review (departure 2026-09-14): the drive fill
                // skipped it and it posted no requisition, so a reader seeing pickets=0 has to be
                // able to tell "waiting for its flight" from "nothing idle to give it".
                if (IsAirDeliveredPicket(state, mission.Point))
                {
                    diagnostics.Append(" air");
                }
            }
            if (mission.Kind == CommanderMissionKind.ForwardBase)
            {
                diagnostics.Append(mission.Truck != null ? " truck" : " no-truck");
            }
            if (mission.Kind == CommanderMissionKind.Attack)
            {
                int arrived = 0, manned = 0;
                for (int a = 0; a < mission.Axes.Count; a++)
                {
                    if (mission.Axes[a].Platoon != null) manned++;
                    if (mission.Axes[a].Arrived) arrived++;
                }
                diagnostics.Append(" axes=").Append(mission.Axes.Count)
                    .Append(" manned=").Append(manned).Append(" arrived=").Append(arrived)
                    .Append(mission.Launched ? " LAUNCHED" : " forming");
            }
        }

        diagnostics.Append("] points front=").Append(front).Append(" rear=").Append(rear).Append(" threat=").Append(threat)
            .Append(" pressure=").Append(state.Pressure.ToString("0.0")).Append('/')
            .Append(CommanderSettings.OperationsPressureIntervalMinutes.ToString("0"))
            .Append(" book: armour=").Append(diagnosticsTotals[(int)CommanderPlatoonRole.Armour])
            .Append(" carrier=").Append(diagnosticsTotals[(int)CommanderPlatoonRole.Carrier])
            .Append(" ad=").Append(diagnosticsTotals[(int)CommanderPlatoonRole.AirDefence])
            .Append(" truck=").Append(diagnosticsTotals[(int)CommanderPlatoonRole.Truck])
            // What already covered those lines at the buyer's last pass — bought that review,
            // banked as depot supply, idle in the pool (fix A, 2026-09-15). A book that reads
            // open beside a covered figure at least as large is a commander that is not buying
            // because it already has the vehicles, not because it cannot afford them.
            .Append(" covered: armour=").Append(state.CoveredByRole[(int)CommanderPlatoonRole.Armour])
            .Append(" carrier=").Append(state.CoveredByRole[(int)CommanderPlatoonRole.Carrier])
            .Append(" ad=").Append(state.CoveredByRole[(int)CommanderPlatoonRole.AirDefence])
            .Append(" truck=").Append(state.CoveredByRole[(int)CommanderPlatoonRole.Truck])
            .Append(" reqs=").Append(state.Requisitions.Count);

        // Air-support design SS4: the sortie summary rides the same review line.
        DescribeAir(state, diagnostics);

        CommanderPlugin.Log.LogInfo(diagnostics.ToString());
    }

    private static string DescribeMission(CommanderOperationsMission? mission)
    {
        return mission == null ? "none" : $"{mission.Kind} {mission.Label}";
    }

    /// <summary>Called from the depot claim so a vehicle entering the pool is visible in the log.</summary>
    private static void LogClaim(FactionHQ hq, Unit unit, int poolSize)
    {
        if (CommanderSettings.OperationsDebugLog && hq.faction != null)
        {
            CommanderPlugin.Log.LogInfo(
                $"Ops {CommanderPlayerCommanderService.CommanderLabel(hq)}: claimed {CommanderGameAccess.GetUnitLabel(unit)} into the pool ({poolSize} idle).");
        }
    }

    /// <summary>
    /// Called from <c>StagePool</c> the first time each pooled vehicle is given a standing post, so
    /// the log shows that a claimed vehicle is actually being driven by this commander rather than
    /// by the Basegame's own walk-at-the-enemy AI. Silent once the pool has settled.
    /// </summary>
    private static void LogStaged(FactionHQ hq, int newlyStaged, int poolSize)
    {
        if (newlyStaged > 0 && CommanderSettings.OperationsDebugLog && hq.faction != null)
        {
            CommanderPlugin.Log.LogInfo(
                $"Ops {CommanderPlayerCommanderService.CommanderLabel(hq)}: staged {newlyStaged} pooled vehicle(s) "
                    + $"on the reserve ring ({poolSize} idle).");
        }
    }

    /// <summary>One line when a marching platoon first deploys on contact.</summary>
    private static void LogContact(FactionHQ hq, CommanderPlatoon platoon, float distanceMeters)
    {
        if (!CommanderSettings.OperationsDebugLog)
        {
            return;
        }

        CommanderAiLog.Note(hq, $"{platoon.Name} in contact: hostile at {distanceMeters:0} m, deploys into line.");
    }

    /// <summary>
    /// One line when a platoon standing on its posts first comes under attack (addendum
    /// 2026-09-14 §2) — the detection-only contact, logged where the marching line's is so a match
    /// can be debugged from the log alone. A negative distance means the evidence was a member
    /// loss with nothing tracked. Debug-gated, like <see cref="LogContact"/>.
    /// </summary>
    private static void LogPostContact(FactionHQ hq, CommanderPlatoon platoon, float distanceMeters)
    {
        if (!CommanderSettings.OperationsDebugLog)
        {
            return;
        }

        CommanderAiLog.Note(
            hq,
            distanceMeters >= 0f
                ? $"{platoon.Name} in contact at its post: hostile at {distanceMeters:0} m, holds its posts and calls for air support."
                : $"{platoon.Name} in contact at its post: a member was lost, holds its posts and calls for air support.");
    }

    /// <summary>
    /// One line when a garrison forms its defence arc (ground-tactics §2) and one when it gives it
    /// up. Not debug-gated: a platoon changing how it is deployed is a commander decision, and the
    /// COMMANDER LOG is where the design's own acceptance test reads it.
    /// </summary>
    private static void LogDefenceArc(FactionHQ hq, CommanderPlatoon platoon, float bearingDegrees, string label)
    {
        CommanderAiLog.Note(hq, $"{platoon.Name} forms a defence arc toward {bearingDegrees:0}° at {label}.");
    }

    /// <summary>The other half of <see cref="LogDefenceArc"/>: the fight is over and the platoon
    /// spreads back out over the whole point.</summary>
    private static void LogReturnsToRing(FactionHQ hq, CommanderPlatoon platoon)
    {
        CommanderAiLog.Note(hq, $"{platoon.Name} returns to the ring.");
    }

    /// <summary>One line the first time a platoon leaves the road network for cross-country bounds
    /// (ground-tactics §3) — once per advance, not once per bound.</summary>
    private static void LogBounding(FactionHQ hq, CommanderPlatoon platoon, string label)
    {
        CommanderAiLog.Note(
            hq,
            $"{platoon.Name} leaves the road at the release point; bounding to {label} in "
                + $"{CommanderSettings.BoundMeters:0} m steps.");
    }

    /// <summary>One line when a reinforcement sent to a point that is already held goes round the
    /// attack instead of joining the ring (ground-tactics §4).</summary>
    private static void LogCounterAttack(FactionHQ hq, CommanderPlatoon platoon, string label)
    {
        CommanderAiLog.Note(hq, $"{platoon.Name} counter-attacks from the flank at {label}.");
    }

    /// <summary>One line when a reinforcement takes up a screen instead, with nothing tracked to
    /// counter-attack (ground-tactics §4).</summary>
    private static void LogScreen(FactionHQ hq, CommanderPlatoon platoon, string label)
    {
        CommanderAiLog.Note(hq, $"{platoon.Name} screens the approach to {label}.");
    }

    /// <summary>One line when the garrison has dropped below the minimum and the reinforcement
    /// gives up its manoeuvre to stand on the point itself (ground-tactics §4).</summary>
    private static void LogFoldsIntoRing(FactionHQ hq, CommanderPlatoon platoon, string label)
    {
        CommanderAiLog.Note(hq, $"{platoon.Name} folds into the ring at {label}: the garrison is below strength.");
    }

    /// <summary>Role tally of a platoon as <c>A3/C1/D2</c> (armour / carrier / air defence), with
    /// <c>/T</c> or <c>/O</c> appended only when a truck or an unclassified vehicle is inside — the
    /// recipe check the review line was missing.</summary>
    private static void AppendComposition(System.Text.StringBuilder into, CommanderPlatoon platoon)
    {
        int armour = 0, carrier = 0, airDefence = 0, truck = 0, other = 0;
        for (int i = 0; i < platoon.Members.Count; i++)
        {
            switch (CommanderPlatoonRoles.Of(platoon.Members[i]?.definition as VehicleDefinition))
            {
                case CommanderPlatoonRole.Armour: armour++; break;
                case CommanderPlatoonRole.Carrier: carrier++; break;
                case CommanderPlatoonRole.AirDefence: airDefence++; break;
                case CommanderPlatoonRole.Truck: truck++; break;
                default: other++; break;
            }
        }

        into.Append('A').Append(armour).Append("/C").Append(carrier).Append("/D").Append(airDefence);
        if (truck > 0) into.Append("/T").Append(truck);
        if (other > 0) into.Append("/O").Append(other);
    }
}
