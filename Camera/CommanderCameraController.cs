namespace GroundControlRts;

internal sealed class CommanderCameraController
{
    private CameraBaseState? previousState;
    private CameraStateManager? activeManager;
    private bool active;

    internal static bool CustomInputActive { get; private set; }

    internal bool TryActivate()
    {
        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (cameraManager == null || cameraManager.freeState == null)
        {
            return false;
        }

        activeManager = cameraManager;
        previousState = cameraManager.currentState;
        if (cameraManager.currentState != cameraManager.freeState)
        {
            cameraManager.SwitchState(cameraManager.freeState);
        }

        active = true;
        CommanderFreeCameraInputPatch.ResetCustomMotion();
        CustomInputActive = true;
        return true;
    }

    internal void Deactivate(bool restorePreviousState = true)
    {
        CustomInputActive = false;
        CommanderFreeCameraInputPatch.ResetCustomMotion();
        if (activeManager != null)
        {
            activeManager.cameraVelocity = UnityEngine.Vector3.zero;
        }
        if (!active)
        {
            return;
        }

        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (restorePreviousState
            && ReferenceEquals(cameraManager, activeManager)
            && cameraManager != null
            && previousState != null
            && cameraManager.currentState != previousState)
        {
            cameraManager.SwitchState(previousState);
        }

        activeManager = null;
        previousState = null;
        active = false;
    }
}
