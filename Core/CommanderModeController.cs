using UnityEngine;
using UnityEngine.SceneManagement;

namespace GroundControlRts;

/// <summary>
/// The RTS mode itself: it owns the camera/cursor takeover, the draw pass, and a
/// <see cref="CommanderServiceRegistry"/> holding everything else.
/// <para>
/// Adding a feature means writing a service, implementing the lifecycle interfaces it
/// actually needs, and adding one <c>Register</c> line in <see cref="Awake"/>. Nothing else
/// in this file should have to change. The order of those Register calls is the per-frame
/// schedule, so read <see cref="Awake"/> top to bottom to see what runs when.
/// </para>
/// </summary>
internal sealed class CommanderModeController : MonoBehaviour
{
    private readonly CommanderServiceRegistry services = new();

    // The camera and cursor takeover brackets every service, so it is driven by hand rather
    // than registered.
    private CommanderCameraController? cameraController;
    private CommanderCursorController? cursorController;

    // Registered services this file still talks to directly, because something about them is
    // conditional: physics-step camera work, the map window's open/close, the draw pass, and
    // the scene-change teardown that has to run even when the mode was never entered.
    private CommanderCameraFollowService? cameraFollowService;
    private CommanderTacticalMapService? tacticalMapService;
    private CommanderBoxSelectService? boxSelectService;
    private CommanderOverlayUi? overlayUi;

    // Draw-only and input-only surfaces: no lifecycle of their own.
    private CommanderPovCrewUi? povCrewUi;
    private CommanderAlertUi? alertUi;
    private CommanderInputController? inputController;

    private float nextInactiveEntryProbeAt;
    private bool aircraftSelectionMenuPresent;

    internal bool IsActive { get; private set; }

    private void Awake()
    {
        CommanderUiScale.ApplyResolutionPreset();
        cameraController = new CommanderCameraController();
        cursorController = new CommanderCursorController();

        CommanderSelectionService selectionService = services.Register(new CommanderSelectionService());
        CommanderGroupService groupService = services.Register(new CommanderGroupService(selectionService));
        cameraFollowService = services.Register(new CommanderCameraFollowService(selectionService));
        CommanderMarkerService markerService = services.Register(new CommanderMarkerService(selectionService));
        boxSelectService = services.Register(new CommanderBoxSelectService(selectionService, markerService));
        tacticalMapService = services.Register(new CommanderTacticalMapService(cameraFollowService));
        // Alerts run first among the persistent ticks and outside the feature gate: their whole
        // point is telling the player what happened while they were flying.
        CommanderAlertService alertService = services.Register(new CommanderAlertService());

        CommanderRadarService radarService =
            services.Register(new CommanderRadarService(selectionService), CommanderTier.Advanced);
        CommanderMobileEmplacementService mobileEmplacementService =
            services.Register(new CommanderMobileEmplacementService(selectionService), CommanderTier.Advanced);
        CommanderDirectPathService directPathService =
            services.Register(new CommanderDirectPathService(selectionService), CommanderTier.Advanced);
        CommanderSupplyHeliService supplyHeliService =
            services.Register(new CommanderSupplyHeliService(), CommanderTier.Advanced);
        CommanderAirCommandService airCommandService =
            services.Register(new CommanderAirCommandService(tacticalMapService), CommanderTier.Advanced);
        CommanderNavalPurchaseService navalPurchaseService =
            services.Register(new CommanderNavalPurchaseService(tacticalMapService), CommanderTier.Advanced);
        CommanderSamSiteAnalyzerService samSiteAnalyzerService =
            services.Register(new CommanderSamSiteAnalyzerService(), CommanderTier.Advanced);
        CommanderSamSiteService samSiteService = services.Register(
            new CommanderSamSiteService(samSiteAnalyzerService, supplyHeliService),
            CommanderTier.Advanced);
        CommanderFactionVehicleService factionVehicleService = services.Register(new CommanderFactionVehicleService());
        CommanderSpawnService spawnService = services.Register(
            new CommanderSpawnService(selectionService, factionVehicleService, tacticalMapService),
            CommanderTier.Advanced);
        services.Register(new CommanderEnemyCommanderService(), CommanderTier.Advanced);

        CommanderRepairService repairService = services.Register(new CommanderRepairService());
        // Routes tick last so orders act on this frame's spawns and kills.
        CommanderMoveService moveService = services.Register(new CommanderMoveService(selectionService));
        // Route lines live inside the map's icon layer, so they tick after the routes they draw.
        services.Register(new CommanderMapRouteRenderer(selectionService, moveService));

        // The overlay reads every service's state, so it ticks after all of them.
        overlayUi = services.Register(new CommanderOverlayUi(
            selectionService,
            moveService,
            groupService,
            spawnService,
            radarService,
            mobileEmplacementService,
            repairService,
            directPathService,
            supplyHeliService,
            airCommandService,
            navalPurchaseService,
            samSiteAnalyzerService,
            samSiteService,
            UnlockAdvancedFeatures,
            () => Deactivate()));

        povCrewUi = new CommanderPovCrewUi(cameraFollowService);
        alertUi = new CommanderAlertUi(alertService, selectionService, () => IsActive);
        inputController = new CommanderInputController(
            overlayUi,
            selectionService,
            spawnService,
            markerService,
            moveService,
            tacticalMapService,
            supplyHeliService,
            mobileEmplacementService,
            airCommandService,
            boxSelectService);
        inputController.SetPovCrewUi(povCrewUi);
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    private void Update()
    {
        CommanderUiScale.RefreshResolutionPreset();
        services.TickPersistent(CommanderFeatureGate.AdvancedFeaturesEnabled);
        if (!IsActive)
        {
            return;
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.ToggleUi))
        {
            overlayUi?.ToggleScreenshotUi();
        }

        if (IsPlayerInOperationalAircraft())
        {
            Deactivate(restorePreviousCamera: false);
            return;
        }

        cursorController?.TickActive();
        services.TickActive(CommanderFeatureGate.AdvancedFeaturesEnabled);
        inputController?.Tick();
    }

    private void FixedUpdate()
    {
        if (IsActive)
        {
            cameraFollowService?.FixedTick();
        }
    }

    private void OnGUI()
    {
        Matrix4x4 previousMatrix = CommanderUiScale.Begin();
        CommanderUiTheme.Ensure();
        // Themed scrollbars/sliders come from the skin, so install it for our draw pass only.
        GUISkin previousSkin = GUI.skin;
        GUI.skin = CommanderUiTheme.Skin;
        try
        {
            if (!IsActive)
            {
                alertUi?.Draw();
                if (ShouldShowCommanderEntry())
                {
                    overlayUi?.DrawInactiveLauncher(Activate);
                }
                return;
            }

            overlayUi?.Draw();
            if (overlayUi?.CommanderUiHidden != true)
            {
                alertUi?.Draw();
                povCrewUi?.Draw();
                boxSelectService?.Draw();
            }
            if (overlayUi?.ShowTacticalMapUi == true)
            {
                tacticalMapService?.DrawControls();
            }
        }
        finally
        {
            GUI.skin = previousSkin;
            CommanderUiScale.End(previousMatrix);
        }
    }

    private bool ShouldShowCommanderEntry()
    {
        if (IsPlayerInOperationalAircraft()
            || (GameManager.gameState != GameState.SinglePlayer && GameManager.gameState != GameState.Multiplayer))
        {
            return false;
        }

        if (DynamicMap.mapMaximized)
        {
            return true;
        }

        if (Time.unscaledTime >= nextInactiveEntryProbeAt)
        {
            nextInactiveEntryProbeAt = Time.unscaledTime + 0.75f;
            aircraftSelectionMenuPresent = UnityEngine.Object.FindObjectOfType<AircraftSelectionMenu>() != null;
        }
        return aircraftSelectionMenuPresent;
    }

    private void OnDisable()
    {
        Deactivate();
    }

    private void OnDestroy()
    {
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        Deactivate(restorePreviousCamera: false);
    }

    private void OnApplicationQuit()
    {
        Deactivate();
    }

    internal void Toggle()
    {
        if (IsActive)
        {
            Deactivate();
            return;
        }

        Activate();
    }

    private void Activate()
    {
        if (IsActive)
        {
            return;
        }

        if (IsPlayerInOperationalAircraft())
        {
            CommanderPlugin.Log.LogWarning("RTS mode is only available while the player is outside an aircraft.");
            return;
        }

        AircraftSelectionMenu? aircraftSelectionMenu = UnityEngine.Object.FindObjectOfType<AircraftSelectionMenu>();
        if (aircraftSelectionMenu != null && aircraftSelectionMenu.gameObject.activeInHierarchy)
        {
            aircraftSelectionMenu.ReturnToMap();
        }

        if (cameraController == null || !cameraController.TryActivate())
        {
            CommanderPlugin.Log.LogWarning("RTS mode could not start because the free camera is not available yet.");
            return;
        }

        CommanderFeatureGate.RefreshMission();
        cursorController?.Activate();
        IsActive = true;
        services.Activate(CommanderFeatureGate.AdvancedFeaturesEnabled);
        OpenTacticalMapIfRequested();
        CommanderPlugin.Log.LogInfo(
            $"RTS mode enabled: mission={CommanderFeatureGate.MissionName}, features={(CommanderFeatureGate.AdvancedFeaturesEnabled ? "full" : "core")}.");
    }

    private void UnlockAdvancedFeatures()
    {
        if (CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            return;
        }

        CommanderFeatureGate.UnlockAdvancedFeatures();
        if (IsActive)
        {
            services.ActivateAdvanced();
            OpenTacticalMapIfRequested();
        }
        CommanderPlugin.Log.LogWarning(
            $"Advanced RTS features manually unlocked for mission '{CommanderFeatureGate.MissionName}'.");
    }

    private void OpenTacticalMapIfRequested()
    {
        if (CommanderFeatureGate.AdvancedFeaturesEnabled && overlayUi?.ShowTacticalMapUi == true)
        {
            tacticalMapService?.Open();
        }
    }

    private void Deactivate(bool restorePreviousCamera = true)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        services.Deactivate();
        tacticalMapService?.Close();
        cursorController?.Deactivate();
        cameraController?.Deactivate(restorePreviousCamera);
        CommanderPlugin.Log.LogInfo("RTS mode disabled.");
    }

    private static bool IsPlayerInOperationalAircraft()
    {
        return GameManager.GetLocalAircraft(out Aircraft aircraft)
            && aircraft != null
            && !aircraft.disabled;
    }

    private void OnActiveSceneChanged(Scene previousScene, Scene newScene)
    {
        Deactivate(restorePreviousCamera: false);
        // Deactivate() is a no-op when the mode was never entered, so these two still have to
        // be nudged by hand: a stale camera follow or drag would otherwise survive the load.
        cameraFollowService?.Deactivate();
        boxSelectService?.Cancel();
        CommanderFeatureGate.ResetSession();
        services.ResetSession();
        aircraftSelectionMenuPresent = false;
        nextInactiveEntryProbeAt = 0f;
    }
}
