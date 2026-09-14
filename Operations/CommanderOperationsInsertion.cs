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
    /// How close a tracked hostile air-defence vehicle or aircraft has to be to the landing zone or
    /// to the flight's route before the insertion is called off: the same 8 km ring the operations
    /// service already reads enemy strength over (<see cref="ObservedRadiusMeters"/>, one
    /// definition), which is also roughly the reach of the medium SAM and radar-guided AAA vehicles
    /// a slow, low transport cannot survive. Derived rather than retyped so a retune of the ring
    /// moves both together.
    /// </summary>
    internal const float InsertionThreatRadiusMeters = ObservedRadiusMeters;

    /// <summary>
    /// How finely the straight line from the launching airbase to the landing zone is sampled for
    /// the threat test: every 2 km, a quarter of <see cref="InsertionThreatRadiusMeters"/>, so a
    /// threat sitting beside the middle of a leg cannot hide between two samples — the widest a
    /// sample can be from the true nearest point on the line is half the spacing, 1 km.
    /// </summary>
    internal const float InsertionRouteSampleMeters = 2000f;

    /// <summary>
    /// How many insertion flights may be lost back to back, with no successful drop in between,
    /// before this commander stops sending them anywhere: two. The per-point cooldown alone only
    /// moves the bleeding to the next hilltop — the first play test lost five flights in a row that
    /// way — so the second loss is read as "the air is not ours today", not as bad luck at one point.
    /// </summary>
    internal const int InsertionLossStreakLimit = 2;

    /// <summary>
    /// How long that commander-wide pause lasts: 15 minutes. Long enough for the front to move and
    /// for a lost transport's killer to be engaged or to move on (three times the per-point
    /// cooldown), short enough that a commander whose rear points are genuinely safe resumes
    /// filling them by air inside the same engagement.
    /// </summary>
    internal const float InsertionLossPauseMinutes = 15f;

    /// <summary>
    /// The commander-wide pause rule, pure: a run of <paramref name="limit"/> losses with no drop
    /// in between stops every insertion, not just the one point's. A limit of zero or less disables
    /// the pause entirely.
    /// </summary>
    internal static bool ShouldPauseInsertions(int lossStreak, int limit)
    {
        return limit > 0 && lossStreak >= limit;
    }

    /// <summary>
    /// The positions the threat test measures against, pure: the straight line from
    /// <paramref name="from"/> to <paramref name="to"/> cut into legs no longer than
    /// <paramref name="spacingMeters"/>, both endpoints always included, so the landing zone and
    /// the departure airbase are always tested even on a route shorter than one leg.
    /// </summary>
    internal static void BuildRouteSamples(Vector3 from, Vector3 to, float spacingMeters, List<Vector3> samples)
    {
        samples.Clear();
        float length = CommanderGameAccess.HorizontalDistance(from, to);
        int legs = Mathf.Max(1, Mathf.CeilToInt(length / Mathf.Max(1f, spacingMeters)));
        for (int i = 0; i <= legs; i++)
        {
            samples.Add(Vector3.Lerp(from, to, (float)i / legs));
        }
    }

    /// <summary>
    /// Whether one threat covers a sampled route, pure: true when it lies within
    /// <paramref name="radius"/> of any sample, horizontally (a map coordinate's height says
    /// nothing about a missile's reach). <paramref name="distanceMeters"/> comes back as the
    /// shortest distance to any sample, which is what the decline line quotes. A threat exactly on
    /// the radius blocks — the safe side of the boundary, the opposite convention to the road gate,
    /// because being wrong here costs a transport and two vehicles.
    /// </summary>
    internal static bool RouteThreatened(
        IReadOnlyList<Vector3> samples, Vector3 threat, float radius, out float distanceMeters)
    {
        distanceMeters = float.MaxValue;
        for (int i = 0; i < samples.Count; i++)
        {
            float distance = CommanderGameAccess.HorizontalDistance(samples[i], threat);
            if (distance < distanceMeters)
            {
                distanceMeters = distance;
            }
        }

        return distanceMeters <= radius;
    }

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

    /// <summary>Reused by the threat test so a review that gates several points does not allocate a
    /// fresh sample list per point; the builder clears it before every fill.</summary>
    private readonly List<Vector3> insertionRouteSamples = new();

    /// <summary>
    /// True when this commander has something tracked that kills transports within
    /// <see cref="InsertionThreatRadiusMeters"/> of the landing zone or of the straight line to it
    /// from <paramref name="from"/> — a hostile ground vehicle the shared
    /// <see cref="CommanderEnemyCommanderService.IsAirDefence"/> test calls air defence, or any
    /// hostile aircraft, because a transport that meets a fighter is a transport lost.
    /// <paramref name="distanceMeters"/> is the closest such contact's distance to the route.
    /// </summary>
    /// <remarks>
    /// The walk is <c>CountObserved</c>'s (Offensive.cs) with its <c>ThreatMemorySeconds</c>
    /// freshness and its own-faction skip — the commander acts on what it has actually tracked, not
    /// on the truth — widened from ground vehicles only to the two things that shoot helicopters.
    /// </remarks>
    private bool TryFindInsertionRouteThreat(
        FactionHQ hq, GlobalPosition from, GlobalPosition to, out float distanceMeters)
    {
        BuildRouteSamples(from.AsVector3(), to.AsVector3(), InsertionRouteSampleMeters, insertionRouteSamples);
        distanceMeters = float.MaxValue;
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > CommanderEnemyCommanderService.ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq)
                || !ThreatensTransports(unit))
            {
                continue;
            }

            if (RouteThreatened(
                    insertionRouteSamples,
                    info.lastKnownPosition.AsVector3(),
                    InsertionThreatRadiusMeters,
                    out float distance)
                && distance < distanceMeters)
            {
                distanceMeters = distance;
            }
        }

        return distanceMeters <= InsertionThreatRadiusMeters;
    }

    /// <summary>The two kinds of tracked contact that end an insertion: a ground vehicle that is air
    /// defence, and any aircraft. Buildings are skipped as everywhere else — a static SAM building
    /// is the enemy's own base defence, and the flight never routes over one to reach a rear point
    /// of ours.</summary>
    private static bool ThreatensTransports(Unit unit)
    {
        if (unit is Building)
        {
            return false;
        }

        if (unit is Aircraft)
        {
            return true;
        }

        return unit is GroundVehicle
            && unit.definition is VehicleDefinition definition
            && CommanderEnemyCommanderService.IsAirDefence(definition);
    }

    /// <summary>
    /// Where an insertion would most likely lift from: this commander's own airbase nearest the
    /// landing zone. The supply side picks the airbase that can actually field the load and applies
    /// the same threat test to it (<see cref="IsInsertionRouteThreatened"/>), so this is the gate's
    /// stand-in for the leg the flight will fly. False when the commander holds no airbase, in
    /// which case only the landing zone itself is tested.
    /// </summary>
    private static bool TryFindInsertionLaunchBase(FactionHQ hq, GlobalPosition lz, out GlobalPosition position)
    {
        position = default;
        float best = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            GlobalPosition candidate = airbase.center.GlobalPosition();
            float distance = CommanderGameAccess.HorizontalDistance(candidate.AsVector3(), lz.AsVector3());
            if (distance < best)
            {
                best = distance;
                position = candidate;
            }
        }

        return best < float.MaxValue;
    }

    /// <summary>The threat test as the supply side's airbase choice reads it — one definition, two
    /// callers (the demand gate here and the launch entry's per-airbase filter), so a flight is
    /// never dispatched from a base whose route the gate would have refused.</summary>
    internal static bool IsInsertionRouteThreatened(FactionHQ hq, GlobalPosition from, GlobalPosition to)
    {
        CommanderOperationsService? service = Instance;
        return service != null && service.TryFindInsertionRouteThreat(hq, from, to, out _);
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
    /// The structural half of the insertion gate for one point — the pure
    /// <see cref="QualifiesForInsertion"/> test with this commander's live inputs. The request loop
    /// and the priority ladder's rung-3 demand read share it (Reuse rule 4, one definition, two
    /// callers); the launch-time gates (route threat, the rung's share) decide whether this cycle's
    /// ask succeeds, not whether the rung has work.
    /// </summary>
    private static bool PointQualifiesForInsertion(
        OperationsState state, CommanderRankedPoint ranked, CommanderOperationsMission mission, float roadDistance, int inFlight, int limit)
    {
        bool cooldownLive = state.InsertionCooldownUntil.TryGetValue(ranked.Point, out float until)
            && Time.time < until;
        return QualifiesForInsertion(
            !ranked.IsFront,
            mission.PicketMembers.Count < CommanderSettings.PointsMinGarrison,
            !IsBoundToInsertion(state, ranked.Point),
            cooldownLive,
            roadDistance,
            CommanderSettings.OperationsHeliInsertionOffRoadMeters,
            inFlight,
            limit);
    }

    /// <summary>
    /// Whether any picket point would ask for a flight right now — rung 3's demand read for the
    /// priority ladder's draw (design.md, commander-priorities_20260914 Section 3). The same
    /// structural test the request loop uses, so the ladder's floor and the request can never
    /// disagree about whether the rung has work; the post-commander pause counts as no demand for
    /// as long as it runs.
    /// </summary>
    internal static bool HasInsertionDemand(FactionHQ hq)
    {
        CommanderOperationsService? service = Instance;
        if (service == null
            || !service.states.TryGetValue(hq, out OperationsState state)
            || (state.InsertionPauseUntil > 0f && Time.time < state.InsertionPauseUntil))
        {
            return false;
        }

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

            float roadDistance = CommanderStrategicPointService.Instance?.NearestRoadDistanceMeters(ranked.Point.Position)
                ?? float.MaxValue;
            if (PointQualifiesForInsertion(state, ranked, mission, roadDistance, inFlight, limit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Insertion requests one review may issue, one per point (pickets-first design Section 4).
    /// Three matches the airborne limit's own default, so a commander that starts a match with every
    /// hilltop empty can fill that limit in a single review instead of one flight every 30 s — at
    /// one request per review the first play test was still on its second hilltop when the match
    /// turned. The airborne limit, not this, is what bounds how many are actually in the air.
    /// </summary>
    internal const int InsertionRequestsPerReview = 3;

    /// <summary>One picket point that passed the structural gate this review, with the road distance
    /// the farthest-off-road ordering sorts on. Scratch, so a review never allocates.</summary>
    private readonly List<(CommanderRankedPoint Ranked, CommanderOperationsMission Mission, float RoadDistance)> insertionCandidates = new();

    /// <summary>
    /// The review's insertion step (design Section 1, widened by pickets-first Section 4): up to
    /// <see cref="InsertionRequestsPerReview"/> requests per review, one per point, the point
    /// farthest from a road first — the one a driving picket can least reach is the one that most
    /// needs the flight. Runs after <c>PlanPickets</c> so the missions it reads are this review's.
    /// Declines log once per point per reason (the <c>ReportAirDenial</c> convention).
    /// <para>Since the priority ladder (2026-09-14), a request also spends the share rung 3 was
    /// granted this cycle: the flight's hull and vehicles are charged against it (design Section 3),
    /// and an empty share holds the flight — the point still fills from the pool, and asks again
    /// next cycle.</para>
    /// </summary>
    private void PlanInsertions(FactionHQ hq, OperationsState state)
    {
        PruneInsertions(hq, state);
        if (!CommanderSettings.OperationsHeliInsertionEnabled)
        {
            return;
        }

        if (state.InsertionPauseUntil > 0f)
        {
            if (Time.time < state.InsertionPauseUntil)
            {
                return;
            }

            state.InsertionPauseUntil = 0f;
            state.InsertionLossStreak = 0;
            CommanderAiLog.Note(
                hq, $"the {InsertionLossPauseMinutes:0} min insertion pause is over; picket flights may resume.");
        }

        CommanderSupplyHeliService.Instance?.LogInsertionRosterOnce(hq);

        int limit = Mathf.Max(0, CommanderSettings.OperationsHeliInsertionLimit);
        int inFlight = CountInsertionsInFlight(state);
        insertionCandidates.Clear();
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
            if (!PointQualifiesForInsertion(state, ranked, mission, roadDistance, inFlight, limit))
            {
                continue;
            }

            insertionCandidates.Add((ranked, mission, roadDistance));
        }

        // Farthest off the road first (pickets-first Section 4). The ranked order this loop used to
        // run in is value order, which on a mountain map handed the flight to whichever point paid
        // best rather than to the one no picket could ever drive to.
        insertionCandidates.Sort(static (a, b) => b.RoadDistance.CompareTo(a.RoadDistance));

        int requested = 0;
        for (int i = 0; i < insertionCandidates.Count && requested < InsertionRequestsPerReview; i++)
        {
            (CommanderRankedPoint ranked, CommanderOperationsMission mission, float roadDistance) = insertionCandidates[i];

            // The airborne limit binds inside one review too: every request granted above put
            // another flight in the air, and the gate that admitted this candidate was read before
            // any of them launched.
            if (CountInsertionsInFlight(state) >= limit)
            {
                return;
            }

            // The launch gate: nothing that shoots helicopters may be tracked near the landing zone
            // or along the way in. Without it the per-point cooldown simply moved the losses to the
            // next hilltop — the first play test lost five flights out of five that way.
            GlobalPosition origin = TryFindInsertionLaunchBase(hq, ranked.Point.Position, out GlobalPosition airbase)
                ? airbase
                : ranked.Point.Position;
            if (TryFindInsertionRouteThreat(hq, origin, ranked.Point.Position, out float threatDistance))
            {
                ReportInsertionDenial(
                    hq,
                    state,
                    ranked.Point,
                    $"hostile air defence tracked {threatDistance / 1000f:0.0} km from the route",
                    "route threat");
                continue;
            }

            // The rung's share (design Section 3): a point that passed every gate but was granted
            // nothing this cycle says so once and waits for the next grant. An empty share ends the
            // review's requests, not just this point's — there is nothing left to charge them to.
            if (state.InsertionAllowance <= 0f)
            {
                ReportInsertionDenial(
                    hq, state, ranked.Point, "the priority ladder's picket share is empty this cycle");
                return;
            }

            if (RequestInsertion(hq, state, mission, ranked.Point, roadDistance, state.InsertionAllowance))
            {
                requested++;
            }
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
    private bool RequestInsertion(
        FactionHQ hq, OperationsState state, CommanderOperationsMission mission, CommanderStrategicPoint point, float roadDistance, float allowance)
    {
        List<GlobalPosition> posts = EnsureHoldPosts(point, Mathf.Max(1, CommanderSettings.PointsMinGarrison));
        if (posts.Count == 0)
        {
            ReportInsertionDenial(hq, state, point, "no dry landing post inside the ring");
            return false;
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
        float charged = 0f;
        if (CommanderSupplyHeliService.Instance?.TryLaunchInsertionAircraft(hq, point, lz, allowance, out decline, out charged) != true)
        {
            state.Insertions.Remove(insertion);
            ReportInsertionDenial(hq, state, point, decline);
            return false;
        }

        // The flight's cost comes off the rung's share and onto the tally the next ladder line
        // reports (design Section 3: "hull + vehicles from the rung's share"). Charged is the
        // worst-case total — a hull found free in stock leaves the share with headroom for the
        // next flight inside the same cycle.
        state.InsertionAllowance = Mathf.Max(0f, state.InsertionAllowance - charged);
        state.InsertionSpentSinceLadder += charged;

        // A flight that got away clears the point's last decline, so the next refusal — a threat
        // that comes back over the same point, say — logs again instead of being swallowed as a
        // repeat of a reason that has since been answered.
        state.InsertionDenials.Remove(point);
        return true;
    }

    /// <summary>Says why no flight was launched, but only when the reason changes for this point —
    /// a review runs every 30 s and the same line every time is noise nobody reads.
    /// <paramref name="dedupKey"/> is for a reason whose wording carries a live measurement: a
    /// threat that drifts from 3.1 km to 3.4 km is the same refusal, and must not log twice.</summary>
    private void ReportInsertionDenial(
        FactionHQ hq, OperationsState state, CommanderStrategicPoint point, string reason, string? dedupKey = null)
    {
        string key = dedupKey ?? reason;
        if (state.InsertionDenials.TryGetValue(point, out string? last) && last == key)
        {
            return;
        }

        state.InsertionDenials[point] = key;
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

            // The in-flight recall: the launch gate is re-run every review over what is left of the
            // route — from where the transport actually is to its landing zone — so a threat that
            // appears or is spotted after take-off turns the flight round instead of flying it into
            // the same guns the launch gate would have refused.
            if (insertion.Delivered < insertion.ExpectedLoads
                && insertion.Aircraft != null
                && !insertion.Aircraft.disabled
                && TryFindInsertionRouteThreat(
                    hq, insertion.Aircraft.GlobalPosition(), insertion.Lz, out float threatDistance))
            {
                CommanderSupplyHeliService.Instance?.CancelInsertion(hq, insertion.Point);
                state.Insertions.RemoveAt(i);
                state.InsertionCooldownUntil[insertion.Point] =
                    Time.time + CommanderSettings.OperationsHeliInsertionCooldownMinutes * 60f;
                CommanderAiLog.Note(
                    hq,
                    $"{insertion.Point.Label}: recalls the insertion flight; hostile air defence tracked "
                        + $"{threatDistance / 1000f:0.0} km from the remaining route. Cooldown "
                        + $"{CommanderSettings.OperationsHeliInsertionCooldownMinutes:0} min, the picket drives instead.");
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

        state.InsertionLossStreak++;
        if (ShouldPauseInsertions(state.InsertionLossStreak, InsertionLossStreakLimit)
            && state.InsertionPauseUntil <= 0f)
        {
            state.InsertionPauseUntil = Time.time + InsertionLossPauseMinutes * 60f;
            CommanderAiLog.Note(
                hq,
                $"{state.InsertionLossStreak} insertion flights lost in a row: no picket flies anywhere for "
                    + $"{InsertionLossPauseMinutes:0} min.");
        }
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
            // A vehicle on the ground is proof the air is flyable again: the run of losses that
            // would otherwise pause every insertion starts over.
            state.InsertionLossStreak = 0;
            if (insertion.Mission.Kind == CommanderMissionKind.Picket
                && ReferenceEquals(insertion.Mission.Point, point))
            {
                AdoptPicketVehicle(insertion.Mission.PicketMembers, state.Pool, unit);
                // The marker's "Dropped, taking posts" window runs from here, not from the
                // insertion record, which is swept as soon as the transport is recovered.
                insertion.Mission.LastDropAt = Time.time;
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

        CheckInsertionThreat(failures);
    }

    /// <summary>
    /// The launch gate's two pure pieces at their named boundaries: the route sampling that decides
    /// where the threat test looks, and the commander-wide pause the run of losses triggers.
    /// </summary>
    private static void CheckInsertionThreat(List<string> failures)
    {
        Vector3 airbase = new(0f, 0f, 0f);
        Vector3 landingZone = new(5000f, 0f, 0f);
        List<Vector3> samples = new();

        BuildRouteSamples(airbase, landingZone, InsertionRouteSampleMeters, samples);
        Expect(failures, "a 5 km route at 2 km spacing is cut into three legs", samples.Count, 4);
        Expect(
            failures,
            "the route sampling starts at the launching airbase",
            CommanderGameAccess.HorizontalDistance(samples[0], airbase) < 0.01f,
            true);
        Expect(
            failures,
            "the route sampling ends at the landing zone",
            CommanderGameAccess.HorizontalDistance(samples[samples.Count - 1], landingZone) < 0.01f,
            true);
        Expect(
            failures,
            "no gap between samples is wider than the spacing",
            CommanderGameAccess.HorizontalDistance(samples[0], samples[1]) <= InsertionRouteSampleMeters,
            true);

        BuildRouteSamples(airbase, airbase, InsertionRouteSampleMeters, samples);
        Expect(
            failures,
            "a route with no length still samples its own position",
            samples.Count > 0 && CommanderGameAccess.HorizontalDistance(samples[0], airbase) < 0.01f,
            true);

        BuildRouteSamples(airbase, landingZone, InsertionRouteSampleMeters, samples);
        Expect(
            failures,
            "a threat exactly on the threat radius blocks the flight",
            RouteThreatened(
                samples, new Vector3(0f, 0f, InsertionThreatRadiusMeters), InsertionThreatRadiusMeters, out _),
            true);
        Expect(
            failures,
            "a threat one metre outside the ring lets the flight go",
            RouteThreatened(
                samples, new Vector3(0f, 0f, InsertionThreatRadiusMeters + 1f), InsertionThreatRadiusMeters, out _),
            false);
        Expect(
            failures,
            "a threat beside the middle of the route blocks it, not just one beside an end",
            RouteThreatened(samples, new Vector3(2500f, 0f, 500f), InsertionThreatRadiusMeters, out _),
            true);
        Expect(
            failures,
            "the quoted distance is the threat's distance to the nearest sample",
            RouteThreatened(samples, new Vector3(5000f, 0f, 3000f), InsertionThreatRadiusMeters, out float quoted)
                && Mathf.Abs(quoted - 3000f) < 1f,
            true);

        Expect(failures, "no loss at all never pauses", ShouldPauseInsertions(0, InsertionLossStreakLimit), false);
        Expect(failures, "one lost flight never pauses", ShouldPauseInsertions(1, InsertionLossStreakLimit), false);
        Expect(failures, "two lost flights in a row pause every insertion", ShouldPauseInsertions(2, InsertionLossStreakLimit), true);
        Expect(failures, "a longer run stays paused", ShouldPauseInsertions(5, InsertionLossStreakLimit), true);
        Expect(
            failures,
            "a drop between two losses resets the streak, so neither pauses",
            ShouldPauseInsertions(1, InsertionLossStreakLimit),
            false);
        Expect(failures, "a zero limit turns the pause off", ShouldPauseInsertions(9, 0), false);

        Expect(failures, "a review may always issue at least one insertion request", InsertionRequestsPerReview >= 1, true);
        Expect(
            failures,
            "a review never issues more requests than the default airborne limit can carry",
            InsertionRequestsPerReview <= DefaultHeliInsertionLimit,
            true);
        Expect(
            failures,
            "a commander at its airborne limit asks for nothing more",
            QualifiesForInsertion(true, true, true, false, 5000f, 2000f, DefaultHeliInsertionLimit, DefaultHeliInsertionLimit),
            false);
        Expect(
            failures,
            "one flight already airborne no longer blocks the next",
            QualifiesForInsertion(true, true, true, false, 5000f, 2000f, 1, DefaultHeliInsertionLimit),
            true);
        Expect(
            failures,
            "a commander one flight under its airborne limit still asks",
            QualifiesForInsertion(true, true, true, false, 5000f, 2000f, DefaultHeliInsertionLimit - 1, DefaultHeliInsertionLimit),
            true);
    }

    /// <summary>The shipped default of <c>OperationsHeliInsertionLimit</c>, so the self-check can
    /// pin the per-review cap against it without reading a config a player may have retuned.</summary>
    private const int DefaultHeliInsertionLimit = 3;
}