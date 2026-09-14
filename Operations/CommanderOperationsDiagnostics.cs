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
            .Append(" heli=").Append(CountInsertionsInFlight(state))
            .Append(" platoons=").Append(state.Platoons.Count).Append(" [");
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (i > 0) diagnostics.Append("; ");
            diagnostics.Append(platoon.Name).Append(' ')
                .Append(platoon.Members.Count).Append('/').Append(platoon.Establishment).Append(' ');
            AppendComposition(diagnostics, platoon);
            diagnostics.Append(' ').Append(platoon.State).Append('@').Append(DescribeMission(platoon.Mission));
        }

        diagnostics.Append("] missions=[");
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (i > 0) diagnostics.Append("; ");
            diagnostics.Append(mission.Kind).Append(' ').Append(mission.Label)
                .Append(' ').Append(mission.Assigned.Count).Append('/').Append(mission.WantedPlatoons);
            if (mission.Kind == CommanderMissionKind.Picket)
            {
                diagnostics.Append(" pickets=").Append(mission.PicketMembers.Count);
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
