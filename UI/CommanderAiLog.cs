using System.Collections;
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

    /// <summary>The ring buffer itself, one per faction, capped at <see cref="Capacity"/>
    /// entries each. Keyed by <see cref="FactionHQ"/> rather than a faction index or name because
    /// that is the same identity every other per-faction service in the mod uses to distinguish
    /// HQs, including the local one when the player-commander switch is on.</summary>
    private static readonly Dictionary<FactionHQ, Ring> logs = new();

    /// <summary>One logged decision: when, in mission time, and what — the label is prepended at
    /// display time by whoever reads the tab, not stored twice. The `mm:ss` stamp is formatted here,
    /// once per decision, rather than in the window's draw loop: the window redraws its whole
    /// 200-row list on every IMGUI event (twice a frame at rest), so formatting there cost 200
    /// string allocations per event for text that never changes once written.</summary>
    internal readonly struct Entry
    {
        internal Entry(float missionTime, string text)
        {
            MissionTime = missionTime;
            Text = text;
            Timestamp = FormatMissionTime(missionTime);
        }

        internal float MissionTime { get; }
        internal string Text { get; }

        /// <summary><see cref="MissionTime"/> as `mm:ss`, built once when the entry was logged.</summary>
        internal string Timestamp { get; }
    }

    /// <summary>
    /// A fixed-size newest-first view over the last <see cref="Capacity"/> entries. Replaces the
    /// `List.Insert(0, …)` + `RemoveAt(Count - 1)` pair this class used to do on every decision:
    /// inserting at the front shifted all 200 elements on every single log line, and the mod logs
    /// from 200-odd call sites whether or not the COMMANDER LOG window is open. Writes now land in
    /// one slot and move a start index; the newest-first order the window wants is produced by the
    /// indexer instead of by the storage order.
    /// </summary>
    internal sealed class Ring : IReadOnlyList<Entry>
    {
        private readonly Entry[] slots;

        /// <summary>Slot holding the OLDEST live entry. Advances only once the ring is full.</summary>
        private int start;

        private int count;

        internal Ring(int capacity)
        {
            slots = new Entry[capacity];
        }

        public int Count => count;

        /// <summary>Newest first: index 0 is the most recent decision, <see cref="Count"/> - 1 the
        /// oldest still kept.</summary>
        public Entry this[int index]
        {
            get
            {
                if (index < 0 || index >= count)
                {
                    throw new System.ArgumentOutOfRangeException(nameof(index));
                }

                return slots[(start + count - 1 - index) % slots.Length];
            }
        }

        /// <summary>Files one decision, dropping the oldest once the ring is full.</summary>
        internal void Add(in Entry entry)
        {
            if (count < slots.Length)
            {
                slots[(start + count) % slots.Length] = entry;
                count++;
                return;
            }

            slots[start] = entry;
            start = (start + 1) % slots.Length;
        }

        public IEnumerator<Entry> GetEnumerator()
        {
            for (int i = 0; i < count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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

        if (!logs.TryGetValue(hq, out Ring entries))
        {
            entries = new Ring(Capacity);
            logs[hq] = entries;
        }

        entries.Add(new Entry(Time.timeSinceLevelLoad, text));
    }

    /// <summary>This faction's decisions, newest first. An empty list for an HQ with none yet.</summary>
    internal static IReadOnlyList<Entry> For(FactionHQ hq)
    {
        return logs.TryGetValue(hq, out Ring entries) ? entries : System.Array.Empty<Entry>();
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

    /// <summary>
    /// The ring's four rules, run once from <see cref="CommanderPlugin"/> at load: it counts up to
    /// capacity and stops there, it reads newest-first, a full ring drops the OLDEST entry rather
    /// than the newest, and the order survives wrapping past the end of the backing array. Every
    /// one of those is a silent failure in the window (lines in the wrong order, or the wrong 200
    /// lines kept) rather than an exception, so it is pinned here.
    /// </summary>
    internal static void SelfCheck()
    {
        List<string> failures = new();
        CollectSelfCheckFailures(failures);

        if (failures.Count == 0)
        {
            CommanderPlugin.Log.LogDebug("Commander log self-check passed.");
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Commander log self-check FAILED: {failures[i]}");
        }
    }

    /// <summary>The cases themselves, with no BepInEx logging in them, so an offline harness can
    /// load the built assembly and read the same named failures the game would log.</summary>
    internal static void CollectSelfCheckFailures(List<string> failures)
    {
        Ring ring = new(3);

        Expect(failures, "empty ring has no entries", ring.Count, 0);

        ring.Add(new Entry(0f, "a"));
        ring.Add(new Entry(60f, "b"));
        Expect(failures, "partly filled ring counts its entries", ring.Count, 2);
        Expect(failures, "newest entry reads first", ring[0].Text, "b");
        Expect(failures, "older entry reads second", ring[1].Text, "a");

        ring.Add(new Entry(120f, "c"));
        Expect(failures, "ring fills to capacity", ring.Count, 3);

        ring.Add(new Entry(180f, "d"));
        Expect(failures, "a full ring stays at capacity", ring.Count, 3);
        Expect(failures, "the newest entry survives the overwrite", ring[0].Text, "d");
        Expect(failures, "order is preserved across the wrap", ring[1].Text, "c");
        Expect(failures, "the oldest entry is the one dropped", ring[2].Text, "b");

        // Two more laps: the start index has now moved right round the backing array, which is
        // where an off-by-one in the modulo shows up and nowhere earlier.
        ring.Add(new Entry(240f, "e"));
        ring.Add(new Entry(300f, "f"));
        ring.Add(new Entry(360f, "g"));
        Expect(failures, "a wrapped ring still reads newest first", ring[0].Text, "g");
        Expect(failures, "a wrapped ring still reads oldest last", ring[2].Text, "e");

        Expect(failures, "mission time formats as mm:ss", FormatMissionTime(125f), "02:05");
        Expect(failures, "an entry stamps itself once", new Entry(3661f, "x").Timestamp, "61:01");
    }

    private static void Expect(List<string> failures, string name, int actual, int expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, string actual, string expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }
}
