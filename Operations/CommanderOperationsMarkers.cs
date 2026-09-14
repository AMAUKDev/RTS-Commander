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

        // Every aircraft on the map, whether or not a commander is managing its faction yet
        // (design.md, smarter-air-wing_20260914 Section 14). This runs FIRST and needs no state, so
        // aircraft carry labels from the first frame of the mission instead of from the first
        // 30-second review; the per-commander walk below then adds the specific ones.
        DrawAllAircraftMarkers(camera, localHq, fullscreenMap, anyMap, map);

        foreach (KeyValuePair<FactionHQ, OperationsState> entry in states)
        {
            FactionHQ hq = entry.Key;
            OperationsState state = entry.Value;
            for (int i = 0; i < state.Platoons.Count; i++)
            {
                DrawPlatoonMarker(camera, localHq, hq, state, state.Platoons[i], fullscreenMap, anyMap, map);
            }

            // Picket detachments and forward-base munitions trucks (user 2026-09-14: "we need
            // markers on pickets too"). Neither is a platoon, so neither was drawn by the loop
            // above — a rear crossroads held by two vehicles had nothing on screen at all.
            DrawDetachmentMarkers(camera, localHq, hq, state, fullscreenMap, anyMap, map);

            // Air sorties and packages, in Operations/CommanderOperationsAirMarkers.cs (design.md,
            // smarter-air-wing_20260914 Section 11) — the same dot, label helper and tracking test.
            DrawAirMarkers(camera, localHq, hq, state, fullscreenMap, anyMap, map);

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
        OperationsState state,
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
        DrawLabelledMarker(
            camera,
            leader.transform.GlobalPosition(),
            PlatoonMarkerLabel(hq, state, platoon),
            color,
            fullscreenMap,
            anyMap,
            map);
    }

    /// <summary>
    /// One dot with its label above it, in the world view and on the map, with the fullscreen-map
    /// hide every commander marker obeys. Extracted from <see cref="DrawPlatoonMarker"/> when the
    /// picket and truck markers became the second and third callers of the identical body (Reuse
    /// rule 5); the platoon marker now goes through it unchanged, since a unit's
    /// <c>transform.position</c> and its <c>GlobalPosition().ToLocalPosition()</c> are the same
    /// place by construction.
    /// </summary>
    private static void DrawLabelledMarker(
        Camera camera,
        GlobalPosition position,
        string label,
        Color color,
        bool fullscreenMap,
        bool anyMap,
        CommanderTacticalMapService? map)
    {
        if (!fullscreenMap)
        {
            Vector3 screen = camera.WorldToScreenPoint(position.ToLocalPosition());
            if (screen.z > 0f && screen.x >= 0f && screen.x <= Screen.width && screen.y >= 0f && screen.y <= Screen.height)
            {
                Vector2 guiPoint = CommanderUiScale.ScreenToGui(screen);
                DrawDotMarker(guiPoint, MarkerWorldSize, color);
                CommanderUiTheme.DrawWorldLabel(new Vector2(guiPoint.x, guiPoint.y - MarkerWorldSize * 0.5f - 2f), label, color);
            }
        }

        if (anyMap && map != null && map.TryWorldToMapScreen(position, out Vector2 mapPoint))
        {
            Vector2 guiPoint = CommanderUiScale.ScreenToGui(mapPoint);
            DrawDotMarker(guiPoint, MarkerMapSize, color);
            CommanderUiTheme.DrawWorldLabel(new Vector2(guiPoint.x, guiPoint.y - MarkerMapSize * 0.5f - 2f), label, color);
        }
    }

    /// <summary>
    /// How long after a picket's last air-dropped vehicle its marker still reads
    /// <c>Dropped, taking posts</c>: the strategic points' own hold window
    /// (<c>CommanderSettings.PointsHoldSeconds</c>, 60 s by default). The same 60 s the point needs
    /// its garrison standing in the ring before it changes hands, so the marker stops saying the
    /// detachment is still sorting itself out exactly when the point would have started paying.
    /// Derived rather than retyped so a retune of the hold rule moves both together.
    /// </summary>
    private static float PicketDropWindowSeconds => CommanderSettings.PointsHoldSeconds;

    /// <summary>
    /// The two detachments that are not platoons and so were drawn by nobody: a picket's pair of
    /// vehicles and a forward base's munitions truck (user 2026-09-14: "we need markers on pickets
    /// too"). Same dot, same label style, same colours, same world-vs-map split and the same 8 s
    /// tracking test on anything that is not ours.
    /// </summary>
    private void DrawDetachmentMarkers(
        Camera camera,
        FactionHQ? localHq,
        FactionHQ hq,
        OperationsState state,
        bool fullscreenMap,
        bool anyMap,
        CommanderTacticalMapService? map)
    {
        bool isLocal = ReferenceEquals(hq, localHq);
        Color color = isLocal ? CommanderGameAccess.GetFriendlyColor() : CommanderGameAccess.GetHostileColor();
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.Picket)
            {
                DrawPicketMarker(camera, localHq, state, mission, isLocal, color, fullscreenMap, anyMap, map);
            }

            if (mission.Truck != null)
            {
                DrawTruckMarker(camera, localHq, hq, mission, isLocal, color, fullscreenMap, anyMap, map);
            }
        }
    }

    /// <summary>
    /// One marker per picket detachment, at its first live member — or at the point itself while a
    /// flight is still bringing the vehicles in, since there is nothing on the ground to hang it
    /// on. A picket with no member and no insertion bound to it gets nothing: an empty mission
    /// record is bookkeeping, not a unit on the map.
    /// </summary>
    private static void DrawPicketMarker(
        Camera camera,
        FactionHQ? localHq,
        OperationsState state,
        CommanderOperationsMission mission,
        bool isLocal,
        Color color,
        bool fullscreenMap,
        bool anyMap,
        CommanderTacticalMapService? map)
    {
        CommanderStrategicPoint? point = mission.Point;
        if (point == null)
        {
            return;
        }

        Unit? anchor = FirstLiveMember(mission.PicketMembers);
        bool awaitingInsertion = anchor == null && IsBoundToInsertion(state, point);
        if (anchor == null && !awaitingInsertion)
        {
            return;
        }

        // The same tracking test every other commander marker uses. An enemy picket whose vehicles
        // are not being tracked is not drawn at all, and an enemy insertion the player cannot see
        // is never announced by the point lighting up on its own.
        if (!isLocal && (anchor == null || !CommanderGameAccess.ShouldRetainCommanderMarker(anchor, localHq)))
        {
            return;
        }

        int live = CountLiveMembers(mission.PicketMembers);
        FindSortie(state, null, mission, out bool casShort, out bool _);
        string? label = PicketMarkerText(
            pointLabel: point.Label.Length > 0 ? point.Label : mission.Label,
            liveMembers: live,
            wanted: CommanderSettings.PointsMinGarrison,
            awaitingInsertion: awaitingInsertion,
            droppedRecently: mission.LastDropAt >= 0f && Time.time - mission.LastDropAt <= PicketDropWindowSeconds,
            holding: live > 0 && CountInsidePoint(point, mission.PicketMembers) == live,
            inContact: mission.ContactUntil >= Time.time,
            requestingCas: casShort);
        if (label == null)
        {
            return;
        }

        GlobalPosition where = anchor != null ? anchor.transform.GlobalPosition() : point.Position;
        DrawLabelledMarker(camera, where, label, color, fullscreenMap, anyMap, map);
    }

    /// <summary>One marker per forward-base munitions truck, at the truck (user 2026-09-14: the
    /// trucks "have no marker" either). A truck is a single vehicle with no establishment, so its
    /// label carries no <c>n/m</c> and no flags — only where it is going and whether it is there.</summary>
    private static void DrawTruckMarker(
        Camera camera,
        FactionHQ? localHq,
        FactionHQ hq,
        CommanderOperationsMission mission,
        bool isLocal,
        Color color,
        bool fullscreenMap,
        bool anyMap,
        CommanderTacticalMapService? map)
    {
        Unit? truck = mission.Truck;
        if (truck == null || truck.disabled)
        {
            return;
        }

        if (!isLocal && !CommanderGameAccess.ShouldRetainCommanderMarker(truck, localHq))
        {
            return;
        }

        CommanderStrategicPoint? point = mission.Point;
        // "Returning" is the truck with nowhere left to park: the point it was supplying has gone,
        // or has changed hands while it was still on the road. The next review hands it back to the
        // pool; until then it is driving away from a post that is no longer ours.
        bool returning = point == null || PointIsLostTo(hq, point);
        bool supplying = !returning
            && point != null
            && FastMath.InRange(truck.transform.GlobalPosition(), point.Position, point.Radius);
        string label = TruckMarkerText(
            pointLabel: point != null && point.Label.Length > 0 ? point.Label : mission.Label,
            returning: returning,
            supplying: supplying);
        DrawLabelledMarker(camera, truck.transform.GlobalPosition(), label, color, fullscreenMap, anyMap, map);
    }

    /// <summary>True when <paramref name="point"/> has an owner and that owner is somebody other
    /// than <paramref name="hq"/> — the same "no longer ours to hold" test
    /// <c>PruneInsertions</c> recalls a flight on.</summary>
    private static bool PointIsLostTo(FactionHQ hq, CommanderStrategicPoint point)
    {
        FactionHQ? owner = point.GetOwner();
        return owner != null && !ReferenceEquals(owner, hq);
    }

    /// <summary>The first vehicle of a detachment still alive, or null when none is.</summary>
    private static Unit? FirstLiveMember(IReadOnlyList<Unit> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            Unit unit = members[i];
            if (unit != null && !unit.disabled)
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>How many of a detachment's vehicles are still alive — the <c>n</c> of its
    /// <c>n/m</c>.</summary>
    private static int CountLiveMembers(IReadOnlyList<Unit> members)
    {
        int count = 0;
        for (int i = 0; i < members.Count; i++)
        {
            Unit unit = members[i];
            if (unit != null && !unit.disabled)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The whole picket marker text: <c>PICKET &lt;point label&gt; n/2 — &lt;situation&gt;[ ·
    /// flags]</c> — e.g. <c>PICKET Crossroads 13 1/2 — Holding Crossroads 13 · In contact ·
    /// Requesting CAS · Under strength</c>. Null for a picket with nothing on the ground and no
    /// flight bringing anything, which is drawn by nobody. Pure, for the self-check.
    /// </summary>
    internal static string? PicketMarkerText(
        string pointLabel,
        int liveMembers,
        int wanted,
        bool awaitingInsertion,
        bool droppedRecently,
        bool holding,
        bool inContact,
        bool requestingCas)
    {
        if (liveMembers <= 0 && !awaitingInsertion)
        {
            return null;
        }

        return $"PICKET {pointLabel} {liveMembers}/{wanted} — "
            + PicketSituation(liveMembers, awaitingInsertion, droppedRecently, holding, pointLabel)
            + MarkerFlags(
                inContact: inContact,
                requestingCas: requestingCas,
                casOverhead: false,
                requestingReinforcements: false,
                reinforcingLabel: string.Empty,
                underStrength: liveMembers < wanted);
    }

    /// <summary>
    /// A picket detachment's situation text, in priority order: a flight still inbound with nothing
    /// on the ground says so; vehicles that have just rolled off the ramp are taking their posts
    /// even though they are already standing inside the ring; a detachment whose whole live
    /// strength is inside the point's radius is holding it; anything else is still on the road.
    /// Pure, for the self-check.
    /// </summary>
    internal static string PicketSituation(
        int liveMembers, bool awaitingInsertion, bool droppedRecently, bool holding, string pointLabel)
    {
        if (liveMembers <= 0)
        {
            return awaitingInsertion ? "Awaiting insertion" : string.Empty;
        }

        if (droppedRecently)
        {
            return "Dropped, taking posts";
        }

        return holding ? $"Holding {pointLabel}" : $"Moving to {pointLabel}";
    }

    /// <summary>The whole forward-base truck marker text: <c>TRUCK &lt;point label&gt; —
    /// &lt;situation&gt;</c>. Pure, for the self-check.</summary>
    internal static string TruckMarkerText(string pointLabel, bool returning, bool supplying)
    {
        return $"TRUCK {pointLabel} — " + TruckSituation(returning, supplying, pointLabel);
    }

    /// <summary>A munitions truck's situation text: driving out to its forward base, parked inside
    /// its ring keeping the platoon in ammunition, or coming home from a post that is no longer
    /// ours. Pure, for the self-check.</summary>
    internal static string TruckSituation(bool returning, bool supplying, string pointLabel)
    {
        if (returning)
        {
            return "Returning";
        }

        return supplying ? $"Supplying {pointLabel}" : $"Moving to {pointLabel}";
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

    /// <summary>
    /// The whole platoon marker text (addendum 2026-09-14 §1):
    /// <c>&lt;NAME&gt; n/m — &lt;situation&gt;[ · flags]</c> — e.g.
    /// <c>2ND PLATOON 5/6 — Holding Hilltop 12 · In contact · Requesting CAS</c>. The situation
    /// comes from <see cref="MarkerSituation"/> and the flags from <see cref="MarkerFlags"/>; both
    /// are pure so the self-check can drive them.
    /// </summary>
    private static string PlatoonMarkerLabel(FactionHQ hq, OperationsState state, CommanderPlatoon platoon)
    {
        bool isReserve = platoon.State == CommanderPlatoonState.Holding
            && platoon.Mission?.Kind == CommanderMissionKind.Reserve;
        string label = $"{platoon.Name} {platoon.Members.Count}/{platoon.Establishment} — "
            + MarkerSituation(platoon.State, platoon.Posture, isReserve, SituationPlaceLabel(hq, platoon));

        FindSortie(state, platoon, null, out bool casShort, out bool casOverhead);
        return label + MarkerFlags(
            inContact: platoon.InContactUntil >= Time.time,
            requestingCas: casShort,
            casOverhead: casOverhead,
            requestingReinforcements: platoon.Mission != null && platoon.Mission.ReinforcePlatoons > 0,
            reinforcingLabel: platoon.ReinforcesLabel);
    }

    /// <summary>
    /// The situation text for each state (addendum 2026-09-14 §1's table, exactly): where a
    /// forming platoon gathers, where a march is going, which point a garrison holds (a reserve
    /// names its base instead), what an attack is at, and where a withdrawing platoon falls back
    /// to. Pure, for the self-check.
    /// <para>
    /// A platoon deployed in one of the ground-tactics postures (ground-tactics design §5) says
    /// what it is doing on the ground instead, since that is the more specific answer: a garrison
    /// on its defence arc reads <c>Defence line at &lt;label&gt;</c> rather than
    /// <c>Holding &lt;label&gt;</c>. <see cref="CommanderGroundPosture.Ring"/> — every platoon that
    /// is not manoeuvring — reads exactly as it always did.
    /// </para>
    /// </summary>
    internal static string MarkerSituation(
        CommanderPlatoonState state, CommanderGroundPosture posture, bool isReserve, string label)
    {
        switch (posture)
        {
            case CommanderGroundPosture.DefenceArc:
                return $"Defence line at {label}";
            case CommanderGroundPosture.Bounding:
                return $"Bounding to {label}";
            case CommanderGroundPosture.CounterAttack:
                return "Counter-attacking from the flank";
            case CommanderGroundPosture.Screen:
                return $"Screening {label}";
        }

        return state switch
        {
            CommanderPlatoonState.Forming => $"Forming at {label}",
            CommanderPlatoonState.Moving => $"Moving to {label}",
            CommanderPlatoonState.Holding => isReserve ? $"Reserve at {label}" : $"Holding {label}",
            CommanderPlatoonState.Attacking => $"Attacking {label}",
            CommanderPlatoonState.Withdrawing => $"Withdrawing to {label}",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// The marker's flags, appended in the addendum's fixed order (<c>In contact</c>,
    /// <c>Requesting CAS</c>, <c>CAS overhead</c>, <c>Requesting reinforcements</c>,
    /// <c>Reinforcing &lt;label&gt;</c>, <c>Under strength</c>) behind a <c> · </c> separator, or
    /// nothing when no flag is set. Pure, for the self-check.
    /// <para>
    /// <paramref name="underStrength"/> is the picket marker's own flag (picket/truck markers,
    /// 2026-09-14) and defaults to false, so this stayed one definition with two callers rather
    /// than forking a near-identical picket version (Reuse rule 4). A platoon already says its
    /// strength in its <c>n/m</c> and asks for bodies through <c>Requesting reinforcements</c>, so
    /// it never sets it.
    /// </para>
    /// </summary>
    internal static string MarkerFlags(
        bool inContact,
        bool requestingCas,
        bool casOverhead,
        bool requestingReinforcements,
        string reinforcingLabel,
        bool underStrength = false)
    {
        if (!inContact
            && !requestingCas
            && !casOverhead
            && !requestingReinforcements
            && reinforcingLabel.Length == 0
            && !underStrength)
        {
            return string.Empty;
        }

        System.Text.StringBuilder text = new();
        if (inContact)
        {
            text.Append(" · In contact");
        }

        if (requestingCas)
        {
            text.Append(" · Requesting CAS");
        }

        if (casOverhead)
        {
            text.Append(" · CAS overhead");
        }

        if (requestingReinforcements)
        {
            text.Append(" · Requesting reinforcements");
        }

        if (reinforcingLabel.Length > 0)
        {
            text.Append(" · Reinforcing ").Append(reinforcingLabel);
        }

        if (underStrength)
        {
            text.Append(" · Under strength");
        }

        return text.ToString();
    }

    /// <summary>
    /// The place label this platoon's situation text names: its own mission's point or target
    /// where it has one (the label the COMMANDER LOG already uses), otherwise the nearest point
    /// or base to wherever it stands — the addendum's "nearest point or base" for a forming
    /// platoon, "objective label" for a march it has no point for, and "<c>base</c>" for the
    /// reserve ring.
    /// </summary>
    private static string SituationPlaceLabel(FactionHQ hq, CommanderPlatoon platoon)
    {
        switch (platoon.State)
        {
            case CommanderPlatoonState.Holding:
                if (platoon.Mission?.Kind == CommanderMissionKind.Reserve)
                {
                    return NearestAirbaseLabel(hq, platoon.Objective);
                }

                return platoon.Mission != null && platoon.Mission.Label.Length > 0
                    ? platoon.Mission.Label
                    : NearestPlaceLabel(platoon.Objective);
            case CommanderPlatoonState.Moving:
                return platoon.Mission?.Point != null ? platoon.Mission.Point.Label : NearestPlaceLabel(platoon.Objective);
            case CommanderPlatoonState.Attacking:
                return platoon.Mission != null && platoon.Mission.Label.Length > 0
                    ? platoon.Mission.Label
                    : NearestPlaceLabel(platoon.Objective);
            default:
                // Forming has no point of its own yet; Withdrawing's rally point is a position,
                // not a label. Both name the nearest place to where they are going.
                return NearestPlaceLabel(platoon.Objective);
        }
    }

    /// <summary>
    /// The nearest strategic point or airbase label to <paramref name="position"/> — every point
    /// kind and every faction's bases count, because a place on the map is a place whoever holds
    /// it, and an enemy platoon's marker is drawn too. <c>"unknown"</c> only before discovery has
    /// anything to name.
    /// </summary>
    private static string NearestPlaceLabel(GlobalPosition position)
    {
        string best = "unknown";
        float bestDistance = float.MaxValue;
        IReadOnlyList<CommanderStrategicPoint>? points = CommanderStrategicPointService.Instance?.Points;
        if (points != null)
        {
            for (int i = 0; i < points.Count; i++)
            {
                CommanderStrategicPoint point = points[i];
                if (point.Label.Length == 0)
                {
                    continue;
                }

                float distance = CommanderGameAccess.HorizontalDistance(position.AsVector3(), point.Position.AsVector3());
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = point.Label;
                }
            }
        }

        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                position.AsVector3(), airbase.center.GlobalPosition().AsVector3());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = CommanderCaptureService.GetAirbaseLabel(airbase);
            }
        }

        return best;
    }

    /// <summary>The <c>Reserve at &lt;base&gt;</c> label: the base the reserve ring actually
    /// surrounds — this HQ's own nearest live airbase to <paramref name="position"/>, falling
    /// back to the nearest place of any kind when it holds none.</summary>
    private static string NearestAirbaseLabel(FactionHQ hq, GlobalPosition position)
    {
        string? best = null;
        float bestDistance = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                position.AsVector3(), airbase.center.GlobalPosition().AsVector3());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = CommanderCaptureService.GetAirbaseLabel(airbase);
            }
        }

        return best ?? NearestPlaceLabel(position);
    }

    /// <summary>
    /// Reads a live air sortie for the marker's CAS flags (addendum 2026-09-14 §1): a sortie open
    /// over this platoon — or over this mission's point, which is how a picket asks — with any
    /// airframe still unfilled reads <c>Requesting CAS</c>, a filled one <c>CAS overhead</c>. Both
    /// false when no sortie serves it.
    /// <para>One lookup with two keys rather than a platoon copy and a picket copy (Reuse rule 5):
    /// exactly one of <paramref name="platoon"/> and <paramref name="mission"/> is given, and a
    /// sortie matches on whichever it is.</para>
    /// </summary>
    private static void FindSortie(
        OperationsState state,
        CommanderPlatoon? platoon,
        CommanderOperationsMission? mission,
        out bool casShort,
        out bool casOverhead)
    {
        casShort = false;
        casOverhead = false;
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            bool matches = platoon != null
                ? ReferenceEquals(sortie.ContactPlatoon, platoon)
                : mission != null && ReferenceEquals(sortie.Mission, mission);
            if (!matches)
            {
                continue;
            }

            if (sortie.Cas.Count < sortie.Wanted || sortie.Caps.Count < sortie.CapsWanted)
            {
                casShort = true;
            }
            else
            {
                casOverhead = true;
            }

            return;
        }
    }

    /// <summary>The addendum 2026-09-14 §1 label table: every state produces its situation text,
    /// the reserve variant reads as its base, and the flags append in the fixed order.</summary>
    private static void CheckMarkerLabels(List<string> failures)
    {
        Expect(failures, "a forming platoon says where it forms", MarkerSituation(CommanderPlatoonState.Forming, CommanderGroundPosture.Ring, false, "Hilltop 12"), "Forming at Hilltop 12");
        Expect(failures, "a moving platoon says where it is going", MarkerSituation(CommanderPlatoonState.Moving, CommanderGroundPosture.Ring, false, "Crossroads 4"), "Moving to Crossroads 4");
        Expect(failures, "a holding platoon names the point it holds", MarkerSituation(CommanderPlatoonState.Holding, CommanderGroundPosture.Ring, false, "Hilltop 12"), "Holding Hilltop 12");
        Expect(failures, "a reserve platoon names its base", MarkerSituation(CommanderPlatoonState.Holding, CommanderGroundPosture.Ring, true, "Maris Airport"), "Reserve at Maris Airport");
        Expect(failures, "an attacking platoon names its target", MarkerSituation(CommanderPlatoonState.Attacking, CommanderGroundPosture.Ring, false, "Maris Airport"), "Attacking Maris Airport");
        Expect(failures, "a withdrawing platoon says where it falls back to", MarkerSituation(CommanderPlatoonState.Withdrawing, CommanderGroundPosture.Ring, false, "FOB Crossroads 4"), "Withdrawing to FOB Crossroads 4");
        Expect(failures, "no state, no situation text", MarkerSituation((CommanderPlatoonState)99, CommanderGroundPosture.Ring, false, "Hilltop 12"), string.Empty);

        // ground-tactics design §5: a posture outranks the state in the situation text.
        Expect(
            failures,
            "a garrison on its arc says so",
            MarkerSituation(CommanderPlatoonState.Holding, CommanderGroundPosture.DefenceArc, false, "Crossroads 13"),
            "Defence line at Crossroads 13");
        Expect(
            failures,
            "a bounding attack names where it is bounding to",
            MarkerSituation(CommanderPlatoonState.Attacking, CommanderGroundPosture.Bounding, false, "Hilltop 12"),
            "Bounding to Hilltop 12");
        Expect(
            failures,
            "a counter-attack names no place, only the flank",
            MarkerSituation(CommanderPlatoonState.Moving, CommanderGroundPosture.CounterAttack, false, "Crossroads 13"),
            "Counter-attacking from the flank");
        Expect(
            failures,
            "a screen names the point it covers",
            MarkerSituation(CommanderPlatoonState.Holding, CommanderGroundPosture.Screen, false, "Crossroads 13"),
            "Screening Crossroads 13");

        Expect(failures, "a quiet platoon carries no flags", MarkerFlags(false, false, false, false, string.Empty), string.Empty);
        Expect(
            failures,
            "flags appear in the addendum's order",
            MarkerFlags(true, true, false, true, "Hilltop 12"),
            " · In contact · Requesting CAS · Requesting reinforcements · Reinforcing Hilltop 12");
        Expect(failures, "a filled sortie reads as CAS overhead", MarkerFlags(false, false, true, false, string.Empty), " · CAS overhead");
        Expect(failures, "a reinforcing platoon alone still says so", MarkerFlags(false, false, false, false, "Crossroads 4"), " · Reinforcing Crossroads 4");
        Expect(
            failures,
            "under strength is the last flag on the line",
            MarkerFlags(true, false, false, false, string.Empty, underStrength: true),
            " · In contact · Under strength");
    }

    /// <summary>The picket and forward-base truck marker tables (user 2026-09-14: "we need markers
    /// on pickets too"): every situation and every flag renders, the <c>n/2</c> arithmetic reads
    /// the live count against the wanted one, and a picket with nothing on the ground and no flight
    /// inbound produces no marker at all.</summary>
    private static void CheckDetachmentMarkerLabels(List<string> failures)
    {
        Expect(
            failures,
            "a picket standing in its ring holds the point",
            PicketSituation(2, false, false, true, "Crossroads 13"),
            "Holding Crossroads 13");
        Expect(
            failures,
            "a picket still on the road says where it is going",
            PicketSituation(2, false, false, false, "Crossroads 13"),
            "Moving to Crossroads 13");
        Expect(
            failures,
            "a picket with a flight inbound and nothing on the ground awaits it",
            PicketSituation(0, true, false, false, "Hilltop 12"),
            "Awaiting insertion");
        Expect(
            failures,
            "a freshly dropped picket is taking its posts, not holding yet",
            PicketSituation(2, false, true, true, "Hilltop 12"),
            "Dropped, taking posts");
        Expect(
            failures,
            "a picket with nothing on the ground and no flight has no situation",
            PicketSituation(0, false, false, false, "Hilltop 12"),
            string.Empty);

        Expect(
            failures,
            "a full picket reads two of two",
            PicketMarkerText("Crossroads 13", 2, 2, false, false, true, false, false) ?? string.Empty,
            "PICKET Crossroads 13 2/2 — Holding Crossroads 13");
        Expect(
            failures,
            "a picket down to one vehicle reads one of two and says it is under strength",
            PicketMarkerText("Crossroads 13", 1, 2, false, false, true, false, false) ?? string.Empty,
            "PICKET Crossroads 13 1/2 — Holding Crossroads 13 · Under strength");
        Expect(
            failures,
            "every picket flag renders in the fixed order",
            PicketMarkerText("Crossroads 13", 1, 2, false, false, true, true, true) ?? string.Empty,
            "PICKET Crossroads 13 1/2 — Holding Crossroads 13 · In contact · Requesting CAS · Under strength");
        Expect(
            failures,
            "an awaited insertion is drawn at nought of two",
            PicketMarkerText("Hilltop 12", 0, 2, true, false, false, false, false) ?? string.Empty,
            "PICKET Hilltop 12 0/2 — Awaiting insertion · Under strength");
        Expect(
            failures,
            "a larger garrison setting is counted against, not a hard-coded two",
            PicketMarkerText("Hilltop 12", 3, 3, false, false, false, false, false) ?? string.Empty,
            "PICKET Hilltop 12 3/3 — Moving to Hilltop 12");
        Expect(
            failures,
            "one short of a larger garrison is still under strength",
            PicketMarkerText("Hilltop 12", 2, 3, false, false, false, false, false) ?? string.Empty,
            "PICKET Hilltop 12 2/3 — Moving to Hilltop 12 · Under strength");
        Expect(
            failures,
            "an empty picket with no flight inbound gets no marker",
            PicketMarkerText("Crossroads 13", 0, 2, false, false, false, false, false) == null,
            true);

        Expect(failures, "a truck on the road says where it is going", TruckSituation(false, false, "Crossroads 13"), "Moving to Crossroads 13");
        Expect(failures, "a truck inside the ring is supplying it", TruckSituation(false, true, "Crossroads 13"), "Supplying Crossroads 13");
        Expect(failures, "a truck with no post left is coming home", TruckSituation(true, false, "Crossroads 13"), "Returning");
        Expect(
            failures,
            "the whole truck marker names the point once",
            TruckMarkerText("Crossroads 13", false, true),
            "TRUCK Crossroads 13 — Supplying Crossroads 13");
        Expect(
            failures,
            "a returning truck still says which post it is leaving",
            TruckMarkerText("Crossroads 13", true, false),
            "TRUCK Crossroads 13 — Returning");
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
