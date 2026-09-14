using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// World/map markers for platoons and live release points — the
/// <c>Points/CommanderStrategicPointMarkers.cs</c> world-vs-map split, copied member for member
/// where it applies (ledger row 27).
/// </summary>
internal sealed partial class CommanderOperationsService
{
    /// <summary>World-view marker size. Matches
    /// <c>Points/CommanderStrategicPointMarkers.WorldMarkerSize</c>'s value; independently declared
    /// here because that one is private to a different class.</summary>
    private const float MarkerWorldSize = 30f;

    /// <summary>Map-view marker size, matching the same class's <c>MapMarkerSize</c>.</summary>
    private const float MarkerMapSize = 18f;

    internal void DrawMarkers(Camera camera)
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
        bool fullscreenMap = map?.IsFullscreenOpen == true;
        bool anyMap = DynamicMap.mapMaximized;

        foreach (KeyValuePair<FactionHQ, OperationsState> entry in states)
        {
            FactionHQ hq = entry.Key;
            OperationsState state = entry.Value;
            for (int i = 0; i < state.Platoons.Count; i++)
            {
                DrawPlatoonMarker(camera, localHq, hq, state.Platoons[i], fullscreenMap, anyMap, map);
            }

            // Release points are shown for the local faction's own attacks only — the player is not
            // told in advance where the enemy will stop.
            if (ReferenceEquals(hq, localHq))
            {
                DrawReleaseCrosses(camera, state, fullscreenMap, anyMap, map);
            }
        }
    }

    private static void DrawPlatoonMarker(
        Camera camera,
        FactionHQ? localHq,
        FactionHQ hq,
        CommanderPlatoon platoon,
        bool fullscreenMap,
        bool anyMap,
        CommanderTacticalMapService? map)
    {
        Unit? leader = platoon.Leader;
        // A leader the sweep has already removed (player order, faction change) is drawn by nobody:
        // one vehicle carrying the markers of three platoons it used to lead is the bug this guards.
        if (leader == null || leader.disabled || platoon.Members.Count == 0 || !platoon.Members.Contains(leader))
        {
            return;
        }

        bool isLocal = ReferenceEquals(hq, localHq);
        // An enemy platoon is drawn only where its leader is actually tracked — the same 8 s
        // tracking test every other commander marker in the mod uses.
        if (!isLocal && !CommanderGameAccess.ShouldRetainCommanderMarker(leader, localHq))
        {
            return;
        }

        Color color = isLocal ? CommanderGameAccess.GetFriendlyColor() : CommanderGameAccess.GetHostileColor();
        string label = $"{platoon.Name} {platoon.Members.Count}/{platoon.Establishment} {GetStateLabel(platoon.State)}";

        if (!fullscreenMap)
        {
            Vector3 screen = camera.WorldToScreenPoint(leader.transform.position);
            if (screen.z > 0f && screen.x >= 0f && screen.x <= Screen.width && screen.y >= 0f && screen.y <= Screen.height)
            {
                Vector2 guiPoint = CommanderUiScale.ScreenToGui(screen);
                DrawDotMarker(guiPoint, MarkerWorldSize, color);
                CommanderUiTheme.DrawWorldLabel(new Vector2(guiPoint.x, guiPoint.y - MarkerWorldSize * 0.5f - 2f), label, color);
            }
        }

        if (anyMap && map != null && map.TryWorldToMapScreen(leader.transform.GlobalPosition(), out Vector2 mapPoint))
        {
            Vector2 guiPoint = CommanderUiScale.ScreenToGui(mapPoint);
            DrawDotMarker(guiPoint, MarkerMapSize, color);
            CommanderUiTheme.DrawWorldLabel(new Vector2(guiPoint.x, guiPoint.y - MarkerMapSize * 0.5f - 2f), label, color);
        }
    }

    private static void DrawReleaseCrosses(
        Camera camera, OperationsState state, bool fullscreenMap, bool anyMap, CommanderTacticalMapService? map)
    {
        Color color = CommanderGameAccess.GetFriendlyColor();
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind != CommanderMissionKind.Attack)
            {
                continue;
            }

            for (int a = 0; a < mission.Axes.Count; a++)
            {
                GlobalPosition release = mission.Axes[a].ReleasePoint;
                if (!fullscreenMap)
                {
                    Vector3 screen = camera.WorldToScreenPoint(release.ToLocalPosition());
                    if (screen.z > 0f && screen.x >= 0f && screen.x <= Screen.width && screen.y >= 0f && screen.y <= Screen.height)
                    {
                        DrawCross(CommanderUiScale.ScreenToGui(screen), color);
                    }
                }

                if (anyMap && map != null && map.TryWorldToMapScreen(release, out Vector2 mapPoint))
                {
                    DrawCross(CommanderUiScale.ScreenToGui(mapPoint), color);
                }
            }
        }
    }

    /// <summary>A filled dot with a dark rim — the <c>CommanderStrategicPointMarkers.DrawDot</c>
    /// shape, without the contested-alternation a platoon marker has no use for.</summary>
    private static void DrawDotMarker(Vector2 center, float size, Color color)
    {
        float dot = Mathf.Max(6f, size / 3f);
        float half = dot * 0.5f;
        Color previous = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.85f * color.a);
        CommanderUiTheme.Bar(center.x - half - 1f, center.y - half - 1f, dot + 2f, dot + 2f);
        GUI.color = color;
        CommanderUiTheme.Bar(center.x - half, center.y - half, dot, dot);
        GUI.color = previous;
    }

    /// <summary>An axis-aligned cross only — rotated bars do not survive the UI-scale matrix
    /// (<c>Points/CommanderStrategicPointMarkers.cs:203-209</c>'s own remark).</summary>
    private static void DrawCross(Vector2 center, Color color)
    {
        const float size = 10f;
        const float thickness = 2f;
        Color previous = GUI.color;
        GUI.color = color;
        CommanderUiTheme.Bar(center.x - size * 0.5f, center.y - thickness * 0.5f, size, thickness);
        CommanderUiTheme.Bar(center.x - thickness * 0.5f, center.y - size * 0.5f, thickness, size);
        GUI.color = previous;
    }

    private static string GetStateLabel(CommanderPlatoonState state)
    {
        return state switch
        {
            CommanderPlatoonState.Forming => "FORMING",
            CommanderPlatoonState.Moving => "MOVING",
            CommanderPlatoonState.Holding => "HOLDING",
            CommanderPlatoonState.Attacking => "ATTACKING",
            CommanderPlatoonState.Withdrawing => "WITHDRAWING",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// True while <paramref name="point"/> carries a live forward-base mission for
    /// <paramref name="localHq"/> — the point marker's " FOB" tag reads this (ledger row 27 addendum,
    /// <c>Points/CommanderStrategicPointMarkers.cs</c>). Restricted to the local faction, the same
    /// way <see cref="DrawReleaseCrosses"/> only ever draws the local faction's release points: an
    /// enemy forward base is not something the player is told about in advance.
    /// </summary>
    internal bool IsForwardBase(CommanderStrategicPoint point, FactionHQ? localHq)
    {
        if (localHq == null || !states.TryGetValue(localHq, out OperationsState state))
        {
            return false;
        }

        List<CommanderOperationsMission> missions = state.Missions;
        for (int i = 0; i < missions.Count; i++)
        {
            if (missions[i].Kind == CommanderMissionKind.ForwardBase && ReferenceEquals(missions[i].Point, point))
            {
                return true;
            }
        }

        return false;
    }
}
