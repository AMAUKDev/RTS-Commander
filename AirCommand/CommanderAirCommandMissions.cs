using System;
using System.Collections.Generic;
using System.Reflection;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    /// <summary>
    /// Every aircraft definition the game carries — the encyclopedia's list plus anything loaded
    /// as a live resource, deduplicated, in one list. Internal (one definition, two callers, Reuse
    /// rule 4): the AIR window's option list and the AI buyers' candidate list are THE SAME LIST —
    /// a commander that walked its faction supply instead (the first ladder build) could never see
    /// an airbase's hangars will accept but the faction was never issued, so a highway-strip
    /// commander sat on a full fund and bought no CAP fighter all match while the player launched
    /// the same aircraft by hand (user report, 2026-09-14: Compass and VT-7 Vagrant with Scythes).
    /// </summary>
    internal static void CollectAircraftDefinitions(List<AircraftDefinition> definitions)
    {
        definitions.Clear();
        HashSet<AircraftDefinition> seen = new();
        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia?.aircraft != null)
        {
            for (int i = 0; i < encyclopedia.aircraft.Count; i++)
            {
                AircraftDefinition definition = encyclopedia.aircraft[i];
                if (definition != null && seen.Add(definition))
                {
                    definitions.Add(definition);
                }
            }
        }

        AircraftDefinition[] resourceDefinitions = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
        for (int i = 0; i < resourceDefinitions.Length; i++)
        {
            AircraftDefinition definition = resourceDefinitions[i];
            if (definition != null && seen.Add(definition))
            {
                definitions.Add(definition);
            }
        }
    }

    private void RefreshOptions()
    {
        options.Clear();
        airbases.Clear();
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (!uiVisible || hq == null)
        {
            return;
        }

        List<AircraftDefinition> definitions = new();
        CollectAircraftDefinitions(definitions);

        for (int i = 0; i < definitions.Count; i++)
        {
            AircraftDefinition definition = definitions[i];
            if (definition == null
                || definition.unitPrefab == null
                || definition.aircraftParameters == null)
            {
                continue;
            }

            if (!HasPlanePilot(definition))
            {
                continue;
            }

            // The player's AIR window offers this faction's own airframes and nothing else
            // (user decision, 2026-09-16: the restriction applies to the human player exactly as it
            // applies to the computer commanders). Same predicate the computer's own buy gate
            // reads, so the two can never be offered different aircraft.
            if (!CommanderFactionRoster.MayFlyAircraft(hq, definition))
            {
                continue;
            }

            AirMissionOption? option = CreateVariableLoadoutOption(definition, hq, selectedMode);
            if (option == null)
            {
                continue;
            }

            bool supported = false;
            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (IsCompatibleAirbase(airbase, hq, definition))
                {
                    supported = true;
                    break;
                }
            }

            if (supported)
            {
                options.Add(option);
            }
        }

        BuildWeaponOptions();
        if (selectedPrimaryWeaponIndex < 0)
        {
            selectedPrimaryWeaponIndex = FindFirstSuitableWeaponIndex();
        }
        ApplySelectedWeaponsAndSort();
        RefreshAirbases();
    }

    private void RefreshAirbases()
    {
        Airbase? previouslySelected = SelectedAirbase?.Airbase;
        airbases.Clear();
        AirMissionOption? option = SelectedOption;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (option == null || hq == null)
        {
            selectedAirbaseIndex = 0;
            return;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (!IsCompatibleAirbase(airbase, hq, option.Definition)
                || !airbase.CanSpawnAircraft(option.Definition))
            {
                continue;
            }

            Transform position = airbase.center != null ? airbase.center : airbase.transform;
            airbases.Add(new AirbaseOption(
                airbase,
                GetAirbaseName(airbase),
                Vector3.Distance(cameraPosition, position.position),
                ready: true));
        }

        airbases.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        int preservedIndex = previouslySelected == null
            ? -1
            : airbases.FindIndex(option => ReferenceEquals(option.Airbase, previouslySelected));
        selectedAirbaseIndex = preservedIndex >= 0
            ? preservedIndex
            : Mathf.Clamp(selectedAirbaseIndex, 0, Mathf.Max(airbases.Count - 1, 0));
    }

    private void TryHandleTacticalMapClick()
    {
        if (!DynamicMap.mapMaximized)
        {
            return;
        }

        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        UpdatePendingAreaPreview();
        Vector2 guiMouse = CommanderUiScale.ScreenToGui(Input.mousePosition);
        if (areaSelectionBlockingRect.Contains(guiMouse) || areaSelectionSecondaryBlockingRect.Contains(guiMouse))
        {
            mapClickTracker.Reset();
            return;
        }
        if (map != null && mapClickTracker.Tick(map, out GlobalPosition target))
        {
            CompleteAreaSelection(target);
        }
    }

    private void CompleteAreaSelection(GlobalPosition target)
    {
        PendingAreaSelection? selection = pendingAreaSelection;
        Aircraft? relocationAircraft = pendingMissionRelocation;
        Aircraft? adoptionAircraft = pendingAdoption;
        pendingAreaSelection = null;
        pendingMissionRelocation = null;
        pendingAdoption = null;
        DestroyPendingAreaPreview();
        CloseOrderMap();
        if (relocationAircraft != null && missions.TryGetValue(relocationAircraft, out AirMission relocationMission))
        {
            relocationMission.AreaCenter = target;
            relocationMission.RememberArea();
            DestroyMissionMapVisual(relocationMission);
            EnsureMissionMapVisual(relocationMission);
            SetStatus($"{GetModeLabel(relocationMission.Mode)} mission area relocated.");
            return;
        }
        if (adoptionAircraft != null)
        {
            if (TryAdoptAircraft(adoptionAircraft, selectedMode, target, GetMissionRadius(selectedMode)))
            {
                SetStatus($"{CommanderGameAccess.GetUnitLabel(adoptionAircraft)} tasked with a {GetModeLabel(selectedMode)} mission.");
            }
            else
            {
                SetStatus("That aircraft can no longer be tasked.");
            }
            return;
        }

        if (selection == null)
        {
            return;
        }

        SpawnMission(selection.Option, selection.Airbase, target);
    }

    private void CancelAreaSelection(bool showStatus)
    {
        if (pendingAreaSelection == null && pendingMissionRelocation == null && pendingAdoption == null)
        {
            return;
        }

        pendingAreaSelection = null;
        pendingMissionRelocation = null;
        pendingAdoption = null;
        DestroyPendingAreaPreview();
        CloseOrderMap();
        if (showStatus)
        {
            SetStatus("Air mission area selection cancelled.");
        }
    }

    /// <summary>Altitude a commander-launched AI airframe enters the map at, over its own base.</summary>
    private const float LaunchAltitudeMeters = 1200f;

    /// <summary>How far out along its heading an airborne spawn is placed from the base centre:
    /// 4 km, outside the 3 km circle in which the game's combat AI lands an aircraft that has had no
    /// target for 15 s — a freshly spawned airframe has none until the review 30 s later.</summary>
    private const float AirborneSpawnStandoffMeters = 4000f;

    /// <summary>Altitude a helicopter or tiltwing enters at: 300 m above the ground below it. High
    /// enough to clear masts and trees on any base, low enough that the rotary AI's first action is
    /// a normal transit rather than a 1,200 m descent it does not survive (placed loss lines,
    /// 2026-09-15: transports lost 30 s after an airborne spawn at 600–900 m, 0.5 km from base).</summary>
    private const float RotaryLaunchAltitudeMeters = 300f;

    /// <summary>Forward speed a helicopter enters at: 40 m/s, a helicopter's easy cruise, where the
    /// fixed-wing floor of 120 m/s is a dive for a rotor.</summary>
    private const float RotaryLaunchSpeedMetersPerSecond = 40f;

    /// <summary>Altitude a tiltwing enters at: 500 m above the ground below it. It enters wing-borne
    /// (see <see cref="IsTiltwingAirframe"/>), so it needs more room under it than a helicopter to
    /// settle onto its autopilot, and less than a jet's 1,200 m, which the rotary combat AI answers
    /// with a descent it does not survive (user decision 2026-09-16: "try a plane style start").</summary>
    private const float TiltwingLaunchAltitudeMeters = 500f;

    /// <summary>Floor on the speed a fixed wing enters at: 120 m/s, comfortably above every fighter's
    /// take-off speed; the reference airspeed and 1.5x take-off speed raise it per type.</summary>
    private const float FixedWingLaunchSpeedFloorMetersPerSecond = 120f;

    /// <summary>The altitude an airborne entry is placed at above the ground under it, pure: a
    /// tiltwing's, a helicopter's or a fixed wing's. A tiltwing is asked first because its pilot type
    /// also counts as rotary.</summary>
    internal static float AirborneEntryAltitude(bool rotary, bool tiltwing)
    {
        return tiltwing ? TiltwingLaunchAltitudeMeters : rotary ? RotaryLaunchAltitudeMeters : LaunchAltitudeMeters;
    }

    /// <summary>The speed an airborne entry is given, pure: a helicopter's gentle cruise, or for a
    /// fixed wing AND a tiltwing (its ducts are parked forward, so it flies on its wing from the first
    /// frame) the airframe's own reference speed, floored so no type enters below a wing's stall.</summary>
    internal static float AirborneEntrySpeed(bool rotary, bool tiltwing, float referenceAirspeed, float takeoffSpeed)
    {
        if (rotary && !tiltwing)
        {
            return RotaryLaunchSpeedMetersPerSecond;
        }

        return Mathf.Max(referenceAirspeed, takeoffSpeed * 1.5f, FixedWingLaunchSpeedFloorMetersPerSecond);
    }

    /// <summary>
    /// Puts one AI airframe in the air over <paramref name="airbase"/>, pointed at
    /// <paramref name="facing"/>, at a speed the flight model can hold. Returns null if it could
    /// not be spawned.
    /// </summary>
    /// <remarks>
    /// This exists because <c>Airbase.TrySpawnAircraft</c> is not survivable for an AI pilot on
    /// this mod's maps. A hangar spawn hands the airframe to <c>AIPilotTaxiState</c> and then
    /// <c>AIPilotTakeoffState</c>, and neither copes with a highway strip: there is no taxi
    /// network, so the pilot drives a straight line at the runway threshold, and both states
    /// answer any trouble at all — a stuck timer, a scrape, a wing that touches anything — with
    /// <c>StartEjectionSequence</c>. That is the enemy's aircraft dying beside the hangar over and
    /// over, and it applies just as much to an airframe the player's AIR window buys; a player
    /// never sees it only because a player flies the aeroplane themselves.
    /// <c>Pilot.SetStartingAiState</c> already has the branch that avoids all of it: an aircraft
    /// whose <c>radarAlt</c> is above its spawn offset skips taxi and takeoff and starts in
    /// <c>AIPilotCombatModes</c> — the one state every Air Command patch in this mod targets. It is
    /// also how a mission spawns its own aircraft in flight (<c>Spawner.TrySpawnAircraft</c> on a
    /// <c>SavedAircraft</c> with a starting speed), so this is a supported path, not a trick.
    /// The airbase still decides *which* airframes exist — <c>CanSpawnAircraft</c> is the roster —
    /// it just no longer has to be taxied off. Callers own their own supply bookkeeping, because
    /// no hangar runs here to do it for them.
    /// </remarks>
    internal static bool TryLaunchAiAircraft(
        FactionHQ hq,
        Airbase airbase,
        AircraftDefinition definition,
        LiveryKey livery,
        Loadout loadout,
        float fuel,
        GlobalPosition facing)
    {
        // The INTERNAL CANNONS rule, applied HERE rather than trusted to every caller (user,
        // 2026-09-14: "aircraft spawned need to have 0 internal cannon rounds, otherwise they go
        // suicidal and try and use their cannons in heavily contested airspace"). The buy path
        // already stripped its own loadout; the supply and naval paths build theirs empty; this is
        // the one door all of them go through, so a future caller cannot reintroduce the gun by
        // forgetting. Idempotent — a loadout with no gun mount comes back unchanged.
        loadout = CommanderAirCommandService.WithoutInternalCannons(loadout);
        // The taxi queue rule (user, 2026-09-14): with more than AirLaunchQueueMax aircraft already
        // waiting on this deck, the launch spawns airborne at the map edge nearest the base rather
        // than joining the queue behind them.
        int onDeck = CommanderEnemyCommanderService.CountOnDeckNear(hq, airbase);
        bool fromMapEdge = CommanderOperationsService.LaunchesFromMapEdge(
            CommanderSettings.AiAircraftLaunchFromHangar, onDeck, CommanderSettings.AirLaunchQueueMax);
        // The deck spacing rule (fix, 2026-09-15): a second hangar spawn inside DeckLaunchSpacingSeconds
        // of the first goes airborne instead. The placed loss lines read aircraft destroyed on their
        // own deck at ground level 60–90 s after spawning, several per review, at a base whose
        // launches came out of its hangars seconds apart; the game frees a hangar once the last
        // aircraft is 30 m clear of the door, which is a tail in front of the next nose.
        // The clear-hangar rule (user decision 2026-09-15: "manually check for a safe spawn location
        // and if not, only then airborne"): a hangar is used only when nothing stands on its spawn
        // spot or along its exit path; otherwise the launch goes airborne over the base and the log
        // names what was in the way.
        string blockedBy = string.Empty;
        Hangar? clearHangar = CommanderSettings.AiAircraftLaunchFromHangar && !fromMapEdge
            ? TryFindClearHangar(airbase, definition, out blockedBy)
            : null;
        if (clearHangar != null)
        {
            // The game's own path, behind the Gameplay toggle so the ejection problem above can be
            // re-tested map by map instead of assumed. A hangar takes one airframe out of stock on
            // the way out of the door, and this method promises its callers that it leaves stock
            // alone (they do their own bookkeeping), so the one it consumes is put in first and
            // taken back if the hangar refuses.
            hq.ModifyUnitSupply(definition, 1);
            if (clearHangar.TrySpawnAircraft(null, definition, livery, loadout, fuel).Allowed)
            {
                StampDeckSpawn(airbase);
                return true;
            }

            hq.ModifyUnitSupply(definition, -1);
            return false;
        }

        if (CommanderSettings.AiAircraftLaunchFromHangar && !fromMapEdge)
        {
            CommanderAiLog.Note(
                hq,
                $"launches {definition.unitName} airborne over {CommanderCaptureService.GetAirbaseLabel(airbase)}: "
                    + (blockedBy.Length > 0 ? $"no hangar is clear ({blockedBy})." : "no hangar there can spawn it."));
        }

        return SpawnAirborneOverBase(hq, airbase, definition, loadout, livery, fuel, facing, fromMapEdge, onDeck) != null;
    }

    /// <summary>How close a unit may stand to a hangar's spawn spot or exit path before that hangar
    /// is unsafe to spawn from: 40 m, a fighter's length with a margin — the game frees the door at
    /// 30 m, which put a nose into the tail ahead of it (placed loss lines, 2026-09-15).</summary>
    internal const float HangarClearanceMeters = 40f;

    /// <summary>How far in front of the door the exit path is checked: 150 m, the roll-out an
    /// aircraft makes before it turns onto the taxiway.</summary>
    internal const float HangarExitPathMeters = 150f;

    /// <summary>Whether a unit at <paramref name="unit"/> blocks a spawn whose door is at
    /// <paramref name="spawn"/> and whose roll-out ends at <paramref name="exitEnd"/>, pure: within
    /// <paramref name="clearance"/> of that segment. All three in the same (Unity) space.</summary>
    internal static bool SpawnPathBlocked(Vector3 unit, Vector3 spawn, Vector3 exitEnd, float clearance)
    {
        return CommanderBuildPreview.SegmentDistanceSquared(
                unit.ToGlobalPosition(), spawn.ToGlobalPosition(), exitEnd.ToGlobalPosition())
            <= clearance * clearance;
    }

    /// <summary>
    /// The first hangar of <paramref name="airbase"/>, in the base's own priority order, that can
    /// spawn <paramref name="definition"/> and whose spawn spot and exit path are clear of every
    /// aircraft and ground vehicle in the world. Null when none is; <paramref name="blockedBy"/>
    /// then names the nearest blocker for the log.
    /// </summary>
    internal static Hangar? TryFindClearHangar(Airbase airbase, AircraftDefinition definition, out string blockedBy)
    {
        blockedBy = string.Empty;
        if (airbase == null || airbase.hangars == null)
        {
            return null;
        }

        for (int h = 0; h < airbase.hangars.Count; h++)
        {
            Hangar hangar = airbase.hangars[h];
            if (hangar == null || hangar.Disabled || !hangar.Available || !hangar.CanSpawnAircraft(definition))
            {
                continue;
            }

            Transform? spawn = hangar.GetSpawnTransform();
            if (spawn == null)
            {
                continue;
            }

            Vector3 door = spawn.position;
            Vector3 exitEnd = door + spawn.forward * HangarExitPathMeters;
            Unit? blocker = null;
            float blockerMeters = float.MaxValue;
            for (int i = 0; i < UnitRegistry.allUnits.Count; i++)
            {
                Unit unit = UnitRegistry.allUnits[i];
                if (unit == null || unit.disabled || !(unit is Aircraft || unit is GroundVehicle))
                {
                    continue;
                }

                if (SpawnPathBlocked(unit.transform.position, door, exitEnd, HangarClearanceMeters))
                {
                    float meters = Vector3.Distance(unit.transform.position, door);
                    if (meters < blockerMeters)
                    {
                        blocker = unit;
                        blockerMeters = meters;
                    }
                }
            }

            if (blocker == null)
            {
                return hangar;
            }

            blockedBy = $"{CommanderGameAccess.GetUnitLabel(blocker)} {blockerMeters:0} m from a door";
        }

        return null;
    }

    /// <summary>
    /// Seconds a base's deck is left alone after a hangar spawn before the next hangar spawn there:
    /// 20. A fighter taxis clear of its door and onto the taxiway in about that; the game's own
    /// hangar rule frees the door at 30 m, which is not clear of the next aircraft's nose (placed
    /// loss lines, 2026-09-15: aircraft destroyed 0.0–0.6 km from Sandrift Airbase at ground level
    /// 60–90 s after spawning, several per review). Both the wing and the supply side's transports
    /// read this one clock.
    /// </summary>
    internal const float DeckLaunchSpacingSeconds = 20f;

    /// <summary>Scaled <c>Time.time</c> of the last hangar spawn per base.</summary>
    private static readonly Dictionary<Airbase, float> lastDeckSpawnAt = new();

    /// <summary>The spacing rule, pure: a deck used strictly less than the spacing ago is busy.</summary>
    internal static bool DeckBusy(float secondsSinceSpawn, float spacingSeconds)
    {
        return secondsSinceSpawn < spacingSeconds;
    }

    internal static bool DeckRecentlyUsed(Airbase airbase)
    {
        return lastDeckSpawnAt.TryGetValue(airbase, out float at) && DeckBusy(Time.time - at, DeckLaunchSpacingSeconds);
    }

    internal static void StampDeckSpawn(Airbase airbase)
    {
        lastDeckSpawnAt[airbase] = Time.time;
    }

    /// <summary>How far apart successive airborne spawns are placed: 400 m sideways and 150 m up per
    /// step, cycling every four. Two fighters of one element spawned at the same point with the same
    /// heading were destroyed together 30 s later at the map edge (placed loss lines, 2026-09-15) —
    /// they were inside each other.</summary>
    internal const float AirborneSpawnLateralMeters = 400f;
    internal const float AirborneSpawnVerticalMeters = 150f;
    internal const int AirborneSpawnSlots = 4;

    private static int airborneSpawnSerial;

    /// <summary>The offset of the <paramref name="serial"/>th airborne spawn from the nominal point,
    /// pure: slots spread left and right of the heading and stepped upward, so no two consecutive
    /// spawns share a point.</summary>
    internal static Vector3 AirborneSpawnOffset(int serial, Vector3 right, float lateral, float vertical, int slots)
    {
        int slot = ((serial % slots) + slots) % slots;
        float side = (slot - (slots - 1) * 0.5f) * lateral;
        return right * side + Vector3.up * (slot * vertical);
    }

    /// <summary>
    /// The airborne spawn every commander launch shares (Reuse rule 4, 2026-09-15): the wing's
    /// air-spawn mode and its map-edge rule came through here already; the supply side's transports
    /// now do too, because a transport out of a hangar at an enemy base was being wrecked on the deck
    /// before it lifted (24–36 s after spawn, 47–81% of its parts detached, every one of 99 flights in
    /// the 2026-09-15 overnight log). Spawns at <see cref="LaunchAltitudeMeters"/> over the base —
    /// or at the nearest map edge when <paramref name="fromMapEdge"/> — heading for
    /// <paramref name="facing"/> at a speed the airframe is happy at. Null when the spawner refused.
    /// </summary>
    internal static Aircraft? SpawnAirborneOverBase(
        FactionHQ hq,
        Airbase airbase,
        AircraftDefinition definition,
        Loadout? loadout,
        LiveryKey livery,
        float fuel,
        GlobalPosition facing,
        bool fromMapEdge,
        int onDeck)
    {
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (spawner == null || airbase.center == null || definition.unitPrefab == null)
        {
            return null;
        }

        // A helicopter or tiltwing enters low and slow (fix, 2026-09-15): the placed loss lines read
        // transports "lost after 30 s" 0.5 km from their base at 600–900 m, i.e. crashing out of a
        // 1,200 m, 120 m/s entry the rotary AI cannot recover from.
        // A tiltwing is NOT flown in as a helicopter (fix, 2026-09-16): its ducts are parked forward
        // and the AI never swivels them, so a 40 m/s entry stalls it — see IsTiltwingAirframe.
        bool tiltwing = IsTiltwingAirframe(definition);
        bool rotary = IsRotaryAirframe(definition);
        float altitude = AirborneEntryAltitude(rotary, tiltwing);
        Vector3 heading = facing.ToLocalPosition() - airbase.center.position;
        heading.y = 0f;
        heading = heading.sqrMagnitude < 1f ? airbase.center.forward : heading.normalized;
        // Not straight over the base (fix, 2026-09-15): the game's own combat AI lands an aircraft
        // that has had no target for 15 s while within 3 km of its base, and an airborne spawn over
        // the base has no target until the next review — `recovered A-19 Brawler after 30 s in the
        // air`. Entering AirborneSpawnStandoffMeters out along its heading puts it outside that
        // circle for the half-minute the review needs.
        // Above the ground UNDER the spawn point, not above the base's ground (fix, 2026-09-16): four
        // kilometres out the terrain can stand higher than the base, and a 300 m helicopter start
        // measured from the base was inside the hillside — construction flights "lost … 52.7 km short
        // of the point … at 0 m" a few kilometres from their own deck.
        Vector3 standoff = airbase.center.position + heading * AirborneSpawnStandoffMeters;
        float standoffGround = CommanderGameAccess.SnapToTerrain(standoff.ToGlobalPosition()).ToLocalPosition().y;
        Vector3 origin = new Vector3(standoff.x, Mathf.Max(standoffGround, airbase.center.position.y) + altitude, standoff.z);
        if (fromMapEdge && CommanderSamSiteAnalyzerService.TryGetStrategicHeightMapSize(out Vector2 mapSize))
        {
            Vector3 edge = CommanderOperationsService.NearestMapEdgePoint(airbase.center.position, mapSize);
            // Above the ground AT THE EDGE, not above the base's ground (fix, 2026-09-15): the edge
            // can be high country, and fighters spawned 1,200 m over the base's height were "lost
            // after 30 s" 40 km out at 300–550 m — flown into a hillside they were born inside.
            float edgeGround = CommanderGameAccess.SnapToTerrain(new Vector3(edge.x, airbase.center.position.y, edge.z).ToGlobalPosition()).ToLocalPosition().y;
            origin = new Vector3(edge.x, Mathf.Max(edgeGround, airbase.center.position.y) + altitude, edge.z);
            Vector3 toBase = airbase.center.position - origin;
            toBase.y = 0f;
            heading = toBase.sqrMagnitude < 1f ? heading : toBase.normalized;
            CommanderAiLog.Note(
                hq,
                $"launches {definition.unitName} airborne from the map edge: {onDeck} aircraft already waiting on the deck at "
                    + $"{CommanderCaptureService.GetAirbaseLabel(airbase)}.");
        }

        // Consecutive airborne spawns never share a point (fix, 2026-09-15; see AirborneSpawnOffset).
        Vector3 right = Vector3.Cross(Vector3.up, heading).normalized;
        origin += AirborneSpawnOffset(airborneSpawnSerial++, right, AirborneSpawnLateralMeters, AirborneSpawnVerticalMeters, AirborneSpawnSlots);

        // A stationary air spawn falls out of the sky before the autopilot has any airspeed to
        // work with, so it enters at a speed the airframe is happy at — a plane's reference speed,
        // a helicopter's gentle forward cruise.
        float speed = AirborneEntrySpeed(
            rotary,
            tiltwing,
            definition.aircraftParameters.PIDReferenceAirspeed,
            definition.aircraftParameters.takeoffSpeed);
        if (tiltwing)
        {
            CommanderAiLog.Note(
                hq,
                $"{definition.unitName} enters wing-borne at {altitude:0} m and {speed:0} m/s "
                    + "(a tiltwing; a helicopter's entry stalls it).");
        }

        Aircraft aircraft = spawner.SpawnAircraft(
            null,
            definition.unitPrefab,
            loadout,
            fuel,
            livery,
            origin.ToGlobalPosition(),
            Quaternion.LookRotation(heading, Vector3.up),
            heading * speed,
            null,
            hq,
            null,
            1f,
            0.5f);
        if (aircraft != null && loadout == null)
        {
            // Hangar.SpawnAircraft's fallback: an AI airframe with no standard loadout otherwise
            // arrives with empty pylons. SelectAIAircraftWeapons picks a random legal mount for
            // EVERY hardpoint set, the gun's included — so it was the one commander launch path
            // that armed the cannon however the setting was set (found 2026-09-14). It goes through
            // the same strip as everything else now.
            aircraft.Networkloadout = CommanderAirCommandService.WithoutInternalCannons(
                aircraft.weaponManager.SelectAIAircraftWeapons(airbase));
        }

        // Belt and braces, one launch at a time: if a gun station exists on the airframe anyway —
        // the game substituting a stock loadout, an asset whose cannon is not a hardpoint entry at
        // all — its rounds come off now rather than being discovered in a gun run.
        int cannonRounds = CommanderAirCommandService.StripCannonAmmo(aircraft);
        if (cannonRounds > 0)
        {
            CommanderPlugin.Log.LogWarning(
                $"Air Command: {definition.unitName} spawned with {cannonRounds} internal cannon rounds "
                    + "despite the loadout rule; they have been removed.");
        }

        // An airborne spawn has, for every rule that reads it, already taken off: the game only
        // sets the flag when the take-off state is left, and this aircraft never enters it. The
        // supply side's abandoned-on-deck test reads it (fix, 2026-09-15).
        if (aircraft?.pilots != null)
        {
            for (int i = 0; i < aircraft.pilots.Length; i++)
            {
                if (aircraft.pilots[i]?.flightInfo != null)
                {
                    aircraft.pilots[i].flightInfo.HasTakenOff = true;
                }
            }
        }

        return aircraft;
    }

    private void SpawnMission(AirMissionOption option, Airbase airbase, GlobalPosition target)
    {
        // The UI path: the recipe is whatever the AIR window has selected right now, and the loadout
        // is built from the live hardpoint picker. A relaunch comes through TrySpawnFromRecipe with
        // the recipe recorded at first launch instead, so later picker changes do not leak into it.
        Loadout loadout = option.BuildLoadout();
        NormalizeLoadoutLength(loadout, option.Definition);
        AirMissionRecipe recipe = new(
            option,
            airbase,
            loadout,
            target,
            GetMissionRadius(option.Mode),
            SupportsTargetAltitude(option.Mode) ? selectedTargetAltitude : 0f,
            option.Mode == AirCommandMode.AirGuard && TargetOrdnance,
            option.Mode == AirCommandMode.Arad && SaturationAttack);
        TrySpawnFromRecipe(recipe, airbase, autoRecreate: false);
    }

    /// <summary>
    /// Launches one mission from a recipe. Returns false, with the reason in the status line, when
    /// nothing left the ground and nothing was charged.
    /// </summary>
    private bool TrySpawnFromRecipe(AirMissionRecipe recipe, Airbase airbase, bool autoRecreate)
    {
        AirMissionOption option = recipe.Option;
        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            SetStatus("Air Command is host-only.");
            return false;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null || !IsCompatibleAirbase(airbase, hq, option.Definition))
        {
            SetStatus("The selected airbase is no longer compatible.");
            return false;
        }

        if (!airbase.CanSpawnAircraft(option.Definition))
        {
            SetStatus("The selected airbase is busy. Retry when a compatible hangar is free.");
            return false;
        }

        if (pendingAircraftSpawn != null)
        {
            SetStatus("Wait for the previous Air Command aircraft to finish spawning.");
            return false;
        }

        bool purchased = false;
        if (hq.GetUnitSupply(option.Definition) <= 0)
        {
            if (hq.factionFunds < option.Definition.value)
            {
                SetStatus("The faction cannot afford this aircraft.");
                return false;
            }

            hq.AddFunds(-option.Definition.value);
            hq.ModifyUnitSupply(option.Definition, 1);
            purchased = true;
        }

        pendingAircraftSpawn = new PendingAircraftSpawn(
            hq,
            recipe,
            autoRecreate,
            purchased,
            purchased ? option.Definition.value : 0f,
            Time.unscaledTime + PendingSpawnTimeoutSeconds);

        int liveryIndex = option.Definition.aircraftParameters.GetRandomLiveryForFaction(hq.faction);
        Loadout loadout = recipe.Loadout;
        if (!ValidateSelectedLoadout(option, loadout, airbase, hq, out string loadoutError, fromPicker: !autoRecreate))
        {
            pendingAircraftSpawn = null;
            if (purchased)
            {
                hq.ModifyUnitSupply(option.Definition, -1);
                hq.AddFunds(option.Definition.value);
            }
            SetStatus(loadoutError);
            return false;
        }
        if (!TryLaunchAiAircraft(
                hq,
                airbase,
                option.Definition,
                new LiveryKey(liveryIndex),
                loadout,
                option.Definition.aircraftParameters.DefaultFuelLevel,
                recipe.AreaCenter))
        {
            pendingAircraftSpawn = null;
            if (purchased)
            {
                hq.ModifyUnitSupply(option.Definition, -1);
                hq.AddFunds(option.Definition.value);
            }
            SetStatus("The selected airbase rejected the aircraft spawn.");
            return false;
        }

        // TryLaunchAiAircraft leaves stock as it found it whichever way the airframe left, so the
        // one consumed comes off here: it cancels the purchase's +1 above, or uses one the faction
        // already had.
        hq.ModifyUnitSupply(option.Definition, -1);

        SetStatus($"{GetModeLabel(recipe.Mode)} mission {(autoRecreate ? "relaunched" : "launched")}: "
            + $"{GetAircraftLabel(option.Definition)} / {option.LoadoutName}"
            + (CommanderSettings.AiAircraftLaunchFromHangar ? " (hangar)." : "."));
        return true;
    }

    private void TryAssignPendingAircraft(FactionHQ hq, Unit unit)
    {
        PendingAircraftSpawn? pending = pendingAircraftSpawn;
        if (pending == null
            || !ReferenceEquals(hq, pending.Hq)
            || unit is not Aircraft aircraft
            || aircraft.Player != null
            || !ReferenceEquals(aircraft.definition, pending.Option.Definition))
        {
            return;
        }

        AirMissionRecipe recipe = pending.Recipe;
        AirMission mission = new(
            pending.Hq,
            recipe.Mode,
            recipe.AreaCenter,
            recipe.Radius,
            recipe.TargetAltitude,
            recipe.TargetOrdnance,
            recipe.SaturationAttack,
            pending.PurchasedWithFunds,
            pending.PurchaseCost,
            recipe,
            pending.AutoRecreate);
        missions[aircraft] = mission;
        CommanderSelectionService.PinMissionUnit(
            aircraft,
            "AIR COMMAND",
            GetModeLabel(recipe.Mode));
        pendingAircraftSpawn = null;
    }

    private void PruneMissions()
    {
        staleAircraft.Clear();
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            if (entry.Key == null || entry.Key.disabled)
            {
                staleAircraft.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleAircraft.Count; i++)
        {
            RemoveMission(staleAircraft[i]);
        }
    }
}
