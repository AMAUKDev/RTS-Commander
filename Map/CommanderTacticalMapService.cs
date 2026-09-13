using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace GroundControlRts;

internal sealed class CommanderTacticalMapService : ICommanderTickActive, ICommanderResetSession
{
    private const float MinimumMapSize = 260f;
    private const float TacticalMapMargin = 12f;
    private const float ResizeGripSize = 18f;
    private const float HeaderHeight = 30f;

    private static readonly MethodInfo? JumpCameraToMethod = AccessTools.Method(typeof(DynamicMap), "JumpCameraTo");

    private readonly CommanderCameraFollowService cameraFollowService;
    private readonly CommanderMapClickTracker cameraJumpTracker = new();
    private readonly Vector3[] mapCorners = new Vector3[4];
    private DynamicMap? activeMap;
    private bool tacticalOpen;
    private bool positionInitialized;
    private bool dragging;
    private bool resizing;
    private bool helpVisible;
    private float mapSize = Mathf.Clamp(CommanderSettings.TacticalMapSize, MinimumMapSize, 2000f);
    private Vector2 dragOffset;
    private Rect mapWindowRect;
    private GameObject? hiddenVirtualMfd;
    private bool virtualMfdWasActive;
    private bool restoreTacticalAfterFullscreen;
    private int suppressExtraUiFrame = -1;
    private float lastAppliedUiScale = -1f;
    private Vector2 lastAppliedMapPosition = new(float.NaN, float.NaN);
    private RawImage? coverageMapLayer;
    private Image? coverageOriginMarker;
    private GameObject? coverageLayerObject;

    internal static CommanderTacticalMapService? Instance { get; private set; }
    internal static bool AllowCommanderMapJump { get; private set; }
    internal bool IsOpen => tacticalOpen && DynamicMap.mapMaximized;
    internal bool IsFullscreenOpen => !tacticalOpen && DynamicMap.mapMaximized;
    private bool suppressMapFollow;

    /// <summary>
    /// True while a placement (air mission area, rally point) owns the map. Turning it off also
    /// swallows map clicks for the next couple of frames: the placement completes on the same
    /// mouse-up every other map handler is about to see, and with the flag already cleared that
    /// click went on to select the unit under the cursor and jump the camera to it.
    /// </summary>
    internal bool SuppressMapFollow
    {
        get => suppressMapFollow;
        set
        {
            if (suppressMapFollow && !value)
            {
                CommanderTacticalMapControlsPatch.SwallowMapClicks();
            }

            suppressMapFollow = value;
        }
    }
    internal bool SuppressExtraUiThisFrame => suppressExtraUiFrame == Time.frameCount;

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screenPoint);
        if (IsOpen)
        {
            return mapWindowRect.Contains(guiPoint);
        }

        return !DynamicMap.mapMaximized
            && new Rect(CommanderUiScale.Width - 86f, TacticalMapMargin, 74f, 38f).Contains(guiPoint);
    }

    internal CommanderTacticalMapService(CommanderCameraFollowService cameraFollowService)
    {
        this.cameraFollowService = cameraFollowService;
        Instance = this;
    }

    internal void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    internal bool HandleMapKey()
    {
        if (IsOpen)
        {
            bool opened = OpenFullscreen();
            restoreTacticalAfterFullscreen = opened;
            return opened;
        }

        if (restoreTacticalAfterFullscreen && IsFullscreenOpen)
        {
            RestoreTacticalMap();
            return true;
        }

        return false;
    }

    internal bool Open()
    {
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null)
        {
            return false;
        }

        DynamicMap.AllowedToOpen = true;
        if (!DynamicMap.mapMaximized)
        {
            dynamicMap.Maximize();
        }

        activeMap = dynamicMap;
        tacticalOpen = true;
        helpVisible = false;
        cameraJumpTracker.Reset();
        EnsureInitialPosition();
        ApplyLayout();
        HideFullMapPanels();
        SyncCoverageLayer();
        return true;
    }

    /// <summary>
    /// Opens the mod's own map for a placement click — an air mission area, a naval rally point —
    /// and answers whether this call is the one that opened it, so the caller knows whether to put
    /// it away again. The compact RTS map is the only map the mod ever asks the player to work on:
    /// swapping to the fullscreen game map mid-order and back was the whole complaint.
    /// </summary>
    internal bool OpenForPlacement()
    {
        return !IsOpen && Open();
    }

    internal bool OpenFullscreen()
    {
        if (IsOpen)
        {
            Close();
        }

        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null)
        {
            return false;
        }

        RestoreVirtualMfd();
        RestoreScale(dynamicMap);
        DynamicMap.AllowedToOpen = true;
        if (!DynamicMap.mapMaximized)
        {
            dynamicMap.Maximize();
        }

        activeMap = dynamicMap;
        tacticalOpen = false;
        return DynamicMap.mapMaximized;
    }

    internal bool ShowCoverageFullscreen()
    {
        if (!OpenFullscreen())
        {
            return false;
        }

        SyncCoverageLayer();
        return true;
    }

    internal void Close()
    {
        DynamicMap? dynamicMap = activeMap ?? SceneSingleton<DynamicMap>.i;
        RestoreScale(dynamicMap);
        RestoreVirtualMfd();
        if (dynamicMap != null && DynamicMap.mapMaximized)
        {
            dynamicMap.Minimize();
        }

        tacticalOpen = false;
        dragging = false;
        resizing = false;
        helpVisible = false;
        SuppressMapFollow = false;
        activeMap = null;
        cameraJumpTracker.Reset();
        restoreTacticalAfterFullscreen = false;
        HideCoverageLayer();
    }

    internal void CloseFullscreen()
    {
        DynamicMap? dynamicMap = activeMap ?? SceneSingleton<DynamicMap>.i;
        RestoreScale(dynamicMap);
        RestoreVirtualMfd();
        if (dynamicMap != null && DynamicMap.mapMaximized)
        {
            dynamicMap.Minimize();
        }

        tacticalOpen = false;
        SuppressMapFollow = false;
        activeMap = null;
        HideCoverageLayer();
    }

    public void TickActive()
    {
        if (restoreTacticalAfterFullscreen && IsFullscreenOpen && CommanderGameInput.CancelDown)
        {
            suppressExtraUiFrame = Time.frameCount;
            RestoreTacticalMap();
            return;
        }

        if (restoreTacticalAfterFullscreen && !DynamicMap.mapMaximized)
        {
            restoreTacticalAfterFullscreen = false;
        }

        if (!tacticalOpen)
        {
            return;
        }

        if (!DynamicMap.mapMaximized || activeMap == null)
        {
            tacticalOpen = false;
            dragging = false;
            SuppressMapFollow = false;
            activeMap = null;
            cameraJumpTracker.Reset();
            return;
        }

        mapSize = Mathf.Clamp(mapSize, MinimumMapSize, MaximumMapSize);
        mapWindowRect.width = mapSize;
        mapWindowRect.height = mapSize + HeaderHeight;
        mapWindowRect = CommanderUiTheme.ClampWindow(mapWindowRect, TacticalMapMargin);
        if (!Mathf.Approximately(lastAppliedUiScale, CommanderUiScale.Scale)
            || lastAppliedMapPosition != mapWindowRect.position)
        {
            ApplyLayout();
        }
        HideFullMapPanels();
        SyncCoverageLayer();

        if (!SuppressMapFollow
            && !CommanderTacticalMapControlsPatch.AnyPlacementArmed()
            && cameraJumpTracker.Tick(activeMap, out GlobalPosition position))
        {
            JumpCameraTo(position);
        }
    }

    internal void DrawControls()
    {
        CommanderUiTheme.Ensure();
        if (!IsOpen)
        {
            if (!DynamicMap.mapMaximized)
            {
                Rect launcher = new(CommanderUiScale.Width - 86f, TacticalMapMargin, 74f, 38f);
                if (GUI.Button(launcher, "MAP  <", CommanderUiTheme.PrimaryButton))
                {
                    Open();
                }
            }
            return;
        }

        Rect header = new(mapWindowRect.x, mapWindowRect.y, mapWindowRect.width, HeaderHeight);
        GUI.Box(header, string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(header.x + 10f, header.y + 3f, 42f, 24f), "MAP", CommanderUiTheme.Header);
        bool oldEnabled = GUI.enabled;
        Rect cameraGroup = new(header.xMax - 452f, header.y + 2f, 384f, 26f);
        CommanderUiTheme.DrawFrame(cameraGroup, 1f);
        GUI.Label(new Rect(cameraGroup.x + 4f, cameraGroup.y + 1f, 38f, 24f), "CAM", CommanderUiTheme.MutedLabel);
        GUI.enabled = oldEnabled && cameraFollowService.CanFollow;
        if (GUI.Button(new Rect(cameraGroup.x + 42f, cameraGroup.y + 2f, 104f, 22f),
            cameraFollowService.Enabled ? "FOLLOW POS" : "FOLLOW",
            cameraFollowService.Enabled ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            cameraFollowService.Toggle();
        }
        if (GUI.Button(new Rect(cameraGroup.x + 150f, cameraGroup.y + 2f, 104f, 22f), "CENTER", CommanderUiTheme.Button))
        {
            cameraFollowService.CenterOnSelection();
        }
        if (GUI.Button(new Rect(cameraGroup.x + 258f, cameraGroup.y + 2f, 104f, 22f),
            cameraFollowService.PovMode ? "POV ON" : "POV",
            cameraFollowService.PovMode ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            cameraFollowService.TogglePov();
        }
        GUI.enabled = oldEnabled;
        if (GUI.Button(new Rect(header.xMax - 60f, header.y + 3f, 26f, 24f), "?", CommanderUiTheme.HelpButton))
        {
            helpVisible = !helpVisible;
        }
        if (GUI.Button(new Rect(header.xMax - 30f, header.y + 3f, 26f, 24f), "X", CommanderUiTheme.DangerButton))
        {
            Close();
            return;
        }

        Rect mapRect = GetMapGuiRect();
        CommanderUiTheme.DrawFrame(new Rect(mapRect.x - 2f, mapRect.y - 2f, mapRect.width + 4f, mapRect.height + 4f));
        if (helpVisible)
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(mapRect.x + 10f, mapRect.y + 10f, mapRect.width - 20f, 82f),
                "LMB clicks icons; drag LMB to pan the map, or hold the map box-select key and drag for a selection box. RMB sets a travel point, and RMB on a hostile icon makes it an attack order. Hold the queue key to chain points into a route the unit drives in order. Drag the bottom-right corner to resize this map. FOLLOW tracks position, CENTER jumps once, POV attaches to the unit. M opens the fullscreen map.");
        }

        HandleDrag(new Rect(header.x, header.y, header.width - 66f, header.height));
        HandleResize(new Rect(mapRect.xMax - ResizeGripSize, mapRect.yMax - ResizeGripSize, ResizeGripSize, ResizeGripSize));
    }

    public void ResetSession()
    {
        RestoreScale(activeMap);
        RestoreVirtualMfd();
        tacticalOpen = false;
        dragging = false;
        helpVisible = false;
        SuppressMapFollow = false;
        activeMap = null;
        cameraJumpTracker.Reset();
        restoreTacticalAfterFullscreen = false;
        suppressExtraUiFrame = -1;
        lastAppliedUiScale = -1f;
        lastAppliedMapPosition = new Vector2(float.NaN, float.NaN);
        DestroyCoverageLayer();
    }

    internal void ResetLayoutPosition()
    {
        positionInitialized = false;
        if (IsOpen)
        {
            EnsureInitialPosition();
            ApplyLayout();
        }
    }

    internal bool JumpCameraToPosition(GlobalPosition position)
    {
        DynamicMap? dynamicMap = activeMap ?? SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null || JumpCameraToMethod == null)
        {
            return false;
        }

        AllowCommanderMapJump = true;
        try
        {
            JumpCameraToMethod.Invoke(dynamicMap, new object[] { position });
            return true;
        }
        finally
        {
            AllowCommanderMapJump = false;
        }
    }

    private void RestoreTacticalMap()
    {
        restoreTacticalAfterFullscreen = false;
        CloseFullscreen();
        Open();
    }

    private void EnsureInitialPosition()
    {
        if (positionInitialized)
        {
            return;
        }

        mapSize = Mathf.Clamp(mapSize, MinimumMapSize, MaximumMapSize);
        mapWindowRect = new Rect(
            CommanderUiScale.Width - TacticalMapMargin - mapSize,
            TacticalMapMargin,
            mapSize,
            mapSize + HeaderHeight);
        positionInitialized = true;
    }

    private static float MaximumMapSize => Mathf.Max(
        MinimumMapSize,
        Mathf.Min(CommanderUiScale.Width - TacticalMapMargin * 2f, CommanderUiScale.Height - TacticalMapMargin * 2f - HeaderHeight));

    private Rect GetMapGuiRect()
    {
        return new Rect(mapWindowRect.x, mapWindowRect.y + HeaderHeight, mapSize, mapSize);
    }

    /// <summary>Projects a world coordinate onto the map canvas in screen pixels.</summary>
    internal bool TryWorldToMapScreen(GlobalPosition position, out Vector2 screenPosition)
    {
        screenPosition = default;
        DynamicMap? map = activeMap ?? SceneSingleton<DynamicMap>.i;
        if (map == null || !DynamicMap.mapMaximized || map.mapImage == null || map.mapDimension <= 0f)
        {
            return false;
        }

        // Inverse of DynamicMap.GetCursorCoordinates.
        float pixelsPerMeter = 900f * map.mapImage.transform.lossyScale.x / map.mapDimension;
        Vector3 origin = map.mapImage.transform.position;
        Vector3 offset = position.AsVector3();
        screenPosition = new Vector2(
            origin.x + offset.x * pixelsPerMeter,
            origin.y + offset.z * pixelsPerMeter);
        return RectTransformUtility.RectangleContainsScreenPoint(map.mapBackground.rectTransform, screenPosition, null);
    }

    private void SyncCoverageLayer()
    {
        CommanderSamSiteAnalyzerService? analyzer = CommanderSamSiteAnalyzerService.Instance;
        Texture2D? texture = analyzer?.CoverageOverlayTexture;
        if (activeMap == null
            || analyzer?.CoverageOverlayEnabled != true
            || texture == null)
        {
            HideCoverageLayer();
            return;
        }

        EnsureCoverageLayer(activeMap);
        if (coverageMapLayer == null || coverageOriginMarker == null || coverageLayerObject == null)
        {
            return;
        }

        bool textureChanged = coverageMapLayer.texture != texture;
        if (textureChanged)
        {
            coverageMapLayer.texture = texture;
        }
        GlobalPosition origin = analyzer.CoverageOverlayOrigin;
        Vector2 mapSize = NetworkSceneSingleton<LevelInfo>.i?.LoadedMapSettings?.MapSize
            ?? new Vector2(81920f, 81920f);
        Vector2 anchor = new(
            Mathf.Clamp01(origin.x / Mathf.Max(mapSize.x, 1f) + 0.5f),
            Mathf.Clamp01(origin.z / Mathf.Max(mapSize.y, 1f) + 0.5f));
        RectTransform markerTransform = coverageOriginMarker.rectTransform;
        markerTransform.anchorMin = anchor;
        markerTransform.anchorMax = anchor;
        markerTransform.anchoredPosition = Vector2.zero;
        bool wasHidden = !coverageLayerObject.activeSelf;
        coverageLayerObject.SetActive(true);
        if (textureChanged || wasHidden)
        {
            coverageMapLayer.SetAllDirty();
        }
    }

    private void EnsureCoverageLayer(DynamicMap dynamicMap)
    {
        Transform parent = dynamicMap.mapImage.transform;
        if (coverageLayerObject != null && coverageLayerObject.transform.parent == parent)
        {
            return;
        }

        DestroyCoverageLayer();
        coverageLayerObject = new GameObject(
            "GroundControl SAM Coverage",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(RawImage));
        RectTransform layerTransform = coverageLayerObject.GetComponent<RectTransform>();
        layerTransform.SetParent(parent, false);
        layerTransform.anchorMin = Vector2.zero;
        layerTransform.anchorMax = Vector2.one;
        layerTransform.offsetMin = Vector2.zero;
        layerTransform.offsetMax = Vector2.zero;
        layerTransform.localRotation = Quaternion.identity;
        layerTransform.localScale = Vector3.one;
        coverageMapLayer = coverageLayerObject.GetComponent<RawImage>();
        coverageMapLayer.raycastTarget = false;
        coverageMapLayer.color = Color.white;

        GameObject markerObject = new(
            "GroundControl SAM Coverage Origin",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        RectTransform markerTransform = markerObject.GetComponent<RectTransform>();
        markerTransform.SetParent(layerTransform, false);
        markerTransform.sizeDelta = new Vector2(6f, 6f);
        coverageOriginMarker = markerObject.GetComponent<Image>();
        coverageOriginMarker.raycastTarget = false;
        coverageOriginMarker.color = new Color(0.15f, 0.9f, 1f, 0.95f);
    }

    private void HideCoverageLayer()
    {
        if (coverageLayerObject != null)
        {
            coverageLayerObject.SetActive(false);
        }
    }

    private void DestroyCoverageLayer()
    {
        if (coverageLayerObject != null)
        {
            UnityEngine.Object.Destroy(coverageLayerObject);
        }
        coverageLayerObject = null;
        coverageMapLayer = null;
        coverageOriginMarker = null;
    }

    private void ApplyLayout()
    {
        if (activeMap == null)
        {
            return;
        }

        Rect mapRect = GetMapGuiRect();
        RectTransform rectTransform = activeMap.GetComponent<RectTransform>();
        rectTransform.localScale = Vector3.one;
        RectTransform measuredTransform = activeMap.mapBackground != null
            ? activeMap.mapBackground.rectTransform
            : rectTransform;
        measuredTransform.GetWorldCorners(mapCorners);
        Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(null, mapCorners[0]);
        Vector2 bottomRight = RectTransformUtility.WorldToScreenPoint(null, mapCorners[3]);
        float sourcePixelSize = Mathf.Max(Vector2.Distance(bottomLeft, bottomRight), 1f);
        float scale = mapSize * CommanderUiScale.Scale / sourcePixelSize;
        rectTransform.localScale = Vector3.one * scale;
        Vector2 screenCenter = CommanderUiScale.GuiToScreen(mapRect.center);
        rectTransform.position = new Vector3(
            screenCenter.x,
            screenCenter.y,
            rectTransform.position.z);
        lastAppliedUiScale = CommanderUiScale.Scale;
        lastAppliedMapPosition = mapWindowRect.position;
    }

    private void HandleDrag(Rect dragRect)
    {
        Event current = Event.current;
        if (current.type == EventType.MouseDown && current.button == 0 && dragRect.Contains(current.mousePosition))
        {
            dragging = true;
            dragOffset = current.mousePosition - mapWindowRect.position;
            current.Use();
        }
        else if (current.type == EventType.MouseDrag && dragging)
        {
            mapWindowRect.position = current.mousePosition - dragOffset;
            mapWindowRect = CommanderUiTheme.ClampWindow(mapWindowRect, TacticalMapMargin);
            ApplyLayout();
            current.Use();
        }
        else if (current.type == EventType.MouseUp && current.button == 0)
        {
            dragging = false;
        }
    }

    /// <summary>Corner grip that scales the compact tactical map.</summary>
    private void HandleResize(Rect gripRect)
    {
        GUI.Box(gripRect, string.Empty, CommanderUiTheme.Panel);
        CommanderUiTheme.DrawFrame(gripRect, 1f);

        Event current = Event.current;
        if (current.type == EventType.MouseDown && current.button == 0 && gripRect.Contains(current.mousePosition))
        {
            resizing = true;
            current.Use();
        }
        else if (current.type == EventType.MouseDrag && resizing)
        {
            float wanted = Mathf.Max(
                current.mousePosition.x - mapWindowRect.x,
                current.mousePosition.y - mapWindowRect.y - HeaderHeight);
            mapSize = Mathf.Clamp(wanted, MinimumMapSize, MaximumMapSize);
            mapWindowRect.width = mapSize;
            mapWindowRect.height = mapSize + HeaderHeight;
            mapWindowRect = CommanderUiTheme.ClampWindow(mapWindowRect, TacticalMapMargin);
            ApplyLayout();
            current.Use();
        }
        else if (current.type == EventType.MouseUp && resizing)
        {
            resizing = false;
            CommanderSettings.TacticalMapSize = mapSize;
        }
    }

    private void JumpCameraTo(GlobalPosition position)
    {
        JumpCameraToPosition(position);
    }

    private static void RestoreScale(DynamicMap? dynamicMap)
    {
        if (dynamicMap != null)
        {
            dynamicMap.GetComponent<RectTransform>().localScale = Vector3.one;
        }
    }

    private static void HideFullMapPanels()
    {
        GameplayUI? gameplayUi = SceneSingleton<GameplayUI>.i;
        gameplayUi?.HideSelectAirbase();
        gameplayUi?.HideSpectatorPanel();
        Instance?.HideVirtualMfd();
    }

    private void HideVirtualMfd()
    {
        GameObject? virtualMfd = SceneSingleton<MapOptions>.i?.screen?.virtualMFD?.gameObject;
        if (virtualMfd == null)
        {
            return;
        }

        if (hiddenVirtualMfd != virtualMfd)
        {
            RestoreVirtualMfd();
            hiddenVirtualMfd = virtualMfd;
            virtualMfdWasActive = virtualMfd.activeSelf;
        }
        virtualMfd.SetActive(false);
    }

    private void RestoreVirtualMfd()
    {
        if (hiddenVirtualMfd != null)
        {
            hiddenVirtualMfd.SetActive(virtualMfdWasActive);
        }
        hiddenVirtualMfd = null;
        virtualMfdWasActive = false;
    }
}
