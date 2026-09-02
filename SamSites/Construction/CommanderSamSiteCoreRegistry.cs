using System.Collections.Generic;

namespace GroundControlRts;

internal static class CommanderSamSiteCoreRegistry
{
    private static readonly HashSet<Unit> cores = new();
    private static readonly HashSet<Unit> trackedSiteUnits = new();
    private static readonly Dictionary<Unit, Unit> visualToCore = new();

    internal static bool IsCore(Unit? unit)
    {
        return unit != null && cores.Contains(unit);
    }

    internal static bool IsTrackedSiteUnit(Unit? unit)
    {
        return unit != null && trackedSiteUnits.Contains(unit);
    }

    internal static Unit? ResolveSelection(Unit? unit)
    {
        return unit != null && visualToCore.TryGetValue(unit, out Unit core)
            ? core
            : unit;
    }

    internal static void Register(Unit core, Unit? visual)
    {
        cores.Add(core);
        trackedSiteUnits.Add(core);
        if (visual != null)
        {
            trackedSiteUnits.Add(visual);
            visualToCore[visual] = core;
        }
    }

    internal static void RegisterTracked(Unit? unit)
    {
        if (unit != null)
        {
            trackedSiteUnits.Add(unit);
        }
    }

    internal static void MapVisualToCore(Unit? visual, Unit? core)
    {
        if (visual == null || core == null)
        {
            return;
        }

        trackedSiteUnits.Add(visual);
        visualToCore[visual] = core;
    }

    internal static void Unregister(Unit? unit)
    {
        if (unit == null)
        {
            return;
        }

        cores.Remove(unit);
        trackedSiteUnits.Remove(unit);
        List<Unit> visuals = new();
        foreach (KeyValuePair<Unit, Unit> entry in visualToCore)
        {
            if (entry.Key == null || entry.Value == null || ReferenceEquals(entry.Key, unit) || ReferenceEquals(entry.Value, unit))
            {
                if (!ReferenceEquals(entry.Key, null))
                {
                    visuals.Add(entry.Key);
                }
            }
        }

        for (int i = 0; i < visuals.Count; i++)
        {
            visualToCore.Remove(visuals[i]);
        }
    }

    internal static void Clear()
    {
        cores.Clear();
        trackedSiteUnits.Clear();
        visualToCore.Clear();
    }
}
