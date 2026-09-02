using System;
using System.Collections.Generic;
using System.Reflection;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive, ICommanderTickPersistent, ICommanderResetSession
{
    private const float PendingSpawnTimeoutSeconds = 45f;
    private const float StatusDurationSeconds = 6f;
    private const float MissionPruneIntervalSeconds = 2f;
    private const float AircraftWaypointRadiusMeters = 1500f;

    private readonly CommanderTacticalMapService tacticalMapService;
    private readonly CommanderMapClickTracker mapClickTracker = new();
    private readonly List<AirMissionOption> options = new();
    private readonly List<WeaponMount> weaponOptions = new();
    private readonly List<AirbaseOption> airbases = new();
    private readonly Dictionary<Aircraft, AirMission> missions = new();
    private readonly List<Aircraft> staleAircraft = new();

    private PendingAreaSelection? pendingAreaSelection;
    private Aircraft? pendingMissionRelocation;
    private Aircraft? pendingAdoption;
    private PendingAircraftSpawn? pendingAircraftSpawn;
    private AirCommandMode selectedMode = AirCommandMode.AirGuard;
    private int selectedOptionIndex;
    private int selectedAirbaseIndex;
    private int selectedPrimaryWeaponIndex = -1;
    private int selectedSecondaryWeaponIndex = -1;
    private LoadoutBalance selectedLoadoutBalance = LoadoutBalance.Primary;
    private float selectedTargetAltitude;
    private float nextAirbaseRefreshAt;
    private float nextMissionPruneAt;
    private float statusUntil;
    private string statusText = string.Empty;
    private bool uiVisible;
    private Rect areaSelectionBlockingRect;
    private Rect areaSelectionSecondaryBlockingRect;
    private Aircraft? selectedMissionAircraft;

    internal static CommanderAirCommandService? Instance { get; private set; }

    internal CommanderAirCommandService(CommanderTacticalMapService tacticalMapService)
    {
        this.tacticalMapService = tacticalMapService;
        if (Enum.TryParse(CommanderSettings.AirCommandMode, out AirCommandMode savedMode)) selectedMode = savedMode;
        if (Enum.TryParse(CommanderSettings.AirLoadoutBalance, out LoadoutBalance savedBalance)) selectedLoadoutBalance = savedBalance;
        selectedTargetAltitude = CommanderSettings.AirTargetAltitude;
        Instance = this;
    }

    internal AirCommandMode SelectedMode => selectedMode;
    internal IReadOnlyList<AirMissionOption> Options => options;
    internal IReadOnlyList<AirbaseOption> Airbases => airbases;
    internal IReadOnlyList<WeaponMount> WeaponOptions => weaponOptions;
    internal int SelectedOptionIndex => selectedOptionIndex;
    internal int SelectedAirbaseIndex => selectedAirbaseIndex;
    internal int SelectedPrimaryWeaponIndex => selectedPrimaryWeaponIndex;
    internal int SelectedSecondaryWeaponIndex => selectedSecondaryWeaponIndex;
    internal LoadoutBalance SelectedLoadoutBalance => selectedLoadoutBalance;
    internal float SelectedTargetAltitude => selectedTargetAltitude;
    internal bool TargetOrdnance { get => CommanderSettings.AirGuardTargetOrdnance; set => CommanderSettings.AirGuardTargetOrdnance = value; }
    internal bool SaturationAttack { get => CommanderSettings.AradSaturationAttack; set => CommanderSettings.AradSaturationAttack = value; }
    internal bool IncludeInternalCannons
    {
        get => CommanderSettings.AirIncludeInternalCannons;
        set
        {
            if (CommanderSettings.AirIncludeInternalCannons == value) return;
            CommanderSettings.AirIncludeInternalCannons = value;
            ApplySelectedWeaponsAndSort();
        }
    }
    internal bool AwaitingAreaSelection => pendingAreaSelection != null
        || pendingMissionRelocation != null
        || pendingAdoption != null;
    internal float PendingMissionRadius => pendingAreaSelection != null
        ? GetMissionRadius(pendingAreaSelection.Option.Mode)
        : pendingMissionRelocation != null && missions.TryGetValue(pendingMissionRelocation, out AirMission relocationMission)
            ? relocationMission.Radius
            : 0f;
    internal int ActiveMissionCount => missions.Count;
    internal bool IsUiVisible => uiVisible;
    internal bool CanLaunchSelected => SelectedOption != null
        && SelectedPrimaryWeapon != null
        && GetPrimaryWeaponCount(SelectedOption) > 0
        && SelectedAirbase != null;
    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;
    internal WeaponMount? SelectedPrimaryWeapon => selectedPrimaryWeaponIndex >= 0 && selectedPrimaryWeaponIndex < weaponOptions.Count
        ? weaponOptions[selectedPrimaryWeaponIndex]
        : null;
    internal WeaponMount? SelectedSecondaryWeapon => selectedSecondaryWeaponIndex >= 0 && selectedSecondaryWeaponIndex < weaponOptions.Count
        ? weaponOptions[selectedSecondaryWeaponIndex]
        : null;

    internal AirMissionOption? SelectedOption => options.Count == 0
        ? null
        : options[Mathf.Clamp(selectedOptionIndex, 0, options.Count - 1)];

    internal AirbaseOption? SelectedAirbase => airbases.Count == 0
        ? null
        : airbases[Mathf.Clamp(selectedAirbaseIndex, 0, airbases.Count - 1)];

    public void Activate()
    {
        nextMissionPruneAt = CommanderScheduler.Stagger("air-command.prune", MissionPruneIntervalSeconds, 0.7f);
        nextAirbaseRefreshAt = Time.unscaledTime;
        RefreshMissionMapVisuals();
    }

    public void Deactivate()
    {
        uiVisible = false;
        CancelAreaSelection(showStatus: false);
        ClearMissionMapVisuals();
    }

    public void ResetSession()
    {
        uiVisible = false;
        pendingAreaSelection = null;
        pendingMissionRelocation = null;
        pendingAdoption = null;
        pendingAircraftSpawn = null;
        options.Clear();
        weaponOptions.Clear();
        airbases.Clear();
        ClearMissionMapVisuals();
        missions.Clear();
        staleAircraft.Clear();
        mapClickTracker.Reset();
        selectedPrimaryWeaponIndex = -1;
        selectedSecondaryWeaponIndex = -1;
        statusText = string.Empty;
    }

    public void TickActive()
    {
        if (AwaitingAreaSelection)
        {
            if (CommanderGameInput.CancelDown)
            {
                CancelAreaSelection(showStatus: true);
            }
            else
            {
                TryHandleTacticalMapClick();
            }
        }

        if (uiVisible && Time.unscaledTime >= nextAirbaseRefreshAt)
        {
            nextAirbaseRefreshAt = Time.unscaledTime + 0.5f;
            RefreshAirbases();
        }
    }

    public void TickPersistent()
    {
        if (pendingAircraftSpawn != null && Time.unscaledTime > pendingAircraftSpawn.ExpiresAt)
        {
            PendingAircraftSpawn timedOutSpawn = pendingAircraftSpawn;
            pendingAircraftSpawn = null;
            if (timedOutSpawn.PurchasedWithFunds)
            {
                timedOutSpawn.Hq.AddFunds(timedOutSpawn.PurchaseCost);
            }
            else
            {
                timedOutSpawn.Hq.ModifyUnitSupply(timedOutSpawn.Option.Definition, 1);
            }
            SetStatus($"Aircraft spawn timed out for {GetAircraftLabel(timedOutSpawn.Option.Definition)}; cost restored.");
        }

        if (CommanderScheduler.IsDue(ref nextMissionPruneAt, MissionPruneIntervalSeconds))
        {
            PruneMissions();
            RefreshMissionMapVisuals();
            ProcessReturningMissions();
        }

    }

    internal void SetUiVisible(bool visible)
    {
        uiVisible = visible;
        if (visible)
        {
            RefreshOptions();
        }
    }

    internal void SelectMode(AirCommandMode mode)
    {
        if (selectedMode == mode && options.Count > 0)
        {
            return;
        }

        selectedMode = mode;
        CommanderSettings.AirCommandMode = mode.ToString();
        selectedOptionIndex = 0;
        selectedAirbaseIndex = 0;
        selectedPrimaryWeaponIndex = -1;
        selectedSecondaryWeaponIndex = -1;
        RefreshOptions();
    }

    internal void SelectLoadoutBalance(LoadoutBalance balance)
    {
        if (selectedLoadoutBalance == balance)
        {
            return;
        }

        selectedLoadoutBalance = balance;
        CommanderSettings.AirLoadoutBalance = balance.ToString();
        ApplySelectedWeaponsAndSort();
    }

    internal void CancelAreaSelection()
    {
        CancelAreaSelection(showStatus: true);
    }

    internal void SetTargetAltitude(float altitude)
    {
        selectedTargetAltitude = SupportsTargetAltitude(selectedMode) ? Mathf.Max(altitude, 0f) : 0f;
        CommanderSettings.AirTargetAltitude = selectedTargetAltitude;
    }

    internal bool SupportsTargetAltitude() => SupportsTargetAltitude(selectedMode);

    internal void SetAreaSelectionBlockingRects(Rect primary, Rect secondary)
    {
        areaSelectionBlockingRect = primary;
        areaSelectionSecondaryBlockingRect = secondary;
    }

    internal static bool SupportsTargetAltitude(AirCommandMode mode)
    {
        return mode == AirCommandMode.AwacsJammer || mode == AirCommandMode.AirGuard;
    }

    internal void SelectOption(int index)
    {
        if (index < 0 || index >= options.Count)
        {
            return;
        }

        selectedOptionIndex = index;
        RefreshAirbases();
    }

    internal void SelectAirbase(int index)
    {
        if (index >= 0 && index < airbases.Count)
        {
            selectedAirbaseIndex = index;
        }
    }

    internal bool TrySelectAirbaseFromMap(Airbase? airbase)
    {
        if (!uiVisible || airbase == null)
        {
            return false;
        }

        for (int i = 0; i < airbases.Count; i++)
        {
            if (ReferenceEquals(airbases[i].Airbase, airbase))
            {
                selectedAirbaseIndex = i;
                SetStatus($"Departure airbase selected: {airbases[i].Label}.");
                return true;
            }
        }

        SetStatus("This airbase cannot currently spawn the selected aircraft.");
        return false;
    }

    internal bool IsSelectableAirbase(Airbase? airbase)
    {
        if (!uiVisible || airbase == null)
        {
            return false;
        }

        for (int i = 0; i < airbases.Count; i++)
        {
            if (ReferenceEquals(airbases[i].Airbase, airbase))
            {
                return true;
            }
        }
        return false;
    }

    internal bool IsSelectedAirbase(Airbase? airbase)
    {
        return airbase != null
            && SelectedAirbase is AirbaseOption selected
            && ReferenceEquals(selected.Airbase, airbase);
    }

    internal float SelectedMissionRadiusKm => GetMissionRadius(selectedMode) / 1000f;

    internal void StepMissionRadius(float deltaKm)
    {
        SetMissionRadius(selectedMode, Mathf.Clamp(SelectedMissionRadiusKm + deltaKm, 5f, 150f));
    }

    internal void CollectMissionAircraft(List<Aircraft> aircraft)
    {
        aircraft.Clear();
        foreach (Aircraft unit in missions.Keys)
        {
            if (unit != null && !unit.disabled) aircraft.Add(unit);
        }
        aircraft.Sort((left, right) => string.Compare(
            CommanderGameAccess.GetUnitLabel(left), CommanderGameAccess.GetUnitLabel(right), StringComparison.OrdinalIgnoreCase));
    }

    internal bool IsMissionAircraftSelected(Aircraft aircraft) => ReferenceEquals(selectedMissionAircraft, aircraft);

    internal void ToggleMissionAircraft(Aircraft aircraft)
    {
        selectedMissionAircraft = ReferenceEquals(selectedMissionAircraft, aircraft) ? null : aircraft;
        RefreshMissionMapVisuals();
    }

    internal void ClearMissionAircraftSelection()
    {
        selectedMissionAircraft = null;
        RefreshMissionMapVisuals();
    }

    internal string GetMissionAircraftLabel(Aircraft aircraft)
    {
        return missions.TryGetValue(aircraft, out AirMission mission)
            ? $"{CommanderGameAccess.GetUnitLabel(aircraft)}\n{GetModeLabel(mission.Mode)}  |  {(mission.Returning ? "RTB" : "ACTIVE")}"
            : CommanderGameAccess.GetUnitLabel(aircraft);
    }

    internal void RequestReturnToBase(Aircraft aircraft)
    {
        if (!missions.TryGetValue(aircraft, out AirMission mission)) return;
        mission.Returning = true;
        IssueReturnToBase(aircraft, mission);
        SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} ordered to RTB.");
    }

    internal float GetMissionRadiusKm(Aircraft aircraft)
    {
        return missions.TryGetValue(aircraft, out AirMission mission)
            ? mission.Radius / 1000f
            : 0f;
    }

    internal void StepMissionRadius(Aircraft aircraft, float deltaKm)
    {
        if (!missions.TryGetValue(aircraft, out AirMission mission))
        {
            return;
        }

        mission.Radius = Mathf.Clamp(mission.Radius / 1000f + deltaKm, 5f, 150f) * 1000f;
        DestroyMissionMapVisual(mission);
        if (ReferenceEquals(selectedMissionAircraft, aircraft))
        {
            EnsureMissionMapVisual(mission);
        }
        SetStatus($"{GetModeLabel(mission.Mode)} radius set to {mission.Radius / 1000f:0} km.");
    }

    private void ProcessReturningMissions()
    {
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            if (entry.Value.Returning && !entry.Value.RtbIssued) IssueReturnToBase(entry.Key, entry.Value);
        }
    }

    private static void IssueReturnToBase(Aircraft aircraft, AirMission mission)
    {
        if (aircraft == null || aircraft.disabled || aircraft.pilots == null) return;
        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot == null) continue;
            if (pilot.AILandingState == null) pilot.AILandingState = new AIPilotLandingState();
            pilot.SwitchState(pilot.AILandingState);
            mission.RtbIssued = true;
            return;
        }
    }

    private static bool KeepsStationInMissionArea(AirCommandMode mode)
    {
        return mode == AirCommandMode.AwacsJammer || mode == AirCommandMode.AirGuard;
    }

    private static bool TargetsRestrictedToMissionArea(AirCommandMode mode)
    {
        return mode == AirCommandMode.Cas
            || mode == AirCommandMode.Arad
            || mode == AirCommandMode.StrategicStrike;
    }

    internal static string GetModeLabel(AirCommandMode mode)
    {
        return mode switch
        {
            AirCommandMode.AwacsJammer => "AWACS / JAMMER",
            AirCommandMode.Cas => "CAS",
            AirCommandMode.AirGuard => "AIR SUPERIORITY",
            AirCommandMode.Arad => "ARAD",
            AirCommandMode.StrategicStrike => "STRATEGIC STRIKE",
            _ => mode.ToString().ToUpperInvariant(),
        };
    }

    private static string GetAircraftLabel(AircraftDefinition definition)
    {
        return !string.IsNullOrWhiteSpace(definition.unitName)
            ? definition.unitName
            : !string.IsNullOrWhiteSpace(definition.code)
                ? definition.code
                : definition.name;
    }

    private static string GetAirbaseName(Airbase airbase)
    {
        if (airbase.SavedAirbase != null)
        {
            if (!string.IsNullOrWhiteSpace(airbase.SavedAirbase.DisplayName))
            {
                return airbase.SavedAirbase.DisplayName;
            }
            if (!string.IsNullOrWhiteSpace(airbase.SavedAirbase.UniqueName))
            {
                return airbase.SavedAirbase.UniqueName;
            }
        }

        return airbase.name;
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }

}
