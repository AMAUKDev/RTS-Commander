using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The per-aircraft survival check (design.md, air-survival-layer_20260916 Layer 2; user decision
/// 2026-09-16: "insert some sense of self-preservation into our aircraft"). One question, asked of
/// every commanded airframe on the existing two-second mission clock: should this aeroplane leave?
/// Three reasons say yes — its racks are empty and its route is flown, its fuel is under
/// <c>CommanderSettings.AirSurvivalFuelFraction</c>, or the game's own pilot has already put itself
/// into the landing state while the commander's books still say the aeroplane is on station.
/// </summary>
/// <remarks>
/// Nothing here picks a target and nothing here buys an airframe. The action is always
/// <see cref="RequestReturnToBase"/>, the one door that already handles rotary pads, deck recovery
/// and relaunch; the landing-state case only marks the mission returning, because the aeroplane is
/// already flying the approach and switching its pilot state again would restart it.
/// <para>
/// The player's own Air Command missions are EXEMPT by design (design Section 3: survival beats the
/// sortie, never the player's own orders), so only airframes the commander itself launched — the ones
/// <c>CommanderOperationsService.IsCommanderAirframe</c> knows — are judged. A human in the cockpit
/// is never touched at all.
/// </para>
/// <para>
/// This file also holds the one out-of-ammo definition (audit
/// <c>conductor/designs/2026-09-16-air-self-preservation-audit.md</c> Section 2: it used to be
/// decided twice with different rules). <see cref="MissionIsOutOfAmmo"/> is the pure rule and lives
/// beside the chooser that first needed it; <see cref="IsWinchester"/> is its runtime reader, moved
/// here from <c>CommanderAirCommandService.cs</c>.
/// </para>
/// </remarks>
internal sealed partial class CommanderAirCommandService
{
    /// <summary>
    /// True when this airframe has nothing left to shoot with but its guns — "Winchester" in the
    /// idle sweep's sense (design SS6). Guns are deliberately not counted: a pilot with cannon
    /// rounds left keeps making strafing runs, which is the behaviour the wing's INTERNAL CANNONS
    /// rule already exists to stop. Cargo stations are not weapons.
    /// <para>
    /// The runtime reader of <see cref="MissionIsOutOfAmmo"/> (Reuse rule 4), moved here from
    /// <c>CommanderAirCommandService.cs</c> on 2026-09-16. The two readers ask the same pure question
    /// of different stations, and the difference is deliberate: the chooser asks whether the store
    /// its MISSION MODE can use was ever fitted, so a radar aeroplane carrying only a pod is never
    /// "empty"; the idle sweep asks whether the airframe has any stations at all, so a transport with
    /// nothing but cargo racks and a fighter that has fired its last missile both read as finished
    /// and are sent home. An airframe with no station list at all is not judged by either.
    /// </para>
    /// </summary>
    internal static bool IsWinchester(Aircraft? aircraft)
    {
        List<WeaponStation>? stations = aircraft?.weaponStations;
        if (stations == null || stations.Count == 0)
        {
            return false;
        }

        bool anyLoadedStore = false;
        for (int i = 0; i < stations.Count; i++)
        {
            WeaponStation station = stations[i];
            if (station != null
                && !station.Cargo
                && station.WeaponInfo != null
                && !station.WeaponInfo.gun
                && station.Ammo > 0)
            {
                anyLoadedStore = true;
                break;
            }
        }

        return MissionIsOutOfAmmo(carriesEligibleStore: true, anyLoadedStore, radarWatch: false);
    }

    /// <summary>
    /// Whether an airframe at <paramref name="fuelLevel"/> (a share of a full load, 0 to 1) is low
    /// enough on fuel to turn for home, pure. A fraction of zero or less is "off" and sends nobody
    /// home; exactly on the fraction counts as low, the same limit-inclusive convention the wing's
    /// other clocks use.
    /// </summary>
    internal static bool FuelBelow(float fuelLevel, float fraction)
    {
        return fraction > 0f && fuelLevel <= fraction;
    }

    /// <summary>Airframes that have already had a survival line written about them, so the log says
    /// each thing once rather than every two seconds. Cleared on <c>ResetSession</c> with the rest of
    /// the mod's memory.</summary>
    private readonly HashSet<Aircraft> survivalReported = new();

    /// <summary>Scratch for one survival pass, so the walk never mutates the mission dictionary it is
    /// reading.</summary>
    private readonly List<Aircraft> survivalCandidates = new();

    /// <summary>Forgets which airframes have been reported. Called from <c>ResetSession</c>.</summary>
    private void ResetSurvival()
    {
        survivalReported.Clear();
        survivalCandidates.Clear();
    }

    /// <summary>
    /// One survival pass over every commander-issued mission, on the two-second mission clock. Runs
    /// after <c>ProcessReturningMissions</c> so an airframe that is already going home is skipped by
    /// the <c>Returning</c> test rather than ordered home twice.
    /// </summary>
    private void TickSurvival()
    {
        survivalCandidates.Clear();
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            Aircraft aircraft = entry.Key;
            AirMission mission = entry.Value;
            if (aircraft == null
                || aircraft.disabled
                || aircraft.Player != null
                || mission.Returning
                || mission.LandingBase != null
                || !mission.Hq.IsServer
                || !CommanderOperationsService.IsCommanderAirframe(mission.Hq, aircraft))
            {
                continue;
            }

            survivalCandidates.Add(aircraft);
        }

        for (int i = 0; i < survivalCandidates.Count; i++)
        {
            Aircraft aircraft = survivalCandidates[i];
            if (missions.TryGetValue(aircraft, out AirMission mission))
            {
                JudgeOneAirframe(aircraft, mission);
            }
        }

        survivalCandidates.Clear();
    }

    /// <summary>The three reasons an airframe leaves, in the order they are asked. Empty racks first:
    /// it is the only one of the three that is about the mission being finished rather than the
    /// aeroplane being in trouble.</summary>
    private void JudgeOneAirframe(Aircraft aircraft, AirMission mission)
    {
        FactionHQ hq = mission.Hq;
        string label = CommanderGameAccess.GetUnitLabel(aircraft);

        // An empty rack ends a mission, not an order: travel points still to fly are flown first,
        // exactly as the target chooser has always read it (CommanderAirCommandPilotHooks.cs).
        if (IsWinchester(aircraft) && mission.RouteIndex >= mission.Route.Count)
        {
            RequestReturnToBase(aircraft);
            ReportSurvival(hq, aircraft, $"{label} goes home: out of ammo.");
            return;
        }

        if (FuelBelow(aircraft.GetFuelLevel(), CommanderSettings.AirSurvivalFuelFraction))
        {
            RequestReturnToBase(aircraft);
            ReportSurvival(hq, aircraft, $"{label} goes home: fuel at {aircraft.GetFuelLevel():P0}.");
            return;
        }

        // Audit gap 2: the game's own fuel checker switches the pilot into the landing state at 20 %
        // and tells the mod nothing, so the sortie kept counting the aeroplane as on station and the
        // deck-recovery sweep skipped it — which is the path the recovery file documents as where
        // these airframes die. The pilot is already flying the approach, so the mission is marked
        // returning and ISSUED rather than switched again: switching the state a second time would
        // restart the approach.
        if (PilotIsLanding(aircraft))
        {
            mission.Returning = true;
            mission.RtbIssued = true;
            ReportSurvival(hq, aircraft, $"{label}: the pilot is landing for fuel; the sortie frees its slot.");
        }
    }

    /// <summary>Whether any of this airframe's pilots is already flying the game's own landing
    /// approach. Fixed wing only: a helicopter's landing state is a different state machine and the
    /// rotary posture is out of scope for this layer (design Section 8).</summary>
    private static bool PilotIsLanding(Aircraft aircraft)
    {
        Pilot[]? pilots = aircraft.pilots;
        if (pilots == null)
        {
            return false;
        }

        for (int i = 0; i < pilots.Length; i++)
        {
            if (pilots[i] != null && pilots[i].currentState is AIPilotLandingState)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One survival line per airframe, whatever the reason: the check runs every two seconds
    /// and the commander log holds two hundred entries.</summary>
    private void ReportSurvival(FactionHQ hq, Aircraft aircraft, string text)
    {
        if (!survivalReported.Add(aircraft))
        {
            return;
        }

        CommanderAiLog.Note(hq, text);
    }
}
