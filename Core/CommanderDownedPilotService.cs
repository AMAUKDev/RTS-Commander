using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Recovers pilots left standing on the ground (user report 2026-09-14: "a lot of downed pilots
/// waiting for rescue that should slowly auto-clear up" — each one is a live networked unit with
/// physics, a parachute and a capture check, and an hour of air attrition leaves dozens of them).
/// Every <see cref="SweepIntervalSeconds"/>, on the server, an AI pilot that has been on the ground
/// long enough (<c>DownedPilotRescueMinutes</c>) is handed to the game's own rescue path —
/// <c>PilotDismounted.Capture</c> by a friendly unit, which is exactly what a friendly helicopter
/// landing beside them does: the pilot is reported returned, the players see the game's own
/// "pilot rescued" message, and the object is destroyed. A human player's pilot is never touched.
/// </summary>
internal sealed class CommanderDownedPilotService : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>How often the ground is swept for pilots. Fifteen seconds: the rescue clock is
    /// minutes long, so a finer sweep only costs a scene walk.</summary>
    private const float SweepIntervalSeconds = 15f;

    /// <summary>Scratch list for the sweep, so no allocation per tick.</summary>
    private readonly List<PilotDismounted> pilots = new();

    /// <summary>When each pilot was first seen on the ground, by our clock. The game's own
    /// <c>timeSinceSpawn</c> cannot be used: <c>PilotDismounted.OnKinematicChanged</c> disables the
    /// component the moment the pilot goes kinematic, so its Update — and that clock — stop at about
    /// a minute, and a five-minute rule read off it never fired (350 pilots on the ground, none
    /// rescued, 2026-09-14).</summary>
    private readonly Dictionary<PilotDismounted, float> landedSince = new();

    private readonly List<PilotDismounted> landedPrune = new();

    private float nextSweepAt;

    public void ResetSession()
    {
        nextSweepAt = 0f;
        pilots.Clear();
        landedSince.Clear();
    }

    public void TickPersistent()
    {
        float minutes = CommanderSettings.DownedPilotRescueMinutes;
        if (minutes <= 0f
            || GameManager.gameResolution != GameResolution.Ongoing
            || !CommanderScheduler.IsDue(ref nextSweepAt, SweepIntervalSeconds))
        {
            return;
        }

        pilots.Clear();
        pilots.AddRange(Object.FindObjectsOfType<PilotDismounted>());
        int landed = 0;
        int rescued = 0;
        int deadOnGround = 0;
        int humanOnGround = 0;
        int noRescuer = 0;
        for (int i = 0; i < pilots.Count; i++)
        {
            PilotDismounted pilot = pilots[i];
            if (pilot == null || !pilot.IsServer || pilot.NetworkHQ == null)
            {
                continue;
            }

            if (!pilot.NetworkisKinematic)
            {
                continue;
            }

            landed++;
            if (!landedSince.TryGetValue(pilot, out float since))
            {
                since = Time.time;
                landedSince[pilot] = since;
            }

            if (pilot.disabled)
            {
                deadOnGround++;
            }
            else if (pilot.Networkplayer != null)
            {
                humanOnGround++;
            }

            if (!RescueDue(true, pilot.disabled, pilot.Networkplayer != null, Time.time - since, minutes))
            {
                continue;
            }

            Unit? rescuer = FindRescuer(pilot.NetworkHQ, pilot);
            if (rescuer == null)
            {
                noRescuer++;
                continue;
            }

            rescued++;
            CommanderAiLog.Note(
                pilot.NetworkHQ,
                $"rescue team recovers {CommanderGameAccess.GetUnitLabel(pilot)} after {(Time.time - since) / 60f:0} min on the ground.");
            landedSince.Remove(pilot);
            pilot.Capture(rescuer);
        }

        // Forget pilots that are gone (rescued by a real helicopter, captured, or despawned), so
        // the clock table cannot grow for the length of a match.
        landedPrune.Clear();
        foreach (KeyValuePair<PilotDismounted, float> entry in landedSince)
        {
            if (entry.Key == null || !pilots.Contains(entry.Key))
            {
                landedPrune.Add(entry.Key!);
            }
        }

        for (int i = 0; i < landedPrune.Count; i++)
        {
            landedSince.Remove(landedPrune[i]);
        }

        // The count line rides behind the operations debug switch and only every few sweeps, so a
        // match with no pilots down is not narrated: it exists to show the sweep is finding them.
        if (CommanderSettings.OperationsDebugLog && pilots.Count > 0 && ++sweepsSinceReport >= ReportEverySweeps)
        {
            sweepsSinceReport = 0;
            CommanderPlugin.Log.LogInfo(
                $"Downed pilots: {pilots.Count} in the world, {landed} on the ground ({deadOnGround} dead, {humanOnGround} human, "
                    + $"{noRescuer} with no friendly unit to credit), {rescued} recovered this sweep; oldest on the ground {OldestLandedMinutes():0.0} min.");
        }
    }

    /// <summary>How long the longest-landed pilot has been on the ground by this service's clock,
    /// for the count line.</summary>
    private float OldestLandedMinutes()
    {
        float oldest = 0f;
        foreach (KeyValuePair<PilotDismounted, float> entry in landedSince)
        {
            oldest = Mathf.Max(oldest, Time.time - entry.Value);
        }

        return oldest / 60f;
    }

    /// <summary>Count line every this many sweeps (two minutes at the 15 s sweep).</summary>
    private const int ReportEverySweeps = 8;

    private int sweepsSinceReport = ReportEverySweeps - 1;

    /// <summary>
    /// Whether a downed pilot is picked up this sweep, pure: on the ground long enough to have gone
    /// kinematic (the game sets that 35 s after landing, so a pilot still under a parachute is never
    /// taken), still alive, not a human player's pilot, and at least <paramref name="minutes"/> on
    /// the ground by this service's own clock. Zero or negative minutes disables the rescue.
    /// </summary>
    internal static bool RescueDue(bool landed, bool disabled, bool humanPilot, float secondsOnGround, float minutes)
    {
        return minutes > 0f
            && landed
            && !disabled
            && !humanPilot
            && secondsOnGround >= minutes * 60f;
    }

    /// <summary>The friendly unit credited with the pickup: the nearest live ground unit of the
    /// pilot's own faction, or null when it has none left — a faction with nothing on the map has
    /// nobody to send.</summary>
    private static Unit? FindRescuer(FactionHQ hq, PilotDismounted pilot)
    {
        if (hq.factionUnits == null)
        {
            return null;
        }

        Unit? nearest = null;
        float nearestDistance = float.MaxValue;
        Vector3 here = pilot.transform.position;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit == null || unit.disabled || unit is Aircraft || unit is PilotDismounted)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(unit.transform.position, here);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = unit;
            }
        }

        return nearest;
    }

    /// <summary>The rescue clock at its boundaries, run at plugin load beside the other services'
    /// self-checks.</summary>
    internal static void SelfCheck()
    {
        List<string> failures = new();
        Expect(failures, "a pilot landed five minutes is rescued", RescueDue(true, false, false, 300f, 5f), true);
        Expect(failures, "a pilot landed four minutes waits", RescueDue(true, false, false, 240f, 5f), false);
        Expect(failures, "a pilot still in the air is never rescued", RescueDue(false, false, false, 900f, 5f), false);
        Expect(failures, "a dead pilot is left to the game's own despawn", RescueDue(true, true, false, 900f, 5f), false);
        Expect(failures, "a human player's pilot is never auto-rescued", RescueDue(true, false, true, 900f, 5f), false);
        Expect(failures, "zero minutes disables the rescue", RescueDue(true, false, false, 900f, 0f), false);
        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Downed pilot self-check FAILED: {failures[i]}");
        }
    }

    private static void Expect(List<string> failures, string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }
}
