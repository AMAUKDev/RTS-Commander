using System;
using System.Collections.Generic;
using System.Reflection;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    /// <summary>
    /// Every aircraft definition the game carries — the encyclopedia's list plus anything loaded
    /// as a live resource, deduplicated, in one list. Internal (one definition, two callers, Reuse
    /// rule 4): the AIR window's option list and the AI buyers' candidate list are THE SAME LIST —
    /// a commander that walked its faction supply instead (the first ladder build) could never see
    /// an airbase's hangars will accept but the faction was never issued, so a highway-strip
    /// commander sat on a full fund and bought no CAP fighter all match while the player launched
    /// the same aircraft by hand (user report, 2026-09-14: Compass and VT-7 Vagrant with Scythes).
    /// </summary>
    internal static void CollectAircraftDefinitions(List<AircraftDefinition> definitions)
    {
        definitions.Clear();
        HashSet<AircraftDefinition> seen = new();
        Encyclopedia? encyclopedia = Encyclopedia.i;
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
    }

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
        CollectAircraftDefinitions(definitions);

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
        CloseOrderMap();
        if (relocationAircraft != null && missions.TryGetValue(relocationAircraft, out AirMission relocationMission))
        {
            relocationMission.AreaCenter = target;
            relocationMission.RememberArea();
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
        CloseOrderMap();
        if (showStatus)
        {
            SetStatus("Air mission area selection cancelled.");
        }
    }

    /// <summary>Altitude a commander-launched AI airframe enters the map at, over its own base.</summary>
    private const float LaunchAltitudeMeters = 1200f;

    /// <summary>
    /// Puts one AI airframe in the air over <paramref name="airbase"/>, pointed at
    /// <paramref name="facing"/>, at a speed the flight model can hold. Returns null if it could
    /// not be spawned.
    /// </summary>
    /// <remarks>
    /// This exists because <c>Airbase.TrySpawnAircraft</c> is not survivable for an AI pilot on
    /// this mod's maps. A hangar spawn hands the airframe to <c>AIPilotTaxiState</c> and then
    /// <c>AIPilotTakeoffState</c>, and neither copes with a highway strip: there is no taxi
    /// network, so the pilot drives a straight line at the runway threshold, and both states
    /// answer any trouble at all — a stuck timer, a scrape, a wing that touches anything — with
    /// <c>StartEjectionSequence</c>. That is the enemy's aircraft dying beside the hangar over and
    /// over, and it applies just as much to an airframe the player's AIR window buys; a player
    /// never sees it only because a player flies the aeroplane themselves.
    /// <c>Pilot.SetStartingAiState</c> already has the branch that avoids all of it: an aircraft
    /// whose <c>radarAlt</c> is above its spawn offset skips taxi and takeoff and starts in
    /// <c>AIPilotCombatModes</c> — the one state every Air Command patch in this mod targets. It is
    /// also how a mission spawns its own aircraft in flight (<c>Spawner.TrySpawnAircraft</c> on a
    /// <c>SavedAircraft</c> with a starting speed), so this is a supported path, not a trick.
    /// The airbase still decides *which* airframes exist — <c>CanSpawnAircraft</c> is the roster —
    /// it just no longer has to be taxied off. Callers own their own supply bookkeeping, because
    /// no hangar runs here to do it for them.
    /// </remarks>
    internal static bool TryLaunchAiAircraft(
        FactionHQ hq,
        Airbase airbase,
        AircraftDefinition definition,
        LiveryKey livery,
        Loadout loadout,
        float fuel,
        GlobalPosition facing)
    {
        if (CommanderSettings.AiAircraftLaunchFromHangar)
        {
            // The game's own path, behind the Gameplay toggle so the ejection problem above can be
            // re-tested map by map instead of assumed. A hangar takes one airframe out of stock on
            // the way out of the door, and this method promises its callers that it leaves stock
            // alone (they do their own bookkeeping), so the one it consumes is put in first and
            // taken back if the hangar refuses.
            hq.ModifyUnitSupply(definition, 1);
            if (airbase.TrySpawnAircraft(null, definition, livery, loadout, fuel).Allowed)
            {
                return true;
            }

            hq.ModifyUnitSupply(definition, -1);
            return false;
        }

        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (spawner == null || airbase.center == null || definition.unitPrefab == null)
        {
            return false;
        }

        Vector3 origin = airbase.center.position + Vector3.up * LaunchAltitudeMeters;
        Vector3 heading = facing.ToLocalPosition() - origin;
        heading.y = 0f;
        heading = heading.sqrMagnitude < 1f ? airbase.center.forward : heading.normalized;

        // A stationary air spawn falls out of the sky before the autopilot has any airspeed to
        // work with, so it enters at a speed the airframe is happy at.
        float speed = Mathf.Max(
            definition.aircraftParameters.PIDReferenceAirspeed,
            definition.aircraftParameters.takeoffSpeed * 1.5f,
            120f);
        Aircraft aircraft = spawner.SpawnAircraft(
            null,
            definition.unitPrefab,
            loadout,
            fuel,
            livery,
            origin.ToGlobalPosition(),
            Quaternion.LookRotation(heading, Vector3.up),
            heading * speed,
            null,
            hq,
            null,
            1f,
            0.5f);
        if (aircraft != null && loadout == null)
        {
            // Hangar.SpawnAircraft's fallback: an AI airframe with no standard loadout otherwise
            // arrives with empty pylons.
            aircraft.Networkloadout = aircraft.weaponManager.SelectAIAircraftWeapons(airbase);
        }

        return aircraft != null;
    }

    private void SpawnMission(AirMissionOption option, Airbase airbase, GlobalPosition target)
    {
        // The UI path: the recipe is whatever the AIR window has selected right now, and the loadout
        // is built from the live hardpoint picker. A relaunch comes through TrySpawnFromRecipe with
        // the recipe recorded at first launch instead, so later picker changes do not leak into it.
        Loadout loadout = option.BuildLoadout();
        NormalizeLoadoutLength(loadout, option.Definition);
        AirMissionRecipe recipe = new(
            option,
            airbase,
            loadout,
            target,
            GetMissionRadius(option.Mode),
            SupportsTargetAltitude(option.Mode) ? selectedTargetAltitude : 0f,
            option.Mode == AirCommandMode.AirGuard && TargetOrdnance,
            option.Mode == AirCommandMode.Arad && SaturationAttack);
        TrySpawnFromRecipe(recipe, airbase, autoRecreate: false);
    }

    /// <summary>
    /// Launches one mission from a recipe. Returns false, with the reason in the status line, when
    /// nothing left the ground and nothing was charged.
    /// </summary>
    private bool TrySpawnFromRecipe(AirMissionRecipe recipe, Airbase airbase, bool autoRecreate)
    {
        AirMissionOption option = recipe.Option;
        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            SetStatus("Air Command is host-only.");
            return false;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null || !IsCompatibleAirbase(airbase, hq, option.Definition))
        {
            SetStatus("The selected airbase is no longer compatible.");
            return false;
        }

        if (!airbase.CanSpawnAircraft(option.Definition))
        {
            SetStatus("The selected airbase is busy. Retry when a compatible hangar is free.");
            return false;
        }

        if (pendingAircraftSpawn != null)
        {
            SetStatus("Wait for the previous Air Command aircraft to finish spawning.");
            return false;
        }

        bool purchased = false;
        if (hq.GetUnitSupply(option.Definition) <= 0)
        {
            if (hq.factionFunds < option.Definition.value)
            {
                SetStatus("The faction cannot afford this aircraft.");
                return false;
            }

            hq.AddFunds(-option.Definition.value);
            hq.ModifyUnitSupply(option.Definition, 1);
            purchased = true;
        }

        pendingAircraftSpawn = new PendingAircraftSpawn(
            hq,
            recipe,
            autoRecreate,
            purchased,
            purchased ? option.Definition.value : 0f,
            Time.unscaledTime + PendingSpawnTimeoutSeconds);

        int liveryIndex = option.Definition.aircraftParameters.GetRandomLiveryForFaction(hq.faction);
        Loadout loadout = recipe.Loadout;
        if (!ValidateSelectedLoadout(option, loadout, airbase, hq, out string loadoutError, fromPicker: !autoRecreate))
        {
            pendingAircraftSpawn = null;
            if (purchased)
            {
                hq.ModifyUnitSupply(option.Definition, -1);
                hq.AddFunds(option.Definition.value);
            }
            SetStatus(loadoutError);
            return false;
        }
        if (!TryLaunchAiAircraft(
                hq,
                airbase,
                option.Definition,
                new LiveryKey(liveryIndex),
                loadout,
                option.Definition.aircraftParameters.DefaultFuelLevel,
                recipe.AreaCenter))
        {
            pendingAircraftSpawn = null;
            if (purchased)
            {
                hq.ModifyUnitSupply(option.Definition, -1);
                hq.AddFunds(option.Definition.value);
            }
            SetStatus("The selected airbase rejected the aircraft spawn.");
            return false;
        }

        // TryLaunchAiAircraft leaves stock as it found it whichever way the airframe left, so the
        // one consumed comes off here: it cancels the purchase's +1 above, or uses one the faction
        // already had.
        hq.ModifyUnitSupply(option.Definition, -1);

        SetStatus($"{GetModeLabel(recipe.Mode)} mission {(autoRecreate ? "relaunched" : "launched")}: "
            + $"{GetAircraftLabel(option.Definition)} / {option.LoadoutName}"
            + (CommanderSettings.AiAircraftLaunchFromHangar ? " (hangar)." : "."));
        return true;
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

        AirMissionRecipe recipe = pending.Recipe;
        AirMission mission = new(
            pending.Hq,
            recipe.Mode,
            recipe.AreaCenter,
            recipe.Radius,
            recipe.TargetAltitude,
            recipe.TargetOrdnance,
            recipe.SaturationAttack,
            pending.PurchasedWithFunds,
            pending.PurchaseCost,
            recipe,
            pending.AutoRecreate);
        missions[aircraft] = mission;
        CommanderSelectionService.PinMissionUnit(
            aircraft,
            "AIR COMMAND",
            GetModeLabel(recipe.Mode));
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
