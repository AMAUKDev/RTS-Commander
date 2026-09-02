using System;
using System.Collections.Generic;
using System.Reflection;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    private void RefreshOptions()
    {
        options.Clear();
        airbases.Clear();
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (!uiVisible || hq == null)
        {
            return;
        }

        List<AircraftDefinition> definitions = new();
        HashSet<AircraftDefinition> seen = new();
        Encyclopedia encyclopedia = Encyclopedia.i;
        if (encyclopedia?.aircraft != null)
        {
            for (int i = 0; i < encyclopedia.aircraft.Count; i++)
            {
                AircraftDefinition definition = encyclopedia.aircraft[i];
                if (definition != null && seen.Add(definition))
                {
                    definitions.Add(definition);
                }
            }
        }

        AircraftDefinition[] resourceDefinitions = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
        for (int i = 0; i < resourceDefinitions.Length; i++)
        {
            AircraftDefinition definition = resourceDefinitions[i];
            if (definition != null && seen.Add(definition))
            {
                definitions.Add(definition);
            }
        }

        for (int i = 0; i < definitions.Count; i++)
        {
            AircraftDefinition definition = definitions[i];
            if (definition == null
                || definition.unitPrefab == null
                || definition.aircraftParameters == null)
            {
                continue;
            }

            if (!HasPlanePilot(definition))
            {
                continue;
            }

            AirMissionOption? option = CreateVariableLoadoutOption(definition, hq, selectedMode);
            if (option == null)
            {
                continue;
            }

            bool supported = false;
            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (IsCompatibleAirbase(airbase, hq, definition))
                {
                    supported = true;
                    break;
                }
            }

            if (supported)
            {
                options.Add(option);
            }
        }

        BuildWeaponOptions();
        if (selectedPrimaryWeaponIndex < 0)
        {
            selectedPrimaryWeaponIndex = FindFirstSuitableWeaponIndex();
        }
        ApplySelectedWeaponsAndSort();
        RefreshAirbases();
    }

    private void RefreshAirbases()
    {
        Airbase? previouslySelected = SelectedAirbase?.Airbase;
        airbases.Clear();
        AirMissionOption? option = SelectedOption;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (option == null || hq == null)
        {
            selectedAirbaseIndex = 0;
            return;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (!IsCompatibleAirbase(airbase, hq, option.Definition)
                || !airbase.CanSpawnAircraft(option.Definition))
            {
                continue;
            }

            Transform position = airbase.center != null ? airbase.center : airbase.transform;
            airbases.Add(new AirbaseOption(
                airbase,
                GetAirbaseName(airbase),
                Vector3.Distance(cameraPosition, position.position),
                ready: true));
        }

        airbases.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        int preservedIndex = previouslySelected == null
            ? -1
            : airbases.FindIndex(option => ReferenceEquals(option.Airbase, previouslySelected));
        selectedAirbaseIndex = preservedIndex >= 0
            ? preservedIndex
            : Mathf.Clamp(selectedAirbaseIndex, 0, Mathf.Max(airbases.Count - 1, 0));
    }

    private void TryHandleTacticalMapClick()
    {
        if (!DynamicMap.mapMaximized)
        {
            return;
        }

        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        UpdatePendingAreaPreview();
        Vector2 guiMouse = CommanderUiScale.ScreenToGui(Input.mousePosition);
        if (areaSelectionBlockingRect.Contains(guiMouse) || areaSelectionSecondaryBlockingRect.Contains(guiMouse))
        {
            mapClickTracker.Reset();
            return;
        }
        if (map != null && mapClickTracker.Tick(map, out GlobalPosition target))
        {
            CompleteAreaSelection(target);
        }
    }

    private void CompleteAreaSelection(GlobalPosition target)
    {
        PendingAreaSelection? selection = pendingAreaSelection;
        Aircraft? relocationAircraft = pendingMissionRelocation;
        Aircraft? adoptionAircraft = pendingAdoption;
        pendingAreaSelection = null;
        pendingMissionRelocation = null;
        pendingAdoption = null;
        DestroyPendingAreaPreview();
        tacticalMapService.SuppressMapFollow = false;
        mapClickTracker.Reset();
        if (!uiVisible) tacticalMapService.CloseFullscreen();
        if (relocationAircraft != null && missions.TryGetValue(relocationAircraft, out AirMission relocationMission))
        {
            relocationMission.AreaCenter = target;
            DestroyMissionMapVisual(relocationMission);
            EnsureMissionMapVisual(relocationMission);
            SetStatus($"{GetModeLabel(relocationMission.Mode)} mission area relocated.");
            return;
        }
        if (adoptionAircraft != null)
        {
            if (TryAdoptAircraft(adoptionAircraft, selectedMode, target, GetMissionRadius(selectedMode)))
            {
                SetStatus($"{CommanderGameAccess.GetUnitLabel(adoptionAircraft)} tasked with a {GetModeLabel(selectedMode)} mission.");
            }
            else
            {
                SetStatus("That aircraft can no longer be tasked.");
            }
            return;
        }

        if (selection == null)
        {
            return;
        }

        SpawnMission(selection.Option, selection.Airbase, target);
    }

    private void CancelAreaSelection(bool showStatus)
    {
        if (pendingAreaSelection == null && pendingMissionRelocation == null && pendingAdoption == null)
        {
            return;
        }

        pendingAreaSelection = null;
        pendingMissionRelocation = null;
        pendingAdoption = null;
        DestroyPendingAreaPreview();
        tacticalMapService.SuppressMapFollow = false;
        mapClickTracker.Reset();
        if (!uiVisible && tacticalMapService.IsFullscreenOpen)
        {
            tacticalMapService.CloseFullscreen();
        }
        if (showStatus)
        {
            SetStatus("Air mission area selection cancelled.");
        }
    }

    private void SpawnMission(AirMissionOption option, Airbase airbase, GlobalPosition target)
    {
        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            SetStatus("Air Command is host-only.");
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null || !IsCompatibleAirbase(airbase, hq, option.Definition))
        {
            SetStatus("The selected airbase is no longer compatible.");
            return;
        }

        if (!airbase.CanSpawnAircraft(option.Definition))
        {
            SetStatus("The selected airbase is busy. Retry when a compatible hangar is free.");
            return;
        }

        if (pendingAircraftSpawn != null)
        {
            SetStatus("Wait for the previous Air Command aircraft to finish spawning.");
            return;
        }

        bool purchased = false;
        if (hq.GetUnitSupply(option.Definition) <= 0)
        {
            if (hq.factionFunds < option.Definition.value)
            {
                SetStatus("The faction cannot afford this aircraft.");
                return;
            }

            hq.AddFunds(-option.Definition.value);
            hq.ModifyUnitSupply(option.Definition, 1);
            purchased = true;
        }

        pendingAircraftSpawn = new PendingAircraftSpawn(
            hq,
            option,
            target,
            GetMissionRadius(option.Mode),
            SupportsTargetAltitude(option.Mode) ? selectedTargetAltitude : 0f,
            option.Mode == AirCommandMode.AirGuard && TargetOrdnance,
            option.Mode == AirCommandMode.Arad && SaturationAttack,
            purchased,
            purchased ? option.Definition.value : 0f,
            Time.unscaledTime + PendingSpawnTimeoutSeconds);

        int liveryIndex = option.Definition.aircraftParameters.GetRandomLiveryForFaction(hq.faction);
        Loadout loadout = option.BuildLoadout();
        NormalizeLoadoutLength(loadout, option.Definition);
        if (!ValidateSelectedLoadout(option, loadout, airbase, hq, out string loadoutError))
        {
            pendingAircraftSpawn = null;
            if (purchased)
            {
                hq.ModifyUnitSupply(option.Definition, -1);
                hq.AddFunds(option.Definition.value);
            }
            SetStatus(loadoutError);
            return;
        }
        Airbase.TrySpawnResult result = airbase.TrySpawnAircraft(
            null,
            option.Definition,
            new LiveryKey(liveryIndex),
            loadout,
            option.Definition.aircraftParameters.DefaultFuelLevel);
        if (!result.Allowed)
        {
            pendingAircraftSpawn = null;
            if (purchased)
            {
                hq.ModifyUnitSupply(option.Definition, -1);
                hq.AddFunds(option.Definition.value);
            }
            SetStatus("The selected airbase rejected the aircraft spawn.");
            return;
        }

        SetStatus($"{GetModeLabel(option.Mode)} mission launched: {GetAircraftLabel(option.Definition)} / {option.LoadoutName}.");
    }

    private void TryAssignPendingAircraft(FactionHQ hq, Unit unit)
    {
        PendingAircraftSpawn? pending = pendingAircraftSpawn;
        if (pending == null
            || !ReferenceEquals(hq, pending.Hq)
            || unit is not Aircraft aircraft
            || aircraft.Player != null
            || !ReferenceEquals(aircraft.definition, pending.Option.Definition))
        {
            return;
        }

        AirMission mission = new(
            pending.Hq,
            pending.Option.Mode,
            pending.AreaCenter,
            pending.Radius,
            pending.TargetAltitude,
            pending.TargetOrdnance,
            pending.SaturationAttack,
            pending.PurchasedWithFunds,
            pending.PurchaseCost);
        missions[aircraft] = mission;
        CommanderSelectionService.PinMissionUnit(
            aircraft,
            "AIR COMMAND",
            GetModeLabel(pending.Option.Mode));
        pendingAircraftSpawn = null;
    }

    private void PruneMissions()
    {
        staleAircraft.Clear();
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            if (entry.Key == null || entry.Key.disabled)
            {
                staleAircraft.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleAircraft.Count; i++)
        {
            RemoveMission(staleAircraft[i]);
        }
    }
}
