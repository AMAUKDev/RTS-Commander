using HarmonyLib;
using NuclearOption.MissionEditorScripts;
using System.Reflection;
using UnityEngine;

namespace GroundControlRts;

[HarmonyPatch(typeof(CameraStateManager), nameof(CameraStateManager.SetFollowingUnit))]
internal static class CommanderCameraFollowingPatch
{
    private static bool Prefix(Unit unit)
    {
        if (CommanderPlugin.Instance?.IsCommanderModeActive != true || !DynamicMap.mapMaximized)
        {
            return true;
        }

        if (CommanderTacticalMapService.Instance?.SuppressMapFollow == true)
        {
            return false;
        }

        return !CommanderGameAccess.ShouldAllowCommanderSelection(unit, CommanderGameAccess.GetLocalHq());
    }
}

[HarmonyPatch(typeof(SonicBoomManager), nameof(SonicBoomManager.ManageSonicBooms))]
internal static class CommanderFollowSonicBoomPatch
{
    private static void Prefix(ref Vector3 __state)
    {
        CameraStateManager? camera = SceneSingleton<CameraStateManager>.i;
        __state = camera != null ? camera.cameraVelocity : Vector3.zero;
        Aircraft? followed = CommanderCameraFollowService.Instance?.FollowedAircraft;
        if (camera != null && followed?.rb != null)
        {
            // SonicBoomManager only needs the listener velocity during this call.
            // Restoring it immediately avoids feeding aircraft speed into FreeCam.
            camera.cameraVelocity = followed.rb.velocity;
        }
    }

    private static void Postfix(Vector3 __state)
    {
        CameraStateManager? camera = SceneSingleton<CameraStateManager>.i;
        if (camera != null)
        {
            camera.cameraVelocity = __state;
        }
    }
}

[HarmonyPatch(typeof(CameraFreeState), nameof(CameraFreeState.UpdateState))]
internal static class CommanderFreeCameraInputPatch
{
    private const float MovementSpeed = 300f;
    private const float BoostMultiplier = 10f;
    private const float PovMovementSpeed = 2f;
    private const float PovBoostedMovementSpeed = 10f;
    private const float AccelerationResponse = 5f;
    private const float DecelerationResponse = 7f;
    private const float MouseLookScale = 2f;
    private static readonly FieldInfo? PanViewField = AccessTools.Field(typeof(CameraFreeState), "panView");
    private static readonly FieldInfo? TiltViewField = AccessTools.Field(typeof(CameraFreeState), "tiltView");
    private static Vector3 customVelocity;

    private struct CameraInputState
    {
        internal bool Active;
        internal bool AllowInputs;
        internal float Pan;
        internal float Tilt;
        internal Quaternion Rotation;
        internal Vector3 ReportedVelocity;
    }

    private static void Prefix(CameraFreeState __instance, CameraStateManager cam, out CameraInputState __state)
    {
        __state = default;
        if (!CommanderCameraController.CustomInputActive)
        {
            return;
        }

        __state.Active = true;
        __state.AllowInputs = cam.allowInputs;
        __state.Rotation = cam.transform.rotation;
        __state.Pan = GetAngle(PanViewField, __instance, cam.transform.eulerAngles.y);
        __state.Tilt = GetAngle(TiltViewField, __instance, cam.transform.eulerAngles.x);

        // Keep the Basegame FreeCam update for movement integration, damping, terrain
        // collision and FOV, but prevent Rewired's aircraft bindings from moving it.
        cam.allowInputs = false;
        cam.cameraVelocity = Vector3.zero;
        if (CommanderTacticalMapService.Instance?.IsFullscreenOpen == true
            || InputFieldChecker.InsideInputField)
        {
            customVelocity = Vector3.zero;
            return;
        }

        float longitudinal = Axis(CommanderSettings.CameraForward, CommanderSettings.CameraBackward);
        float lateral = Axis(CommanderSettings.CameraRight, CommanderSettings.CameraLeft);
        float vertical = Axis(CommanderSettings.CameraUp, CommanderSettings.CameraDown);
        Vector3 direction = cam.transform.forward * longitudinal
            + cam.transform.right * lateral
            + Vector3.up * vertical;
        bool hasMovementInput = direction.sqrMagnitude > 0.001f;
        if (direction.sqrMagnitude > 1f)
        {
            direction.Normalize();
        }

        bool povActive = CommanderCameraFollowService.IsPovActive;
        bool boost = CommanderShortcutInput.IsPressed(CommanderSettings.CameraBoost);
        float speed = povActive
            ? (boost ? PovBoostedMovementSpeed : PovMovementSpeed)
            : MovementSpeed * (boost ? BoostMultiplier : 1f);
        float stateSpeed = povActive ? 1f : cam.desiredTransSpeed;
        Vector3 targetVelocity = direction * stateSpeed * speed;
        if (povActive)
        {
            customVelocity = targetVelocity;
        }
        else
        {
            float response = hasMovementInput ? AccelerationResponse : DecelerationResponse;
            float blend = 1f - Mathf.Exp(-response * Time.unscaledDeltaTime);
            customVelocity = Vector3.Lerp(customVelocity, targetVelocity, blend);
        }
        if (!hasMovementInput && customVelocity.sqrMagnitude < 0.01f)
        {
            customVelocity = Vector3.zero;
        }
        cam.transform.position += customVelocity * Time.unscaledDeltaTime;
        __state.ReportedVelocity = povActive
            ? Vector3.zero
            : customVelocity;

        if (!CommanderCameraFollowService.IsPovActive
            && CommanderShortcutInput.IsPressed(CommanderSettings.CameraFreeLook))
        {
            float fovScale = Mathf.Min(cam.mainCamera.fieldOfView / 20f, 1f);
            float pitchDirection = PlayerSettings.viewInvertPitch ? 1f : -1f;
            __state.Tilt += pitchDirection
                * fovScale
                * Input.GetAxisRaw("Mouse Y")
                * MouseLookScale
                * PlayerSettings.viewSensitivity;
            __state.Pan += fovScale
                * Input.GetAxisRaw("Mouse X")
                * MouseLookScale
                * PlayerSettings.viewSensitivity;
        }
    }

    private static void Postfix(CameraFreeState __instance, CameraStateManager cam, CameraInputState __state)
    {
        if (!__state.Active)
        {
            return;
        }

        cam.allowInputs = __state.AllowInputs;
        cam.cameraVelocity = __state.ReportedVelocity;
        PanViewField?.SetValue(__instance, __state.Pan);
        TiltViewField?.SetValue(__instance, __state.Tilt);

        // Erase any Basegame Free Look input applied by the original method. POV
        // rebuilds its complete attached rotation in the camera LateUpdate postfix.
        if (!CommanderCameraFollowService.IsPovActive)
        {
            float smoothing = Mathf.Min(2f * Time.unscaledDeltaTime / PlayerSettings.viewSmoothing, 1f);
            cam.transform.rotation = Quaternion.Lerp(
                __state.Rotation,
                Quaternion.Euler(__state.Tilt, __state.Pan, 0f),
                smoothing);
        }
    }

    private static float Axis(BepInEx.Configuration.KeyboardShortcut positive, BepInEx.Configuration.KeyboardShortcut negative)
    {
        return (CommanderShortcutInput.IsPressed(positive) ? 1f : 0f)
            - (CommanderShortcutInput.IsPressed(negative) ? 1f : 0f);
    }

    private static float GetAngle(FieldInfo? field, CameraFreeState state, float fallback)
    {
        return field?.GetValue(state) is float value ? value : fallback;
    }

    internal static void ResetCustomMotion()
    {
        customVelocity = Vector3.zero;
    }
}
