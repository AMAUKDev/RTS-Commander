using HarmonyLib;

namespace GroundControlRts;

[HarmonyPatch]
internal static class CommanderEconomyPatches
{
    /// <summary>
    /// A factory's production cadence is a <c>SlowUpdate</c> registered once with the interval it
    /// was authored with, so raising <c>productionInterval</c> later reschedules nothing. The only
    /// lever that moves is the batch size, so an upgraded factory tops up the reserve here, right
    /// after the Basegame added its single unit.
    /// </summary>
    [HarmonyPatch(typeof(Factory), "ProduceUnit")]
    [HarmonyPostfix]
    private static void ProduceUnitPostfix(Factory __instance)
    {
        Unit attached = __instance.attachedUnit;
        if (attached == null || __instance.ProductionUnit == null)
        {
            return;
        }

        int bonus = CommanderEconomyService.GetFactoryOutput(attached) - 1;
        FactionHQ hq = attached.NetworkHQ;
        // AddSupplyUnit is [Server]: it throws rather than no-ops on a pure client.
        if (bonus > 0 && hq != null && hq.IsServer)
        {
            hq.AddSupplyUnit(__instance.ProductionUnit, bonus);
        }
    }
}
