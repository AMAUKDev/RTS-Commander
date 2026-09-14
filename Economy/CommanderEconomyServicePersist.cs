using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>
/// Hot-reload persistence for mine/factory/dock upgrade levels (design §Section 3, developer
/// quality-of-life, off by default — see <see cref="CommanderStateStore"/>). This is deliberately
/// narrower than the class remark above (levels dying with the building on a *mission* reload is
/// still the intended stake): a hot reload is not a mission reload, the world and every building
/// in it are untouched, only this mod's own in-memory dictionaries are wiped, so restoring them is
/// restoring bookkeeping, not granting anything the player did not already hold.
/// </summary>
internal sealed partial class CommanderEconomyService
{
    /// <summary>How far a restored mine may be from a resource site and still count as standing on
    /// it. Design §Section 3 names 100 m; wider than <see cref="CommanderStrategicPointService.AttachMine"/>'s
    /// own 1 m (used only right after a fresh <c>SpawnBuilding</c> at the exact site position) because
    /// a restored mine's placement offset within the site footprint is not itself saved.</summary>
    private const float MineReattachRadiusMeters = 100f;

    public void Snapshot(CommanderStateWriter w)
    {
        AddLevels(w.Snapshot.MineLevels, mineLevels);
        AddLevels(w.Snapshot.FactoryLevels, factoryLevels);
        AddLevels(w.Snapshot.DockLevels, dockLevels);
    }

    public void Restore(CommanderStateReader r)
    {
        int restoredMines = RestoreLevels(r.Snapshot.MineLevels, mineLevels);
        int restoredFactories = RestoreLevels(r.Snapshot.FactoryLevels, factoryLevels);
        int restoredDocks = RestoreLevels(r.Snapshot.DockLevels, dockLevels);

        int reattached = 0;
        CommanderStrategicPointService? pointService = CommanderStrategicPointService.Instance;
        if (pointService != null)
        {
            foreach (KeyValuePair<Unit, int> entry in mineLevels)
            {
                if (entry.Key != null && !entry.Key.disabled
                    && pointService.AttachNearestMine(entry.Key, MineReattachRadiusMeters))
                {
                    reattached++;
                }
            }
        }

        CommanderPlugin.Log.LogInfo(
            $"Economy restored {restoredMines} mine levels ({reattached} reattached to a resource site), "
                + $"{restoredFactories} factory levels, {restoredDocks} dock levels.");
    }

    private static void AddLevels(List<CommanderEconomyLevelRecord> target, Dictionary<Unit, int> source)
    {
        foreach (KeyValuePair<Unit, int> entry in source)
        {
            if (entry.Key == null || entry.Key.disabled)
            {
                continue;
            }

            target.Add(new CommanderEconomyLevelRecord { PersistentId = entry.Key.persistentID.Id, Level = entry.Value });
        }
    }

    private static int RestoreLevels(List<CommanderEconomyLevelRecord> records, Dictionary<Unit, int> target)
    {
        int restored = 0;
        for (int i = 0; i < records.Count; i++)
        {
            PersistentID id = new() { Id = records[i].PersistentId };
            if (id.TryGetUnit(out Unit unit) && unit != null && !unit.disabled)
            {
                target[unit] = records[i].Level;
                restored++;
            }
        }

        return restored;
    }
}
