using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace GroundControlRts;

[HarmonyPatch]
internal static class CommanderAirCommandPatches
{
    private static readonly FieldInfo? StateAircraftField = AccessTools.Field(typeof(PilotBaseState), "aircraft");
    private static readonly FieldInfo? DestinationField = AccessTools.Field(typeof(PilotBaseState), "destination");
    private static readonly FieldInfo? TimeWithoutTargetField = AccessTools.Field(typeof(AIPilotCombatModes), "timeWithoutTarget");
    private static readonly FieldInfo? TargetHeightField = AccessTools.Field(typeof(AIPilotCombatModes), "targetHeight");

    [HarmonyPatch(typeof(CombatAI), nameof(CombatAI.ChooseHQTarget))]
    [HarmonyPrefix]
    private static bool ChooseHqTargetPrefix(
        Unit searcher,
        List<WeaponStation> stationList,
        ref CombatAI.TargetSearchResults __result)
    {
        if (!CommanderAirCommandService.TryChooseMissionTarget(searcher, stationList, out CombatAI.TargetSearchResults result))
        {
            return true;
        }

        __result = result;
        return false;
    }

    [HarmonyPatch(typeof(CombatAI), nameof(CombatAI.LookForMissileTargets))]
    [HarmonyPrefix]
    private static bool LookForMissileTargetsPrefix(
        Aircraft aircraft,
        WeaponStation weaponStation,
        ref int __result)
    {
        if (!CommanderAirCommandService.TryBuildAradSaturationTargets(aircraft, weaponStation, out int targetCount))
        {
            return true;
        }

        __result = targetCount;
        return false;
    }

    [HarmonyPatch(typeof(AIPilotCombatModes), "NoTarget")]
    [HarmonyPostfix]
    private static void NoTargetPostfix(AIPilotCombatModes __instance)
    {
        if (!CommanderAirCommandService.TryGetMissionHoldPoint(__instance, out GlobalPosition point))
        {
            return;
        }

        DestinationField?.SetValue(__instance, point);
        TimeWithoutTargetField?.SetValue(__instance, 0f);
    }

    [HarmonyPatch(typeof(AIPilotCombatModes), "ManageAltitude")]
    [HarmonyPostfix]
    private static void ManageAltitudePostfix(AIPilotCombatModes __instance)
    {
        CommanderAirCommandService.ApplyMissionTargetAltitude(__instance, TargetHeightField);
    }

    [HarmonyPatch(typeof(AIPilotCombatModes), "RunAttackMode")]
    [HarmonyPostfix]
    private static void RunAttackModePostfix(AIPilotCombatModes __instance)
    {
        CommanderAirCommandService.ConstrainMissionDestination(__instance, DestinationField);
    }

    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.RegisterFactionUnit))]
    [HarmonyPostfix]
    private static void RegisterFactionUnitPostfix(FactionHQ __instance, Unit unit)
    {
        CommanderAirCommandService.NotifyFactionUnitRegistered(__instance, unit);
    }

    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.ReturnToInventory))]
    [HarmonyPostfix]
    private static void ReturnToInventoryPostfix(Aircraft __instance)
    {
        CommanderAirCommandService.NotifyAircraftReturned(__instance);
    }

    [HarmonyPatch(typeof(Unit), nameof(Unit.DisableUnit))]
    [HarmonyPostfix]
    private static void DisableUnitPostfix(Unit __instance)
    {
        CommanderAirCommandService.NotifyUnitDisabled(__instance);
    }

    internal static Aircraft? GetStateAircraft(AIPilotCombatModes state)
    {
        return StateAircraftField?.GetValue(state) as Aircraft;
    }

}
