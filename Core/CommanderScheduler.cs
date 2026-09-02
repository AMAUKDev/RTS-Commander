using UnityEngine;

namespace GroundControlRts;

internal static class CommanderScheduler
{
    internal static float Stagger(string taskName, float interval, float maximumDelay = -1f)
    {
        float delayRange = maximumDelay >= 0f ? Mathf.Min(maximumDelay, interval) : interval;
        uint hash = 2166136261;
        for (int i = 0; i < taskName.Length; i++)
        {
            hash = (hash ^ taskName[i]) * 16777619;
        }

        float phase = (hash & 0xFFFF) / 65535f;
        return Time.unscaledTime + phase * delayRange;
    }

    internal static bool IsDue(ref float nextRun, float interval)
    {
        if (Time.unscaledTime < nextRun)
        {
            return false;
        }

        nextRun = Time.unscaledTime + interval;
        return true;
    }
}
