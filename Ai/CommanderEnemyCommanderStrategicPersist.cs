using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The one piece of the enemy commander's state the strategic save carries, and the most dangerous
/// omission the feasibility study found: which factions have already had their opening balance.
/// </summary>
/// <remarks>
/// <para>
/// <c>CommanderEconomyService.ShouldOpenTreasury(isLocal, alreadyPrepared)</c> is
/// <c>!isLocal &amp;&amp; !alreadyPrepared</c>, and <c>alreadyPrepared</c> is
/// <c>CommanderState.Prepared</c>, which <c>ResetSession</c> clears on every mission change. So a
/// strategic load that puts the money back without this flag has every computer faction's very next
/// review re-open its treasury ON TOP of the restored war chest — the duel head start again, the
/// matched-economy reset again, the difficulty multiplier applied a second time. Nothing logs it and
/// the match is simply unwinnable.
/// </para>
/// <para>
/// Nothing else of the commander's picture is saved. Its plan, standing posts, savings pots and
/// strike base are all order-in-flight or re-derived within a review or two, and the developer's
/// scope for this track is the strategic layer only.
/// </para>
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService : ICommanderPersistStrategic
{
    /// <summary>Faction names read out of the strategic save that had already opened their
    /// treasury. Applied to the per-faction state as each one is first reviewed, because on the
    /// frame the save loads the states do not exist yet — the first review is what creates them.</summary>
    private readonly HashSet<string> strategicPreparedFactions = new(StringComparer.Ordinal);

    public void SnapshotStrategic(CommanderStrategicWriter w)
    {
        foreach (KeyValuePair<FactionHQ, CommanderState> entry in states)
        {
            FactionHQ hq = entry.Key;
            if (hq?.faction == null || string.IsNullOrEmpty(hq.faction.factionName))
            {
                continue;
            }

            CommanderState state = entry.Value;
            if (state.Prepared)
            {
                w.Snapshot.PreparedFactions.Add(hq.faction.factionName);
            }

            // The pots the commander holds OUTSIDE the treasury. Not extra money — they are
            // earmarks against future income rather than a second balance — but dropping them
            // resets every saving-up decision the commander had made, so a load would start a
            // commander that was two reviews from an AWACS back at nothing. The economy service
            // fills in this record's structure bank, which lives on its side.
            w.Snapshot.Savings.Add(new CommanderStrategicSavingsRecord
            {
                Faction = hq.faction.factionName,
                NavalFund = state.NavalFund,
                AirFund = state.AirFund,
                AwacsSavings = state.AwacsSavings,
                PicketSavings = state.PicketSavings,
            });
        }
    }

    /// <summary>
    /// Puts the out-of-treasury pots back. Driven by the economy service, which owns the structure
    /// bank half of the same record, so the whole of a commander's money is restored in one step
    /// and in one place. A faction whose state does not exist yet gets one, because otherwise the
    /// first review would create a fresh one and the pots would be lost anyway.
    /// </summary>
    internal void ApplyStrategicSavings(List<CommanderStrategicSavingsRecord> records)
    {
        for (int i = 0; i < records.Count; i++)
        {
            CommanderStrategicSavingsRecord record = records[i];
            FactionHQ? hq = CommanderEconomyService.StrategicHqByName(record.Faction);
            if (hq == null)
            {
                continue;
            }

            if (!states.TryGetValue(hq, out CommanderState state))
            {
                state = new CommanderState();
                states[hq] = state;
                ApplyStrategicPrepared(hq, state);
            }

            state.NavalFund = Mathf.Max(0f, record.NavalFund);
            state.AirFund = Mathf.Max(0f, record.AirFund);
            state.AwacsSavings = Mathf.Max(0f, record.AwacsSavings);
            state.PicketSavings = Mathf.Max(0f, record.PicketSavings);
        }
    }

    public void RestoreStrategic(CommanderStrategicReader r)
    {
        // The prepared flags are NOT loaded here. They are the first thing a restore applies, well
        // before this load-only fan-out runs, because the commander's first review fires on its own
        // first tick and opens its treasury there. See ApplyStrategicPrepared.
    }

    /// <summary>
    /// Takes the saved "already opened its treasury" flags, and applies them to any per-faction
    /// state that already exists. Called at the very start of a restore, ahead of everything else.
    /// </summary>
    internal void ApplyStrategicPrepared(List<string> factionNames)
    {
        strategicPreparedFactions.Clear();
        for (int i = 0; i < factionNames.Count; i++)
        {
            if (!string.IsNullOrEmpty(factionNames[i]))
            {
                strategicPreparedFactions.Add(factionNames[i]);
            }
        }

        // A faction whose state already exists is seeded now; one whose state does not is seeded the
        // moment Review creates it. Both paths are needed, because whether a commander has reviewed
        // yet depends on frame ordering this must not rely on.
        foreach (KeyValuePair<FactionHQ, CommanderState> entry in states)
        {
            ApplyStrategicPrepared(entry.Key, entry.Value);
        }

        CommanderPlugin.Log.LogInfo(
            $"Strategic load: {strategicPreparedFactions.Count} factions had already opened their "
                + "treasury and will not be given a second opening balance.");
    }

    /// <summary>
    /// Whether this faction's treasury has already been opened, once the strategic save is taken
    /// into account. Called from <c>Review</c> the moment a faction's state is created, which is
    /// the first point at which the flag can be applied — the states do not exist on the frame the
    /// save loads. Pure apart from the lookup, and the rule it feeds
    /// (<c>CommanderEconomyService.ShouldOpenTreasury</c>) is already checked at load.
    /// </summary>
    private bool WasPreparedBeforeLoad(FactionHQ hq)
    {
        return hq?.faction != null
            && strategicPreparedFactions.Count > 0
            && strategicPreparedFactions.Contains(hq.faction.factionName);
    }

    /// <summary>
    /// Seeds a freshly created per-faction state from the strategic save. One call site, in
    /// <c>Review</c>, right where the state is created: any later and the opening balance has
    /// already been handed out.
    /// </summary>
    private void ApplyStrategicPrepared(FactionHQ hq, CommanderState state)
    {
        if (!state.Prepared && WasPreparedBeforeLoad(hq))
        {
            state.Prepared = true;
            CommanderAiLog.Note(
                hq, "strategic load: treasury already opened before the save, so no second opening balance.");
            CommanderPlugin.Log.LogInfo(
                $"Strategic load: {hq.faction.factionName} keeps its restored war chest; "
                    + "no second opening balance.");
        }
    }
}
