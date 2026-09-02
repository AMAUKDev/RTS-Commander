using UnityEngine;

namespace GroundControlRts;

internal static class CommanderUiScale
{
    private const float BaselineScale = 1.5f;
    private static int lastScreenWidth;
    private static int lastScreenHeight;

    internal static float DisplayScale => CommanderSettings.UiScale;
    internal static float Scale => DisplayScale / BaselineScale;
    internal static float Width => Screen.width / Scale;
    internal static float Height => Screen.height / Scale;

    internal static void ApplyResolutionPreset()
    {
        lastScreenWidth = Screen.width;
        lastScreenHeight = Screen.height;
        CommanderSettings.UiScale = Screen.height <= 1200 ? 1f : Screen.height <= 1600 ? 1.25f : 1.5f;
    }

    internal static void RefreshResolutionPreset()
    {
        if (Screen.width != lastScreenWidth || Screen.height != lastScreenHeight)
        {
            ApplyResolutionPreset();
        }
    }

    internal static Matrix4x4 Begin()
    {
        Matrix4x4 previous = GUI.matrix;
        GUI.matrix = previous * Matrix4x4.Scale(new Vector3(Scale, Scale, 1f));
        return previous;
    }

    internal static void End(Matrix4x4 previous)
    {
        GUI.matrix = previous;
    }

    internal static Vector2 ScreenToGui(Vector2 screenPoint)
    {
        return new Vector2(screenPoint.x / Scale, (Screen.height - screenPoint.y) / Scale);
    }

    internal static Vector2 GuiToScreen(Vector2 guiPoint)
    {
        return new Vector2(guiPoint.x * Scale, Screen.height - guiPoint.y * Scale);
    }

}
