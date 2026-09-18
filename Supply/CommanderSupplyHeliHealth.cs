namespace GroundControlRts;

/// <summary>
/// The supply service's answer to the health line (<see cref="CommanderHealthDiagnostics"/>): how
/// many cargo flights, bound autopilots and shielded vehicles it is currently holding.
/// </summary>
/// <remarks>
/// The autopilot count is the one to watch. Its removal is guarded by the aircraft's autopilot
/// component, which is destroyed with the aircraft, so a transport that is shot down may leave its
/// entry behind where the mission table self-heals. The two counts should track each other; a
/// widening gap between <c>cargoAutopilots</c> and <c>cargoMissions</c> on the health line is that
/// leak, and it is the one finding in the investigation that could not be settled by reading.
/// </remarks>
internal sealed partial class CommanderSupplyHeliService
{
    /// <summary>Entries in the three live tables. Read-only.</summary>
    internal void CollectHealthCounts(out int cargoMissions, out int autopilots, out int shieldedVehicles)
    {
        cargoMissions = assignedMissions.Count;
        autopilots = terrainClearanceAutopilots.Count;
        shieldedVehicles = shielded.Count;
    }
}
