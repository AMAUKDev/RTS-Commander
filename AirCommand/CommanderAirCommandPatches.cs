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
    /// <summary>The pilot's current attack mode. The enum itself is private, so the value is read as
    /// a number against the ordinals in <c>CommanderAirCommandService.AttackModeBreakOffAttack</c>
    /// and its pair; the same reflection pattern as <see cref="DestinationField"/>.</summary>
    private static readonly FieldInfo? AttackModeField = AccessTools.Field(typeof(AIPilotCombatModes), "attackMode");
    private static readonly FieldInfo? TargetHeightField = AccessTools.Field(typeof(AIPilotCombatModes), "targetHeight");
    private static readonly FieldInfo? LandingModeField = AccessTools.Field(typeof(AIPilotLandingState), "landingMode");
    private static readonly FieldInfo? LandingAirbaseField = AccessTools.Field(typeof(AIPilotLandingState), "airbase");
    private static readonly FieldInfo? LandingRunwayUsageField = AccessTools.Field(typeof(AIPilotLandingState), "runwayUsage");
    private static readonly FieldInfo? LandingSpeedField = AccessTools.Field(typeof(AIPilotLandingState), "adjustedLandingSpeed");

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

    /// <summary>
    /// The Basegame idle timer is what was flying commanded aircraft home: <c>NoTarget</c> counts
    /// every tick without a target and switches to the landing state after 15 of them, before the
    /// postfix below ever gets to write the destination — which is why an aircraft ordered to the
    /// enemy base landed at its own with most of its fuel left. Zeroing the counter on the way in
    /// means a commanded aircraft never reaches that threshold.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotCombatModes), "NoTarget")]
    [HarmonyPrefix]
    private static void NoTargetPrefix(AIPilotCombatModes __instance)
    {
        if (CommanderAirCommandService.TryGetMissionHoldPoint(__instance, out _))
        {
            TimeWithoutTargetField?.SetValue(__instance, 0f);
        }
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
        CommanderAirCommandService.ConstrainMissionDestination(__instance, DestinationField, AttackModeField);
    }

    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.RegisterFactionUnit))]
    [HarmonyPostfix]
    private static void RegisterFactionUnitPostfix(FactionHQ __instance, Unit unit)
    {
        CommanderAirCommandService.NotifyFactionUnitRegistered(__instance, unit);
        // A second postfix on the same method is already established (Supply/CommanderSupplyHeliPatches.cs):
        // the operations pool claim is a third and the air-support claim a fourth, not a new patch class.
        CommanderOperationsService.NotifyFactionUnitRegistered(__instance, unit);
        CommanderOperationsService.NotifyAircraftRegistered(__instance, unit);
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

    /// <summary>
    /// The landing state only ever looks for an airbase its own faction holds, so an aircraft
    /// ordered onto a neutral field could never actually land on it. This substitutes the commanded
    /// field; with no commanded field it does nothing and the Basegame search runs unchanged.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotLandingState), "LandingState_SearchAirbase")]
    [HarmonyPrefix]
    private static bool LandingSearchAirbasePrefix(AIPilotLandingState __instance)
    {
        return !CommanderAirCommandService.TryOverrideLandingAirbase(
            __instance,
            StateAircraftField,
            LandingModeField,
            LandingAirbaseField,
            LandingRunwayUsageField,
            LandingSpeedField);
    }

    internal static Aircraft? GetStateAircraft(AIPilotCombatModes state)
    {
        return StateAircraftField?.GetValue(state) as Aircraft;
    }

}
