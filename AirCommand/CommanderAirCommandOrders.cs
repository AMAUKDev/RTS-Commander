using System;
using System.Collections.Generic;
using System.Reflection;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    /// <summary>Friendly AI aircraft that are airborne or parked and not already on a Commander mission.</summary>
    internal void CollectIdleAircraft(List<Aircraft> aircraft)
    {
        aircraft.Clear();
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq?.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID unitId in hq.factionUnits)
        {
            if (unitId.TryGetUnit(out Unit unit)
                && unit is Aircraft candidate
                && IsTaskableAircraft(candidate)
                && !missions.ContainsKey(candidate))
            {
                aircraft.Add(candidate);
            }
        }

        aircraft.Sort((left, right) => string.Compare(
            CommanderGameAccess.GetUnitLabel(left), CommanderGameAccess.GetUnitLabel(right), StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsTaskableAircraft(Aircraft? aircraft)
    {
        return aircraft != null
            && !aircraft.disabled
            && aircraft.Player == null
            && aircraft.pilots != null
            && aircraft.pilots.Length > 0
            && HasPlanePilot(aircraft)
            && CommanderGameAccess.IsFriendlyUnit(aircraft, CommanderGameAccess.GetLocalHq());
    }

    internal bool IsCommandedAircraft(Aircraft aircraft) => missions.ContainsKey(aircraft);

    /// <summary>Starts placing a mission area for an aircraft that is already in the air.</summary>
    internal void BeginAdoption(Aircraft aircraft)
    {
        if (!IsTaskableAircraft(aircraft))
        {
            SetStatus("That aircraft cannot be tasked.");
            return;
        }

        pendingAreaSelection = null;
        pendingMissionRelocation = null;
        pendingAdoption = aircraft;
        OpenOrderMap();
        UpdatePendingAreaPreview();
        SetStatus($"Select the {GetModeLabel(selectedMode)} mission area for {CommanderGameAccess.GetUnitLabel(aircraft)}.");
    }

    /// <summary>
    /// Puts a mission on an aircraft that belongs to a faction the player does not command, which is
    /// how the enemy commander tasks the airframes it buys. Everything downstream of a mission is
    /// already faction-agnostic — <c>ChooseMissionTarget</c> reads <c>aircraft.NetworkHQ</c> and that
    /// HQ's own tracking database — so this is the mission record and nothing else: no pin in the
    /// player's unit list, no status line, no map circle on the player's map.
    /// <para>
    /// It is load-bearing, not decoration. Without a mission the Basegame <c>NoTarget</c> timer flies
    /// a freshly launched AI aircraft home after 15 ticks with nothing found, which is exactly why
    /// an enemy that was buying aircraft was never seen making an attack run.
    /// </para>
    /// </summary>
    internal bool TryTaskAiAircraft(Aircraft? aircraft, AirCommandMode mode, GlobalPosition center, float radius)
    {
        FactionHQ? hq = aircraft?.NetworkHQ;
        if (aircraft == null
            || hq == null
            || aircraft.disabled
            || aircraft.Player != null
            || aircraft.pilots == null
            || aircraft.pilots.Length == 0
            || !HasPlanePilot(aircraft)
            || missions.ContainsKey(aircraft))
        {
            return false;
        }

        missions[aircraft] = new AirMission(hq, mode, center, radius, 0f, false, false, false, 0f);
        return true;
    }

    private bool TryAdoptAircraft(Aircraft aircraft, AirCommandMode mode, GlobalPosition center, float radius)
    {
        if (!IsTaskableAircraft(aircraft))
        {
            return false;
        }

        FactionHQ? hq = aircraft.NetworkHQ ?? CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return false;
        }

        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            // Adopted airframes were never bought by the Commander, so nothing is refunded on return.
            mission = new AirMission(hq, mode, center, radius, selectedTargetAltitude, TargetOrdnance, SaturationAttack, false, 0f);
            missions[aircraft] = mission;
        }
        else
        {
            mission.Mode = mode;
            mission.AreaCenter = center;
            mission.Radius = radius;
        }

        mission.Returning = false;
        mission.RtbIssued = false;
        DestroyMissionMapVisual(mission);
        RefreshMissionMapVisuals();
        CommanderSelectionService.PinMissionUnit(aircraft, "AIR COMMAND", GetModeLabel(mode));
        return true;
    }

    /// <summary>
    /// Applies a Commander travel or attack point to a single aircraft, tasking it first if
    /// it was still flying its own Basegame orders.
    /// </summary>
    internal void SetAircraftOrder(Aircraft aircraft, GlobalPosition point, Unit? attackTarget, bool append)
    {
        if (!IsTaskableAircraft(aircraft))
        {
            return;
        }

        AirCommandMode mode = attackTarget != null ? GetModeForTarget(attackTarget) : selectedMode;
        GlobalPosition center = attackTarget != null ? attackTarget.GlobalPosition() : point;
        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            if (!TryAdoptAircraft(aircraft, mode, center, GetMissionRadius(mode)))
            {
                return;
            }
            mission = missions[aircraft];
        }
        else if (attackTarget != null)
        {
            mission.Mode = mode;
            mission.AreaCenter = center;
            DestroyMissionMapVisual(mission);
        }

        // Any plain order supersedes a landing order, which is what gets an aircraft parked on a
        // base it was told to take back into the air the moment the player wants it elsewhere.
        ClearLandingOrder(aircraft, mission, release: true);

        if (!append)
        {
            mission.Route.Clear();
            mission.RouteIndex = 0;
        }

        mission.ForcedTarget = attackTarget;
        mission.Returning = false;
        if (attackTarget == null)
        {
            mission.Route.Add(point);
            // The last travel point doubles as the station the aircraft settles on.
            mission.AreaCenter = point;
            DestroyMissionMapVisual(mission);
        }

        RefreshMissionMapVisuals();
        SetStatus(attackTarget != null
            ? $"{CommanderGameAccess.GetUnitLabel(aircraft)} ordered to attack {CommanderGameAccess.GetUnitLabel(attackTarget)}."
            : $"{CommanderGameAccess.GetUnitLabel(aircraft)} travel point set.");
    }

    internal void ClearAircraftRoute(Aircraft aircraft)
    {
        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            return;
        }

        ClearLandingOrder(aircraft, mission, release: true);
        mission.Route.Clear();
        mission.RouteIndex = 0;
        mission.ForcedTarget = null;
        mission.AreaCenter = aircraft.GlobalPosition();
        DestroyMissionMapVisual(mission);
        RefreshMissionMapVisuals();
    }

    internal bool TryGetAircraftRoute(
        Aircraft aircraft,
        out IReadOnlyList<GlobalPosition> route,
        out int index,
        out Unit? attackTarget)
    {
        if (missions.TryGetValue(aircraft, out AirMission mission))
        {
            route = mission.Route;
            index = mission.RouteIndex;
            attackTarget = mission.ForcedTarget;
            return true;
        }

        route = Array.Empty<GlobalPosition>();
        index = 0;
        attackTarget = null;
        return false;
    }

    private static AirCommandMode GetModeForTarget(Unit target)
    {
        return target is Aircraft or Missile ? AirCommandMode.AirGuard : AirCommandMode.Cas;
    }

    internal void BeginMissionAreaEdit(Aircraft aircraft)
    {
        if (!missions.ContainsKey(aircraft))
        {
            return;
        }

        pendingAreaSelection = null;
        pendingMissionRelocation = aircraft;
        selectedMissionAircraft = aircraft;
        OpenOrderMap();
        UpdatePendingAreaPreview();
        RefreshMissionMapVisuals();
        SetStatus("Select the new mission-area center on the tactical map or in the 3D world.");
    }

    internal void SelectPrimaryWeapon(int index)
    {
        selectedPrimaryWeaponIndex = index >= 0 && index < weaponOptions.Count ? index : -1;
        if (SelectedPrimaryWeapon != null && SameMountType(SelectedPrimaryWeapon, SelectedSecondaryWeapon))
        {
            selectedSecondaryWeaponIndex = -1;
        }
        ApplySelectedWeaponsAndSort();
    }

    internal void SelectSecondaryWeapon(int index)
    {
        WeaponMount? candidate = index >= 0 && index < weaponOptions.Count ? weaponOptions[index] : null;
        selectedSecondaryWeaponIndex = candidate != null && !SameMountType(candidate, SelectedPrimaryWeapon) ? index : -1;
        ApplySelectedWeaponsAndSort();
    }

    internal void BeginAreaSelection()
    {
        AirMissionOption? option = SelectedOption;
        AirbaseOption? airbase = SelectedAirbase;
        if (option == null)
        {
            SetStatus("No aircraft with configurable hardpoints is available.");
            return;
        }

        if (SelectedPrimaryWeapon == null || GetPrimaryWeaponCount(option) <= 0)
        {
            SetStatus("Select a primary weapon supported by the selected aircraft.");
            return;
        }

        if (airbase == null)
        {
            SetStatus("No compatible friendly airbase is available.");
            return;
        }

        // Refuse before the map opens, not after the area is placed. SpawnMission checks the same
        // thing, but by then the player has picked a target and watched nothing happen, which reads
        // as a broken button rather than an empty treasury.
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq != null
            && hq.GetUnitSupply(option.Definition) <= 0
            && hq.factionFunds < option.Definition.value)
        {
            string price = UnitConverter.ValueReading(option.Definition.value)
                ?? option.Definition.value.ToString("F1");
            SetStatus($"Cannot afford {GetAircraftLabel(option.Definition)} ({price}). "
                + $"Faction funds {CommanderEconomyService.FundsLabel(hq.factionFunds)}.");
            return;
        }

        pendingMissionRelocation = null;
        pendingAreaSelection = new PendingAreaSelection(option, airbase.Airbase);
        OpenOrderMap();
        UpdatePendingAreaPreview();
        SetStatus("Select the mission area on the tactical map or in the 3D world. The game's Cancel binding cancels.");
    }

    /// <summary>
    /// Arms the map an order is placed on. That is the mod's own tactical map, never the fullscreen
    /// game map: an order that swapped the whole screen out from under the player and back again is
    /// the reason this pair exists. The flag records whether this order is what opened the map, so
    /// only an order that opened it puts it away.
    /// </summary>
    private void OpenOrderMap()
    {
        openedMapForOrder = tacticalMapService.OpenForPlacement();
        tacticalMapService.SuppressMapFollow = true;
        mapClickTracker.Reset();
    }

    private void CloseOrderMap()
    {
        tacticalMapService.SuppressMapFollow = false;
        mapClickTracker.Reset();
        if (openedMapForOrder)
        {
            tacticalMapService.Close();
        }
        openedMapForOrder = false;
    }

    internal bool TrySetAreaFromWorld(Vector2 screenPosition)
    {
        if (!AwaitingAreaSelection)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition target))
        {
            SetStatus("No valid mission area was found under the cursor.");
            return true;
        }

        CompleteAreaSelection(target);
        return true;
    }

    internal string GetOptionLabel(AirMissionOption option)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        int supply = hq?.GetUnitSupply(option.Definition) ?? 0;
        string cost = UnitConverter.ValueReading(option.Definition.value) ?? option.Definition.value.ToString("F1");
        return $"{GetAircraftLabel(option.Definition)}  |  PRI {GetPrimaryWeaponCount(option)} / SEC {GetSecondaryWeaponCount(option)}\nSupply {supply}  |  Cost {cost}";
    }

    internal string GetAirbaseLabel(AirbaseOption option)
    {
        string distance = UnitConverter.DistanceReading(option.Distance) ?? $"{option.Distance:F0} m";
        return $"{(option.Ready ? "READY" : "BUSY")}  |  {option.Label}  |  {distance}";
    }
}
