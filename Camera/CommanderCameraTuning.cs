using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The RTS camera's feel, in one place: how fast panning scales with altitude, how far the
/// camera may pitch, how quickly it settles, and whether a unit is already framed well enough
/// that moving the camera to it would be an unwanted yank.
/// <para>
/// It is deliberately pure maths on plain numbers so <see cref="SelfCheck"/> can run it at load
/// without a scene, the same way the other services self-check.
/// </para>
/// </summary>
internal static class CommanderCameraTuning
{
    /// <summary>Pitch limits, in degrees down-positive. Past these the camera goes upside down.</summary>
    internal const float MinPitch = -80f;
    internal const float MaxPitch = 85f;

    /// <summary>Height above ground at which pan speed is exactly the configured speed.</summary>
    internal const float ReferenceHeight = 400f;
    internal const float MinSpeedScale = 0.15f;
    internal const float MaxSpeedScale = 4f;

    /// <summary>The camera eases toward this clearance, and is hard-stopped at the smaller one.</summary>
    internal const float GroundClearance = 6f;
    internal const float HardGroundClearance = 3f;
    internal const float MaxAltitude = 30000f;

    /// <summary>A unit inside this much of the screen edge counts as comfortably visible.</summary>
    internal const float FrameMargin = 0.14f;

    /// <summary>Beyond this a unit is on screen but too far away to read, so it still gets framed.</summary>
    internal const float FrameMaxDistance = 15000f;

    /// <summary>Fraction of the height above ground one mouse-wheel notch travels.</summary>
    internal const float ZoomStepFraction = 0.25f;

    /// <summary>Screen border, in pixels, that triggers edge scrolling.</summary>
    internal const float EdgeScrollMargin = 6f;

    /// <summary>
    /// Pan speed multiplier for the camera's height above the ground. Near the deck you nudge;
    /// high up you cross the map. Without this a single speed is either useless at altitude or
    /// uncontrollable on the deck, which is the usual reason a free camera feels wrong in an RTS.
    /// </summary>
    internal static float SpeedScale(float heightAboveGround)
    {
        return Mathf.Clamp(heightAboveGround / ReferenceHeight, MinSpeedScale, MaxSpeedScale);
    }

    /// <summary>
    /// Normalises a pan/tilt angle that may have arrived as 0-360 (Unity euler angles) and clamps
    /// it to the usable pitch band. 350 means -10, not "past the top".
    /// </summary>
    internal static float ClampPitch(float tilt)
    {
        return Mathf.Clamp(Mathf.DeltaAngle(0f, tilt), MinPitch, MaxPitch);
    }

    /// <summary>
    /// Blend factor for an exponential approach with a time-constant in seconds, frame-rate
    /// independent. A smoothing of zero means "arrive this frame".
    /// </summary>
    internal static float SmoothBlend(float smoothingSeconds, float deltaTime)
    {
        if (smoothingSeconds <= 0.0001f || deltaTime <= 0f)
        {
            return 1f;
        }

        return 1f - Mathf.Exp(-deltaTime / smoothingSeconds);
    }

    /// <summary>
    /// True when a unit is already on screen, away from the edges and close enough to read, so
    /// selecting it should leave the camera exactly where the player put it.
    /// </summary>
    internal static bool IsFramed(Vector3 viewportPoint, float distance)
    {
        return viewportPoint.z > 0f
            && distance <= FrameMaxDistance
            && viewportPoint.x >= FrameMargin
            && viewportPoint.x <= 1f - FrameMargin
            && viewportPoint.y >= FrameMargin
            && viewportPoint.y <= 1f - FrameMargin;
    }

    /// <summary>
    /// One runnable check, run once from <see cref="CommanderPlugin"/> at load and logged to the
    /// BepInEx console. It guards the two pieces of camera maths that are silently wrong rather
    /// than obviously broken: a pitch clamp fed a 0-360 angle, and the framing test.
    /// </summary>
    internal static void SelfCheck()
    {
        System.Collections.Generic.List<string> failures = new();

        // 350 degrees is -10 degrees, and must survive the clamp unchanged.
        Expect(failures, "pitch 350 normalises", ClampPitch(350f), -10f);
        Expect(failures, "pitch clamps down", ClampPitch(120f), MaxPitch);
        Expect(failures, "pitch clamps up", ClampPitch(-120f), MinPitch);
        Expect(failures, "pitch passthrough", ClampPitch(35f), 35f);

        Expect(failures, "speed at reference", SpeedScale(ReferenceHeight), 1f);
        Expect(failures, "speed floor", SpeedScale(0f), MinSpeedScale);
        Expect(failures, "speed ceiling", SpeedScale(1e6f), MaxSpeedScale);

        Expect(failures, "smoothing off is instant", SmoothBlend(0f, 0.016f), 1f);
        if (SmoothBlend(0.05f, 0.016f) is var blend && (blend <= 0f || blend >= 1f))
        {
            failures.Add($"smoothing blend out of range: {blend}");
        }

        ExpectTrue(failures, "centred unit is framed", IsFramed(new Vector3(0.5f, 0.5f, 100f), 1000f));
        ExpectTrue(failures, "behind camera is not framed", !IsFramed(new Vector3(0.5f, 0.5f, -1f), 1000f));
        ExpectTrue(failures, "edge is not framed", !IsFramed(new Vector3(0.02f, 0.5f, 100f), 1000f));
        ExpectTrue(failures, "far away is not framed", !IsFramed(new Vector3(0.5f, 0.5f, 100f), FrameMaxDistance + 1f));

        if (failures.Count == 0)
        {
            CommanderPlugin.Log.LogDebug("Camera tuning self-check passed.");
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Camera tuning self-check FAILED: {failures[i]}");
        }
    }

    private static void Expect(System.Collections.Generic.List<string> failures, string name, float actual, float expected)
    {
        if (!Mathf.Approximately(actual, expected))
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void ExpectTrue(System.Collections.Generic.List<string> failures, string name, bool condition)
    {
        if (!condition)
        {
            failures.Add(name);
        }
    }
}
