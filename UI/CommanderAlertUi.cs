using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Movable toast window for combat alerts, parked in the top-right corner so it never covers
/// the faction funds readout. Drawn whether or not RTS mode is active; clicking a toast
/// selects the unit and jumps the RTS camera to it.
/// </summary>
internal sealed class CommanderAlertUi
{
    private const int WindowId = 0x434F4D41;
    private const float WindowWidth = 380f;
    private const float TitleHeight = 28f;
    private const float ToastHeight = 30f;

    private readonly CommanderAlertService alertService;
    private readonly CommanderSelectionService selectionService;
    private readonly System.Func<bool> commanderActive;
    private Rect windowRect;
    private bool positionInitialized;
    private CommanderAlertService.Alert? pendingDismiss;

    internal static CommanderAlertUi? Instance { get; private set; }

    internal CommanderAlertUi(
        CommanderAlertService alertService,
        CommanderSelectionService selectionService,
        System.Func<bool> commanderActive)
    {
        this.alertService = alertService;
        this.selectionService = selectionService;
        this.commanderActive = commanderActive;
        Instance = this;
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        return alertService.Alerts.Count > 0
            && windowRect.Contains(CommanderUiScale.ScreenToGui(screenPoint));
    }

    internal void ResetPosition()
    {
        positionInitialized = false;
    }

    internal void Draw()
    {
        int count = alertService.Alerts.Count;
        if (count == 0)
        {
            return;
        }

        CommanderUiTheme.Ensure();
        float width = Mathf.Min(WindowWidth, CommanderUiScale.Width - 24f);
        float height = TitleHeight + 6f + count * (ToastHeight + 4f);
        if (!positionInitialized)
        {
            windowRect = new Rect(CommanderUiScale.Width - width - 12f, 58f, width, height);
            positionInitialized = true;
        }
        else
        {
            windowRect.width = width;
            windowRect.height = height;
        }

        windowRect = CommanderUiTheme.ClampWindow(windowRect);
        windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "ALERTS", CommanderUiTheme.Window);
    }

    private void DrawWindow(int windowId)
    {
        IReadOnlyList<CommanderAlertService.Alert> alerts = alertService.Alerts;
        bool active = commanderActive();
        float y = TitleHeight + 2f;
        for (int i = alerts.Count - 1; i >= 0; i--)
        {
            CommanderAlertService.Alert alert = alerts[i];
            Rect rect = new(6f, y, windowRect.width - 12f, ToastHeight);
            y += ToastHeight + 4f;

            Color previous = GUI.color;
            GUI.color = GetColor(alert.Kind);
            bool clicked = GUI.Button(rect, alert.Text, CommanderUiTheme.Toast);
            GUI.color = previous;
            if (!clicked)
            {
                continue;
            }

            if (active && alert.Unit != null && !alert.Unit.disabled)
            {
                selectionService.SelectUnit(alert.Unit, false);
                CommanderCameraFollowService.Instance?.FocusSelection();
            }
            pendingDismiss = alert;
            break;
        }

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width, TitleHeight));

        if (pendingDismiss != null)
        {
            alertService.DismissAlert(pendingDismiss);
            pendingDismiss = null;
        }
    }

    private static Color GetColor(CommanderAlertService.LogKind kind)
    {
        return kind switch
        {
            CommanderAlertService.LogKind.Kill => new Color(0.55f, 1f, 0.6f, 1f),
            CommanderAlertService.LogKind.Arrival => new Color(0.6f, 0.85f, 1f, 1f),
            _ => new Color(1f, 0.65f, 0.55f, 1f),
        };
    }
}
