using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// World/map markers for air sorties and packages (design.md, smarter-air-wing_20260914 Section 11;
/// user 2026-09-14: "can we add markers to packages (and its elements) like we do with platoons?").
/// The platoon marker's shape exactly — same dot, same label helper, same world-vs-map split, same
/// tracking test on anything that is not ours — with one marker for the package and a smaller one
/// for each airframe in it, so the player can watch a package converge on its form-up point.
/// </summary>
internal sealed partial class CommanderOperationsService
{
    /// <summary>
    /// World-view size of one airframe's marker. The same size as a platoon's
    /// (<see cref="MarkerWorldSize"/>), raised from 18 on 2026-09-14: the user reported "no label on
    /// aircraft still" with the markers already drawing, and a smaller dot sits further inside the
    /// aeroplane's own silhouette at the altitudes aircraft are seen at. The label's font was never
    /// the difference — <see cref="CommanderUiTheme.DrawWorldLabel"/> has one fixed style, so an
    /// aircraft's text has always been the same size as a platoon's; the size here only moves the
    /// dot and the label's offset above it.
    /// </summary>
    private const float MarkerElementWorldSize = MarkerWorldSize;

    /// <summary>Map-view size of one airframe's marker, matching <see cref="MarkerMapSize"/> for the
    /// same reason.</summary>
    private const float MarkerElementMapSize = MarkerMapSize;

    /// <summary>
    /// Aircraft already given a marker this frame, so the state-less walk and the per-commander one
    /// can never both label the same aeroplane (design.md, smarter-air-wing_20260914 Section 14).
    /// Cleared at the top of every frame's draw, and a field rather than a local because the draw is
    /// a per-frame GUI call and this must not allocate.
    /// </summary>
    private readonly HashSet<Aircraft> aircraftLabelled = new();

    /// <summary>The last count the state-less walk reported, so its diagnostics line prints when the
    /// picture changes rather than on every frame.</summary>
    private int lastStatelessMarkerCount = -1;

    /// <summary>
    /// Every aircraft in the world, labelled without needing a commander to exist yet (user
    /// decision 2026-09-14, Section 14: "labels seem to only activate a minute or so after mission
    /// start"). Both aircraft walks used to live inside the per-commander loop, and a commander's
    /// state is only created after point discovery and its first 30-second review — and again after
    /// every hot reload — so for the first minute of a mission nothing in the sky was named.
    /// <para>The commander's own airframes are skipped here: the walk that knows what they are doing
    /// runs later in the same frame and labels them properly.</para>
    /// </summary>
    private void DrawAllAircraftMarkers(
        Camera camera,
        FactionHQ? localHq,
        bool fullscreenMap,
        bool anyMap,
        CommanderTacticalMapService? map)
    {
        aircraftLabelled.Clear();
        int local = 0;
        int enemy = 0;
        if (localHq != null)
        {
            local = DrawFactionAircraft(camera, localHq, localHq, isLocal: true, fullscreenMap, anyMap, map);
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || ReferenceEquals(hq, localHq))
            {
                continue;
            }

            enemy += DrawFactionAircraft(camera, hq, localHq, isLocal: false, fullscreenMap, anyMap, map);
        }

        int total = local + enemy;
        if (CommanderSettings.OperationsDebugLog && total != lastStatelessMarkerCount)
        {
            lastStatelessMarkerCount = total;
            CommanderPlugin.Log.LogInfo(
                $"Air markers ready: {local} of ours, {enemy} tracked hostile — labelled without waiting for a commander review.");
        }
    }

    /// <summary>One faction's aircraft, classified and labelled with no operations state involved.
    /// Returns how many markers it drew.</summary>
    private int DrawFactionAircraft(
        Camera camera,
        FactionHQ hq,
        FactionHQ? localHq,
        bool isLocal,
        bool fullscreenMap,
        bool anyMap,
        CommanderTacticalMapService? map)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        Color color = isLocal ? CommanderGameAccess.GetFriendlyColor() : CommanderGameAccess.GetHostileColor();
        int drawn = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit is not Aircraft aircraft || unit.disabled)
            {
                continue;
            }

            if (!LabelsInStatelessWalk(
                    aircraftLabelled.Contains(aircraft), IsCommanderAirframe(hq, aircraft)))
            {
                continue;
            }

            CommanderAirCommandService.AirMission? mission = airCommand?.TryGetMission(aircraft);
            CommanderOtherAircraftKind kind = ClassifyOtherAircraft(
                local: isLocal,
                commanderOwned: false,
                hasPlayer: aircraft.Player != null,
                hasMission: mission != null,
                tracked: isLocal || CommanderGameAccess.ShouldRetainCommanderMarker(aircraft, localHq));
            if (kind == CommanderOtherAircraftKind.Skip)
            {
                continue;
            }

            DrawAirMarkerAt(
                camera,
                aircraft.transform.GlobalPosition(),
                OtherAircraftLabel(
                    kind,
                    mission != null ? CommanderAirCommandService.GetModeLabel(mission.Mode) : string.Empty,
                    CommanderGameAccess.GetUnitLabel(aircraft),
                    returning: mission?.Returning == true,
                    auto: mission?.AutoRecreate == true),
                kind == CommanderOtherAircraftKind.GameAi ? UntaskedMarkerColor : color,
                MarkerElementWorldSize,
                MarkerElementMapSize,
                fullscreenMap,
                anyMap,
                map);
            aircraftLabelled.Add(aircraft);
            drawn++;
        }

        return drawn;
    }

    /// <summary>
    /// Whether the state-less walk labels this aircraft (Section 14): only when nothing has labelled
    /// it yet this frame, and only when the commander does not own it — an owned airframe gets the
    /// specific label from the walk that knows what sortie it is on, later in the same frame. Pure,
    /// for the self-check.
    /// </summary>
    internal static bool LabelsInStatelessWalk(bool alreadyLabelled, bool commanderOwned)
    {
        return !alreadyLabelled && !commanderOwned;
    }

    /// <summary>
    /// Every sortie's marker for every commanded HQ, plus the standing home patrol's. Called from
    /// <c>DrawMarkers</c> beside the platoon markers, so the two are drawn under one camera pass and
    /// obey the same fullscreen-map rule.
    /// </summary>
    private void DrawAirMarkers(
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

        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            DrawSortieMarker(camera, localHq, hq, state, state.AirSorties[i], isLocal, color, fullscreenMap, anyMap, map);
        }

        DrawUnboundAirframes(camera, localHq, hq, state, isLocal, color, fullscreenMap, anyMap, map);
    }

    /// <summary>
    /// Every commander-owned airframe that no sortie marker already names (user decision
    /// 2026-09-14, Section 12: "i want to know what EVERY plane is up to"). The standing patrol, the
    /// insertion transports, anything on its way home, anything the idle sweep has not reached yet —
    /// and, in a warning colour, anything that fits none of those, so a gap in the bookkeeping shows
    /// up on screen instead of as an aeroplane quietly circling.
    /// </summary>
    private static void DrawUnboundAirframes(
        Camera camera,
        FactionHQ? localHq,
        FactionHQ hq,
        OperationsState state,
        bool isLocal,
        Color color,
        bool fullscreenMap,
        bool anyMap,
        CommanderTacticalMapService? map)
    {
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        int owned = 0;
        int drawn = 0;
        int untasked = 0;
        Aircraft? homeCapLead = HomeCapLead(state);
        int homeCapAlive = CountHomeCapFighters(hq);
        int homeCapWanted = CommanderEnemyCommanderService.WantedHomeCap(
            CommanderSettings.HomeCapBaseline,
            CommanderSettings.HomeCapPerEnemyAircraft,
            CommanderEnemyCommanderService.CountTrackedEnemyAircraftNearBases(hq),
            HomeCapLosses(hq),
            CommanderSettings.HomeCapMax);

        foreach (Aircraft aircraft in state.CommanderAirframes)
        {
            if (aircraft == null || aircraft.disabled)
            {
                continue;
            }

            owned++;
            if (IsBoundToAnySortie(state, aircraft))
            {
                drawn++; // the sortie walk above names it
                continue;
            }

            CommanderInsertion? insertion = FindInsertion(state, aircraft);
            CommanderAirframeTask task = ClassifyAirframe(
                insertion: insertion != null,
                returning: IsReturning(aircraft),
                homeCap: state.HomeCapAirframes.Contains(aircraft),
                transport: aircraft.definition is AircraftDefinition definition
                    && CommanderEnemyCommanderService.GetAirRole(definition)
                        == CommanderEnemyCommanderService.AirRole.Transport,
                hasMission: airCommand?.IsOnAnyMission(aircraft) == true);

            if (task == CommanderAirframeTask.Untasked)
            {
                untasked++;
            }

            if (!isLocal && !CommanderGameAccess.ShouldRetainCommanderMarker(aircraft, localHq))
            {
                continue;
            }

            GlobalPosition at = aircraft.transform.GlobalPosition();
            string place = task switch
            {
                CommanderAirframeTask.Insertion => insertion!.Point.Label,
                CommanderAirframeTask.Rtb => NearestAirbaseLabel(hq, at),
                CommanderAirframeTask.HomeCap => NearestAirbaseLabel(hq, at),
                _ => NearestPlaceLabel(at),
            };
            string detail = task switch
            {
                CommanderAirframeTask.Insertion => InsertionPhase(insertion!),
                CommanderAirframeTask.Rtb => CommanderAirCommandService.IsWinchester(aircraft)
                    ? "Winchester"
                    : "recovered soon",
                _ => CommanderGameAccess.GetUnitLabel(aircraft),
            };

            string label = AirframeMarkerLabel(
                task,
                place,
                ReferenceEquals(aircraft, homeCapLead) ? homeCapAlive : -1,
                homeCapWanted,
                detail,
                enemy: !isLocal);
            // Section 16: the same ground/departing/en-route truth on the patrol, the AWACS, the
            // lent fighters and the adopted strays.
            string phase = AirframePhaseText(hq, aircraft, at, place, string.Empty);
            if (phase.Length > 0)
            {
                label += " · " + phase;
            }
            DrawAirMarkerAt(
                camera,
                at,
                label,
                task == CommanderAirframeTask.Untasked ? UntaskedMarkerColor : color,
                MarkerElementWorldSize,
                MarkerElementMapSize,
                fullscreenMap,
                anyMap,
                map);
            drawn++;
        }

        if (CommanderSettings.OperationsDebugLog && hq.faction != null && owned > 0)
        {
            CommanderPlugin.Log.LogInfo(
                $"Ops {CommanderPlayerCommanderService.CommanderLabel(hq)}: air markers: {owned} owned, "
                    + $"{drawn} drawn, {untasked} untasked.");
        }
    }

    /// <summary>The first home-CAP fighter still flying — the one whose marker carries the patrol's
    /// strength; the rest name the patrol without repeating the count.</summary>
    private static Aircraft? HomeCapLead(OperationsState state)
    {
        foreach (Aircraft fighter in state.HomeCapAirframes)
        {
            if (fighter != null && !fighter.disabled)
            {
                return fighter;
            }
        }

        return null;
    }

    private static CommanderInsertion? FindInsertion(OperationsState state, Aircraft aircraft)
    {
        for (int i = 0; i < state.Insertions.Count; i++)
        {
            if (ReferenceEquals(state.Insertions[i].Aircraft, aircraft))
            {
                return state.Insertions[i];
            }
        }

        return null;
    }

    /// <summary>Where an insertion flight is in its round trip, read off what it has put down.</summary>
    private static string InsertionPhase(CommanderInsertion insertion)
    {
        if (insertion.Delivered <= 0)
        {
            return "outbound";
        }

        return insertion.Delivered < insertion.ExpectedLoads ? "unloading" : "returning";
    }

    /// <summary>
    /// One marker for the package and one for each airframe in it. The package's sits on its lead
    /// airframe — the first bound one still flying — or on the form-up point while it is forming and
    /// nothing has reached it yet. The form-up fallback is for our own packages only: an enemy
    /// package has no tracked unit at that point, and drawing a marker on empty air would tell the
    /// player where a strike is gathering before anything of theirs has seen it.
    /// </summary>
    private static void DrawSortieMarker(
        Camera camera,
        FactionHQ? localHq,
        FactionHQ hq,
        OperationsState state,
        CommanderAirSortie sortie,
        bool isLocal,
        Color color,
        bool fullscreenMap,
        bool anyMap,
        CommanderTacticalMapService? map)
    {
        Aircraft? lead = LeadAirframe(sortie);
        // An opposing commander's sortie is named only as much as the player could work out by
        // looking at it (Section 12): what it is, never what it has been told to do.
        string label = isLocal ? SortieMarkerLabel(hq, sortie, lead) : EnemySortieLabel(sortie);

        if (lead != null)
        {
            if (isLocal || CommanderGameAccess.ShouldRetainCommanderMarker(lead, localHq))
            {
                DrawAirMarkerAt(
                    camera, lead.transform.GlobalPosition(), label, color,
                    MarkerWorldSize, MarkerMapSize, fullscreenMap, anyMap, map);
            }
        }
        else if (isLocal && sortie.HasFormUp && !sortie.GoneIn)
        {
            DrawAirMarkerAt(
                camera, sortie.FormUpPoint, label, color,
                MarkerWorldSize, MarkerMapSize, fullscreenMap, anyMap, map);
        }

        // The lead already carries the package's own marker; a second label stacked on the same
        // aeroplane reads as two aircraft.
        DrawSortieElements(camera, localHq, hq, state, sortie, sortie.Cas, lead, asCap: false, isLocal, color, fullscreenMap, anyMap, map);
        DrawSortieElements(camera, localHq, hq, state, sortie, sortie.Caps, lead, asCap: true, isLocal, color, fullscreenMap, anyMap, map);
    }

    private static void DrawSortieElements(
        Camera camera,
        FactionHQ? localHq,
        FactionHQ hq,
        OperationsState state,
        CommanderAirSortie sortie,
        List<Aircraft> bound,
        Aircraft? lead,
        bool asCap,
        bool isLocal,
        Color color,
        bool fullscreenMap,
        bool anyMap,
        CommanderTacticalMapService? map)
    {
        for (int i = 0; i < bound.Count; i++)
        {
            Aircraft aircraft = bound[i];
            if (aircraft == null
                || aircraft.disabled
                || ReferenceEquals(aircraft, lead)
                || (!isLocal && !CommanderGameAccess.ShouldRetainCommanderMarker(aircraft, localHq)))
            {
                continue;
            }

            string role = state.LentHomeCap.Contains(aircraft)
                ? $"lent to {sortie.Label}"
                : AirMarkerElementRole(sortie.Kind, asCap);
            if (!isLocal)
            {
                DrawAirMarkerAt(
                    camera, aircraft.transform.GlobalPosition(), EnemySortieLabel(sortie), color,
                    MarkerElementWorldSize, MarkerElementMapSize, fullscreenMap, anyMap, map);
                continue;
            }

            DrawAirMarkerAt(
                camera,
                aircraft.transform.GlobalPosition(),
                $"{CommanderGameAccess.GetUnitLabel(aircraft)} — {role} · "
                    + AirframePhaseText(
                        hq,
                        aircraft,
                        SortieHoldsAtFormUp(sortie) ? sortie.FormUpPoint : sortie.Center,
                        sortie.Label,
                        "on station"),
                color,
                MarkerElementWorldSize,
                MarkerElementMapSize,
                fullscreenMap,
                anyMap,
                map);
        }
    }

    /// <summary>The platoon marker's own draw, parameterised by size (Reuse rule 4): the 3D view
    /// unless the fullscreen map has taken the screen, and the tactical map whenever it is up.</summary>
    private static void DrawAirMarkerAt(
        Camera camera,
        GlobalPosition position,
        string label,
        Color color,
        float worldSize,
        float mapSize,
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
                DrawDotMarker(guiPoint, worldSize, color);
                CommanderUiTheme.DrawWorldLabel(new Vector2(guiPoint.x, guiPoint.y - worldSize * 0.5f - 2f), label, color);
            }
        }

        if (anyMap && map != null && map.TryWorldToMapScreen(position, out Vector2 mapPoint))
        {
            Vector2 guiPoint = CommanderUiScale.ScreenToGui(mapPoint);
            DrawDotMarker(guiPoint, mapSize, color);
            CommanderUiTheme.DrawWorldLabel(new Vector2(guiPoint.x, guiPoint.y - mapSize * 0.5f - 2f), label, color);
        }
    }

    /// <summary>
    /// The colour of an airframe this commander owns and cannot account for. Amber rather than the
    /// faction colour on purpose (user decision 2026-09-14, Section 12): the whole reason the label
    /// exists is that the player was watching aeroplanes fly around with nothing to say what they
    /// were doing, and a gap in the bookkeeping has to be visible as a gap.
    /// </summary>
    private static readonly Color UntaskedMarkerColor = new(1f, 0.76f, 0.2f, 1f);

    /// <summary>What one commander-owned airframe is doing, for its own marker (Section 12).</summary>
    internal enum CommanderAirframeTask
    {
        /// <summary>Bound to a sortie; the sortie's own markers name it.</summary>
        Sortie,

        /// <summary>Flying a picket insertion.</summary>
        Insertion,

        /// <summary>On its way home, whether out of ammunition or for recovery.</summary>
        Rtb,

        /// <summary>On the standing patrol over the commander's own bases.</summary>
        HomeCap,

        /// <summary>A transport with nothing to deliver.</summary>
        Transport,

        /// <summary>Owned, carrying no mission at all: the idle sweep takes it this review.</summary>
        Idle,

        /// <summary>Owned, missioned, and accounted for by nothing. A bug made visible.</summary>
        Untasked,
    }

    /// <summary>
    /// What an owned airframe is doing, in precedence order: a delivery in progress first, then a
    /// flight already going home, then the standing patrol, then an idle transport, then anything
    /// with no mission at all — and, last, an airframe that is none of those, which should not
    /// happen and is drawn in the warning colour when it does. A sortie-bound airframe never reaches
    /// here; its sortie names it. Pure, for the self-check.
    /// </summary>
    internal static CommanderAirframeTask ClassifyAirframe(
        bool insertion, bool returning, bool homeCap, bool transport, bool hasMission)
    {
        if (insertion)
        {
            return CommanderAirframeTask.Insertion;
        }

        if (returning)
        {
            return CommanderAirframeTask.Rtb;
        }

        if (homeCap)
        {
            return CommanderAirframeTask.HomeCap;
        }

        if (transport)
        {
            return CommanderAirframeTask.Transport;
        }

        return hasMission ? CommanderAirframeTask.Untasked : CommanderAirframeTask.Idle;
    }

    /// <summary>
    /// One airframe's own marker text (Section 12). <paramref name="bound"/> is the patrol's live
    /// strength on the home-CAP lead's marker and negative on every other, so only one fighter of
    /// the patrol carries the count. <paramref name="enemy"/> prefixes an opposing commander's
    /// airframe, whose task the player can only guess at. Pure, for the self-check.
    /// </summary>
    internal static string AirframeMarkerLabel(
        CommanderAirframeTask task, string place, int bound, int wanted, string detail, bool enemy)
    {
        if (enemy)
        {
            return task switch
            {
                CommanderAirframeTask.HomeCap => "ENEMY CAP",
                CommanderAirframeTask.Transport => "ENEMY TRANSPORT",
                CommanderAirframeTask.Insertion => "ENEMY TRANSPORT",
                _ => "ENEMY AIRCRAFT",
            };
        }

        return task switch
        {
            CommanderAirframeTask.Insertion => $"INSERTION {place} — {detail}",
            CommanderAirframeTask.Rtb => $"RTB {place} — {detail}",
            CommanderAirframeTask.HomeCap => bound >= 0
                ? $"HOME CAP {place} — {bound}/{wanted}"
                : $"HOME CAP {place}",
            CommanderAirframeTask.Transport => $"TRANSPORT {place}",
            CommanderAirframeTask.Idle => "IDLE — retasking",
            CommanderAirframeTask.Untasked => $"UNTASKED {detail}",
            _ => detail,
        };
    }

    /// <summary>
    /// What an opposing commander's sortie is called on the player's screen (Section 12): what the
    /// aircraft plainly is, and for a strike the ground it is over — never the counts, the phase or
    /// the flags, which are this commander's plan and not something the player could see by looking.
    /// Pure, for the self-check.
    /// </summary>
    internal static string EnemySortieLabel(CommanderSortieKind kind, bool hasCas, string label)
    {
        return kind switch
        {
            CommanderSortieKind.Awacs => "ENEMY AWACS",
            CommanderSortieKind.Arad => "ENEMY ARAD",
            CommanderSortieKind.Cap => "ENEMY CAP",
            _ => hasCas ? $"ENEMY CAS {label}" : "ENEMY CAP",
        };
    }

    private static string EnemySortieLabel(CommanderAirSortie sortie)
    {
        return EnemySortieLabel(sortie.Kind, sortie.Wanted > 0, sortie.Label);
    }

    /// <summary>
    /// How far from the base it left an airframe still counts as departing rather than en route
    /// (user report 2026-09-14, Section 16: "seeing aircraft on the runway saying 'On station'").
    /// 3 km is about one climb-out: inside it the aeroplane is still visibly over its own field, and
    /// saying it is en route to somewhere 40 km away would read as wrong as "on station" did.
    /// </summary>
    private const float DepartureRadiusMeters = 3000f;

    /// <summary>Where an airframe actually is, which outranks what its sortie is doing (Section 16).</summary>
    internal enum CommanderAirPhase
    {
        /// <summary>Airborne and inside the ring it was sent to: the sortie's own phase applies.</summary>
        AtStation,

        /// <summary>On the ground, not going anywhere yet.</summary>
        AwaitingTakeOff,

        /// <summary>On the ground after a return.</summary>
        OnDeck,

        /// <summary>Airborne and going home.</summary>
        Landing,

        /// <summary>Airborne and still over the field it left.</summary>
        Departing,

        /// <summary>Airborne, on its way, not there yet.</summary>
        EnRoute,
    }

    /// <summary>
    /// Where an airframe is, in the order that matters: on the ground beats everything, then going
    /// home, then still climbing out, then still travelling — and only when none of those apply does
    /// the sortie's own phase get to speak. <paramref name="metersFromBase"/> is negative when the
    /// commander holds no base to measure from. Pure, for the self-check.
    /// </summary>
    internal static CommanderAirPhase ResolveAirPhase(
        bool onDeck,
        bool returning,
        float metersFromBase,
        float metersFromStation,
        float departingMeters,
        float arrivedMeters)
    {
        if (onDeck)
        {
            return returning ? CommanderAirPhase.OnDeck : CommanderAirPhase.AwaitingTakeOff;
        }

        if (returning)
        {
            return CommanderAirPhase.Landing;
        }

        if (metersFromBase >= 0f && metersFromBase <= departingMeters)
        {
            return CommanderAirPhase.Departing;
        }

        return metersFromStation > arrivedMeters ? CommanderAirPhase.EnRoute : CommanderAirPhase.AtStation;
    }

    /// <summary>The words for a phase. <paramref name="missionPhase"/> is what the sortie itself
    /// would have said, used only once the airframe is actually where it was sent. Pure, for the
    /// self-check.</summary>
    internal static string AirPhaseText(
        CommanderAirPhase phase, string baseLabel, string stationLabel, string missionPhase)
    {
        return phase switch
        {
            CommanderAirPhase.AwaitingTakeOff => "Taxiing / awaiting take-off",
            CommanderAirPhase.OnDeck => $"On deck {baseLabel}",
            CommanderAirPhase.Landing => $"Landing {baseLabel}",
            CommanderAirPhase.Departing => $"Departing {baseLabel}",
            CommanderAirPhase.EnRoute => $"En route to {stationLabel}",
            _ => missionPhase,
        };
    }

    /// <summary>
    /// The phase words for one live airframe: the runtime plumbing behind
    /// <see cref="ResolveAirPhase"/>. Empty when the airframe is where it was sent AND the caller
    /// had nothing of its own to say.
    /// </summary>
    private static string AirframePhaseText(
        FactionHQ hq, Aircraft aircraft, GlobalPosition station, string stationLabel, string missionPhase)
    {
        GlobalPosition at = aircraft.transform.GlobalPosition();
        string baseLabel = NearestAirbaseLabel(hq, at);
        float fromBase = -1f;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                airbase.center.GlobalPosition().AsVector3(), at.AsVector3());
            if (fromBase < 0f || distance < fromBase)
            {
                fromBase = distance;
            }
        }

        return AirPhaseText(
            ResolveAirPhase(
                CommanderAirCommandService.IsOnDeck(aircraft),
                IsReturning(aircraft),
                fromBase,
                CommanderGameAccess.HorizontalDistance(at.AsVector3(), station.AsVector3()),
                DepartureRadiusMeters,
                CasSortieRadiusMeters),
            baseLabel,
            stationLabel,
            missionPhase);
    }

    /// <summary>What an aircraft this commander does NOT own is, for its own marker (Section 13).</summary>
    internal enum CommanderOtherAircraftKind
    {
        /// <summary>Named by somebody else, or nobody's business: skip it.</summary>
        Skip,

        /// <summary>One of our faction's aircraft on a mission the player set in the AIR window.</summary>
        AirCommand,

        /// <summary>One of our faction's AI aircraft that neither the player nor the commander is
        /// flying — free or mission-authored. Drawn in the warning colour: nothing in the mod is
        /// steering it.</summary>
        GameAi,

        /// <summary>An opposing faction's aircraft the player's side is tracking.</summary>
        Enemy,
    }

    /// <summary>
    /// What to do with an aircraft the commander does not own (Section 13), in precedence order: one
    /// the commander DOES own is already named by the owned walk; an opposing faction's is drawn
    /// only while it is tracked; one of ours with a human in it is left alone, because the player
    /// knows what they are flying; and the rest split on whether the AIR window has a mission for it.
    /// Pure, for the self-check.
    /// </summary>
    internal static CommanderOtherAircraftKind ClassifyOtherAircraft(
        bool local, bool commanderOwned, bool hasPlayer, bool hasMission, bool tracked)
    {
        if (commanderOwned)
        {
            return CommanderOtherAircraftKind.Skip;
        }

        if (!local)
        {
            return tracked ? CommanderOtherAircraftKind.Enemy : CommanderOtherAircraftKind.Skip;
        }

        if (hasPlayer)
        {
            return CommanderOtherAircraftKind.Skip;
        }

        return hasMission ? CommanderOtherAircraftKind.AirCommand : CommanderOtherAircraftKind.GameAi;
    }

    /// <summary>The marker text for an aircraft the commander does not own (Section 13). Pure, for
    /// the self-check.</summary>
    internal static string OtherAircraftLabel(
        CommanderOtherAircraftKind kind, string modeLabel, string name, bool returning, bool auto)
    {
        return kind switch
        {
            CommanderOtherAircraftKind.AirCommand =>
                $"AIR CMD {modeLabel} — {(returning ? "RTB" : "ACTIVE")}{(auto ? " · AUTO" : string.Empty)}",
            CommanderOtherAircraftKind.GameAi => $"GAME AI {name}",
            CommanderOtherAircraftKind.Enemy => $"ENEMY AIRCRAFT {name}",
            _ => string.Empty,
        };
    }

    /// <summary>The first bound airframe still flying — CAS before escorts, the order the sortie was
    /// filled in. Null when the whole sortie is still on the ground or has just been wiped out.</summary>
    private static Aircraft? LeadAirframe(CommanderAirSortie sortie)
    {
        for (int i = 0; i < sortie.Cas.Count; i++)
        {
            if (sortie.Cas[i] != null && !sortie.Cas[i].disabled)
            {
                return sortie.Cas[i];
            }
        }

        for (int i = 0; i < sortie.Caps.Count; i++)
        {
            if (sortie.Caps[i] != null && !sortie.Caps[i].disabled)
            {
                return sortie.Caps[i];
            }
        }

        return null;
    }

    /// <summary>The live sortie's whole marker text, assembled from the pure pieces below.</summary>
    private static string SortieMarkerLabel(FactionHQ hq, CommanderAirSortie sortie, Aircraft? lead)
    {
        bool package = SortieIsPackage(sortie.Wanted, sortie.CapsWanted, sortie.NoCapWait);
        bool forming = SortieHoldsAtFormUp(sortie);
        bool capSlot = sortie.Kind == CommanderSortieKind.Cap || sortie.Wanted <= 0;
        int bound = capSlot ? sortie.Caps.Count : sortie.Cas.Count;
        int wanted = capSlot ? sortie.CapsWanted : sortie.Wanted;

        // The AWACS sortie's own label already says "AWACS …", so the marker names the ground it
        // orbits instead and lets the kind word carry the rest.
        string label = sortie.Kind == CommanderSortieKind.Awacs
            ? NearestPlaceLabel(sortie.Center)
            : sortie.Label;

        return AirMarkerLabel(
            AirMarkerKind(sortie.Kind, package, sortie.Wanted),
            label,
            bound,
            wanted,
            // Where the lead ACTUALLY is outranks what the sortie is doing (Section 16): an
            // aeroplane still on the runway was reading "On station" because the sortie was.
            LeadPhase(
                hq,
                sortie,
                lead,
                AirMarkerPhase(
                    lent: false,
                    retasked: false,
                    aradPending: sortie.AradPending,
                    formingAtFormUp: forming,
                    atFormUp: forming ? CountAtFormUp(sortie.Cas, sortie.FormUpPoint) : 0,
                    casWanted: sortie.Wanted,
                    escortMissing: sortie.CapsWanted > 0 && sortie.Caps.Count == 0,
                    onStation: sortie.GoneIn && bound > 0)),
            AirMarkerFlags(
                hasEscort: sortie.CapsWanted > 0 && sortie.Caps.Count > 0,
                preemptive: sortie.Preemptive,
                rotary: sortie.Kind == CommanderSortieKind.Objective && sortie.WantsRotary));
    }

    /// <summary>The sortie's phase, overridden by where its lead airframe really is. With no lead
    /// airborne at all the sortie's own phase stands: there is nobody to ask.</summary>
    private static string LeadPhase(
        FactionHQ hq, CommanderAirSortie sortie, Aircraft? lead, string missionPhase)
    {
        return lead == null
            ? missionPhase
            : AirframePhaseText(
                hq,
                lead,
                SortieHoldsAtFormUp(sortie) ? sortie.FormUpPoint : sortie.Center,
                sortie.Label,
                missionPhase);
    }

    /// <summary>
    /// What a sortie's marker calls it (user decision 2026-09-14, Section 11). A package of more
    /// than one airframe is <c>PKG</c> whatever it is carrying — that is the thing the player asked
    /// to be able to see. Otherwise the kind: the radar airframe, a suppression strike, the
    /// fighters a platoon called for on its own (<c>CAP</c>), the fighters flying over an objective
    /// that has no strike of its own (<c>ESCORT</c>), and a single strike airframe (<c>CAS</c>).
    /// Pure, for the self-check.
    /// </summary>
    internal static string AirMarkerKind(CommanderSortieKind kind, bool isPackage, int casWanted)
    {
        switch (kind)
        {
            case CommanderSortieKind.Awacs:
                return "AWACS";
            case CommanderSortieKind.Arad:
                return "ARAD";
            case CommanderSortieKind.Cap:
                return "CAP";
        }

        if (isPackage)
        {
            return "PKG";
        }

        return casWanted > 0 ? "CAS" : "ESCORT";
    }

    /// <summary>
    /// What a sortie or one of its airframes is doing right now (user decision 2026-09-14,
    /// Section 11), in this precedence: a loan and a retask are facts about one AIRFRAME and are
    /// only ever passed for an element marker; then the reasons a package is still holding, most
    /// specific first; then whether it has arrived. Pure, for the self-check.
    /// </summary>
    internal static string AirMarkerPhase(
        bool lent,
        bool retasked,
        bool aradPending,
        bool formingAtFormUp,
        int atFormUp,
        int casWanted,
        bool escortMissing,
        bool onStation)
    {
        if (lent)
        {
            return "Lent from home CAP";
        }

        if (retasked)
        {
            return "Retasked";
        }

        if (aradPending)
        {
            return "Holding for ARAD";
        }

        if (formingAtFormUp)
        {
            return escortMissing
                ? "Holding for escort"
                : $"Forming at form-up ({atFormUp}/{casWanted})";
        }

        return onStation ? "On station" : "Going in";
    }

    /// <summary>
    /// The marker's flags, appended behind the platoon marker's own <c> · </c> separator in a fixed
    /// order, or nothing when none is set (Section 11). Pure, for the self-check.
    /// </summary>
    internal static string AirMarkerFlags(bool hasEscort, bool preemptive, bool rotary)
    {
        if (!hasEscort && !preemptive && !rotary)
        {
            return string.Empty;
        }

        System.Text.StringBuilder text = new();
        if (hasEscort)
        {
            text.Append(" · +esc");
        }

        if (preemptive)
        {
            text.Append(" · pre");
        }

        if (rotary)
        {
            text.Append(" · rotary");
        }

        return text.ToString();
    }

    /// <summary>The whole sortie marker line: <c>&lt;KIND&gt; &lt;label&gt; n/m — &lt;phase&gt;[ ·
    /// flags]</c>, the platoon marker's shape with the kind word in front. Pure, for the
    /// self-check.</summary>
    internal static string AirMarkerLabel(string kind, string label, int bound, int wanted, string phase, string flags)
    {
        return $"{kind} {label} {bound}/{wanted} — {phase}{flags}";
    }

    /// <summary>What one airframe's own marker calls its job in the package. Pure, for the
    /// self-check.</summary>
    internal static string AirMarkerElementRole(CommanderSortieKind kind, bool asCap)
    {
        if (asCap)
        {
            return "escort";
        }

        return kind switch
        {
            CommanderSortieKind.Awacs => "radar",
            CommanderSortieKind.Arad => "ARAD",
            _ => "CAS",
        };
    }

    /// <summary>
    /// The Section 11 label table: every kind and every phase produces text, and the counts read the
    /// way the player expects. These strings are the only description of the air plan a player ever
    /// sees without opening the log, so a kind that silently falls through to the wrong word is a
    /// bug nothing else would catch.
    /// </summary>
    private static void CheckAirMarkerLabels(List<string> failures)
    {
        Expect(failures, "a multi-airframe sortie is a package", AirMarkerKind(CommanderSortieKind.Objective, true, 3), "PKG");
        Expect(failures, "a single strike airframe is CAS", AirMarkerKind(CommanderSortieKind.Objective, false, 1), "CAS");
        Expect(failures, "fighters over an objective with no strike are an escort", AirMarkerKind(CommanderSortieKind.Objective, false, 0), "ESCORT");
        Expect(failures, "a platoon's own request is a CAP", AirMarkerKind(CommanderSortieKind.Cap, false, 0), "CAP");
        Expect(failures, "the radar airframe is the AWACS", AirMarkerKind(CommanderSortieKind.Awacs, false, 1), "AWACS");
        Expect(failures, "a suppression strike is ARAD", AirMarkerKind(CommanderSortieKind.Arad, false, 2), "ARAD");
        Expect(failures, "a suppression pair is still ARAD, not a package", AirMarkerKind(CommanderSortieKind.Arad, true, 2), "ARAD");

        Expect(failures, "a package gathering says how much of it is there", AirMarkerPhase(false, false, false, true, 2, 3, false, false), "Forming at form-up (2/3)");
        Expect(failures, "a package with no escort yet says what it waits for", AirMarkerPhase(false, false, false, true, 2, 3, true, false), "Holding for escort");
        Expect(failures, "a package waiting on suppression says so first", AirMarkerPhase(false, false, true, true, 2, 3, true, false), "Holding for ARAD");
        Expect(failures, "a released package on its way is going in", AirMarkerPhase(false, false, false, false, 0, 3, false, false), "Going in");
        Expect(failures, "a package over its objective is on station", AirMarkerPhase(false, false, false, false, 0, 3, false, true), "On station");
        Expect(failures, "a borrowed home-CAP fighter says where it came from", AirMarkerPhase(true, false, true, true, 0, 3, true, false), "Lent from home CAP");
        Expect(failures, "an airframe pulled onto a contact says so", AirMarkerPhase(false, true, true, true, 0, 3, true, false), "Retasked");

        Expect(failures, "a quiet sortie carries no flags", AirMarkerFlags(false, false, false), string.Empty);
        Expect(failures, "the flags appear in their fixed order", AirMarkerFlags(true, true, true), " · +esc · pre · rotary");
        Expect(failures, "an escorted sortie says so alone", AirMarkerFlags(true, false, false), " · +esc");
        Expect(failures, "a helicopter sortie says so alone", AirMarkerFlags(false, false, true), " · rotary");

        Expect(
            failures,
            "the package line reads as the design writes it",
            AirMarkerLabel("PKG", "CROSSROADS 14", 2, 3, "Forming at form-up (2/3)", " · +esc · rotary"),
            "PKG CROSSROADS 14 2/3 — Forming at form-up (2/3) · +esc · rotary");
        Expect(
            failures,
            "a full sortie reads its counts equal",
            AirMarkerLabel("CAS", "Hilltop 12", 1, 1, "On station", string.Empty),
            "CAS Hilltop 12 1/1 — On station");
        Expect(
            failures,
            "an empty sortie still names what it wants",
            AirMarkerLabel("ARAD", "Maris Airport", 0, 2, "Going in", string.Empty),
            "ARAD Maris Airport 0/2 — Going in");

        Expect(failures, "a strike airframe's own marker says CAS", AirMarkerElementRole(CommanderSortieKind.Objective, false), "CAS");
        Expect(failures, "a fighter's own marker says escort", AirMarkerElementRole(CommanderSortieKind.Objective, true), "escort");
        Expect(failures, "the radar airframe's own marker says radar", AirMarkerElementRole(CommanderSortieKind.Awacs, false), "radar");
        Expect(failures, "a suppression airframe's own marker says ARAD", AirMarkerElementRole(CommanderSortieKind.Arad, false), "ARAD");
        Expect(failures, "an escort on a suppression sortie is still an escort", AirMarkerElementRole(CommanderSortieKind.Arad, true), "escort");

        CheckAirframeMarkerLabels(failures);
    }

    /// <summary>
    /// Section 12: EVERY state an owned airframe can be in produces a label. The user's complaint
    /// was aeroplanes flying about with nothing on them, so a state that falls through to an empty
    /// string is the exact bug this table exists to catch.
    /// </summary>
    private static void CheckAirframeMarkerLabels(List<string> failures)
    {
        Expect(failures, "a delivery in progress outranks everything else", (int)ClassifyAirframe(true, true, true, true, true), (int)CommanderAirframeTask.Insertion);
        Expect(failures, "an airframe going home says so before its patrol does", (int)ClassifyAirframe(false, true, true, true, true), (int)CommanderAirframeTask.Rtb);
        Expect(failures, "a standing-patrol fighter reads as home CAP", (int)ClassifyAirframe(false, false, true, false, true), (int)CommanderAirframeTask.HomeCap);
        Expect(failures, "an idle transport reads as a transport", (int)ClassifyAirframe(false, false, false, true, true), (int)CommanderAirframeTask.Transport);
        Expect(failures, "an airframe with no mission at all is on the idle sweep", (int)ClassifyAirframe(false, false, false, false, false), (int)CommanderAirframeTask.Idle);
        Expect(failures, "an owned, missioned airframe nothing accounts for is untasked", (int)ClassifyAirframe(false, false, false, false, true), (int)CommanderAirframeTask.Untasked);

        Expect(failures, "an insertion names its point and its leg", AirframeMarkerLabel(CommanderAirframeTask.Insertion, "HILLTOP 12", -1, 0, "outbound", false), "INSERTION HILLTOP 12 — outbound");
        Expect(failures, "an empty airframe going home says why", AirframeMarkerLabel(CommanderAirframeTask.Rtb, "Maris Airport", -1, 0, "Winchester", false), "RTB Maris Airport — Winchester");
        Expect(failures, "the patrol's lead fighter carries its strength", AirframeMarkerLabel(CommanderAirframeTask.HomeCap, "Maris Airport", 3, 4, "FS-12 Revoker", false), "HOME CAP Maris Airport — 3/4");
        Expect(failures, "the rest of the patrol names it without the count", AirframeMarkerLabel(CommanderAirframeTask.HomeCap, "Maris Airport", -1, 4, "FS-12 Revoker", false), "HOME CAP Maris Airport");
        Expect(failures, "a transport names where it is", AirframeMarkerLabel(CommanderAirframeTask.Transport, "CROSSROADS 4", -1, 0, "UH-90 Ibis", false), "TRANSPORT CROSSROADS 4");
        Expect(failures, "an idle airframe says the sweep is about to task it", AirframeMarkerLabel(CommanderAirframeTask.Idle, "CROSSROADS 4", -1, 0, "A-19 Brawler", false), "IDLE — retasking");
        Expect(failures, "an unaccounted airframe names itself so the gap is visible", AirframeMarkerLabel(CommanderAirframeTask.Untasked, "CROSSROADS 4", -1, 0, "A-19 Brawler", false), "UNTASKED A-19 Brawler");
        Expect(failures, "a sortie-bound airframe falls through to its own name", AirframeMarkerLabel(CommanderAirframeTask.Sortie, "CROSSROADS 4", -1, 0, "A-19 Brawler", false), "A-19 Brawler");

        Expect(failures, "an enemy patrol fighter is named only as a CAP", AirframeMarkerLabel(CommanderAirframeTask.HomeCap, "Maris Airport", 3, 4, "FS-12 Revoker", true), "ENEMY CAP");
        Expect(failures, "an enemy transport is named as a transport", AirframeMarkerLabel(CommanderAirframeTask.Transport, "CROSSROADS 4", -1, 0, "UH-90 Ibis", true), "ENEMY TRANSPORT");
        Expect(failures, "an enemy insertion reads as a transport, not as a plan", AirframeMarkerLabel(CommanderAirframeTask.Insertion, "HILLTOP 12", -1, 0, "outbound", true), "ENEMY TRANSPORT");
        Expect(failures, "any other enemy airframe is just an aircraft", AirframeMarkerLabel(CommanderAirframeTask.Untasked, "CROSSROADS 4", -1, 0, "A-19 Brawler", true), "ENEMY AIRCRAFT");

        Expect(failures, "an enemy strike names the ground it is over", EnemySortieLabel(CommanderSortieKind.Objective, true, "CROSSROADS 14"), "ENEMY CAS CROSSROADS 14");
        Expect(failures, "an enemy sortie with no strike reads as a CAP", EnemySortieLabel(CommanderSortieKind.Objective, false, "CROSSROADS 14"), "ENEMY CAP");
        Expect(failures, "an enemy platoon CAP reads as a CAP", EnemySortieLabel(CommanderSortieKind.Cap, false, "3RD PLATOON"), "ENEMY CAP");
        Expect(failures, "an enemy radar airframe reads as an AWACS", EnemySortieLabel(CommanderSortieKind.Awacs, true, "HILLTOP 12"), "ENEMY AWACS");
        Expect(failures, "an enemy suppression strike reads as ARAD", EnemySortieLabel(CommanderSortieKind.Arad, true, "HILLTOP 12"), "ENEMY ARAD");

        // Section 13: the aircraft the commander does NOT own. The user reported unlabelled
        // aeroplanes while the owned walk was already drawing every one of its own, so every
        // remaining case has to produce text too.
        Expect(failures, "an airframe the commander owns is left to the owned walk", (int)ClassifyOtherAircraft(true, true, false, true, true), (int)CommanderOtherAircraftKind.Skip);
        Expect(failures, "an aeroplane with a human in it is left alone", (int)ClassifyOtherAircraft(true, false, true, true, true), (int)CommanderOtherAircraftKind.Skip);
        Expect(failures, "one of ours on an AIR window mission says so", (int)ClassifyOtherAircraft(true, false, false, true, true), (int)CommanderOtherAircraftKind.AirCommand);
        Expect(failures, "one of ours nothing is steering is a game AI aeroplane", (int)ClassifyOtherAircraft(true, false, false, false, true), (int)CommanderOtherAircraftKind.GameAi);
        Expect(failures, "a tracked enemy aeroplane is drawn", (int)ClassifyOtherAircraft(false, false, false, false, true), (int)CommanderOtherAircraftKind.Enemy);
        Expect(failures, "an untracked enemy aeroplane is never drawn", (int)ClassifyOtherAircraft(false, false, false, true, false), (int)CommanderOtherAircraftKind.Skip);
        Expect(failures, "an enemy aeroplane with a human in it is still drawn while tracked", (int)ClassifyOtherAircraft(false, false, true, false, true), (int)CommanderOtherAircraftKind.Enemy);

        Expect(failures, "an active AIR window mission names its mode", OtherAircraftLabel(CommanderOtherAircraftKind.AirCommand, "CAS", "A-19 Brawler", false, false), "AIR CMD CAS — ACTIVE");
        Expect(failures, "an AIR window mission on its way home says RTB", OtherAircraftLabel(CommanderOtherAircraftKind.AirCommand, "AIR SUPERIORITY", "FS-12 Revoker", true, false), "AIR CMD AIR SUPERIORITY — RTB");
        Expect(failures, "a relaunching AIR window mission says so", OtherAircraftLabel(CommanderOtherAircraftKind.AirCommand, "CAS", "A-19 Brawler", false, true), "AIR CMD CAS — ACTIVE · AUTO");
        Expect(failures, "a free AI aeroplane names itself", OtherAircraftLabel(CommanderOtherAircraftKind.GameAi, string.Empty, "T/A-30 Compass", false, false), "GAME AI T/A-30 Compass");
        Expect(failures, "a tracked enemy aeroplane names itself", OtherAircraftLabel(CommanderOtherAircraftKind.Enemy, string.Empty, "KR-67 Ifrit", false, false), "ENEMY AIRCRAFT KR-67 Ifrit");
        Expect(failures, "a skipped aeroplane produces no text at all", OtherAircraftLabel(CommanderOtherAircraftKind.Skip, "CAS", "A-19 Brawler", false, false), string.Empty);

        Expect(failures, "an aircraft marker is no smaller than a platoon's", MarkerElementWorldSize >= MarkerWorldSize, true);

        // Section 16: where the aeroplane IS beats what its sortie is doing. The reported failure
        // was an aircraft on the runway reading "On station".
        Expect(failures, "an aeroplane still on the runway is awaiting take-off", (int)ResolveAirPhase(true, false, 0f, 40000f, 3000f, 6000f), (int)CommanderAirPhase.AwaitingTakeOff);
        Expect(failures, "an aeroplane back on the deck after a return says so", (int)ResolveAirPhase(true, true, 0f, 40000f, 3000f, 6000f), (int)CommanderAirPhase.OnDeck);
        Expect(failures, "an aeroplane going home is landing, wherever its sortie is", (int)ResolveAirPhase(false, true, 20000f, 0f, 3000f, 6000f), (int)CommanderAirPhase.Landing);
        Expect(failures, "an aeroplane still over its own field is departing", (int)ResolveAirPhase(false, false, 1500f, 40000f, 3000f, 6000f), (int)CommanderAirPhase.Departing);
        Expect(failures, "the departure ring's edge is still departing", (int)ResolveAirPhase(false, false, 3000f, 40000f, 3000f, 6000f), (int)CommanderAirPhase.Departing);
        Expect(failures, "an aeroplane past its field and short of its station is en route", (int)ResolveAirPhase(false, false, 20000f, 40000f, 3000f, 6000f), (int)CommanderAirPhase.EnRoute);
        Expect(failures, "an aeroplane inside the ring it was sent to defers to its sortie", (int)ResolveAirPhase(false, false, 20000f, 4000f, 3000f, 6000f), (int)CommanderAirPhase.AtStation);
        Expect(failures, "the station ring's edge counts as arrived", (int)ResolveAirPhase(false, false, 20000f, 6000f, 3000f, 6000f), (int)CommanderAirPhase.AtStation);
        Expect(failures, "a commander with no base to measure from is never called departing", (int)ResolveAirPhase(false, false, -1f, 40000f, 3000f, 6000f), (int)CommanderAirPhase.EnRoute);

        Expect(failures, "the runway text names no place it has not reached", AirPhaseText(CommanderAirPhase.AwaitingTakeOff, "Maris Airport", "CROSSROADS 14", "On station"), "Taxiing / awaiting take-off");
        Expect(failures, "the deck text names the field", AirPhaseText(CommanderAirPhase.OnDeck, "Maris Airport", "CROSSROADS 14", "On station"), "On deck Maris Airport");
        Expect(failures, "the landing text names the field", AirPhaseText(CommanderAirPhase.Landing, "Maris Airport", "CROSSROADS 14", "On station"), "Landing Maris Airport");
        Expect(failures, "the departure text names the field", AirPhaseText(CommanderAirPhase.Departing, "Maris Airport", "CROSSROADS 14", "On station"), "Departing Maris Airport");
        Expect(failures, "the transit text names where it is going", AirPhaseText(CommanderAirPhase.EnRoute, "Maris Airport", "CROSSROADS 14", "On station"), "En route to CROSSROADS 14");
        Expect(failures, "an arrived airframe says whatever its sortie says", AirPhaseText(CommanderAirPhase.AtStation, "Maris Airport", "CROSSROADS 14", "Forming at form-up (2/3)"), "Forming at form-up (2/3)");

        // Section 14: the state-less walk runs first and must leave the commander's own airframes to
        // the walk that knows what they are doing, and never label anything twice.
        Expect(failures, "an unlabelled aircraft nobody owns is labelled straight away", LabelsInStatelessWalk(false, false), true);
        Expect(failures, "a commander's own airframe is left to the walk that knows its sortie", LabelsInStatelessWalk(false, true), false);
        Expect(failures, "an aircraft already labelled this frame is never labelled twice", LabelsInStatelessWalk(true, false), false);
        Expect(failures, "an owned aircraft already labelled stays labelled once", LabelsInStatelessWalk(true, true), false);
    }
}
