namespace GroundControlRts;

/// <summary>
/// Air Command's answer to the health line (<see cref="CommanderHealthDiagnostics"/>): how many
/// airframes it is flying missions for, and how many it has ever written a survival line about.
/// </summary>
/// <remarks>
/// The two are reported side by side because they should diverge, and by how much is the point. The
/// mission table is swept every two seconds and drops shot-down airframes; the survival set is never
/// swept at all and only grows, one entry per airframe that has ever been short of fuel or badly
/// damaged. It is a small leak, but it is the clearest confirmed one in the mod, so it doubles as a
/// sanity check that the health line is really measuring what it says it is: if
/// <c>airSurvivalSeen</c> does not climb across a match, the line is not being read correctly.
/// </remarks>
internal sealed partial class CommanderAirCommandService
{
    /// <summary>Live commander-issued air missions, and airframes ever reported on. Read-only.</summary>
    internal void CollectHealthCounts(out int airMissions, out int survivalSeen)
    {
        airMissions = missions.Count;
        survivalSeen = survivalReported.Count;
    }
}
