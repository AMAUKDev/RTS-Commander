using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Draws every non-base point on the world view or the tactical map — the
/// <c>DrawCaptureTargets</c> world-vs-map split (<see cref="CommanderWorldMarkerRenderer"/>)
/// — and remembers where each one last landed on screen so a click can find it (T13).
/// </summary>
internal sealed partial class CommanderStrategicPointService
{
    /// <summary>World-view marker size, in GUI pixels. Chosen between the two sizes
    /// <see cref="CommanderWorldMarkerRenderer"/> uses for its own world markers — its standard unit
    /// marker (<c>26f</c>) and its base capture-progress ring (<c>34f</c>) — so a strategic point
    /// reads at roughly the same visual weight as either without being mistaken for one in
    /// particular.</summary>
    private const float WorldMarkerSize = 30f;

    /// <summary>Map-view marker size — smaller, because the map shows the whole theatre at once.</summary>
    private const float MapMarkerSize = 18f;

    internal void DrawMarkers(Camera camera)
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
        // The world view stays marked unless the fullscreen (M) map covers it. The compact
        // Commander Map is a window beside the 3D view, so with it open both get markers; a
        // "hide the world whenever any map is open" rule left the 3D view blank for anyone who
        // plays with the map window up, which is everyone.
        bool fullscreenMap = map?.IsFullscreenOpen == true;
        bool anyMap = DynamicMap.mapMaximized;
        int frame = Time.frameCount;

        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            if (point.Kind == StrategicPointKind.Base)
            {
                // The capture renderer already marks bases; a second marker on top would only
                // double up on the same information.
                continue;
            }

            if (!fullscreenMap)
            {
                Vector3 screen = camera.WorldToScreenPoint(point.Position.ToLocalPosition());
                if (screen.z > 0f
                    && screen.x >= 0f && screen.x <= Screen.width
                    && screen.y >= 0f && screen.y <= Screen.height)
                {
                    Vector2 worldPosition = CommanderUiScale.ScreenToGui(screen);
                    point.ScreenPosition = worldPosition;
                    point.ScreenHalfSize = WorldMarkerSize * 0.5f;
                    point.ScreenFrame = frame;
                    DrawPointMarker(point, localHq, worldPosition, WorldMarkerSize);
                }
            }

            if (anyMap && map != null && map.TryWorldToMapScreen(point.Position, out Vector2 mapPoint))
            {
                // Drawn last, so a click resolves against the map marker when both are on screen:
                // that is where the pointer is when the map window is up.
                Vector2 mapPosition = CommanderUiScale.ScreenToGui(mapPoint);
                point.ScreenPosition = mapPosition;
                point.ScreenHalfSize = MapMarkerSize * 0.5f;
                point.ScreenFrame = frame;
                DrawPointMarker(point, localHq, mapPosition, MapMarkerSize);
            }
        }
    }

    private void DrawPointMarker(CommanderStrategicPoint point, FactionHQ? localHq, Vector2 center, float size)
    {
        FactionHQ? owner = point.GetOwner();
        bool contested = point.Kind != StrategicPointKind.Site && point.Hold.Contested;
        Color shapeColor = owner == null
            ? CommanderUiTheme.NeutralMarker
            : ReferenceEquals(owner, localHq)
                ? CommanderGameAccess.GetFriendlyColor()
                : CommanderGameAccess.GetHostileColor();

        DrawDot(center, size, shapeColor, contested);

        // Same voice as the base markers ("CAPTURABLE  MARIS AIRPORT"): what it is, its name, and
        // the one number that says what to do about it. The label carries the kind; the dot only
        // says where and whose.
        string readout = BuildReadout(point, localHq, owner, out Color labelColor, shapeColor);
        // A forward-base mission on this point gets an FOB tag on top of the ordinary readout
        // (ledger row 27 addendum, Operations/CommanderOperationsMarkers.cs).
        if (CommanderOperationsService.Instance?.IsForwardBase(point, localHq) == true)
        {
            readout += "  FOB";
        }

        CommanderUiTheme.DrawWorldLabel(
            new Vector2(center.x, center.y - size * 0.5f - 2f),
            $"{point.Label.ToUpperInvariant()}  {readout.ToUpperInvariant()}",
            labelColor);
    }

    private string BuildReadout(
        CommanderStrategicPoint point, FactionHQ? localHq, FactionHQ? owner, out Color labelColor, Color shapeColor)
    {
        labelColor = shapeColor;
        if (point.Kind == StrategicPointKind.Site)
        {
            bool free = point.Mine == null || point.Mine.disabled;
            if (!free)
            {
                int level = CommanderEconomyService.Instance?.GetMineLevel(point.Mine!) ?? 1;
                return $"mine L{level}";
            }

            // A free site is now taken and held like any other point (user decision 2026-09-14), so
            // its marker has to show the garrison taking it. Saying only "free" is what made the
            // user's own test — two vehicles parked on a site — look like nothing was happening.
            if (owner != null)
            {
                return "held, no mine";
            }

            int siteGarrison = localHq != null ? point.PresentCount(localHq, hqOrder) : 0;
            return point.Hold.Contested
                ? $"free, contested {siteGarrison}/{CommanderSettings.PointsMinGarrison}"
                : $"free {siteGarrison}/{CommanderSettings.PointsMinGarrison}";
        }

        int minGarrison = CommanderSettings.PointsMinGarrison;
        int ownCount = localHq != null ? point.PresentCount(localHq, hqOrder) : 0;
        string countReadout;
        if (owner == null && ownCount == 0)
        {
            int otherCount = 0;
            for (int h = 0; h < hqOrder.Count && h < point.GarrisonCounts.Length; h++)
            {
                if (!ReferenceEquals(hqOrder[h], localHq) && point.GarrisonCounts[h] > 0)
                {
                    otherCount = point.GarrisonCounts[h];
                    break;
                }
            }

            if (otherCount > 0)
            {
                countReadout = $"{otherCount}/{minGarrison}";
                labelColor = CommanderGameAccess.GetHostileColor();
            }
            else
            {
                countReadout = $"0/{minGarrison}";
            }
        }
        else
        {
            countReadout = $"{ownCount}/{minGarrison}";
        }

        if (point.Hold.Contested)
        {
            return $"{countReadout}  CONTESTED";
        }

        if (point.Hold.CandidateIndex >= 0)
        {
            return $"{countReadout}  {point.Hold.Progress:0}s";
        }

        return countReadout;
    }


    /// <summary>
    /// Finds and focuses the nearest point drawn at <paramref name="screenPosition"/> this frame or
    /// the last (a point not drawn this tick — off screen, map closed on the wrong view — cannot be
    /// clicked). The nearest-hit shape <see cref="CommanderMarkerService.TryGetMarkerUnitAt"/>
    /// uses for units.
    /// </summary>
    internal bool TryFocusPointAt(Vector2 screenPosition)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screenPosition);
        int frame = Time.frameCount;
        CommanderStrategicPoint? best = null;
        float bestDistanceSquared = float.MaxValue;

        for (int i = 0; i < points.Count; i++)
        {
            CommanderStrategicPoint point = points[i];
            if (point.ScreenFrame < frame - 1)
            {
                continue;
            }

            float dx = guiPoint.x - point.ScreenPosition.x;
            float dy = guiPoint.y - point.ScreenPosition.y;
            if (Mathf.Abs(dx) > point.ScreenHalfSize || Mathf.Abs(dy) > point.ScreenHalfSize)
            {
                continue;
            }

            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = point;
            }
        }

        if (best == null)
        {
            return false;
        }

        FocusedPoint = best;
        return true;
    }

    internal void ClearFocus()
    {
        FocusedPoint = null;
    }
    /// <summary>
    /// A filled dot with a dark rim at the point, in the owner's colour. Shapes drawn from rotated
    /// bars were tried first and came out skewed and mis-scaled: <c>GUIUtility.RotateAroundPivot</c>
    /// composes badly with the UI-scale matrix every marker is drawn under (the same trap
    /// <c>CommanderUiTheme</c> notes for its own lines). The label already names the kind, so the
    /// dot only has to say "here" and "whose". Contested points alternate rim and fill in white.
    /// </summary>
    private static void DrawDot(Vector2 center, float size, Color color, bool contested)
    {
        // The marker size is the click target; the dot itself is a third of it so it never hides
        // what is standing on the point.
        float dot = Mathf.Max(6f, size / 3f);
        float half = dot * 0.5f;
        Color previous = GUI.color;

        GUI.color = new Color(0f, 0f, 0f, 0.85f * color.a);
        CommanderUiTheme.Bar(center.x - half - 1f, center.y - half - 1f, dot + 2f, dot + 2f);
        GUI.color = contested ? Color.white : color;
        CommanderUiTheme.Bar(center.x - half, center.y - half, dot, dot);
        if (contested)
        {
            GUI.color = color;
            CommanderUiTheme.Bar(center.x - half + 2f, center.y - half + 2f, Mathf.Max(1f, dot - 4f), Mathf.Max(1f, dot - 4f));
        }

        GUI.color = previous;
    }
}
