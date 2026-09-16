using System;
using System.Collections.Generic;
using System.Reflection;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    internal static void NotifyFactionUnitRegistered(FactionHQ hq, Unit unit)
    {
        Instance?.TryAssignPendingAircraft(hq, unit);
    }

    internal static void NotifyAircraftReturned(Aircraft aircraft)
    {
        Instance?.HandleAircraftReturned(aircraft);
        // Back in stock is not shot down: the commander's attrition ledger stops counting it here,
        // before its loss sweep can see the airframe gone (user decision 2026-09-15).
        CommanderEnemyCommanderService.NoteAirframeNotLost(aircraft);
    }

    internal static void NotifyUnitDisabled(Unit unit)
    {
        if (unit is Aircraft aircraft)
        {
            // Killed on its own deck is not a war loss: the price comes back before the mission
            // leaves the table, because the mission record is what carries the price. See
            // AirCommand/CommanderAirCommandGroundLoss.cs.
            Instance?.RefundGroundLoss(aircraft);
            Instance?.RemoveMission(aircraft);
        }
    }

    internal static bool TryChooseMissionTarget(
        Unit searcher,
        List<WeaponStation> stations,
        out CombatAI.TargetSearchResults result)
    {
        result = default;
        if (Instance == null
            || searcher is not Aircraft aircraft
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission))
        {
            return false;
        }

        // Going home means no targets, from the moment the order is given. A landing order used to
        // leave Returning false until the 9 km hand-over, so an aircraft with gun ammunition left
        // — a Brawler's cannon, a Chicane's — kept picking fights all the way back instead of
        // flying the route, which read as RTB being ignored. Guns count as ordnance to the pilot AI.
        if (mission.Returning || mission.LandingBase != null)
        {
            result = new CombatAI.TargetSearchResults(null!, null!, 0f, true);
            return true;
        }

        result = Instance.ChooseMissionTarget(aircraft, stations, mission);
        // An empty rack ends a *mission*, not an order. A commanded aircraft with travel points
        // still to fly keeps flying them — otherwise a transport or a gun-only airframe turned for
        // home the instant it was told to go somewhere, which reads as the order being ignored.
        if (result.outOfAmmo && mission.RouteIndex >= mission.Route.Count)
        {
            mission.Returning = true;
        }
        return true;
    }

    private void HandleAircraftReturned(Aircraft aircraft)
    {
        if (missions.TryGetValue(aircraft, out AirMission mission))
        {
            // Basegame ReturnToInventory has just restored one airframe. Convert
            // that temporary purchased airframe back into its original funds. The refund itself
            // moved to TryRefundPurchase (AirCommand/CommanderAirCommandGroundLoss.cs, Reuse rule 4)
            // when the ground loss needed the same payment with nothing put back into stock.
            TryRefundPurchase(aircraft, mission, returnedToStock: true);
        }

        RemoveMission(aircraft);
    }

    internal static bool TryBuildAradSaturationTargets(
        Aircraft aircraft,
        WeaponStation station,
        out int targetCount)
    {
        targetCount = 0;
        if (Instance == null
            || aircraft == null
            || station == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.Returning
            || mission.Mode != AirCommandMode.Arad
            || !mission.SaturationAttack)
        {
            return false;
        }

        WeaponManager manager = aircraft.weaponManager;
        if (station.SalvoInProgress)
        {
            targetCount = manager.GetTargetList().Count;
            return true;
        }

        FactionHQ? hq = aircraft.NetworkHQ;
        WeaponInfo? info = station.WeaponInfo;
        if (hq == null || info == null || station.Ammo <= 0)
        {
            manager.ClearTargetList();
            return true;
        }

        List<Unit> candidates = new();
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo tracking = entry.Value;
            if (!tracking.TryGetUnit(out Unit target)
                || target == null
                || target.disabled
                || target.NetworkHQ == null
                || target.NetworkHQ == hq
                || !IsTargetEligible(target, AirCommandMode.Arad, targetOrdnance: false)
                || !FastMath.InRange(tracking.GetPosition(), mission.AreaCenter, mission.Radius)
                || !hq.IsTargetPositionAccurate(target, 1000f))
            {
                continue;
            }

            float range = FastMath.Distance(tracking.GetPosition(), aircraft.GlobalPosition());
            Vector3 direction = tracking.GetPosition() - aircraft.GlobalPosition();
            if (range < info.targetRequirements.minRange
                || range > info.targetRequirements.maxRange
                || Vector3.Angle(direction, aircraft.transform.forward) >= info.targetRequirements.minAlignment
                || (info.targetRequirements.lineOfSight && !target.LineOfSight(aircraft.transform.position, 1000f))
                || station.CalcOpportunityThreat(target.definition, aircraft).opportunity <= 0f)
            {
                continue;
            }

            candidates.Add(target);
        }

        manager.ClearTargetList();
        if (candidates.Count == 0)
        {
            return true;
        }

        int missileCount = Mathf.Min(station.Ammo, 32);
        for (int i = 0; i < missileCount; i++)
        {
            manager.AddTargetList(candidates[i % candidates.Count]);
        }

        targetCount = missileCount;
        return true;
    }

    internal static bool TryGetMissionHoldPoint(AIPilotCombatModes state, out GlobalPosition point)
    {
        point = default;
        if (Instance == null || Instance.missions.Count == 0)
        {
            return false;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null || !Instance.missions.TryGetValue(aircraft, out AirMission mission))
        {
            return false;
        }

        if (mission.Returning) return false;
        point = Instance.GetActiveRoutePoint(aircraft, mission);
        return true;
    }

    /// <summary>Current travel point, advancing the route as the aircraft reaches each one.</summary>
    private GlobalPosition GetActiveRoutePoint(Aircraft aircraft, AirMission mission)
    {
        // The sortie posture's hold (design.md, air-fallback-posture_20260916 Section 4.3): while it
        // is set the airframe holds there and the route is NOT advanced, so a fighter that falls back
        // resumes its route where it left it once the override is cleared.
        if (mission.HoldOverride is GlobalPosition hold)
        {
            return hold;
        }

        while (mission.RouteIndex < mission.Route.Count)
        {
            GlobalPosition point = mission.Route[mission.RouteIndex];
            if (FastMath.Distance(point, aircraft.GlobalPosition()) > AircraftWaypointRadiusMeters)
            {
                return point;
            }

            mission.RouteIndex++;
        }

        return mission.AreaCenter;
    }

    internal static void ApplyMissionTargetAltitude(AIPilotCombatModes state, FieldInfo? targetHeightField)
    {
        if (Instance == null || Instance.missions.Count == 0 || targetHeightField == null)
        {
            return;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.TargetAltitude <= 0f)
        {
            return;
        }

        targetHeightField.SetValue(state, mission.TargetAltitude);
    }

    /// <summary>
    /// The two ordinals of the game's private <c>AIPilotCombatModes.AttackMode</c> enum that mean the
    /// pilot has decided to LEAVE the fight: <c>BreakOffAttack</c> = 1 and <c>RetreatStandoff</c> = 2
    /// (enum declaration order in the decompiled game, <c>Assembly-CSharp.decompiled.cs:12316</c>:
    /// FlyingToTarget, BreakOffAttack, RetreatStandoff, NoTarget, Bombing, …). The enum is private, so
    /// it can only be read as a number; a game update that re-orders it must be re-read here, and the
    /// symptom would be a commanded fighter that either never breaks off or never holds its station.
    /// </summary>
    internal const int AttackModeBreakOffAttack = 1;

    /// <summary>See <see cref="AttackModeBreakOffAttack"/>.</summary>
    internal const int AttackModeRetreatStandoff = 2;

    /// <summary>Whether the game's pilot is disengaging right now, pure. Anything else — flying to a
    /// target, no target, a bombing run — is ordinary flying the commander's station keeping may
    /// clamp.</summary>
    internal static bool PilotIsDisengaging(int attackMode)
    {
        return attackMode == AttackModeBreakOffAttack || attackMode == AttackModeRetreatStandoff;
    }

    internal static void ConstrainMissionDestination(
        AIPilotCombatModes state, FieldInfo? destinationField, FieldInfo? attackModeField)
    {
        if (Instance == null || destinationField == null)
        {
            return;
        }

        // The game's own disengagement is left alone (audit 2026-09-16 Section 2: break-off and
        // retreat-to-standoff write their destination on the one-second check and were clamped back
        // toward the patrol box on the very next physics frame, so a fighter told by the game to run
        // for its airbase was held in the fight). Layer 1 of design.md, air-survival-layer_20260916:
        // the game's pilot owns the seconds.
        object? rawAttackMode = attackModeField?.GetValue(state);
        if (rawAttackMode != null && PilotIsDisengaging(Convert.ToInt32(rawAttackMode)))
        {
            return;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.Returning
            || mission.RouteIndex < mission.Route.Count
            // A falling-back airframe holds outside its area by design; pulling its attack destination
            // back to the area centre would fly it into the fight it has just left.
            || mission.HoldOverride != null
            || !KeepsStationInMissionArea(mission.Mode))
        {
            return;
        }

        object? rawDestination = destinationField.GetValue(state);
        if (rawDestination is not GlobalPosition destination)
        {
            return;
        }

        float radius = Mathf.Max(mission.Radius, 1000f);
        Vector3 destinationOffset = destination - mission.AreaCenter;
        destinationOffset.y = 0f;
        Vector3 aircraftOffset = aircraft.GlobalPosition() - mission.AreaCenter;
        aircraftOffset.y = 0f;

        float destinationLimit = radius * 0.75f;
        if (aircraftOffset.magnitude > radius * 0.9f)
        {
            destination = mission.AreaCenter;
        }
        else if (destinationOffset.magnitude > destinationLimit)
        {
            destination = mission.AreaCenter + destinationOffset.normalized * destinationLimit;
        }
        else
        {
            return;
        }

        destinationField.SetValue(state, destination);
    }

    /// <summary>Whether a target <paramref name="rangeMeters"/> from the airframe is beyond its
    /// self-defence radius, pure (design.md, air-fallback-posture_20260916 Section 4.3). A radius of
    /// zero or less is "off" and refuses nothing; exactly on the radius is still inside.</summary>
    internal static bool TargetOutsideSelfDefence(float rangeMeters, float radiusMeters)
    {
        return radiusMeters > 0f && rangeMeters > radiusMeters;
    }

    /// <summary>
    /// How close a hostile has to be before a falling-back airframe shoots at it whatever it is
    /// doing: 6,000 m. A hostile this close has already chosen the fight — it is inside a gun pass
    /// and well inside every missile's minimum useful range, so "leave it to the reinforcements"
    /// would mean sitting still while it shoots. Was the setting <c>AirSelfDefenceRadiusMeters</c>
    /// until 2026-09-16 (audit Section 2, design.md air-survival-layer_20260916 Layer 3): it is a
    /// property of how air combat works at that range, not a doctrine the commander retunes, and one
    /// setting fewer is one setting fewer to get wrong.
    /// </summary>
    internal const float SelfDefenceCloseMeters = 6000f;

    /// <summary>
    /// The nerve the game's own pilot needs before it takes a fight it rates as threatening: 0.35.
    /// Not a tuning knob — it is read straight off <c>CombatAI.ChooseHQTarget</c> in the decompiled
    /// game (<c>Assembly-CSharp.decompiled.cs:724</c>), and the whole point of
    /// <see cref="TargetIsTooDangerous"/> is that a commanded aircraft refuses the same fights an
    /// uncommanded one refuses. A game update that changes the number must be re-read here.
    /// </summary>
    internal const float BraveryRefusalNerve = 0.35f;

    /// <summary>The doubling the game applies to opportunity × bravery before both of the refusal's
    /// comparisons: 2. Same provenance as <see cref="BraveryRefusalNerve"/>.</summary>
    internal const float BraveryNerveMultiplier = 2f;

    /// <summary>How many times its weapon's maximum range a target must be beyond before the refusal
    /// applies at all: 2. Same provenance as <see cref="BraveryRefusalNerve"/>. It is what makes the
    /// rule a refusal to CHASE rather than a refusal to fight: a threatening target already within
    /// reach is shot at whatever the odds.</summary>
    internal const float BraveryRefusalRangeMultiple = 2f;

    /// <summary>
    /// The game's own refusal to take a fight, transcribed so the mod's replacement target chooser
    /// keeps it (audit <c>conductor/designs/2026-09-16-air-self-preservation-audit.md</c> Section 2:
    /// the Harmony prefix on <c>ChooseHQTarget</c> means the Basegame's only "do not take this fight"
    /// test never runs for a commanded aircraft, which is the single largest reason a commanded
    /// fighter flies at something it cannot beat).
    /// <para>
    /// All three clauses must hold: the airframe's nerve for this target — its opportunity times its
    /// bravery, doubled — is under <see cref="BraveryRefusalNerve"/>; the commander's own threat
    /// rating for that target is higher than that nerve; and the target is more than
    /// <see cref="BraveryRefusalRangeMultiple"/> times the chosen weapon's maximum range away. A
    /// braver airframe (bravery 1.0 against the stock 0.5) needs half the opportunity to clear the
    /// first clause, which is what bravery is for.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static bool TargetIsTooDangerous(
        float opportunity, float bravery, float hqThreat, float rangeMeters, float weaponMaxRangeMeters)
    {
        float nerve = opportunity * bravery * BraveryNerveMultiplier;
        return nerve < BraveryRefusalNerve
            && hqThreat > nerve
            && rangeMeters > weaponMaxRangeMeters * BraveryRefusalRangeMultiple;
    }

    /// <summary>How fast a hostile must be closing on a falling-back airframe to count as coming for
    /// it: 50 m/s. A fighter turning in to attack closes at two or three hundred; a patrol orbiting
    /// its own point reads near zero, and one flying away reads negative.</summary>
    internal const float SelfDefenceClosingSpeedMetersPerSecond = 50f;

    /// <summary>
    /// Whether a falling-back airframe may engage a target, pure (user report 2026-09-16: "Falling
    /// back aircraft are not firing or engaging, even when rushed by enemy aircraft"): anything
    /// inside <see cref="SelfDefenceCloseMeters"/>, wherever it is going — and anything CLOSING on it
    /// at <see cref="SelfDefenceClosingSpeedMetersPerSecond"/> or more that is already inside the
    /// weapon's own reach. Missiles are fired from fifteen or twenty kilometres, so a bubble of six
    /// alone was a fighter that never shot back. A hostile loitering far off, or flying away, is
    /// still left to the reinforcements.
    /// </summary>
    internal static bool SelfDefenceEngages(
        float rangeMeters, float closingSpeed, float weaponMaxRangeMeters)
    {
        if (!TargetOutsideSelfDefence(rangeMeters, SelfDefenceCloseMeters))
        {
            return true;
        }

        return closingSpeed >= SelfDefenceClosingSpeedMetersPerSecond && rangeMeters <= weaponMaxRangeMeters;
    }

    /// <summary>Metres per second at which <paramref name="target"/> is closing on
    /// <paramref name="aircraft"/>: positive when the gap is shrinking. Zero for a target with no
    /// rigidbody to read.</summary>
    private static float ClosingSpeed(Aircraft aircraft, Unit target)
    {
        Vector3 toUs = aircraft.transform.position - target.transform.position;
        if (toUs.sqrMagnitude < 1f)
        {
            return 0f;
        }

        Vector3 targetVelocity = target.rb != null ? target.rb.velocity : Vector3.zero;
        Vector3 ourVelocity = aircraft.rb != null ? aircraft.rb.velocity : Vector3.zero;
        return Vector3.Dot(targetVelocity - ourVelocity, toUs.normalized);
    }

    private CombatAI.TargetSearchResults ChooseMissionTarget(
        Aircraft aircraft,
        List<WeaponStation> stations,
        AirMission mission)
    {
        Unit? bestTarget = null;
        WeaponStation? bestStation = null;
        float bestOpportunity = 0f;
        float bestScore = 0f;
        float bestRange = 0f;
        // The radar airframe's exemption, as it has always been: a commanded AWACS/jammer with a
        // radar aboard is not judged on ammunition. The wider rule below catches the case this one
        // misses.
        bool radarWatch = mission.Mode == AirCommandMode.AwacsJammer && aircraft.radar != null;
        bool carriesEligibleStore = false;
        bool anyLoadedStore = false;
        FactionHQ? hq = aircraft.NetworkHQ;
        if (hq == null)
        {
            return new CombatAI.TargetSearchResults(null!, null!, 0f, true);
        }

        if (mission.ForcedTarget != null && mission.ForcedTarget.disabled)
        {
            mission.ForcedTarget = null;
        }

        for (int stationIndex = 0; stationIndex < stations.Count; stationIndex++)
        {
            WeaponStation station = stations[stationIndex];
            if (station == null || station.Cargo || !IsStationEligible(station, mission.Mode))
            {
                continue;
            }

            carriesEligibleStore = true;
            if (station.Ammo <= 0)
            {
                continue;
            }

            anyLoadedStore = true;
            foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
            {
                TrackingInfo tracking = entry.Value;
                if (!tracking.TryGetUnit(out Unit target)
                    || target == null
                    || target.disabled
                    || target.NetworkHQ == null
                    || target.NetworkHQ == hq
                    || !IsTargetEligible(target, mission.Mode, mission.TargetOrdnance))
                {
                    continue;
                }

                float range = Mathf.Max(FastMath.Distance(tracking.GetPosition(), aircraft.GlobalPosition()), 100f);
                bool commanded = ReferenceEquals(target, mission.ForcedTarget);
                // The self-defence radius (design.md, air-fallback-posture_20260916 Section 4.3): while
                // its sortie is falling back an airframe engages only what is close to IT — inside the
                // radius, wherever that is relative to the mission area it has left — and a commanded
                // target regardless. Anything farther is left to the reinforcements it has called for.
                if (!commanded
                    && mission.SelfDefenceOnly
                    && !SelfDefenceEngages(
                        range,
                        ClosingSpeed(aircraft, target),
                        station.WeaponInfo.targetRequirements.maxRange))
                {
                    continue;
                }

                bool inSelfDefence = mission.SelfDefenceOnly;
                if (TargetsRestrictedToMissionArea(mission.Mode)
                    && !commanded
                    && !inSelfDefence
                    && !FastMath.InRange(tracking.GetPosition(), mission.AreaCenter, mission.Radius))
                {
                    continue;
                }

                if (mission.Mode == AirCommandMode.AirGuard
                    && range > station.WeaponInfo.targetRequirements.maxRange * 1.05f)
                {
                    continue;
                }
                if (mission.Mode == AirCommandMode.AwacsJammer
                    && (range > station.WeaponInfo.targetRequirements.maxRange
                        || !target.LineOfSight(aircraft.transform.position, 1000f)))
                {
                    continue;
                }

                OpportunityThreat assessment = CombatAI.AnalyzeTarget(
                    station,
                    aircraft,
                    tracking,
                    0f,
                    range,
                    maxRangeMultiplier: 100f);
                float score = assessment.GetCombinedScore() / range;
                if (ReferenceEquals(target, mission.ForcedTarget))
                {
                    // A commanded target outranks anything the automatic search finds.
                    score *= 1000f;
                }
                float requiredAccuracy = mission.Mode == AirCommandMode.AwacsJammer ? 100f : 1000f;
                if (score <= bestScore || !hq.IsTargetPositionAccurate(target, requiredAccuracy))
                {
                    continue;
                }

                bestScore = score;
                bestOpportunity = assessment.opportunity;
                bestTarget = target;
                bestStation = station;
                bestRange = range;
            }
        }

        // The bravery refusal the Harmony prefix took away (Layer 1 of design.md,
        // air-survival-layer_20260916). A commanded target is exempt: the player asked for it, and
        // the mission's own forced target outranks everything else in the scorer above for the same
        // reason. The game weighs the HIGHEST opportunity any candidate offered; the loop above keeps
        // the best-SCORING candidate's, which is never higher, so this refuses at least as often as
        // the Basegame would and never less often. Exactly as the game does it, the station is left
        // in place and only the target and the opportunity are dropped.
        if (bestTarget != null
            && bestStation != null
            && !ReferenceEquals(bestTarget, mission.ForcedTarget)
            && TargetIsTooDangerous(
                bestOpportunity,
                aircraft.bravery,
                hq.GetAircraftThreat(bestTarget.persistentID),
                bestRange,
                bestStation.WeaponInfo.targetRequirements.maxRange))
        {
            bestTarget = null;
            bestOpportunity = 0f;
        }

        return new CombatAI.TargetSearchResults(
            bestTarget!,
            bestStation!,
            bestOpportunity,
            MissionIsOutOfAmmo(carriesEligibleStore, anyLoadedStore, radarWatch));
    }

    /// <summary>
    /// Whether a commanded airframe counts as out of ammunition — the test that ends a mission and
    /// turns the aircraft for home (see <see cref="TryChooseMissionTarget"/>).
    /// <para>
    /// An airframe that carries NO expendable store for its mission at all was never armed, so it
    /// can never be empty: it is a support airframe doing a support job. This is the rule the AWACS
    /// needed (user report, 2026-09-14: "it seems to have bought 2x AWACS"). A radar aeroplane on
    /// station carries its pod and nothing that can be shot off, so the old seed read it as empty
    /// the second it reached its orbit, sent it home to rearm, freed its sortie slot and let the
    /// commander buy another one — a rearm loop that put two radar airframes in the air at once.
    /// The explicit AWACS/jammer exemption stays as well, because it holds even for a radar
    /// airframe that IS carrying jammer stores.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    /// <param name="carriesEligibleStore">Any non-cargo store its mission mode can use is FITTED,
    /// whether or not it still has rounds in it.</param>
    /// <param name="anyLoadedStore">At least one of those stores still has ammunition.</param>
    /// <param name="radarWatch">The mission is the radar/jammer watch and the airframe has a radar
    /// aboard.</param>
    internal static bool MissionIsOutOfAmmo(bool carriesEligibleStore, bool anyLoadedStore, bool radarWatch)
    {
        if (radarWatch || !carriesEligibleStore)
        {
            return false;
        }

        return !anyLoadedStore;
    }
}
