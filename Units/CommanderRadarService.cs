using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderRadarService : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive, ICommanderResetSession
{
    private const float ControlRangeMeters = 300f;
    private const float RefreshIntervalSeconds = 1f;
    private const float StatusDurationSeconds = 4f;

    private static readonly FieldInfo? FireControlModeField = AccessTools.Field(typeof(FireControl), "targetAcquisitionMode");
    private static readonly FieldInfo? FireControlTurretsField = AccessTools.Field(typeof(FireControl), "turrets");
    private static readonly FieldInfo? TurretModeField = AccessTools.Field(typeof(Turret), "targetAcquisitionMode");
    private static readonly MethodInfo? AircraftRpcToggleRadarMethod = AccessTools.Method(typeof(Aircraft), "RpcToggleRadar");

    private readonly CommanderSelectionService selectionService;
    private readonly HashSet<Unit> offlineRadarUnits = new();
    private readonly HashSet<Unit> nearbyRadarUnits = new();
    private readonly HashSet<Unit> nearbyLauncherUnits = new();
    private readonly Dictionary<Unit, Radar[]> radarsByUnit = new();
    private readonly Dictionary<Unit, Turret[]> turretsByUnit = new();

    private FactionHQ? boundHq;
    private Unit? inspectedUnit;
    private RadarState? focusedState;
    private float nextRefreshTime;
    private float statusUntil;
    private string statusText = string.Empty;

    internal static CommanderRadarService? Instance { get; private set; }

    internal CommanderRadarService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
    }

    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;

    public void Activate()
    {
        nextRefreshTime = CommanderScheduler.Stagger("radar.nearby", RefreshIntervalSeconds, 0.4f);
        RefreshBindings();
        SyncFocusedUnit();
    }

    public void Deactivate()
    {
        UnbindHq();
        inspectedUnit = null;
        focusedState = null;
    }

    public void ResetSession()
    {
        UnbindHq();
        inspectedUnit = null;
        focusedState = null;
        offlineRadarUnits.Clear();
        nearbyRadarUnits.Clear();
        nearbyLauncherUnits.Clear();
        statusText = string.Empty;
    }

    public void TickActive()
    {
        RefreshBindings();
        SyncFocusedUnit();
        if (focusedState == null
            || !focusedState.IsCommandTruck
            || !CommanderScheduler.IsDue(ref nextRefreshTime, RefreshIntervalSeconds))
        {
            return;
        }

        RefreshNearbyCounts(focusedState);
    }

    internal bool TryGetFocusedState(out RadarState state)
    {
        state = focusedState!;
        return state != null && state.Unit != null && !state.Unit.disabled;
    }

    internal void ToggleRadar()
    {
        if (!TryGetFocusedState(out RadarState state) || state.Radars.Length == 0)
        {
            SetStatus("The selected unit has no radar to switch.");
            return;
        }

        if (!CommanderGameAccess.IsFriendlyUnit(state.Unit, CommanderGameAccess.GetLocalHq()))
        {
            SetStatus("Enemy unit systems cannot be controlled.");
            return;
        }

        bool enable = !state.IsRadarOnline;
        if (state.Unit is Aircraft aircraft && aircraft.radar is Radar aircraftRadar)
        {
            if (enable && !aircraftRadar.IsOperational())
            {
                SetStatus("The selected radar is damaged and cannot be activated.");
                return;
            }

            if (AircraftRpcToggleRadarMethod != null && aircraft.IsServer)
            {
                AircraftRpcToggleRadarMethod.Invoke(aircraft, new object[] { enable });
            }
            else
            {
                aircraftRadar.activated = enable;
            }

            state.IsRadarOnline = aircraftRadar.activated;
            if (state.IsRadarOnline) offlineRadarUnits.Remove(state.Unit);
            else offlineRadarUnits.Add(state.Unit);
            SetStatus(state.IsRadarOnline ? "Aircraft radar online." : "Aircraft radar offline.");
            return;
        }

        if (enable && !HasOperationalRadar(state.Radars))
        {
            SetStatus("The selected radar is damaged and cannot be activated.");
            return;
        }

        ApplyRadarState(state.Unit, state.Radars, enable);
        state.IsRadarOnline = enable;
        SetStatus(enable ? "Radar online." : "Radar offline. ARAD seekers can no longer track its emission.");
    }

    /// <summary>
    /// Switches one unit's radars without it being selected, so a waypoint EMCON action can
    /// run on a unit the player is not currently looking at.
    /// </summary>
    internal bool SetUnitRadar(Unit? unit, bool enable)
    {
        if (unit == null
            || unit.disabled
            || !CommanderGameAccess.IsFriendlyUnit(unit, CommanderGameAccess.GetLocalHq()))
        {
            return false;
        }

        if (radarsByUnit.TryGetValue(unit, out Radar[] cached))
        {
            if (enable && !HasOperationalRadar(cached))
            {
                return false;
            }

            ApplyRadarState(unit, cached, enable);
            return true;
        }

        Radar[] radars = unit.GetComponentsInChildren<Radar>();
        if (radars.Length == 0 || (enable && !HasOperationalRadar(radars)))
        {
            return false;
        }

        ApplyRadarState(unit, radars, enable);
        return true;
    }

    private void ApplyRadarState(Unit unit, Radar[] radars, bool enable)
    {
        for (int i = 0; i < radars.Length; i++)
        {
            Radar radar = radars[i];
            if (radar == null || !radar.IsOperational())
            {
                continue;
            }

            radar.activated = enable;
            radar.enabled = enable;
            if (!enable)
            {
                radar.detectedTargets.Clear();
                radar.ResetRotators();
                ClearTargetsUsingRadar(radar);
            }
        }

        if (enable)
        {
            offlineRadarUnits.Remove(unit);
        }
        else
        {
            offlineRadarUnits.Add(unit);
        }

        if (focusedState != null && ReferenceEquals(focusedState.Unit, unit))
        {
            focusedState.IsRadarOnline = enable;
        }
    }

    internal static bool IsUnitRadarOffline(Unit unit)
    {
        return Instance?.offlineRadarUnits.Contains(unit) == true;
    }

    private void SyncFocusedUnit()
    {
        Unit? unit = selectionService.FocusedSelection;
        if (unit == null || unit.disabled)
        {
            inspectedUnit = null;
            focusedState = null;
            return;
        }

        if (ReferenceEquals(unit, inspectedUnit))
        {
            UpdateFocusedRadarStatus(unit);
            return;
        }

        inspectedUnit = unit;

        Radar[] radars = unit.GetComponentsInChildren<Radar>();
        FireControl? batteryController = FindBatteryController(unit);
        if (radars.Length == 0 && batteryController == null)
        {
            focusedState = null;
            return;
        }

        focusedState = new RadarState(unit, radars, batteryController);
        nextRefreshTime = CommanderScheduler.Stagger("radar.nearby.focus", RefreshIntervalSeconds, 0.2f);
        UpdateFocusedRadarStatus(unit);
    }

    private void UpdateFocusedRadarStatus(Unit unit)
    {
        if (focusedState == null || !ReferenceEquals(focusedState.Unit, unit))
        {
            return;
        }

        focusedState.IsRadarOnline = unit is Aircraft aircraft && aircraft.radar is Radar aircraftRadar
            ? aircraftRadar.IsOperational() && aircraftRadar.activated
            : IsAnyRadarOnline(focusedState.Radars);
        if (focusedState.Radars.Length > 0 && !focusedState.IsRadarOnline)
        {
            offlineRadarUnits.Add(unit);
        }
        else
        {
            offlineRadarUnits.Remove(unit);
        }
    }

    private void RefreshNearbyCounts(RadarState state)
    {
        nearbyRadarUnits.Clear();
        nearbyLauncherUnits.Clear();

        FactionHQ? hq = state.Unit.NetworkHQ;
        if (hq == null)
        {
            state.NearbyRadarCount = 0;
            state.NearbyLauncherCount = 0;
            return;
        }

        GlobalPosition center = state.Unit.GlobalPosition();
        foreach (KeyValuePair<Unit, Radar[]> entry in radarsByUnit)
        {
            Unit radarUnit = entry.Key;
            if (!radarUnit.disabled
                && radarUnit.NetworkHQ == hq
                && FastMath.InRange(center, radarUnit.GlobalPosition(), ControlRangeMeters))
            {
                nearbyRadarUnits.Add(radarUnit);
            }
        }

        foreach (KeyValuePair<Unit, Turret[]> entry in turretsByUnit)
        {
            Unit launcherUnit = entry.Key;
            if (launcherUnit.disabled
                || launcherUnit.NetworkHQ != hq
                || !FastMath.InRange(center, launcherUnit.GlobalPosition(), ControlRangeMeters))
            {
                continue;
            }

            Turret[] turrets = entry.Value;
            for (int i = 0; i < turrets.Length; i++)
            {
                if (turrets[i] != null && IsRadarSeekingTurret(turrets[i]))
                {
                    nearbyLauncherUnits.Add(launcherUnit);
                    break;
                }
            }
        }

        state.NearbyRadarCount = nearbyRadarUnits.Count;
        state.NearbyLauncherCount = nearbyLauncherUnits.Count;
    }

    private void RefreshBindings()
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (ReferenceEquals(hq, boundHq))
        {
            return;
        }

        UnbindHq();
        boundHq = hq;
        if (boundHq?.factionUnits == null)
        {
            return;
        }

        boundHq.onRegisterUnit += OnRegisterUnit;
        boundHq.onRemoveUnit += OnRemoveUnit;
        foreach (PersistentID unitId in boundHq.factionUnits)
        {
            if (unitId.TryGetUnit(out Unit unit))
            {
                CacheUnitSystems(unit);
            }
        }
    }

    private void UnbindHq()
    {
        if (boundHq != null)
        {
            boundHq.onRegisterUnit -= OnRegisterUnit;
            boundHq.onRemoveUnit -= OnRemoveUnit;
        }

        boundHq = null;
        radarsByUnit.Clear();
        turretsByUnit.Clear();
    }

    private void OnRegisterUnit(Unit unit)
    {
        CacheUnitSystems(unit);
    }

    private void OnRemoveUnit(Unit unit)
    {
        radarsByUnit.Remove(unit);
        turretsByUnit.Remove(unit);
    }

    private void CacheUnitSystems(Unit unit)
    {
        if (unit == null || unit.disabled || unit is not GroundVehicle || unit.NetworkHQ != boundHq)
        {
            return;
        }

        Radar[] radars = unit.GetComponentsInChildren<Radar>();
        if (radars.Length > 0)
        {
            radarsByUnit[unit] = radars;
        }

        Turret[] turrets = unit.GetComponentsInChildren<Turret>();
        if (turrets.Length > 0)
        {
            turretsByUnit[unit] = turrets;
        }
    }

    private static FireControl? FindBatteryController(Unit unit)
    {
        FireControl[] fireControls = unit.GetComponentsInChildren<FireControl>();
        for (int i = 0; i < fireControls.Length; i++)
        {
            if (string.Equals(FireControlModeField?.GetValue(fireControls[i])?.ToString(), "searchForRadar", System.StringComparison.Ordinal))
            {
                return fireControls[i];
            }
        }

        return null;
    }

    private static bool IsRadarSeekingTurret(Turret turret)
    {
        return string.Equals(TurretModeField?.GetValue(turret)?.ToString(), "searchForRadar", System.StringComparison.Ordinal);
    }

    private static bool IsAnyRadarOnline(Radar[] radars)
    {
        for (int i = 0; i < radars.Length; i++)
        {
            if (radars[i] != null && radars[i].IsOperational() && radars[i].activated)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasOperationalRadar(Radar[] radars)
    {
        for (int i = 0; i < radars.Length; i++)
        {
            if (radars[i] != null && radars[i].IsOperational())
            {
                return true;
            }
        }

        return false;
    }

    private static void ClearTargetsUsingRadar(Radar radar)
    {
        FireControl[] fireControls = Object.FindObjectsOfType<FireControl>();
        for (int i = 0; i < fireControls.Length; i++)
        {
            FireControl fireControl = fireControls[i];
            if (fireControl == null || !fireControl.TryGetRadar(out Radar linkedRadar) || linkedRadar != radar)
            {
                continue;
            }

            List<Turret>? turrets = FireControlTurretsField?.GetValue(fireControl) as List<Turret>;
            if (turrets == null)
            {
                continue;
            }

            for (int turretIndex = 0; turretIndex < turrets.Count; turretIndex++)
            {
                turrets[turretIndex]?.SetTargetFromController(null);
            }
        }

        Turret[] sceneTurrets = Object.FindObjectsOfType<Turret>();
        for (int i = 0; i < sceneTurrets.Length; i++)
        {
            Unit? turretUnit = sceneTurrets[i]?.GetAttachedUnit();
            if (turretUnit != null && turretUnit.radar == radar)
            {
                sceneTurrets[i].SetTargetFromController(null);
            }
        }
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }

    internal sealed class RadarState
    {
        internal RadarState(Unit unit, Radar[] radars, FireControl? batteryController)
        {
            Unit = unit;
            Radars = radars;
            BatteryController = batteryController;
            IsRadarOnline = unit is Aircraft aircraft && aircraft.radar is Radar aircraftRadar
                ? aircraftRadar.IsOperational() && aircraftRadar.activated
                : IsAnyRadarOnline(radars);
        }

        internal Unit Unit { get; }
        internal Radar[] Radars { get; }
        internal FireControl? BatteryController { get; }
        internal bool IsCommandTruck => BatteryController != null;
        internal bool HasRadar => Radars.Length > 0;
        internal bool IsRadarOnline { get; set; }
        internal int NearbyRadarCount { get; set; }
        internal int NearbyLauncherCount { get; set; }
    }
}
