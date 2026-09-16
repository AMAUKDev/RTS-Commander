using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The delivery bypass (design, delivery-bypass_20260916; user decision 2026-09-16: bypass EVERY
/// delivery, not only the ones that stall). A cargo transport stops trying to touch down. It arrives,
/// holds low and roughly still, the mod places its vehicles on clear ground nearby, empties the
/// aircraft and sends it home.
/// <para>
/// Why it exists. The game releases cargo only when radar altitude is under 2 m AND speed is under
/// 10 m/s — in plain words, the aircraft must be physically on the ground and stopped — while inside
/// 300 m of its touchdown point it switches flight assist off and commands a hover at 20 m. A
/// transport that holds that hover never satisfies the gate, which is the "it cannot land, units
/// aren't dropped, stuck" the developer reported, and the tiltwing that keeps trying is the airframe
/// that flies into the ground (survey, 2026-09-16, Part 1: the VL-49 Tarantula ends 29 percent of
/// its losses in the dirt against the UH-90 Ibis's 9 percent).
/// </para>
/// <para>
/// What it does NOT fix, recorded here so nobody re-litigates it: of 80 measured cargo flights, 18
/// were shot down in transit and 22 were recalled by our own route watch. Both buckets are larger
/// than the 9 that hovered, and both are separate work (design section 6).
/// </para>
/// <para>
/// Nothing here creates a vehicle or sends a transport home. Firing a cargo mount is already how
/// every delivered vehicle enters the world, and firing already leaves the aircraft reading empty
/// (<c>MountedCargo.Fire</c> sets its fired flag and zeroes its ammunition, and
/// <c>GetAmmoLoaded</c> then answers 0 — the very field <see cref="HasDeployableCargo"/> and
/// <see cref="TrySelectNextCargoWeapon"/> read), so <see cref="DeployNextAssignedCargo"/> reaches its
/// own return-to-base branch unchanged. This file only decides WHEN that routine runs and WHERE the
/// vehicles end up.
/// </para>
/// </summary>
internal sealed partial class CommanderSupplyHeliService
{
    /// <summary>
    /// Whether this cargo flight is one the bypass owns: a picket insertion, a forward-base
    /// construction load or a platoon lift, flying a landing delivery. The SAM platform and naval
    /// approaches keep their own arrival rules — they deliver onto a deck, not onto ground — and a
    /// drop run has already bypassed the landing gate by another route.
    /// </summary>
    private static bool BypassesLanding(CargoMission mission)
    {
        return CommanderSettings.SupplyUnloadInPlaceEnabled
            && mission.InsertionPoint != null
            && !mission.Airdrop
            && !mission.Cancelled
            && !mission.RouteTransitActive
            && mission.NavalTarget == null
            && mission.DepositSiteId < 0
            && mission.FoundationSiteId < 0
            && mission.JacknifeSiteId < 0;
    }

    /// <summary>
    /// One fixed frame of the bypass, called from <c>OverrideTransportTarget</c> for every assigned
    /// transport. Until the flight latches, this only measures; once it has latched it runs the
    /// release routine that already exists, which is what opens the doors, keeps the 2.5 s gap
    /// between vehicles, waits for each released vehicle to activate before firing the next, credits
    /// the delivery and finally issues the flight home.
    /// <para>There is no clock here on purpose (design section 4.2). A transport that never gets low
    /// enough to latch is caught by the stall clock the operations side has always run — 120 s
    /// within 500 m of the landing zone — which then asks for a forced unload at a higher ceiling and
    /// falls through to the parachute drop and finally the recall. So "must also be low" cannot
    /// become a new way of waiting for ever.</para>
    /// </summary>
    private void TickUnloadInPlace(AIHeloTransportState state, Aircraft aircraft, CargoMission mission)
    {
        if (!BypassesLanding(mission))
        {
            return;
        }

        if (!mission.UnloadInPlace)
        {
            float metres = CommanderGameAccess.HorizontalDistance(
                aircraft.transform.position, mission.Target.ToLocalPosition());
            if (!CommanderCargoUnloadRule.UnloadsInPlace(
                    metres,
                    aircraft.radarAlt,
                    aircraft.speed,
                    CommanderOperationsService.InsertionStallRadiusMeters,
                    CommanderCargoUnloadRule.UnloadHeightMeters,
                    CommanderCargoUnloadRule.UnloadSpeedMetersPerSecond))
            {
                return;
            }

            BeginUnloadInPlace(
                aircraft,
                mission,
                $"{UnloadWhere(mission)}: unloading in place at {aircraft.radarAlt:0} m and "
                    + $"{aircraft.speed:0} m/s, {metres:0} m out — the transport does not land.");
        }

        DeployNextAssignedCargo(state);
    }

    /// <summary>Latches the bypass for one flight and says so once. The one place the flag is set,
    /// so the normal path and the bounded wait can never latch it differently.</summary>
    private static void BeginUnloadInPlace(Aircraft aircraft, CargoMission mission, string line)
    {
        mission.UnloadInPlace = true;
        mission.UnloadStartedAt = Time.timeSinceLevelLoad;
        // Opened here as well as by the engine: firing a mount springs its own bay doors, but a load
        // of two then waits on a ramp that is still travelling, and the doors are what the release
        // cadence's gap is for. The landing path opens them the same way for a logistics run.
        OpenCargoDoors(aircraft, mission);
        CommanderAiLog.Note(mission.Hq, line);
    }

    /// <summary>
    /// The bounded wait (design section 4.2). The operations side's existing stall clock — 120 s
    /// within 500 m of the landing zone, the same clock and the same setting it has run since
    /// 2026-09-14 — calls this when it expires, BEFORE it converts the flight to a parachute drop.
    /// The flight unloads at whatever height it has reached, up to
    /// <see cref="CommanderCargoUnloadRule.ForcedUnloadHeightMeters"/>; above that this refuses and the parachute path takes
    /// it exactly as it does today. So a transport that cannot descend delivers, drops or is
    /// recalled, and never hangs.
    /// </summary>
    /// <param name="flightId">Which ONE flight of a lift stalled, or
    /// <see cref="CommanderCargoFlightSlot.Unslotted"/> for the first flight standing on the point.
    /// A wave has several deliveries in the air for one place at once, so this must unload the
    /// transport that could not get down and not the one beside it.</param>
    internal bool TryForceUnloadInPlace(
        FactionHQ hq, CommanderStrategicPoint point, int flightId = CommanderCargoFlightSlot.Unslotted)
    {
        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            CargoMission mission = entry.Value;
            Aircraft aircraft = entry.Key;
            if (aircraft == null
                || aircraft.disabled
                || !ReferenceEquals(mission.Hq, hq)
                || !ReferenceEquals(mission.InsertionPoint, point)
                || !CommanderCargoFlightSlot.Matches(mission.InsertionFlightId, flightId)
                || !BypassesLanding(mission))
            {
                continue;
            }

            if (mission.UnloadInPlace)
            {
                // Already unloading. Yes while it is still putting vehicles out, so the caller gives
                // it another window rather than converting a flight mid-unload — a normal unload can
                // easily still be running when the clock that started on arrival expires. No once it
                // has gone quiet, because saying yes for ever would reset the stall clock for ever
                // and rebuild the hang this track exists to remove, one layer up. A no hands the
                // flight to the parachute drop and then the recall, the same rescue every other
                // stuck flight gets.
                return CommanderCargoUnloadRule.UnloadIsProgressing(
                    Time.timeSinceLevelLoad,
                    Mathf.Max(mission.UnloadStartedAt, mission.LastCargoReleasedAt),
                    CommanderCargoUnloadRule.UnloadProgressGraceSeconds);
            }

            float metres = CommanderGameAccess.HorizontalDistance(
                aircraft.transform.position, mission.Target.ToLocalPosition());
            if (!CommanderCargoUnloadRule.UnloadsInPlace(
                    metres,
                    aircraft.radarAlt,
                    aircraft.speed,
                    CommanderOperationsService.InsertionStallRadiusMeters,
                    CommanderCargoUnloadRule.ForcedUnloadHeightMeters,
                    CommanderCargoUnloadRule.UnloadSpeedMetersPerSecond))
            {
                return false;
            }

            BeginUnloadInPlace(
                aircraft,
                mission,
                $"{UnloadWhere(mission)}: could not land after "
                    + $"{CommanderSettings.OperationsInsertionStallTimeoutSeconds:0} s; unloading in place at "
                    + $"{aircraft.radarAlt:0} m instead.");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Where the next vehicle of a bypassed load is set down: clear ground near the transport,
    /// found by the landing-zone picker that already avoids trees, static scenery, steep ground and
    /// the sea (design section 4.3 — there is deliberately no second clear-ground search in this
    /// mod). The wanted spot is the terrain under the aircraft, stepped one spacing along a cardinal
    /// bearing for each vehicle after the first, and the picker then walks outward from there.
    /// <para>When the whole search radius is blocked the vehicle still goes down, on the terrain
    /// directly beneath the wanted spot: a vehicle on awkward ground is better than a delivery that
    /// never happens, and the insertion shield covers the arrival either way. The only false is the
    /// guard below, which means the ground could not be worked out at all and the vehicle is left
    /// where it came out of the aircraft.</para>
    /// </summary>
    private static bool TryChooseUnloadGround(
        Aircraft aircraft, CargoMission mission, int releaseIndex, out GlobalPosition ground)
    {
        // Guarded because this runs inside a Harmony postfix on the engine's own cargo activation,
        // where an escaping exception would stop the vehicle being adopted at all — the shape of two
        // incidents this delivery path has already had. A failure here costs the vehicle its chosen
        // ground and nothing else: it stays where it fell, under the shield, and is still credited.
        try
        {
            return ChooseUnloadGround(aircraft, mission, releaseIndex, out ground);
        }
        catch (System.Exception error)
        {
            CommanderPlugin.Log.LogWarning(
                $"Supply: could not choose ground for a load unloaded in place at {UnloadWhere(mission)} "
                    + $"({error.GetType().Name}); the vehicle is left where it came out.");
            ground = default;
            return false;
        }
    }

    private static bool ChooseUnloadGround(
        Aircraft aircraft, CargoMission mission, int releaseIndex, out GlobalPosition ground)
    {
        CommanderCargoUnloadRule.UnloadOffsetMeters(releaseIndex, CommanderCargoUnloadRule.UnloadVehicleSpacingMeters, out float east, out float north);
        GlobalPosition beneath = aircraft.GlobalPosition();
        GlobalPosition wanted = CommanderGameAccess.SnapToTerrain(
            new GlobalPosition(beneath.x + east, beneath.y, beneath.z + north));
        if (TryFindClearLandingZone(wanted, out ground))
        {
            return true;
        }

        ground = wanted;
        if (!mission.UnloadGroundFallbackLogged)
        {
            mission.UnloadGroundFallbackLogged = true;
            CommanderAiLog.Note(
                mission.Hq,
                $"{UnloadWhere(mission)}: no clear ground within "
                    + $"{CommanderSettings.OperationsLzSearchRadiusMeters:0} m of the transport; the load goes down "
                    + "on the terrain beneath it.");
        }

        return true;
    }

    /// <summary>The place a bypassed flight's log lines name: the point it reinforces, or the cargo
    /// itself for a flight with no point.</summary>
    private static string UnloadWhere(CargoMission mission)
    {
        return mission.InsertionPoint != null ? mission.InsertionPoint.Label : mission.CargoLabel;
    }

    /// <summary>
    /// The bypass's decision table checked at its boundaries, run at plugin load through the supply
    /// service's own <see cref="SelfCheck"/>. The cases themselves live beside the rules in
    /// <see cref="CommanderCargoUnloadRule"/>, which has no game dependency, so the same named cases
    /// can be driven outside a running game.
    /// </summary>
    private static void SelfCheckUnload()
    {
        List<string> failures = CommanderCargoUnloadRule.CollectFailures();
        if (failures.Count == 0)
        {
            CommanderPlugin.Log.LogDebug("Delivery bypass self-check passed.");
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Delivery bypass self-check FAILED: {failures[i]}");
        }
    }
}
