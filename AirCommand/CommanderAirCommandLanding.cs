using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Putting a commanded aircraft down on a named airbase — to take that base, or to hand the
/// airframe back for a fresh one.
/// </summary>
/// <remarks>
/// The Basegame has no "land there" order. <c>AIPilotLandingState</c> picks its own field out of
/// <c>aircraft.NetworkHQ.GetNearestAirbase</c>, which can only ever be one the faction already
/// holds, so an aircraft ordered onto a neutral airfield flew over it and turned for home — which
/// is exactly what the fourth playtest saw. Three things make it land instead:
/// <list type="number">
/// <item>the mission carries a <c>LandingBase</c>, and the route point is that base, so the
/// aircraft actually flies there;</item>
/// <item><see cref="TryOverrideLandingAirbase"/> is a prefix on the landing state's private airbase
/// search that substitutes the commanded field and requests a runway from it;</item>
/// <item>on a capture the aircraft is parked by hand the moment it stops rolling. Left alone the
/// Basegame hands it to <c>AIPilotTaxiState</c>, which at a field the faction does not own finds
/// nothing to taxi to, sits still for thirty seconds, calls itself stuck and ejects the pilot —
/// and an ejection away from a friendly base ends in <c>DisableUnit</c>, i.e. the aeroplane is
/// gone. <c>PilotParkedState</c> does nothing at all, which is precisely what is wanted.</item>
/// </list>
/// <para>
/// Capture strength is granted by the mod rather than found on the airframe.
/// <c>Unit.CaptureStrength</c> is 0 for every aircraft that is not carrying a <c>MountedTroops</c>
/// pod, so without this a fighter parked in the middle of the ring would move the bar by exactly
/// nothing and the order would look ignored for a second time. <c>Unit.ModifyCaptureStrength</c> is
/// the same public hook the troop pod itself uses, and the grant is handed back when the aircraft
/// leaves.
/// </para>
/// </remarks>
internal sealed partial class CommanderAirCommandService
{
    /// <summary>How close the aircraft has to be before the pilot is switched into the approach.</summary>
    private const float LandingHandoverMeters = 9000f;

    /// <summary>Rolling out: below this it is on the deck and should be parked, not taxied. The
    /// window has to be wide enough to beat the Basegame's own 2 s check, which hands anything under
    /// 15 m/s to the taxi state and from there to an ejection.</summary>
    private const float ParkedSpeedMetersPerSecond = 12f;
    private const float ParkedRadarAltMeters = 5f;

    /// <summary>Height a released aircraft is lifted to when it cannot use a runway. See
    /// <see cref="ReleaseFromGround"/>.</summary>
    private const float ReleaseAltitudeMeters = 350f;

    private readonly List<Aircraft> landingSweep = new();

    /// <summary>
    /// Orders one aircraft to put itself down on <paramref name="airbase"/>. The route point is the
    /// field itself, so the yellow travel line the selection already draws leads to the base it is
    /// going to — which is the only feedback the player gets that the order took.
    /// </summary>
    internal bool SetAircraftLandingOrder(Aircraft aircraft, Airbase airbase, LandingIntent intent, bool append)
    {
        if (!IsTaskableAircraft(aircraft) || airbase == null || airbase.center == null || intent == LandingIntent.None)
        {
            return false;
        }

        GlobalPosition field = airbase.center.GlobalPosition();
        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            if (!TryAdoptAircraft(aircraft, AirCommandMode.Cas, field, GetMissionRadius(AirCommandMode.Cas)))
            {
                return false;
            }
            mission = missions[aircraft];
        }

        // A redirect from one field to another has to let go of the first, which for an aircraft
        // already sitting on the deck means getting it airborne before it is told to fly anywhere.
        ClearLandingOrder(aircraft, mission, release: true);

        if (!append)
        {
            mission.Route.Clear();
            mission.RouteIndex = 0;
        }

        mission.Route.Add(field);
        mission.AreaCenter = field;
        mission.ForcedTarget = null;
        mission.Returning = false;
        mission.RtbIssued = false;
        mission.LandingBase = airbase;
        mission.Intent = intent;
        mission.LandingIssued = false;
        mission.Parked = false;
        DestroyMissionMapVisual(mission);
        RefreshMissionMapVisuals();

        string label = CommanderGameAccess.GetUnitLabel(aircraft);
        string field_ = CommanderCaptureService.GetAirbaseLabel(airbase);
        SetStatus(intent == LandingIntent.Capture
            ? $"{label} ordered to land on {field_} and hold it."
            : $"{label} ordered to land at {field_} to rearm.");
        CommanderAlertService.Instance?.NotifyCaptureOrder(
            intent == LandingIntent.Capture
                ? $"AIR CAPTURE: {field_.ToUpperInvariant()}"
                : $"RESUPPLY: {field_.ToUpperInvariant()}",
            aircraft);
        return true;
    }

    /// <summary>
    /// Sends an aircraft to the nearest field its faction holds and lands it there. That is what the
    /// game calls resupply: an AI airframe that stops at a friendly base is recovered into stock by
    /// <c>Aircraft.ReturnToInventory</c>, funds and all, ready to be launched again fully armed.
    /// There is no refuelling in place for an AI aircraft — the airframe going back into the pool
    /// <em>is</em> the rearm.
    /// </summary>
    internal bool ResupplyAircraft(Aircraft aircraft)
    {
        Airbase? nearest = FindNearestOwnedAirbase(aircraft);
        if (nearest == null)
        {
            SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} has no friendly airbase to return to.");
            return false;
        }

        return SetAircraftLandingOrder(aircraft, nearest, LandingIntent.Resupply, append: false);
    }

    /// <summary>Nearest airbase the aircraft's own faction holds and that it could put down on.</summary>
    internal static Airbase? FindNearestOwnedAirbase(Aircraft aircraft)
    {
        FactionHQ? hq = aircraft?.NetworkHQ;
        if (aircraft == null || hq == null)
        {
            return null;
        }

        Airbase? best = null;
        float bestDistance = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = FastMath.Distance(airbase.center.GlobalPosition(), aircraft.GlobalPosition());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = airbase;
            }
        }

        return best;
    }

    /// <summary>True while this aircraft is on its way to, or sitting on, a commanded field.</summary>
    internal bool TryGetLandingOrder(Aircraft aircraft, out Airbase? airbase, out LandingIntent intent)
    {
        if (missions.TryGetValue(aircraft, out AirMission mission) && mission.LandingBase != null)
        {
            airbase = mission.LandingBase;
            intent = mission.Intent;
            return true;
        }

        airbase = null;
        intent = LandingIntent.None;
        return false;
    }

    /// <summary>
    /// Drops a landing order, hands back any capture strength it granted, and gets the aircraft off
    /// the ground again if it is sitting on one.
    /// </summary>
    private void ClearLandingOrder(Aircraft aircraft, AirMission mission, bool release)
    {
        if (mission.LandingBase == null)
        {
            return;
        }

        if (mission.GrantedCaptureStrength > 0f && aircraft != null && !aircraft.disabled)
        {
            aircraft.ModifyCaptureStrength(-mission.GrantedCaptureStrength);
        }
        mission.GrantedCaptureStrength = 0f;

        bool parked = mission.Parked;
        Airbase airbase = mission.LandingBase;
        mission.LandingBase = null;
        mission.Intent = LandingIntent.None;
        mission.LandingIssued = false;
        mission.Parked = false;
        mission.Returning = false;
        mission.RtbIssued = false;

        if (release && parked && aircraft != null && !aircraft.disabled)
        {
            ReleaseFromGround(aircraft, airbase);
        }
    }

    /// <summary>
    /// Per persistent tick: fly the approach, park on touchdown, and let go once the base is taken.
    /// </summary>
    private void TickLandingOrders()
    {
        landingSweep.Clear();
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            if (entry.Value.LandingBase != null)
            {
                landingSweep.Add(entry.Key);
            }
        }

        for (int i = 0; i < landingSweep.Count; i++)
        {
            Aircraft aircraft = landingSweep[i];
            if (aircraft == null || aircraft.disabled || !missions.TryGetValue(aircraft, out AirMission mission))
            {
                continue;
            }

            Airbase? airbase = mission.LandingBase;
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                ClearLandingOrder(aircraft, mission, release: true);
                continue;
            }

            // The base fell while the aircraft was holding it: the order is done, so give the
            // airframe back rather than leaving it parked on a field it no longer has to sit on.
            if (mission.Intent == LandingIntent.Capture && airbase.CurrentHQ == mission.Hq)
            {
                CommanderAlertService.Instance?.NotifyCaptureOrder(
                    $"{CommanderCaptureService.GetAirbaseLabel(airbase).ToUpperInvariant()} TAKEN — AIRBORNE AGAIN",
                    aircraft);
                ClearLandingOrder(aircraft, mission, release: true);
                continue;
            }

            if (mission.Parked)
            {
                continue;
            }

            bool onDeck = aircraft.radarAlt < ParkedRadarAltMeters && aircraft.speed < ParkedSpeedMetersPerSecond;
            if (onDeck && mission.LandingIssued)
            {
                ParkOnDeck(aircraft, mission, airbase);
                continue;
            }

            if (!mission.LandingIssued
                && FastMath.InRange(airbase.center.GlobalPosition(), aircraft.GlobalPosition(), LandingHandoverMeters))
            {
                // The route has done its job; from here the Basegame approach flies the aeroplane,
                // and Returning stands the route steering down so the two do not fight.
                mission.Returning = true;
                mission.RtbIssued = true;
                mission.LandingIssued = true;
                IssueReturnToBase(aircraft, mission);
            }
        }
    }

    /// <summary>
    /// Stops the aircraft where it rolled out and, on a capture, makes it worth something to the
    /// ring it is standing in.
    /// </summary>
    private void ParkOnDeck(Aircraft aircraft, AirMission mission, Airbase airbase)
    {
        mission.Parked = true;

        // A resupply landing is *finished* by the Basegame and must not be interfered with: the taxi
        // state takes the aircraft to a service point, the pilot disembarks, and — because this is a
        // field the faction holds — the ejection ends in ReturnToInventory, which is what puts the
        // airframe back in stock with its cost refunded. Parking it stops exactly that happening and
        // leaves an aeroplane sitting on the runway forever. Parked is set anyway, so this service
        // stops steering it.
        if (mission.Intent != LandingIntent.Capture)
        {
            return;
        }

        if (aircraft.pilots != null)
        {
            for (int i = 0; i < aircraft.pilots.Length; i++)
            {
                Pilot pilot = aircraft.pilots[i];
                if (pilot != null)
                {
                    pilot.SwitchState(pilot.parkedState);
                }
            }
        }

        // The parked state only stands on the brakes below 1 m radar altitude, and an aeroplane
        // sitting on its gear can read higher than that. Nothing else writes the controls once the
        // state machine is parked, so this is what actually stops it rolling off the strip.
        ControlInputs inputs = aircraft.GetInputs();
        inputs.throttle = 0f;
        inputs.brake = 1f;

        // Zero on the slider means "aircraft do not capture" — the order still lands them, they just
        // stop being worth anything to the ring, which is the honest way to switch this off.
        float strength = Mathf.Max(0f, CommanderSettings.AircraftCaptureStrength);
        if (strength > 0f)
        {
            aircraft.ModifyCaptureStrength(strength);
            mission.GrantedCaptureStrength = strength;
        }
        CommanderAlertService.Instance?.NotifyCaptureOrder(
            $"{CommanderGameAccess.GetUnitLabel(aircraft).ToUpperInvariant()} DOWN ON "
                + CommanderCaptureService.GetAirbaseLabel(airbase).ToUpperInvariant(),
            aircraft);
    }

    /// <summary>
    /// Gets a parked aircraft flying again. A field the faction holds gets the Basegame takeoff
    /// state, which is the real thing; anywhere else does not, because
    /// <c>AIPilotTakeoffState</c> ejects the pilot outright when
    /// <c>airbase.CurrentHQ != aircraft.NetworkHQ</c> — so an aircraft called off a base it never
    /// managed to take is lifted clear instead, the same compromise
    /// <see cref="LaunchAiAircraft"/> already makes for every commander launch on this map.
    /// </summary>
    private static void ReleaseFromGround(Aircraft aircraft, Airbase? airbase)
    {
        Pilot? pilot = null;
        if (aircraft.pilots != null)
        {
            for (int i = 0; i < aircraft.pilots.Length && pilot == null; i++)
            {
                pilot = aircraft.pilots[i];
            }
        }

        if (pilot == null)
        {
            return;
        }

        if (airbase != null && !airbase.disabled && airbase.CurrentHQ == aircraft.NetworkHQ)
        {
            if (pilot.AITakeoffState == null) pilot.AITakeoffState = new AIPilotTakeoffState();
            pilot.SwitchState(pilot.AITakeoffState);
            return;
        }

        float speed = Mathf.Max(
            aircraft.definition.aircraftParameters.PIDReferenceAirspeed,
            aircraft.definition.aircraftParameters.takeoffSpeed * 1.5f,
            120f);
        aircraft.transform.position += Vector3.up * ReleaseAltitudeMeters;
        if (aircraft.rb != null)
        {
            aircraft.rb.velocity = aircraft.transform.forward * speed;
        }
        aircraft.SetFlightAssist(enabled: true);
        aircraft.SetGear(deployed: false);
        if (pilot.AICombatState == null) pilot.AICombatState = new AIPilotCombatModes(aircraft);
        pilot.SwitchState(pilot.AICombatState);
    }

    /// <summary>
    /// Prefix hook for <c>AIPilotLandingState.LandingState_SearchAirbase</c>: substitutes the
    /// commanded field for the one the Basegame would have found. Returns false — "run the original"
    /// — whenever there is no commanded field or that field will not take the aircraft, so a
    /// rejected approach still lands somewhere rather than being abandoned in the circuit.
    /// </summary>
    internal static bool TryOverrideLandingAirbase(
        AIPilotLandingState state,
        FieldInfo? aircraftField,
        FieldInfo? landingModeField,
        FieldInfo? airbaseField,
        FieldInfo? runwayUsageField,
        FieldInfo? landingSpeedField)
    {
        CommanderAirCommandService? service = Instance;
        if (service == null
            || service.missions.Count == 0
            || aircraftField == null
            || landingModeField == null
            || airbaseField == null
            || runwayUsageField == null
            || landingSpeedField == null)
        {
            return false;
        }

        // The original bails out unless it is still joining the pattern (mode 0); once established
        // the runway is chosen and re-picking it mid-approach would be a go-around.
        object? mode = landingModeField.GetValue(state);
        if (mode == null || System.Convert.ToInt32(mode) != 0)
        {
            return false;
        }

        if (aircraftField.GetValue(state) is not Aircraft aircraft
            || !service.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.LandingBase == null
            || mission.LandingBase.disabled)
        {
            return false;
        }

        AircraftParameters parameters = aircraft.GetAircraftParameters();
        float landingSpeed = Mathf.Sqrt(aircraft.GetMass() / aircraft.definition.aircraftInfo.maxWeight)
            * parameters.landingSpeed;
        RunwayQuery query = new()
        {
            RunwayType = RunwayQueryType.Landing,
            MinSize = parameters.verticalLanding ? aircraft.definition.length : parameters.takeoffDistance,
            LandingSpeed = parameters.verticalLanding ? 0f : landingSpeed,
            TailHook = aircraft.weaponManager.HasTailHook(),
        };

        Airbase.Runway.RunwayUsage? usage = mission.LandingBase.RequestLanding(aircraft, query);
        if (!usage.HasValue)
        {
            CommanderPlugin.Log.LogWarning(
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} cannot land at "
                    + $"{CommanderCaptureService.GetAirbaseLabel(mission.LandingBase)}: no runway it fits. "
                    + "Falling back to the Basegame choice.");
            return false;
        }

        airbaseField.SetValue(state, mission.LandingBase);
        runwayUsageField.SetValue(state, usage.Value);
        landingSpeedField.SetValue(state, parameters.verticalLanding ? 90f : landingSpeed);
        return true;
    }
}
