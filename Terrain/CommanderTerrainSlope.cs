using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The mod's one piece of slope arithmetic: turn four ground heights around a point into the
/// direction "up" is on that ground, limit how far from vertical that is allowed to lean, and
/// combine it with a compass heading into a rotation.
/// </summary>
/// <remarks>
/// Extracted from <c>CommanderLocalHeightMapBaker.HeightMap.EstimateNormalY</c>, which fitted the
/// same four-point plane but threw away everything except the vertical component (Reuse rule 5 —
/// the second caller generalises the first rather than forking it). That baker now calls
/// <see cref="EstimateNormal"/> and takes the <c>y</c> off the result, so the SAM analyser's
/// flat-ground test and the build ghost's tilt cannot drift apart.
/// <para>
/// Everything here is pure: no terrain reads, no Unity scene access. The caller decides where the
/// heights come from, which is what lets the self-check run the arithmetic on a made-up slope.
/// </para>
/// </remarks>
internal static class CommanderTerrainSlope
{
    /// <summary>
    /// The ground's up direction at a point, from the heights measured <paramref name="spacing"/>
    /// metres away on each side of it. A central difference rather than a three-point triangle: a
    /// triangle's answer depends on which way round its corners are taken, and on a ridge it reads
    /// the slope of one flank instead of the average of both.
    /// </summary>
    internal static Vector3 EstimateNormal(
        float westHeight, float eastHeight, float southHeight, float northHeight, float spacing)
    {
        float span = Mathf.Max(spacing, 0.01f);
        return new Vector3(westHeight - eastHeight, span * 2f, southHeight - northHeight).normalized;
    }

    /// <summary>How far a ground normal leans away from vertical, in degrees. Zero is dead flat.</summary>
    internal static float TiltDegrees(Vector3 normal)
    {
        return normal.sqrMagnitude < 0.0001f ? 0f : Vector3.Angle(normal.normalized, Vector3.up);
    }

    /// <summary>
    /// The same normal, leaned back toward vertical until it is no steeper than
    /// <paramref name="maxTiltDegrees"/>. A normal that is already inside the limit is returned
    /// unchanged, so flat ground is never nudged.
    /// </summary>
    internal static Vector3 ClampTilt(Vector3 normal, float maxTiltDegrees)
    {
        Vector3 candidate = normal.sqrMagnitude < 0.0001f ? Vector3.up : normal.normalized;
        float tilt = TiltDegrees(candidate);
        if (tilt <= maxTiltDegrees)
        {
            return candidate;
        }

        return Vector3.RotateTowards(
            candidate, Vector3.up, (tilt - maxTiltDegrees) * Mathf.Deg2Rad, 0f).normalized;
    }

    /// <summary>
    /// A compass heading laid over a slope: the building still points where the player aimed it,
    /// but its floor lies on the ground rather than cutting into the hill. Flat ground
    /// (<paramref name="normal"/> straight up) gives exactly the plain yaw rotation, so the
    /// conforming and upright paths agree wherever the ground is level.
    /// </summary>
    /// <remarks>
    /// The project-then-look idiom is the one the SAM construction code already uses to stand a
    /// vehicle on a slope (<c>SamSites/Construction/CommanderSamSiteConstruction.cs</c>,
    /// <c>TeleportGroundVehicle</c>) — same two lines, so a building and a launcher sit on a hill
    /// the same way.
    /// </remarks>
    internal static Quaternion Compose(float yawDegrees, Vector3 normal)
    {
        Vector3 up = normal.sqrMagnitude < 0.0001f ? Vector3.up : normal.normalized;
        Vector3 forward = Quaternion.Euler(0f, yawDegrees, 0f) * Vector3.forward;
        Vector3 flattened = Vector3.ProjectOnPlane(forward, up);
        if (flattened.sqrMagnitude < 0.0001f)
        {
            // The heading is straight up the normal, which only happens on a cliff face the tilt
            // clamp would already have rejected. Fall back to due north on the same plane.
            flattened = Vector3.ProjectOnPlane(Vector3.forward, up);
        }

        return flattened.sqrMagnitude < 0.0001f
            ? Quaternion.Euler(0f, yawDegrees, 0f)
            : Quaternion.LookRotation(flattened.normalized, up);
    }
}
