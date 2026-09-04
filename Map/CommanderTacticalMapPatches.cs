using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// While RTS mode is active the mod owns map input: the left button pans the map and clicks
/// icons, the box-select modifier turns a left drag into a selection box, the middle button
/// also pans, and the Basegame zoom and keyboard-pan bindings keep working.
/// </summary>
[HarmonyPatch(typeof(DynamicMap), "MapControls")]
internal static class CommanderTacticalMapControlsPatch
{
    private const float MapIconPickRadiusPixels = 32f;
    private const float ClickSlopPixels = 6f;

    private static bool leftButtonHeld;
    private static Vector2 leftButtonDownPosition;
    private static bool leftButtonMoved;

    private static readonly AccessTools.FieldRef<DynamicMap, bool> FollowingCamera =
        AccessTools.FieldRefAccess<DynamicMap, bool>("followingCamera");
    private static readonly AccessTools.FieldRef<DynamicMap, Vector2> PositionOffset =
        AccessTools.FieldRefAccess<DynamicMap, Vector2>("positionOffset");
    private static readonly AccessTools.FieldRef<DynamicMap, Vector2> StationaryOffset =
        AccessTools.FieldRefAccess<DynamicMap, Vector2>("stationaryOffset");
    private static readonly AccessTools.FieldRef<DynamicMap, float> MapMoveMaxJumpSpeed =
        AccessTools.FieldRefAccess<DynamicMap, float>("mapMoveMaxJumpSpeed");

    private static bool Prefix(DynamicMap __instance)
    {
        if (CommanderPlugin.Instance?.IsCommanderModeActive != true)
        {
            return true;
        }

        if (CommanderOverlayUi.Instance?.ContainsScreenPoint(Input.mousePosition) != true
            && __instance.IsCursorInMapRectangle())
        {
            CommanderMapControls(__instance);
        }

        UpdateCameraTracking(__instance);
        return false;
    }

    private static void CommanderMapControls(DynamicMap map)
    {
        float zoomAxis = CommanderGameInput.GetAxis("Zoom View") * 0.05f;
        if (zoomAxis != 0f)
        {
            map.SetZoomLevel(Mathf.Clamp(map.mapScaleCenter.transform.localScale.x * (zoomAxis + 1f), 1f, 40f));
        }

        float zoomScale = Mathf.Max(map.mapScaleCenter.localScale.x, 0.01f);
        float keyboardHorizontal = CommanderGameInput.GetAxis("Move Map Horizontal");
        float keyboardVertical = CommanderGameInput.GetAxis("Move Map Vertical");
        if (keyboardHorizontal != 0f || keyboardVertical != 0f)
        {
            float speed = 300f * Time.unscaledDeltaTime / zoomScale;
            PositionOffset(map) += new Vector2(keyboardHorizontal * speed, keyboardVertical * speed);
        }

        // A plain left drag pans, exactly like the Basegame map. The middle button keeps
        // panning too, and a left drag with the box-select modifier belongs to the box.
        bool boxDragging = CommanderBoxSelectService.Instance?.DraggingOnMap == true;
        if (Input.GetMouseButton(2) || (Input.GetMouseButton(0) && !boxDragging))
        {
            // Mouse axes are already a per-frame delta, so no deltaTime here: multiplying by it
            // made the pan crawl, and crawl worse the higher the framerate ran.
            float dragSpeed = CommanderSettings.MapDragSensitivity / zoomScale;
            PositionOffset(map) += new Vector2(
                -Input.GetAxisRaw("Mouse X") * dragSpeed,
                -Input.GetAxisRaw("Mouse Y") * dragSpeed);
        }

        FollowingCamera(map) = PositionOffset(map) == Vector2.zero;

        if (CommanderGameInput.JumpMapDown && map.TryGetCursorCoordinates(out GlobalPosition jumpTarget))
        {
            CommanderTacticalMapService.Instance?.JumpCameraToPosition(jumpTarget);
        }

        TrackIconClick(map);
    }

    /// <summary>
    /// Icon selection resolves on release, and only when the cursor barely moved: the same
    /// button now pans the map, so clicking on press would select an icon every time the
    /// player grabbed the map to drag it.
    /// </summary>
    private static void TrackIconClick(DynamicMap map)
    {
        if (Input.GetMouseButtonDown(0))
        {
            leftButtonHeld = true;
            leftButtonMoved = false;
            leftButtonDownPosition = Input.mousePosition;
        }

        if (!leftButtonHeld)
        {
            return;
        }

        if (Vector2.Distance(leftButtonDownPosition, Input.mousePosition) > ClickSlopPixels)
        {
            leftButtonMoved = true;
        }

        if (!Input.GetMouseButtonUp(0))
        {
            return;
        }

        leftButtonHeld = false;
        if (leftButtonMoved
            || CommanderBoxSelectService.Instance?.Dragging == true
            || CommanderSpawnService.Instance?.AwaitingRallyPointSelection == true
            || CommanderNavalPurchaseService.Instance?.AwaitingRallySelection == true)
        {
            return;
        }

        ClickNearestIcon(map);
    }

    /// <summary>Basegame icon picking, reimplemented because the original lives inside MapControls.</summary>
    private static void ClickNearestIcon(DynamicMap map)
    {
        Vector3 mousePosition = Input.mousePosition;
        float bestDistance = MapIconPickRadiusPixels * MapIconPickRadiusPixels;
        MapIcon? best = null;
        System.Collections.Generic.List<MapIcon> icons = map.mapIcons;
        for (int i = 0; i < icons.Count; i++)
        {
            MapIcon icon = icons[i];
            if (icon == null
                || !icon.gameObject.activeInHierarchy
                || icon.iconImage == null
                || !icon.iconImage.raycastTarget)
            {
                continue;
            }

            if (icon is UnitMapIcon unitIcon
                && (unitIcon.unit == null
                    || SceneSingleton<TargetListSelector>.i?.CheckExclusions(unitIcon.unit) == true))
            {
                continue;
            }

            float distance = ((Vector2)icon.transform.position - (Vector2)mousePosition).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = icon;
            }
        }

        best?.ClickIcon(MapIcon.ClickSource.Controller);
    }

    private static void UpdateCameraTracking(DynamicMap map)
    {
        CameraStateManager? camera = SceneSingleton<CameraStateManager>.i;
        if (camera == null)
        {
            return;
        }

        Vector3 cameraPosition = camera.transform.position.ToGlobalPosition().AsVector3() * map.mapDisplayFactor;
        ref Vector2 positionOffset = ref PositionOffset(map);
        ref Vector2 stationaryOffset = ref StationaryOffset(map);
        if (FollowingCamera(map))
        {
            Vector2 target = new(cameraPosition.x, cameraPosition.z);
            Aircraft? aircraft = SceneSingleton<CombatHUD>.i?.aircraft;
            if (aircraft != null && !aircraft.disabled)
            {
                Vector3 forward = aircraft.transform.forward;
                stationaryOffset = target + 5000f * map.mapDisplayFactor * new Vector2(forward.x, forward.z);
            }
            else
            {
                stationaryOffset = Vector2.MoveTowards(stationaryOffset, target, MapMoveMaxJumpSpeed(map));
            }
        }

        map.mapImage.transform.localEulerAngles = Vector3.zero;
        map.mapScaleCenter.transform.localEulerAngles = Vector3.zero;
        Vector2 mapPosition = -stationaryOffset - positionOffset;
        ((RectTransform)map.mapBackground.transform).rect.ClampPos(ref mapPosition, 2f);
        positionOffset = -stationaryOffset - mapPosition;
        map.mapImage.transform.localPosition = mapPosition * map.mapImage.transform.localScale.x;
        map.viewIndicator.transform.localPosition = new Vector3(cameraPosition.x, cameraPosition.z, 0f);
        map.viewIndicator.transform.eulerAngles = new Vector3(
            0f,
            0f,
            map.mapImage.transform.eulerAngles.z - camera.transform.eulerAngles.y);
    }
}

[HarmonyPatch(typeof(DynamicMap), "JumpCameraTo")]
internal static class CommanderDisableBaseMapJumpPatch
{
    private static bool Prefix()
    {
        return CommanderPlugin.Instance?.IsCommanderModeActive != true
            || CommanderTacticalMapService.AllowCommanderMapJump;
    }
}

[HarmonyPatch(typeof(ExtraUiInput), "Update")]
internal static class CommanderKeepTacticalMapOpenPatch
{
    private static bool Prefix()
    {
        if (CommanderPlugin.Instance?.IsCommanderModeActive != true)
        {
            return true;
        }

        if (CommanderTacticalMapService.Instance?.SuppressExtraUiThisFrame == true)
        {
            return false;
        }

        if (!CommanderGameInput.MapDown)
        {
            return true;
        }

        if (CommanderAirCommandUi.Instance?.HandleMapKey() == true)
        {
            return false;
        }

        return CommanderTacticalMapService.Instance?.HandleMapKey() != true;
    }
}

[HarmonyPatch(typeof(UnitMapIcon), nameof(UnitMapIcon.UpdateIcon))]
internal static class CommanderTacticalMapIconScalePatch
{
    private static void Postfix(UnitMapIcon __instance)
    {
        if (CommanderPlugin.Instance?.IsCommanderModeActive == true
            && CommanderTacticalMapService.Instance?.IsOpen == true
            && __instance.iconImage != null)
        {
            __instance.iconImage.transform.localScale *= 1.4f;
        }
    }
}

[HarmonyPatch(typeof(AirbaseMapIcon), nameof(AirbaseMapIcon.ClickIcon))]
internal static class CommanderAirbaseMapClickPatch
{
    private static bool Prefix(AirbaseMapIcon __instance)
    {
        if (CommanderPlugin.Instance?.IsCommanderModeActive != true)
        {
            return true;
        }

        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand?.IsUiVisible == true)
        {
            airCommand.TrySelectAirbaseFromMap(__instance.airbase);
            return false;
        }

        // The compact Tactical Map is for command interaction and should never open
        // the Basegame aircraft-selection panel.
        return CommanderTacticalMapService.Instance?.IsOpen != true;
    }
}

[HarmonyPatch(typeof(AirbaseMapIcon), nameof(AirbaseMapIcon.UpdateIcon))]
internal static class CommanderAirCommandAirbaseIconPatch
{
    private static void Postfix(AirbaseMapIcon __instance)
    {
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (CommanderPlugin.Instance?.IsCommanderModeActive != true
            || airCommand?.IsUiVisible != true)
        {
            return;
        }

        bool selectable = airCommand.IsSelectableAirbase(__instance.airbase);
        __instance.gameObject.SetActive(DynamicMap.mapMaximized && selectable);
        if (selectable && __instance.iconImage != null && airCommand.IsSelectedAirbase(__instance.airbase))
        {
            __instance.iconImage.color = GameAssets.i.HUDFriendlySelected;
        }
    }
}
