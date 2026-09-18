using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>
/// The operations service's answer to the health line (<see cref="CommanderHealthDiagnostics"/>):
/// how big its per-commander tables currently are, summed across every commanded headquarters.
/// </summary>
/// <remarks>
/// A file of its own rather than a member on the service so the diagnostic can be added and taken
/// away again without touching the file the doctrine lives in. Read-only: it counts and returns, and
/// must never be the reason a table changes.
/// </remarks>
internal sealed partial class CommanderOperationsService
{
    /// <summary>The tables worth watching, totalled over every commander. Totals rather than a
    /// per-commander split because the two commanders' figures move together by construction — both
    /// run the same review — and the health line already splits the two game-side tables where the
    /// split carries information.</summary>
    internal void CollectHealthCounts(
        out int pool,
        out int platoons,
        out int missions,
        out int sorties,
        out int airframes,
        out int airIssued)
    {
        pool = 0;
        platoons = 0;
        missions = 0;
        sorties = 0;
        airframes = 0;
        airIssued = 0;
        foreach (KeyValuePair<FactionHQ, OperationsState> entry in states)
        {
            OperationsState state = entry.Value;
            pool += state.Pool.Count;
            platoons += state.Platoons.Count;
            missions += state.Missions.Count;
            sorties += state.AirSorties.Count;
            airframes += state.CommanderAirframes.Count;
            airIssued += state.AirIssued.Count;
        }
    }
}
