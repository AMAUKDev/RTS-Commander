using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderWorldMarkerRenderer
{
    private readonly CommanderSelectionService selectionService;
    private readonly CommanderMoveService moveService;
    private readonly CommanderSpawnService spawnService;
    private readonly CommanderSupplyHeliService supplyHeliService;
    private readonly CommanderSamSiteAnalyzerService samSiteAnalyzerService;
    private readonly CommanderSamSiteService samSiteService;
    private readonly List<GlobalPosition> deliveryTargets = new();
    private readonly List<GlobalPosition> supplyRoute = new();
    private readonly List<GlobalPosition> routePoints = new();
    private readonly List<CommanderSamSiteAnalyzerService.SiteLayoutMarker> samSiteLayout = new();
    private readonly List<CommanderSamSiteAnalyzerService.SiteCandidate> samSiteProposals = new();

    internal CommanderWorldMarkerRenderer(
        CommanderSelectionService selectionService,
        CommanderMoveService moveService,
        CommanderSpawnService spawnService,
        CommanderSupplyHeliService supplyHeliService,
        CommanderSamSiteAnalyzerService samSiteAnalyzerService,
        CommanderSamSiteService samSiteService)
    {
        this.selectionService = selectionService;
        this.moveService = moveService;
        this.spawnService = spawnService;
        this.supplyHeliService = supplyHeliService;
        this.samSiteAnalyzerService = samSiteAnalyzerService;
        this.samSiteService = samSiteService;
    }

    internal void Draw(bool supplyWindowVisible)
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return;
        }

        CommanderOrderPing.Draw(camera);

        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (DrawOrderRoute(camera, unit))
            {
                continue;
            }

            if (moveService.TryGetPlayerDestination(unit, out GlobalPosition destination))
            {
                DrawMarker(camera, destination, "MOVE", new Color(0.2f, 0.85f, 0.82f, 0.9f));
            }
        }

        if (spawnService.SelectedDepot != null && spawnService.TryGetSelectedRallyPoint(out GlobalPosition rallyPoint))
        {
            DrawMarker(camera, rallyPoint, "RALLY", new Color(0.95f, 0.78f, 0.22f, 0.9f));
        }

        if (supplyHeliService.AwaitingTargetSelection)
        {
            DrawCursorMarker("LZ", new Color(0.35f, 0.9f, 0.42f, 0.95f));
        }

        samSiteAnalyzerService.CopyProposalSites(samSiteProposals);
        for (int i = 0; i < samSiteProposals.Count; i++)
        {
            DrawLargeMarker(
                camera,
                samSiteProposals[i].Position,
                $"SAM SITE {i + 1}",
                new Color(0.1f, 0.82f, 1f, 0.95f));
        }

        samSiteAnalyzerService.CopyVisibleActiveLayout(samSiteLayout);
        for (int i = 0; i < samSiteLayout.Count; i++)
        {
            CommanderSamSiteAnalyzerService.SiteLayoutMarker marker = samSiteLayout[i];
            if (marker.Role == CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower)
            {
                continue;
            }
            DrawMarker(camera, marker.Position, GetSamLabel(marker.Role), GetSamColor(marker.Role));
        }

        samSiteService.CopyVisibleSupplyRoute(supplyRoute);
        for (int i = 0; i < supplyRoute.Count; i++)
        {
            string label = i == 0
                ? "AIRBASE"
                : i == supplyRoute.Count - 1 ? "SAM SITE" : $"ROUTE {i}";
            DrawMarker(camera, supplyRoute[i], label, new Color(0.2f, 0.78f, 1f, 0.92f));
        }
        if (!supplyWindowVisible)
        {
            return;
        }

        supplyHeliService.CopyActiveDeliveryTargets(deliveryTargets);
        for (int i = 0; i < deliveryTargets.Count; i++)
        {
            DrawMarker(camera, deliveryTargets[i], "LZ", new Color(0.35f, 0.9f, 0.42f, 0.9f));
        }
    }

    /// <summary>
    /// Draws the remaining travel points of a RTS order in the 3D view, plus the attack marker
    /// when the route ends on a target. While the map is up the route lines come from
    /// <see cref="CommanderMapRouteRenderer"/> instead — camera-projected lines drawn over an open
    /// map sweep across it as the camera turns, which is what made routes look wrong there — so
    /// only the numbered point labels are drawn on the map.
    /// </summary>
    private bool DrawOrderRoute(Camera camera, Unit unit)
    {
        IReadOnlyList<GlobalPosition> waypoints;
        int index;
        Unit? attackTarget;
        if (unit is Aircraft aircraft)
        {
            if (CommanderAirCommandService.Instance?.TryGetAircraftRoute(
                    aircraft, out waypoints, out index, out attackTarget) != true)
            {
                return false;
            }
        }
        else if (!moveService.TryGetOrder(unit, out waypoints, out index, out attackTarget))
        {
            return false;
        }

        routePoints.Clear();
        // The leg the unit is flying or driving right now starts at the unit, so the whole
        // plan reads as one unbroken line from the unit through every remaining point.
        routePoints.Add(unit.GlobalPosition());
        for (int i = Mathf.Max(0, index); i < waypoints.Count; i++)
        {
            routePoints.Add(waypoints[i]);
        }
        if (attackTarget != null && !attackTarget.disabled)
        {
            routePoints.Add(attackTarget.GlobalPosition());
        }

        if (routePoints.Count < 2)
        {
            return false;
        }

        Color routeColor = attackTarget != null
            ? new Color(1f, 0.42f, 0.24f, 0.9f)
            : new Color(0.30f, 0.92f, 0.80f, 0.9f);
        bool mapOpen = DynamicMap.mapMaximized;
        if (!mapOpen)
        {
            DrawRouteLines(camera, routeColor);
        }

        // Passed points still count, so the labels match the numbering the player placed.
        CommanderTacticalMapService? map = CommanderTacticalMapService.Instance;
        for (int i = 1; i < routePoints.Count; i++)
        {
            bool attackPoint = i == routePoints.Count - 1 && attackTarget != null;
            string label = attackPoint ? "ATTACK" : (index + i).ToString();
            // The attack bracket is sized to frame the target it is aimed at; travel points
            // stay small so a long route does not clutter the view.
            float size = attackPoint ? 48f : 14f;
            if (!mapOpen)
            {
                DrawRoutePoint(camera, routePoints[i], label, routeColor, size);
            }
            else if (map != null && map.TryWorldToMapScreen(routePoints[i], out Vector2 mapPoint))
            {
                CommanderUiTheme.DrawWorldMarker(
                    CommanderUiScale.ScreenToGui(mapPoint), label, routeColor, attackPoint ? 20f : 14f);
            }
        }
        return true;
    }

    private void DrawRouteLines(Camera camera, Color color)
    {
        for (int i = 1; i < routePoints.Count; i++)
        {
            if (TryGetRouteSegment(camera, routePoints[i - 1], routePoints[i], out Vector2 from, out Vector2 to))
            {
                CommanderUiTheme.DrawLine(from, to, color, 2f);
            }
        }
    }

    /// <summary>
    /// Projects one route leg into GUI space. A point behind the camera projects mirrored, so
    /// the leg is clipped against the camera plane instead of being dropped: dropping it is
    /// what made routes look like disconnected fragments whenever a waypoint went off screen.
    /// </summary>
    private static bool TryGetRouteSegment(
        Camera camera,
        GlobalPosition start,
        GlobalPosition end,
        out Vector2 from,
        out Vector2 to)
    {
        from = default;
        to = default;
        Vector3 a = start.ToLocalPosition();
        Vector3 b = end.ToLocalPosition();
        float aDepth = camera.transform.InverseTransformPoint(a).z;
        float bDepth = camera.transform.InverseTransformPoint(b).z;
        const float NearClip = 1f;

        if (aDepth < NearClip && bDepth < NearClip)
        {
            return false;
        }

        if (aDepth < NearClip)
        {
            a = Vector3.Lerp(a, b, (NearClip - aDepth) / (bDepth - aDepth));
        }
        else if (bDepth < NearClip)
        {
            b = Vector3.Lerp(b, a, (NearClip - bDepth) / (aDepth - bDepth));
        }

        from = CommanderUiScale.ScreenToGui(camera.WorldToScreenPoint(a));
        to = CommanderUiScale.ScreenToGui(camera.WorldToScreenPoint(b));
        return true;
    }

    /// <summary>
    /// Travel point: an open bracket with its number above it. Points off screen are clamped
    /// to the screen edge so the route still tells you which way it goes.
    /// </summary>
    private static void DrawRoutePoint(Camera camera, GlobalPosition position, string label, Color color, float size)
    {
        Vector3 screen = camera.WorldToScreenPoint(position.ToLocalPosition());
        if (screen.z <= 0f)
        {
            return;
        }

        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screen);
        // Clamped to the screen so a route that runs off the edge still shows which way it goes.
        guiPoint.x = Mathf.Clamp(guiPoint.x, 16f, Mathf.Max(16f, CommanderUiScale.Width - 16f));
        guiPoint.y = Mathf.Clamp(guiPoint.y, 16f, Mathf.Max(16f, CommanderUiScale.Height - 12f));
        CommanderUiTheme.DrawWorldMarker(guiPoint, label, color, size);
    }

    private static string GetSamLabel(CommanderSamSiteAnalyzerService.SiteUnitRole role)
    {
        return role switch
        {
            CommanderSamSiteAnalyzerService.SiteUnitRole.Radar => "RADAR",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Platform => "PLATFORM",
            CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower => "SITE CORE",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm => "23MM",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Irm => "IRM",
            CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher => "STRATOLANCE",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo => "AMMO",
            CommanderSamSiteAnalyzerService.SiteUnitRole.FireControl => "FIRE CTRL",
            _ => "SITE"
        };
    }

    private static Color GetSamColor(CommanderSamSiteAnalyzerService.SiteUnitRole role)
    {
        return role switch
        {
            CommanderSamSiteAnalyzerService.SiteUnitRole.Radar => new Color(1f, 0.78f, 0.12f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.Platform => new Color(0.72f, 0.7f, 1f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower => new Color(0.6f, 0.78f, 1f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm => new Color(1f, 0.46f, 0.12f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.Irm => new Color(1f, 0.2f, 0.08f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher => new Color(0.95f, 0.12f, 0.12f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo => new Color(0.35f, 1f, 0.35f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.FireControl => new Color(0.1f, 0.9f, 1f, 0.95f),
            _ => Color.white
        };
    }

    /// <summary>Bracket parked below the cursor, so the cursor and what it is over stay clear.</summary>
    private static void DrawCursorMarker(string label, Color color)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(Input.mousePosition);
        CommanderUiTheme.DrawWorldMarker(new Vector2(guiPoint.x, guiPoint.y + 30f), label, color, 22f);
    }

    private static void DrawMarker(Camera camera, GlobalPosition position, string label, Color color)
    {
        DrawWorldPoint(camera, position, label, color, 26f);
    }

    private static void DrawLargeMarker(Camera camera, GlobalPosition position, string label, Color color)
    {
        DrawWorldPoint(camera, position, label, color, 44f);
    }

    private static void DrawWorldPoint(Camera camera, GlobalPosition position, string label, Color color, float size)
    {
        Vector3 screen = camera.WorldToScreenPoint(position.ToLocalPosition());
        if (screen.z <= 0f || screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height)
        {
            return;
        }

        CommanderUiTheme.DrawWorldMarker(CommanderUiScale.ScreenToGui(screen), label, color, size);
    }
}
