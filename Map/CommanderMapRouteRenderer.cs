using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GroundControlRts;

/// <summary>
/// Draws the route of every selected unit onto the tactical map as real map objects, cloned from
/// the game's own waypoint line and parented to its icon layer.
/// <para>
/// The route used to be painted with IMGUI lines on top of the map instead. Those were projected
/// once per frame from screen coordinates, so they sat above the map rather than in it: panning,
/// zooming or resizing the map slid them off the terrain they belonged to, and a leg that left the
/// map rectangle was dropped outright. Living in the icon layer means the map's own transform pans,
/// zooms and clips them exactly like unit icons.
/// </para>
/// </summary>
internal sealed class CommanderMapRouteRenderer : ICommanderTickActive, ICommanderDeactivate, ICommanderResetSession
{
    /// <summary>On-screen line width, held constant as the map zooms.</summary>
    private const float LineWidthPixels = 3f;

    private static readonly Color MoveColor = new(0.30f, 0.92f, 0.80f, 0.9f);
    private static readonly Color AttackColor = new(1f, 0.42f, 0.24f, 0.9f);

    private readonly CommanderSelectionService selectionService;
    private readonly CommanderMoveService moveService;
    private readonly List<Segment> segments = new();
    private readonly List<GlobalPosition> routePoints = new();
    private DynamicMap? boundMap;
    private int used;

    internal CommanderMapRouteRenderer(CommanderSelectionService selectionService, CommanderMoveService moveService)
    {
        this.selectionService = selectionService;
        this.moveService = moveService;
    }

    public void TickActive()
    {
        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        if (map == null || !DynamicMap.mapMaximized || map.iconLayer == null || map.mapWaypointVector == null)
        {
            HideFrom(0);
            return;
        }

        // The icon layer is rebuilt with the map, so segments cloned under the old one are gone.
        if (!ReferenceEquals(boundMap, map))
        {
            Clear();
            boundMap = map;
        }

        used = 0;
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            DrawRoute(map, selectionService.SelectedUnits[i]);
        }
        HideFrom(used);
    }

    public void Deactivate() => HideFrom(0);

    public void ResetSession() => Clear();

    private void DrawRoute(DynamicMap map, Unit unit)
    {
        IReadOnlyList<GlobalPosition> waypoints;
        int index;
        Unit? attackTarget;
        if (unit is Aircraft aircraft)
        {
            if (CommanderAirCommandService.Instance?.TryGetAircraftRoute(
                    aircraft, out waypoints, out index, out attackTarget) != true)
            {
                return;
            }
        }
        else if (!moveService.TryGetOrder(unit, out waypoints, out index, out attackTarget))
        {
            return;
        }

        // Same shape as the 3D view: the leg in progress starts at the unit, so the plan reads as
        // one unbroken line from the unit through every point it has left.
        routePoints.Clear();
        routePoints.Add(unit.GlobalPosition());
        for (int i = Mathf.Max(0, index); i < waypoints.Count; i++)
        {
            routePoints.Add(waypoints[i]);
        }
        if (attackTarget != null && !attackTarget.disabled)
        {
            routePoints.Add(attackTarget.GlobalPosition());
        }

        Color color = attackTarget != null ? AttackColor : MoveColor;
        for (int i = 1; i < routePoints.Count; i++)
        {
            PlaceSegment(map, routePoints[i - 1], routePoints[i], color);
        }
    }

    /// <summary>
    /// Positions one leg the way <c>MapWaypoint.PlaceMarker</c> does: the line object sits on the
    /// later point, rotated back towards the earlier one, with its length in icon-layer units.
    /// </summary>
    private void PlaceSegment(DynamicMap map, GlobalPosition from, GlobalPosition to, Color color)
    {
        Segment? segment = Take(map);
        if (segment == null)
        {
            return;
        }

        Vector2 start = new Vector2(from.x, from.z) * map.mapDisplayFactor;
        Vector2 end = new Vector2(to.x, to.z) * map.mapDisplayFactor;
        Vector2 delta = end - start;
        float length = delta.magnitude;
        if (length < 0.001f)
        {
            segment.SetVisible(false);
            return;
        }

        float width = LineWidthPixels / Mathf.Max(0.0001f, map.iconLayer.transform.lossyScale.x);
        Transform transform = segment.Root.transform;
        transform.localPosition = new Vector3(end.x, end.y, 0f);
        transform.localEulerAngles = new Vector3(0f, 0f, -Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg + 180f);
        transform.localScale = new Vector3(width, length, width);
        segment.SetColor(color);
        segment.SetVisible(true);
    }

    private Segment? Take(DynamicMap map)
    {
        while (used < segments.Count && segments[used].Root == null)
        {
            segments.RemoveAt(used);
        }

        if (used < segments.Count)
        {
            return segments[used++];
        }

        GameObject root = Object.Instantiate(map.mapWaypointVector, map.iconLayer.transform);
        root.name = "GroundControl Route Leg";
        Segment segment = new(root);
        segments.Add(segment);
        used++;
        return segment;
    }

    private void HideFrom(int first)
    {
        for (int i = first; i < segments.Count; i++)
        {
            segments[i].SetVisible(false);
        }
    }

    private void Clear()
    {
        for (int i = 0; i < segments.Count; i++)
        {
            segments[i].Destroy();
        }
        segments.Clear();
        boundMap = null;
        used = 0;
    }

    private sealed class Segment
    {
        private readonly Image[] images;
        private Color appliedColor;
        private bool visible = true;

        internal Segment(GameObject root)
        {
            Root = root;
            images = root.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                images[i].raycastTarget = false;
            }
        }

        internal GameObject Root { get; }

        internal void SetColor(Color color)
        {
            if (appliedColor == color)
            {
                return;
            }

            appliedColor = color;
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i] != null)
                {
                    images[i].color = color;
                }
            }
        }

        internal void SetVisible(bool value)
        {
            if (visible == value || Root == null)
            {
                return;
            }

            visible = value;
            Root.SetActive(value);
        }

        internal void Destroy()
        {
            if (Root != null)
            {
                Object.Destroy(Root);
            }
        }
    }
}
