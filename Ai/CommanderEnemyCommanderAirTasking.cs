using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Denials, tasking the wing, tracking launches and losses. Split out of <c>CommanderEnemyCommanderAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past three thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>
    /// Says why no aircraft was bought. A NEW reason logs at once; a reason that keeps repeating —
    /// which is the interesting one, because it means the wing is stuck — re-logs on the holds
    /// cadence (<see cref="HoldReportEveryReviews"/>), the same as <c>ReportHold</c>. Pure
    /// once-per-reason proved to mean once per MATCH for a reason that never changes: the first
    /// ladder build logged "its strips accept no … Fighter airframe at all" once and then went
    /// silent with a 400+ fund and a whole match of unfilled CAP demand (user report,
    /// 2026-09-14). A successful buy resets the reason, so the next refusal is newsworthy again.
    /// </summary>
    private static void ReportAirDenial(FactionHQ hq, CommanderState state, string reason)
    {
        if (state.LastAirDenial == reason)
        {
            state.AirDenialReviews++;
            if (state.AirDenialReviews % HoldReportEveryReviews != 0)
            {
                return;
            }
        }
        else
        {
            state.AirDenialReviews = 0;
        }

        state.LastAirDenial = reason;

        // Which half of the wing was refused, and what the other half did this review (fix,
        // 2026-09-14). The dedup key above is the REASON alone and stays a stable string, so the
        // context can move review to review without re-logging a refusal that has not changed.
        string side = string.IsNullOrEmpty(state.AirBuySide)
            ? string.Empty
            : $"[{state.AirBuySide}] ";
        string context = string.IsNullOrEmpty(state.AirDenialContext)
            ? string.Empty
            : $" (earlier this review: {state.AirDenialContext})";
        CommanderAiLog.Note(hq, $"{side}bought no aircraft: {reason}{context}.");
    }

    /// <summary>
    /// The wing's standing posture: every airframe this commander bought that carries no mission at
    /// all holds <see cref="CommanderAirCommandService.AirCommandMode.AirGuard"/> over home
    /// territory — a commander with nothing overhead loses its mines and its factories to the first
    /// thing that flies over, and the player asked to be met on the way in rather than only shot at
    /// once on top of the enemy.
    /// <para>
    /// Sortie tasking lives in the operations air step; this is only the residual, and it never
    /// touches anything outside the commander's own set (decision 5: the player's Air Command
    /// missions are already mission-bound, and a stock mission's authored free aircraft carry no
    /// claim). The old StrategicStrike over the opponent's opening airbase is gone — that was the
    /// separate air brain.
    /// </para>
    /// </summary>
    private void TaskAirWing(FactionHQ hq)
    {
        ReportLostAircraft(hq);
        ReviewAirAttrition(hq);
        if (hq.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is Aircraft aircraft && !unit.disabled)
            {
                TrackAircraft(hq, aircraft);
            }
        }

        // The residual posture is the operations air step's idle sweep now (design.md,
        // smarter-air-wing_20260914 Section 6, Reuse rule 4): the loop that used to live here did
        // the same walk with one fewer decision — it could not send a transport or a Winchester
        // airframe home — so the two were merged rather than kept side by side.
        CommanderOperationsService.SweepIdleAirframes(hq);
    }

    /// <summary>
    /// Where a commander sends its strikes: the airbase the opponent started the mission holding.
    /// </summary>
    /// <remarks>
    /// This used to be <c>GetTerritoryCenter</c> — the average position of every airbase the player
    /// holds. On a duel that starts one-base-each it is the right answer for about five minutes, and
    /// then the player takes Maris or Sandrift and the "target" slides off into open desert halfway
    /// between their bases, so a strike package flies to an empty patch of ground, finds nothing
    /// inside its area filter and goes home. The opening base does not move, the enemy is told about
    /// it at the first review (see <c>RevealPlayerBase</c>), and it is what the player means by
    /// their main base — so it is remembered once and kept. It is only re-picked if the player loses
    /// it outright.
    /// </remarks>
    private static GlobalPosition GetStrikeTarget(CommanderState state, FactionHQ opponent)
    {
        if (state.StrikeBase == null
            || state.StrikeBase.disabled
            || state.StrikeBase.center == null
            || !opponent.ContainsAirbase(state.StrikeBase))
        {
            state.StrikeBase = null;
            foreach (Airbase airbase in opponent.GetAirbases())
            {
                if (airbase != null && !airbase.disabled && airbase.center != null)
                {
                    state.StrikeBase = airbase;
                    break;
                }
            }
        }

        return state.StrikeBase != null && state.StrikeBase.center != null
            ? state.StrikeBase.center.GlobalPosition()
            : CommanderCaptureService.GetTerritoryCenter(opponent);
    }

    /// <summary>
    /// Airframes destroyed on the ground at one of their own airbases and already paid back, with
    /// the loss line Air Command wrote for each. Filled by
    /// <see cref="NoteAirframeRefundedOnDeck"/> the instant the hull is disabled and drained by
    /// <see cref="ReportLostAircraft"/> at the next air review, so the faction's own ledger says
    /// what happened in its own voice instead of a second service logging the same news.
    /// </summary>
    private readonly Dictionary<Aircraft, string> groundLossRefunds = new();

    /// <summary>
    /// Air Command telling this ledger that <paramref name="aircraft"/> was killed on its own deck
    /// and refunded. False when this commander was not tracking the airframe at all — a player's
    /// own Air Command sortie in a faction with no commander running — in which case Air Command
    /// logs the line itself. Exactly one line either way.
    /// </summary>
    internal static bool NoteAirframeRefundedOnDeck(Aircraft? aircraft, string line)
    {
        if (aircraft == null || Instance == null || !Instance.airborneSince.ContainsKey(aircraft))
        {
            return false;
        }

        Instance.groundLossRefunds[aircraft] = line;
        return true;
    }

    /// <summary>Drops refund lines whose airframe the tracker no longer holds, so a deck loss the
    /// loss sweep never reported cannot sit in the table for the rest of the match. Scratch list is
    /// allocated only when there is something to drop, which is almost never.</summary>
    private void PruneGroundLossRefunds()
    {
        if (groundLossRefunds.Count == 0)
        {
            return;
        }

        List<Aircraft>? stale = null;
        foreach (KeyValuePair<Aircraft, string> entry in groundLossRefunds)
        {
            if (entry.Key == null || !airborneSince.ContainsKey(entry.Key))
            {
                (stale ??= new List<Aircraft>()).Add(entry.Key!);
            }
        }

        for (int i = 0; stale != null && i < stale.Count; i++)
        {
            groundLossRefunds.Remove(stale[i]);
        }
    }

    private void TrackAircraft(FactionHQ hq, Aircraft aircraft)
    {
        if (!airborneSince.TryGetValue(aircraft, out TrackedAirframe tracked))
        {
            tracked = new TrackedAirframe
            {
                Since = Time.time,
                Name = aircraft.definition != null ? aircraft.definition.unitName : "aircraft",
            };
            airborneSince[aircraft] = tracked;
        }

        // Re-read every tick, so an airframe that changes hands is reported by whoever holds it.
        tracked.Owner = hq;
        tracked.LastPosition = aircraft.transform.GlobalPosition();
        tracked.LastRadarAlt = aircraft.radarAlt;
    }

    /// <summary>
    /// Says how long each enemy airframe lasted once it leaves the world.
    /// </summary>
    /// <remarks>
    /// The fifth playtest logged twenty-six launches over twenty minutes and the player never saw an
    /// airstrike, which the log could not explain: a launch line and then silence looks identical
    /// whether the aeroplane was shot down on the way in, flew home and was recovered into the
    /// inventory, or hit a hill. Without this line the next playtest reports the same nothing.
    /// </remarks>
    private void ReportLostAircraft(FactionHQ hq)
    {
        lostAircraft.Clear();
        foreach (KeyValuePair<Aircraft, TrackedAirframe> entry in airborneSince)
        {
            // Only this faction's own airframes. The table is shared by every commanded faction, and
            // without the owner test the first HQ the review loop reaches drains the lot under its
            // own name — which is why the player's transports had no fate line at all and the
            // enemy's loss counts were inflated by the player's (measured, 2026-09-16).
            // Considered and accepted: entries whose faction stops being commanded mid-match are no
            // longer drained by anyone and sit here until ResetSession. That is bounded by one
            // faction's aircraft count and costs a few small objects; draining them under a faction
            // that does not own them is the bug this test exists to fix.
            if (!ReferenceEquals(entry.Value.Owner, hq))
            {
                continue;
            }

            if (entry.Key == null || entry.Key.disabled)
            {
                lostAircraft.Add(entry.Key!);
            }
        }

        for (int i = 0; i < lostAircraft.Count; i++)
        {
            Aircraft aircraft = lostAircraft[i];
            TrackedAirframe tracked = airborneSince[aircraft];
            // Killed on its own deck and already paid back (user instruction, 2026-09-16). It is a
            // loss, so it is not silenced the way a recovery is, but the line says where it died and
            // that the money came back rather than reading as a hull lost in the war.
            if (groundLossRefunds.TryGetValue(aircraft, out string refundLine))
            {
                groundLossRefunds.Remove(aircraft);
                recoveredAirframes.Remove(aircraft);
                CommanderAiLog.Note(hq, refundLine);
                airborneSince.Remove(aircraft);
                continue;
            }

            if (recoveredAirframes.Remove(aircraft))
            {
                // Home, or written off on the deck by the stuck rule: not a loss, and said so, or
                // the "lost … at 0 m" lines cannot be told from aircraft destroyed on the ground.
                CommanderAiLog.Note(hq, $"recovered {tracked.Name} after {Time.time - tracked.Since:0} s in the air.");
                airborneSince.Remove(aircraft);
                continue;
            }

            // Where it was last seen, against the commander's nearest own base: a loss a few hundred
            // metres from its own deck at 30 s is the deck killing it; one 40 km out is the war.
            string baseLabel = CommanderEconomyService.NearestHeldBaseLabel(hq, tracked.LastPosition);
            float baseMeters = NearestOwnBaseMeters(hq, tracked.LastPosition);
            // Shot at or crashed (diagnostics, 2026-09-16): the object is usually still there,
            // disabled, when the tracker notices, so the game's damage ledger can still be read.
            string credit = CommanderGameAccess.DescribeDamageCredit(aircraft);
            CommanderAiLog.Note(
                hq,
                $"lost {tracked.Name} after {Time.time - tracked.Since:0} s in the air; last seen "
                    + $"{baseMeters / 1000f:0.0} km from {baseLabel} at {tracked.LastRadarAlt:0} m above ground"
                    + (credit.Length > 0 ? $", {credit}" : string.Empty) + ".");
            airborneSince.Remove(aircraft);
        }

        lostAircraft.Clear();
        // A recovered airframe the tracker never saw (a transport, which the wing does not track)
        // would otherwise sit in the set for the match. Same for a refunded deck loss.
        recoveredAirframes.RemoveWhere(recovered => recovered == null || !airborneSince.ContainsKey(recovered));
        PruneGroundLossRefunds();
    }

    /// <summary>Metres from <paramref name="position"/> to the nearest airbase this faction holds,
    /// or a very large number when it holds none. The search itself moved to
    /// <see cref="TryNearestOwnBase"/> (2026-09-16, Reuse rule 3) when the air fallback posture
    /// needed the base as well as the distance; the loss line keeps its name.</summary>
    private static float NearestOwnBaseMeters(FactionHQ hq, GlobalPosition position)
    {
        TryNearestOwnBase(hq, position, out _, out float meters);
        return meters;
    }

    /// <summary>The airbase this faction holds nearest to <paramref name="position"/> and the
    /// horizontal metres to it. False, with a null base and a very large distance, when it holds
    /// none. Internal (one-word widening): the operations air fallback posture falls back toward it
    /// (design.md, air-fallback-posture_20260916 Section 4.3).</summary>
    internal static bool TryNearestOwnBase(
        FactionHQ hq, GlobalPosition position, out Airbase? airbase, out float meters)
    {
        airbase = null;
        meters = float.MaxValue;
        foreach (Airbase candidate in hq.GetAirbases())
        {
            if (candidate == null || candidate.center == null)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                candidate.center.GlobalPosition().AsVector3(), position.AsVector3());
            if (distance < meters)
            {
                meters = distance;
                airbase = candidate;
            }
        }

        return airbase != null;
    }

    /// <summary>
    /// Whether one aircraft definition is a transport, memoised. <see cref="GetAirRole"/> is the
    /// mod's one definition of that question (Reuse rule 4) but it reaches into the prefab through
    /// <c>CommanderAirCommandService.HasPlanePilot</c>, which is a Unity component lookup; the
    /// question is asked once per live aircraft per air buy and again for every escort sizing, so the
    /// answer is kept. Aircraft definitions are shared assets that live for the whole process, so the
    /// memo needs no sweep and cannot go stale.
    /// </summary>
    private static readonly Dictionary<AircraftDefinition, bool> transportDefinitions = new();

    /// <summary>The memoised transport test over a live unit.</summary>
    private static bool IsTransportAircraft(Unit unit)
    {
        if (unit.definition is not AircraftDefinition definition)
        {
            return false;
        }

        if (!transportDefinitions.TryGetValue(definition, out bool transport))
        {
            transport = GetAirRole(definition) == AirRole.Transport;
            transportDefinitions[definition] = transport;
        }

        return transport;
    }

    /// <summary>Aircraft this faction currently has in the world, pilots and AI alike, EXCEPT its
    /// transports. Internal (one-word widening): the strike package's escort is capped by the room
    /// left under the airborne ceiling, and the ceiling and the count have to be the same pair the
    /// buy loop reads or the package would be sized against a different sky (Reuse rule 4).
    /// <para>
    /// Transports came out on 2026-09-18 by user decision (air-ceiling_20260918 §4 decision B): a
    /// lift must never be blocked by a full sky. It still may not fly into contested air without its
    /// escort — that gate is <c>Operations/CommanderOperationsFob.cs:2320</c> and is untouched — so
    /// what this buys is that the LIFT is free, not that it goes unprotected. Because the ceiling and
    /// this count are deliberately one pair, escort sizing sees the same change, which is intended:
    /// the room a package measures itself against is room for COMBAT aircraft.
    /// </para></summary>
    internal static int CountAirborne(FactionHQ hq)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is Aircraft && !unit.disabled && !IsTransportAircraft(unit))
            {
                count++;
            }
        }

        return count;
    }
}
