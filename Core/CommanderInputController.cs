using NuclearOption.MissionEditorScripts;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderInputController
{
    private const float MapIconPickRadiusPixels = 24f;
    private const float DoubleClickSeconds = 0.35f;

    private readonly CommanderOverlayUi overlayUi;
    private readonly CommanderSelectionService selectionService;
    private readonly CommanderSpawnService spawnService;
    private readonly CommanderMarkerService markerService;
    private readonly CommanderMoveService moveService;
    private readonly CommanderTacticalMapService tacticalMapService;
    private readonly CommanderSupplyHeliService supplyHeliService;
    private readonly CommanderMobileEmplacementService mobileEmplacementService;
    private readonly CommanderAirCommandService airCommandService;
    private readonly CommanderEconomyService economyService;
    private readonly CommanderBoxSelectService boxSelectService;
    private CommanderPovCrewUi? povCrewUi;
    private Unit? lastClickedUnit;
    private float lastClickAt;

    internal CommanderInputController(
        CommanderOverlayUi overlayUi,
        CommanderSelectionService selectionService,
        CommanderSpawnService spawnService,
        CommanderMarkerService markerService,
        CommanderMoveService moveService,
        CommanderTacticalMapService tacticalMapService,
        CommanderSupplyHeliService supplyHeliService,
        CommanderMobileEmplacementService mobileEmplacementService,
        CommanderAirCommandService airCommandService,
        CommanderEconomyService economyService,
        CommanderBoxSelectService boxSelectService)
    {
        this.overlayUi = overlayUi;
        this.selectionService = selectionService;
        this.spawnService = spawnService;
        this.markerService = markerService;
        this.moveService = moveService;
        this.tacticalMapService = tacticalMapService;
        this.supplyHeliService = supplyHeliService;
        this.mobileEmplacementService = mobileEmplacementService;
        this.airCommandService = airCommandService;
        this.economyService = economyService;
        this.boxSelectService = boxSelectService;
    }

    internal void SetPovCrewUi(CommanderPovCrewUi ui)
    {
        povCrewUi = ui;
    }

    internal void Tick()
    {
        // Keyboard orders run before the cursor tests: a hotkey must not depend on whether the
        // mouse happens to be resting over a Commander window.
        TickKeyboardOrders();

        if (CommanderNavalPurchaseService.Instance?.AwaitingRallySelection == true
            || spawnService.IsMapInteractionActive())
        {
            boxSelectService.Cancel();
            return;
        }

        Vector2 mousePosition = Input.mousePosition;
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        bool overMap = dynamicMap != null
            && DynamicMap.mapMaximized
            && dynamicMap.IsCursorInMapRectangle();

        // RTS windows always win, even when they sit on top of the fullscreen map: without
        // this, clicking a row in Order of Battle also panned the map underneath it.
        if (overlayUi.ContainsScreenPoint(mousePosition)
            || (!overMap
                && (povCrewUi?.ContainsScreenPoint(mousePosition) == true
                    || tacticalMapService.ContainsScreenPoint(mousePosition))))
        {
            boxSelectService.Cancel();
            return;
        }

        // Placement modes own the click outright; no selection or ordering while one is armed.
        if (TryHandlePlacementClick(mousePosition, overMap))
        {
            boxSelectService.Cancel();
            return;
        }

        bool boxSelected = boxSelectService.Tick(overMap);

        if (CommanderShortcutInput.IsDown(CommanderSettings.SecondaryAction))
        {
            if (overMap)
            {
                HandleMapOrder(dynamicMap!);
            }
            else
            {
                moveService.TryIssueMoveOrder(mousePosition);
            }
        }

        // Selection resolves on release so a drag can become a box instead of a click.
        // Map clicks stay with the Basegame icon picker.
        if (!boxSelected && !overMap && CommanderShortcutInput.IsUp(CommanderSettings.PrimaryAction))
        {
            HandlePrimaryClick(mousePosition);
        }
    }

    private void TickKeyboardOrders()
    {
        if (InputFieldChecker.InsideInputField)
        {
            return;
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.StopOrder))
        {
            moveService.StopSelectedUnits();
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.CycleIdleUnit)
            && selectionService.SelectNextIdleUnit())
        {
            CommanderCameraFollowService.Instance?.CenterOnSelection();
        }
    }

    private bool TryHandlePlacementClick(Vector2 mousePosition, bool overMap)
    {
        if (!supplyHeliService.AwaitingTargetSelection
            && !airCommandService.AwaitingAreaSelection
            && !mobileEmplacementService.AwaitingDestination
            && !economyService.AwaitingPlacement
            && !spawnService.AwaitingRallyPointSelection)
        {
            return false;
        }

        // Right-click backs out of an armed placement. Without it the only way out was finding the
        // CANCEL button again, which is the wrong instinct in an RTS.
        if (CommanderShortcutInput.IsDown(CommanderSettings.SecondaryAction))
        {
            supplyHeliService.CancelTargetSelection();
            airCommandService.CancelAreaSelection();
            mobileEmplacementService.CancelDestinationSelection();
            economyService.CancelBuild();
            spawnService.CancelRallyPointSelection();
            return true;
        }

        // Over the map, the click belongs to whichever service is watching the map — an air
        // mission area, a naval rally point — and never to the world raycast below, which would
        // read the terrain hidden behind the map canvas. Consumed either way, so nothing under the
        // map gets selected or ordered.
        if (overMap
            || !CommanderShortcutInput.IsDown(CommanderSettings.PrimaryAction)
            || overlayUi.ContainsScreenPoint(mousePosition))
        {
            return true;
        }

        if (supplyHeliService.AwaitingTargetSelection)
        {
            supplyHeliService.TrySpawnAtWorldPoint(mousePosition);
        }
        else if (airCommandService.AwaitingAreaSelection)
        {
            airCommandService.TrySetAreaFromWorld(mousePosition);
        }
        else if (mobileEmplacementService.AwaitingDestination)
        {
            mobileEmplacementService.TrySetDestinationFromWorld(mousePosition);
        }
        else if (economyService.AwaitingPlacement)
        {
            economyService.TryPlaceBuildingFromWorld(mousePosition);
        }
        else
        {
            spawnService.TrySetRallyPointFromWorld(mousePosition);
        }
        return true;
    }

    private void HandlePrimaryClick(Vector2 mousePosition)
    {
        bool additive = CommanderSettings.AddToSelection.IsPressed();

        Unit? clicked = null;
        if (markerService.TryGetMarkerUnitAt(mousePosition, out Unit markerUnit))
        {
            clicked = markerUnit;
        }
        else if (CommanderGameAccess.TryRaycastSelectableUnit(mousePosition, out Unit worldUnit))
        {
            clicked = worldUnit;
        }

        if (clicked == null)
        {
            lastClickedUnit = null;
            if (!additive)
            {
                selectionService.DeselectAll();
            }
            return;
        }

        bool doubleClick = ReferenceEquals(clicked, lastClickedUnit)
            && Time.unscaledTime - lastClickAt <= DoubleClickSeconds;
        lastClickedUnit = clicked;
        lastClickAt = Time.unscaledTime;

        bool sameTypeModifier = CommanderSettings.SelectSameType.IsPressed();
        if (sameTypeModifier || doubleClick)
        {
            // Double click takes the units you can actually see; the modifier takes every one
            // of that type the faction owns, wherever it is.
            selectionService.SelectAllOfType(clicked, additive, onScreenOnly: !sameTypeModifier);
            return;
        }

        if (additive && selectionService.IsSelected(clicked))
        {
            selectionService.DeselectUnit(clicked);
            return;
        }

        selectionService.SelectUnit(clicked, additive);
    }

    /// <summary>
    /// Right click on the tactical or fullscreen map. A hostile icon becomes an attack order,
    /// a friendly one a guard order, and anything else a travel point.
    /// </summary>
    private void HandleMapOrder(DynamicMap dynamicMap)
    {
        if (!dynamicMap.TryGetCursorCoordinates(out GlobalPosition position))
        {
            return;
        }

        Unit? attackTarget = null;
        if (TryGetMapUnitUnderCursor(dynamicMap, out Unit hoveredUnit))
        {
            if (CommanderGameAccess.IsFriendlyUnit(hoveredUnit, CommanderGameAccess.GetLocalHq()))
            {
                if (CommanderSettings.GuardOrders && moveService.IssueGuardOrder(hoveredUnit))
                {
                    return;
                }
            }
            else
            {
                attackTarget = hoveredUnit;
            }
        }

        moveService.IssueOrderAt(position, attackTarget, CommanderSettings.QueueWaypoint.IsPressed());
    }

    private static bool TryGetMapUnitUnderCursor(DynamicMap dynamicMap, out Unit unit)
    {
        unit = null!;
        Vector2 mousePosition = Input.mousePosition;
        float bestDistance = MapIconPickRadiusPixels * MapIconPickRadiusPixels;
        System.Collections.Generic.List<MapIcon> icons = dynamicMap.mapIcons;
        for (int i = 0; i < icons.Count; i++)
        {
            if (icons[i] is not UnitMapIcon unitIcon
                || unitIcon == null
                || !unitIcon.gameObject.activeInHierarchy
                || unitIcon.unit == null
                || unitIcon.unit.disabled)
            {
                continue;
            }

            float distance = ((Vector2)unitIcon.transform.position - mousePosition).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                unit = unitIcon.unit;
            }
        }

        return unit != null;
    }
}
