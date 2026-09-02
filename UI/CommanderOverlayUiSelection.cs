using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderOverlayUi
{
    private GUIStyle GetGhostCommandStyle()
    {
        if (ghostCommandStyle != null)
        {
            return ghostCommandStyle;
        }

        ghostCommandStyle = new GUIStyle(CommanderUiTheme.Button);
        ghostCommandStyle.normal.background = null;
        ghostCommandStyle.hover.background = null;
        ghostCommandStyle.active.background = null;
        Color dim = ghostCommandStyle.normal.textColor;
        dim.a = 0.5f;
        ghostCommandStyle.normal.textColor = dim;
        ghostCommandStyle.hover.textColor = dim;
        ghostCommandStyle.active.textColor = dim;
        return ghostCommandStyle;
    }

    /// <summary>
    /// One chip per unit type in the selection. Clicking a chip narrows the selection to that
    /// type; holding the add-selection key drops it, so a box that caught the wrong thing does
    /// not have to be redrawn from scratch.
    /// </summary>
    private void DrawSelectionChips()
    {
        RefreshSelectionChips();

        float x = selectionBarRect.x + 14f;
        float maxX = selectionBarRect.xMax - 14f;
        float y = selectionBarRect.y + 70f;
        GUIStyle style = CommanderUiTheme.Button;
        for (int i = 0; i < chipLabels.Count; i++)
        {
            float width = Mathf.Min(190f, style.CalcSize(new GUIContent(chipLabels[i])).x + 18f);
            if (x + width > maxX)
            {
                GUI.Label(new Rect(x, y, maxX - x, 30f), $"+{chipLabels.Count - i}", CommanderUiTheme.MutedLabel);
                return;
            }

            if (GUI.Button(new Rect(x, y, width, 30f), chipLabels[i], style))
            {
                if (CommanderSettings.AddToSelection.IsPressed())
                {
                    selectionService.RemoveType(chipTypes[i]);
                }
                else
                {
                    selectionService.KeepOnlyType(chipTypes[i]);
                }
                return;
            }
            x += width + 6f;
        }
    }

    private void RefreshSelectionChips()
    {
        // Units that die out of the selection are pruned without bumping the revision, so the
        // count is checked too or the chips would keep advertising a destroyed vehicle.
        if (chipRevision == selectionService.SelectionRevision
            && chipCount == selectionService.SelectedUnits.Count)
        {
            return;
        }

        chipRevision = selectionService.SelectionRevision;
        chipCount = selectionService.SelectedUnits.Count;
        chipTypes.Clear();
        chipCounts.Clear();
        chipLabels.Clear();
        IReadOnlyList<Unit> units = selectionService.SelectedUnits;
        for (int i = 0; i < units.Count; i++)
        {
            string typeKey = CommanderSelectionService.GetTypeKey(units[i]);
            int index = chipTypes.IndexOf(typeKey);
            if (index < 0)
            {
                chipTypes.Add(typeKey);
                chipCounts.Add(1);
            }
            else
            {
                chipCounts[index]++;
            }
        }

        for (int i = 0; i < chipTypes.Count; i++)
        {
            chipLabels.Add(chipCounts[i] > 1 ? $"{chipTypes[i]} x{chipCounts[i]}" : chipTypes[i]);
        }
    }

    private void DrawSelectionBar()
    {
        int count = selectionService.SelectedUnits.Count;
        if (count == 0)
        {
            return;
        }

        GUI.Box(selectionBarRect, string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(selectionBarRect.x + 12f, selectionBarRect.y + 3f, 150f, 24f), "UNIT SELECTION", CommanderUiTheme.MutedLabel);
        GUI.Label(
            new Rect(selectionBarRect.x + 166f, selectionBarRect.y + 3f, selectionBarRect.width - 210f, 24f),
            GetSelectionCardText(),
            CommanderUiTheme.MutedLabel);
        if (GUI.Button(new Rect(selectionBarRect.xMax - 34f, selectionBarRect.y + 3f, 26f, 22f), "?", CommanderUiTheme.HelpButton))
        {
            selectionHelpVisible = !selectionHelpVisible;
        }
        Unit? focused = selectionService.FocusedSelection;
        string label = count == 1 && focused != null
            ? CommanderGameAccess.GetUnitLabel(focused)
            : $"{count} UNITS SELECTED";
        int group = groupService.GetGroupOf(focused);
        if (group > 0)
        {
            label = $"GROUP {group}  |  {label}";
        }
        GUI.Label(new Rect(selectionBarRect.x + 14f, selectionBarRect.y + 37f, selectionBarRect.width - 408f, 24f), label, CommanderUiTheme.Header);
        if (count > 1)
        {
            DrawSelectionChips();
        }
        else
        {
            DrawLoadout();
        }

        float buttonX = selectionBarRect.xMax - 338f;
        if (selectionHelpVisible)
        {
            CommanderUiTheme.DrawHelpOverlay(selectionHelpRect,
                "STOP (X by default) cancels RTS orders and holds friendly ground/ship units where they stand. AI returns them to Basegame tasking; munitions trucks resume RearmVehicleAI logistics. ROAD toggles Basegame roads for one friendly ground vehicle. PIN stores the selection; hold Alt to expose DEL. The type chips narrow a mixed selection to one unit type, or drop that type when the add-selection key is held. PATROL turns a multi-point route into a loop. The stance button cycles Free Fire, Hold Fire (turrets acquire nothing, for staying dark) and Hold Pos. RETREAT sends the selection to the nearest repair or rearm vehicle. The formation button picks ring, line, column or wedge, and the WP button attaches an action - hold, radar off, radar on - to the next travel point you place. Selected aircraft take travel and attack points too, flown by their AI pilot through Air Command.");
        }
        bool oldEnabled = GUI.enabled;
        bool advanced = CommanderFeatureGate.AdvancedFeaturesEnabled;
        GUI.enabled = oldEnabled && moveService.HasCommandableSelection;
        if (GUI.Button(new Rect(buttonX, selectionBarRect.y + 32f, 72f, 34f), "STOP", CommanderUiTheme.DangerButton))
        {
            moveService.StopSelectedUnits();
        }
        GUI.enabled = oldEnabled && advanced && moveService.HasCommandableSelection;
        if (GUI.Button(new Rect(buttonX + 78f, selectionBarRect.y + 32f, 72f, 34f), "AI", CommanderUiTheme.PrimaryButton))
        {
            moveService.ResumeAiForSelectedUnits();
        }
        GUI.enabled = oldEnabled;
        bool canToggleRoad = advanced
            && count == 1
            && focused != null
            && directPathService.CanConfigure(focused)
            && !CommanderMobileEmplacementService.IsReservedHauler(focused)
            && !CommanderSamSiteService.IsReservedConstructionJacknife(focused);
        bool roadEnabled = !directPathService.IsEnabled(focused);
        GUI.enabled = oldEnabled && canToggleRoad;
        if (GUI.Button(new Rect(buttonX + 156f, selectionBarRect.y + 32f, 82f, 34f),
            roadEnabled ? "ROAD ON" : "ROAD OFF",
            roadEnabled ? CommanderUiTheme.Button : CommanderUiTheme.DangerButton))
        {
            directPathService.ToggleFocusedUnit();
        }
        GUI.enabled = oldEnabled;
        bool deleteMode = CommanderSettings.DeleteUnitModifier.IsPressed();
        string pinLabel = deleteMode ? "DEL" : (selectionService.IsCurrentSelectionPinned ? "UNPIN" : "PIN");
        GUI.enabled = oldEnabled
            && advanced
            && (!deleteMode || selectionService.CanDeleteSelection);
        if (GUI.Button(new Rect(buttonX + 244f, selectionBarRect.y + 32f, 82f, 34f), pinLabel,
            deleteMode ? CommanderUiTheme.DangerButton : CommanderUiTheme.Button))
        {
            if (deleteMode)
            {
                selectionService.DeleteSelectedUnits();
            }
            else
            {
                selectionService.TogglePinSelected();
            }
        }
        GUI.enabled = oldEnabled;

        DrawOrderRow(oldEnabled);
    }

    /// <summary>Patrol, stance, retreat, formation and the action attached to the next waypoint.</summary>
    private void DrawOrderRow(bool oldEnabled)
    {
        const float buttonWidth = 88f;
        float rowY = selectionBarRect.yMax - 40f;
        float x = selectionBarRect.xMax - (buttonWidth * 5f + 24f) - 14f;
        bool commandable = moveService.HasCommandableSelection;

        GUI.enabled = oldEnabled && commandable && moveService.CanSelectionPatrol;
        bool patrolling = moveService.IsSelectionPatrolling;
        if (GUI.Button(new Rect(x, rowY, buttonWidth, 34f), patrolling ? "PATROL ON" : "PATROL",
            patrolling ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            moveService.ToggleSelectionPatrol();
        }

        GUI.enabled = oldEnabled && commandable;
        CommanderStance stance = moveService.SelectionStance;
        if (GUI.Button(new Rect(x + (buttonWidth + 6f), rowY, buttonWidth, 34f), GetStanceLabel(stance),
            stance == CommanderStance.FreeEngage ? CommanderUiTheme.Button : CommanderUiTheme.DangerButton))
        {
            moveService.CycleSelectionStance();
        }

        if (GUI.Button(new Rect(x + (buttonWidth + 6f) * 2f, rowY, buttonWidth, 34f), "RETREAT", CommanderUiTheme.Button))
        {
            moveService.RetreatSelectedUnits();
        }

        GUI.enabled = oldEnabled;
        if (GUI.Button(new Rect(x + (buttonWidth + 6f) * 3f, rowY, buttonWidth, 34f),
            moveService.Formation.ToString().ToUpperInvariant(), CommanderUiTheme.Button))
        {
            moveService.CycleFormation();
        }

        CommanderWaypointAction action = moveService.PendingWaypointAction;
        if (GUI.Button(new Rect(x + (buttonWidth + 6f) * 4f, rowY, buttonWidth, 34f), GetWaypointActionLabel(action),
            action == CommanderWaypointAction.None ? CommanderUiTheme.Button : CommanderUiTheme.PrimaryButton))
        {
            moveService.CyclePendingWaypointAction();
        }
        GUI.enabled = oldEnabled;
    }

    private static string GetStanceLabel(CommanderStance stance)
    {
        return stance switch
        {
            CommanderStance.HoldFire => "HOLD FIRE",
            CommanderStance.HoldPosition => "HOLD POS",
            _ => "FREE FIRE",
        };
    }

    private static string GetWaypointActionLabel(CommanderWaypointAction action)
    {
        return action switch
        {
            CommanderWaypointAction.Hold => $"WP HOLD {CommanderSettings.WaypointHoldSeconds:0}s",
            CommanderWaypointAction.RadarOff => "WP EMCON",
            CommanderWaypointAction.RadarOn => "WP RADAR",
            _ => "WP: NONE",
        };
    }

    /// <summary>
    /// Condition, ammo and current order for the selection. Rebuilt a few times a second
    /// because OnGUI runs more than once per frame and the condition walk is not free.
    /// </summary>
    private string GetSelectionCardText()
    {
        if (!CommanderScheduler.IsDue(ref nextSelectionCardAt, 0.25f))
        {
            return selectionCardText;
        }

        int count = selectionService.SelectedUnits.Count;
        if (count == 0)
        {
            selectionCardText = string.Empty;
            return selectionCardText;
        }

        float condition = 0f;
        float ammo = 0f;
        int armedUnits = 0;
        float fuel = 0f;
        int fuelledUnits = 0;
        for (int i = 0; i < count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            condition += CommanderGameAccess.GetUnitCondition(unit);
            float unitAmmo = CommanderGameAccess.GetUnitAmmo(unit);
            if (unitAmmo >= 0f)
            {
                ammo += unitAmmo;
                armedUnits++;
            }

            float unitFuel = CommanderGameAccess.GetUnitFuel(unit);
            if (unitFuel >= 0f)
            {
                fuel += unitFuel;
                fuelledUnits++;
            }
        }

        string ammoText = armedUnits > 0 ? $"   AMMO {ammo / armedUnits * 100f:0}%" : string.Empty;
        string fuelText = fuelledUnits > 0 ? $"   FUEL {fuel / fuelledUnits * 100f:0}%" : string.Empty;
        string orderText = string.Empty;
        Unit? focused = selectionService.FocusedSelection;
        if (count == 1 && focused != null)
        {
            string order = moveService.GetOrderLabel(focused);
            orderText = order.Length > 0 ? $"   |   {order}" : string.Empty;
        }

        selectionCardText = $"COND {condition / count * 100f:0}%{ammoText}{fuelText}{orderText}";
        return selectionCardText;
    }

    /// <summary>
    /// Rebuilds the per-weapon rows for a single selected unit. Called from Tick, not from OnGUI,
    /// because the bar's height is laid out from the row count before anything is drawn.
    /// </summary>
    private void RefreshLoadout()
    {
        Unit? focused = selectionService.SelectedUnits.Count == 1 ? selectionService.FocusedSelection : null;
        if (focused == null)
        {
            loadoutUnit = null;
            loadoutRows.Clear();
            return;
        }

        // Rounds change on every shot, so this is polled rather than event driven, but the walk
        // touches every weapon on the unit - a few times a second is enough for a readout.
        bool sameUnit = ReferenceEquals(focused, loadoutUnit);
        if (sameUnit && !CommanderScheduler.IsDue(ref nextLoadoutAt, 0.25f))
        {
            return;
        }

        loadoutUnit = focused;
        loadoutRows.Clear();
        loadoutNames.Clear();
        loadoutAmmo.Clear();
        loadoutFullAmmo.Clear();
        List<WeaponStation> stations = focused.weaponStations;
        if (stations == null)
        {
            return;
        }

        for (int i = 0; i < stations.Count; i++)
        {
            WeaponStation? station = stations[i];
            WeaponInfo? info = station?.WeaponInfo;
            if (station == null || info == null || info.hideInDisplay || station.FullAmmo <= 0)
            {
                continue;
            }

            // One airframe carries the same missile on several stations; merging by name keeps
            // the readout to one row per weapon type instead of one row per pylon.
            string name = GetWeaponName(info);
            int index = loadoutNames.IndexOf(name);
            if (index < 0)
            {
                loadoutNames.Add(name);
                loadoutAmmo.Add(station.Ammo);
                loadoutFullAmmo.Add(station.FullAmmo);
            }
            else
            {
                loadoutAmmo[index] += station.Ammo;
                loadoutFullAmmo[index] += station.FullAmmo;
            }
        }

        for (int i = 0; i < loadoutNames.Count; i++)
        {
            loadoutRows.Add($"{loadoutNames[i]}   {loadoutAmmo[i]} / {loadoutFullAmmo[i]}");
        }
    }

    private static string GetWeaponName(WeaponInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.weaponName))
        {
            return info.weaponName;
        }

        return string.IsNullOrWhiteSpace(info.shortName) ? "Weapon" : info.shortName;
    }

    /// <summary>Two columns of "WEAPON  loaded / full" for the single selected unit.</summary>
    private void DrawLoadout()
    {
        if (loadoutRows.Count == 0)
        {
            return;
        }

        float columnWidth = (selectionBarRect.width - 34f) * 0.5f;
        float y = selectionBarRect.y + 68f;
        for (int i = 0; i < loadoutRows.Count; i++)
        {
            float x = selectionBarRect.x + 14f + (i % 2 == 0 ? 0f : columnWidth + 6f);
            GUI.Label(
                new Rect(x, y + i / 2 * LoadoutRowHeight, columnWidth, LoadoutRowHeight),
                loadoutRows[i],
                CommanderUiTheme.MutedLabel);
        }
    }
}
