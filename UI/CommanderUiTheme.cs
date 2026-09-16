using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Central IMGUI look for every Commander surface: flat, near-black translucent plates with a
/// single accent hairline and light text. The window plate is 9-sliced with a wide border
/// whose alpha ramps to zero, so window edges dissolve into the scene instead of ending on a
/// hard rectangle; it also bakes in its own title band.
/// </summary>
internal static class CommanderUiTheme
{
    /// <summary>Fixed pixel rows at the top of the window texture: title band plus its divider.</summary>
    private const int TitleBand = 26;

    /// <summary>Width of the alpha ramp baked into the border slice, in texture pixels.</summary>
    private const int Fade = 18;

    // Green is an accent only: edges, rules, headers, the on-state tint. It is never the
    // colour of body text, because green text on a green plate is what made this unreadable.
    private static readonly Color Neon = new(0.42f, 1f, 0.70f, 1f);
    private static readonly Color NeonEdge = new(0.32f, 0.62f, 0.50f, 0.55f);
    private static readonly Color TextColor = new(0.95f, 0.96f, 0.97f, 1f);
    private static readonly Color MutedColor = new(0.76f, 0.80f, 0.82f, 1f);

    /// <summary>Nobody holds it: an untaken capture target, a free resource site, a village or
    /// hilltop with no owner. Moved out of <see cref="CommanderWorldMarkerRenderer"/>'s own
    /// neutral-base colour field (Reuse rule 4) so a strategic point and a capture target read as
    /// the same "neutral" instead of two near-identical yellows.</summary>
    internal static readonly Color NeutralMarker = new(1f, 0.86f, 0.25f, 0.95f);

    private static bool initialized;
    private static Texture2D? windowTexture;
    private static Texture2D? panelTexture;
    private static Texture2D? buttonTexture;
    private static Texture2D? buttonHoverTexture;
    private static Texture2D? buttonActiveTexture;
    private static Texture2D? primaryTexture;
    private static Texture2D? primaryHoverTexture;
    private static Texture2D? selectedTexture;
    private static Texture2D? dangerTexture;
    private static Texture2D? dangerHoverTexture;
    private static Texture2D? toastTexture;
    private static Texture2D? trackTexture;
    private static Texture2D? thumbTexture;
    private static Texture2D? toggleOffTexture;
    private static Texture2D? toggleOnTexture;
    private static Texture2D? borderTexture;

    internal static GUIStyle Window { get; private set; } = null!;
    internal static GUIStyle Panel { get; private set; } = null!;
    internal static GUIStyle Button { get; private set; } = null!;
    internal static GUIStyle PrimaryButton { get; private set; } = null!;
    internal static GUIStyle DangerButton { get; private set; } = null!;
    internal static GUIStyle SelectedButton { get; private set; } = null!;
    internal static GUIStyle Label { get; private set; } = null!;
    internal static GUIStyle MutedLabel { get; private set; } = null!;
    internal static GUIStyle Header { get; private set; } = null!;
    internal static GUIStyle Money { get; private set; } = null!;
    internal static GUIStyle HelpButton { get; private set; } = null!;
    internal static GUIStyle Toggle { get; private set; } = null!;

    /// <summary>Plateless label for markers drawn over the 3D scene. It carries its own dark
    /// halo instead of a background box, because a box big enough to read is a box big enough
    /// to hide whatever the marker is pointing at.</summary>
    internal static GUIStyle WorldLabel { get; private set; } = null!;

    /// <summary>Neutral dark plate for alert toasts, which tint themselves with GUI.color.</summary>
    internal static GUIStyle Toast { get; private set; } = null!;

    /// <summary>Skin installed for the OnGUI pass so scrollbars and sliders match the theme.</summary>
    internal static GUISkin Skin { get; private set; } = null!;

    internal static Color Accent => Neon;
    internal static Color Friendly => GameAssets.i != null ? GameAssets.i.HUDFriendly : Accent;
    internal static Texture2D BorderTexture
    {
        get
        {
            Ensure();
            return borderTexture!;
        }
    }

    internal static void Ensure()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        windowTexture = MakeWindowSlice();
        // Only windows fade. Inner plates are often shorter than the ramp itself (a 26px row,
        // a map header), and a squashed ramp reads as a smudge, so they keep a 1px slice.
        panelTexture = MakeSlice(new Color(0.045f, 0.050f, 0.058f, 0.92f), NeonEdge);
        toastTexture = MakeSlice(
            new Color(0.055f, 0.060f, 0.070f, 0.94f),
            new Color(0.48f, 0.52f, 0.56f, 0.60f));
        buttonTexture = MakeSlice(new Color(0.105f, 0.115f, 0.130f, 0.96f), new Color(0.30f, 0.36f, 0.40f, 0.70f));
        buttonHoverTexture = MakeSlice(new Color(0.175f, 0.195f, 0.215f, 0.99f), new Color(0.42f, 0.90f, 0.66f, 0.90f));
        buttonActiveTexture = MakeSlice(new Color(0.22f, 0.44f, 0.33f, 1f), Neon);
        primaryTexture = MakeSlice(new Color(0.105f, 0.135f, 0.125f, 0.97f), new Color(0.38f, 0.92f, 0.64f, 0.90f));
        primaryHoverTexture = MakeSlice(new Color(0.16f, 0.29f, 0.22f, 1f), Neon);
        selectedTexture = MakeSlice(new Color(0.14f, 0.31f, 0.23f, 1f), Neon);
        dangerTexture = MakeSlice(new Color(0.165f, 0.070f, 0.075f, 0.96f), new Color(0.72f, 0.32f, 0.33f, 0.85f));
        dangerHoverTexture = MakeSlice(new Color(0.30f, 0.085f, 0.09f, 1f), new Color(1f, 0.45f, 0.45f, 1f));
        trackTexture = MakeSlice(new Color(0.02f, 0.024f, 0.030f, 0.6f), new Color(0.02f, 0.024f, 0.030f, 0.6f));
        thumbTexture = MakeSlice(new Color(0.26f, 0.54f, 0.39f, 0.95f), new Color(0.40f, 0.82f, 0.58f, 0.9f));
        toggleOffTexture = MakeSlice(new Color(0.075f, 0.082f, 0.095f, 1f), new Color(0.28f, 0.32f, 0.36f, 0.8f), 16);
        // On-state is a dark green plate with a lit edge, not a neon fill: the label stays white.
        toggleOnTexture = MakeSlice(new Color(0.14f, 0.31f, 0.23f, 1f), Neon, 16);
        borderTexture = MakeSlice(Neon, Neon, 2);

        Label = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            normal = { textColor = TextColor },
            wordWrap = true,
            alignment = TextAnchor.MiddleLeft
        };
        MutedLabel = new GUIStyle(Label)
        {
            fontSize = 12,
            normal = { textColor = MutedColor }
        };
        Header = new GUIStyle(Label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Neon },
            alignment = TextAnchor.MiddleLeft
        };
        Window = new GUIStyle(GUI.skin.window)
        {
            normal = { background = windowTexture, textColor = Neon },
            onNormal = { background = windowTexture, textColor = Neon },
            // Top slice keeps the baked title band and its divider unstretched; the other
            // three carry the alpha ramp, so only the title edge is a hard line.
            border = new RectOffset(Fade, Fade, TitleBand + 1, Fade),
            padding = new RectOffset(12, 10, 5, 10),
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft
        };
        Panel = new GUIStyle(GUI.skin.box)
        {
            normal = { background = panelTexture, textColor = TextColor },
            border = new RectOffset(1, 1, 1, 1),
            padding = new RectOffset(8, 8, 8, 8)
        };
        Button = new GUIStyle(GUI.skin.button)
        {
            normal = { background = buttonTexture, textColor = TextColor },
            hover = { background = buttonHoverTexture, textColor = Color.white },
            active = { background = buttonActiveTexture, textColor = Color.white },
            focused = { background = buttonHoverTexture, textColor = Color.white },
            border = new RectOffset(1, 1, 1, 1),
            padding = new RectOffset(8, 8, 5, 5),
            fontSize = 12,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true
        };
        PrimaryButton = new GUIStyle(Button)
        {
            normal = { background = primaryTexture, textColor = TextColor },
            hover = { background = primaryHoverTexture, textColor = Color.white },
            focused = { background = primaryHoverTexture, textColor = Color.white },
            fontStyle = FontStyle.Bold
        };
        DangerButton = new GUIStyle(Button)
        {
            normal = { background = dangerTexture, textColor = new Color(1f, 0.86f, 0.84f) },
            hover = { background = dangerHoverTexture, textColor = Color.white },
            active = { background = dangerHoverTexture, textColor = Color.white },
            focused = { background = dangerHoverTexture, textColor = Color.white }
        };
        SelectedButton = new GUIStyle(Button)
        {
            normal = { background = selectedTexture, textColor = Color.white },
            hover = { background = selectedTexture, textColor = Color.white },
            focused = { background = selectedTexture, textColor = Color.white },
            fontStyle = FontStyle.Bold
        };
        Toast = new GUIStyle(Button)
        {
            normal = { background = toastTexture, textColor = Color.white },
            hover = { background = toastTexture, textColor = Color.white },
            active = { background = toastTexture, textColor = Color.white },
            focused = { background = toastTexture, textColor = Color.white },
            border = new RectOffset(1, 1, 1, 1),
            fontSize = 13
        };
        HelpButton = new GUIStyle(Button)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(0, 0, 0, 0)
        };
        Toggle = new GUIStyle(GUI.skin.toggle)
        {
            // Metrics stay inherited so the check box keeps its built-in size and placement.
            normal = { background = toggleOffTexture, textColor = MutedColor },
            hover = { background = toggleOffTexture, textColor = Color.white },
            active = { background = toggleOnTexture, textColor = Color.white },
            onNormal = { background = toggleOnTexture, textColor = Color.white },
            onHover = { background = toggleOnTexture, textColor = Color.white },
            onActive = { background = toggleOnTexture, textColor = Color.white },
            border = new RectOffset(2, 2, 2, 2),
            fontSize = 13,
            wordWrap = true
        };
        WorldLabel = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = false,
            padding = new RectOffset(0, 0, 0, 0),
            normal = { background = null, textColor = Color.white }
        };
        Money = new GUIStyle(Panel)
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { background = panelTexture, textColor = Neon }
        };

        BuildSkin();
    }

    /// <summary>Copy of the active skin with themed scrollbars and sliders, installed during OnGUI.</summary>
    private static void BuildSkin()
    {
        Skin = Object.Instantiate(GUI.skin);
        Skin.hideFlags = HideFlags.HideAndDontSave;
        Skin.label = Label;
        Skin.box = Panel;
        Skin.button = Button;
        Skin.window = Window;
        Skin.toggle = Toggle;

        GUIStyle track = new(Skin.verticalScrollbar)
        {
            normal = { background = trackTexture },
            border = new RectOffset(1, 1, 1, 1),
            fixedWidth = 7f
        };
        GUIStyle thumb = new(Skin.verticalScrollbarThumb)
        {
            normal = { background = thumbTexture },
            hover = { background = thumbTexture },
            active = { background = thumbTexture },
            border = new RectOffset(1, 1, 1, 1),
            fixedWidth = 7f
        };
        Skin.verticalScrollbar = track;
        Skin.verticalScrollbarThumb = thumb;
        Skin.horizontalScrollbar = new GUIStyle(track) { fixedWidth = 0f, fixedHeight = 7f };
        Skin.horizontalScrollbarThumb = new GUIStyle(thumb) { fixedWidth = 0f, fixedHeight = 7f };
        // No arrow buttons: modern rail look, and the thumb gets the whole track.
        Skin.verticalScrollbarUpButton = Blank();
        Skin.verticalScrollbarDownButton = Blank();
        Skin.horizontalScrollbarLeftButton = Blank();
        Skin.horizontalScrollbarRightButton = Blank();

        Skin.horizontalSlider = new GUIStyle(Skin.horizontalSlider)
        {
            normal = { background = trackTexture },
            border = new RectOffset(1, 1, 1, 1),
            fixedHeight = 5f
        };
        Skin.horizontalSliderThumb = new GUIStyle(Skin.horizontalSliderThumb)
        {
            normal = { background = thumbTexture },
            hover = { background = thumbTexture },
            active = { background = thumbTexture },
            border = new RectOffset(1, 1, 1, 1),
            fixedWidth = 10f,
            fixedHeight = 15f
        };
    }

    private static GUIStyle Blank()
    {
        return new GUIStyle { fixedWidth = 0f, fixedHeight = 0f };
    }

    internal static Rect ClampWindow(Rect rect, float margin = 12f)
    {
        rect.width = Mathf.Min(rect.width, Mathf.Max(160f, CommanderUiScale.Width - margin * 2f));
        rect.height = Mathf.Min(rect.height, Mathf.Max(120f, CommanderUiScale.Height - margin * 2f));
        rect.x = Mathf.Clamp(rect.x, margin, Mathf.Max(margin, CommanderUiScale.Width - rect.width - margin));
        rect.y = Mathf.Clamp(rect.y, margin, Mathf.Max(margin, CommanderUiScale.Height - rect.height - margin));
        return rect;
    }

    internal static void DrawFrame(Rect rect, float thickness = 2f)
    {
        Ensure();
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), borderTexture!);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), borderTexture!);
        GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), borderTexture!);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), borderTexture!);
    }

    /// <summary>Accent hairline, used as the one separator in the layout language.</summary>
    internal static void DrawRule(Rect rect, float alpha = 0.30f)
    {
        Ensure();
        Color previous = GUI.color;
        GUI.color = new Color(Neon.r, Neon.g, Neon.b, alpha);
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), borderTexture!);
        GUI.color = previous;
    }

    /// <summary>Straight line between two GUI-space points, used for order routes.</summary>
    internal static void DrawLine(Vector2 from, Vector2 to, Color color, float thickness = 2f)
    {
        Ensure();
        Vector2 delta = to - from;
        float length = delta.magnitude;
        if (length < 0.5f)
        {
            return;
        }

        Matrix4x4 previousMatrix = GUI.matrix;
        Color previousColor = GUI.color;
        GUI.color = color;
        // Rotate about `from` in GUI space. GUIUtility.RotateAroundPivot composes the pivot the
        // other way round - outside the UI scale matrix - so it swung every line away from its
        // own start point by however far the UI scale moved that point.
        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        GUI.matrix = previousMatrix
            * Matrix4x4.TRS(from, Quaternion.Euler(0f, 0f, angle), Vector3.one)
            * Matrix4x4.TRS(-(Vector3)from, Quaternion.identity, Vector3.one);
        // White, not the neon border slice: GUI.color multiplies, so drawing an orange attack
        // route on a green texture produced the muddy olive lines instead of orange.
        GUI.DrawTexture(new Rect(from.x, from.y - thickness * 0.5f, length, thickness), Texture2D.whiteTexture);
        GUI.matrix = previousMatrix;
        GUI.color = previousColor;
    }

    /// <summary>
    /// Marks a point in the 3D scene: four corner brackets around it with the label floating
    /// above them. The middle stays empty on purpose - the old filled plate sat right on top
    /// of the target it was marking, so an ordered attack hid the enemy behind a black box.
    /// Pass an empty label for a bare bracket.
    /// </summary>
    internal static void DrawWorldMarker(Vector2 point, string label, Color color, float size)
    {
        Ensure();
        float half = size * 0.5f;
        float arm = Mathf.Max(3f, size * 0.34f);
        float thickness = size >= 34f ? 2f : 1f;
        float left = point.x - half;
        float right = point.x + half;
        float top = point.y - half;
        float bottom = point.y + half;

        Color previous = GUI.color;
        GUI.color = color;
        // Top-left, top-right, bottom-left, bottom-right: one horizontal arm and one vertical
        // arm each, nothing across the middle.
        Bar(left, top, arm, thickness);
        Bar(left, top, thickness, arm);
        Bar(right - arm, top, arm, thickness);
        Bar(right - thickness, top, thickness, arm);
        Bar(left, bottom - thickness, arm, thickness);
        Bar(left, bottom - arm, thickness, arm);
        Bar(right - arm, bottom - thickness, arm, thickness);
        Bar(right - thickness, bottom - arm, thickness, arm);
        GUI.color = previous;

        DrawWorldLabel(new Vector2(point.x, top - 2f), label, color);
    }

    /// <summary>One filled rectangle in the current <c>GUI.color</c>. Internal so a caller building
    /// its own shape out of bars — a strategic point's diamond/square/triangle markers — does not
    /// have to reimplement this one line.</summary>
    internal static void Bar(float x, float y, float width, float height)
    {
        GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
    }

    /// <summary>The colour of every logistics marker the local commander owns — transports,
    /// construction and lift flights, FOB orders, convoy and supply trucks (user, 2026-09-15: "make
    /// logistic mission markers green"). Green reads as "supply", apart from the faction colour of the
    /// fighting units and the amber of an untasked airframe. Hostile logistics keep the hostile colour.</summary>
    internal static readonly Color LogisticsMarkerColor = new(0.35f, 0.9f, 0.4f, 1f);

    /// <summary>
    /// Gap between two stacked labels: 2 px, the halo's own reach, so stacked lines touch without
    /// their halos overwriting each other's letters (user, 2026-09-15: "when markers stack on a
    /// point they become illegible, as the text is placed directly over each other").
    /// </summary>
    private const float LabelStackGapPixels = 2f;

    /// <summary>How many places up a label will try before it gives up and overlaps: 12. A dozen
    /// stacked lines is already a column the eye cannot read at a glance; past that the point is
    /// overloaded and stacking further would put labels off the top of the screen.</summary>
    private const int LabelStackMaxSteps = 12;

    /// <summary>The label rectangles drawn so far this repaint. Every world label — units, points,
    /// aircraft, orders — comes through <see cref="DrawWorldLabel"/>, so this one list is the whole
    /// picture, and it is emptied when the frame number changes.</summary>
    private static readonly List<Rect> placedLabels = new();

    private static int placedLabelsFrame = -1;

    /// <summary>
    /// Where a label goes so it does not sit on one already drawn, pure: the rectangle is moved UP
    /// by its own height plus the gap, one step at a time, until it overlaps nothing in
    /// <paramref name="placed"/> or <paramref name="maxSteps"/> is spent. Up rather than down,
    /// because every world label is anchored above its marker — stacking upward keeps the marker
    /// itself clear and reads as a column rising off the point. Touching edges do not count as an
    /// overlap.
    /// </summary>
    internal static Rect StackedLabelRect(Rect rect, IReadOnlyList<Rect> placed, float gap, int maxSteps)
    {
        for (int step = 0; step < maxSteps; step++)
        {
            bool overlaps = false;
            for (int i = 0; i < placed.Count; i++)
            {
                if (RectsOverlap(rect, placed[i]))
                {
                    overlaps = true;
                    break;
                }
            }

            if (!overlaps)
            {
                return rect;
            }

            rect.y -= rect.height + gap;
        }

        return rect;
    }

    /// <summary>Strict overlap: two rectangles that merely touch along an edge do not overlap.</summary>
    internal static bool RectsOverlap(Rect a, Rect b)
    {
        return a.xMin < b.xMax && a.xMax > b.xMin && a.yMin < b.yMax && a.yMax > b.yMin;
    }

    /// <summary>Label with a dark halo, bottom-centred on <paramref name="anchor"/> — or stacked
    /// above whatever label already stands there (see <see cref="StackedLabelRect"/>).</summary>
    internal static void DrawWorldLabel(Vector2 anchor, string label, Color color)
    {
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        Ensure();
        Vector2 size = WorldLabel.CalcSize(new GUIContent(label));
        Rect rect = new(anchor.x - size.x * 0.5f, anchor.y - size.y, size.x, size.y);
        // Only the repaint pass places labels: the layout pass draws nothing visible, and letting it
        // register rectangles would make the repaint stack every label one step higher than needed.
        if (Event.current != null && Event.current.type == EventType.Repaint)
        {
            if (placedLabelsFrame != Time.frameCount)
            {
                placedLabelsFrame = Time.frameCount;
                placedLabels.Clear();
            }

            rect = StackedLabelRect(rect, placedLabels, LabelStackGapPixels, LabelStackMaxSteps);
            placedLabels.Add(rect);
        }

        Color previous = GUI.color;
        // Four-way halo instead of a plate: legible over sky, terrain or a unit, and it hides
        // nothing. ponytail: 4 offsets, go to 8 only if it still smears on bright terrain.
        GUI.color = new Color(0f, 0f, 0f, 0.9f * color.a);
        GUI.Label(new Rect(rect.x - 1f, rect.y, rect.width, rect.height), label, WorldLabel);
        GUI.Label(new Rect(rect.x + 1f, rect.y, rect.width, rect.height), label, WorldLabel);
        GUI.Label(new Rect(rect.x, rect.y - 1f, rect.width, rect.height), label, WorldLabel);
        GUI.Label(new Rect(rect.x, rect.y + 1f, rect.width, rect.height), label, WorldLabel);
        GUI.color = color;
        GUI.Label(rect, label, WorldLabel);
        GUI.color = previous;
    }

    /// <summary>The stacking rule at its boundaries, run at plugin load beside the other services'
    /// checks: a clear rectangle stays put, an overlapping one moves up by its height plus the gap,
    /// a touching edge is not an overlap, and the step count is bounded.</summary>
    internal static void SelfCheck()
    {
        List<Rect> placed = new() { new Rect(0f, 100f, 80f, 16f) };
        Rect clear = StackedLabelRect(new Rect(200f, 100f, 80f, 16f), placed, 2f, 12);
        Rect moved = StackedLabelRect(new Rect(10f, 100f, 80f, 16f), placed, 2f, 12);
        Rect touching = StackedLabelRect(new Rect(80f, 100f, 80f, 16f), placed, 2f, 12);
        List<Rect> column = new();
        for (int i = 0; i < 12; i++)
        {
            column.Add(new Rect(0f, 100f - i * 18f, 80f, 16f));
        }

        Rect capped = StackedLabelRect(new Rect(0f, 100f, 80f, 16f), column, 2f, 12);
        if (!Mathf.Approximately(clear.y, 100f)
            || !Mathf.Approximately(moved.y, 82f)
            || !Mathf.Approximately(touching.y, 100f)
            || !Mathf.Approximately(capped.y, 100f - 12 * 18f))
        {
            CommanderPlugin.Log.LogError(
                "UI theme self-check FAILED: world labels no longer stack upward off an occupied spot "
                    + $"(clear {clear.y}, moved {moved.y}, touching {touching.y}, capped {capped.y}).");
        }
    }

    internal static bool DrawHelpButton(float windowWidth, ref bool visible)
    {
        Ensure();
        if (GUI.Button(new Rect(windowWidth - 64f, 3f, 26f, 22f), "?", HelpButton))
        {
            visible = !visible;
        }
        return visible;
    }

    internal static void DrawHelpOverlay(Rect rect, string text)
    {
        Ensure();
        GUI.Box(rect, string.Empty, Panel);
        GUI.Label(new Rect(rect.x + 10f, rect.y + 8f, rect.width - 20f, rect.height - 16f), text, Label);
    }

    /// <summary>Flat plate with a 1px edge, 9-sliced with a 1px border.</summary>
    private static Texture2D MakeSlice(Color fill, Color edge, int height = 8)
    {
        Texture2D texture = NewTexture(4, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                bool onEdge = x == 0 || x == 3 || y == 0 || y == height - 1;
                texture.SetPixel(x, y, onEdge ? edge : fill);
            }
        }
        return Finish(texture);
    }

    /// <summary>Window plate: title band on a hard top edge, faded sides and bottom.</summary>
    private static Texture2D MakeWindowSlice()
    {
        const int bodyHeight = 4;
        int width = Fade * 2 + 4;
        int height = TitleBand + 1 + bodyHeight + Fade;
        Color band = new(0.080f, 0.088f, 0.100f, 0.97f);
        Color body = new(0.032f, 0.037f, 0.044f, 0.94f);

        Texture2D texture = NewTexture(width, height);
        for (int y = 0; y < height; y++)
        {
            // Texture space is bottom-up, so the title band lives in the last rows.
            int fromTop = height - 1 - y;
            Color row = fromTop < TitleBand ? band : fromTop == TitleBand ? Neon : body;
            // Sides fade always; the bottom fades too, but the title band keeps a hard top
            // edge so the header still reads as a header.
            float verticalAlpha = y >= Fade ? 1f : y / (float)Fade;

            for (int x = 0; x < width; x++)
            {
                float alpha = Mathf.Min(EdgeRamp(x, width), verticalAlpha);
                bool onRule = fromTop > TitleBand && EdgeDistance(x, width) == Fade - 1;
                Color source = onRule ? NeonEdge : row;
                texture.SetPixel(x, y, new Color(source.r, source.g, source.b, source.a * alpha));
            }
        }
        return Finish(texture);
    }

    /// <summary>Distance in pixels to the nearer edge on this axis.</summary>
    private static int EdgeDistance(int coordinate, int size)
    {
        return Mathf.Min(coordinate, size - 1 - coordinate);
    }

    /// <summary>0 at the outer pixel, 1 once <see cref="Fade"/> pixels in.</summary>
    private static float EdgeRamp(int coordinate, int size)
    {
        int distance = EdgeDistance(coordinate, size);
        return distance >= Fade ? 1f : distance / (float)Fade;
    }

    private static Texture2D NewTexture(int width, int height)
    {
        return new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
    }

    private static Texture2D Finish(Texture2D texture)
    {
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return texture;
    }
}
