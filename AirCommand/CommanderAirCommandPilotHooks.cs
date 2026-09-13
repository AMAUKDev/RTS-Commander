using System;
using System.Collections.Generic;
using System.Reflection;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    internal static void NotifyFactionUnitRegistered(FactionHQ hq, Unit unit)
    {
        Instance?.TryAssignPendingAircraft(hq, unit);
    }

    internal static void NotifyAircraftReturned(Aircraft aircraft)
    {
        Instance?.HandleAircraftReturned(aircraft);
    }

    internal static void NotifyUnitDisabled(Unit unit)
    {
        if (unit is Aircraft aircraft)
        {
            Instance?.RemoveMission(aircraft);
        }
    }

    internal static bool TryChooseMissionTarget(
        Unit searcher,
        List<WeaponStation> stations,
        out CombatAI.TargetSearchResults result)
    {
        result = default;
        if (Instance == null
            || searcher is not Aircraft aircraft
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission))
        {
            return false;
        }

        // Going home means no targets, from the moment the order is given. A landing order used to
        // leave Returning false until the 9 km hand-over, so an aircraft with gun ammunition left
        // — a Brawler's cannon, a Chicane's — kept picking fights all the way back instead of
        // flying the route, which read as RTB being ignored. Guns count as ordnance to the pilot AI.
        if (mission.Returning || mission.LandingBase != null)
        {
            result = new CombatAI.TargetSearchResults(null!, null!, 0f, true);
            return true;
        }

        result = Instance.ChooseMissionTarget(aircraft, stations, mission);
        // An empty rack ends a *mission*, not an order. A commanded aircraft with travel points
        // still to fly keeps flying them — otherwise a transport or a gun-only airframe turned for
        // home the instant it was told to go somewhere, which reads as the order being ignored.
        if (result.outOfAmmo && mission.RouteIndex >= mission.Route.Count)
        {
            mission.Returning = true;
        }
        return true;
    }

    private void HandleAircraftReturned(Aircraft aircraft)
    {
        if (missions.TryGetValue(aircraft, out AirMission mission)
            && mission.PurchasedWithFunds)
        {
            // Basegame ReturnToInventory has just restored one airframe. Convert
            // that temporary purchased airframe back into its original funds.
            mission.Hq.ModifyUnitSupply(aircraft.definition, -1);
            mission.Hq.AddFunds(mission.PurchaseCost);
        }

        RemoveMission(aircraft);
    }

    internal static bool TryBuildAradSaturationTargets(
        Aircraft aircraft,
        WeaponStation station,
        out int targetCount)
    {
        targetCount = 0;
        if (Instance == null
            || aircraft == null
            || station == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.Returning
            || mission.Mode != AirCommandMode.Arad
            || !mission.SaturationAttack)
        {
            return false;
        }

        WeaponManager manager = aircraft.weaponManager;
        if (station.SalvoInProgress)
        {
            targetCount = manager.GetTargetList().Count;
            return true;
        }

        FactionHQ? hq = aircraft.NetworkHQ;
        WeaponInfo? info = station.WeaponInfo;
        if (hq == null || info == null || station.Ammo <= 0)
        {
            manager.ClearTargetList();
            return true;
        }

        List<Unit> candidates = new();
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo tracking = entry.Value;
            if (!tracking.TryGetUnit(out Unit target)
                || target == null
                || target.disabled
                || target.NetworkHQ == null
                || target.NetworkHQ == hq
                || !IsTargetEligible(target, AirCommandMode.Arad, targetOrdnance: false)
                || !FastMath.InRange(tracking.GetPosition(), mission.AreaCenter, mission.Radius)
                || !hq.IsTargetPositionAccurate(target, 1000f))
            {
                continue;
            }

            float range = FastMath.Distance(tracking.GetPosition(), aircraft.GlobalPosition());
            Vector3 direction = tracking.GetPosition() - aircraft.GlobalPosition();
            if (range < info.targetRequirements.minRange
                || range > info.targetRequirements.maxRange
                || Vector3.Angle(direction, aircraft.transform.forward) >= info.targetRequirements.minAlignment
                || (info.targetRequirements.lineOfSight && !target.LineOfSight(aircraft.transform.position, 1000f))
                || station.CalcOpportunityThreat(target.definition, aircraft).opportunity <= 0f)
            {
                continue;
            }

            candidates.Add(target);
        }

        manager.ClearTargetList();
        if (candidates.Count == 0)
        {
            return true;
        }

        int missileCount = Mathf.Min(station.Ammo, 32);
        for (int i = 0; i < missileCount; i++)
        {
            manager.AddTargetList(candidates[i % candidates.Count]);
        }

        targetCount = missileCount;
        return true;
    }

    internal static bool TryGetMissionHoldPoint(AIPilotCombatModes state, out GlobalPosition point)
    {
        point = default;
        if (Instance == null || Instance.missions.Count == 0)
        {
            return false;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null || !Instance.missions.TryGetValue(aircraft, out AirMission mission))
        {
            return false;
        }

        if (mission.Returning) return false;
        point = Instance.GetActiveRoutePoint(aircraft, mission);
        return true;
    }

    /// <summary>Current travel point, advancing the route as the aircraft reaches each one.</summary>
    private GlobalPosition GetActiveRoutePoint(Aircraft aircraft, AirMission mission)
    {
        while (mission.RouteIndex < mission.Route.Count)
        {
            GlobalPosition point = mission.Route[mission.RouteIndex];
            if (FastMath.Distance(point, aircraft.GlobalPosition()) > AircraftWaypointRadiusMeters)
            {
                return point;
            }

            mission.RouteIndex++;
        }

        return mission.AreaCenter;
    }

    internal static void ApplyMissionTargetAltitude(AIPilotCombatModes state, FieldInfo? targetHeightField)
    {
        if (Instance == null || Instance.missions.Count == 0 || targetHeightField == null)
        {
            return;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.TargetAltitude <= 0f)
        {
            return;
        }

        targetHeightField.SetValue(state, mission.TargetAltitude);
    }

    internal static void ConstrainMissionDestination(AIPilotCombatModes state, FieldInfo? destinationField)
    {
        if (Instance == null || destinationField == null)
        {
            return;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.Returning
            || mission.RouteIndex < mission.Route.Count
            || !KeepsStationInMissionArea(mission.Mode))
        {
            return;
        }

        object? rawDestination = destinationField.GetValue(state);
        if (rawDestination is not GlobalPosition destination)
        {
            return;
        }

        float radius = Mathf.Max(mission.Radius, 1000f);
        Vector3 destinationOffset = destination - mission.AreaCenter;
        destinationOffset.y = 0f;
        Vector3 aircraftOffset = aircraft.GlobalPosition() - mission.AreaCenter;
        aircraftOffset.y = 0f;

        float destinationLimit = radius * 0.75f;
        if (aircraftOffset.magnitude > radius * 0.9f)
        {
            destination = mission.AreaCenter;
        }
        else if (destinationOffset.magnitude > destinationLimit)
        {
            destination = mission.AreaCenter + destinationOffset.normalized * destinationLimit;
        }
        else
        {
            return;
        }

        destinationField.SetValue(state, destination);
    }

    private CombatAI.TargetSearchResults ChooseMissionTarget(
        Aircraft aircraft,
        List<WeaponStation> stations,
        AirMission mission)
    {
        Unit? bestTarget = null;
        WeaponStation? bestStation = null;
        float bestOpportunity = 0f;
        float bestScore = 0f;
        bool outOfAmmo = mission.Mode != AirCommandMode.AwacsJammer || aircraft.radar == null;
        FactionHQ? hq = aircraft.NetworkHQ;
        if (hq == null)
        {
            return new CombatAI.TargetSearchResults(null!, null!, 0f, true);
        }

        if (mission.ForcedTarget != null && mission.ForcedTarget.disabled)
        {
            mission.ForcedTarget = null;
        }

        for (int stationIndex = 0; stationIndex < stations.Count; stationIndex++)
        {
            WeaponStation station = stations[stationIndex];
            if (station == null || station.Cargo || !IsStationEligible(station, mission.Mode))
            {
                continue;
            }

            if (station.Ammo <= 0)
            {
                continue;
            }

            outOfAmmo = false;
            foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
            {
                TrackingInfo tracking = entry.Value;
                if (!tracking.TryGetUnit(out Unit target)
                    || target == null
                    || target.disabled
                    || target.NetworkHQ == null
                    || target.NetworkHQ == hq
                    || !IsTargetEligible(target, mission.Mode, mission.TargetOrdnance)
                    || (TargetsRestrictedToMissionArea(mission.Mode)
                        && !ReferenceEquals(target, mission.ForcedTarget)
                        && !FastMath.InRange(tracking.GetPosition(), mission.AreaCenter, mission.Radius)))
                {
                    continue;
                }

                float range = Mathf.Max(FastMath.Distance(tracking.GetPosition(), aircraft.GlobalPosition()), 100f);
                if (mission.Mode == AirCommandMode.AirGuard
                    && range > station.WeaponInfo.targetRequirements.maxRange * 1.05f)
                {
                    continue;
                }
                if (mission.Mode == AirCommandMode.AwacsJammer
                    && (range > station.WeaponInfo.targetRequirements.maxRange
                        || !target.LineOfSight(aircraft.transform.position, 1000f)))
                {
                    continue;
                }

                OpportunityThreat assessment = CombatAI.AnalyzeTarget(
                    station,
                    aircraft,
                    tracking,
                    0f,
                    range,
                    maxRangeMultiplier: 100f);
                float score = assessment.GetCombinedScore() / range;
                if (ReferenceEquals(target, mission.ForcedTarget))
                {
                    // A commanded target outranks anything the automatic search finds.
                    score *= 1000f;
                }
                float requiredAccuracy = mission.Mode == AirCommandMode.AwacsJammer ? 100f : 1000f;
                if (score <= bestScore || !hq.IsTargetPositionAccurate(target, requiredAccuracy))
                {
                    continue;
                }

                bestScore = score;
                bestOpportunity = assessment.opportunity;
                bestTarget = target;
                bestStation = station;
            }
        }

        return new CombatAI.TargetSearchResults(bestTarget!, bestStation!, bestOpportunity, outOfAmmo);
    }
}
