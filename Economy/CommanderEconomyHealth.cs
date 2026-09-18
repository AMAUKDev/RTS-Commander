namespace GroundControlRts;

/// <summary>
/// The economy service's answer to the health line (<see cref="CommanderHealthDiagnostics"/>): how
/// many buildings each of its level tables is currently holding.
/// </summary>
/// <remarks>
/// Worth watching specifically because two of these three are known to leak. The mine table is swept
/// on the income tick; the factory and dock tables lose an entry only when the PLAYER demolishes the
/// building, never when the enemy destroys it, so under fire they only ever grow. The health line is
/// how that is confirmed in a running match rather than argued from the code
/// (<c>conductor/designs/2026-09-17-frame-rate-investigation.md</c>).
/// </remarks>
internal sealed partial class CommanderEconomyService
{
    /// <summary>Entries in each level table, plus the built-unit set. Read-only.</summary>
    internal void CollectHealthCounts(out int mines, out int factories, out int docks, out int built)
    {
        mines = mineLevels.Count;
        factories = factoryLevels.Count;
        docks = dockLevels.Count;
        built = builtUnits.Count;
    }
}
