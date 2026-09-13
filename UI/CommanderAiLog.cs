using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The one funnel every AI decision line — the enemy commander's, the player commander's when the
/// switch is on, and the strategic point hold/garrison lines — writes through. It keeps
/// `BepInEx\LogOutput.log` exactly as it always was (design SS4 asks for this: "no second set of
/// strings") and, on top of that, files the same text into a per-faction ring buffer the COMMANDER
/// LOG window (T11) reads.
/// </summary>
internal static class CommanderAiLog
{
    /// <summary>Decisions kept per faction — design's "last 200 decisions". At roughly one entry
    /// per 30 s review across the (at most four) things a commander spends on, that is close to an
    /// hour of play before the oldest line is trimmed.</summary>
    private const int Capacity = 200;

    /// <summary>The ring buffer itself, one list per faction, capped at <see cref="Capacity"/>
    /// entries each. Keyed by <see cref="FactionHQ"/> rather than a faction index or name because
    /// that is the same identity every other per-faction service in the mod uses to distinguish
    /// HQs, including the local one when the player-commander switch is on.</summary>
    private static readonly Dictionary<FactionHQ, List<Entry>> logs = new();

    /// <summary>One logged decision: when, in mission time, and what — the label is prepended at
    /// display time by whoever reads the tab, not stored twice.</summary>
    internal readonly struct Entry
    {
        internal Entry(float missionTime, string text)
        {
            MissionTime = missionTime;
            Text = text;
        }

        internal float MissionTime { get; }
        internal string Text { get; }
    }

    /// <summary>
    /// `mm:ss` of a mission-time value. Moved here from
    /// <c>Units/CommanderAlertService.LogEntry.Timestamp</c> (Reuse rule 3): the COMMANDER LOG
    /// window needed the exact same formatter, so it now has one definition instead of two.
    /// </summary>
    internal static string FormatMissionTime(float seconds)
    {
        return $"{Mathf.FloorToInt(seconds / 60f):00}:{Mathf.FloorToInt(seconds % 60f):00}";
    }

    /// <summary>
    /// Logs one AI decision for <paramref name="hq"/>: to the BepInEx console exactly as every
    /// `LogInfo` call already did (byte-identical text), and into that faction's COMMANDER LOG tab.
    /// <paramref name="detail"/> is the one extra parenthetical the unit-purchase line has always
    /// carried (which plan the purchase was made under).
    /// </summary>
    internal static void Note(FactionHQ hq, string text, string? detail = null)
    {
        string line = $"{CommanderPlayerCommanderService.CommanderLabel(hq, detail)} {text}";
        CommanderPlugin.Log.LogInfo(line);

        if (!logs.TryGetValue(hq, out List<Entry> entries))
        {
            entries = new List<Entry>();
            logs[hq] = entries;
        }

        entries.Insert(0, new Entry(Time.timeSinceLevelLoad, text));
        if (entries.Count > Capacity)
        {
            entries.RemoveAt(entries.Count - 1);
        }
    }

    /// <summary>This faction's decisions, newest first. An empty list for an HQ with none yet.</summary>
    internal static IReadOnlyList<Entry> For(FactionHQ hq)
    {
        return logs.TryGetValue(hq, out List<Entry> entries) ? entries : System.Array.Empty<Entry>();
    }

    /// <summary>
    /// Called from <see cref="CommanderStrategicPointService.ResetSession"/> — the one reset hook
    /// this track owns and the natural place for it, since every point (and therefore every
    /// garrison and hold-state log line) is about to be rediscovered from scratch anyway.
    /// </summary>
    internal static void Clear()
    {
        logs.Clear();
    }
}
