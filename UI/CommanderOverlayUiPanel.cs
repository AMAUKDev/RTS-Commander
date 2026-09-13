using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderOverlayUi
{
    private void DrawPanelWindow(int windowId)
    {
        if (CommanderUiTheme.DrawHelpButton(panelRect.width, ref panelHelpVisible))
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 34f, panelRect.width - 24f, 168f),
                "LMB selects, drag LMB for a selection box, Shift+LMB adds (and removes an already selected unit), empty LMB clears. Double-click takes every unit of that type on screen, Ctrl+LMB takes every one the faction owns. RMB sets a travel point; hold the queue key for a multi-point route; RMB on a hostile makes the last point an attack order. X stops the selection where it stands, the period key jumps to the next unit with no orders. 1-9 recall control groups, Ctrl+1-9 store them; F1-F4 recall camera views, Ctrl+F1-F4 store them. Camera and action bindings live under Settings > Controls. M opens the fullscreen map.");
        }
        if (GUI.Button(new Rect(panelRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.Button))
        {
            panelVisible = false;
        }

        float y = panelHelpVisible ? 212f : 38f;
        bool advanced = CommanderFeatureGate.AdvancedFeaturesEnabled;
        Rect unlockRect = default;
        const string unlockTooltip = "Features behind this toggle are designed for large strategic missions such as Escalation and Terminal Control. Enabling them in other missions may break the mission.";
        if (!advanced)
        {
            string mission = string.IsNullOrWhiteSpace(CommanderFeatureGate.MissionName)
                ? "UNKNOWN MISSION"
                : CommanderFeatureGate.MissionName.ToUpperInvariant();
            GUI.Label(new Rect(12f, y, panelRect.width - 24f, 20f), $"CORE MODE   |   {mission}", CommanderUiTheme.MutedLabel);
            y += 24f;
            unlockRect = new Rect(12f, y, panelRect.width - 24f, 38f);
            string unlockLabel = advancedUnlockConfirmation
                ? "ARE YOU SURE? UNLOCK ALL FEATURES"
                : "UNLOCK ALL FEATURES";
            if (GUI.Button(unlockRect, new GUIContent(unlockLabel, unlockTooltip), CommanderUiTheme.DangerButton))
            {
                if (advancedUnlockConfirmation)
                {
                    unlockAdvancedFeatures();
                    advancedUnlockConfirmation = false;
                }
                else
                {
                    advancedUnlockConfirmation = true;
                }
            }
            y += 48f;
        }

        bool oldEnabled = GUI.enabled;
        if (GUI.Button(new Rect(12f, y, panelRect.width - 24f, 34f), "ORDER OF BATTLE",
            unitListUi.Visible ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
        {
            showUnitListUi = true;
            CommanderSettings.ShowUnitListUi = true;
            unitListUi.Toggle();
        }
        y += 42f;

        if (GUI.Button(new Rect(12f, y, panelRect.width - 24f, 34f), "COMMANDER LOG",
            aiLogUi.Visible ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
        {
            aiLogUi.Toggle();
        }
        y += 42f;

        // No CAPTURE button. Expansion is an ordinary order: drop a travel point on the yellow
        // capture marker and the selection goes and takes the base. A button that did the same
        // thing to the nearest target only ever competed with the order the player was already
        // making, and it hid the fact that a route can end on a base.

        y = DrawTimeControls(y, oldEnabled);

        GUI.enabled = oldEnabled && advanced;
        GUI.Label(new Rect(12f, y, panelRect.width - 24f, 18f), "GROUND UNITS", CommanderUiTheme.MutedLabel);
        y += 20f;
        if (GUI.Button(new Rect(12f, y, panelRect.width - 24f, 34f), "SELECT NEAREST DEPOT", CommanderUiTheme.PrimaryButton))
        {
            spawnService.SelectNearestDepot();
        }
        y += 38f;
        if (GUI.Button(new Rect(12f, y, panelRect.width - 24f, 34f), "FACTION RESERVE", CommanderUiTheme.PrimaryButton))
        {
            reserveWindowVisible = !reserveWindowVisible;
        }
        y += 44f;

        GUI.Label(new Rect(12f, y, panelRect.width - 24f, 18f), "AIR UNITS", CommanderUiTheme.MutedLabel);
        y += 20f;
        if (GUI.Button(new Rect(12f, y, panelRect.width - 24f, 34f), "SUPPLY HELI", CommanderUiTheme.PrimaryButton))
        {
            supplyHeliUi.Toggle();
        }
        y += 38f;
        if (GUI.Button(new Rect(12f, y, panelRect.width - 24f, 34f), "AIR COMMAND", CommanderUiTheme.PrimaryButton))
        {
            if (airCommandUi.Visible)
            {
                airCommandUi.Hide();
            }
            else
            {
                panelVisible = false;
                reserveWindowVisible = false;
                supplyHeliUi.Hide();
                economyUi.Hide();
                depotUi.Reset();
                airCommandUi.Show();
            }
        }
        y += 44f;

        GUI.Label(new Rect(12f, y, panelRect.width - 24f, 18f), "NAVAL", CommanderUiTheme.MutedLabel);
        y += 20f;
        if (GUI.Button(new Rect(12f, y, panelRect.width - 24f, 34f), "NAVAL PURCHASE", CommanderUiTheme.PrimaryButton))
        {
            navalPurchaseUi.Toggle();
        }
        y += 44f;

        GUI.Label(new Rect(12f, y, panelRect.width - 24f, 18f), "ECONOMY", CommanderUiTheme.MutedLabel);
        y += 20f;
        if (GUI.Button(new Rect(12f, y, panelRect.width - 24f, 34f), "BUILD",
            economyUi.Visible ? CommanderUiTheme.SelectedButton : CommanderUiTheme.PrimaryButton))
        {
            showBuildUi = true;
            CommanderSettings.ShowBuildUi = true;
            economyUi.Toggle();
        }
        y += 40f;

        string helper = economyService.AwaitingPlacement
            ? economyService.PlacementStatus
            : supplyHeliService.AwaitingTargetSelection
            ? "Select the cargo destination in the 3D world. The game's Cancel binding cancels."
            : airCommandService.AwaitingAreaSelection
                ? "Select the Air Command mission area on the tactical map or in the 3D world."
                : navalPurchaseService.AwaitingRallySelection
                    ? "Select a water rally point on the fullscreen map."
                : mobileEmplacementService.AwaitingDestination
                    ? "Select the trailer destination in the 3D world. The game's Cancel binding cancels."
            : spawnService.AwaitingRallyPointSelection
                ? "Select the rally point on the tactical map or in the 3D world."
                : string.Empty;
        float settingsY = panelRect.height - 102f;
        float experimentalY = settingsY - 84f;
        if (!string.IsNullOrEmpty(helper) && experimentalY - y >= 36f)
        {
            GUI.Label(new Rect(14f, y, panelRect.width - 28f, 36f), helper, CommanderUiTheme.MutedLabel);
        }
        GUI.Label(new Rect(12f, experimentalY, panelRect.width - 24f, 18f), "EXPERIMENTAL", CommanderUiTheme.MutedLabel);
        if (GUI.Button(new Rect(12f, experimentalY + 20f, panelRect.width - 24f, 34f), "SAM SITE ANALYZER", CommanderUiTheme.Button))
        {
            samSiteAnalyzerUi.Toggle();
        }
        GUI.enabled = oldEnabled;
        if (GUI.Button(new Rect(12f, settingsY, panelRect.width - 24f, 34f), "SETTINGS", CommanderUiTheme.Button))
        {
            settingsVisible = !settingsVisible;
            bindingCapture = null;
        }

        if (GUI.Button(new Rect(12f, panelRect.height - 54f, panelRect.width - 24f, 38f), "EXIT COMMANDER MODE", CommanderUiTheme.DangerButton))
        {
            GUI.FocusControl(null);
            exitCommander();
        }

        if (!advanced && unlockRect.Contains(Event.current.mousePosition))
        {
            Rect tooltipRect = new(12f, unlockRect.yMax + 4f, panelRect.width - 24f, 64f);
            GUI.Box(tooltipRect, string.Empty, CommanderUiTheme.Panel);
            GUI.Label(
                new Rect(tooltipRect.x + 8f, tooltipRect.y + 5f, tooltipRect.width - 16f, tooltipRect.height - 10f),
                unlockTooltip,
                CommanderUiTheme.Label);
        }

        GUI.DragWindow(new Rect(0f, 0f, panelRect.width - 72f, 28f));
    }

    /// <summary>
    /// Game speed. An RTS spends a lot of its time waiting for a convoy to cross the map, so this
    /// is the button that makes the mod's own pacing bearable.
    /// </summary>
    /// <remarks>
    /// It writes <c>TimeScaleManager.Scale</c>, which is the game's own knob — the same one the
    /// slow-motion binding and the pause menu use. That means it is <b>host-only</b>: on a pure
    /// multiplayer client the clock is the server's, and speeding up the local one would only
    /// desynchronise what you are looking at. The game already treats speed as a single-player
    /// affordance, and so does this.
    /// <para>
    /// It is deliberately not a saved setting. A persisted 4× would apply itself the moment the next
    /// mission loaded, before anyone chose it.
    /// </para>
    /// </remarks>
    private float DrawTimeControls(float y, bool oldEnabled)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        bool host = hq == null || hq.IsServer;
        GUI.Label(new Rect(12f, y, panelRect.width - 24f, 18f),
            host ? "GAME SPEED" : "GAME SPEED   (HOST ONLY)", CommanderUiTheme.MutedLabel);
        y += 20f;

        float width = (panelRect.width - 24f - 8f) / 3f;
        GUI.enabled = oldEnabled && host;
        for (int i = 0; i < TimeScaleSteps.Length; i++)
        {
            float scale = TimeScaleSteps[i];
            bool current = Mathf.Approximately(Time.timeScale, scale);
            if (GUI.Button(
                new Rect(12f + i * (width + 4f), y, width, 30f),
                $"{scale:0}x",
                current ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
            {
                TimeScaleManager.Scale = scale;
            }
        }
        GUI.enabled = oldEnabled;
        return y + 38f;
    }

    private static readonly float[] TimeScaleSteps = { 1f, 2f, 4f };
}
