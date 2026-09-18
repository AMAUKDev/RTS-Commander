using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// COMMANDER LOG: one tab per faction — yours (and your own AI, when the player-commander switch
/// is on: it is the same HQ, so it is the same tab), then every other commanded faction — showing
/// funds, income by source, the current plan and the reserve target above a scrolling log of every
/// decision that faction's commander has made, newest first. A copy of
/// <see cref="CommanderUnitListUi"/>'s window shape, reading <see cref="CommanderAiLog"/> instead
/// of the unit roster.
/// </summary>
/// <remarks>
/// Everything above the scrolling log is built on the <see cref="RefreshIntervalSeconds"/> clock
/// and cached, not rebuilt inside the window callback. IMGUI runs that callback at least twice a
/// frame (Layout and Repaint) and again for every input event, and the header's inputs are not
/// cheap: the income line walks every strategic point on the map, and the OPERATIONS block asks
/// <see cref="CommanderOperationsService.DescribeOperations"/> for one freshly built string per live
/// mission — sixty of them in a mature match, of which this window shows four. Rebuilding that on
/// every event is what made the window expensive to have open, and steadily more expensive as the
/// mission count grew.
/// </remarks>
internal sealed class CommanderAiLogUi
{
    /// <summary>IMGUI window id, ASCII <c>COMA</c>. <see cref="CommanderUnitListUi"/>'s own window
    /// (the only other one in the mod) is <c>0x434F4D4C</c> — ASCII <c>COML</c> — so the two differ
    /// only in their last byte (<c>A</c> vs <c>L</c>); <c>GUI.Window</c> needs distinct ids to tell
    /// the windows apart, and this is the smallest change to the borrowed id that guarantees
    /// that.</summary>
    private const int WindowId = 0x434F4D41;

    /// <summary>Seconds between rebuilds of the faction tab list and of the cached header. Matches
    /// <see cref="CommanderUnitListUi"/>'s own <c>RefreshIntervalSeconds</c> (also 0.5f) for the
    /// same reason: fast enough that a new decision line feels live, without rebuilding every
    /// frame while the window is open.</summary>
    private const float RefreshIntervalSeconds = 0.5f;

    /// <summary>Log row pitch, in GUI pixels. Copied from the row spacing
    /// <see cref="CommanderUnitListUi"/>'s own battle-log tab uses (the literal <c>26f</c> in its
    /// <c>DrawBattleLog</c>) — not that window's <c>RowHeight</c> constant, which is <c>30f</c> and
    /// sizes its unit rows instead. Kept equal to the log it was copied from so both battle-log
    /// views read at the same density.</summary>
    private const float RowHeight = 26f;

    /// <summary>Gap in GUI pixels between the top of the scroll view's content and the first log
    /// row. The row layout has always been <c>RowTopPadding + i * RowHeight</c>; it is a constant
    /// now only because <see cref="VisibleRowRange"/> has to invert that same expression.</summary>
    private const float RowTopPadding = 2f;

    /// <summary>Rows drawn above and below the slice the scroll view actually shows. One row of
    /// slack absorbs the rounding in <see cref="VisibleRowRange"/> and any sub-pixel scroll offset,
    /// so a partly visible row at either edge is never clipped away. More than one buys nothing:
    /// the range is recomputed from the live scroll offset on every draw.</summary>
    private const int OverscanRows = 1;

    /// <summary>Mission lines shown in the header before the rest fold into a "+n more" tail — the
    /// 520 px line budget this window's own note already flags (see the class remarks on
    /// <c>DrawHeader</c>'s width). Planner-chosen.</summary>
    private const int MissionLinesInHeader = 3;

    private readonly List<FactionHQ> tabs = new();

    /// <summary>Scratch for the header's OPERATIONS block, refilled on the refresh clock rather
    /// than on every draw.</summary>
    private readonly List<string> operationsLines = new();

    private Rect windowRect;
    private Vector2 scroll;
    private bool positionInitialized;
    private bool helpVisible;
    private float nextRefreshAt;
    private FactionHQ? selectedHq;

    private string fundsLine = string.Empty;
    private string incomeLine = string.Empty;
    private string planLine = string.Empty;
    private string moreOperationsLine = string.Empty;

    internal bool Visible { get; private set; }

    internal void Toggle()
    {
        Visible = !Visible;
        if (Visible)
        {
            nextRefreshAt = 0f;
        }
    }

    internal void Hide() => Visible = false;

    internal void ResetPosition() => positionInitialized = false;

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        return Visible && windowRect.Contains(CommanderUiScale.ScreenToGui(screenPoint));
    }

    internal void Tick()
    {
        if (!Visible)
        {
            return;
        }

        float width = Mathf.Min(520f, CommanderUiScale.Width - 24f);
        float height = Mathf.Min(620f, CommanderUiScale.Height - 24f);
        if (!positionInitialized)
        {
            // Offset from ORDER OF BATTLE's own default spot so the two windows do not land
            // exactly on top of each other the first time either one opens.
            windowRect = new Rect(
                Mathf.Max(12f, 486f + 40f),
                Mathf.Max(12f, CommanderUiScale.Height * 0.5f - height * 0.5f),
                width,
                height);
            positionInitialized = true;
        }
        else
        {
            windowRect.width = width;
            windowRect.height = height;
        }

        windowRect = CommanderUiTheme.ClampWindow(windowRect);

        if (CommanderScheduler.IsDueRealtime(ref nextRefreshAt, RefreshIntervalSeconds))
        {
            RefreshTabs();
            RefreshSummary();
        }
    }

    internal void Draw()
    {
        if (!Visible)
        {
            return;
        }

        windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "COMMANDER LOG", CommanderUiTheme.Window);
    }

    private void RefreshTabs()
    {
        tabs.Clear();
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq != null && localHq.faction != null)
        {
            tabs.Add(localHq);
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq != null && hq.faction != null && !ReferenceEquals(hq, localHq))
            {
                tabs.Add(hq);
            }
        }

        if (selectedHq == null || !tabs.Contains(selectedHq))
        {
            selectedHq = tabs.Count > 0 ? tabs[0] : null;
        }
    }

    /// <summary>
    /// Rebuilds every string above the scrolling log for the selected tab. Called on the refresh
    /// clock and once more the moment a different tab is picked, so a tab switch shows that
    /// faction's figures immediately rather than up to half a second later.
    /// </summary>
    private void RefreshSummary()
    {
        operationsLines.Clear();
        moreOperationsLine = string.Empty;

        FactionHQ? hq = selectedHq;
        if (hq == null)
        {
            fundsLine = string.Empty;
            incomeLine = string.Empty;
            planLine = string.Empty;
            return;
        }

        fundsLine = $"FUNDS {CommanderEconomyService.FundsLabel(hq.factionFunds)}";

        CommanderStrategicPointService? pointService = CommanderStrategicPointService.Instance;
        CommanderStrategicPointService.PointCounts counts = default;
        pointService?.GetPointIncomePerMinute(hq, out counts);
        float mineIncome = CommanderEconomyService.Instance?.GetMineIncomePerMinute(hq) ?? 0f;
        // Outposts, crossroads and roadside points fold into one CONTROL PTS figure rather than
        // three more terms on this line — nine terms across BASES/VILLAGES/HILLTOPS/outposts/
        // crossroads/roadside/MINES would not fit the 520 px window this line is drawn in.
        float controlIncome = counts.Outposts * CommanderSettings.PointsOutpostIncomePerMinute
            + counts.Crossroads * CommanderSettings.PointsCrossroadsIncomePerMinute
            + counts.Roadside * CommanderSettings.PointsRoadsideIncomePerMinute;
        incomeLine = $"INCOME/MIN  BASES +{counts.Bases * CommanderSettings.PointsBaseIncomePerMinute:0}  "
            + $"VILLAGES +{counts.Villages * CommanderSettings.PointsVillageIncomePerMinute:0}  "
            + $"HILLTOPS +{counts.Hilltops * CommanderSettings.PointsHilltopIncomePerMinute:0}  "
            + $"CONTROL PTS +{controlIncome:0}  MINES +{mineIncome:0}";

        string plan = CommanderEnemyCommanderService.Instance?.GetPlanLabel(hq) ?? "NONE";
        // The value is still GetEnemyBuildReserve — the one definition of what the commander builds
        // next — but the ladder (2026-09-14) no longer holds it back from anything, so the old
        // RESERVE TARGET label stopped being true. What rung 4 wants next is what it says.
        string next = CommanderEconomyService.FundsLabel(CommanderEconomyService.GetEnemyBuildReserve(hq));
        planLine = $"PLAN {plan}   NEXT BUILD {next}";

        CommanderOperationsService.Instance?.DescribeOperations(hq, operationsLines);
        int more = operationsLines.Count - 1 - Mathf.Min(operationsLines.Count - 1, MissionLinesInHeader);
        if (operationsLines.Count > 0 && more > 0)
        {
            moreOperationsLine = $"+{more} more";
        }
    }

    private void DrawWindow(int windowId)
    {
        CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible);
        if (GUI.Button(new Rect(windowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.Button))
        {
            Visible = false;
            return;
        }

        float y = 34f;
        if (helpVisible)
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(10f, y, windowRect.width - 20f, 78f),
                "One tab per faction. YOU is your own commander's log, filled in once the player commander switch is "
                    + "on. Funds, income by source and the current buy plan sit above the log; the log itself is "
                    + "every decision that faction's commander has made, newest first.");
            y += 86f;
        }

        if (tabs.Count == 0)
        {
            GUI.Label(new Rect(10f, y, windowRect.width - 20f, 22f), "NO FACTIONS YET", CommanderUiTheme.MutedLabel);
            GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
            return;
        }

        float tabWidth = (windowRect.width - 20f - 4f * (tabs.Count - 1)) / tabs.Count;
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        for (int i = 0; i < tabs.Count; i++)
        {
            FactionHQ hq = tabs[i];
            Rect rect = new(10f + (tabWidth + 4f) * i, y, tabWidth, 26f);
            string label = ReferenceEquals(hq, localHq) ? "YOU" : hq.faction.name.ToUpperInvariant();
            if (GUI.Button(rect, label, ReferenceEquals(selectedHq, hq) ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                selectedHq = hq;
                scroll = Vector2.zero;
                RefreshSummary();
            }
        }

        y += 32f;

        FactionHQ? selected = selectedHq;
        if (selected == null)
        {
            GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
            return;
        }

        y = DrawHeader(y);
        DrawBody(selected, y);
        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
    }

    private float DrawHeader(float y)
    {
        GUI.Label(new Rect(10f, y, windowRect.width - 20f, 22f), fundsLine, CommanderUiTheme.Label);
        y += 22f;
        GUI.Label(new Rect(10f, y, windowRect.width - 20f, 22f), incomeLine, CommanderUiTheme.Label);
        y += 22f;
        GUI.Label(new Rect(10f, y, windowRect.width - 20f, 22f), planLine, CommanderUiTheme.Label);
        y += 26f;

        if (operationsLines.Count > 0)
        {
            GUI.Label(new Rect(10f, y, windowRect.width - 20f, 22f), operationsLines[0], CommanderUiTheme.Label);
            y += 26f;

            int missionLines = Mathf.Min(operationsLines.Count - 1, MissionLinesInHeader);
            for (int i = 0; i < missionLines; i++)
            {
                GUI.Label(new Rect(10f, y, windowRect.width - 20f, 22f), operationsLines[i + 1], CommanderUiTheme.Label);
                y += 26f;
            }

            if (moreOperationsLine.Length > 0)
            {
                GUI.Label(
                    new Rect(10f, y, windowRect.width - 20f, 22f), moreOperationsLine, CommanderUiTheme.MutedLabel);
                y += 26f;
            }
        }

        return y;
    }

    /// <summary>
    /// Which log rows the scroll view can actually show, as a half-open range over the newest-first
    /// entry list. The scroll view's content is <see cref="RowHeight"/> per entry, so 200 entries
    /// make a 5 200 px tall content sheet inside a window about 400 px tall: drawing every row
    /// meant two <c>GUI.Label</c> calls per entry, 400 of them, on every IMGUI event, for the
    /// fifteen-odd rows anyone could see. Pure, so the self-check can pin it.
    /// </summary>
    internal static void VisibleRowRange(
        float scrollY, float viewHeight, int entryCount, out int first, out int lastExclusive)
    {
        if (entryCount <= 0)
        {
            first = 0;
            lastExclusive = 0;
            return;
        }

        int top = Mathf.FloorToInt((scrollY - RowTopPadding) / RowHeight) - OverscanRows;
        int bottom = Mathf.CeilToInt((scrollY + viewHeight - RowTopPadding) / RowHeight) + OverscanRows;
        first = Mathf.Clamp(top, 0, entryCount);
        lastExclusive = Mathf.Clamp(bottom, first, entryCount);
    }

    private void DrawBody(FactionHQ hq, float y)
    {
        IReadOnlyList<CommanderAiLog.Entry> entries = CommanderAiLog.For(hq);
        Rect view = new(10f, y, windowRect.width - 20f, windowRect.height - y - 12f);
        Rect inner = new(0f, 0f, view.width - 18f, Mathf.Max(view.height, entries.Count * RowHeight + 4f));
        scroll = GUI.BeginScrollView(view, scroll, inner);
        if (entries.Count == 0)
        {
            GUI.Label(new Rect(6f, 6f, inner.width - 12f, 22f), "NO DECISIONS YET", CommanderUiTheme.MutedLabel);
        }

        VisibleRowRange(scroll.y, view.height, entries.Count, out int first, out int lastExclusive);
        for (int i = first; i < lastExclusive; i++)
        {
            CommanderAiLog.Entry entry = entries[i];
            float rowY = RowTopPadding + i * RowHeight;
            GUI.Label(new Rect(6f, rowY, 60f, RowHeight - 4f), entry.Timestamp, CommanderUiTheme.MutedLabel);
            GUI.Label(new Rect(68f, rowY, inner.width - 74f, RowHeight - 4f), entry.Text, CommanderUiTheme.Label);
        }

        GUI.EndScrollView();
    }

    /// <summary>
    /// The scroll-culling rule, run once from <see cref="CommanderPlugin"/> at load. A wrong range
    /// here does not throw; it silently blanks the top or bottom row of the COMMANDER LOG, or
    /// quietly gives back the cost the culling was added to save. Pinned at its four boundaries:
    /// an empty log, the top of a long log, a scrolled-down slice, and the far end.
    /// </summary>
    internal static void SelfCheck()
    {
        List<string> failures = new();
        CollectSelfCheckFailures(failures);

        if (failures.Count == 0)
        {
            CommanderPlugin.Log.LogDebug("Commander log window self-check passed.");
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Commander log window self-check FAILED: {failures[i]}");
        }
    }

    /// <summary>The cases themselves, with no BepInEx logging in them, so an offline harness can
    /// load the built assembly and read the same named failures the game would log.</summary>
    internal static void CollectSelfCheckFailures(List<string> failures)
    {
        VisibleRowRange(0f, 400f, 0, out int emptyFirst, out int emptyLast);
        Expect(failures, "an empty log draws no rows (first)", emptyFirst, 0);
        Expect(failures, "an empty log draws no rows (last)", emptyLast, 0);

        VisibleRowRange(0f, 400f, 200, out int topFirst, out int topLast);
        Expect(failures, "the unscrolled log starts at the newest row", topFirst, 0);
        // 400 px of view at a 26 px pitch is 15.4 rows; ceil plus one row of overscan is 17.
        Expect(failures, "the unscrolled log stops just past the view", topLast, 17);

        VisibleRowRange(1000f, 400f, 200, out int midFirst, out int midLast);
        Expect(failures, "a scrolled log starts one row above the view", midFirst, 37);
        Expect(failures, "a scrolled log stops one row below the view", midLast, 55);

        VisibleRowRange(4900f, 400f, 200, out int endFirst, out int endLast);
        Expect(failures, "the far end never runs past the last entry", endLast, 200);
        Expect(failures, "the far end still starts inside the log", endFirst, 187);

        // A view taller than the whole log must still be clamped to the entries that exist.
        VisibleRowRange(0f, 4000f, 20, out int shortFirst, out int shortLast);
        Expect(failures, "a short log draws all of itself (first)", shortFirst, 0);
        Expect(failures, "a short log draws all of itself (last)", shortLast, 20);
    }

    private static void Expect(List<string> failures, string name, int actual, int expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }
}
