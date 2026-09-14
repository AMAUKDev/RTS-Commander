using System;
using System.Collections.Generic;
using NuclearOption.SavedMission;

namespace GroundControlRts;

/// <summary>
/// Hot-reload persistence for Air Command (design §Section 2, developer quality-of-life, off by
/// default — see <see cref="CommanderStateStore"/>). A live mission's airframe survives a hot
/// reload on its own (it is a live game object); what is lost is this service's own bookkeeping —
/// the <see cref="missions"/> table and the <see cref="relaunchQueue"/> — so that is all this
/// saves and restores.
/// </summary>
/// <remarks>
/// Only a mission launched from a recipe is persisted. An adopted airframe
/// (<see cref="TryAdoptAircraft"/>) has no recipe and was never "ours" to relaunch, so losing its
/// pin across a reload is an acceptable trade — the player can re-adopt it in a few clicks, same
/// as after any other UI reset.
/// </remarks>
internal sealed partial class CommanderAirCommandService
{
    public void Snapshot(CommanderStateWriter w)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            Aircraft aircraft = entry.Key;
            AirMission mission = entry.Value;
            if (aircraft == null || aircraft.disabled || mission.Recipe == null
                || !CommanderGameAccess.IsFriendlyUnit(aircraft, hq))
            {
                continue;
            }

            w.Snapshot.AirMissions.Add(new CommanderAirMissionRecord
            {
                PersistentId = aircraft.persistentID.Id,
                PurchasedWithFunds = mission.PurchasedWithFunds,
                PurchaseCost = mission.PurchaseCost,
                AutoRecreate = mission.AutoRecreate,
                Recipe = ToRecord(mission.Recipe),
            });
        }

        for (int i = 0; i < relaunchQueue.Count; i++)
        {
            w.Snapshot.AirRelaunchQueue.Add(ToRecord(relaunchQueue[i]));
        }
    }

    public void Restore(CommanderStateReader r)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return;
        }

        int restored = 0;
        int dropped = 0;

        List<CommanderAirMissionRecord> records = r.Snapshot.AirMissions;
        for (int i = 0; i < records.Count; i++)
        {
            CommanderAirMissionRecord record = records[i];
            PersistentID id = new() { Id = record.PersistentId };
            if (!id.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not Aircraft aircraft
                || !CommanderGameAccess.IsFriendlyUnit(aircraft, hq))
            {
                dropped++;
                continue;
            }

            if (!Enum.TryParse(record.Recipe.Mode, out AirCommandMode mode))
            {
                dropped++;
                CommanderPlugin.Log.LogInfo(
                    $"Air Command restore: dropped {CommanderGameAccess.GetUnitLabel(aircraft)} (unknown mission mode '{record.Recipe.Mode}').");
                continue;
            }

            // A recipe failure only costs the ability to auto-relaunch this one airframe later —
            // the live mission itself (mode/area/radius) is still worth restoring either way.
            TryRebuildRecipe(hq, record.Recipe, out AirMissionRecipe? recipe, out string recipeDropReason);
            if (recipe == null && !string.IsNullOrEmpty(recipeDropReason))
            {
                CommanderPlugin.Log.LogInfo(
                    $"Air Command restore: kept {CommanderGameAccess.GetUnitLabel(aircraft)} but could not rebuild its relaunch recipe ({recipeDropReason}).");
            }

            AirMission mission = new(
                hq,
                mode,
                new GlobalPosition(record.Recipe.AreaX, record.Recipe.AreaY, record.Recipe.AreaZ),
                record.Recipe.Radius,
                record.Recipe.TargetAltitude,
                record.Recipe.TargetOrdnance,
                record.Recipe.SaturationAttack,
                record.PurchasedWithFunds,
                record.PurchaseCost,
                recipe,
                record.AutoRecreate);

            RederiveLandingFlags(aircraft, mission);
            missions[aircraft] = mission;
            CommanderSelectionService.PinMissionUnit(aircraft, "AIR COMMAND", GetModeLabel(mode));
            restored++;
        }

        int relaunchRestored = 0;
        List<CommanderAirRecipeRecord> queued = r.Snapshot.AirRelaunchQueue;
        for (int i = 0; i < queued.Count; i++)
        {
            if (TryRebuildRecipe(hq, queued[i], out AirMissionRecipe? recipe, out string reason) && recipe != null)
            {
                relaunchQueue.Add(recipe);
                relaunchRestored++;
            }
            else
            {
                dropped++;
                CommanderPlugin.Log.LogInfo($"Air Command restore: dropped a queued relaunch ({reason}).");
            }
        }

        CommanderPlugin.Log.LogInfo(
            $"Air Command restored {restored} missions, {relaunchRestored} relaunches queued, {dropped} dropped.");
        RefreshMissionMapVisuals();
    }

    private static CommanderAirRecipeRecord ToRecord(AirMissionRecipe recipe)
    {
        List<string> mountKeys = new(recipe.Loadout.weapons.Count);
        for (int i = 0; i < recipe.Loadout.weapons.Count; i++)
        {
            mountKeys.Add(recipe.Loadout.weapons[i]?.jsonKey ?? string.Empty);
        }

        return new CommanderAirRecipeRecord
        {
            AircraftJsonKey = recipe.Option.Definition.jsonKey,
            OriginAirbaseName = GetAirbaseName(recipe.Origin),
            Mode = recipe.Mode.ToString(),
            AreaX = recipe.AreaCenter.x,
            AreaY = recipe.AreaCenter.y,
            AreaZ = recipe.AreaCenter.z,
            Radius = recipe.Radius,
            TargetAltitude = recipe.TargetAltitude,
            TargetOrdnance = recipe.TargetOrdnance,
            SaturationAttack = recipe.SaturationAttack,
            MountJsonKeys = mountKeys,
        };
    }

    /// <summary>
    /// Rebuilds a launchable recipe from a persisted record: the aircraft type via its
    /// <c>jsonKey</c> (design §2), the loadout via a mount-by-<c>jsonKey</c> lookup beside
    /// <see cref="BuildWeaponOptions"/>, and the origin airbase by name, falling back to any
    /// airbase the faction still holds. Returns false (with the reason for the log) when nothing
    /// launchable can be rebuilt at all — an aircraft type removed by a mod update, an airframe
    /// with no weapon hardpoints, or a faction left holding no airbase whatsoever.
    /// </summary>
    private static bool TryRebuildRecipe(
        FactionHQ hq, CommanderAirRecipeRecord record, out AirMissionRecipe? recipe, out string dropReason)
    {
        recipe = null;
        if (!Enum.TryParse(record.Mode, out AirCommandMode mode))
        {
            dropReason = $"unknown mission mode '{record.Mode}'";
            return false;
        }

        AircraftDefinition? definition = FindAircraftDefinitionByJsonKey(record.AircraftJsonKey);
        if (definition == null)
        {
            dropReason = $"aircraft type '{record.AircraftJsonKey}' no longer exists";
            return false;
        }

        AirMissionOption? option = CreateVariableLoadoutOption(definition, hq, mode);
        if (option == null)
        {
            dropReason = $"{GetAircraftLabel(definition)} has no weapon hardpoints";
            return false;
        }

        Airbase? origin = FindAirbaseByName(hq, record.OriginAirbaseName);
        if (origin == null)
        {
            dropReason = $"{hq.faction.name} holds no airbase to relaunch {GetAircraftLabel(definition)} from";
            return false;
        }

        Loadout loadout = RebuildLoadoutFromJsonKeys(option, record.MountJsonKeys);
        NormalizeLoadoutLength(loadout, definition);
        recipe = new AirMissionRecipe(
            option,
            origin,
            loadout,
            new GlobalPosition(record.AreaX, record.AreaY, record.AreaZ),
            record.Radius,
            record.TargetAltitude,
            record.TargetOrdnance,
            record.SaturationAttack);
        dropReason = string.Empty;
        return true;
    }

    /// <summary>The restore-time counterpart to <see cref="BuildWeaponOptions"/>: given the mount
    /// <c>jsonKey</c> saved per hardpoint (design §2), selects the matching mount in each group and
    /// returns the resulting loadout. A key that no longer matches any mount on this airframe (a
    /// weapon removed by a mod update) simply leaves that hardpoint empty.</summary>
    private static Loadout RebuildLoadoutFromJsonKeys(AirMissionOption option, IReadOnlyList<string> mountJsonKeys)
    {
        for (int groupIndex = 0; groupIndex < option.HardpointGroups.Count; groupIndex++)
        {
            AirHardpointGroup group = option.HardpointGroups[groupIndex];
            string? key = null;
            for (int i = 0; i < group.HardpointIndices.Count; i++)
            {
                int hardpointIndex = group.HardpointIndices[i];
                if (hardpointIndex < mountJsonKeys.Count && !string.IsNullOrEmpty(mountJsonKeys[hardpointIndex]))
                {
                    key = mountJsonKeys[hardpointIndex];
                    break;
                }
            }

            if (key == null)
            {
                continue;
            }

            for (int mountIndex = 0; mountIndex < group.Mounts.Count; mountIndex++)
            {
                if (string.Equals(group.Mounts[mountIndex].jsonKey, key, StringComparison.Ordinal))
                {
                    group.Select(mountIndex);
                    break;
                }
            }
        }

        return option.BuildLoadout();
    }

    private static AircraftDefinition? FindAircraftDefinitionByJsonKey(string jsonKey)
    {
        List<AircraftDefinition>? aircraft = Encyclopedia.i?.aircraft;
        if (aircraft == null)
        {
            return null;
        }

        for (int i = 0; i < aircraft.Count; i++)
        {
            if (aircraft[i] != null && string.Equals(aircraft[i].jsonKey, jsonKey, StringComparison.Ordinal))
            {
                return aircraft[i];
            }
        }

        return null;
    }

    /// <summary>The saved origin airbase by its display/unique name (<see cref="GetAirbaseName"/>),
    /// or any airbase the faction still holds if that one is gone — matches the fallback
    /// <see cref="ChooseRelaunchAirbase"/> already uses for the same reason.</summary>
    private static Airbase? FindAirbaseByName(FactionHQ hq, string name)
    {
        Airbase? fallback = null;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled)
            {
                continue;
            }

            fallback ??= airbase;
            if (string.Equals(GetAirbaseName(airbase), name, StringComparison.Ordinal))
            {
                return airbase;
            }
        }

        return fallback;
    }

    /// <summary>The pilot's live AI state survives a hot reload; only this service's own
    /// bookkeeping was lost. So <see cref="AirMission.Returning"/> and
    /// <see cref="AirMission.RtbIssued"/> are re-derived from whether the pilot is currently in a
    /// landing state, rather than persisted (design §2).</summary>
    private static void RederiveLandingFlags(Aircraft aircraft, AirMission mission)
    {
        if (aircraft.pilots == null)
        {
            return;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot != null && (pilot.currentState is AIPilotLandingState || pilot.currentState is AIHeloLandingState))
            {
                mission.Returning = true;
                mission.RtbIssued = true;
                return;
            }
        }
    }
}
