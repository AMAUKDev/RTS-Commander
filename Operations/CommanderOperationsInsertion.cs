using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Picket insertion by transport helicopter (design.md, heli-picket-insertion_20260913): a rear
/// picket point farther than <c>OperationsHeliInsertionOffRoadMeters</c> from the nearest road is
/// filled by air — the operations service requests a transport, the two picket vehicles are bought
/// as cargo mounts at heli spawn, the supply service flies the whole buy-fly-land-unload-return
/// chain it already runs for SAM sites, and the delivered vehicles are adopted straight into the
/// picket mission's <c>PicketMembers</c>.
/// </summary>
/// <remarks>
/// This partial owns the demand side only: the gate, the per-point loss cooldown, the insertion
/// records and the notifications the supply side calls back on. The flying side is
/// <see cref="CommanderSupplyHeliService"/>'s, generalised to any commanded HQ (Reuse rule 5: the
/// second programmatic caller after <c>RequestSamSiteFoundationDrop</c>, parameterised rather than
/// forked). Host-only like everything here — the review that drives it already is.
/// </remarks>
internal sealed partial class CommanderOperationsService
{
    /// <summary>
    /// One live insertion: the picket mission it serves, the transport once one has been assigned,
    /// and how many of the expected vehicles have actually reached the ground.
    /// </summary>
    internal sealed class CommanderInsertion
    {
        /// <summary>The picket mission this flight delivers for; its <c>Point</c> is the target.</summary>
        internal CommanderOperationsMission Mission = null!;

        /// <summary>The control point being reinforced — the cooldown's key.</summary>
        internal CommanderStrategicPoint Point = null!;

        /// <summary>The transport, once the supply side has matched the spawn to this insertion.</summary>
        internal Aircraft? Aircraft;

        /// <summary>The landing post inside the point's ring the flight delivers to.</summary>
        internal GlobalPosition Lz;

        /// <summary>How many vehicles the flight carries — the picket minimum, by design.</summary>
        internal int ExpectedLoads = 2;

        /// <summary>How many of them have been adopted into the picket so far.</summary>
        internal int Delivered;

        /// <summary>Scaled <c>Time.time</c> the request was made, for the stale-request valve
        /// below.</summary>
        internal float RequestedAt;
    }

    /// <summary>
    /// How long a request may sit without a transport ever spawning before the record is dropped:
    /// six reviews. A record with no aircraft blocks its point (rightly — double-ordering is
    /// worse), but a queue that has not drained in six reviews is not going to, and the point
    /// should be allowed to fall back to driving.
    /// </summary>
    private const float StaleInsertionSeconds = 180f;

    /// <summary>
    /// The demand gate, pure: a picket is flown in only when it is rear, short-handed, unbound and
    /// off-cooldown, farther than <paramref name="offRoadMeters"/> from the nearest road (the
    /// user's gate — a roadless map reports <c>float.MaxValue</c> and so always flies), and the
    /// commander is under its airborne insertion limit. Inclusive boundary on the driving side,
    /// the convention the rest of the mod uses: exactly on the gate, the point drives.
    /// </summary>
    internal static bool QualifiesForInsertion(
        bool rear,
        bool shortHanded,
        bool unbound,
        bool cooldownLive,
        float nearestRoadDistance,
        float offRoadMeters,
        int inFlight,
        int limit)
    {
        return rear
            && shortHanded
            && unbound
            && !cooldownLive
            && nearestRoadDistance > offRoadMeters
            && inFlight < limit;
    }

    /// <summary>
    /// The insertion's two-vehicle load, pure (design Decision 8: doctrine over bargains — the
    /// insertion is a purchase, and a rear point's threat is aircraft): one air-defence vehicle
    /// plus the cheapest other, both within <paramref name="budget"/>. No affordable air-defence
    /// candidate → the two cheapest others. The partner falls back to any role (a second
    /// air-defence vehicle included) when no non-air-defence vehicle is affordable, so a
    /// lopsided catalog still yields a full two-vehicle load; fewer than two affordable
    /// vehicles → no picks at all, and the caller declines.
    /// </summary>
    internal static void PickInsertionCargo(
        IReadOnlyList<CommanderPlatoonRole> roles,
        IReadOnlyList<float> values,
        float budget,
        List<int> picks)
    {
        picks.Clear();
        int airDefence = CheapestInsertionCandidate(roles, values, budget, airDefenceOnly: true, exclude: -1);
        if (airDefence >= 0)
        {
            int partner = CheapestInsertionCandidate(
                roles, values, budget - values[airDefence], airDefenceOnly: false, exclude: airDefence);
            if (partner < 0)
            {
                partner = CheapestInsertionCandidate(
                    roles, values, budget - values[airDefence], airDefenceOnly: true, exclude: airDefence);
            }

            if (partner >= 0)
            {
                picks.Add(airDefence);
                picks.Add(partner);
            }

            return;
        }

        int first = CheapestInsertionCandidate(roles, values, budget, airDefenceOnly: false, exclude: -1);
        if (first < 0)
        {
            return;
        }

        int second = CheapestInsertionCandidate(
            roles, values, budget - values[first], airDefenceOnly: false, exclude: first);
        if (second < 0)
        {
            // A one-vehicle picket cannot hold a point (the garrison minimum is two), so a lone
            // affordable vehicle buys nothing.
            return;
        }

        picks.Add(first);
        picks.Add(second);
    }

    /// <summary>
    /// Which of two candidate loads wins (design Decision 8, "doctrine over bargains"): a load
    /// containing an air-defence vehicle beats one without at any price — a rear point's threat is
    /// aircraft and the insertion is a purchase, so a commander who could buy doctrine must not be
    /// handed bargains instead. Between loads of the same air-defence standing, the cheaper total
    /// wins. Pure, for the self-check.
    /// </summary>
    internal static bool InsertionCombinationBeats(
        bool hasAirDefence, float total, bool bestHasAirDefence, float bestTotal)
    {
        if (hasAirDefence != bestHasAirDefence)
        {
            return hasAirDefence;
        }

        return total < bestTotal;
    }

    /// <summary>
    /// The price an insertion charges for one cargo vehicle: the faction's own depot price when
    /// its ground catalog lists the same vehicle by name (cargo variants in the game's assets
    /// often carry a placeholder price or none at all), the cargo definition's own value
    /// otherwise. Pure, for the self-check; the supply side's <c>TryGetVehicleCargo</c> resolves
    /// through it so the charge, the budget and the once-per-mission roster line all quote the
    /// same number.
    /// </summary>
    internal static float ResolveInsertionVehiclePrice(bool inDepotCatalog, float depotValue, float cargoValue)
    {
        return inDepotCatalog ? depotValue : cargoValue;
    }

    /// <summary>
    /// True when <paramref name="aircraft"/> is one the air wing has claimed for itself — read by
    /// the supply service's insertion claim so one airframe is never both a wing asset and an
    /// insertion transport, whichever of the two registration notifies runs first.
    /// </summary>
    internal static bool IsWingAirframe(FactionHQ hq, Aircraft aircraft)
    {
        CommanderOperationsService? service = Instance;
        return service != null
            && service.states.TryGetValue(hq, out OperationsState state)
            && state.CommanderAirframes.Contains(aircraft);
    }

    /// <summary>Index of the cheapest candidate inside <paramref name="budget"/>, -1 when none is:
    /// either the air-defence candidates only, or everything but them. Pure, for the self-check.</summary>
    private static int CheapestInsertionCandidate(
        IReadOnlyList<CommanderPlatoonRole> roles,
        IReadOnlyList<float> values,
        float budget,
        bool airDefenceOnly,
        int exclude)
    {
        int best = -1;
        for (int i = 0; i < roles.Count; i++)
        {
            if (i == exclude || values[i] > budget)
            {
                continue;
            }

            if ((roles[i] == CommanderPlatoonRole.AirDefence) != airDefenceOnly)
            {
                continue;
            }

            if (best < 0 || values[i] < values[best])
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Adopts a delivered vehicle into its picket, pure: out of the free pool if the depot claim
    /// race put it there first (a vehicle registers with its faction before the game enables it,
    /// so <c>TryClaim</c> can pool it ahead of this), and into <c>PicketMembers</c> exactly once.
    /// Generic over the member type so the self-check can drive it with plain objects rather than
    /// Unity units at plugin load.
    /// </summary>
    internal static void AdoptPicketVehicle<T>(List<T> members, List<T> pool, T unit)
        where T : class
    {
        pool.Remove(unit);
        if (!members.Contains(unit))
        {
            members.Add(unit);
        }
    }

    /// <summary>
    /// The review's insertion step (design Section 1): at most one request per review, over the
    /// picket missions in ranked order, behind the gate. Runs after <c>PlanPickets</c> so the
    /// missions it reads are this review's. Declines log once per point per reason (the
    /// <c>ReportAirDenial</c> convention).
    /// </summary>
    private void PlanInsertions(FactionHQ hq, OperationsState state)
    {
        PruneInsertions(hq, state);
        if (!CommanderSettings.OperationsHeliInsertionEnabled)
        {
            return;
        }

        CommanderSupplyHeliService.Instance?.LogInsertionRosterOnce(hq);

        int limit = Mathf.Max(0, CommanderSettings.OperationsHeliInsertionLimit);
        int inFlight = CountInsertionsInFlight(state);
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            CommanderOperationsMission? mission = FindPicketFor(state, ranked.Point);
            if (mission == null)
            {
                continue;
            }

            // A roadless map reports MaxValue and so flies (design Decision 7); no points service
            // yet cannot happen here (no missions exist before discovery), but reads as roadless.
            float roadDistance = CommanderStrategicPointService.Instance?.NearestRoadDistanceMeters(ranked.Point.Position)
                ?? float.MaxValue;
            bool cooldownLive = state.InsertionCooldownUntil.TryGetValue(ranked.Point, out float until)
                && Time.time < until;
            if (!QualifiesForInsertion(
                    !ranked.IsFront,
                    mission.PicketMembers.Count < CommanderSettings.PointsMinGarrison,
                    !IsBoundToInsertion(state, ranked.Point),
                    cooldownLive,
                    roadDistance,
                    CommanderSettings.OperationsHeliInsertionOffRoadMeters,
                    inFlight,
                    limit))
            {
                continue;
            }

            RequestInsertion(hq, state, mission, ranked.Point, roadDistance);
            return;
        }
    }

    /// <summary>The one picket mission on <paramref name="point"/>, if the review left one there.</summary>
    private static CommanderOperationsMission? FindPicketFor(OperationsState state, CommanderStrategicPoint point)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.Picket && ReferenceEquals(mission.Point, point))
            {
                return mission;
            }
        }

        return null;
    }

    /// <summary>True while any insertion record is open for <paramref name="point"/> — including a
    /// delivered one whose transport has not come home yet, so the point is never double-lifted.</summary>
    private static bool IsBoundToInsertion(OperationsState state, CommanderStrategicPoint point)
    {
        for (int i = 0; i < state.Insertions.Count; i++)
        {
            if (ReferenceEquals(state.Insertions[i].Point, point))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Open, undelivered insertions — what the airborne limit and the <c>heli=</c> review
    /// field count. A delivered flight awaiting its sweep does not block the next request.</summary>
    private static int CountInsertionsInFlight(OperationsState state)
    {
        int count = 0;
        for (int i = 0; i < state.Insertions.Count; i++)
        {
            if (state.Insertions[i].Delivered < state.Insertions[i].ExpectedLoads)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Opens the record and launches the flight. The landing post is the hold post nearest the
    /// commander's territory centre — the same posts the picket will hold, so the vehicles roll out
    /// of the ramp almost onto their stations.
    /// </summary>
    private void RequestInsertion(
        FactionHQ hq, OperationsState state, CommanderOperationsMission mission, CommanderStrategicPoint point, float roadDistance)
    {
        List<GlobalPosition> posts = EnsureHoldPosts(point, Mathf.Max(1, CommanderSettings.PointsMinGarrison));
        if (posts.Count == 0)
        {
            ReportInsertionDenial(hq, state, point, "no dry landing post inside the ring");
            return;
        }

        GlobalPosition territory = CommanderCaptureService.GetTerritoryCenter(hq);
        GlobalPosition lz = posts[0];
        float best = float.MaxValue;
        for (int i = 0; i < posts.Count; i++)
        {
            float distance = CommanderGameAccess.HorizontalDistance(territory.AsVector3(), posts[i].AsVector3());
            if (distance < best)
            {
                best = distance;
                lz = posts[i];
            }
        }

        // The record opens BEFORE the launch, not after: the launch spawns and registers the
        // transport synchronously inside its own call, and the registration notify must find this
        // record to bind the aircraft. The first play test had it the other way round, so no
        // flight ever bound, every record died on the stale valve while its transport was still
        // inbound, and the point re-ordered flight after flight (plan.md, execution log,
        // departure 10). A declined launch removes the record again.
        CommanderInsertion insertion = new()
        {
            Mission = mission,
            Point = point,
            Lz = lz,
            ExpectedLoads = Mathf.Max(1, CommanderSettings.PointsMinGarrison),
            RequestedAt = Time.time,
        };
        state.Insertions.Add(insertion);
        CommanderAiLog.Note(
            hq, $"PICKET {mission.Label}: requesting air insertion ({roadDistance:0} m from the nearest road).");

        string decline = "no transport service available";
        if (CommanderSupplyHeliService.Instance?.TryLaunchInsertionAircraft(hq, point, lz, out decline) != true)
        {
            state.Insertions.Remove(insertion);
            ReportInsertionDenial(hq, state, point, decline);
        }
    }

    /// <summary>Says why no flight was launched, but only when the reason changes for this point —
    /// a review runs every 30 s and the same line every time is noise nobody reads.</summary>
    private void ReportInsertionDenial(
        FactionHQ hq, OperationsState state, CommanderStrategicPoint point, string reason)
    {
        if (state.InsertionDenials.TryGetValue(point, out string? last) && last == reason)
        {
            return;
        }

        state.InsertionDenials[point] = reason;
        CommanderAiLog.Note(hq, $"PICKET {point.Label}: air insertion declined — {reason}.");
    }

    /// <summary>
    /// The insertion records' own sweep, first thing every review: a delivered flight whose
    /// transport is gone is closed; a flight whose point has fallen or whose mission has dissolved
    /// is recalled; a transport that died before delivering is stamped with the loss cooldown even
    /// if the supply side's own notify somehow missed it.
    /// </summary>
    private void PruneInsertions(FactionHQ hq, OperationsState state)
    {
        for (int i = state.Insertions.Count - 1; i >= 0; i--)
        {
            CommanderInsertion insertion = state.Insertions[i];
            if (insertion.Delivered >= insertion.ExpectedLoads
                && (insertion.Aircraft == null || insertion.Aircraft.disabled))
            {
                // Delivered and recovered (or lost after the drop, which cost nothing more) — the
                // insertion is over either way.
                state.Insertions.RemoveAt(i);
                continue;
            }

            FactionHQ? owner = insertion.Point.GetOwner();
            if (!state.Missions.Contains(insertion.Mission) || (owner != null && !ReferenceEquals(owner, hq)))
            {
                // The point is no longer ours to hold: recall the flight. Undeployed cargo is
                // loadout, so there is nothing to unwind but the hull, which the existing refund
                // handles when the transport lands.
                CommanderSupplyHeliService.Instance?.CancelInsertion(hq, insertion.Point);
                state.Insertions.RemoveAt(i);
                CommanderAiLog.Note(
                    hq, $"{insertion.Point.Label}: recalls the insertion flight; the point is no longer ours to hold.");
                continue;
            }

            if (insertion.Delivered < insertion.ExpectedLoads
                && insertion.Aircraft != null
                && insertion.Aircraft.disabled)
            {
                EndInsertionAsLost(hq, state, insertion);
                continue;
            }

            // The stale-request valve: a request whose transport never registered (a spawn queue
            // that never drained — a bound flight reaches its LZ well inside the window or the
            // record was never going to bind) releases the point after six reviews rather than
            // blocking it forever. The withdrawal takes the queued request with it, so a hangar
            // that frees later cannot launch a transport nobody is waiting for.
            if (insertion.Aircraft == null && Time.time - insertion.RequestedAt > StaleInsertionSeconds)
            {
                CommanderSupplyHeliService.Instance?.CancelInsertion(hq, insertion.Point);
                state.Insertions.RemoveAt(i);
                CommanderAiLog.Note(
                    hq,
                    $"insertion flight to {insertion.Point.Label} went stale after {Time.time - insertion.RequestedAt:0} s: "
                        + "never registered; the request is withdrawn.");
            }
        }
    }

    /// <summary>
    /// The loss path, shared by the supply side's notify and this partial's own backstop: the
    /// vehicles died with the hull (engine behaviour — <c>MountedCargo.OnPartDetached</c> spawns
    /// cargo disabled), the point takes the cooldown and falls back to drive fill.
    /// </summary>
    private static void EndInsertionAsLost(FactionHQ hq, OperationsState state, CommanderInsertion insertion)
    {
        state.Insertions.Remove(insertion);
        state.InsertionCooldownUntil[insertion.Point] =
            Time.time + CommanderSettings.OperationsHeliInsertionCooldownMinutes * 60f;
        CommanderAiLog.Note(
            hq,
            $"lost the insertion flight near {insertion.Point.Label}; cooldown "
                + $"{CommanderSettings.OperationsHeliInsertionCooldownMinutes:0} min, the picket drives instead.");
    }

    /// <summary>Called by the supply side when a spawned insertion transport registers with its
    /// faction: binds the aircraft to the open record so the sweep can watch it.</summary>
    internal static void NotifyInsertionAircraft(FactionHQ hq, CommanderStrategicPoint point, Aircraft aircraft)
    {
        CommanderOperationsService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        for (int i = 0; i < state.Insertions.Count; i++)
        {
            if (ReferenceEquals(state.Insertions[i].Point, point) && state.Insertions[i].Aircraft == null)
            {
                state.Insertions[i].Aircraft = aircraft;
                return;
            }
        }
    }

    /// <summary>
    /// Called by the supply side each time a cargo vehicle activates at the landing zone: adopts
    /// it into the picket it was bought for — or, if the point went front and the picket became a
    /// forward base while the flight was out, into the free pool for the next platoon fill (a
    /// forward base has no <c>PicketMembers</c>).
    /// </summary>
    internal static void NotifyPicketVehicleDelivered(FactionHQ hq, CommanderStrategicPoint point, Unit unit)
    {
        CommanderOperationsService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out OperationsState state) || unit == null)
        {
            return;
        }

        for (int i = 0; i < state.Insertions.Count; i++)
        {
            CommanderInsertion insertion = state.Insertions[i];
            if (!ReferenceEquals(insertion.Point, point))
            {
                continue;
            }

            insertion.Delivered++;
            if (insertion.Mission.Kind == CommanderMissionKind.Picket
                && ReferenceEquals(insertion.Mission.Point, point))
            {
                AdoptPicketVehicle(insertion.Mission.PicketMembers, state.Pool, unit);
                CommanderAiLog.Note(
                    hq, $"dropped {CommanderGameAccess.GetUnitLabel(unit)} at {point.Label} ({insertion.Delivered}/{insertion.ExpectedLoads}).");
            }
            else
            {
                state.Pool.Add(unit);
                CommanderAiLog.Note(
                    hq, $"{point.Label} went front while the flight was out; {CommanderGameAccess.GetUnitLabel(unit)} joins the pool.");
            }

            return;
        }

        // No record for this delivery — a request withdrawn mid-flight, or a hot reload between
        // launch and landing. The vehicle is still a paid-for unit of this faction, so it joins
        // the pool visibly rather than vanishing into bookkeeping.
        state.Pool.Add(unit);
        CommanderAiLog.Note(
            hq,
            $"{CommanderGameAccess.GetUnitLabel(unit)} arrived at {point.Label} with no insertion on record; it joins the pool.");
    }

    /// <summary>Called by the supply side when an insertion transport died before delivering:
    /// stamps the point's loss cooldown. No record means the sweep already handled it.</summary>
    internal static void NoteInsertionLost(FactionHQ hq, CommanderStrategicPoint point)
    {
        CommanderOperationsService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        for (int i = 0; i < state.Insertions.Count; i++)
        {
            if (ReferenceEquals(state.Insertions[i].Point, point))
            {
                EndInsertionAsLost(hq, state, state.Insertions[i]);
                return;
            }
        }
    }

    /// <summary>
    /// The insertion gate, chooser and adoption at their named boundaries (design Section 4), next
    /// to the operations service's other self-checks.
    /// </summary>
    private static void CheckInsertion(List<string> failures)
    {
        const float gate = 2000f;
        Expect(failures, "a point exactly on the off-road gate drives", QualifiesForInsertion(true, true, true, false, gate, gate, 0, 1), false);
        Expect(failures, "a point past the gate flies", QualifiesForInsertion(true, true, true, false, gate + 1f, gate, 0, 1), true);
        Expect(failures, "a roadless map flies every picket", QualifiesForInsertion(true, true, true, false, float.MaxValue, gate, 0, 1), true);
        Expect(failures, "a live cooldown drives instead", QualifiesForInsertion(true, true, true, true, gate + 1f, gate, 0, 1), false);
        Expect(failures, "a front point never flies", QualifiesForInsertion(false, true, true, false, float.MaxValue, gate, 0, 1), false);
        Expect(failures, "a full picket never flies", QualifiesForInsertion(true, false, true, false, float.MaxValue, gate, 0, 1), false);
        Expect(failures, "a bound point never double-lifts", QualifiesForInsertion(true, true, false, false, float.MaxValue, gate, 0, 1), false);
        Expect(failures, "the airborne limit blocks the next flight", QualifiesForInsertion(true, true, true, false, float.MaxValue, gate, 1, 1), false);

        List<int> picks = new();
        List<CommanderPlatoonRole> roles = new() { CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.Other };
        List<float> values = new() { 10f, 20f };

        PickInsertionCargo(roles, values, 100f, picks);
        ExpectSequence(failures, "air defence plus the cheapest other", picks, 0, 1);

        // An air-defence vehicle that does not fit the budget must not be bought on credit: the
        // cheaper pair wins instead.
        values = new List<float> { 100f, 5f, 30f };
        roles = new List<CommanderPlatoonRole>
        {
            CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.Other, CommanderPlatoonRole.Other,
        };
        PickInsertionCargo(roles, values, 50f, picks);
        ExpectSequence(failures, "an unaffordable air-defence vehicle falls back to two others", picks, 1, 2);

        roles = new List<CommanderPlatoonRole> { CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.Other };
        values = new List<float> { 10f, 12f, 100f };
        PickInsertionCargo(roles, values, 50f, picks);
        ExpectSequence(failures, "a second air-defence vehicle is preferable to no load at all", picks, 0, 1);

        roles = new List<CommanderPlatoonRole> { CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.Other };
        values = new List<float> { 10f, 100f };
        PickInsertionCargo(roles, values, 50f, picks);
        ExpectSequence(failures, "a lone affordable vehicle buys nothing", picks);

        roles = new List<CommanderPlatoonRole> { CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.Other };
        values = new List<float> { 10f, 10f };
        PickInsertionCargo(roles, values, 20f, picks);
        ExpectSequence(failures, "exactly the budget is exactly the load", picks, 0, 1);

        Expect(failures, "an air-defence load beats a cheaper load without one",
            InsertionCombinationBeats(true, 100f, false, 50f), true);
        Expect(failures, "a cheaper load without air defence never beats one with",
            InsertionCombinationBeats(false, 10f, true, 100f), false);
        Expect(failures, "between two air-defence loads the cheaper wins",
            InsertionCombinationBeats(true, 100f, true, 50f), false);
        Expect(failures, "between two air-defence loads the cheaper wins (other way round)",
            InsertionCombinationBeats(true, 40f, true, 50f), true);
        Expect(failures, "between two loads without air defence the cheaper wins",
            InsertionCombinationBeats(false, 40f, false, 50f), true);

        Expect(failures, "a vehicle the depot lists charges the depot price",
            ResolveInsertionVehiclePrice(true, 45f, 0f), 45f);
        Expect(failures, "a vehicle the depot does not list charges its cargo value",
            ResolveInsertionVehiclePrice(false, 45f, 2f), 2f);
        Expect(failures, "a placeholder cargo price is never read when the depot lists the vehicle",
            ResolveInsertionVehiclePrice(true, 20f, 1f), 20f);

        object poolFirst = new();
        object alreadyMember = new();
        List<object> members = new() { alreadyMember };
        List<object> pool = new() { poolFirst, alreadyMember };
        AdoptPicketVehicle(members, pool, alreadyMember);
        Expect(failures, "an adopted vehicle leaves the pool", pool.Contains(alreadyMember), false);
        Expect(failures, "an adopted vehicle joins the picket exactly once", members.Count, 1);
    }
}