using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The logistics watch: the cheap re-checks a delivery already under way needs far more often than
/// the thirty-second mission review runs (user, 2026-09-16: "FOB/insertion re-calculating needs to
/// happen more regularly - enemy presence can change rapidly and we need to redirect or abandon
/// missions if there's little hope of a successful delivery").
/// </summary>
/// <remarks>
/// It asks three questions of every open lift, FOB construction order and picket insertion, and
/// nothing else: is the way in still clear, is the ground at the end of it still clear, and is
/// anything covering the transport. Re-planning missions — which points are front, which want
/// platoons, what the order book should buy — stays on the review's own clock, because those
/// answers do not change in five seconds and working them out six times as often would cost a great
/// deal and buy nothing.
/// <para>
/// Each rule has ONE home. The lift standoff moved here out of <c>ReviewFobs</c> and the picket
/// insertion's two in-flight recalls moved here out of <c>PruneInsertions</c>; neither is called
/// from the review any more, so a rule cannot run twice with two different answers. What stayed in
/// the review is what needs the review's own state: the loss counting, the stall clock and the
/// stale-request valve, all of which are measured in minutes and none of which gets better at 5 s.
/// </para>
/// </remarks>
internal sealed partial class CommanderOperationsService
{
    /// <summary>
    /// Whether a delivery has little enough hope of arriving that it should be turned round, pure
    /// (user decision 2026-09-16). Three ways, and each on its own is enough:
    /// <list type="bullet">
    /// <item>the way in crosses tracked air defence — the launch gate's own refusal, asked again of
    /// the route that is actually left;</item>
    /// <item>the ground at the end of it is inside the standoff of a spotted hostile ground unit —
    /// a transport that lands there unloads into a column;</item>
    /// <item>the cover has no escort in the air at all while hostile aircraft are tracked near the
    /// route. Either half alone is survivable: an unescorted transport over an empty sky usually
    /// gets through, and a raid the escorts are actually fighting is what escorts are for. Together
    /// they are a transport flying to meet fighters with nothing over it.</item>
    /// </list>
    /// </summary>
    internal static bool DeliveryHopeless(
        bool routeThreatened, bool landingInsideStandoff, bool escortsLost, bool hostileAirNearRoute)
    {
        return routeThreatened || landingInsideStandoff || (escortsLost && hostileAirNearRoute);
    }

    /// <summary>
    /// Whether an order that has been waiting for a safe route has waited long enough to be given
    /// up, pure. Exactly on the limit counts as given up — the convention the package clocks use. A
    /// limit of zero or less never gives up, so the rule can be switched off by its own setting
    /// rather than by deleting the call; a wait that has not started (negative) never gives up.
    /// </summary>
    internal static bool LiftWaitedTooLong(float secondsWaiting, float minutes)
    {
        return minutes > 0f && secondsWaiting >= 0f && secondsWaiting >= minutes * 60f;
    }

    /// <summary>
    /// One logistics watch for every commanded HQ. Deliberately the same shape as
    /// <c>Review</c>'s own walk — one pass per commander, no allocation — because it runs six times
    /// as often and must stay cheap enough that nobody notices it.
    /// </summary>
    private void TickLogistics()
    {
        foreach (KeyValuePair<FactionHQ, OperationsState> entry in states)
        {
            FactionHQ hq = entry.Key;
            if (!hq.IsServer)
            {
                continue;
            }

            WatchLiftOrders(hq, entry.Value);
            WatchInsertionFlights(hq, entry.Value);
            // After the delivery watch, so a cover that falls back this tick is read by the hope rule
            // on the next one rather than a tick before its fighters have been told to turn
            // (CommanderOperationsAirPosture.cs; design.md, air-fallback-posture_20260916).
            WatchAirPosture(hq, entry.Value);
        }
    }

    /// <summary>
    /// Every open delivery, re-asked the questions its launch asked. The standoff check comes first
    /// — it can move or cancel the order outright, and there is no point weighing the route to
    /// ground the order is about to leave.
    /// </summary>
    private void WatchLiftOrders(FactionHQ hq, OperationsState state)
    {
        for (int i = state.FobOrders.Count - 1; i >= 0; i--)
        {
            if (i >= state.FobOrders.Count)
            {
                // Backwards, so a cancel taking this order off the list leaves every index still to
                // come valid. The bounds test is the belt to that brace, not a rule of its own.
                continue;
            }

            CommanderFobOrder order = state.FobOrders[i];
            if (order.Phase != CommanderFobPhase.Delivering || !order.ByAir)
            {
                continue;
            }

            if (!ReviewLiftStandoff(hq, state, order))
            {
                continue;
            }

            WatchDeliveryHope(hq, state, order);
        }
    }

    /// <summary>
    /// The hope rule for one open order. A delivery with no safe way in is turned round rather than
    /// flown into the guns: a platoon lift tries other ground first, and anything that cannot move
    /// waits on the deck for the route to clear, which most of them do — a suppressed belt, a column
    /// that drives on. Only a wait that outlasts <c>CommanderSettings.LiftHopelessMinutes</c> gives
    /// the order up.
    /// </summary>
    private void WatchDeliveryHope(FactionHQ hq, OperationsState state, CommanderFobOrder order)
    {
        string reason = DescribeDeliveryHopelessness(hq, state, order, out string kind);
        if (reason.Length == 0)
        {
            ClearDeliveryHopeless(hq, order);
            return;
        }

        // A platoon lift's first answer is other ground, not a wait: the objective still wants its
        // platoon and the forward landing zone rule exists to find somewhere else to put it down.
        if (order.Purpose == CommanderLiftPurpose.Platoon
            && order.Mission != null
            && TryDivertLift(hq, state, order, reason))
        {
            return;
        }

        if (order.HopelessSince < 0f)
        {
            order.HopelessSince = Time.time;
            WithdrawLiftFlights(hq, state, order, "there is no safe route");
        }

        // Said once per KIND of trouble, not once per tick: the reason carries a distance that moves
        // with the transport, and comparing the whole sentence re-logged the same recall every five
        // seconds (`lift for 9TH PLATOON recalled: hostile air defence tracked 7.2 km … 6.1 km … 5.3
        // km`, twelve lines for one recall, 2026-09-16).
        if (order.HopelessReported != kind)
        {
            order.HopelessReported = kind;
            CommanderAiLog.Note(
                hq,
                order.Purpose == CommanderLiftPurpose.Platoon
                    ? $"lift for {LiftLabel(order)} recalled: {reason}; waits for a clear route."
                    : $"{LiftLabel(order)}: the delivery waits on the deck: {reason}.");
        }

        if (!LiftWaitedTooLong(Time.time - order.HopelessSince, CommanderSettings.LiftHopelessMinutes))
        {
            return;
        }

        CancelFobOrder(
            hq,
            state,
            order,
            $"no safe route for {CommanderSettings.LiftHopelessMinutes:0} min",
            keepAirMobile: false);
    }

    /// <summary>The route is open again: the order stops waiting and the dispatch may send the next
    /// load. Said once, when it changes, like every other line this watch writes.</summary>
    private static void ClearDeliveryHopeless(FactionHQ hq, CommanderFobOrder order)
    {
        if (order.HopelessSince < 0f && order.HopelessReported.Length == 0)
        {
            return;
        }

        order.HopelessSince = -1f;
        order.HopelessReported = string.Empty;
        CommanderAiLog.Note(hq, $"{LiftLabel(order)}: the route is clear again; the delivery resumes.");
    }

    /// <summary>
    /// Moves a platoon lift to other ground when the way to this landing zone has closed. The zone
    /// takes the lift cooldown first so the search cannot hand back the ground it is leaving, and
    /// anything in the air flies on to the new zone (<c>MoveLiftOrder</c>). False when there is
    /// nowhere else, and the caller waits.
    /// </summary>
    private bool TryDivertLift(
        FactionHQ hq, OperationsState state, CommanderFobOrder order, string reason)
    {
        CommanderStrategicPoint old = order.Point;
        state.LiftCooldownUntil[old] = Time.time + FobPointCooldownMinutes * 60f;
        // The new zone must have a safe way in as well as clear ground (fix, 2026-09-16): the picker
        // judges the ground alone, and a lift diverted three times in fifteen seconds — HILLTOP 5,
        // CROSSROADS 1, CROSSROADS 12, each further short — because every zone it was handed sat
        // behind the same belt. A candidate whose route is tracked takes the lift cooldown like a
        // zone the lift was turned back from, and the search moves on, a bounded number of times.
        bool haveFrom = TryLiftRouteOrigin(hq, state, order, out GlobalPosition from);
        CommanderStrategicPoint moved = old;
        float shortMeters = 0f;
        bool found = false;
        for (int attempt = 0; attempt < LiftDivertCandidates && !found; attempt++)
        {
            if (!TryFindLiftLandingPoint(hq, state, order.Mission!, out moved, out shortMeters)
                || ReferenceEquals(moved, old))
            {
                break;
            }

            found = !haveFrom || !TryFindInsertionRouteThreat(hq, from, moved.Position, out _);
            if (!found)
            {
                state.LiftCooldownUntil[moved] = Time.time + FobPointCooldownMinutes * 60f;
            }
        }

        if (!found)
        {
            // Nowhere else: the cooldown on the zone it is still using would stop the order being
            // re-sent to it when the route clears, so it is lifted again.
            state.LiftCooldownUntil.Remove(old);
            return false;
        }

        MoveLiftOrder(hq, state, order, moved);
        CommanderAiLog.Note(
            hq,
            $"lift for {LiftLabel(order)} diverts to {moved.Label}: {reason}; it is now "
                + $"{shortMeters / 1000f:0} km short of the objective.");
        return true;
    }

    /// <summary>
    /// The live inputs behind <see cref="DeliveryHopeless"/> for one order, and the words for the
    /// log. Empty when the delivery still has a way in. The route is measured from where the
    /// transport actually IS when one is in the air — the question is what is left of the leg, not
    /// what the whole leg looked like when it was ordered — and from the base it would launch from
    /// otherwise. <paramref name="kind"/> names WHICH of the three rules fired, without the moving
    /// numbers, so the caller can tell a new kind of trouble from the same trouble measured again.
    /// </summary>
    private string DescribeDeliveryHopelessness(
        FactionHQ hq, OperationsState state, CommanderFobOrder order, out string kind)
    {
        kind = string.Empty;
        GlobalPosition zone = order.Point.Position;
        bool haveFrom = TryLiftRouteOrigin(hq, state, order, out GlobalPosition from);

        float threatMeters = float.MaxValue;
        bool routeThreatened = haveFrom && TryFindInsertionRouteThreat(hq, from, zone, out threatMeters);
        // Only a platoon lift is judged on the lift standoff here. A construction order's landing
        // ground has ALREADY been judged, one step earlier in this same watch, by the rule its own
        // site picker used — the contact range for a point the commander holds, the full enemy
        // standoff for one flown onto ground nobody holds. Asking it the lift's ten-kilometre rule as
        // well would give up on nearly every FOB on the map for a rule its launch never asked.
        bool landingInsideStandoff = order.Purpose == CommanderLiftPurpose.Platoon
            && !LiftLandingClearOfContacts(hq, zone);
        CommanderAirSortie? cover = FindLiftCover(state, order);
        // A cover that is falling back has no escort over the transport either (design.md,
        // air-fallback-posture_20260916 Section 4.5): its fighters are on their way toward the base,
        // so the transport waits or diverts exactly as it would with the escort shot down.
        bool escortsLost = cover == null || cover.FallingBack || CountLiftEscortsUp(cover) <= 0;
        int hostileAir = haveFrom ? CountHostileAirNearLift(hq, from, zone, true) : 0;
        if (!DeliveryHopeless(routeThreatened, landingInsideStandoff, escortsLost, hostileAir > 0))
        {
            return string.Empty;
        }

        if (routeThreatened)
        {
            kind = "route";
            return $"hostile air defence tracked {threatMeters / 1000f:0.0} km from the remaining route";
        }

        kind = landingInsideStandoff ? "landing" : "air";
        return landingInsideStandoff
            ? "the enemy is standing on the landing zone"
            : $"{hostileAir} hostile aircraft near the route and no escort in the air";
    }

    /// <summary>
    /// Where the rest of an order's route starts: the transport itself when one is in the air with
    /// this order's cargo, else the base a transport would launch from. False when neither exists,
    /// and the route cannot be judged. One definition for the hope rule and the divert (Reuse rule 4).
    /// </summary>
    private bool TryLiftRouteOrigin(
        FactionHQ hq, OperationsState state, CommanderFobOrder order, out GlobalPosition from)
    {
        for (int i = 0; i < state.FobFlights.Count; i++)
        {
            CommanderFobFlight flight = state.FobFlights[i];
            if (ReferenceEquals(flight.Order, order)
                && !flight.Delivered
                && flight.Aircraft != null
                && !flight.Aircraft.disabled)
            {
                from = flight.Aircraft.transform.GlobalPosition();
                return true;
            }
        }

        return TryFindInsertionLaunchBase(hq, order.Point.Position, out from);
    }

    /// <summary>How many landing zones a divert tries before it gives up and waits: 4. Each try is a
    /// full ranked-point pass plus a route check, six times a minute per stuck lift; four covers
    /// every zone a belt is likely to sit behind without the watch paying for the whole map.</summary>
    private const int LiftDivertCandidates = 4;

    /// <summary>
    /// The picket insertion's two in-flight recalls, moved here out of <c>PruneInsertions</c> so
    /// they run on the watch's clock rather than the review's (user decision 2026-09-16). The rules
    /// are unchanged: the launch gate re-asked over what is LEFT of the route, and the landing
    /// zone's own standoff re-asked against anything spotted. Both take the point's ordinary
    /// insertion cooldown and hand it back to the drive fill.
    /// </summary>
    private void WatchInsertionFlights(FactionHQ hq, OperationsState state)
    {
        for (int i = state.Insertions.Count - 1; i >= 0; i--)
        {
            CommanderInsertion insertion = state.Insertions[i];
            if (insertion.Delivered >= insertion.ExpectedLoads
                || insertion.Aircraft == null
                || insertion.Aircraft.disabled)
            {
                continue;
            }

            if (TryFindInsertionRouteThreat(
                    hq, insertion.Aircraft.GlobalPosition(), insertion.Lz, out float threatDistance))
            {
                RecallInsertion(
                    hq,
                    state,
                    insertion,
                    i,
                    $"hostile air defence tracked {threatDistance / 1000f:0.0} km from the remaining route");
                continue;
            }

            if (TryNearestTrackedHostile(
                    hq,
                    insertion.Lz,
                    CommanderSettings.OperationsHeliInsertionEnemyStandoffMeters,
                    CommanderSettings.StandoffContactMemorySeconds,
                    out _,
                    out float lzThreatDistance,
                    out string lzThreatLabel)
                && !InsertionLandingClear(
                    lzThreatDistance, CommanderSettings.OperationsHeliInsertionEnemyStandoffMeters))
            {
                RecallInsertion(
                    hq,
                    state,
                    insertion,
                    i,
                    $"hostile {(lzThreatLabel.Length > 0 ? lzThreatLabel : "ground unit")} tracked "
                        + $"{lzThreatDistance / 1000f:0.0} km from {insertion.Point.Label}");
            }
        }
    }

    /// <summary>Turns one picket flight round: the supply side's mission is withdrawn, the record is
    /// dropped, the point takes its insertion cooldown and the picket goes back to the drive fill.
    /// One definition for both of the watch's recalls (Reuse rule 4).</summary>
    private static void RecallInsertion(
        FactionHQ hq, OperationsState state, CommanderInsertion insertion, int index, string reason)
    {
        CommanderSupplyHeliService.Instance?.CancelInsertion(hq, insertion.Point);
        state.Insertions.RemoveAt(index);
        state.InsertionCooldownUntil[insertion.Point] =
            Time.time + CommanderSettings.OperationsHeliInsertionCooldownMinutes * 60f;
        CommanderAiLog.Note(
            hq,
            $"{insertion.Point.Label}: recalls the insertion flight; {reason}. Cooldown "
                + $"{CommanderSettings.OperationsHeliInsertionCooldownMinutes:0} min, the picket drives instead.");
    }

    /// <summary>The watch's own rules at their named boundaries (user decision 2026-09-16).</summary>
    private static void CheckLogistics(List<string> failures)
    {
        // The hope rule: any one of the three is enough, and the escort term needs both its halves.
        Expect(
            failures,
            "a clear route, a clear landing zone and an escort up is not hopeless",
            DeliveryHopeless(false, false, false, false),
            false);
        Expect(
            failures,
            "air defence on the remaining route is hopeless on its own",
            DeliveryHopeless(true, false, false, false),
            true);
        Expect(
            failures,
            "the enemy on the landing zone is hopeless on its own",
            DeliveryHopeless(false, true, false, false),
            true);
        Expect(
            failures,
            "no escort with raiders near the route is hopeless",
            DeliveryHopeless(false, false, true, true),
            true);
        Expect(
            failures,
            "no escort over an empty sky is not hopeless",
            DeliveryHopeless(false, false, true, false),
            false);
        Expect(
            failures,
            "raiders near the route with the escort up is not hopeless",
            DeliveryHopeless(false, false, false, true),
            false);
        Expect(
            failures,
            "every reason at once is still just hopeless",
            DeliveryHopeless(true, true, true, true),
            true);

        // The wait: a route that shuts is a delay, not a cancelled order, until it has cost too much.
        Expect(failures, "a delivery that has just started waiting is not given up", LiftWaitedTooLong(0f, 6f), false);
        Expect(failures, "five minutes of waiting is not yet given up", LiftWaitedTooLong(300f, 6f), false);
        Expect(failures, "exactly the limit gives the delivery up", LiftWaitedTooLong(360f, 6f), true);
        Expect(failures, "a second short of the limit does not", LiftWaitedTooLong(359f, 6f), false);
        Expect(failures, "a wait that never started never gives up", LiftWaitedTooLong(-1f, 6f), false);
        Expect(failures, "a limit of zero turns the rule off rather than giving up at once", LiftWaitedTooLong(9000f, 0f), false);

        // The clock itself: a watch no faster than the review would be the review.
        Expect(
            failures,
            "the logistics watch runs more often than the mission review; check the Operations section of the config",
            CommanderSettings.LogisticsWatchSeconds < ReviewIntervalSeconds,
            true);
        Expect(
            failures,
            "the logistics watch interval is positive; check the Operations section of the config",
            CommanderSettings.LogisticsWatchSeconds > 0f,
            true);
        Expect(
            failures,
            "a delivery waits for a route across several watches before it is given up; check the Operations section of the config",
            CommanderSettings.LiftHopelessMinutes * 60f > CommanderSettings.LogisticsWatchSeconds,
            true);
    }
}
