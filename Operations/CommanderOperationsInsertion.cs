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

        /// <summary>How many vehicles the flight carries: the whole garrison minimum for a point
        /// with nothing on it, or just the vehicles a surviving picket is short of (user decision
        /// 2026-09-14). Bounded by <see cref="MaxInsertionCargoVehicles"/>.</summary>
        internal int ExpectedLoads = 2;

        /// <summary>How many of them have been adopted into the picket so far.</summary>
        internal int Delivered;

        /// <summary>Scaled <c>Time.time</c> the request was made, for the stale-request valve
        /// below.</summary>
        internal float RequestedAt;

        /// <summary>Scaled <c>Time.time</c> the transport first came within
        /// <see cref="InsertionStallRadiusMeters"/> of its landing zone, or 0 when it is not there.
        /// The stall clock (user report 2026-09-14: the flight "cannot land, units aren't dropped,
        /// stuck").</summary>
        internal float NearLandingZoneSince;

        /// <summary>True when this flight drops under parachutes rather than landing — set at the
        /// request when the landing zone scouted blocked, or by the stall clock when a landing flight
        /// is converted. The stall clock reads it so a drop run is never "converted" a second time
        /// and gets its own full window before the recall bites.</summary>
        internal bool Airdrop;
    }

    /// <summary>
    /// How close to its landing zone a transport counts as "at" it for the stall clock: 500 m, the
    /// same ring the ejection suppression uses to decide a transport is working at its target rather
    /// than in transit (<c>SuppressEjectionAtAssignedSamSite</c>). Wide enough that a helicopter
    /// circling for an approach still counts, tight enough that an inbound flight does not.
    /// </summary>
    internal const float InsertionStallRadiusMeters = 500f;

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

    /// <summary>How a picket point's garrison is meant to get there this review.</summary>
    internal enum CommanderPicketDelivery
    {
        /// <summary>Filled from the free pool and driven to its hold posts, the ordinary case.</summary>
        Drive,

        /// <summary>Reserved for a transport helicopter: the drive fill leaves it alone and no
        /// road-stock requisition is posted for it, so a shortfall survives long enough for
        /// <c>PlanInsertions</c> to see it.</summary>
        Air,
    }

    /// <summary>
    /// Who delivers one picket point's vehicles, pure (departure 2026-09-14, pickets-first
    /// Section 4). A point past the off-road gate is RESERVED for the flight — the drive fill skips
    /// it and it posts no requisition — because the drive fill and the order book both run ahead of
    /// <see cref="PlanInsertions"/> and were filling every off-road point out of the pool before the
    /// insertion gate's <c>shortHanded</c> test ever saw a shortfall: the whole 2026-09-14 match
    /// read <c>pickets=2</c> and <c>heli=0/3</c> with not one <c>requesting air insertion</c> line.
    /// <para>The reservation is given up the moment the flight cannot happen — no transport airframe
    /// with vehicle cargo can launch, the loss-streak pause is running, or this point's own cooldown
    /// is live — because a point nobody can fly to must still be driven to rather than left empty.
    /// The front/rear test is the caller's (<see cref="QualifiesForInsertion"/> owns it): a picket
    /// standing on a FRONT point is a demoted forward base and always drives.</para>
    /// </summary>
    internal static CommanderPicketDelivery PicketDeliveryMode(
        bool offRoad, bool insertionPossible, bool paused, bool cooldownLive)
    {
        return offRoad && insertionPossible && !paused && !cooldownLive
            ? CommanderPicketDelivery.Air
            : CommanderPicketDelivery.Drive;
    }

    /// <summary>Whether a point is far enough from the enemy for an unescorted transport, pure:
    /// not a front point, and farther than the standoff from the nearest enemy asset.</summary>
    internal static bool InsertionStandoffClear(bool front, float distanceToEnemyMeters, float standoffMeters)
    {
        return !front && distanceToEnemyMeters > standoffMeters;
    }

    /// <summary>
    /// The same standoff asked of a LANDING ZONE while the flight is already in the air, pure (user
    /// decision 2026-09-15): the ground at the end of the route must still be farther than the
    /// standoff from anything hostile this commander has spotted. The front/rear half of
    /// <see cref="InsertionStandoffClear"/> is left out on purpose — the flight is already flying, so
    /// the question is no longer "is this a rear point" but "is the enemy standing on the spot it is
    /// about to land on". Exactly on the standoff is too close, the same side of the boundary the
    /// launch gate refuses on.
    /// </summary>
    internal static bool InsertionLandingClear(float nearestHostileMeters, float standoffMeters)
    {
        return nearestHostileMeters > standoffMeters;
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
        int limit,
        bool outOfReach = false)
    {
        // A point beyond depot reach flies whatever its road distance (reach-and-points,
        // 2026-09-14): no road picket can ever be sent to it, so the road test that keeps a
        // driveable point on the ground is the wrong question there. The 2026-09-14 match planned
        // seven air pickets on roads and made not one request.
        return rear
            && shortHanded
            && unbound
            && !cooldownLive
            && (nearestRoadDistance > offRoadMeters || outOfReach)
            && inFlight < limit;
    }

    /// <summary>
    /// Whether a point is a resource site worth jumping the insertion queue for, pure (user
    /// instruction 2026-09-14: "resource sites need to be a priority for air insertion"). A site we
    /// do not hold is the only point on the map whose capture earns anything — its holder is who may
    /// put a mine on it (<c>CommanderStrategicPointService.SiteMinePermitted</c>), and the mine pays.
    /// A site already held is just another point: the picket standing on it is holding ground, not
    /// unlocking income.
    /// </summary>
    internal static bool IsPriorityInsertionSite(StrategicPointKind kind, bool held)
    {
        return kind == StrategicPointKind.Site && !held;
    }

    /// <summary>
    /// The rank every other point shares, and the ceiling a site's own rank is held below: bigger
    /// than any distance a map can produce, so no site ever sorts behind a point that is not one.
    /// A thousand kilometres — the largest stock map is a few tens of kilometres across.
    /// </summary>
    internal const float InsertionSiteRankCeiling = 1000000f;

    /// <summary>
    /// Where one candidate sits in the review's insertion queue, pure — lower flies first (user
    /// instruction 2026-09-14). A resource site we do not hold ranks by how near it is to the
    /// commander's own territory, and ranks ahead of every other point at ANY distance; everything
    /// else shares the ceiling, and the caller's farthest-off-road tie-break decides between them
    /// exactly as it did before.
    /// <para>Ranking is also the whole of "a site jumps the savings queue". The rung banks enough
    /// for one flight at a time, and the request loop spends it on the first candidate that passes
    /// its gates — so putting the site first IS giving it the flight when a site and a hilltop both
    /// qualify and there is money for one.</para>
    /// </summary>
    internal static float InsertionCandidateRank(StrategicPointKind kind, bool held, float distanceMeters)
    {
        return IsPriorityInsertionSite(kind, held)
            ? Mathf.Clamp(distanceMeters, 0f, InsertionSiteRankCeiling - 1f)
            : InsertionSiteRankCeiling;
    }

    /// <summary>
    /// Whether the picket rung's bank can pay for a flight, pure (departure 2026-09-14): the
    /// savings must cover the whole price of one, exactly the shape of the FOB order's own money
    /// gate (<see cref="FobAffordable"/> — one rule, two rungs). Exactly the price is enough.
    /// <para>An unpriced flight — <paramref name="price"/> zero or less, which is what a commander
    /// whose airbases launch no vehicle-carrying transport reports — is never covered, because
    /// there is nothing to buy.</para>
    /// <para>The price is a FULL flight's, so a picket that has lost one of its pair and only wants
    /// one vehicle still waits for the full bank. That costs a review or two of saving and buys one
    /// rule instead of two; the rung's cap is the same number, so the bank always reaches it.</para>
    /// </summary>
    internal static bool PicketSavingsCover(float savings, float price)
    {
        return price > 0f && savings >= price;
    }

    /// <summary>
    /// Trees allowed inside the clear radius before a landing zone is called woodland: three. One or
    /// two trees beside a field is scenery a helicopter lands next to; the fourth means canopy. The
    /// count comes from the game's own scatter data, which places trees in their hundreds where there
    /// is forest at all, so the rule is not sensitive to the exact number — only to the difference
    /// between a stray tree and a wood.
    /// </summary>
    internal const int LzMaxTreesInClearRadius = 3;

    /// <summary>
    /// How steep the ground at a landing zone may be, in degrees: 20, which is not our number but
    /// the game's. <c>AIHeloTransportState.TransportDestination.UpdateTouchdownPoint</c> accepts a
    /// touchdown point only when the surface normal is within 20° of vertical, so ground steeper than
    /// this is ground the game will never agree to land on however long the transport hovers over it.
    /// Reading the same threshold here is what lets the flight be turned into an airdrop before it
    /// takes off rather than after it has sat over the hilltop for two minutes.
    /// </summary>
    internal const float LzMaxSlopeDegrees = 20f;

    /// <summary>
    /// The altitude an airdrop is flown at, in metres: 200. Recorded here because the log line and
    /// the design quote it, NOT because anything sets it — <c>AIHeloTransportState.FixedUpdateState</c>
    /// hard-codes <c>num = 200f</c> in its airdrop branch and releases the load inside four seconds'
    /// flying time of the drop point. Verified in the decompile, 2026-09-14. If this constant and the
    /// game ever disagree, the game wins and this comment is the bug.
    /// </summary>
    internal const float AirdropAltitudeMeters = 200f;

    /// <summary>
    /// Whether one candidate landing zone is somewhere a transport can put its wheels down, pure:
    /// no woodland in the clear radius, no static obstacle in it at all, and ground no steeper than
    /// the game's own landing rule allows. A single building or rock inside the radius is a refusal
    /// because the game's touchdown search will land beside it and the vehicles roll out into it.
    /// Boundaries pass: exactly <paramref name="maxTrees"/> trees and exactly
    /// <paramref name="maxSlopeDegrees"/> of slope are still a landing zone.
    /// </summary>
    internal static bool IsLandingZoneClear(
        int trees, int obstacles, float slopeDegrees, int maxTrees, float maxSlopeDegrees)
    {
        return trees <= maxTrees && obstacles <= 0 && slopeDegrees <= maxSlopeDegrees;
    }

    /// <summary>How an insertion reaches a point once the landing zone has been scouted.</summary>
    internal enum CommanderInsertionDelivery
    {
        /// <summary>Land on the chosen post and roll the vehicles off, the ordinary case.</summary>
        Land,

        /// <summary>Drop them under parachutes over the point instead — the user's preferred answer
        /// to a landing zone that cannot be landed on (2026-09-14).</summary>
        Airdrop,

        /// <summary>Land, but on the nearest clear ground the search could find instead of the
        /// chosen post.</summary>
        Relocate,

        /// <summary>Send nothing; the picket drives.</summary>
        Decline,
    }

    /// <summary>
    /// The delivery decision, pure, in the order the user asked for (2026-09-14: "we either need to
    /// check for that and do an airdrop (preferred), or ignore those areas"): a clear landing zone is
    /// landed on; a blocked one is airdropped onto when the load has parachutes; failing that the
    /// flight lands on the nearest clear ground; failing that nothing flies and the picket drives.
    /// <para>Airdrop beats relocation deliberately. Relocating puts the vehicles up to 400 m off the
    /// posts they were bought to hold, which costs the point a review or two of driving; an airdrop
    /// puts them on the point itself.</para>
    /// </summary>
    internal static CommanderInsertionDelivery ChooseInsertionDelivery(
        bool landingZoneClear, bool loadCanAirdrop, bool clearAlternateFound)
    {
        if (landingZoneClear)
        {
            return CommanderInsertionDelivery.Land;
        }

        if (loadCanAirdrop)
        {
            return CommanderInsertionDelivery.Airdrop;
        }

        return clearAlternateFound
            ? CommanderInsertionDelivery.Relocate
            : CommanderInsertionDelivery.Decline;
    }

    /// <summary>
    /// Whether a bound flight has been sitting over its landing zone long enough to call it stuck,
    /// pure. The catch-all for every reason a transport cannot get down that the scout did not
    /// predict: the game's touchdown search never satisfying its slope rule, a drop zone the HQ will
    /// not clear, or anything else. Exactly on the timeout is not yet stuck — the same
    /// inclusive-on-the-patient-side convention the road gate uses.
    /// </summary>
    internal static bool HasStalledAtLandingZone(float secondsNearLandingZone, float timeoutSeconds)
    {
        return timeoutSeconds > 0f && secondsNearLandingZone > timeoutSeconds;
    }

    /// <summary>
    /// The most vehicles one insertion flight carries: two, the garrison minimum
    /// (<c>PointsMinGarrison</c>) a fresh picket needs, and the most any transport's cargo mounts
    /// have ever been asked for. A picket asking for more than this is filled by successive flights,
    /// each under its own cooldown.
    /// </summary>
    internal const int MaxInsertionCargoVehicles = 2;

    /// <summary>
    /// The insertion's load, pure (design Decision 8: doctrine over bargains — the insertion is a
    /// purchase, and a rear point's threat is aircraft): the air-defence vehicle first, then the
    /// cheapest others, all within <paramref name="budget"/>.
    /// <para>
    /// <paramref name="wanted"/> is how many vehicles the point is actually short of (user decision
    /// 2026-09-14). A fresh picket wants two; a picket that has LOST one wants exactly one, and
    /// flying a full pair to it would buy a vehicle the point has no room in its establishment for.
    /// Clamped to <see cref="MaxInsertionCargoVehicles"/> and to at least one.
    /// </para>
    /// Fewer than <paramref name="wanted"/> affordable vehicles → no picks at all, and the caller
    /// declines: a load that cannot fill the request is not a cheaper load, it is the wrong flight.
    /// </summary>
    /// <param name="wantedRoles">The roles the load is being flown to fill, best first, with repeats
    /// for repeated slots — an air-mobile platoon's remaining recipe (design.md,
    /// air-mobile-platoons_20260915 Section 2). Null or empty is the picket insertion's own case and
    /// leaves the doctrine rule below exactly as it was. A load that cannot be filled from the wanted
    /// roles falls back to that rule rather than flying nothing: two vehicles of the wrong role on
    /// the point beat two of the right role at the depot.</param>
    internal static void PickInsertionCargo(
        IReadOnlyList<CommanderPlatoonRole> roles,
        IReadOnlyList<float> values,
        float budget,
        int wanted,
        List<int> picks,
        IReadOnlyList<CommanderPlatoonRole>? wantedRoles = null)
    {
        picks.Clear();
        int need = Mathf.Clamp(wanted, 1, MaxInsertionCargoVehicles);
        if (wantedRoles != null && wantedRoles.Count > 0)
        {
            PickInsertionCargoForRoles(roles, values, budget, need, wantedRoles, picks);
            if (picks.Count >= need)
            {
                return;
            }

            picks.Clear();
        }

        int airDefence = CheapestInsertionCandidate(roles, values, budget, airDefenceOnly: true, exclude: -1);
        if (airDefence >= 0)
        {
            picks.Add(airDefence);
            if (need == 1)
            {
                return;
            }

            int partner = CheapestInsertionCandidate(
                roles, values, budget - values[airDefence], airDefenceOnly: false, exclude: airDefence);
            if (partner < 0)
            {
                partner = CheapestInsertionCandidate(
                    roles, values, budget - values[airDefence], airDefenceOnly: true, exclude: airDefence);
            }

            if (partner < 0)
            {
                picks.Clear();
                return;
            }

            picks.Add(partner);
            return;
        }

        int first = CheapestInsertionCandidate(roles, values, budget, airDefenceOnly: false, exclude: -1);
        if (first < 0)
        {
            return;
        }

        picks.Add(first);
        if (need == 1)
        {
            return;
        }

        int second = CheapestInsertionCandidate(
            roles, values, budget - values[first], airDefenceOnly: false, exclude: first);
        if (second < 0)
        {
            // A one-vehicle picket cannot hold a point (the garrison minimum is two), so a lone
            // affordable vehicle buys nothing when two were asked for.
            picks.Clear();
            return;
        }

        picks.Add(second);
    }

    /// <summary>
    /// The load a lift wants for a platoon it is still building, pure: the cheapest vehicle of the
    /// first wanted role, then the cheapest of a role the first one did not fill, so a lift never
    /// flies two of the same role while another slot of the recipe stands empty. Returns fewer picks
    /// than <paramref name="need"/> when the wanted roles cannot be filled inside the budget, and the
    /// caller then falls back to the doctrine rule.
    /// </summary>
    private static void PickInsertionCargoForRoles(
        IReadOnlyList<CommanderPlatoonRole> roles,
        IReadOnlyList<float> values,
        float budget,
        int need,
        IReadOnlyList<CommanderPlatoonRole> wantedRoles,
        List<int> picks)
    {
        int first = CheapestInsertionCandidateInRoles(roles, values, budget, wantedRoles, exclude: -1);
        if (first < 0)
        {
            return;
        }

        picks.Add(first);
        if (need == 1)
        {
            return;
        }

        // The roles still wanted once the first pick has taken one of them, so a recipe short of one
        // air-defence vehicle and three carriers does not load two air-defence vehicles.
        insertionRolesLeft.Clear();
        bool dropped = false;
        for (int i = 0; i < wantedRoles.Count; i++)
        {
            if (!dropped && wantedRoles[i] == roles[first])
            {
                dropped = true;
                continue;
            }

            insertionRolesLeft.Add(wantedRoles[i]);
        }

        int second = insertionRolesLeft.Count > 0
            ? CheapestInsertionCandidateInRoles(
                roles, values, budget - values[first], insertionRolesLeft, exclude: first)
            : -1;
        insertionRolesLeft.Clear();
        if (second >= 0)
        {
            picks.Add(second);
        }
    }

    /// <summary>The roles a second pick may still fill, rebuilt for each call rather than allocated
    /// — one commander's lift is chosen at a time, the insertion scratch convention.</summary>
    private static readonly List<CommanderPlatoonRole> insertionRolesLeft = new();

    /// <summary>Index of the cheapest candidate inside <paramref name="budget"/> whose role is one of
    /// <paramref name="wantedRoles"/>, -1 when none is. Pure, for the self-check.</summary>
    private static int CheapestInsertionCandidateInRoles(
        IReadOnlyList<CommanderPlatoonRole> roles,
        IReadOnlyList<float> values,
        float budget,
        IReadOnlyList<CommanderPlatoonRole> wantedRoles,
        int exclude)
    {
        int best = -1;
        for (int i = 0; i < roles.Count; i++)
        {
            if (i == exclude || values[i] > budget + InsertionPriceToleranceFunds)
            {
                continue;
            }

            bool wantedRole = false;
            for (int w = 0; w < wantedRoles.Count && !wantedRole; w++)
            {
                wantedRole = wantedRoles[w] == roles[i];
            }

            if (wantedRole && (best < 0 || values[i] < values[best]))
            {
                best = i;
            }
        }

        return best;
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
        return InsertionCombinationBeats(hasAirDefence, 0f, total, bestHasAirDefence, 0f, bestTotal, preferHeavyHull: false);
    }

    /// <summary>
    /// The same choice with the hull in view (user, 2026-09-14: "I'd prefer Tarantula flight FOBs"):
    /// air defence still wins at any price; then, when <paramref name="preferHeavyHull"/>, the
    /// dearer hull wins — a construction flight wants the biggest, fastest transport on the roster,
    /// not the cheapest — and only between equal hulls does the cheaper total decide. With the
    /// preference off the hull is ignored and the rule is the original one.
    /// </summary>
    internal static bool InsertionCombinationBeats(
        bool hasAirDefence, float hull, float total, bool bestHasAirDefence, float bestHull, float bestTotal, bool preferHeavyHull)
    {
        if (hasAirDefence != bestHasAirDefence)
        {
            return hasAirDefence;
        }

        if (preferHeavyHull && !Mathf.Approximately(hull, bestHull))
        {
            return hull > bestHull;
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

    /// <summary>
    /// How far over a cargo budget a vehicle may price and still be chosen: one hundredth of a
    /// fund. Prices are floats summed and subtracted (a budget of 1.6 less a 0.8 pick is not
    /// exactly 0.8), and the picket bank targets EXACTLY the cheapest pair, so without this the
    /// second vehicle of the pair missed the budget by a rounding error and no picket flew all
    /// match (2026-09-14: "cargo budget 1.6 ... Hexhound GMG 0.8 ... chooser picked 0"). A
    /// hundredth is below any price the game quotes.
    /// </summary>
    internal const float InsertionPriceToleranceFunds = 0.01f;

    /// <summary>Index of the cheapest candidate inside <paramref name="budget"/> (to within
    /// <see cref="InsertionPriceToleranceFunds"/>), -1 when none is:
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
            if (i == exclude || values[i] > budget + InsertionPriceToleranceFunds)
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
            InsertionStandoffClear(ranked.IsFront, ranked.DistanceToEnemyMeters, CommanderSettings.OperationsHeliInsertionEnemyStandoffMeters),
            mission.PicketMembers.Count < CommanderSettings.PointsMinGarrison,
            !IsBoundToInsertion(state, ranked.Point),
            cooldownLive,
            roadDistance,
            CommanderSettings.OperationsHeliInsertionOffRoadMeters,
            inFlight,
            limit,
            state.OutOfReach.Contains(ranked.Point));
    }

    /// <summary>
    /// Rebuilds <c>state.AirDeliveredPickets</c> for this review — every rear picket point the
    /// transport is expected to deliver to, so the drive fill and the order book can leave those
    /// points alone (departure 2026-09-14, pickets-first Section 4). Called from
    /// <c>PlanPickets</c> once the review's picket missions exist and before <c>FillPickets</c>.
    /// <para>Whether a flight is possible at all is read ONCE per commander per review — it walks
    /// the cargo catalog — and handed to the pure <see cref="PicketDeliveryMode"/> for every
    /// point, so a commander with no vehicle-carrying transport reserves nothing and every point
    /// drives exactly as before.</para>
    /// </summary>
    private static void MarkAirDeliveredPickets(FactionHQ hq, OperationsState state)
    {
        state.AirDeliveredPickets.Clear();
        bool paused = state.InsertionPauseUntil > 0f && Time.time < state.InsertionPauseUntil;
        bool insertionPossible = CommanderSettings.OperationsHeliInsertionEnabled
            && CommanderSupplyHeliService.Instance?.HasLaunchableVehicleTransport(hq) == true;
        if (!insertionPossible || paused)
        {
            return;
        }

        // A point discovery or a failed flight has marked as woodland can only be served from the
        // air by parachute, so reserving one for a roster with no parachute-capable cargo would
        // leave it empty all match. Read once per commander per review, like the plain test above.
        bool airdropPossible = CommanderSupplyHeliService.Instance?.HasLaunchableVehicleTransport(
            hq, requireAirdrop: true) == true;

        float offRoadMeters = CommanderSettings.OperationsHeliInsertionOffRoadMeters;
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            // A picket on a FRONT point is a demoted forward base; the insertion gate refuses it
            // (QualifiesForInsertion's rear test), so reserving it would starve it outright.
            if (ranked.IsFront || FindPicketFor(state, ranked.Point) == null)
            {
                continue;
            }

            float roadDistance = CommanderStrategicPointService.Instance?.NearestRoadDistanceMeters(ranked.Point.Position)
                ?? float.MaxValue;
            bool cooldownLive = state.InsertionCooldownUntil.TryGetValue(ranked.Point, out float until)
                && Time.time < until;
            if (PicketDeliveryMode(
                    roadDistance > offRoadMeters,
                    !ranked.Point.Wooded || airdropPossible,
                    false,
                    cooldownLive)
                == CommanderPicketDelivery.Air)
            {
                state.AirDeliveredPickets.Add(ranked.Point);
            }
        }
    }

    /// <summary>Whether this review reserved <paramref name="point"/> for a transport helicopter —
    /// the one door the drive fill, the order book and the review line all read.
    /// <para>A point out of reach of every depot this commander owns is air-delivered by definition
    /// (reach-and-points Section 2, user decision 2026-09-14): no vehicle can drive there, so the
    /// drive fill must not draw pool vehicles for it and the order book must not post road stock for
    /// it. This is the same door the woodland and off-road rules already came through, which is why
    /// the reach test is added here rather than repeated at each of the three readers.</para></summary>
    private static bool IsAirDeliveredPicket(OperationsState state, CommanderStrategicPoint? point)
    {
        return point != null
            && (state.AirDeliveredPickets.Contains(point) || state.OutOfReach.Contains(point));
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
    /// Six matches the airborne limit's own default, so a commander that starts a match with every
    /// hilltop empty can fill that limit in a single review instead of one flight every 30 s — at
    /// one request per review the first play test was still on its second hilltop when the match
    /// turned. Raised 3 → 6 with the limit when pickets began asking for REINFORCEMENT as well as
    /// first delivery (2026-09-14): a review now has both kinds of point to serve. The airborne
    /// limit, not this, is what bounds how many are actually in the air.
    /// </summary>
    internal const int InsertionRequestsPerReview = 6;

    /// <summary>One picket point that passed the structural gate this review, with the road distance
    /// the farthest-off-road ordering sorts on. Scratch, so a review never allocates.</summary>
    private readonly List<(CommanderRankedPoint Ranked, CommanderOperationsMission Mission, float RoadDistance, float Rank, bool SiteFirst)> insertionCandidates = new();

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
        // Where the commander "is" — the mean of the airbases it holds. A site's rank is its
        // distance from there, so "nearest to a held asset first" means nearest to the territory
        // rather than to whichever unit has wandered furthest from home. The same read
        // RequestInsertion already makes to choose the landing post, taken once per review here.
        GlobalPosition territory = CommanderCaptureService.GetTerritoryCenter(hq);
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
                // Say so when the ONLY thing stopping a short air picket is the enemy standoff, so
                // the map's quiet far corner and its contested near one read differently.
                if (IsAirDeliveredPicket(state, ranked.Point)
                    && mission.PicketMembers.Count < CommanderSettings.PointsMinGarrison
                    && !InsertionStandoffClear(ranked.IsFront, ranked.DistanceToEnemyMeters, CommanderSettings.OperationsHeliInsertionEnemyStandoffMeters))
                {
                    ReportInsertionDenial(
                        hq, state, ranked.Point,
                        $"{ranked.DistanceToEnemyMeters / 1000f:0} km from the enemy; no unescorted insertion inside {CommanderSettings.OperationsHeliInsertionEnemyStandoffMeters / 1000f:0} km",
                        "enemy standoff");
                }

                continue;
            }

            bool held = ReferenceEquals(ranked.Point.GetOwner(), hq);
            insertionCandidates.Add((
                ranked,
                mission,
                roadDistance,
                InsertionCandidateRank(
                    ranked.Point.Kind,
                    held,
                    CommanderGameAccess.HorizontalDistance(
                        territory.AsVector3(), ranked.Point.Position.AsVector3())),
                IsPriorityInsertionSite(ranked.Point.Kind, held)));
        }

        // Resource sites we do not hold first, nearest to the commander's territory first among them
        // (user instruction 2026-09-14): taking a site permits the mine and the mine pays, and
        // nothing else on the map does. Everything else keeps the order it had — farthest off the
        // road first (pickets-first Section 4), because the point no picket could ever drive to is
        // the one that most needs the flight. The ranked order this loop used to run in was value
        // order, which on a mountain map handed the flight to whichever point paid best.
        insertionCandidates.Sort(static (a, b) =>
        {
            int byRank = a.Rank.CompareTo(b.Rank);
            return byRank != 0 ? byRank : b.RoadDistance.CompareTo(a.RoadDistance);
        });

        int requested = 0;
        for (int i = 0; i < insertionCandidates.Count && requested < InsertionRequestsPerReview; i++)
        {
            (CommanderRankedPoint ranked, CommanderOperationsMission mission, float roadDistance, _, bool siteFirst) =
                insertionCandidates[i];

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

            // The rung's bank (design Section 3, departure 2026-09-14): a point that passed every
            // gate waits while rung 3 is still saving toward a whole flight, and says so once
            // rather than declining every review. A bank that cannot pay for one flight cannot pay
            // for any, so this ends the review's requests, not just this point's.
            if (!PicketSavingsCover(state.InsertionAllowance, state.InsertionSavingsTarget))
            {
                ReportPicketSaving(hq, state, ranked.Point);
                return;
            }

            if (RequestInsertion(hq, state, mission, ranked.Point, roadDistance, state.InsertionAllowance, siteFirst))
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

        // A FOB order standing on the point owns its ring too (fob-construction_20260914): a picket
        // flight and a construction flight racing for the same hold posts would have the supply
        // side's per-point records cancelling each other.
        for (int i = 0; i < state.FobOrders.Count; i++)
        {
            if (ReferenceEquals(state.FobOrders[i].Point, point))
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

        // FOB construction flights fly the same transports out of the same hangars
        // (fob-construction_20260914), so they are bound by the same airborne limit; without this a
        // FOB order would empty the sky of picket transports for as long as it ran.
        return count + CountFobFlightsInFlight(state);
    }

    /// <summary>
    /// Opens the record and launches the flight. The landing post is the hold post nearest the
    /// commander's territory centre — the same posts the picket will hold, so the vehicles roll out
    /// of the ramp almost onto their stations.
    /// </summary>
    /// <param name="siteFirst">True when this point is a resource site the commander does not hold —
    /// the queue jump the user asked for on 2026-09-14. It changes nothing about the flight; it is
    /// what the request line says, so the log shows WHY this point was served before the hilltops
    /// that are farther off the road.</param>
    private bool RequestInsertion(
        FactionHQ hq, OperationsState state, CommanderOperationsMission mission, CommanderStrategicPoint point, float roadDistance, float allowance, bool siteFirst)
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

        // Does the point's own ring offer ground a transport can land on? (user report 2026-09-14:
        // "air insertion of pickets sometimes is sent to land in tree covered areas".) Scouted here,
        // before the record opens, so a blocked ring turns into an airdrop or a relocation rather
        // than into a transport that hovers over a wood until the stall clock recalls it.
        CommanderSupplyHeliService.LandingZoneReport report = CommanderSupplyHeliService.ScoutLandingZone(lz);
        bool loadCanAirdrop = !report.Clear
            && CommanderSupplyHeliService.Instance?.HasLaunchableVehicleTransport(hq, requireAirdrop: true) == true;
        GlobalPosition alternate = lz;
        bool alternateFound = !report.Clear
            && !loadCanAirdrop
            && CommanderSupplyHeliService.TryFindClearLandingZone(lz, out alternate);
        CommanderInsertionDelivery delivery = ChooseInsertionDelivery(report.Clear, loadCanAirdrop, alternateFound);
        if (!report.Clear)
        {
            CommanderAiLog.Note(
                hq, $"LZ {point.Label} blocked by {report.Reason}.");
        }

        bool airdrop = false;
        switch (delivery)
        {
            case CommanderInsertionDelivery.Airdrop:
                airdrop = true;
                CommanderAiLog.Note(
                    hq,
                    $"PICKET {point.Label}: airdropping the picket at {AirdropAltitudeMeters:0} m instead of landing.");
                break;
            case CommanderInsertionDelivery.Relocate:
                CommanderAiLog.Note(
                    hq,
                    $"PICKET {point.Label}: landing zone moved "
                        + $"{CommanderGameAccess.HorizontalDistance(lz.AsVector3(), alternate.AsVector3()):0} m to clear ground.");
                lz = alternate;
                break;
            case CommanderInsertionDelivery.Decline:
                // Nothing can reach this ring from the air. The point is marked so the delivery rule
                // stops reserving it for a flight, and the cooldown makes the drive fill pick it up
                // this review rather than next (pickets-first Section 4's own coordination).
                point.Wooded = true;
                state.InsertionCooldownUntil[point] =
                    Time.time + CommanderSettings.OperationsHeliInsertionCooldownMinutes * 60f;
                ReportInsertionDenial(
                    hq,
                    state,
                    point,
                    $"no clear landing zone within {CommanderSettings.OperationsLzSearchRadiusMeters:0} m of {point.Label}"
                        + " and no parachute-capable cargo",
                    "no clear landing zone");
                return false;
        }

        // What this flight is for: the vehicles the point is actually missing, not a standing pair
        // (user decision 2026-09-14). A picket that has lost one of its two asks for ONE — the
        // establishment has no room for the other, and a full pair would buy a vehicle that then
        // stands about as surplus. A point with nothing on it still asks for the whole garrison.
        int held = mission.PicketMembers.Count;
        int wanted = Mathf.Clamp(
            CommanderSettings.PointsMinGarrison - held, 1, MaxInsertionCargoVehicles);

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
            // What the flight CARRIES, so the delivered/expected bookkeeping closes when the last
            // vehicle rolls off rather than waiting for a load that was never loaded.
            ExpectedLoads = wanted,
            RequestedAt = Time.time,
            Airdrop = airdrop,
        };
        state.Insertions.Add(insertion);
        CommanderAiLog.Note(
            hq,
            held > 0
                ? $"PICKET {mission.Label}: requesting air reinforcement ({wanted} vehicle"
                    + (wanted == 1 ? string.Empty : "s") + " short)."
                : siteFirst
                    ? $"PICKET {mission.Label}: requesting air insertion (site first)."
                    : $"PICKET {mission.Label}: requesting air insertion ({roadDistance:0} m from the nearest road).");

        string decline = "no transport service available";
        float charged = 0f;
        if (CommanderSupplyHeliService.Instance?.TryLaunchInsertionAircraft(
                hq, point, lz, allowance, wanted, airdrop, out decline, out charged) != true)
        {
            state.Insertions.Remove(insertion);
            if (decline == CommanderSupplyHeliService.PicketShareDecline)
            {
                // The bank, not the balance, was short — the rung is saving, not refusing. Said
                // the same way the pre-gate above says it, so a point never prints a refusal for
                // money that is on its way (the 2026-09-14 match logged this decline 92 times).
                // The flight THIS request would fly is priced and remembered when the bank already
                // stood at its target: the target was too low, and the ladder banks toward the
                // real price from the next review.
                // Priced over the bases whose route to THIS point is clear: that is the flight the
                // bank has to reach, not the cheaper one sitting behind a launcher belt.
                float priced = CommanderSupplyHeliService.Instance?.CheapestInsertionFlightValue(hq, wanted, airdrop, point.Position) ?? float.MaxValue;
                if (priced < float.MaxValue && PicketSavingsCover(state.InsertionAllowance, state.InsertionSavingsTarget))
                {
                    state.InsertionRefusedFlightPrice = Mathf.Max(state.InsertionRefusedFlightPrice, priced);
                }

                ReportPicketSaving(hq, state, point, priced, airdrop);
            }
            else
            {
                ReportInsertionDenial(hq, state, point, decline);
            }

            return false;
        }

        // The flight's cost comes off the rung's share and onto the tally the next ladder line
        // reports (design Section 3: "hull + vehicles from the rung's share"). Charged is the
        // worst-case total — a hull found free in stock leaves the share with headroom for the
        // next flight inside the same cycle.
        state.InsertionAllowance = Mathf.Max(0f, state.InsertionAllowance - charged);
        state.InsertionSpentSinceLadder += charged;
        // A flight got away at this bank, so whatever price the bank was learning is settled.
        state.InsertionRefusedFlightPrice = 0f;

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

    /// <summary>The dedup key the saving line books against, so the point's next real refusal still
    /// logs and a point that is merely waiting for money does not.</summary>
    private const string SavingForFlightKey = "saving for the flight";

    /// <summary>
    /// Says that this point is waiting on rung 3's bank rather than being refused, once per point
    /// per stretch of saving (departure 2026-09-14). The running total is on the ladder line's
    /// <c>pickets saved N/M</c> every review, so the progress is visible without a line per point
    /// per 30 s — the decline this replaces printed 92 times in one match.
    /// </summary>
    private static void ReportPicketSaving(
        FactionHQ hq, OperationsState state, CommanderStrategicPoint point, float pricedFlight = float.MaxValue, bool airdrop = false)
    {
        if (state.InsertionDenials.TryGetValue(point, out string? last) && last == SavingForFlightKey)
        {
            return;
        }

        state.InsertionDenials[point] = SavingForFlightKey;
        string price = pricedFlight < float.MaxValue
            ? $"; this {(airdrop ? "airdrop" : "landing")} flight would cost {pricedFlight:0}"
            : string.Empty;
        CommanderAiLog.Note(
            hq,
            $"PICKET {point.Label}: saving for the flight "
                + $"({state.InsertionAllowance:0}/{state.InsertionSavingsTarget:0}{price}).");
    }

    /// <summary>The flight price a refused launch proved the bank's target was below, or 0 — read
    /// by the ladder so rung 3 banks toward what the requests actually cost.</summary>
    internal static float RefusedInsertionFlightPrice(FactionHQ hq)
    {
        return Instance != null && Instance.states.TryGetValue(hq, out OperationsState state)
            ? state.InsertionRefusedFlightPrice
            : 0f;
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

            // The two in-flight recalls — the launch gate re-asked over what is LEFT of the route,
            // and the landing zone's own standoff re-asked against anything spotted — MOVED to the
            // logistics watch on 2026-09-16 (user decision): enemy presence changes far faster than
            // this thirty-second sweep, and a rule that lived in two clocks could answer twice. They
            // are now in Operations/CommanderOperationsLogistics.cs and run every five seconds; this
            // sweep keeps what needs its own cadence — the loss counting above, the stall clock and
            // the stale-request valve below, all measured in minutes.

            // The stall clock (user report 2026-09-14: "it cannot land, units aren't dropped,
            // stuck"). A flight that has been sitting over its landing zone without dropping is not
            // going to start: first try turning it into an airdrop — the user's preferred answer —
            // and recall it only when the load has no parachutes or the drop run stalls too.
            if (insertion.Delivered < insertion.ExpectedLoads
                && insertion.Aircraft != null
                && !insertion.Aircraft.disabled)
            {
                bool atLandingZone = CommanderGameAccess.HorizontalDistance(
                    insertion.Aircraft.transform.position, insertion.Lz.ToLocalPosition())
                    <= InsertionStallRadiusMeters;
                if (!atLandingZone)
                {
                    insertion.NearLandingZoneSince = 0f;
                }
                else if (insertion.NearLandingZoneSince <= 0f)
                {
                    insertion.NearLandingZoneSince = Time.time;
                }
                else if (HasStalledAtLandingZone(
                    Time.time - insertion.NearLandingZoneSince,
                    CommanderSettings.OperationsInsertionStallTimeoutSeconds))
                {
                    // The delivery bypass's bounded wait (delivery-bypass_20260916, design section
                    // 4.2). Before the parachute conversion, ask whether the transport is low enough
                    // to put its vehicles down where it is. This is what stops "must also be low"
                    // becoming a new way to hover for ever: the clock that has always bounded the
                    // hover now bounds the height test too. A refusal falls through to exactly the
                    // two answers this branch has always given.
                    if (!insertion.Airdrop
                        && CommanderSupplyHeliService.Instance?.TryForceUnloadInPlace(hq, insertion.Point) == true)
                    {
                        // Its own full window before the recall bites, as the airdrop conversion gets.
                        insertion.NearLandingZoneSince = Time.time;
                        continue;
                    }

                    if (!insertion.Airdrop
                        && CommanderSupplyHeliService.Instance?.TryConvertInsertionToAirdrop(hq, insertion.Point) == true)
                    {
                        insertion.Airdrop = true;
                        // The drop run gets its own full window before the recall bites.
                        insertion.NearLandingZoneSince = Time.time;
                        CommanderAiLog.Note(
                            hq,
                            $"{insertion.Point.Label}: could not land after "
                                + $"{CommanderSettings.OperationsInsertionStallTimeoutSeconds:0} s; "
                                + $"switching the insertion flight to an airdrop at {AirdropAltitudeMeters:0} m.");
                        continue;
                    }

                    insertion.Point.Wooded = true;
                    CommanderSupplyHeliService.Instance?.CancelInsertion(hq, insertion.Point);
                    state.Insertions.RemoveAt(i);
                    state.InsertionCooldownUntil[insertion.Point] =
                        Time.time + CommanderSettings.OperationsHeliInsertionCooldownMinutes * 60f;
                    CommanderAiLog.Note(
                        hq,
                        $"{insertion.Point.Label}: recalls the insertion flight; could not land at "
                            + $"{insertion.Point.Label}. Cooldown "
                            + $"{CommanderSettings.OperationsHeliInsertionCooldownMinutes:0} min, the picket drives instead.");
                    continue;
                }
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
    /// <param name="flightId">The slot the spawning request carried (lift-wave_20260916), so one
    /// transport of a three-ship lift wave binds to the record that asked for it. Zero — every
    /// picket insertion — keeps the first-free-record match.</param>
    internal static void NotifyInsertionAircraft(
        FactionHQ hq, CommanderStrategicPoint point, int flightId, Aircraft aircraft)
    {
        CommanderOperationsService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        // A FOB construction flight goes to the same point as a picket insertion would, so the FOB
        // records are asked first and claim the spawn when one of them is waiting for it
        // (fob-construction_20260914).
        if (service.TryBindFobAircraft(state, point, flightId, aircraft))
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
    /// <param name="flightId">The slot of the flight that unloaded (lift-wave_20260916), so a wave
    /// credits the right load. Zero — every picket insertion — keeps the point-only match.</param>
    internal static void NotifyPicketVehicleDelivered(
        FactionHQ hq, CommanderStrategicPoint point, int flightId, Unit unit)
    {
        CommanderOperationsService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out OperationsState state) || unit == null)
        {
            return;
        }

        // A construction load is a delivery for the FOB order, not a picket member: it stands on the
        // site until the buildings go up and is consumed by them (fob-construction_20260914).
        if (service.TryTakeFobDelivery(hq, state, point, flightId, unit))
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

        // A lost construction flight is the FOB order's loss, counted once by its own sweep
        // (fob-construction_20260914) rather than stamping a picket cooldown on the point.
        if (service.TryNoteFobFlightLost(state, point))
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
    /// Called by the supply side when an insertion transport was abandoned on the deck before it
    /// ever flew (overnight log 2026-09-15). Neither a loss nor a stale request: the FOB order keeps
    /// its count and sends the load again, the picket request re-opens with no cooldown and no mark
    /// on the loss streak, and the supply side has already closed the base that failed.
    /// </summary>
    /// <param name="flightId">The slot of the load abandoned on the deck (lift-wave_20260916), so a
    /// wave gives up exactly that one. Zero — every picket insertion — keeps the point-only match.</param>
    internal static void NoteInsertionLaunchFailed(
        FactionHQ hq, CommanderStrategicPoint point, int flightId, string baseLabel)
    {
        CommanderOperationsService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        if (service.TryNoteFobLaunchFailed(hq, state, point, flightId, baseLabel))
        {
            return;
        }

        for (int i = state.Insertions.Count - 1; i >= 0; i--)
        {
            CommanderInsertion insertion = state.Insertions[i];
            if (!ReferenceEquals(insertion.Point, point))
            {
                continue;
            }

            state.Insertions.RemoveAt(i);
            CommanderSupplyHeliService.Instance?.CancelInsertion(hq, point);
            CommanderAiLog.Note(
                hq,
                $"insertion flight to {point.Label} was abandoned on the deck at {baseLabel}; "
                    + "the request re-opens with no cooldown — it will be flown from another base.");
            return;
        }
    }

    /// <summary>
    /// The insertion gate, chooser and adoption at their named boundaries (design Section 4), next
    /// to the operations service's other self-checks.
    /// </summary>
    private static void CheckInsertion(List<string> failures)
    {
        // The rounding case that grounded every picket flight (2026-09-14): two 0.8 vehicles under
        // a 1.6 budget, where 1.6f - 0.8f is not exactly 0.8f.
        List<CommanderPlatoonRole> pairRoles = new() { CommanderPlatoonRole.Other, CommanderPlatoonRole.Other, CommanderPlatoonRole.Other };
        List<float> pairValues = new() { 2f, 0.8f, 0.8f };
        List<int> pairPicks = new();
        PickInsertionCargo(pairRoles, pairValues, 1.6f, 2, pairPicks);
        Expect(failures, "a pair that exactly spends the cargo budget is chosen", pairPicks.Count, 2);
        PickInsertionCargo(pairRoles, pairValues, 1.5f, 2, pairPicks);
        Expect(failures, "a pair a tenth over the cargo budget is refused", pairPicks.Count, 0);

        const float gate = 2000f;
        Expect(failures, "a point exactly on the off-road gate drives", QualifiesForInsertion(true, true, true, false, gate, gate, 0, 1), false);
        Expect(failures, "a point on a road but out of depot reach flies", QualifiesForInsertion(true, true, true, false, 0f, gate, 0, 1, outOfReach: true), true);
        Expect(failures, "a rear point 30 km from the enemy is clear for an unescorted flight", InsertionStandoffClear(false, 30_000f, 25_000f), true);
        Expect(failures, "a rear point 20 km from the enemy is inside the standoff", InsertionStandoffClear(false, 20_000f, 25_000f), false);
        Expect(failures, "a front point is never clear whatever the distance", InsertionStandoffClear(true, 90_000f, 25_000f), false);
        Expect(failures, "a point on a road within depot reach drives", QualifiesForInsertion(true, true, true, false, 0f, gate, 0, 1, outOfReach: false), false);
        Expect(failures, "a point past the gate flies", QualifiesForInsertion(true, true, true, false, gate + 1f, gate, 0, 1), true);
        Expect(failures, "a roadless map flies every picket", QualifiesForInsertion(true, true, true, false, float.MaxValue, gate, 0, 1), true);
        Expect(failures, "a live cooldown drives instead", QualifiesForInsertion(true, true, true, true, gate + 1f, gate, 0, 1), false);
        Expect(failures, "a front point never flies", QualifiesForInsertion(false, true, true, false, float.MaxValue, gate, 0, 1), false);
        Expect(failures, "a full picket never flies", QualifiesForInsertion(true, false, true, false, float.MaxValue, gate, 0, 1), false);
        Expect(failures, "a bound point never double-lifts", QualifiesForInsertion(true, true, false, false, float.MaxValue, gate, 0, 1), false);

        // The queue order (user instruction 2026-09-14, "resource sites need to be a priority for
        // air insertion"). Lower flies first, and a site we do not hold beats every other point at
        // any distance — including the farthest-off-road hilltop the previous rule served first.
        const float near = 5000f;
        const float far = 50000f;
        Expect(
            failures,
            "an unheld resource site flies before a hilltop the same distance away",
            InsertionCandidateRank(StrategicPointKind.Site, false, near)
                < InsertionCandidateRank(StrategicPointKind.Hilltop, false, near),
            true);
        Expect(
            failures,
            "a far resource site still flies before a near hilltop",
            InsertionCandidateRank(StrategicPointKind.Site, false, far)
                < InsertionCandidateRank(StrategicPointKind.Hilltop, false, near),
            true);
        Expect(
            failures,
            "a site we already hold queues like any other point",
            InsertionCandidateRank(StrategicPointKind.Site, true, near),
            InsertionCandidateRank(StrategicPointKind.Hilltop, false, far));
        Expect(
            failures,
            "the nearer of two unheld sites flies first",
            InsertionCandidateRank(StrategicPointKind.Site, false, near)
                < InsertionCandidateRank(StrategicPointKind.Site, false, far),
            true);
        Expect(
            failures,
            "a site off the far end of the map is still a site",
            InsertionCandidateRank(StrategicPointKind.Site, false, float.MaxValue)
                < InsertionCandidateRank(StrategicPointKind.Crossroads, false, 0f),
            true);
        Expect(
            failures,
            "a site at the commander's own doorstep never ranks below zero",
            InsertionCandidateRank(StrategicPointKind.Site, false, -100f),
            0f);
        Expect(
            failures,
            "only an unheld site jumps the queue",
            IsPriorityInsertionSite(StrategicPointKind.Site, false)
                && !IsPriorityInsertionSite(StrategicPointKind.Site, true)
                && !IsPriorityInsertionSite(StrategicPointKind.Village, false),
            true);

        // The clear-landing-zone rule at its own boundaries (user report 2026-09-14).
        Expect(failures, "open ground is a landing zone", IsLandingZoneClear(0, 0, 4f, LzMaxTreesInClearRadius, LzMaxSlopeDegrees), true);
        Expect(failures, "a stray tree is still a landing zone", IsLandingZoneClear(LzMaxTreesInClearRadius, 0, 4f, LzMaxTreesInClearRadius, LzMaxSlopeDegrees), true);
        Expect(failures, "one tree past the limit is woodland", IsLandingZoneClear(LzMaxTreesInClearRadius + 1, 0, 4f, LzMaxTreesInClearRadius, LzMaxSlopeDegrees), false);
        Expect(failures, "a single obstacle blocks the landing zone", IsLandingZoneClear(0, 1, 4f, LzMaxTreesInClearRadius, LzMaxSlopeDegrees), false);
        Expect(failures, "ground exactly on the slope limit still lands", IsLandingZoneClear(0, 0, LzMaxSlopeDegrees, LzMaxTreesInClearRadius, LzMaxSlopeDegrees), true);
        Expect(failures, "ground past the slope limit does not", IsLandingZoneClear(0, 0, LzMaxSlopeDegrees + 0.5f, LzMaxTreesInClearRadius, LzMaxSlopeDegrees), false);

        // The delivery order: airdrop before relocation, relocation before declining.
        Expect(failures, "a clear landing zone is landed on", ChooseInsertionDelivery(true, true, true) == CommanderInsertionDelivery.Land, true);
        Expect(failures, "a blocked zone with parachutes is airdropped", ChooseInsertionDelivery(false, true, true) == CommanderInsertionDelivery.Airdrop, true);
        Expect(failures, "airdrop beats relocating even when clear ground exists", ChooseInsertionDelivery(false, true, false) == CommanderInsertionDelivery.Airdrop, true);
        Expect(failures, "without parachutes the flight relocates", ChooseInsertionDelivery(false, false, true) == CommanderInsertionDelivery.Relocate, true);
        Expect(failures, "nowhere to land and no parachutes declines", ChooseInsertionDelivery(false, false, false) == CommanderInsertionDelivery.Decline, true);

        // The stall clock.
        const float stall = 120f;
        Expect(failures, "a flight exactly on the stall timeout is not yet stuck", HasStalledAtLandingZone(stall, stall), false);
        Expect(failures, "a flight past the stall timeout is stuck", HasStalledAtLandingZone(stall + 1f, stall), true);
        Expect(failures, "a disabled stall timeout never recalls", HasStalledAtLandingZone(float.MaxValue, 0f), false);

        // A wooded point is reserved for the air only when the roster can parachute onto it.
        Expect(failures, "a wooded point with parachutes still flies", PicketDeliveryMode(true, true, false, false) == CommanderPicketDelivery.Air, true);
        Expect(failures, "a wooded point without parachutes drives", PicketDeliveryMode(true, false, false, false) == CommanderPicketDelivery.Drive, true);
        Expect(failures, "the airborne limit blocks the next flight", QualifiesForInsertion(true, true, true, false, float.MaxValue, gate, 1, 1), false);

        // The delivery rule (departure 2026-09-14): an off-road point is RESERVED for the flight,
        // and gives the reservation up the moment the flight cannot happen.
        Expect(
            failures,
            "an off-road point is reserved for the flight",
            PicketDeliveryMode(true, true, false, false),
            CommanderPicketDelivery.Air);
        Expect(
            failures,
            "a point beside a road always drives",
            PicketDeliveryMode(false, true, false, false),
            CommanderPicketDelivery.Drive);
        Expect(
            failures,
            "no transport that can carry vehicles means the off-road point drives",
            PicketDeliveryMode(true, false, false, false),
            CommanderPicketDelivery.Drive);
        Expect(
            failures,
            "the loss-streak pause hands every off-road point back to the drive fill",
            PicketDeliveryMode(true, true, true, false),
            CommanderPicketDelivery.Drive);
        Expect(
            failures,
            "a point inside its own loss cooldown drives",
            PicketDeliveryMode(true, true, false, true),
            CommanderPicketDelivery.Drive);
        Expect(
            failures,
            "nothing is reserved when nothing can fly at all",
            PicketDeliveryMode(false, false, true, true),
            CommanderPicketDelivery.Drive);

        List<int> picks = new();
        List<CommanderPlatoonRole> roles = new() { CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.Other };
        List<float> values = new() { 10f, 20f };

        PickInsertionCargo(roles, values, 100f, 2, picks);
        ExpectSequence(failures, "air defence plus the cheapest other", picks, 0, 1);

        // The reinforcement load (user decision 2026-09-14): a picket one vehicle short flies ONE
        // vehicle, and it is the air-defence one — a rear point's threat is aircraft, whether the
        // point is being garrisoned for the first time or topped up after a loss.
        PickInsertionCargo(roles, values, 100f, 1, picks);
        ExpectSequence(failures, "a picket one vehicle short flies one vehicle", picks, 0);
        PickInsertionCargo(roles, values, 100f, 0, picks);
        ExpectSequence(failures, "a request for nothing still flies the one vehicle a flight is for", picks, 0);
        PickInsertionCargo(roles, values, 100f, 5, picks);
        ExpectSequence(failures, "no flight ever carries more than the two vehicles a transport mounts", picks, 0, 1);

        // An air-defence vehicle that does not fit the budget must not be bought on credit: the
        // cheaper pair wins instead.
        values = new List<float> { 100f, 5f, 30f };
        roles = new List<CommanderPlatoonRole>
        {
            CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.Other, CommanderPlatoonRole.Other,
        };
        PickInsertionCargo(roles, values, 50f, 2, picks);
        ExpectSequence(failures, "an unaffordable air-defence vehicle falls back to two others", picks, 1, 2);

        roles = new List<CommanderPlatoonRole> { CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.Other };
        values = new List<float> { 10f, 12f, 100f };
        PickInsertionCargo(roles, values, 50f, 2, picks);
        ExpectSequence(failures, "a second air-defence vehicle is preferable to no load at all", picks, 0, 1);

        roles = new List<CommanderPlatoonRole> { CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.Other };
        values = new List<float> { 10f, 100f };
        PickInsertionCargo(roles, values, 50f, 2, picks);
        ExpectSequence(failures, "a lone affordable vehicle buys nothing when two are wanted", picks);
        PickInsertionCargo(roles, values, 50f, 1, picks);
        ExpectSequence(failures, "that same lone affordable vehicle IS the load when only one is wanted", picks, 0);

        roles = new List<CommanderPlatoonRole> { CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.Other };
        values = new List<float> { 10f, 10f };
        PickInsertionCargo(roles, values, 20f, 2, picks);
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
        Expect(failures, "a construction flight takes the heavier hull over a cheaper total",
            InsertionCombinationBeats(false, 118f, 120f, false, 30f, 32f, preferHeavyHull: true), true);
        Expect(failures, "air defence still beats a heavier hull",
            InsertionCombinationBeats(true, 30f, 32f, false, 118f, 120f, preferHeavyHull: true), true);
        Expect(failures, "with the preference off the cheaper total wins whatever the hull",
            InsertionCombinationBeats(false, 118f, 120f, false, 30f, 32f, preferHeavyHull: false), false);

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

        // Reinforcement (user decision 2026-09-14): the gate does not care WHY a picket is short,
        // only that it is — a point that has lost a vehicle asks again exactly as an empty one does,
        // under the same per-point cooldown, and a picket beside a road still drives either way.
        Expect(
            failures,
            "a picket that has lost a vehicle at an off-road point asks for another flight",
            QualifiesForInsertion(true, true, true, false, 5000f, 2000f, 0, DefaultHeliInsertionLimit),
            true);
        Expect(
            failures,
            "that same picket waits out its loss cooldown before it asks again",
            QualifiesForInsertion(true, true, true, true, 5000f, 2000f, 0, DefaultHeliInsertionLimit),
            false);
        Expect(
            failures,
            "a picket short of a vehicle beside a road drives rather than flying",
            QualifiesForInsertion(true, true, true, false, 1500f, 2000f, 0, DefaultHeliInsertionLimit),
            false);
    }

    /// <summary>The shipped default of <c>OperationsHeliInsertionLimit</c>, so the self-check can
    /// pin the per-review cap against it without reading a config a player may have retuned. Raised
    /// 3 -> 6 on 2026-09-14 with the key rename to <c>Operations/HeliInsertionFlightsMax</c>.</summary>
    private const int DefaultHeliInsertionLimit = 6;
}