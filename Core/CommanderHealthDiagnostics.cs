using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// One labelled line into the BepInEx log every <see cref="ReportIntervalSeconds"/> seconds carrying
/// the counts that separate the remaining explanations for the long-match frame-rate decay
/// (<c>conductor/designs/2026-09-17-frame-rate-investigation.md</c>). Two of those lines — one early
/// in a match, one once it feels bad — are the whole measurement: whatever has grown between them is
/// where to look, and whatever has not is ruled out.
/// </summary>
/// <remarks>
/// Off by default (<see cref="CommanderSettings.HealthDiagnosticLine"/>): this exists to settle one
/// question, not to run for ever.
/// <para>
/// A PERSISTENT-tick service rather than an active-tick one, on purpose. The developer reports
/// (2026-09-17) that the decay is present while flying with RTS mode closed, so a measurement that
/// only ran inside commander mode would miss the very state under investigation. Registered at
/// Core tier for the same reason.
/// </para>
/// <para>
/// ponytail: counts only — no rates, no ratios, no verdict. A number that grows between two lines is
/// the finding; deciding what it means is a reading exercise, and folding judgement into the line
/// would only hide the raw figures the next theory will want. Nothing here is allowed to allocate on
/// an ordinary frame: the per-frame path is three float operations and the strings are built once
/// per report into a reused buffer, because a diagnostic that adds garbage to a garbage-collection
/// investigation measures itself.
/// </para>
/// </remarks>
internal sealed class CommanderHealthDiagnostics : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>Seconds between lines: 30, matching the commander review cadence
    /// (<c>Operations/CommanderOperationsService.cs</c>) so a health line sits in the log beside the
    /// review whose state it describes. Long enough that the per-report walks cost nothing
    /// measurable, short enough that a twenty-minute match still yields forty samples.</summary>
    private const float ReportIntervalSeconds = 30f;

    /// <summary>Milliseconds in a second: 1000. Frame times are reported in milliseconds because
    /// that is the unit a frame budget is quoted in — 16.7 at 60 Hz, 33.3 at 30 Hz — and a decay is
    /// far easier to read as a rising budget than as a falling rate.</summary>
    private const float MillisecondsPerSecond = 1000f;

    /// <summary>Bytes in a mebibyte: 1048576. Managed memory is reported in whole mebibytes because
    /// the question is whether the heap grows by hundreds over a match, and byte precision on a
    /// figure that moves with every allocation is noise.</summary>
    private const long BytesPerMebibyte = 1048576L;

    /// <summary>Seconds in a minute: 60, for the elapsed-time stamp.</summary>
    private const int SecondsPerMinute = 60;

    /// <summary>
    /// Reads the game's own per-faction strategic-target list, which has no public accessor. Worth
    /// the reflection because it is a CONFIRMED game-side grower: <c>FactionHQ</c> builds two
    /// separate tracking records and files one in the contact database and the other in this list,
    /// so the removal test compares different object references and can never succeed. Null, and the
    /// figure reported as -1, if a game update ever renames the field — a missing diagnostic must
    /// never be able to stop the plugin loading.
    /// </summary>
    private static readonly AccessTools.FieldRef<FactionHQ, List<TrackingInfo>>? StrategicTargetsRef =
        ResolveStrategicTargets();

    private readonly StringBuilder line = new();
    private readonly StringBuilder perFaction = new();

    private float nextReportAt;
    private float frameSecondsSum;
    private float worstFrameSeconds;
    private int frameSamples;

    /// <summary>Whether the setting was on for the whole window just finished. Re-read once per
    /// report rather than once per frame, so switching the line off leaves no per-frame cost at all
    /// and switching it on discards the part-gathered window instead of reporting a frame average
    /// over an unknown span.</summary>
    private bool gathering;

    public void TickPersistent()
    {
        if (gathering)
        {
            float frameSeconds = Time.unscaledDeltaTime;
            frameSecondsSum += frameSeconds;
            frameSamples++;
            if (frameSeconds > worstFrameSeconds)
            {
                worstFrameSeconds = frameSeconds;
            }
        }

        // Wall-clock, not game-clock: a frame time measured over a window that stopped advancing
        // while the player paused would be an average over a span the samples do not cover.
        if (!CommanderScheduler.IsDueRealtime(ref nextReportAt, ReportIntervalSeconds))
        {
            return;
        }

        bool wasGathering = gathering;
        gathering = CommanderSettings.HealthDiagnosticLine;
        if (gathering && wasGathering)
        {
            Report();
        }

        frameSecondsSum = 0f;
        worstFrameSeconds = 0f;
        frameSamples = 0;
    }

    public void ResetSession()
    {
        nextReportAt = CommanderScheduler.StaggerRealtime("health.line", ReportIntervalSeconds);
        frameSecondsSum = 0f;
        worstFrameSeconds = 0f;
        frameSamples = 0;
        gathering = false;
    }

    /// <summary>Mean frame time over the window in milliseconds, pure. Zero samples answers zero
    /// rather than dividing by it — the first window after the line is switched on is empty by
    /// construction.</summary>
    internal static float AverageFrameMilliseconds(float sumSeconds, int samples)
    {
        return samples <= 0 ? 0f : sumSeconds / samples * MillisecondsPerSecond;
    }

    /// <summary>Elapsed match time as <c>m:ss</c>, pure. Minutes are not wrapped at sixty: a
    /// ninety-minute match reads <c>90:00</c>, because the whole point of the stamp is to say how
    /// far into the match a line was written and an hours field would only invite a misread.</summary>
    internal static string FormatElapsed(float seconds)
    {
        int whole = Mathf.Max(0, Mathf.FloorToInt(seconds));
        return $"{whole / SecondsPerMinute}:{whole % SecondsPerMinute:00}";
    }

    /// <summary>The line's arithmetic at its boundaries, run at plugin load beside the other
    /// services' checks. Only the two pure helpers are checkable here; every other figure on the
    /// line is a live count with nothing to assert against outside a running match.</summary>
    internal static void SelfCheck()
    {
        if (!Mathf.Approximately(AverageFrameMilliseconds(0.5f, 10), 50f))
        {
            CommanderPlugin.Log.LogError(
                "Health self-check FAILED: ten frames over half a second is not 50 ms per frame.");
        }

        if (!Mathf.Approximately(AverageFrameMilliseconds(1f, 0), 0f))
        {
            CommanderPlugin.Log.LogError(
                "Health self-check FAILED: an empty window does not report a zero frame average.");
        }

        if (!Mathf.Approximately(AverageFrameMilliseconds(0.0167f, 1), 16.7f))
        {
            CommanderPlugin.Log.LogError(
                "Health self-check FAILED: a 60 Hz frame does not report as 16.7 ms.");
        }

        if (FormatElapsed(0f) != "0:00")
        {
            CommanderPlugin.Log.LogError("Health self-check FAILED: a match at zero seconds is not 0:00.");
        }

        if (FormatElapsed(61f) != "1:01")
        {
            CommanderPlugin.Log.LogError("Health self-check FAILED: sixty-one seconds is not 1:01.");
        }

        if (FormatElapsed(3661f) != "61:01")
        {
            CommanderPlugin.Log.LogError(
                "Health self-check FAILED: an hour-long match does not keep counting in minutes.");
        }

        if (FormatElapsed(-5f) != "0:00")
        {
            CommanderPlugin.Log.LogError("Health self-check FAILED: a negative elapsed time is not clamped to 0:00.");
        }
    }

    private void Report()
    {
        CountLiveUnits(out int units, out int aircraft, out int ground, out int buildings, out int missiles);

        line.Length = 0;
        line.Append("Health t=").Append(FormatElapsed(Time.timeSinceLevelLoad));
        line.Append(" frameMsAvg=").Append(AverageFrameMilliseconds(frameSecondsSum, frameSamples).ToString("0.0"));
        line.Append(" frameMsWorst=").Append((worstFrameSeconds * MillisecondsPerSecond).ToString("0.0"));
        line.Append(" frames=").Append(frameSamples);
        line.Append(" units=").Append(units);
        line.Append(" aircraft=").Append(aircraft);
        line.Append(" ground=").Append(ground);
        line.Append(" buildings=").Append(buildings);
        line.Append(" missiles=").Append(missiles);
        line.Append(" everSpawned=").Append(UnitRegistry.persistentUnitLookup.Count);
        line.Append(" wrecks=").Append(WreckCount());
        line.Append(" tracked=").Append(PerFaction(strategic: false));
        line.Append(" strategicTargets=").Append(PerFaction(strategic: true));
        AppendModTables();
        line.Append(" heapMB=").Append(GC.GetTotalMemory(false) / BytesPerMebibyte);
        line.Append(" gc0=").Append(GC.CollectionCount(0));
        CommanderPlugin.Log.LogInfo(line.ToString());
    }

    /// <summary>Every count the mod itself keeps that is big enough to be worth watching, each named
    /// so a grower is obvious at a glance rather than hidden inside a total. Each service answers for
    /// its own tables; a service that is not running reports -1, which reads as "not asked" and can
    /// never be mistaken for an empty table.</summary>
    private void AppendModTables()
    {
        CommanderMarkerService? markers = CommanderMarkerService.Instance;
        line.Append(" markerImages=").Append(markers != null ? markers.HealthMarkerCount : -1);

        CommanderAirCommandService? air = CommanderAirCommandService.Instance;
        if (air != null)
        {
            air.CollectHealthCounts(out int airMissions, out int survivalReported);
            line.Append(" airMissions=").Append(airMissions);
            line.Append(" airSurvivalSeen=").Append(survivalReported);
        }
        else
        {
            line.Append(" airMissions=-1 airSurvivalSeen=-1");
        }

        CommanderSupplyHeliService? supply = CommanderSupplyHeliService.Instance;
        if (supply != null)
        {
            supply.CollectHealthCounts(out int cargoMissions, out int autopilots, out int shielded);
            line.Append(" cargoMissions=").Append(cargoMissions);
            line.Append(" cargoAutopilots=").Append(autopilots);
            line.Append(" shielded=").Append(shielded);
        }
        else
        {
            line.Append(" cargoMissions=-1 cargoAutopilots=-1 shielded=-1");
        }

        CommanderOperationsService? operations = CommanderOperationsService.Instance;
        if (operations != null)
        {
            operations.CollectHealthCounts(
                out int pool, out int platoons, out int missions, out int sorties, out int airframes, out int issued);
            line.Append(" opsPool=").Append(pool);
            line.Append(" opsPlatoons=").Append(platoons);
            line.Append(" opsMissions=").Append(missions);
            line.Append(" opsSorties=").Append(sorties);
            line.Append(" opsAirframes=").Append(airframes);
            line.Append(" opsAirIssued=").Append(issued);
        }
        else
        {
            line.Append(" opsPool=-1 opsPlatoons=-1 opsMissions=-1 opsSorties=-1 opsAirframes=-1 opsAirIssued=-1");
        }

        CommanderEconomyService? economy = CommanderEconomyService.Instance;
        if (economy != null)
        {
            economy.CollectHealthCounts(out int mines, out int factories, out int docks, out int built);
            line.Append(" econMines=").Append(mines);
            line.Append(" econFactories=").Append(factories);
            line.Append(" econDocks=").Append(docks);
            line.Append(" econBuilt=").Append(built);
        }
        else
        {
            line.Append(" econMines=-1 econFactories=-1 econDocks=-1 econBuilt=-1");
        }
    }

    /// <summary>One <c>faction:count</c> pair per headquarters, slash-separated. Per faction rather
    /// than totalled because a table that grows for one commander and not the other says which
    /// commander's work is responsible, and a total would hide exactly that.</summary>
    private string PerFaction(bool strategic)
    {
        perFaction.Length = 0;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || hq.faction == null)
            {
                continue;
            }

            if (perFaction.Length > 0)
            {
                perFaction.Append('/');
            }

            perFaction.Append(hq.faction.name).Append(':');
            if (!strategic)
            {
                perFaction.Append(hq.trackingDatabase != null ? hq.trackingDatabase.Count : -1);
                continue;
            }

            List<TrackingInfo>? targets = StrategicTargetsRef != null ? StrategicTargetsRef(hq) : null;
            perFaction.Append(targets != null ? targets.Count : -1);
        }

        return perFaction.Length == 0 ? "none" : perFaction.ToString();
    }

    /// <summary>Live units split by kind, because "is it the units or the clock" is the whole fork
    /// this line exists to settle and an undivided total cannot answer it: the mod fields ground
    /// vehicles and buildings, and those are what a commander's spending actually adds.
    /// <para>
    /// Missiles are a kind of their own here, and they are NOT a fifth walk: the game models a
    /// missile in flight as a <c>Unit</c> like any other — a direct sibling of <c>Aircraft</c>,
    /// <c>GroundVehicle</c> and <c>Building</c>, confirmed by reflecting over the shipped
    /// <c>Assembly-CSharp.dll</c> — so every live missile was already inside <c>units=</c> and
    /// simply had no name of its own. The research behind this line
    /// (<c>conductor/designs/2026-09-17-nuclear-option-performance-research.md</c>) found the
    /// engine's cost tracking live units, missiles and bombs together, and a wing trading
    /// long-range shots can put a real slice of the total in the air; without this field that slice
    /// read as unexplained units.
    /// </para></summary>
    private static void CountLiveUnits(
        out int units, out int aircraft, out int ground, out int buildings, out int missiles)
    {
        units = 0;
        aircraft = 0;
        ground = 0;
        buildings = 0;
        missiles = 0;
        List<Unit>? all = UnitRegistry.allUnits;
        if (all == null)
        {
            return;
        }

        for (int i = 0; i < all.Count; i++)
        {
            Unit unit = all[i];
            if (unit == null || unit.disabled)
            {
                continue;
            }

            units++;
            if (unit is Aircraft)
            {
                aircraft++;
            }
            else if (unit is GroundVehicle)
            {
                ground++;
            }
            else if (unit is Building)
            {
                buildings++;
            }
            else if (unit is Missile)
            {
                missiles++;
            }
        }
    }

    /// <summary>Wrecks the game is currently holding, or -1 before the mission manager exists. The
    /// missions in this repository set a one-minute wreck decay, so a healthy reading is single
    /// figures; a count in the hundreds would mean the decay is not being applied and the obstacle
    /// lists every ground vehicle walks are growing all match.</summary>
    private static int WreckCount()
    {
        MissionManager? manager = NetworkSceneSingleton<MissionManager>.i;
        return manager?.listWrecks != null ? manager.listWrecks.Count : -1;
    }

    private static AccessTools.FieldRef<FactionHQ, List<TrackingInfo>>? ResolveStrategicTargets()
    {
        try
        {
            return AccessTools.FieldRefAccess<FactionHQ, List<TrackingInfo>>("strategicTargets");
        }
        catch (Exception)
        {
            return null;
        }
    }
}
