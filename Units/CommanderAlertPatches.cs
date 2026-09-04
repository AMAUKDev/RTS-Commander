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
        // Same hook, other side of the board: a hostile commander that is being shot at goes to
        // its defence posture even when nothing of its own ever saw the shooter.
        CommanderEnemyCommanderService.Instance?.NotifyUnitDamaged(__instance);
    }

    [HarmonyPatch(typeof(Unit), nameof(Unit.ReportKilled))]
    [HarmonyPostfix]
    private static void ReportKilledPostfix(Unit __instance)
    {
        CommanderAlertService.Instance?.NotifyKilled(__instance);
    }

    /// <summary>
    /// <c>Airbase.CaptureFaction</c> is the one place an airbase changes owner — the capture ring
    /// completing, the last defender dying, a mission forcing it — so one pair of hooks here
    /// reports every base that changes hands, for the player and against them alike. The prefix
    /// only remembers who held it, because the postfix cannot ask any more.
    /// </summary>
    [HarmonyPatch(typeof(Airbase), "CaptureFaction")]
    [HarmonyPrefix]
    private static void CaptureFactionPrefix(Airbase __instance, out FactionHQ? __state)
    {
        __state = __instance.CurrentHQ;
    }

    [HarmonyPatch(typeof(Airbase), "CaptureFaction")]
    [HarmonyPostfix]
    private static void CaptureFactionPostfix(Airbase __instance, FactionHQ? __state)
    {
        // Missions hand their airbases to their factions during load, and every one of those is a
        // "capture" as far as this method is concerned. Same ten-second grace the arrival alerts use.
        if (ReferenceEquals(__state, __instance.CurrentHQ) || UnityEngine.Time.timeSinceLevelLoad < 10f)
        {
            return;
        }

        CommanderAlertService.Instance?.NotifyCapture(CaptureText(__instance, __state));
    }

    private static string CaptureText(Airbase airbase, FactionHQ? previous)
    {
        string label = CommanderCaptureService.GetAirbaseLabel(airbase).ToUpperInvariant();
        FactionHQ? local = CommanderGameAccess.GetLocalHq();
        FactionHQ? owner = airbase.CurrentHQ;
        if (owner != null && ReferenceEquals(owner, local))
        {
            return $"CAPTURED {label}";
        }

        if (previous != null && ReferenceEquals(previous, local))
        {
            return $"LOST {label}";
        }

        return owner == null
            ? $"{label} IS NEUTRAL"
            : $"{FactionName(owner)} TOOK {label}";
    }

    private static string FactionName(FactionHQ hq)
    {
        string? name = hq.faction?.factionName;
        return string.IsNullOrEmpty(name) ? "ENEMY" : name!.ToUpperInvariant();
    }
}
