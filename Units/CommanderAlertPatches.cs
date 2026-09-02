using HarmonyLib;

namespace GroundControlRts;

/// <summary>
/// Damage and kill notifications. <c>Unit.RecordDamage</c> is the one place every damage event
/// funnels through, so a postfix there is enough to notice the faction losing units.
/// </summary>
/// <remarks>
/// ponytail: both hooks are server-side code paths, so alerts work in singleplayer and when
/// hosting, but a pure multiplayer client only sees the loss and arrival entries that come from
/// the faction's own unit events. Upgrade to the RpcDamage handler only if MP clients matter.
/// </remarks>
[HarmonyPatch]
internal static class CommanderAlertPatches
{
    [HarmonyPatch(typeof(Unit), nameof(Unit.RecordDamage))]
    [HarmonyPostfix]
    private static void RecordDamagePostfix(Unit __instance, PersistentID lastDamagedBy)
    {
        CommanderAlertService.Instance?.NotifyDamage(__instance, lastDamagedBy);
    }

    [HarmonyPatch(typeof(Unit), nameof(Unit.ReportKilled))]
    [HarmonyPostfix]
    private static void ReportKilledPostfix(Unit __instance)
    {
        CommanderAlertService.Instance?.NotifyKilled(__instance);
    }
}
