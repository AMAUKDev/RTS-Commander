using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Re-raising a group of ground vehicles that is still driving from the depot it was bought at, once
/// a nearer depot has appeared behind it (user instruction 2026-09-16: "we have situations where
/// platoons are bought and sent to the enemy, but often a FOB is built closer to the enemy long
/// before they're anywhere near. when that happens, the platoon should despawn and then be
/// re-spawned from that FOB", extended the same day: "platoon re-raising should also apply to
/// pickets travelling by ground").
/// <para>ONE rule and one action with two callers (Reuse rule 4). A marching platoon
/// (<see cref="CommanderPlatoon"/>) and a driving picket detachment
/// (<see cref="CommanderOperationsMission.PicketMembers"/>) are both a list of ground vehicles with
/// somewhere to be; the margin, the "still far out" test, the never-when-fighting guard, the churn
/// guard and the money accounting are shared, and the only differences are what is being moved and
/// what the log line calls it.</para>
/// <para>This is the ground twin of the air-mobile gate in
/// <c>Operations/CommanderOperationsFront.cs</c>. That gate asks "is this objective too far to drive
/// to at all?" when a group is RAISED; this one asks "is the drive that is left worse than starting
/// again from the depot that has just appeared?" while it is under way. Both read the same
/// <see cref="DriveMinutes"/>, the same road detour and ground speed settings, and the same
/// <c>CommanderSettings.AirMobileDriveMinutes</c> idea of "far" — there is deliberately no second
/// notion of a long drive in the mod.</para>
/// </summary>
internal sealed partial class CommanderOperationsService
{
    /// <summary>
    /// How long after one re-raise the same group may be re-raised again: thirty minutes, sixty
    /// reviews. The churn this stops is real — a forward operating base coming online is exactly the
    /// event that makes the rule fire, and a second base opening minutes later would dissolve the
    /// same group again before the first re-raise had put a single vehicle back on the road.
    /// <para>Raised from ten minutes to thirty (user decision 2026-09-16) on live evidence: the rule
    /// fired 230 times across 24 groups in one match, about ten re-raises per group. Every single
    /// move was a real saving — `re-raises 10TH PLATOON from Maris Airport: 44 min from there against
    /// 71 min still to drive from FOB ROAD POINT 12` — so the rule was working as designed, but the
    /// result was platoons shuffled between depots for most of the match instead of ever arriving.
    /// Thirty minutes is longer than almost any single drive the rule is asked about, so a group that
    /// has been moved once gets to finish its journey.</para>
    /// <para>Whatever this is set to, it must stay comfortably longer than the five-minute deployment
    /// reservation the returned vehicles wait on, or a group could be re-raised while it is still
    /// being re-raised. The named check below pins that.</para>
    /// </summary>
    private const float ReseatCooldownSeconds = 1800f;

    /// <summary>
    /// How long the vehicles a re-raise handed back to a depot count as still on their way: the same
    /// <see cref="CommanderFactionVehicleService.DeploymentReservationMinutes"/> window the game's
    /// own deployment loop is steered by (Reuse rule 4 — one definition of how long a reserved
    /// vehicle is owed to a depot). Past it the reservation has expired and whatever has not come
    /// back is not coming back.
    /// </summary>
    private static float ReseatInTransitSeconds =>
        CommanderFactionVehicleService.DeploymentReservationMinutes * 60f;

    /// <summary>
    /// Whether re-raising a group of driving vehicles from a nearer depot is worth it, pure (user
    /// instruction 2026-09-16, above). The one rule both callers ask.
    /// <para>Four terms, all of which must hold. The saving — what is left of the drive against what
    /// the drive from the new depot would be — must EXCEED <paramref name="savingMinutes"/>, so a
    /// gain exactly on the margin leaves the group alone; that is the patient side of the boundary,
    /// the convention <see cref="PlatoonFlies"/> and <see cref="GroundLineAllowed"/> already keep. The
    /// group must still be a long way out, measured on the one threshold the mod already has for a
    /// drive that is too long (<paramref name="farMinutes"/>), which is also what keeps a group at or
    /// near its objective out of this entirely. And a group that is fighting, one that is falling
    /// back, or one that has been re-raised recently is never dissolved.</para>
    /// <para>The fifth term is the strict-progress guard (user decision 2026-09-16): the new depot
    /// must be strictly nearer the objective than the depot the last re-raise moved this group to
    /// (<paramref name="lastDepotMinutes"/>). Every re-raise therefore shortens the remaining
    /// journey, so a group can never be sent back and forth between two depots that are each nearer
    /// than the other depending on where it happens to stand. The thirty-minute cooldown slows such a
    /// loop; this makes it impossible, and the two guards are kept because they do different jobs —
    /// one limits how OFTEN a group is moved, the other limits WHERE it may be moved to.
    /// <see cref="float.MaxValue"/> means no previous re-raise on this journey, which any depot
    /// beats.</para>
    /// </summary>
    internal static bool ShouldReseatGroup(
        float remainingDriveMinutes,
        float depotDriveMinutes,
        float savingMinutes,
        float farMinutes,
        bool inContact,
        bool withdrawing,
        bool onCooldown,
        float lastDepotMinutes)
    {
        if (inContact || withdrawing || onCooldown)
        {
            return false;
        }

        if (remainingDriveMinutes <= farMinutes)
        {
            return false;
        }

        // Strictly nearer, not merely as near: a re-raise to a depot the same distance out buys
        // nothing and is exactly the second half of a two-depot loop.
        if (depotDriveMinutes >= lastDepotMinutes)
        {
            return false;
        }

        return remainingDriveMinutes - depotDriveMinutes > savingMinutes;
    }

    /// <summary>
    /// What <see cref="CommanderReseatRecord.LastDepotMinutes"/> holds when a group has no previous
    /// re-raise on its current journey: no depot is that far away, so any of them beats it. Named
    /// rather than written out at each site so the self-check and the record agree by construction.
    /// </summary>
    internal const float ReseatNoPreviousDepot = float.MaxValue;

    /// <summary>What a record's strict-progress memory reads after a group is given a new objective,
    /// for the self-check: one re-raise recorded, then forgotten. Exercises the real
    /// <see cref="CommanderReseatRecord.ResetForNewObjective"/> rather than asserting a literal, so a
    /// future edit that changes what "forgotten" means fails at load.</summary>
    private static float ReseatRecordAfterNewObjective(float lastDepotMinutes)
    {
        CommanderReseatRecord record = new() { LastDepotMinutes = lastDepotMinutes };
        record.ResetForNewObjective();
        return record.LastDepotMinutes;
    }

    /// <summary>
    /// The churn guard, pure: whether a group re-raised at <paramref name="reseatAt"/> is still
    /// inside its cooldown. A group that has never been re-raised (a negative stamp) is never on
    /// cooldown, and exactly on the cooldown it is free again — the same limit-inclusive convention
    /// the mod's other waits keep.
    /// </summary>
    internal static bool OnReseatCooldown(float reseatAt, float now, float cooldownSeconds)
    {
        return reseatAt >= 0f && now - reseatAt < cooldownSeconds;
    }

    /// <summary>
    /// How many of a re-raise's vehicles are still on their way back to the group, pure: whatever the
    /// re-raise handed back that has not rejoined yet, for as long as the depot reservation lasts.
    /// Zero for a group that has never been re-raised, one whose vehicles have all returned, and one
    /// whose reservation window has run out.
    /// <para>Four callers, all asking the same question — "how many vehicles does this group already
    /// own that simply are not standing here yet?". The empty-platoon rule in
    /// <see cref="AssignPlatoons"/> reads it so a platoon whose vehicles are in the gap between
    /// despawn and redeploy is not swept off the board; the forward base's order book and the
    /// picket's order book read it so the commander does not buy replacements for vehicles it has
    /// already paid for; and the picket's pool fill reads it so a re-raised picket does not
    /// immediately grab another far vehicle and drive the journey again.</para>
    /// </summary>
    internal static int ReseatVehiclesInTransit(
        int banked, int membersNow, float secondsSinceReseat, float windowSeconds)
    {
        if (banked <= 0 || secondsSinceReseat < 0f || secondsSinceReseat >= windowSeconds)
        {
            return 0;
        }

        return Mathf.Max(0, banked - Mathf.Max(0, membersNow));
    }

    /// <summary>The live reading of <see cref="ReseatVehiclesInTransit(int, int, float, float)"/> for
    /// one group, whether that group is a platoon or a picket detachment.</summary>
    internal static int ReseatVehiclesInTransit(CommanderReseatRecord record, int membersNow)
    {
        return ReseatVehiclesInTransit(
            record.Banked,
            membersNow,
            record.At < 0f ? -1f : Time.time - record.At,
            ReseatInTransitSeconds);
    }

    /// <summary>
    /// Every review: re-raises each marching platoon and each ground-driving picket detachment that a
    /// nearer depot has overtaken, and moves a re-raised platoon back out once it has gathered again
    /// at that depot.
    /// <para>Deliberately narrow on the platoon side. Only a platoon in
    /// <see cref="CommanderPlatoonState.Moving"/> on a <see cref="CommanderMissionKind.ForwardBase"/>
    /// mission is considered: an attack's platoons are tied to an axis, a release point and a form-up
    /// timeout that dissolving one would break, the reserve is already standing on its own territory,
    /// and an air-mobile mission's vehicles come by air and were never bought at a depot at all. On
    /// the picket side a point reserved for a transport helicopter is skipped for the same reason —
    /// it is not driving anywhere. Server-only, because the despawn, the vehicle supply and the depot
    /// spawn all are.</para>
    /// </summary>
    private void ReviewGroundReseats(FactionHQ hq, OperationsState state)
    {
        if (!hq.IsServer)
        {
            return;
        }

        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (platoon.Reseat.Banked > 0)
            {
                ReleaseReseatedPlatoon(hq, platoon);
                continue;
            }

            if (platoon.State != CommanderPlatoonState.Moving
                || platoon.Mission == null
                || platoon.Mission.Kind != CommanderMissionKind.ForwardBase
                || platoon.Mission.AirMobile
                || platoon.Mission.Point == null
                || platoon.Leader == null
                || platoon.Leader.disabled)
            {
                continue;
            }

            if (!TryPlanReseat(
                    hq,
                    platoon.Members,
                    platoon.Objective,
                    platoon.InContactUntil >= Time.time,
                    platoon.State == CommanderPlatoonState.Withdrawing,
                    platoon.Reseat,
                    out VehicleDepot depot,
                    out float remainingMinutes,
                    out float depotMinutes,
                    out string fromLabel))
            {
                continue;
            }

            ReseatGroup(
                hq, state, platoon.Members, platoon.Reseat, depot, platoon.Name,
                remainingMinutes, depotMinutes, fromLabel);
            AfterPlatoonReseat(platoon, depot);
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind != CommanderMissionKind.Picket
                || mission.Point == null
                || IsAirDeliveredPicket(state, mission.Point)
                || FirstLiveMember(mission.PicketMembers) == null)
            {
                continue;
            }

            // A picket has no state machine of its own: it is a detachment that drives to its point
            // and stands there, so there is nothing to put back into Forming and nothing to march out
            // again. Emptying the list IS the re-raise — the existing FillPickets pass claims the
            // vehicles back out of the pool as they redeploy at the new depot, from the nearest first,
            // which is exactly the behaviour that makes the near depot win.
            if (!TryPlanReseat(
                    hq,
                    mission.PicketMembers,
                    mission.Point.Position,
                    mission.ContactUntil >= Time.time,
                    false,
                    mission.Reseat,
                    out VehicleDepot depot,
                    out float remainingMinutes,
                    out float depotMinutes,
                    out string fromLabel))
            {
                continue;
            }

            ReseatGroup(
                hq, state, mission.PicketMembers, mission.Reseat, depot,
                $"the picket for {mission.Label}", remainingMinutes, depotMinutes, fromLabel);
        }
    }

    /// <summary>
    /// The shared decision for one group: find the depot nearest its objective, measure what is left
    /// of the drive against what the drive from there would be, and ask
    /// <see cref="ShouldReseatGroup"/>. True when the group should be re-raised, with the depot, the
    /// two drive times and the name of the place it is currently near for the log line.
    /// <para>The remaining drive is measured from the group's first live vehicle — the platoon's
    /// leader and the picket's leading vehicle are the same idea — and stretched by the same road
    /// detour factor the depot's own drive time uses, so the two numbers are comparable.</para>
    /// </summary>
    private static bool TryPlanReseat(
        FactionHQ hq,
        IReadOnlyList<Unit> members,
        GlobalPosition objective,
        bool inContact,
        bool withdrawing,
        CommanderReseatRecord record,
        out VehicleDepot depot,
        out float remainingMinutes,
        out float depotMinutes,
        out string fromLabel)
    {
        depot = null!;
        remainingMinutes = 0f;
        depotMinutes = 0f;
        fromLabel = string.Empty;

        Unit? leader = FirstLiveMember(members);
        if (leader == null)
        {
            return false;
        }

        if (!CommanderEconomyService.TryNearestOwnedDepot(hq, objective, out depot, out float depotMeters))
        {
            return false;
        }

        float detour = CommanderSettings.RoadDetourFactor;
        float speed = CommanderSettings.GroundSpeedMetersPerSecond;
        remainingMinutes = DriveMinutes(
            CommanderGameAccess.HorizontalDistance(leader.transform.position, objective.ToLocalPosition()),
            detour,
            speed);
        depotMinutes = DriveMinutes(depotMeters, detour, speed);
        if (!ShouldReseatGroup(
                remainingMinutes,
                depotMinutes,
                CommanderSettings.PlatoonReseatSavingMinutes,
                CommanderSettings.AirMobileDriveMinutes,
                inContact,
                withdrawing,
                OnReseatCooldown(record.At, Time.time, ReseatCooldownSeconds),
                record.LastDepotMinutes))
        {
            return false;
        }

        fromLabel = CommanderEconomyService.NearestHeldBaseLabel(hq, leader.transform.GlobalPosition());
        return true;
    }

    /// <summary>
    /// The shared action: takes every vehicle in <paramref name="members"/> off the road and hands it
    /// back to <paramref name="depot"/>, leaving the list empty and the group's re-raise record
    /// stamped. Writes the one line that says what happened and why.
    /// <para>No money moves. The vehicle is not sold and a replacement is not bought: it is the SAME
    /// vehicle, removed through <c>CommanderEconomyService.DespawnUnit</c> (the mod's one definition
    /// of "remove this unit from the game") and put straight back either onto the new depot's pad, or
    /// into the faction's vehicle supply with a deployment reservation naming that depot — which is
    /// exactly what the commander's own buy does when a depot pad is busy
    /// (<c>Ai/CommanderEnemyCommanderService.cs</c>). Selling at the idle-pool refund fraction and
    /// re-buying at full price would have cost the commander half a platoon for moving it; charging
    /// full price twice would have cost a whole one.</para>
    /// </summary>
    private void ReseatGroup(
        FactionHQ hq,
        OperationsState state,
        List<Unit> members,
        CommanderReseatRecord record,
        VehicleDepot depot,
        string what,
        float remainingMinutes,
        float depotMinutes,
        string fromLabel)
    {
        string toLabel = CommanderEconomyService.NearestHeldBaseLabel(
            hq, depot.transform.GlobalPosition());

        int returned = 0;
        for (int i = members.Count - 1; i >= 0; i--)
        {
            Unit member = members[i];
            VehicleDefinition? definition = member?.definition as VehicleDefinition;
            members.RemoveAt(i);
            if (member == null || member.disabled || definition == null)
            {
                // Nothing to hand back: a dead member is dropped exactly as DissolveToPool drops one.
                continue;
            }

            if (!CommanderEconomyService.DespawnUnit(member))
            {
                // The despawn is the only step that can fail, and a vehicle that could not be removed
                // must not be lost track of: it goes back to the free pool, where the next review's
                // reinforcement or picket fill can use it, rather than being left in a group that is
                // re-forming thirty kilometres away.
                state.Pool.Add(member);
                continue;
            }

            if (!depot.TrySpawnVehicle(definition))
            {
                hq.ModifyUnitSupply(definition, 1);
                CommanderFactionVehicleService.Instance?.ReserveDeployment(hq, definition, depot);
            }

            returned++;
        }

        record.At = Time.time;
        record.Banked = returned;
        // The strict-progress guard's memory: the next re-raise on this journey must beat this.
        record.LastDepotMinutes = depotMinutes;
        CommanderAiLog.Note(
            hq,
            $"re-raises {what} from {toLabel}: {depotMinutes:0} min from there against "
                + $"{remainingMinutes:0} min still to drive from {fromLabel}; "
                + $"{returned} vehicle(s) go back to the depot at no cost.");
    }

    /// <summary>
    /// The one thing a re-raised PLATOON needs that a picket does not: a platoon is a standing object
    /// with a state, a leader and formation slots, so it is put back into
    /// <see cref="CommanderPlatoonState.Forming"/> — the state this service already means by
    /// "gathering, not retreating", which is what keeps the losses rule off it while it is empty — and
    /// pointed at a gathering spot beside the depot it is being raised from. Everything else it keeps:
    /// its name, its mission, its establishment and the recipe it was raised on.
    /// </summary>
    private static void AfterPlatoonReseat(CommanderPlatoon platoon, VehicleDepot depot)
    {
        platoon.Issued.Clear();
        platoon.Leader = null;
        platoon.State = CommanderPlatoonState.Forming;
        platoon.FormingSince = Time.time;
        platoon.ClearGroundPosture();
        platoon.InContactUntil = -1f;
        platoon.PreemptiveAirUntil = -1f;
        // Gathers beside the depot it is being raised from, on the same clear ground a platoon formed
        // at a depot gathers on, rather than on the apron the vehicles roll out onto.
        platoon.Objective = ChooseFormUpPoint(depot.transform.GlobalPosition());
    }

    /// <summary>
    /// Moves a re-raised platoon back out to its objective once it has gathered again — full, or out
    /// of patience on the same <see cref="FormUpTimeoutSeconds"/> every other forming platoon waits
    /// out (Reuse rule 4). Clearing <see cref="CommanderReseatRecord.Banked"/> here is what closes the
    /// re-raise: the order book stops holding its lines back and the empty-platoon rule applies again.
    /// <para>Scoped to platoons a re-raise emptied, and to nothing else. A platoon gathering at a
    /// landing zone for an air-mobile lift is also Forming with a mission attached, and the lift's own
    /// completion is what moves that one out (<c>Operations/CommanderOperationsFob.cs</c>). A picket
    /// needs no counterpart: it has no state to come out of.</para>
    /// </summary>
    private static void ReleaseReseatedPlatoon(FactionHQ hq, CommanderPlatoon platoon)
    {
        if (platoon.Members.Count == 0)
        {
            return;
        }

        if (!IsReadyToMoveOut(
                platoon.Members.Count,
                platoon.Establishment,
                Time.time - platoon.FormingSince,
                FormUpTimeoutSeconds))
        {
            return;
        }

        platoon.Reseat.Banked = 0;
        CommanderOperationsMission? mission = platoon.Mission;
        if (mission?.Point == null)
        {
            return;
        }

        platoon.State = CommanderPlatoonState.Moving;
        platoon.Objective = mission.Point.Position;
        CommanderAiLog.Note(
            hq,
            $"{platoon.Name} is re-formed and marches on {mission.Label}: "
                + $"{platoon.Members.Count}/{platoon.Establishment} vehicles.");
    }

    /// <summary>The re-raise rule, its churn guard and its in-transit count at every named boundary,
    /// run at plugin load beside the rest of the operations checks. The rule is shared by the platoon
    /// and the picket caller, so the numeric boundaries are checked once; the cases named for a picket
    /// are the ones where the two callers feed it different terms.</summary>
    private static void CheckReseat(List<string> failures)
    {
        // A 40 km march from the old depot with 47 min left, against 12 min from a forward base that
        // has just come online: the case the user reported, and the only one that should act.
        Expect(
            failures,
            "a 47 min march a new depot cuts to 12 is re-raised",
            ShouldReseatGroup(47f, 12f, 15f, 20f, false, false, false, ReseatNoPreviousDepot),
            true);
        Expect(
            failures,
            "a saving exactly on the margin is not worth re-raising",
            ShouldReseatGroup(47f, 32f, 15f, 20f, false, false, false, ReseatNoPreviousDepot),
            false);
        Expect(
            failures,
            "a saving one minute past the margin is re-raised",
            ShouldReseatGroup(47f, 31f, 15f, 20f, false, false, false, ReseatNoPreviousDepot),
            true);
        Expect(
            failures,
            "a nearer depot that saves nothing does not re-raise",
            ShouldReseatGroup(47f, 47f, 15f, 20f, false, false, false, ReseatNoPreviousDepot),
            false);
        Expect(
            failures,
            "a depot behind the group never re-raises it",
            ShouldReseatGroup(47f, 60f, 15f, 20f, false, false, false, ReseatNoPreviousDepot),
            false);
        Expect(
            failures,
            "a platoon in contact is never dissolved",
            ShouldReseatGroup(47f, 12f, 15f, 20f, true, false, false, ReseatNoPreviousDepot),
            false);
        Expect(
            failures,
            "a picket whose point is in contact is never dissolved",
            ShouldReseatGroup(47f, 12f, 15f, 20f, true, false, false, ReseatNoPreviousDepot),
            false);
        Expect(
            failures,
            "a withdrawing platoon is never dissolved",
            ShouldReseatGroup(47f, 12f, 15f, 20f, false, true, false, ReseatNoPreviousDepot),
            false);
        Expect(
            failures,
            "a group on its re-raise cooldown is left alone",
            ShouldReseatGroup(47f, 12f, 15f, 20f, false, false, true, ReseatNoPreviousDepot),
            false);
        Expect(
            failures,
            "a group exactly on the far threshold finishes its drive",
            ShouldReseatGroup(20f, 1f, 15f, 20f, false, false, false, ReseatNoPreviousDepot),
            false);
        // Two separate picket boundaries: the first would clear the margin (17 min saved) and is
        // refused only because the detachment is no longer far enough out, which is the term that
        // keeps a picket close to its point from being churned; the second is a picket that has
        // arrived, where there is nothing left to save at all.
        Expect(
            failures,
            "a picket inside the far threshold is left to finish its drive",
            ShouldReseatGroup(18f, 1f, 15f, 20f, false, false, false, ReseatNoPreviousDepot),
            false);
        Expect(
            failures,
            "a picket already standing on its point is never re-raised",
            ShouldReseatGroup(1f, 1f, 15f, 20f, false, false, false, ReseatNoPreviousDepot),
            false);
        Expect(
            failures,
            "a platoon nearly at its objective is never re-raised",
            ShouldReseatGroup(5f, 1f, 15f, 20f, false, false, false, ReseatNoPreviousDepot),
            false);
        Expect(
            failures,
            "the live re-raise margin refuses a saving exactly on it",
            ShouldReseatGroup(
                CommanderSettings.AirMobileDriveMinutes + CommanderSettings.PlatoonReseatSavingMinutes + 1f,
                CommanderSettings.AirMobileDriveMinutes + 1f,
                CommanderSettings.PlatoonReseatSavingMinutes,
                CommanderSettings.AirMobileDriveMinutes,
                false,
                false,
                false,
                ReseatNoPreviousDepot),
            false);
        Expect(
            failures,
            "the re-raise margin is a positive number of minutes",
            CommanderSettings.PlatoonReseatSavingMinutes > 0f,
            true);
        Expect(
            failures,
            "the re-raise cooldown outlasts the deployment reservation the returned vehicles wait on",
            ReseatCooldownSeconds > ReseatInTransitSeconds,
            true);

        // The strict-progress guard (user decision 2026-09-16). A loop needs a re-raise that does not
        // shorten the journey; these four say there is no such re-raise.
        Expect(
            failures,
            "a group given a new objective may be re-raised again",
            ShouldReseatGroup(47f, 12f, 15f, 20f, false, false, false, ReseatNoPreviousDepot),
            true);
        Expect(
            failures,
            "a re-raise to a depot nearer than the last one is allowed",
            ShouldReseatGroup(47f, 12f, 15f, 20f, false, false, false, 20f),
            true);
        Expect(
            failures,
            "a re-raise that does not shorten the journey is refused",
            ShouldReseatGroup(47f, 12f, 15f, 20f, false, false, false, 12f),
            false);
        Expect(
            failures,
            "a re-raise back toward a depot further out than the last one is refused",
            ShouldReseatGroup(47f, 12f, 15f, 20f, false, false, false, 8f),
            false);
        Expect(
            failures,
            "a fresh re-raise record lets the rule fire",
            new CommanderReseatRecord().LastDepotMinutes,
            ReseatNoPreviousDepot);
        Expect(
            failures,
            "forgetting a journey restores a record that lets the rule fire",
            ReseatRecordAfterNewObjective(12f),
            ReseatNoPreviousDepot);

        Expect(
            failures,
            "a group re-raised this second is on its churn cooldown",
            OnReseatCooldown(1000f, 1000f, 600f),
            true);
        Expect(
            failures,
            "a group one second short of the churn cooldown is still held",
            OnReseatCooldown(1000f, 1599f, 600f),
            true);
        Expect(
            failures,
            "a group exactly on the churn cooldown may be re-raised again",
            OnReseatCooldown(1000f, 1600f, 600f),
            false);
        Expect(
            failures,
            "a group that has never been re-raised is not on the churn cooldown",
            OnReseatCooldown(-1f, 1000f, 600f),
            false);

        Expect(
            failures,
            "six vehicles handed back and none returned are all in transit",
            ReseatVehiclesInTransit(6, 0, 30f, 300f),
            6);
        Expect(
            failures,
            "four of six back leaves two in transit",
            ReseatVehiclesInTransit(6, 4, 30f, 300f),
            2);
        Expect(
            failures,
            "a re-raised picket of two has both vehicles in transit until they arrive",
            ReseatVehiclesInTransit(2, 0, 30f, 300f),
            2);
        Expect(
            failures,
            "every vehicle back leaves none in transit",
            ReseatVehiclesInTransit(6, 6, 30f, 300f),
            0);
        Expect(
            failures,
            "a group that was never re-raised has nothing in transit",
            ReseatVehiclesInTransit(0, 0, -1f, 300f),
            0);
        Expect(
            failures,
            "vehicles stop counting as in transit once the reservation window has run out",
            ReseatVehiclesInTransit(6, 0, 300f, 300f),
            0);
    }
}
