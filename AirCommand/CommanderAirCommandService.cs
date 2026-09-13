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
    private bool openedMapForOrder;
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
    internal int ActiveMissionCount
    {
        get
        {
            FactionHQ? hq = CommanderGameAccess.GetLocalHq();
            int count = 0;
            foreach (Aircraft aircraft in missions.Keys)
            {
                if (CommanderGameAccess.IsFriendlyUnit(aircraft, hq))
                {
                    count++;
                }
            }
            return count;
        }
    }
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
        relaunchQueue.Clear();
        nextRelaunchAt = 0f;
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
            TickLandingOrders();
            ProcessReturningMissions();
            RecoverLandedAircraft();
        }

        ProcessAutoRecreate();
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
        // The enemy commander tasks its own airframes through this same dictionary, so the player's
        // mission list has to filter by faction or it offers an RTB button for their bombers.
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        foreach (Aircraft unit in missions.Keys)
        {
            if (unit != null && !unit.disabled && CommanderGameAccess.IsFriendlyUnit(unit, localHq))
            {
                aircraft.Add(unit);
            }
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
                + (mission.AutoRecreate ? "  |  AUTO" : string.Empty)
            : CommanderGameAccess.GetUnitLabel(aircraft);
    }

    /// <summary>
    /// RTB is a resupply run: the aircraft is routed to the nearest field its faction holds and put
    /// down there, which is what recovers the airframe into stock. The bare landing-state switch is
    /// the fallback for a faction with no base left to go home to.
    /// </summary>
    internal void RequestReturnToBase(Aircraft aircraft)
    {
        if (!missions.TryGetValue(aircraft, out AirMission mission)) return;
        if (ResupplyAircraft(aircraft)) return;

        // ResupplyAircraft only routes planes (the route steering is a fixed-wing patch), so every
        // helicopter lands this way. The Basegame rotary landing state picks the nearest friendly
        // pad itself, but ejects the crew if there is none, so check first.
        if (aircraft.pilots != null && aircraft.pilots.Length > 0 && aircraft.pilots[0] != null
            && IsRotaryPilot(aircraft.pilots[0]) && !HasVerticalPad(aircraft))
        {
            SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} has no friendly landing pad to return to.");
            return;
        }

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
        mission.RememberArea();
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
            if (!entry.Value.Returning)
            {
                continue;
            }

            if (!entry.Value.RtbIssued || RotaryBouncedBackToCombat(entry.Key))
            {
                IssueReturnToBase(entry.Key, entry.Value);
            }
        }
    }

    /// <summary>
    /// True for a helicopter that was told to land and is back in its combat brain. The rotary
    /// landing state hands itself back to combat whenever the pad it wanted is taken or not yet
    /// free, and the combat brain's own no-target logic will orbit a mission objective rather than
    /// land if it has never seen an enemy. So a helicopter on RTB is pushed back into the landing
    /// state every review until it is down.
    /// </summary>
    private static bool RotaryBouncedBackToCombat(Aircraft aircraft)
    {
        if (aircraft == null || aircraft.disabled || aircraft.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot != null)
            {
                return IsRotaryPilot(pilot) && pilot.currentState is AIHeloCombatState;
            }
        }

        return false;
    }

    /// <summary>
    /// Hands the aircraft to the Basegame landing routine that matches its pilot. Planes use
    /// <c>AIPilotLandingState</c>. Helicopters and tiltwings fly on a different state machine
    /// entirely (<c>Pilot.SetStartingAiState</c> gives them <c>AIHeloCombatState</c> and
    /// <c>AIHeloLandingState</c>), and the rotary landing state finds the nearest friendly pad and
    /// flies there on its own, from any range. This used to put every pilot into the fixed-wing
    /// landing state, which for a Chicane meant an aeroplane approach flown by a helicopter: the
    /// RTB order read as ignored.
    /// </summary>
    private static void IssueReturnToBase(Aircraft aircraft, AirMission mission)
    {
        if (aircraft == null || aircraft.disabled || aircraft.pilots == null) return;
        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot == null) continue;
            if (IsRotaryPilot(pilot))
            {
                if (pilot.AIHeloLandingState == null) pilot.AIHeloLandingState = new AIHeloLandingState();
                pilot.SwitchState(pilot.AIHeloLandingState);
            }
            else
            {
                if (pilot.AILandingState == null) pilot.AILandingState = new AIPilotLandingState();
                pilot.SwitchState(pilot.AILandingState);
            }
            mission.RtbIssued = true;
            return;
        }
    }

    private static bool IsRotaryPilot(Pilot pilot)
    {
        return pilot.pilotType == Pilot.PilotType.Helo || pilot.pilotType == Pilot.PilotType.Tiltwing;
    }

    /// <summary>
    /// True when the faction holds a base with a free vertical landing point this airframe fits.
    /// <c>AIHeloLandingState</c> ejects the pilot outright when it finds no airbase at all, so a
    /// helicopter is only sent home when there is a home to go to.
    /// </summary>
    private static bool HasVerticalPad(Aircraft aircraft)
    {
        if (aircraft.NetworkHQ == null) return false;
        RunwayQuery query = new()
        {
            RunwayType = RunwayQueryType.Vertical,
            MinSize = aircraft.maxRadius,
        };
        return aircraft.NetworkHQ.TryGetNearestAirbase(aircraft.transform.position, out Airbase _, query);
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
