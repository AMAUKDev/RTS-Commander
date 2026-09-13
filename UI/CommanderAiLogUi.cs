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
internal sealed class CommanderAiLogUi
{
    /// <summary>IMGUI window id, ASCII <c>COMA</c>. <see cref="CommanderUnitListUi"/>'s own window
    /// (the only other one in the mod) is <c>0x434F4D4C</c> — ASCII <c>COML</c> — so the two differ
    /// only in their last byte (<c>A</c> vs <c>L</c>); <c>GUI.Window</c> needs distinct ids to tell
    /// the windows apart, and this is the smallest change to the borrowed id that guarantees
    /// that.</summary>
    private const int WindowId = 0x434F4D41;

    /// <summary>Seconds between rebuilds of the faction tab list. Matches
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

    private readonly List<FactionHQ> tabs = new();

    private Rect windowRect;
    private Vector2 scroll;
    private bool positionInitialized;
    private bool helpVisible;
    private float nextRefreshAt;
    private FactionHQ? selectedHq;

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
            }
        }

        y += 32f;

        FactionHQ? selected = selectedHq;
        if (selected == null)
        {
            GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
            return;
        }

        y = DrawHeader(selected, y);
        DrawBody(selected, y);
        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
    }

    private float DrawHeader(FactionHQ hq, float y)
    {
        GUI.Label(
            new Rect(10f, y, windowRect.width - 20f, 22f),
            $"FUNDS {CommanderEconomyService.FundsLabel(hq.factionFunds)}",
            CommanderUiTheme.Label);
        y += 22f;

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
        GUI.Label(
            new Rect(10f, y, windowRect.width - 20f, 22f),
            $"INCOME/MIN  BASES +{counts.Bases * CommanderSettings.PointsBaseIncomePerMinute:0}  "
                + $"VILLAGES +{counts.Villages * CommanderSettings.PointsVillageIncomePerMinute:0}  "
                + $"HILLTOPS +{counts.Hilltops * CommanderSettings.PointsHilltopIncomePerMinute:0}  "
                + $"CONTROL PTS +{controlIncome:0}  MINES +{mineIncome:0}",
            CommanderUiTheme.Label);
        y += 22f;

        string plan = CommanderEnemyCommanderService.Instance?.GetPlanLabel(hq) ?? "NONE";
        string reserve = CommanderEconomyService.FundsLabel(CommanderEconomyService.GetEnemyBuildReserve(hq));
        GUI.Label(
            new Rect(10f, y, windowRect.width - 20f, 22f),
            $"PLAN {plan}   RESERVE TARGET {reserve}",
            CommanderUiTheme.Label);
        y += 26f;
        return y;
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

        for (int i = 0; i < entries.Count; i++)
        {
            CommanderAiLog.Entry entry = entries[i];
            float rowY = 2f + i * RowHeight;
            GUI.Label(
                new Rect(6f, rowY, 60f, RowHeight - 4f),
                CommanderAiLog.FormatMissionTime(entry.MissionTime),
                CommanderUiTheme.MutedLabel);
            GUI.Label(new Rect(68f, rowY, inner.width - 74f, RowHeight - 4f), entry.Text, CommanderUiTheme.Label);
        }

        GUI.EndScrollView();
    }
}
