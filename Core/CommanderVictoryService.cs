using System.Collections.Generic;
using NuclearOption.SavedMission.Outcomes;

namespace GroundControlRts;

/// <summary>
/// Ends the match when a faction is left holding no airbase. Capturing the last base an enemy
/// owns is the win condition of an RTS round, and the mod needs one that does not depend on the
/// mission author having written a matching <c>CaptureAirbase</c> objective for every base on the
/// map — a base lost to a capture the mission never named would otherwise leave the round running
/// with a faction that can neither launch nor build.
/// </summary>
/// <remarks>
/// Core tier and a persistent tick: it must run whether or not the player has RTS mode open, and
/// on every mission, not just the mod's own. Host only — <c>DeclareEndGame</c> is a server RPC,
/// and it is the host that owns the result.
/// <para>
/// A faction is only judged once it has held a base, so the seconds before the mission finishes
/// registering airbases cannot declare everybody the loser at t=0.
/// </para>
/// </remarks>
internal sealed class CommanderVictoryService : ICommanderTickPersistent, ICommanderResetSession
{
    private const float CheckIntervalSeconds = 5f;

    private readonly HashSet<FactionHQ> everHeldBase = new();

    private float nextCheckAt;
    private bool declared;

    public void ResetSession()
    {
        everHeldBase.Clear();
        declared = false;
        nextCheckAt = 0f;
    }

    public void TickPersistent()
    {
        if (declared
            || GameManager.gameResolution != GameResolution.Ongoing
            || !CommanderScheduler.IsDue(ref nextCheckAt, CheckIntervalSeconds))
        {
            return;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || !hq.IsServer || hq.faction == null)
            {
                continue;
            }

            if (CountBases(hq) > 0)
            {
                everHeldBase.Add(hq);
                continue;
            }

            if (!everHeldBase.Contains(hq))
            {
                continue;
            }

            // One call settles both sides: RpcDeclareEndGame gives this faction the defeat and
            // everybody else the mirror result, so there is no second declaration to make.
            declared = true;
            hq.DeclareEndGame(EndType.Defeat);
            CommanderPlugin.Log.LogInfo(
                $"{hq.faction.factionName} has lost its last base — declaring defeat.");
            return;
        }
    }

    private static int CountBases(FactionHQ hq)
    {
        int count = 0;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled)
            {
                count++;
            }
        }

        return count;
    }
}
