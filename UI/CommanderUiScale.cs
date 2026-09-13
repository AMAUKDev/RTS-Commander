using UnityEngine;

namespace GroundControlRts;

internal static class CommanderUiScale
{
    private const float BaselineScale = 1.5f;

    /// <summary>
    /// Tallest screen still treated as 1080p class. 1200 rather than 1080 so the 1200 px tall
    /// 16:10 displays land here too, where the unscaled layout already fits.
    /// </summary>
    private const int SmallDisplayMaxHeight = 1200;

    /// <summary>
    /// Tallest screen still treated as 1440p class. Above this is 4K class, which needs the
    /// largest preset to stay readable at arm's length.
    /// </summary>
    private const int MediumDisplayMaxHeight = 1600;

    /// <summary>UI scale for 1080p class displays: the layout was authored to fit these unscaled.</summary>
    private const float SmallDisplayScale = 1f;

    /// <summary>UI scale for 1440p class displays: a quarter more pixels per glyph.</summary>
    private const float MediumDisplayScale = 1.25f;

    /// <summary>
    /// UI scale for 4K class displays. Same number as <see cref="BaselineScale"/> by coincidence
    /// of tuning, not by dependency: that one is the authoring scale of the GUI matrix.
    /// </summary>
    private const float LargeDisplayScale = 1.5f;

    /// <summary>
    /// Smallest scale the UI scale slider offers. Below 0.75x the window text stops being
    /// readable on a 1080p screen, so there is no point letting the player go there.
    /// </summary>
    internal const float MinOverride = 0.75f;

    /// <summary>
    /// Largest scale the UI scale slider offers. Above 2.5x a single commander window is taller
    /// than a 1080p screen, so the window would no longer fit whatever the clamp does.
    /// </summary>
    internal const float MaxOverride = 2.5f;

    private static int lastScreenWidth;
    private static int lastScreenHeight;

    internal static float DisplayScale => CommanderSettings.UiScale;
    internal static float Scale => DisplayScale / BaselineScale;
    internal static float Width => Screen.width / Scale;
    internal static float Height => Screen.height / Scale;

    /// <summary>
    /// The automatic scale for a screen of this height. Pure, so the self-check can call it
    /// without a running game.
    /// </summary>
    internal static float PresetForHeight(int screenHeight)
    {
        return screenHeight <= SmallDisplayMaxHeight ? SmallDisplayScale
            : screenHeight <= MediumDisplayMaxHeight ? MediumDisplayScale
            : LargeDisplayScale;
    }

    /// <summary>
    /// The scale actually used: a manual override when the player set one, otherwise the
    /// automatic preset. Pure, so the self-check can call it without a running game.
    /// </summary>
    internal static float Resolve(float overrideScale, float automaticScale)
    {
        return overrideScale > 0f ? overrideScale : automaticScale;
    }

    internal static void ApplyResolutionPreset()
    {
        lastScreenWidth = Screen.width;
        lastScreenHeight = Screen.height;
        // Only ever writes the automatic value; a manual override is never touched here, so a
        // window resize cannot undo the player's choice.
        CommanderSettings.AutomaticUiScale = PresetForHeight(Screen.height);
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

    /// <summary>
    /// One runnable check, run once from <see cref="CommanderPlugin"/> at load and logged to the
    /// BepInEx console. It covers only the pure functions: the override-versus-automatic decision
    /// and the resolution ladder, both of which can be retuned into nonsense without anything
    /// visibly breaking until a player is looking at the wrong-sized UI.
    /// </summary>
    internal static void SelfCheck()
    {
        System.Collections.Generic.List<string> failures = new();

        Expect(failures, "override wins", Resolve(1.5f, 1f), 1.5f);
        Expect(failures, "automatic when zero", Resolve(0f, 1.25f), 1.25f);
        // A negative value can only come from a hand-edited config file; treat it as automatic
        // rather than flipping the GUI matrix inside out.
        Expect(failures, "negative is automatic", Resolve(-1f, 1.25f), 1.25f);

        ExpectTrue(failures, "bounds ordered", MinOverride < MaxOverride);

        Expect(failures, "ladder 1080p", PresetForHeight(1080), SmallDisplayScale);
        Expect(failures, "ladder 1440p", PresetForHeight(1440), MediumDisplayScale);
        Expect(failures, "ladder 4K", PresetForHeight(2160), LargeDisplayScale);

        // Every preset must survive the slider's clamp untouched. If one sat outside the slider
        // bounds, the first frame of the settings panel would clamp it and write the clamped
        // number back as a manual override, silently ending automatic scaling.
        ExpectTrue(failures, "small preset inside slider bounds", IsInsideSliderBounds(SmallDisplayScale));
        ExpectTrue(failures, "medium preset inside slider bounds", IsInsideSliderBounds(MediumDisplayScale));
        ExpectTrue(failures, "large preset inside slider bounds", IsInsideSliderBounds(LargeDisplayScale));

        if (failures.Count == 0)
        {
            CommanderPlugin.Log.LogDebug("UI scale self-check passed.");
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"UI scale self-check FAILED: {failures[i]}");
        }
    }

    private static bool IsInsideSliderBounds(float scale)
    {
        return scale >= MinOverride && scale <= MaxOverride;
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
