using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Two things that happen after a mission ends: getting a landed airframe back into stock without
/// letting the Basegame taxi it, and launching the same mission again if the player asked for that.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recovery.</b> The game recovers an AI airframe by having <c>AIPilotTaxiState</c> drive it to a
/// service point, disembark the pilot and call <c>Aircraft.ReturnToInventory</c>. On this mod's
/// maps that taxi leg is where aircraft die: a highway strip has no taxi network, so the pilot
/// drives at whatever is nearest, calls itself stuck after thirty seconds and ejects, and an
/// ejection is a lost airframe. Helicopters never reach a service point at all. So the moment an
/// RTB aircraft has been sitting on the deck beside a friendly base for a few seconds, this does
/// what the disembark would have done — <c>ReturnToInventory</c>, which is also what refunds the
/// purchase through <c>HandleAircraftReturned</c> — and the taxi state never gets a turn. The two
/// lines it uses are the game's own (<c>Aircraft.Disembark</c> ends the same way).
/// </para>
/// <para>
/// <b>Auto-recreate.</b> A mission flagged AUTO is relaunched from its recipe whenever its aircraft
/// leaves the mission table for any reason: shot down, crashed, recovered by the sweep above, or
/// brought home by the player. That last one is deliberate — with AUTO on, RTB is a rearm cycle.
/// Turn AUTO off before RTB to bring an aircraft home for good. Relaunches wait for the departure
/// base to be free and the faction to have the money; nothing is charged until one leaves the
/// ground.
/// </para>
/// </remarks>
internal sealed partial class CommanderAirCommandService
{
    /// <summary>
    /// How long an RTB aircraft has to sit on the deck before it is recovered. Long enough that a
    /// bounce or a slow roll-out does not count, short enough to beat the taxi state's own stuck
    /// timer (30 s) and the Basegame's 2 s hand-off to it by a wide margin — the sweep runs every
    /// <see cref="MissionPruneIntervalSeconds"/>, so 3 s here means recovery within about 5 s.
    /// </summary>
    private const float RecoverDwellSeconds = 3f;

    /// <summary>Ground speed under which an aircraft on the deck counts as stopped or taxiing.
    /// Matches the parked threshold used by the capture landing.</summary>
    private const float RecoverSpeedMetersPerSecond = 12f;

    /// <summary>Wait between relaunch attempts, so an unaffordable relaunch does not spam the
    /// status line every frame while the faction saves up.</summary>
    private const float RelaunchRetrySeconds = 5f;

    private readonly List<AirMissionRecipe> relaunchQueue = new();
    private readonly List<Aircraft> recoverySweep = new();
    private float nextRelaunchAt;

    /// <summary>Missions waiting for a base and the money to relaunch, for the AIR MISSIONS tab.</summary>
    internal int PendingRelaunchCount => relaunchQueue.Count;

    internal bool CanToggleAutoRecreate(Aircraft aircraft)
    {
        return missions.TryGetValue(aircraft, out AirMission mission) && mission.CanAutoRecreate;
    }

    internal bool IsAutoRecreate(Aircraft aircraft)
    {
        return missions.TryGetValue(aircraft, out AirMission mission) && mission.AutoRecreate;
    }

    internal void ToggleAutoRecreate(Aircraft aircraft)
    {
        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            return;
        }

        if (!mission.CanAutoRecreate)
        {
            SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} was not launched by Air Command, so it cannot be relaunched.");
            return;
        }

        mission.AutoRecreate = !mission.AutoRecreate;
        SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)}: auto-recreate {(mission.AutoRecreate ? "ON" : "OFF")}.");
    }

    /// <summary>Called from <c>RemoveMission</c>, the one place a mission ever leaves the table.</summary>
    private void QueueAutoRecreate(Aircraft aircraft, AirMission mission)
    {
        if (!mission.AutoRecreate || mission.Recipe == null)
        {
            return;
        }

        relaunchQueue.Add(mission.Recipe);
        SetStatus($"{GetModeLabel(mission.Recipe.Mode)} mission will relaunch: "
            + $"{GetAircraftLabel(mission.Recipe.Option.Definition)} ({relaunchQueue.Count} queued).");
        CommanderPlugin.Log.LogInfo(
            $"Air Command: {CommanderGameAccess.GetUnitLabel(aircraft)} left its {GetModeLabel(mission.Recipe.Mode)} mission; relaunch queued.");
    }

    /// <summary>One relaunch attempt per retry interval, oldest first, one spawn in flight at a time.</summary>
    private void ProcessAutoRecreate()
    {
        if (relaunchQueue.Count == 0
            || pendingAircraftSpawn != null
            || !CommanderScheduler.IsDue(ref nextRelaunchAt, RelaunchRetrySeconds))
        {
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null || !hq.IsServer)
        {
            return;
        }

        AirMissionRecipe recipe = relaunchQueue[0];
        Airbase? airbase = ChooseRelaunchAirbase(hq, recipe);
        if (airbase == null)
        {
            ReportRelaunchWait(recipe, "no compatible airbase is free");
            return;
        }

        if (TrySpawnFromRecipe(recipe, airbase, autoRecreate: true))
        {
            relaunchQueue.RemoveAt(0);
            RelaunchWaitReason = string.Empty;
            lastRelaunchWaitLogged = string.Empty;
            CommanderPlugin.Log.LogInfo(
                $"Air Command: relaunched {GetAircraftLabel(recipe.Option.Definition)} on {GetModeLabel(recipe.Mode)} "
                    + $"from {GetAirbaseName(airbase)}; {relaunchQueue.Count} still queued.");
            return;
        }

        // TrySpawnFromRecipe has put the reason in the status line (funds, busy hangar, rejected
        // spawn); the recipe stays at the head of the queue and is tried again next interval.
        ReportRelaunchWait(recipe, statusText);
    }

    /// <summary>Why the head of the relaunch queue is not flying yet, for the AIR MISSIONS tab.
    /// Empty when nothing is queued or the last attempt succeeded.</summary>
    internal string RelaunchWaitReason { get; private set; } = string.Empty;
    private string lastRelaunchWaitLogged = string.Empty;

    /// <summary>
    /// A relaunch that never happens is indistinguishable from one that is saving up, so the reason
    /// goes on the tab and, once per distinct reason, into the log. Not every retry: the queue is
    /// polled every few seconds and a faction short of money would fill the log with one line.
    /// </summary>
    private void ReportRelaunchWait(AirMissionRecipe recipe, string reason)
    {
        string label = GetAircraftLabel(recipe.Option.Definition);
        RelaunchWaitReason = reason;
        SetStatus($"Relaunch of {label} waiting: {reason}");
        if (reason != lastRelaunchWaitLogged)
        {
            lastRelaunchWaitLogged = reason;
            CommanderPlugin.Log.LogInfo($"Air Command: relaunch of {label} waiting: {reason}");
        }
    }

    /// <summary>The base it left from if that is still usable, else the nearest one that is.</summary>
    private static Airbase? ChooseRelaunchAirbase(FactionHQ hq, AirMissionRecipe recipe)
    {
        AircraftDefinition definition = recipe.Option.Definition;
        Airbase origin = recipe.Origin;
        if (origin != null
            && !origin.disabled
            && IsCompatibleAirbase(origin, hq, definition)
            && origin.CanSpawnAircraft(definition))
        {
            return origin;
        }

        Airbase? best = null;
        float bestDistance = float.MaxValue;
        Vector3 from = origin != null && origin.center != null ? origin.center.position : Vector3.zero;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null
                || airbase.disabled
                || airbase.center == null
                || !IsCompatibleAirbase(airbase, hq, definition)
                || !airbase.CanSpawnAircraft(definition))
            {
                continue;
            }

            float distance = (airbase.center.position - from).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = airbase;
            }
        }

        return best;
    }

    /// <summary>
    /// Recovers every RTB aircraft that has sat on the deck beside a friendly base for
    /// <see cref="RecoverDwellSeconds"/>. Capture landings are excluded: those are parked on purpose
    /// and must stay on the field.
    /// </summary>
    private void RecoverLandedAircraft()
    {
        recoverySweep.Clear();
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            AirMission mission = entry.Value;
            if (mission.Returning && mission.RtbIssued && !mission.Parked && mission.Intent != LandingIntent.Capture)
            {
                recoverySweep.Add(entry.Key);
            }
        }

        for (int i = 0; i < recoverySweep.Count; i++)
        {
            Aircraft aircraft = recoverySweep[i];
            if (aircraft == null || aircraft.disabled || !missions.TryGetValue(aircraft, out AirMission mission))
            {
                continue;
            }

            if (!IsOnDeck(aircraft))
            {
                mission.OnDeckSince = -1f;
                continue;
            }

            if (mission.OnDeckSince < 0f)
            {
                mission.OnDeckSince = Time.time;
                continue;
            }

            if (Time.time - mission.OnDeckSince < RecoverDwellSeconds)
            {
                continue;
            }

            FactionHQ hq = aircraft.NetworkHQ;
            if (hq == null || !hq.IsServer || !hq.AnyNearAirbase(aircraft.transform.position, out Airbase _))
            {
                // Down, but not at home. Leave it to the Basegame; recovering an airframe in a
                // field would be a free teleport back into stock.
                continue;
            }

            string label = CommanderGameAccess.GetUnitLabel(aircraft);
            // Stand the pilot down first. ReturnToInventory disables the airframe but leaves the
            // object alive for two seconds, and a taxi state still ticking on a half-torn-down
            // aircraft threw a null reference every physics frame until it went.
            for (int p = 0; aircraft.pilots != null && p < aircraft.pilots.Length; p++)
            {
                Pilot pilot = aircraft.pilots[p];
                if (pilot != null && pilot.parkedState != null)
                {
                    pilot.SwitchState(pilot.parkedState);
                }
            }

            // Same two lines Aircraft.Disembark ends with when the pilot walks away at a friendly
            // base. ReturnToInventory puts the airframe back in stock, and the mod's postfix on it
            // refunds a funds purchase and removes the mission (which is what queues a relaunch).
            aircraft.NetworkunitState = Unit.UnitState.Abandoned;
            aircraft.ReturnToInventory();
            // No toast here: the alert service writes RECOVERED to the battle log when the faction
            // drops the unit, the same way it writes LOST, so this would be the same news twice.
            CommanderPlugin.Log.LogInfo($"Air Command: {label} recovered into stock after landing.");
        }
    }

    /// <summary>On the ground and either stopped or taxiing. The taxi-state check catches an
    /// aircraft rolling faster than the speed threshold along a taxiway. Internal (one-word
    /// widening, Reuse rule 4): the air markers read the same answer, so "on the ground" means one
    /// thing to the recovery and to the label above the aeroplane.</summary>
    internal static bool IsOnDeck(Aircraft aircraft)
    {
        if (aircraft.radarAlt >= ParkedRadarAltMeters)
        {
            return false;
        }

        if (aircraft.speed < RecoverSpeedMetersPerSecond)
        {
            return true;
        }

        if (aircraft.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot != null && pilot.currentState is AIPilotTaxiState)
            {
                return true;
            }
        }

        return false;
    }
}
