using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Air support (design.md, air-support-tasking_20260913; CAP first, user decision 2026-09-13): a CAP
/// wing over every active objective in the operations plan — an attack target, a platoon in
/// contact, a forward base under a threat mark — sized to a per-objective baseline plus one per
/// tracked hostile aircraft, and only then CAS over the same objectives, sized to the observed
/// picture the ground attack sizing reads. The operations mission list is the single source of air
/// objectives; the Air Command mission table stays the only way an AI aircraft is flown.
/// </summary>
/// <remarks>
/// ponytail: no ARAD/AWACS sorties, no naval tasking, no persistence of bindings across a hot
/// reload (rebuilt from the live roster the way the rest of the operations state is).
/// </remarks>
internal sealed partial class CommanderOperationsService
{
    /// <summary>CAS area over an objective. Inside the 8 km ring the sizing reads
    /// (<see cref="ObservedRadiusMeters"/>) so the strike engages what the sizing saw, wide enough
    /// that the station-keeping clamp (0.75 x radius) still leaves an orbit. Fixed, so no floor.</summary>
    private const float CasSortieRadiusMeters = 6000f;

    /// <summary>The objective must move this far before a bound mission's area is rewritten — a
    /// retask is one dictionary write but it makes the pilot re-path, so contact-anchor drift
    /// inside half the sortie ring is left alone.</summary>
    private const float CasRetargetMeters = 3000f;

    /// <summary>Most CAS airframes one objective may have. More than four over one ring
    /// concentrates the wing and overkills; the rest belongs to a second objective or the CAP.</summary>
    internal const int CasPerObjectiveCap = 4;

    /// <summary>CAP fighters every active objective is owed before anything is seen there (user
    /// decision 2026-09-13: CAP first). The wing's first job is air superiority, and the tracking
    /// picture only shows what has already been spotted — the fighter on station over the objective
    /// is what spots the rest.</summary>
    internal const int CapBaselinePerObjective = 1;

    /// <summary>Most CAP fighters one objective may have — the baseline plus one per tracked hostile
    /// aircraft in the ring, capped here. One fighter holds the ring, a pair covers a two-ship, the
    /// third chases the survivors; past three the wing concentrates under one sky while every other
    /// objective goes bare.</summary>
    internal const int CapPerObjectiveCap = 3;

    /// <summary>Nominal jet transit speed for the launch lead time. Conservative cruise, so an
    /// arrival estimate errs late (an attack waiting on it) rather than early.</summary>
    internal const float JetTransitMetersPerSecond = 200f;

    /// <summary>Nominal rotary transit speed — about 290 km/h, what a laden transport heli
    /// actually covers ground at, not its dash speed.</summary>
    internal const float RotaryTransitMetersPerSecond = 80f;

    /// <summary>Observed hostile air-defence units at a loss that double the replacement cooldown:
    /// the objective is defended in exactly the way CAS dies to, so it waits twice as long before
    /// being fed another airframe.</summary>
    private const int AirDefenceHesitationCount = 2;

    /// <summary>How near the enemy a marching platoon has to be before the wing goes up ahead of
    /// contact (user decision 2026-09-14). The same 8 km ring the sizing, the escort test and the
    /// air-defence test already read (<see cref="ObservedRadiusMeters"/>), so "near the enemy"
    /// means one thing everywhere in this service — and it is the ring the platoon's own contact
    /// would be counted in, which is exactly the fight the pre-emptive sortie exists to be early
    /// for.</summary>
    private const float PreemptiveAirRangeMeters = ObservedRadiusMeters;

    /// <summary>How long a pre-emptive sortie outlives the platoon leaving the ring — two review
    /// cadences (<see cref="ReviewIntervalSeconds"/>). The ground line's own
    /// <c>ContactHoldSeconds</c> (20 s) is shorter than one review, so it would give no hysteresis
    /// at all here: a platoon that steps just outside the ring on one review would release its
    /// whole wing and re-buy it on the next. Unwinding a sortie costs a transit each way; the
    /// firing line it mirrors costs a formation change.</summary>
    private const float PreemptiveAirHoldSeconds = 2f * ReviewIntervalSeconds;

    /// <summary>CAS airframes a pre-emptive sortie is owed with nothing observed yet (user decision
    /// 2026-09-14). The CAS ladder gives zero at zero observed — correct for a static objective,
    /// wrong for a platoon walking into enemy territory, which is precisely the moment air has to
    /// already be overhead. One airframe, not more: nothing has been seen yet, and the ladder takes
    /// over the instant something is.</summary>
    internal const int PreemptiveCasBaseline = 1;

    /// <summary>
    /// How close a tracked enemy aircraft has to be to a home-CAP fighter for the fighter's death to
    /// count as a loss to enemy air (design.md, commander-priorities_20260914 Section 2). Wide on
    /// purpose: it is the "the fight was in the sky, not a strafing run" test, not a weapon-range
    /// test — a fighter shot down with hostile air anywhere near it was doing the CAP's job.
    /// </summary>
    internal const float CapLossRadiusMeters = 15000f;

    /// <summary>
    /// How long a home-CAP loss keeps buying a replacement fighter (user decision 2026-09-14:
    /// "+ 1 per CAP fighter lost to enemy air in the last 10 minutes"). Long enough that a raid is
    /// answered for its whole duration, short enough that a war long over does not keep the CAP
    /// inflated forever.
    /// </summary>
    internal const float CapLossMemoryMinutes = 10f;

    // ---- The smarter air wing (design.md, smarter-air-wing_20260914) ----

    /// <summary>How far from the launching base, along the line to the objective, a package forms
    /// up. 12 km: far enough that the orbit is not in the circuit of a strip that is still launching
    /// the rest of the package, near enough that the leg from the form-up point to the objective is
    /// the short one — the package should spend its wait over its own ground, not halfway to the
    /// enemy.</summary>
    private const float PackageFormUpDistanceMeters = 12000f;

    /// <summary>How near the form-up point an airframe counts as arrived. 3 km is the same slack
    /// the retask hysteresis (<see cref="CasRetargetMeters"/>) already allows an objective, and an
    /// AirGuard orbit is wider than a point: asking for less would never read as arrived at all.</summary>
    private const float PackageArrivalMeters = 3000f;

    /// <summary>How far behind the centre of the front the AWACS orbits. 30 km: outside the reach of
    /// the short-range air defence that sits on a front line and of a fighter sweep that has to come
    /// through the CAP first, while its radar still looks well past the far side of the fight.</summary>
    private const float AwacsFrontStandoffMeters = 30000f;

    /// <summary>The AWACS orbit's own radius, and the whole of its station when no front exists yet
    /// (design SS4: "20 km over the main base"). A wide box on purpose — the airframe is there to
    /// look, not to hold a point.</summary>
    private const float AwacsOrbitRadiusMeters = 20000f;

    /// <summary>How near two tracked hostile air-defence vehicles have to be to count as one
    /// cluster. 5 km: inside it their engagement envelopes overlap, so they are one belt to be
    /// suppressed together rather than two targets to be attacked in turn.</summary>
    private const float AradClusterLinkMeters = 5000f;

    /// <summary>Cluster size at which one anti-radiation airframe is not enough. Six emitters is
    /// more than one airframe's rack of anti-radiation missiles covers in a single pass.</summary>
    private const int AradSecondAirframeCount = 6;

    /// <summary>Tracked hostile aircraft over a platoon per CAP fighter it is owed (addendum
    /// 2026-09-14). The same ratio the home CAP grows by
    /// (<c>CommanderSettings.HomeCapPerEnemyAircraft</c>'s default): a raid is a pair, and one
    /// fighter answers a pair.</summary>
    private const int PlatoonCapPerHostileAircraft = 2;

    /// <summary>Fighters a platoon-requested CAP is owed the moment any hostile aircraft is tracked
    /// over it — the request only opens because something was seen, so it is never zero.</summary>
    private const int PlatoonCapMinimum = 1;

    /// <summary>Most fighters one platoon-requested CAP may have. Three, the same ceiling every
    /// objective's CAP already carries (<see cref="CapPerObjectiveCap"/>): past three the wing
    /// concentrates over one platoon while every other one goes bare.</summary>
    private const int PlatoonCapMax = 3;

    /// <summary>What a sortie is for. Every kind lives in the one sortie table (design Reuse: "every
    /// new kind is a sortie kind in the existing table"), so ownership, binding, the loss cooldown
    /// and the review line are written once.</summary>
    internal enum CommanderSortieKind
    {
        /// <summary>CAS over an objective, with its CAP — the original sortie.</summary>
        Objective,

        /// <summary>Fighters over a platoon or point that has tracked hostile aircraft over it and
        /// nothing else making it an objective (addendum 2026-09-14, Section 7).</summary>
        Cap,

        /// <summary>The commander's single radar airframe, on station behind the front (Section 4).</summary>
        Awacs,

        /// <summary>Anti-radiation strike on a cluster of hostile air-defence vehicles (Section 5).</summary>
        Arad,
    }

    /// <summary>
    /// Home-CAP fighters that never leave the base, however quiet it is (user decision 2026-09-14:
    /// "leaving a minimum of 1 at the base"). One is not a defence; it is the airframe that SEES the
    /// raid — the tracking picture only holds what has been spotted, and a base with nothing overhead
    /// finds out it is being attacked when the buildings start exploding.
    /// </summary>
    internal const int HomeCapMinimumHeld = 1;

    /// <summary>
    /// How long the base ring has to stay empty of tracked hostile aircraft before the home CAP may
    /// be lent forward. Four operations reviews
    /// (<see cref="ReviewIntervalSeconds"/>): tracking decays on its own, so a single contact lost
    /// for one review must not be read as "the raid is over" and empty the base a moment before the
    /// next pass.
    /// </summary>
    internal const float HomeCapQuietSeconds = 4f * ReviewIntervalSeconds;

    /// <summary>
    /// How long an airframe just moved from a quiet sortie to one in contact is left alone (user
    /// decision 2026-09-14, Section 10). Three operations reviews
    /// (<see cref="ReviewIntervalSeconds"/>): without a hold, two fights that are both short would
    /// take the same aeroplane off each other review after review and it would spend the match in
    /// transit between them instead of over either. Three reviews is long enough for the transit
    /// plus a pass.
    /// </summary>
    internal const float RetaskHoldSeconds = 3f * ReviewIntervalSeconds;

    /// <summary>A CAS sortie over one objective, bound to the airframes flying it.</summary>
    internal sealed class CommanderAirSortie
    {
        /// <summary>What this sortie is for (design.md, smarter-air-wing_20260914).</summary>
        internal CommanderSortieKind Kind = CommanderSortieKind.Objective;

        /// <summary>Whether this sortie's CAS should be flown by attack helicopters (Section 2):
        /// forward bases, pickets and platoons in contact want rotary, attacks and pre-emptive
        /// cover want jets. Read by the buy, not by the binding — an owned jet still fills a rotary
        /// sortie's slot rather than idling.</summary>
        internal bool WantsRotary;

        /// <summary>Where this package holds until it goes in (Section 3); only meaningful while
        /// <see cref="HasFormUp"/>.</summary>
        internal GlobalPosition FormUpPoint;

        /// <summary>Whether a form-up point could be computed this review — false when the
        /// commander holds no base to measure from.</summary>
        internal bool HasFormUp;

        /// <summary>Game time the first airframe reached the form-up point, or negative while none
        /// has. The package's bounded wait runs from here.</summary>
        internal float FormUpFirstArrivalAt = -1f;

        /// <summary>The package has gone in; every airframe now flies the objective directly and
        /// late arrivals join there rather than at the form-up point. Always true for a sortie that
        /// is not a package.</summary>
        internal bool GoneIn = true;

        /// <summary>An ARAD sortie over this sortie's own objective has not gone in yet, so this
        /// package waits at its form-up point (Section 5) — bounded by the package wait like every
        /// other reason to hold.</summary>
        internal bool AradPending;

        /// <summary>Airframes at the form-up point the last time the forming line was logged, so
        /// the line prints once per change rather than once per review.</summary>
        internal int FormUpReported = -1;

        /// <summary>Set by the reconcile when this review's demand entry was matched onto a sortie
        /// that already existed. What tells an ARAD belt that has just been found from one that has
        /// been sitting there for ten minutes, so it is announced once rather than every review.</summary>
        internal bool Matched;

        /// <summary>Whether this sortie's objective is in contact with the enemy right now (user
        /// decision 2026-09-14, Section 10). A sortie in contact is never a source for a retask and
        /// is always a candidate target; a quiet one is the other way round.</summary>
        internal bool InContact;

        /// <summary>An airframe was moved onto this sortie from a quiet one this review — the
        /// <c>retask</c> mark on the review line. Fresh each review: the reconcile builds new demand
        /// objects and does not carry it over.</summary>
        internal bool RetaskedThisReview;
        /// <summary>The attack or forward-base mission this sortie serves; null for a contact sortie.</summary>
        internal CommanderOperationsMission? Mission;

        /// <summary>The platoon this sortie serves — in contact, or marching near the enemy under
        /// <see cref="Preemptive"/>; null otherwise. One field for both, so a platoon that walks
        /// into its first contact keeps the wing it already had instead of dissolving one sortie
        /// and opening another (<c>ReconcileSorties</c> matches on it).</summary>
        internal CommanderPlatoon? ContactPlatoon;

        /// <summary>True while this sortie is the pre-emptive one over a platoon that is near the
        /// enemy but not yet in contact (user decision 2026-09-14). It is what the baseline sizing,
        /// the tasking lines and the <c>pre</c> mark on the review line read.</summary>
        internal bool Preemptive;

        /// <summary>How near the enemy the platoon was when this sortie was planned, in metres, for
        /// the pre-emptive tasking line; negative for any other sortie.</summary>
        internal float EnemyDistanceMeters = -1f;

        /// <summary>Where the CAS area sits: the attack target, the last tracked contact, or the point.</summary>
        internal GlobalPosition Center;

        /// <summary>CAS airframes the ladder wants here.</summary>
        internal int Wanted;

        /// <summary>Observed hostiles the ladder read this review, for the tasking log line.</summary>
        internal int LastObserved;

        /// <summary>Hostile aircraft tracked in the objective's ring this review — one CAP fighter
        /// per tracked hostile, on top of the baseline (<see cref="CapWanted"/>).</summary>
        internal int LastHostileAir;

        /// <summary>CAP fighters the objective wants: the baseline plus one per tracked hostile
        /// aircraft in the ring, capped (<see cref="CapWanted"/>). Always at least the baseline —
        /// CAP first, CAS after (user decision 2026-09-13).</summary>
        internal int CapsWanted;

        /// <summary>Contact sorties task immediately (user answer 2026-09-13): no CAP wait, no lead
        /// time — a platoon in contact is being shot at now.</summary>
        internal bool NoCapWait;

        /// <summary>Bound CAP fighters. The first one is the escort the CAS release waits on; the
        /// rest are wingmen.</summary>
        internal readonly List<Aircraft> Caps = new();

        /// <summary>Bound CAS airframes (held-over-home ones included).</summary>
        internal readonly List<Aircraft> Cas = new();

        /// <summary>Game time until which a loss at this objective holds replacements back.</summary>
        internal float CooldownUntil = -1f;

        /// <summary>What the tasking log calls this sortie.</summary>
        internal string Label = string.Empty;
    }

    /// <summary>The buy loop's launch just made, waiting for the airframe to register so it can be
    /// claimed — the air mirror of the depot pool claim, and the mechanism that keeps the
    /// player-side commander's hands off anything it did not buy itself. EVERY commander launch
    /// records one (user decision 2026-09-13): the claim marks ownership first and binds a sortie
    /// second, so an airframe bought for a standing demand (a home-CAP fighter, a transport) is
    /// owned from the moment it registers instead of idling unowned to the ceiling.
    /// <para>The objective is null for a standing buy — there is no hungry sortie to face.</para></summary>
    internal sealed class CommanderPendingAirLaunch
    {
        internal CommanderPendingAirLaunch(
            AircraftDefinition definition, Airbase origin, GlobalPosition? objective, float expiresAt, bool forHomeCap)
        {
            Definition = definition;
            Origin = origin;
            Objective = objective;
            ExpiresAt = expiresAt;
            ForHomeCap = forHomeCap;
        }

        internal AircraftDefinition Definition { get; }
        internal Airbase Origin { get; }

        /// <summary>The sortie centre the airframe was bought to face; null when no sortie wanted it.</summary>
        internal GlobalPosition? Objective { get; }
        internal float ExpiresAt { get; }

        /// <summary>
        /// The priority ladder's rung 1 bought this airframe for the home CAP (design.md,
        /// commander-priorities_20260914): the claim parks it on the standing patrol and it is never
        /// bound to a sortie, however hungry one is.
        /// </summary>
        internal bool ForHomeCap { get; }
    }

    /// <summary>The last task this commander issued an airframe (mode + centre), so a mission that
    /// moved for any other reason is read as the player's order and the airframe is let go — the
    /// air-side hands-off rule (design, decision 5; the mirror of HasPlayerOrder).</summary>
    internal readonly struct IssuedAirTask
    {
        internal IssuedAirTask(CommanderAirCommandService.AirCommandMode mode, GlobalPosition center)
        {
            Mode = mode;
            Center = center;
        }

        internal CommanderAirCommandService.AirCommandMode Mode { get; }
        internal GlobalPosition Center { get; }
    }

    /// <summary>The CAS ladder (design SS1): observed hostiles at the objective → airframes. Pure,
    /// for the self-check.</summary>
    internal static int CasWanted(int observed)
    {
        if (observed <= 0)
        {
            return 0;
        }

        if (observed <= 2)
        {
            return 1;
        }

        if (observed <= 5)
        {
            return 2;
        }

        if (observed <= 9)
        {
            return 3;
        }

        return CasPerObjectiveCap;
    }

    /// <summary>The CAP ladder (user decision 2026-09-13): every active objective gets the
    /// baseline fighter, plus one more per tracked hostile aircraft in its ring, capped at
    /// <see cref="CapPerObjectiveCap"/>. Pure, for the self-check.</summary>
    internal static int CapWanted(int hostileAirTrackedInRing)
    {
        return Mathf.Clamp(CapBaselinePerObjective + hostileAirTrackedInRing, CapBaselinePerObjective, CapPerObjectiveCap);
    }

    /// <summary>The sortie a review plans for an objective: the CAP wing first (baseline plus one
    /// per tracked hostile aircraft), then the CAS ladder's count. CAP is wanted even with nothing
    /// observed — the objective is active. Pure, for the self-check.</summary>
    internal static (int Cas, int Cap) PlanSortie(int effectiveObserved, int hostileAirTrackedInRing)
    {
        return (CasWanted(effectiveObserved), CapWanted(hostileAirTrackedInRing));
    }

    /// <summary>Whether a platoon calls for air support ahead of contact (user decision
    /// 2026-09-14): it has to be under way — Moving or Attacking, not Forming, Holding or
    /// Withdrawing — and within <paramref name="rangeMeters"/> of the nearest enemy asset or
    /// tracked hostile. Exactly at the range counts as near; a metre past does not. Pure, for the
    /// self-check; the caller measures the distance.</summary>
    internal static bool WantsPreemptiveAir(CommanderPlatoonState state, float distanceToEnemyMeters, float rangeMeters)
    {
        // An attacking platoon is going to the enemy by definition, whatever the range reads: its
        // Objective is the next road waypoint while the axis is still en route, so the distance
        // test sat past 8 km for a whole march and no pre-emptive air ever flew for an attack.
        if (state == CommanderPlatoonState.Attacking)
        {
            return true;
        }

        return state == CommanderPlatoonState.Moving && distanceToEnemyMeters <= rangeMeters;
    }

    /// <summary>The sortie a review plans over a platoon that is near the enemy but not yet in
    /// contact (user decision 2026-09-14): the ordinary plan, with the CAS count floored at
    /// <see cref="PreemptiveCasBaseline"/> so a platoon marching into enemy territory has air
    /// overhead before the first shot. The CAP baseline is already in <see cref="CapWanted"/>.
    /// Above the baseline the ladder's values are untouched. Pure, for the self-check.</summary>
    internal static (int Cas, int Cap) PlanPreemptiveSortie(int effectiveObserved, int hostileAirTrackedInRing)
    {
        (int cas, int cap) = PlanSortie(effectiveObserved, hostileAirTrackedInRing);
        return (Mathf.Max(cas, PreemptiveCasBaseline), cap);
    }

    /// <summary>
    /// Whether a sortie forms up before it goes in (design SS3): more than one CAS airframe, or one
    /// with an escort. A single unescorted airframe has nobody to wait for, and a contact sortie
    /// never forms up at all — a platoon being shot at now cannot wait three minutes for a
    /// formation. Pure, for the self-check.
    /// </summary>
    internal static bool SortieIsPackage(int casWanted, int capWanted, bool immediate)
    {
        return !immediate && casWanted > 0 && (casWanted > 1 || capWanted > 0);
    }

    /// <summary>
    /// How far along the line from the launching base to the objective a package forms up (design
    /// SS3): the nominal 12 km, never past the objective itself, and pulled back so the point keeps
    /// at least <paramref name="standoffMeters"/> of clear air ahead of the nearest tracked hostile.
    /// <paramref name="hostileAlongMeters"/> is that hostile's distance from the base measured along
    /// the same line, or negative when nothing is tracked ahead. Never negative — with a hostile
    /// sitting on the strip the package forms up over the strip. Pure, for the self-check.
    /// </summary>
    internal static float PackageFormUpDistance(
        float baseToObjectiveMeters, float nominalMeters, float hostileAlongMeters, float standoffMeters)
    {
        float distance = Mathf.Min(nominalMeters, Mathf.Max(0f, baseToObjectiveMeters));
        if (hostileAlongMeters >= 0f)
        {
            distance = Mathf.Min(distance, hostileAlongMeters - standoffMeters);
        }

        return Mathf.Max(0f, distance);
    }

    /// <summary>
    /// Whether a package goes in this review (design SS3): when every wanted CAS airframe and the
    /// escort have reached the form-up point, or when the bounded wait has run out since the first
    /// arrival — whichever comes first. An ARAD sortie still to go in over the same objective holds
    /// the package, but only until that same wait expires: one bounded clock, no second one. Pure,
    /// for the self-check.
    /// </summary>
    internal static bool PackageGoesIn(
        int casAtFormUp,
        int casWanted,
        int capAtFormUp,
        int capWanted,
        bool aradPending,
        float secondsSinceFirstArrival,
        float timeoutSeconds)
    {
        if (secondsSinceFirstArrival >= 0f && secondsSinceFirstArrival >= timeoutSeconds)
        {
            return true;
        }

        if (aradPending)
        {
            return false;
        }

        return casAtFormUp >= casWanted && capAtFormUp >= capWanted;
    }

    /// <summary>
    /// Anti-radiation airframes one cluster of tracked hostile air-defence vehicles is owed (design
    /// SS5): none below <paramref name="minimum"/>, one for a small belt, two once the belt is
    /// <see cref="AradSecondAirframeCount"/> emitters or more. Pure, for the self-check.
    /// </summary>
    internal static int AradWanted(int clusterSize, int minimum)
    {
        if (clusterSize < Mathf.Max(1, minimum))
        {
            return 0;
        }

        return clusterSize >= AradSecondAirframeCount ? 2 : 1;
    }

    /// <summary>
    /// How far back from the centre of the front the AWACS station sits (design SS4): the 30 km
    /// stand-off, never so far back that it passes the base it is measured toward. With no front at
    /// all the station is the main base itself and the answer is zero — there is no direction to
    /// stand off along, so the 20 km of "20 km over the main base" is the orbit
    /// (<see cref="AwacsOrbitRadiusMeters"/>), not an offset. Pure, for the self-check.
    /// </summary>
    internal static float AwacsStandoffMeters(bool hasFront, float frontToBaseMeters)
    {
        if (!hasFront)
        {
            return 0f;
        }

        return Mathf.Clamp(AwacsFrontStandoffMeters, 0f, Mathf.Max(0f, frontToBaseMeters));
    }

    /// <summary>
    /// Whether a sortie's CAS should be flown by attack helicopters (design SS2, user decision
    /// 2026-09-14: helicopter CAS is chosen by mission kind). Forward bases, pickets and platoons in
    /// contact are the close fight helicopters are for; an attack and the pre-emptive cover ahead of
    /// a march are transits, which want jets. Pure, for the self-check.
    /// </summary>
    internal static bool SortieWantsRotaryCas(CommanderMissionKind kind, bool contact, bool preemptive)
    {
        if (preemptive)
        {
            return false;
        }

        if (contact)
        {
            return true;
        }

        return kind == CommanderMissionKind.ForwardBase || kind == CommanderMissionKind.Picket;
    }

    /// <summary>
    /// Fighters a platoon-requested CAP is owed (addendum 2026-09-14, Section 7): one per
    /// <see cref="PlatoonCapPerHostileAircraft"/> tracked hostile aircraft over it, at least one
    /// because the request only opens when something was seen, capped at
    /// <see cref="PlatoonCapMax"/>. The growth term is the home CAP's own formula with no baseline
    /// (Reuse rule 4, one definition). Pure, for the self-check.
    /// </summary>
    internal static int PlatoonCapWanted(int trackedHostileAir)
    {
        int grown = CommanderEnemyCommanderService.WantedHomeCap(
            baseline: 0,
            perEnemyAircraft: PlatoonCapPerHostileAircraft,
            trackedEnemyAircraft: trackedHostileAir,
            losses: 0,
            max: PlatoonCapMax);
        return Mathf.Clamp(grown, PlatoonCapMinimum, PlatoonCapMax);
    }

    /// <summary>
    /// How many home-CAP fighters may be lent forward (user decision 2026-09-14): everything above
    /// the minimum the base always keeps, never below zero. Pure, for the self-check.
    /// </summary>
    internal static int LendableHomeCap(int alive, int minimumHeld)
    {
        return Mathf.Max(0, alive - Mathf.Max(0, minimumHeld));
    }

    /// <summary>
    /// Whether the commander's own bases are quiet enough to lend the CAP forward: nothing hostile
    /// tracked in the base ring, and nothing for <paramref name="requiredSeconds"/>.
    /// <paramref name="quietSeconds"/> is how long the ring has been empty, or negative while
    /// something is in it. Exactly at the required time counts as quiet. Pure, for the self-check.
    /// </summary>
    internal static bool HomeCapIsQuiet(int trackedNearBases, float quietSeconds, float requiredSeconds)
    {
        return trackedNearBases <= 0 && quietSeconds >= 0f && quietSeconds >= requiredSeconds;
    }

    /// <summary>
    /// Home-CAP fighters the ladder still has to buy. <paramref name="lent"/> is deliberately not
    /// subtracted, and that IS the rule (user decision 2026-09-14): a lent fighter is still this
    /// commander's and comes back the instant the base is threatened, so counting it as missing
    /// would buy a replacement for an airframe that is already in the air — and then recall the
    /// original on top of it. Pure, for the self-check.
    /// </summary>
    internal static int HomeCapShortfall(int wanted, int alive, int lent)
    {
        return wanted - alive;
    }

    /// <summary>
    /// Whether a sortie's objective is in contact with the enemy (user decision 2026-09-14,
    /// Section 10: "air tasks / packages need to be retasked to requests that are in-contact with
    /// the enemy, if their current task is not in-contact with the enemy"). Anything being shot at,
    /// any attack that has gone in, and anything with a hostile tracked in its ring counts —
    /// cover over empty ground does not. Pure, for the self-check.
    /// </summary>
    internal static bool SortieIsInContact(bool objectiveInContact, bool attackGoneIn, int observed, int hostileAir)
    {
        return objectiveInContact || attackGoneIn || observed > 0 || hostileAir > 0;
    }

    /// <summary>
    /// Where a quiet sortie sits in the queue of places to take an airframe FROM (user decision
    /// 2026-09-14, Section 10). 0 is taken first, -1 is never taken.
    /// <list type="number">
    /// <item>A package still waiting at its form-up point: its airframes are airborne, together, and
    /// have not begun anything yet, so moving one costs nothing at all.</item>
    /// <item>A package that has gone in over a quiet objective: it is working, but on nothing.</item>
    /// <item>Pre-emptive cover over a platoon not yet in contact: last, because the whole point of
    /// that sortie is to already be there when the shooting starts.</item>
    /// </list>
    /// Pure, for the self-check.
    /// </summary>
    internal static int RetaskSourceRank(bool inContact, bool preemptive, bool formingAtFormUp)
    {
        if (inContact)
        {
            return -1;
        }

        if (formingAtFormUp)
        {
            return 0;
        }

        return preemptive ? 2 : 1;
    }

    /// <summary>
    /// Whether an airframe may be moved again yet (user decision 2026-09-14, Section 10):
    /// <paramref name="secondsSinceRetask"/> is how long ago it was last moved, or negative when it
    /// never has been. Exactly at the hold counts as free. Pure, for the self-check.
    /// </summary>
    internal static bool MayRetask(float secondsSinceRetask, float holdSeconds)
    {
        return secondsSinceRetask < 0f || secondsSinceRetask >= holdSeconds;
    }

    /// <summary>
    /// Whether a home-CAP fighter lent forward may be moved on to another sortie: only while the
    /// base still has its minimum patrol standing (user decision 2026-09-14, Sections 9 and 10).
    /// Moving a lent fighter does not change how many are lent, so this is the loan's own rule
    /// re-checked at the moment of the move rather than trusted from when it was made. Pure, for the
    /// self-check.
    /// </summary>
    internal static bool MayMoveHomeCapFighter(int alive, int lent, int minimumHeld)
    {
        return alive - lent >= Mathf.Max(0, minimumHeld);
    }

    /// <summary>Which slot of one sortie the next airframe fills (see
    /// <see cref="NextSortieSlot"/>).</summary>
    internal enum CommanderAirSlot
    {
        /// <summary>This sortie is full.</summary>
        None,

        /// <summary>The one fighter the sortie's CAS waits on before it goes in.</summary>
        Escort,

        /// <summary>A strike, suppression or radar airframe.</summary>
        Cas,

        /// <summary>A CAP fighter beyond the escort.</summary>
        Cap,
    }

    /// <summary>
    /// Which slot of ONE sortie the next airframe should fill: its escort first, then all of its
    /// CAS, then the CAP wingmen beyond the escort.
    /// <para>
    /// This is what "CAP first" has always meant (Approval 1, 2026-09-13: "the first CAP of a sortie
    /// is the escort its CAS waits on; the rest are wingmen") — and it is the rule the buy, the
    /// claim and the fill had all lost. They served every sortie's whole CAP demand before any
    /// sortie's CAS, which was harmless with three objectives and fatal with thirty: the player-side
    /// wing opened about fifty CAP slots, so a CAP slot was always short, so every airframe bought
    /// and every airframe claimed went to one. The 2026-09-14 match logged 37 CAP bindings and
    /// literally zero CAS bindings, with every sortie reading <c>CAS 0/4</c> for the whole match.
    /// Wingmen are worth less than the strike they are escorting; they come last.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static CommanderAirSlot NextSortieSlot(int capsBound, int capsWanted, int casBound, int casWanted)
    {
        if (capsWanted > 0 && capsBound <= 0 && casWanted > 0)
        {
            return CommanderAirSlot.Escort;
        }

        if (casBound < casWanted)
        {
            return CommanderAirSlot.Cas;
        }

        return capsBound < capsWanted ? CommanderAirSlot.Cap : CommanderAirSlot.None;
    }

    /// <summary>
    /// Whether the idle sweep adopts an aircraft it does not own (user report 2026-09-14,
    /// Section 15: after a hot reload every aeroplane read <c>GAME AI T/A-30 Compass</c>). A reload
    /// wipes the ownership set and the Air Command mission table, so every airframe the commander
    /// had bought becomes an unowned, mission-less AI aeroplane — correctly labelled, and doing
    /// nothing.
    /// <para>
    /// The guard is what keeps this from seizing a stock mission's authored free aircraft: adoption
    /// needs either the duel, where automatic AI aircraft are off and every airframe in the sky was
    /// bought, or proof that this aeroplane registered AFTER a commander was already running. An
    /// aeroplane with a human in it and one already carrying a mission are never touched.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static bool AdoptsStrayAircraft(
        bool hasHumanPilot,
        bool hasMission,
        bool commanderOwned,
        bool aiFlyable,
        bool duel,
        bool registeredWhileCommanded)
    {
        if (hasHumanPilot || hasMission || commanderOwned || !aiFlyable)
        {
            return false;
        }

        return duel || registeredWhileCommanded;
    }

    /// <summary>
    /// Whether another of the mod's services is already flying this airframe (user report
    /// 2026-09-14, Section 17). Supply runs and picket insertions are flown by the cargo service's
    /// own mission record and by the insertion book, neither of which is an Air Command mission — so
    /// every <c>IsOnAnyMission</c> test the air wing makes reads false for one, and the idle sweep
    /// was ordering transports home from halfway to their landing zone. Pure, for the self-check.
    /// </summary>
    internal static bool LeavesToOtherService(bool onSupplyRun, bool boundToInsertion)
    {
        return onSupplyRun || boundToInsertion;
    }

    /// <summary>The live read behind <see cref="LeavesToOtherService"/>. One door, consulted by the
    /// claim, the fill, the idle sweep, the stray adoption, the retask and the home-CAP loan — an
    /// airframe somebody else is flying is not the air wing's to take, whatever it looks like.</summary>
    private static bool IsUnderOtherService(OperationsState state, Aircraft aircraft)
    {
        return LeavesToOtherService(
            CommanderSupplyHeliService.IsOnSupplyRun(aircraft),
            FindInsertion(state, aircraft) != null);
    }

    /// <summary>Airframes already reported as belonging to another service, so the line prints once
    /// per aeroplane rather than once per review.</summary>
    private readonly HashSet<Aircraft> otherServiceReported = new();

    /// <summary>Says once, behind the debug flag, that an airframe was deliberately left alone.</summary>
    private void ReportOtherService(FactionHQ hq, OperationsState state, Aircraft aircraft)
    {
        if (!CommanderSettings.OperationsDebugLog || hq.faction == null || !otherServiceReported.Add(aircraft))
        {
            return;
        }

        CommanderInsertion? insertion = FindInsertion(state, aircraft);
        CommanderPlugin.Log.LogInfo(
            $"Ops {CommanderPlayerCommanderService.CommanderLabel(hq)}: leaves "
                + $"{CommanderGameAccess.GetUnitLabel(aircraft)} alone: "
                + (insertion != null
                    ? $"flying an insertion to {insertion.Point.Label}."
                    : "flying a supply run."));
    }

    /// <summary>What the buy loop should put in the air first when the wing is short of both:
    /// CAP before CAS at equal demand (user decision 2026-09-13) — when the fund covers one
    /// airframe and both are wanted, it buys the fighter. Pure, for the self-check; the demand
    /// walk gathers the shortfalls and reads its answer through here.</summary>
    internal static CommanderAirDemandKind NextAirDemand(
        bool awacsShort, bool capShort, bool aradShort, bool casShort)
    {
        // AWACS first (design SS4: rung 2, immediately after the CAP baseline): every sortie below
        // it is sized from the tracking picture, and the radar airframe is what fills that in.
        if (awacsShort)
        {
            return CommanderAirDemandKind.Awacs;
        }

        // CAP before ARAD and CAS (user decision 2026-09-13: CAP first) — when the pot covers one
        // airframe and several are wanted, it buys the fighter.
        if (capShort)
        {
            return CommanderAirDemandKind.Cap;
        }

        // ARAD before the CAS it is clearing the way for (design SS5).
        if (aradShort)
        {
            return CommanderAirDemandKind.Arad;
        }

        return casShort ? CommanderAirDemandKind.Cas : CommanderAirDemandKind.None;
    }

    /// <summary>Replacement cooldown after a loss: the base minutes, doubled while the objective's
    /// ring shows <see cref="AirDefenceHesitationCount"/> or more tracked hostile air-defence
    /// units. Pure, for the self-check.</summary>
    internal static float CasLossCooldownMinutes(int observedAirDefence, float baseMinutes)
    {
        return observedAirDefence >= AirDefenceHesitationCount ? baseMinutes * 2f : baseMinutes;
    }

    /// <summary>Launch lead time: how long a freshly launched airframe needs to reach the
    /// objective at its nominal transit speed (design Approval 1). Pure, for the self-check.</summary>
    internal static float CasTransitSeconds(float distanceMeters, bool rotary)
    {
        return distanceMeters / Mathf.Max(1f, rotary ? RotaryTransitMetersPerSecond : JetTransitMetersPerSecond);
    }

    /// <summary>
    /// Whether an attack holds its go-in for CAS (design Approval 1): only while a sortie exists for
    /// its target, wants CAS, has nothing on station, and the form-up wait has not run out. Pure,
    /// for the self-check; the caller gathers the inputs.
    /// </summary>
    internal static bool AttackHoldsForCas(
        bool sortieExists, bool casWanted, bool casOnStation, float waitedSeconds, float timeoutSeconds)
    {
        return sortieExists && casWanted && !casOnStation && waitedSeconds < timeoutSeconds;
    }

    private static void CheckAirSupport(List<string> failures)
    {
        CheckAirSuperiorityRefusal(failures);
        Expect(failures, "nothing observed means no CAS orbits empty ground", CasWanted(0), 0);
        Expect(failures, "one hostile observed gets one airframe", CasWanted(1), 1);
        Expect(failures, "two observed still wants one", CasWanted(2), 1);
        Expect(failures, "three observed wants two", CasWanted(3), 2);
        Expect(failures, "five observed wants two", CasWanted(5), 2);
        Expect(failures, "six observed wants three", CasWanted(6), 3);
        Expect(failures, "nine observed wants three", CasWanted(9), 3);
        Expect(failures, "ten observed hits the per-objective cap", CasWanted(10), CasPerObjectiveCap);
        Expect(failures, "an enormous observed force still gets the cap", CasWanted(100), CasPerObjectiveCap);

        (int _, int cap) = PlanSortie(2, 0);
        Expect(failures, "an active objective wants its baseline CAP with nothing tracked", cap, CapBaselinePerObjective);
        Expect(failures, "an objective with nothing observed still wants its baseline CAP", PlanSortie(0, 0).Cap, CapBaselinePerObjective);
        Expect(failures, "two tracked hostile aircraft put three fighters on the CAP", PlanSortie(2, 2).Cap, 3);
        Expect(failures, "the CAP ladder caps at the per-objective cap however crowded the sky", PlanSortie(5, 9).Cap, CapPerObjectiveCap);
        Expect(failures, "a negative tracking read still leaves the baseline CAP", CapWanted(-3), CapBaselinePerObjective);

        // Pre-emptive air (user decision 2026-09-14): the wing goes up before the platoon is shot at.
        Expect(failures, "a platoon moving exactly at the pre-emptive range is near the enemy", WantsPreemptiveAir(CommanderPlatoonState.Moving, PreemptiveAirRangeMeters, PreemptiveAirRangeMeters), true);
        Expect(failures, "one metre past the pre-emptive range is not near the enemy", WantsPreemptiveAir(CommanderPlatoonState.Moving, PreemptiveAirRangeMeters + 1f, PreemptiveAirRangeMeters), false);
        Expect(failures, "a platoon attacking near the enemy wants pre-emptive air", WantsPreemptiveAir(CommanderPlatoonState.Attacking, 0f, PreemptiveAirRangeMeters), true);
        Expect(failures, "a platoon attacking from far away still wants pre-emptive air: it is going to the enemy", WantsPreemptiveAir(CommanderPlatoonState.Attacking, PreemptiveAirRangeMeters * 5f, PreemptiveAirRangeMeters), true);
        Expect(failures, "a platoon still forming wants no pre-emptive air however near the enemy", WantsPreemptiveAir(CommanderPlatoonState.Forming, 0f, PreemptiveAirRangeMeters), false);
        Expect(failures, "a platoon holding its post wants no pre-emptive air", WantsPreemptiveAir(CommanderPlatoonState.Holding, 0f, PreemptiveAirRangeMeters), false);
        Expect(failures, "a withdrawing platoon wants no pre-emptive air", WantsPreemptiveAir(CommanderPlatoonState.Withdrawing, 0f, PreemptiveAirRangeMeters), false);
        Expect(failures, "a platoon with no enemy anywhere wants no pre-emptive air", WantsPreemptiveAir(CommanderPlatoonState.Moving, float.MaxValue, PreemptiveAirRangeMeters), false);
        Expect(failures, "the pre-emptive range is the observed ring, so near the enemy means one thing everywhere", PreemptiveAirRangeMeters, ObservedRadiusMeters);
        Expect(failures, "the pre-emptive hold outlasts a review, or it would give no hysteresis at all", PreemptiveAirHoldSeconds > ReviewIntervalSeconds, true);
        Expect(failures, "a pre-emptive sortie with nothing observed still gets one CAS airframe", PlanPreemptiveSortie(0, 0).Cas, PreemptiveCasBaseline);
        Expect(failures, "a pre-emptive sortie with nothing observed still gets its baseline CAP", PlanPreemptiveSortie(0, 0).Cap, CapBaselinePerObjective);
        Expect(failures, "one hostile observed leaves the pre-emptive CAS at the ladder's one airframe", PlanPreemptiveSortie(1, 0).Cas, CasWanted(1));
        Expect(failures, "six observed puts the ladder's three airframes over a pre-emptive sortie", PlanPreemptiveSortie(6, 0).Cas, CasWanted(6));
        Expect(failures, "a crowded pre-emptive sortie still stops at the per-objective CAS cap", PlanPreemptiveSortie(100, 0).Cas, CasPerObjectiveCap);
        Expect(failures, "tracked hostile air grows a pre-emptive sortie's CAP the ordinary way", PlanPreemptiveSortie(0, 2).Cap, CapWanted(2));

        Expect(failures, "CAP is served before CAS when both are short (user 2026-09-13)", (int)NextAirDemand(false, capShort: true, false, casShort: true), (int)CommanderAirDemandKind.Cap);
        Expect(failures, "CAS is served when only CAS is short", (int)NextAirDemand(false, capShort: false, false, casShort: true), (int)CommanderAirDemandKind.Cas);
        Expect(failures, "CAP is served when only CAP is short", (int)NextAirDemand(false, capShort: true, false, casShort: false), (int)CommanderAirDemandKind.Cap);
        Expect(failures, "nothing is short means nothing is bought", (int)NextAirDemand(false, capShort: false, false, casShort: false), (int)CommanderAirDemandKind.None);
        Expect(failures, "the AWACS is served before everything else in rung 2", (int)NextAirDemand(true, true, true, true), (int)CommanderAirDemandKind.Awacs);
        Expect(failures, "ARAD is served before the CAS it clears the way for", (int)NextAirDemand(false, false, aradShort: true, casShort: true), (int)CommanderAirDemandKind.Arad);
        Expect(failures, "CAP still outranks ARAD", (int)NextAirDemand(false, capShort: true, aradShort: true, casShort: false), (int)CommanderAirDemandKind.Cap);
        Expect(failures, "ARAD alone is served", (int)NextAirDemand(false, false, aradShort: true, casShort: false), (int)CommanderAirDemandKind.Arad);

        Expect(failures, "the first buy of a review may proceed", CommanderEnemyCommanderService.AirBuyContinues(0), true);
        Expect(failures, "the buys-per-review bound stops the third buy", CommanderEnemyCommanderService.AirBuyContinues(CommanderEnemyCommanderService.MaxAirBuysPerReview), false);
        Expect(failures, "the buys-per-review bound is at least one and at most a review's worth", CommanderEnemyCommanderService.MaxAirBuysPerReview >= 1 && CommanderEnemyCommanderService.MaxAirBuysPerReview <= 5, true);

        Expect(failures, "a loss with no air defence observed costs the base cooldown", CasLossCooldownMinutes(0, 2f), 2f);
        Expect(failures, "one air-defence unit does not double the cooldown", CasLossCooldownMinutes(1, 2f), 2f);
        Expect(failures, "two air-defence units double the cooldown", CasLossCooldownMinutes(2, 2f), 4f);

        Expect(failures, "a jet crosses 20 km in 100 s", CasTransitSeconds(20000f, rotary: false), 100f);
        Expect(failures, "a helicopter crosses 8 km in 100 s", CasTransitSeconds(8000f, rotary: true), 100f);
        Expect(failures, "no distance means no transit", CasTransitSeconds(0f, rotary: false), 0f);

        Expect(failures, "an attack with CAS on station goes in", AttackHoldsForCas(true, true, true, 10f, 240f), false);
        Expect(failures, "an attack holds while its CAS is off station", AttackHoldsForCas(true, true, false, 10f, 240f), true);
        Expect(failures, "the hold ends at the form-up timeout", AttackHoldsForCas(true, true, false, 240f, 240f), false);
        Expect(failures, "an attack with no sortie for its target does not hold", AttackHoldsForCas(false, false, false, 10f, 240f), false);

        // Read into locals first: the pattern CheckPressure uses — the failure branch has to
        // survive a config-file retune.
        float cooldown = CommanderSettings.CasLossCooldownMinutes;
        int ceiling = CommanderSettings.AirborneCeiling;
        Expect(failures, "the loss cooldown is positive; check the Operations section of the config", cooldown > 0f, true);
        Expect(failures, "the airborne ceiling allows at least one airframe; check the Operations section of the config", ceiling >= 1, true);

        // Packages (design SS3, smarter-air-wing_20260914).
        Expect(failures, "two CAS airframes form a package", SortieIsPackage(2, 0, immediate: false), true);
        Expect(failures, "one CAS airframe with an escort forms a package", SortieIsPackage(1, 1, immediate: false), true);
        Expect(failures, "one unescorted CAS airframe has nobody to form up with", SortieIsPackage(1, 0, immediate: false), false);
        Expect(failures, "a CAP-only sortie is not a package", SortieIsPackage(0, 3, immediate: false), false);
        Expect(failures, "a contact sortie never forms up: the platoon is being shot at now", SortieIsPackage(4, 1, immediate: true), false);

        Expect(failures, "the form-up point sits at the nominal distance on a long leg", PackageFormUpDistance(60000f, 12000f, -1f, 8000f), 12000f);
        Expect(failures, "the form-up point never passes the objective on a short leg", PackageFormUpDistance(5000f, 12000f, -1f, 8000f), 5000f);
        Expect(failures, "a tracked hostile ahead pulls the form-up point back to its stand-off", PackageFormUpDistance(60000f, 12000f, 15000f, 8000f), 7000f);
        Expect(failures, "a distant hostile leaves the nominal form-up distance alone", PackageFormUpDistance(60000f, 12000f, 40000f, 8000f), 12000f);
        Expect(failures, "a hostile on top of the strip forms the package up over the strip", PackageFormUpDistance(60000f, 12000f, 2000f, 8000f), 0f);

        Expect(failures, "a full package goes in", PackageGoesIn(3, 3, 1, 1, false, 10f, 180f), true);
        Expect(failures, "a package missing an airframe holds", PackageGoesIn(2, 3, 1, 1, false, 10f, 180f), false);
        Expect(failures, "a package missing its escort holds", PackageGoesIn(3, 3, 0, 1, false, 10f, 180f), false);
        Expect(failures, "a package that has waited its whole clock goes in short", PackageGoesIn(2, 3, 0, 1, false, 180f, 180f), true);
        Expect(failures, "a package with nobody at the form-up point yet is not on the clock", PackageGoesIn(0, 3, 0, 1, false, -1f, 180f), false);
        Expect(failures, "a full package still waits for its ARAD sortie to go in first", PackageGoesIn(3, 3, 1, 1, true, 10f, 180f), false);
        Expect(failures, "the ARAD wait is bounded by the same package clock", PackageGoesIn(3, 3, 1, 1, true, 180f, 180f), true);

        // ARAD (design SS5).
        Expect(failures, "a pair of air-defence vehicles opens no ARAD sortie", AradWanted(2, 3), 0);
        Expect(failures, "three clustered air-defence vehicles open one ARAD sortie", AradWanted(3, 3), 1);
        Expect(failures, "five clustered air-defence vehicles still want one airframe", AradWanted(5, 3), 1);
        Expect(failures, "six clustered air-defence vehicles want two airframes", AradWanted(6, 3), 2);
        Expect(failures, "a whole belt still stops at two airframes", AradWanted(30, 3), 2);
        Expect(failures, "an empty cluster opens nothing", AradWanted(0, 3), 0);

        // AWACS station geometry (design SS4).
        Expect(failures, "the AWACS stands 30 km behind a distant front", AwacsStandoffMeters(hasFront: true, 90000f), AwacsFrontStandoffMeters);
        Expect(failures, "the AWACS never stands off past the base it is measured toward", AwacsStandoffMeters(hasFront: true, 10000f), 10000f);
        Expect(failures, "with no front the AWACS orbits the main base itself", AwacsStandoffMeters(hasFront: false, 0f), 0f);
        Expect(failures, "the AWACS orbit is the 20 km the design asks for over the main base", AwacsOrbitRadiusMeters, 20000f);

        // Rotary CAS by mission kind (design SS2).
        Expect(failures, "a forward base wants helicopter CAS", SortieWantsRotaryCas(CommanderMissionKind.ForwardBase, contact: false, preemptive: false), true);
        Expect(failures, "a picket wants helicopter CAS", SortieWantsRotaryCas(CommanderMissionKind.Picket, contact: false, preemptive: false), true);
        Expect(failures, "a platoon in contact wants helicopter CAS", SortieWantsRotaryCas(CommanderMissionKind.Attack, contact: true, preemptive: false), true);
        Expect(failures, "an attack wants jets", SortieWantsRotaryCas(CommanderMissionKind.Attack, contact: false, preemptive: false), false);
        Expect(failures, "a reserve objective wants jets", SortieWantsRotaryCas(CommanderMissionKind.Reserve, contact: false, preemptive: false), false);
        Expect(failures, "a pre-emptive sortie wants jets however close the fight is", SortieWantsRotaryCas(CommanderMissionKind.ForwardBase, contact: true, preemptive: true), false);

        // Platoon-requested CAP (addendum 2026-09-14, Section 7).
        Expect(failures, "one tracked hostile aircraft over a platoon buys it one fighter", PlatoonCapWanted(1), 1);
        Expect(failures, "two tracked hostile aircraft over a platoon still buy one fighter", PlatoonCapWanted(2), 1);
        Expect(failures, "four tracked hostile aircraft over a platoon buy two fighters", PlatoonCapWanted(4), 2);
        Expect(failures, "six tracked hostile aircraft over a platoon buy three fighters", PlatoonCapWanted(6), 3);
        Expect(failures, "a platoon CAP stops at three however crowded the sky", PlatoonCapWanted(40), PlatoonCapMax);
        Expect(failures, "the platoon CAP cap matches the per-objective CAP cap", PlatoonCapMax, CapPerObjectiveCap);

        // Lending the home CAP forward (design SS9, user decision 2026-09-14).
        Expect(failures, "a four-fighter home CAP can lend three forward", LendableHomeCap(4, HomeCapMinimumHeld), 3);
        Expect(failures, "the last fighter at the base is never lent", LendableHomeCap(1, HomeCapMinimumHeld), 0);
        Expect(failures, "an empty home CAP lends nothing, never a negative count", LendableHomeCap(0, HomeCapMinimumHeld), 0);
        Expect(failures, "a home CAP below its minimum lends nothing", LendableHomeCap(1, 2), 0);
        Expect(failures, "the base always keeps at least one fighter", HomeCapMinimumHeld >= 1, true);

        Expect(failures, "a base quiet for exactly the required time may lend", HomeCapIsQuiet(0, HomeCapQuietSeconds, HomeCapQuietSeconds), true);
        Expect(failures, "a base one second short of the quiet time may not lend", HomeCapIsQuiet(0, HomeCapQuietSeconds - 1f, HomeCapQuietSeconds), false);
        Expect(failures, "a base with hostile air tracked over it never lends, however long it was quiet", HomeCapIsQuiet(1, HomeCapQuietSeconds * 10f, HomeCapQuietSeconds), false);
        Expect(failures, "a base whose quiet clock has not started may not lend", HomeCapIsQuiet(0, -1f, HomeCapQuietSeconds), false);
        Expect(failures, "the quiet wait outlasts a review, or one lost track would empty the base", HomeCapQuietSeconds > ReviewIntervalSeconds, true);

        Expect(failures, "lending two fighters forward buys no replacement for them", HomeCapShortfall(4, 4, 2), 0);
        Expect(failures, "a genuinely missing fighter is still bought while two are lent", HomeCapShortfall(4, 3, 2), 1);
        Expect(failures, "a recall changes nothing about what the rung buys", HomeCapShortfall(4, 3, 0), HomeCapShortfall(4, 3, 2));
        Expect(failures, "a surplus home CAP still reads as a surplus", HomeCapShortfall(2, 3, 1), -1);

        // Airframes another service is already flying (design SS17, user report 2026-09-14). The
        // failure was an insertion helicopter ordered home halfway to its landing zone, because a
        // supply run carries no Air Command mission and so read as idle.
        Expect(failures, "a transport on a supply run is left alone", LeavesToOtherService(true, false), true);
        Expect(failures, "a transport bound to an insertion is left alone", LeavesToOtherService(false, true), true);
        Expect(failures, "a transport doing both is still left alone", LeavesToOtherService(true, true), true);
        Expect(failures, "an airframe no other service is flying is the air wing's to task", LeavesToOtherService(false, false), false);

        // Adopting the strays a hot reload orphaned (design SS15, user report 2026-09-14).
        Expect(failures, "the duel has no authored free aircraft, so a stray there is ours", AdoptsStrayAircraft(false, false, false, true, duel: true, registeredWhileCommanded: false), true);
        Expect(failures, "a stock mission's aircraft that predates the commander is left alone", AdoptsStrayAircraft(false, false, false, true, duel: false, registeredWhileCommanded: false), false);
        Expect(failures, "a stock mission's aircraft that arrived after the commander is ours", AdoptsStrayAircraft(false, false, false, true, duel: false, registeredWhileCommanded: true), true);
        Expect(failures, "an aeroplane with a human in it is never adopted", AdoptsStrayAircraft(true, false, false, true, duel: true, registeredWhileCommanded: true), false);
        Expect(failures, "an aeroplane already carrying a mission is never adopted", AdoptsStrayAircraft(false, true, false, true, duel: true, registeredWhileCommanded: true), false);
        Expect(failures, "an airframe already owned is not adopted again", AdoptsStrayAircraft(false, false, true, true, duel: true, registeredWhileCommanded: true), false);
        Expect(failures, "an airframe no AI can fly is never adopted", AdoptsStrayAircraft(false, false, false, false, duel: true, registeredWhileCommanded: true), false);

        // The per-sortie slot order (fix, 2026-09-14): escort, then CAS, then wingmen. The failure
        // this encodes emptied every CAS slot in the wing for a whole match.
        Expect(failures, "an empty sortie takes its escort first", (int)NextSortieSlot(0, 3, 0, 4), (int)CommanderAirSlot.Escort);
        Expect(failures, "a sortie with its escort up takes strike airframes next", (int)NextSortieSlot(1, 3, 0, 4), (int)CommanderAirSlot.Cas);
        Expect(failures, "a strike half filled still takes strike airframes over wingmen", (int)NextSortieSlot(1, 3, 3, 4), (int)CommanderAirSlot.Cas);
        Expect(failures, "wingmen come only after the strike is complete", (int)NextSortieSlot(1, 3, 4, 4), (int)CommanderAirSlot.Cap);
        Expect(failures, "a full sortie asks for nothing", (int)NextSortieSlot(3, 3, 4, 4), (int)CommanderAirSlot.None);
        Expect(failures, "a CAP-only sortie takes fighters straight away", (int)NextSortieSlot(0, 2, 0, 0), (int)CommanderAirSlot.Cap);
        Expect(failures, "a sortie wanting no escort takes strike airframes straight away", (int)NextSortieSlot(0, 0, 0, 1), (int)CommanderAirSlot.Cas);
        Expect(failures, "the AWACS asks for its one airframe", (int)NextSortieSlot(0, 0, 0, 1), (int)CommanderAirSlot.Cas);
        Expect(failures, "a sortie over its wanted strength asks for nothing", (int)NextSortieSlot(5, 3, 6, 4), (int)CommanderAirSlot.None);

        // Retasking cover onto contact (design SS10, user decision 2026-09-14).
        Expect(failures, "a platoon being shot at is in contact", SortieIsInContact(true, false, 0, 0), true);
        Expect(failures, "an attack that has gone in is in contact whatever the tracking has decayed to", SortieIsInContact(false, true, 0, 0), true);
        Expect(failures, "a tracked hostile ground unit in the ring is contact", SortieIsInContact(false, false, 1, 0), true);
        Expect(failures, "a tracked hostile aircraft in the ring is contact", SortieIsInContact(false, false, 0, 1), true);
        Expect(failures, "cover over empty ground is not in contact", SortieIsInContact(false, false, 0, 0), false);

        Expect(failures, "a sortie in contact is never stripped for another", RetaskSourceRank(inContact: true, preemptive: false, formingAtFormUp: true), -1);
        Expect(failures, "a package still at its form-up point is the first place to take from", RetaskSourceRank(false, preemptive: false, formingAtFormUp: true), 0);
        Expect(failures, "a forming pre-emptive package is still taken from first", RetaskSourceRank(false, preemptive: true, formingAtFormUp: true), 0);
        Expect(failures, "a package working a quiet objective is the second place to take from", RetaskSourceRank(false, preemptive: false, formingAtFormUp: false), 1);
        Expect(failures, "pre-emptive cover is the last place to take from", RetaskSourceRank(false, preemptive: true, formingAtFormUp: false), 2);
        Expect(failures, "the forming package outranks the gone-in one as a source", RetaskSourceRank(false, false, true) < RetaskSourceRank(false, false, false), true);
        Expect(failures, "the gone-in package outranks pre-emptive cover as a source", RetaskSourceRank(false, false, false) < RetaskSourceRank(false, true, false), true);

        Expect(failures, "an airframe that has never been moved may be moved", MayRetask(-1f, RetaskHoldSeconds), true);
        Expect(failures, "an airframe moved a moment ago stays put", MayRetask(0f, RetaskHoldSeconds), false);
        Expect(failures, "an airframe one second inside its hold stays put", MayRetask(RetaskHoldSeconds - 1f, RetaskHoldSeconds), false);
        Expect(failures, "an airframe exactly at its hold may be moved again", MayRetask(RetaskHoldSeconds, RetaskHoldSeconds), true);
        Expect(failures, "the retask hold outlasts a review, or one aeroplane would ping-pong between two fights", RetaskHoldSeconds > ReviewIntervalSeconds, true);

        Expect(failures, "a lent home-CAP fighter may move on while the base keeps its minimum", MayMoveHomeCapFighter(4, 2, HomeCapMinimumHeld), true);
        Expect(failures, "the last fighter at the base is never moved on", MayMoveHomeCapFighter(2, 2, HomeCapMinimumHeld), false);
        Expect(failures, "a base exactly at its minimum still allows the loan already out to move", MayMoveHomeCapFighter(2, 1, HomeCapMinimumHeld), true);

        float formUp = CommanderSettings.PackageFormUpSeconds;
        float rotaryRange = CommanderSettings.RotaryCasRangeMeters;
        int aradMinimum = CommanderSettings.AradClusterMinimum;
        Expect(failures, "the package form-up wait is positive; check the Operations section of the config", formUp > 0f, true);
        Expect(failures, "the package form-up wait fits inside the attack's own form-up timeout; check the Operations section of the config", formUp <= AssaultFormUpTimeoutSeconds, true);
        Expect(failures, "the rotary CAS range is positive; check the Operations section of the config", rotaryRange > 0f, true);
        Expect(failures, "an ARAD cluster is at least a pair; check the Operations section of the config", aradMinimum >= 2, true);
    }

    // ---- Runtime: demand, binding, the claim, and what the buyer reads ----

    /// <summary>Registered faction units forwarded from the shared <c>RegisterFactionUnit</c>
    /// postfix; claims an airframe the buy loop just launched into the hungry sortie.</summary>
    internal static void NotifyAircraftRegistered(FactionHQ hq, Unit unit)
    {
        // Stamp it as "arrived while a commander was running" (design SS15) before the claim, so an
        // airframe the claim declines is still distinguishable from a stock mission's authored free
        // aircraft, which existed before the commander did.
        if (unit is Aircraft aircraft
            && CommanderPlayerCommanderService.AnyCommanderOn
            && Instance != null
            && Instance.states.TryGetValue(hq, out OperationsState state))
        {
            state.RegisteredWhileCommanded.Add(aircraft);
        }

        Instance?.TryClaimAircraft(hq, unit);
    }

    /// <summary>The buy loop calls this just before EVERY launch, so the registration that fires
    /// inside the launch can be matched back and the airframe owned — the same shape the player's
    /// own <c>PendingAircraftSpawn</c> uses, for the same reason. A null objective is a standing
    /// buy: the claim owns the airframe and parks it on the home CAP; a sortie claim may retask it
    /// later. <paramref name="forHomeCap"/> marks a rung-1 buy (design.md,
    /// commander-priorities_20260914: one definition, two callers — the sortie buyer and the
    /// ladder's CAP buyer), which the claim never lends to a sortie.</summary>
    internal static void RecordCommanderLaunch(
        FactionHQ hq, AircraftDefinition definition, Airbase origin, GlobalPosition? objective, bool forHomeCap = false)
    {
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        state.PendingAirLaunch = new CommanderPendingAirLaunch(
            definition, origin, objective, Time.time + CommanderAirCommandService.PendingSpawnTimeoutSeconds, forHomeCap);
    }

    /// <summary>The launch failed, so nothing will register; the expectation would sit for its
    /// whole window and could wrongly claim a same-type airframe registered by something else (a
    /// stock mission's free-aircraft tap on stock maps).</summary>
    internal static void ClearCommanderLaunch(FactionHQ hq)
    {
        if (Instance != null && Instance.states.TryGetValue(hq, out OperationsState state))
        {
            state.PendingAirLaunch = null;
        }
    }

    private void TryClaimAircraft(FactionHQ hq, Unit unit)
    {
        // The pool claim's gates, for the same reasons: host-only, and only for an HQ this service
        // manages. A pure multiplayer client must never touch spawns.
        if (!states.TryGetValue(hq, out OperationsState state)
            || !hq.IsServer
            || unit is not Aircraft aircraft
            || aircraft.disabled
            || aircraft.Player != null
            || state.PendingAirLaunch == null
            || Time.time > state.PendingAirLaunch.ExpiresAt
            || !ReferenceEquals(aircraft.definition, state.PendingAirLaunch.Definition))
        {
            return;
        }

        // Anything already carrying an Air Command mission is somebody else's airframe — the
        // player's own AIR-window launch assigns its mission at registration, and its notify runs
        // ahead of this one. Decision 5's hands-off rule, enforced here rather than by trust.
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null || airCommand.IsOnAnyMission(aircraft))
        {
            return;
        }

        // Section 17: never claim a transport the cargo service is about to match to its own run.
        if (IsUnderOtherService(state, aircraft))
        {
            return;
        }

        Airbase origin = state.PendingAirLaunch.Origin;
        bool forHomeCap = state.PendingAirLaunch.ForHomeCap;
        state.PendingAirLaunch = null;
        state.CommanderAirframes.Add(aircraft);

        // A rung-1 fighter is owned and parked on the standing home CAP here, full stop (design.md,
        // commander-priorities_20260914 Section 2): home-CAP fighters are "never lent to sorties; a
        // sortie's escort is bought separately in rung 2", so however hungry a sortie is, this
        // airframe never enters one.
        if (forHomeCap)
        {
            state.HomeCapAirframes.Add(aircraft);
            if (IssueAirTask(
                    hq,
                    state,
                    aircraft,
                    CommanderAirCommandService.AirCommandMode.AirGuard,
                    HomeCAPCentre(hq),
                    CommanderEnemyCommanderService.HomeGuardRadiusMeters))
            {
                CommanderAiLog.Note(
                    hq,
                    $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} on the home CAP; it is never lent to a sortie.");
            }

            return;
        }

        // Bind to the hungry sortie that matches what the airframe can DO (user decision
        // 2026-09-14: the same capability the buy bought on — a CAP-bought Compass with Scythes
        // binds to the sortie's CAP, not to the first CAS slot its role identity suggests); a
        // sortie standing down after a loss takes no new airframes either. CAP before CAS at
        // equal capability, the demand queue's own order. With no hungry sortie the airframe is
        // owned and parked on the home CAP right here (user decision 2026-09-13) — before the fix
        // it was bought unowned, tasked by nobody and idled to the ceiling.
        // Sortie by sortie in priority order, each one's own escort → CAS → wingmen order (see
        // NextSortieSlot), and bound by what the airframe can DO for that slot: an AWACS slot takes
        // only a radar airframe, an ARAD slot only one that can carry anti-radiation weapons. The
        // walk this replaced tried EVERY sortie's CAP before any sortie's CAS, so every claimed
        // airframe that could carry an air-to-air missile — which on this roster is nearly all of
        // them — went to a CAP slot and no strike was ever bound (2026-09-14 match).
        AircraftDefinition definition = (aircraft.definition as AircraftDefinition)!;
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (Time.time < sortie.CooldownUntil)
            {
                continue;
            }

            CommanderAirSlot slot = NextSortieSlot(
                sortie.Caps.Count, sortie.CapsWanted, sortie.Cas.Count, sortie.Wanted);
            if (slot == CommanderAirSlot.None)
            {
                continue;
            }

            bool asCap = slot != CommanderAirSlot.Cas;
            if (!CommanderEnemyCommanderService.FillsAirRole(
                    hq,
                    definition,
                    asCap ? CommanderEnemyCommanderService.AirRole.Fighter : SortieAirRole(sortie)))
            {
                continue;
            }

            if (asCap)
            {
                BindCap(hq, state, sortie, aircraft);
            }
            else
            {
                BindCas(hq, state, sortie, aircraft, origin);
            }

            return;
        }

        // Nothing that may not hold the patrol is parked on it while it waits (user reports,
        // 2026-09-14: A-19 Brawlers on AIR SUPERIORITY, then "it's spawned a UH-90 transport for CAP
        // for a platoon"). A ground-attack airframe, a helicopter, a troop carrier and anything the
        // Air Command mission cannot steer all stay owned and idle here; the idle sweep sends them
        // home on the next review and the first sortie that wants them takes them. The AirGuard task
        // below is an AIR SUPERIORITY task, and this was one of the paths handing it out.
        if (aircraft.definition is AircraftDefinition owned
            && !CommanderEnemyCommanderService.MayHoldPatrol(owned))
        {
            return;
        }

        // Owned, no sortie wants it: the home CAP now, so the wing's standing air-defence posture
        // exists from the first minute of a match with no live objectives yet. A rotary transport
        // cannot take an Air Command task (TryTaskAiAircraft refuses anything without a plane
        // pilot) — the Basegame flies it, and it stays owned until a sortie or the player takes it.
        if (IssueAirTask(
                hq,
                state,
                aircraft,
                CommanderAirCommandService.AirCommandMode.AirGuard,
                HomeCAPCentre(hq),
                CommanderEnemyCommanderService.HomeGuardRadiusMeters))
        {
            CommanderAiLog.Note(
                hq, $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} on the home CAP until a sortie wants it.");
        }
    }

    /// <summary>The air step of every operations review (design SS1/SS2): prune the air book,
    /// derive this review's sortie demand in priority order, reconcile the live sorties against
    /// it, fill them from unbound owned airframes, and sync every bound airframe's task.</summary>
    private void PlanAirSupport(FactionHQ hq, OperationsState state)
    {
        PruneAirBook(hq, state);
        // Before the demand is built (design SS9): a recalled fighter's CAP slot has to read as open
        // this review, not next, or the sortie flies a review short of its escort for nothing.
        RecallLentHomeCap(hq, state);
        BuildAirDemand(hq, state, airDemand);
        ReconcileSorties(hq, state, airDemand);
        FillSorties(hq, state);
        UpdatePackages(hq, state);
        // After the packages are resolved (so a moved airframe is told the right thing at once) and
        // before the sync, which then agrees with the move rather than undoing it.
        RetaskToContact(hq, state);
        SyncAirTasks(hq, state);
        SweepIdleAirframes(hq, state);
    }

    // ---- Packages (design.md, smarter-air-wing_20260914 Section 3) ----

    /// <summary>
    /// Give every package its form-up point, count who has reached it, and decide whether it goes in
    /// this review. Runs after the fill so this review's new bindings are counted, and before the
    /// task sync so the sync writes the answer straight onto the airframes.
    /// </summary>
    private void UpdatePackages(FactionHQ hq, OperationsState state)
    {
        RefreshAradHolds(state);
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (!SortieIsPackage(sortie.Wanted, sortie.CapsWanted, sortie.NoCapWait))
            {
                // Single airframes, contact sorties, AWACS, ARAD and the CAP-only sorties all fly
                // straight to their station: there is nobody to form up with.
                sortie.HasFormUp = false;
                sortie.GoneIn = true;
                sortie.FormUpFirstArrivalAt = -1f;
                sortie.FormUpReported = -1;
                continue;
            }

            sortie.HasFormUp = TryFindFormUpPoint(hq, sortie.Center, out GlobalPosition formUp);
            sortie.FormUpPoint = formUp;
            if (!sortie.HasFormUp)
            {
                // No base to measure from: there is no friendly side to wait on, so the package is
                // whatever is airborne and it goes in.
                sortie.GoneIn = true;
                continue;
            }

            if (sortie.GoneIn)
            {
                continue; // reinforcements join at the objective, not at the form-up point
            }

            int casAtFormUp = CountAtFormUp(sortie.Cas, formUp);
            int capAtFormUp = CountAtFormUp(sortie.Caps, formUp);
            if (sortie.FormUpFirstArrivalAt < 0f && casAtFormUp + capAtFormUp > 0)
            {
                sortie.FormUpFirstArrivalAt = Time.time;
            }

            float waited = sortie.FormUpFirstArrivalAt < 0f ? -1f : Time.time - sortie.FormUpFirstArrivalAt;
            float timeout = CommanderSettings.PackageFormUpSeconds;
            if (!PackageGoesIn(
                    casAtFormUp, sortie.Wanted, capAtFormUp, sortie.CapsWanted, sortie.AradPending, waited, timeout))
            {
                if (sortie.FormUpReported != casAtFormUp)
                {
                    sortie.FormUpReported = casAtFormUp;
                    CommanderAiLog.Note(
                        hq,
                        $"{sortie.Label}: package forming {casAtFormUp}/{sortie.Wanted} at the form-up point"
                            + (sortie.AradPending ? ", waiting on its ARAD sortie." : "."));
                }

                continue;
            }

            sortie.GoneIn = true;
            bool complete = casAtFormUp >= sortie.Wanted && capAtFormUp >= sortie.CapsWanted;
            CommanderAiLog.Note(
                hq,
                complete
                    ? $"{sortie.Label}: package goes in ({casAtFormUp} airframes, escort up)."
                    : $"{sortie.Label}: package goes in after {timeout:0} s wait ({casAtFormUp}/{sortie.Wanted}).");
        }
    }

    /// <summary>
    /// An objective's package holds only while an ARAD sortie near it is genuinely still inbound
    /// (design SS5). The demand pass raises the flag the moment a belt is found; this clears it the
    /// moment the suppression airframe is over the belt, or the belt dissolves and its sortie is
    /// gone.
    /// </summary>
    private static void RefreshAradHolds(OperationsState state)
    {
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie objective = state.AirSorties[i];
            if (objective.Kind != CommanderSortieKind.Objective || !objective.AradPending)
            {
                continue;
            }

            bool inbound = false;
            for (int a = 0; a < state.AirSorties.Count; a++)
            {
                CommanderAirSortie arad = state.AirSorties[a];
                if (arad.Kind == CommanderSortieKind.Arad
                    && CommanderGameAccess.HorizontalDistance(arad.Center.AsVector3(), objective.Center.AsVector3())
                        <= ObservedRadiusMeters
                    && AradStillInbound(arad))
                {
                    inbound = true;
                    break;
                }
            }

            objective.AradPending = inbound;
        }
    }

    /// <summary>Whether this package is still holding at its form-up point — what the task sync and
    /// the binding read to decide where an airframe is sent.</summary>
    private static bool SortieHoldsAtFormUp(CommanderAirSortie sortie)
    {
        return sortie.HasFormUp
            && !sortie.GoneIn
            && SortieIsPackage(sortie.Wanted, sortie.CapsWanted, sortie.NoCapWait);
    }

    /// <summary>
    /// The package's orbit (design SS3): <see cref="PackageFormUpDistanceMeters"/> from the held
    /// base nearest the objective, along the line toward it, pulled back so it keeps
    /// <see cref="PreemptiveAirRangeMeters"/> of clear air ahead of the nearest tracked hostile —
    /// the same ring "near the enemy" means everywhere else in this service. False when the
    /// commander holds no base to measure from.
    /// </summary>
    private static bool TryFindFormUpPoint(FactionHQ hq, GlobalPosition objective, out GlobalPosition formUp)
    {
        formUp = default;
        Airbase? nearest = null;
        float best = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                airbase.center.GlobalPosition().AsVector3(), objective.AsVector3());
            if (distance < best)
            {
                best = distance;
                nearest = airbase;
            }
        }

        if (nearest == null)
        {
            return false;
        }

        GlobalPosition origin = nearest.center.GlobalPosition();
        float dx = objective.x - origin.x;
        float dz = objective.z - origin.z;
        float length = Mathf.Sqrt(dx * dx + dz * dz);
        if (length < 1f)
        {
            formUp = origin;
            return true;
        }

        float along = PackageFormUpDistance(
            length,
            PackageFormUpDistanceMeters,
            NearestTrackedHostileAlongLine(hq, origin, dx / length, dz / length, length),
            PreemptiveAirRangeMeters);
        formUp = new GlobalPosition(
            origin.x + dx / length * along,
            Mathf.Max(origin.y, objective.y),
            origin.z + dz / length * along);
        return true;
    }

    /// <summary>
    /// How far along the base-to-objective line the nearest tracked hostile sits, or -1 when nothing
    /// tracked lies ahead of the base on it. Only hostiles whose sideways offset is inside the
    /// stand-off ring count: one sitting 40 km off the flank is not on this route. Aircraft as well
    /// as ground units — a fighter sweep is exactly what a forming package must not orbit into.
    /// </summary>
    private static float NearestTrackedHostileAlongLine(
        FactionHQ hq, GlobalPosition origin, float dirX, float dirZ, float legMeters)
    {
        float best = -1f;
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > CommanderEnemyCommanderService.ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is Building
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq))
            {
                continue;
            }

            GlobalPosition at = info.lastKnownPosition;
            float ox = at.x - origin.x;
            float oz = at.z - origin.z;
            float along = ox * dirX + oz * dirZ;
            if (along <= 0f || along > legMeters)
            {
                continue;
            }

            float sideways = Mathf.Abs(ox * dirZ - oz * dirX);
            if (sideways > PreemptiveAirRangeMeters)
            {
                continue;
            }

            if (best < 0f || along < best)
            {
                best = along;
            }
        }

        return best;
    }

    // ---- Retask to contact (design.md, smarter-air-wing_20260914 Section 10) ----

    /// <summary>
    /// Move airframes off sorties that are covering quiet ground and onto sorties whose objective is
    /// being shot at right now (user decision 2026-09-14). Runs after the packages have been
    /// resolved, so a moved airframe is told the right thing straight away, and before the task
    /// sync, which then simply agrees with it.
    /// <para>The quiet sortie's own demand reopens by itself: it is short again on the next review
    /// and is refilled or bought the ordinary way.</para>
    /// </summary>
    private void RetaskToContact(FactionHQ hq, OperationsState state)
    {
        for (int t = 0; t < state.AirSorties.Count; t++)
        {
            CommanderAirSortie target = state.AirSorties[t];
            if (!target.InContact || Time.time < target.CooldownUntil)
            {
                continue;
            }

            while (target.Cas.Count < target.Wanted
                && TryRetaskOne(hq, state, target, asCap: false))
            {
            }

            while (target.Caps.Count < target.CapsWanted
                && TryRetaskOne(hq, state, target, asCap: true))
            {
            }
        }
    }

    /// <summary>
    /// Take one airframe from the best available quiet sortie and put it on
    /// <paramref name="target"/>. Best means the lowest <see cref="RetaskSourceRank"/>, and within
    /// one rank the airframe nearest the target. Returns false when nothing may be moved.
    /// </summary>
    private bool TryRetaskOne(FactionHQ hq, OperationsState state, CommanderAirSortie target, bool asCap)
    {
        CommanderEnemyCommanderService.AirRole role = asCap
            ? CommanderEnemyCommanderService.AirRole.Fighter
            : SortieAirRole(target);

        int alive = CountHomeCapFighters(hq);
        int lent = CountLentHomeCap(hq);
        CommanderAirSortie? bestSortie = null;
        Aircraft? best = null;
        int bestRank = int.MaxValue;
        float bestDistance = float.MaxValue;

        for (int s = 0; s < state.AirSorties.Count; s++)
        {
            CommanderAirSortie source = state.AirSorties[s];
            if (ReferenceEquals(source, target)
                // Never the radar airframe, and never a suppression sortie that has gone in: both
                // are doing a job nothing else on the roster can do.
                || source.Kind == CommanderSortieKind.Awacs
                || (source.Kind == CommanderSortieKind.Arad && source.GoneIn))
            {
                continue;
            }

            int rank = RetaskSourceRank(source.InContact, source.Preemptive, SortieHoldsAtFormUp(source));
            if (rank < 0 || rank > bestRank)
            {
                continue;
            }

            List<Aircraft> bound = asCap ? source.Caps : source.Cas;
            for (int i = 0; i < bound.Count; i++)
            {
                Aircraft aircraft = bound[i];
                if (!MayTakeForContact(hq, state, aircraft, role, alive, lent))
                {
                    continue;
                }

                float distance = CommanderGameAccess.HorizontalDistance(
                    aircraft.transform.GlobalPosition().AsVector3(), target.Center.AsVector3());
                if (rank < bestRank || distance < bestDistance)
                {
                    bestRank = rank;
                    bestDistance = distance;
                    best = aircraft;
                    bestSortie = source;
                }
            }
        }

        if (best == null || bestSortie == null)
        {
            return false;
        }

        (asCap ? bestSortie.Caps : bestSortie.Cas).Remove(best);
        (asCap ? target.Caps : target.Cas).Add(best);
        state.AirRetaskedAt[best] = Time.time;
        target.RetaskedThisReview = true;
        TaskOntoSortie(hq, state, target, best, asCap);
        CommanderAiLog.Note(
            hq,
            $"retasks {CommanderGameAccess.GetUnitLabel(best)} from {bestSortie.Label} to {target.Label}: "
                + "contact outranks cover.");
        return true;
    }

    /// <summary>Whether this bound airframe may be taken off its sortie for one in contact: alive,
    /// able to do the job, out of its own retask hold, and — if it is a home-CAP fighter on loan —
    /// not the one keeping the base at its minimum.</summary>
    private static bool MayTakeForContact(
        FactionHQ hq,
        OperationsState state,
        Aircraft aircraft,
        CommanderEnemyCommanderService.AirRole role,
        int homeCapAlive,
        int homeCapLent)
    {
        if (aircraft == null
            || aircraft.disabled
            || aircraft.definition is not AircraftDefinition definition
            || !CommanderEnemyCommanderService.FillsAirRole(hq, definition, role))
        {
            return false;
        }

        if (state.LentHomeCap.Contains(aircraft)
            && !MayMoveHomeCapFighter(homeCapAlive, homeCapLent, HomeCapMinimumHeld))
        {
            return false;
        }

        if (IsUnderOtherService(state, aircraft))
        {
            return false; // Section 17
        }

        return MayRetask(
            state.AirRetaskedAt.TryGetValue(aircraft, out float at) ? Time.time - at : -1f,
            RetaskHoldSeconds);
    }

    /// <summary>The one door for "put this airframe on this sortie now": the form-up orbit while the
    /// package is still forming, the objective once it is not. The same choice
    /// <see cref="SyncAirTasks"/> makes, so the sync agrees with the move instead of undoing it.</summary>
    private static void TaskOntoSortie(
        FactionHQ hq, OperationsState state, CommanderAirSortie sortie, Aircraft aircraft, bool asCap)
    {
        bool holdAtFormUp = SortieHoldsAtFormUp(sortie);
        IssueAirTask(
            hq,
            state,
            aircraft,
            HoldingMode(sortie, asCap),
            holdAtFormUp ? sortie.FormUpPoint : sortie.Center,
            holdAtFormUp ? PackageArrivalMeters : (asCap ? CasSortieRadiusMeters : SortieRadius(sortie)));
    }

    /// <summary>Why an airframe was sent home instead of put on the standing patrol, in the words a
    /// reader can act on: the log line has to say which rule fired, or a transport going home and a
    /// bomber going home look like the same event.</summary>
    private static string DescribePatrolRefusal(AircraftDefinition? definition)
    {
        if (definition == null)
        {
            return "an airframe with no definition";
        }

        if (CommanderEnemyCommanderService.GetAirRole(definition) == CommanderEnemyCommanderService.AirRole.Transport)
        {
            return "a transport";
        }

        if (!CommanderAirCommandService.HasPlanePilot(definition))
        {
            return "an airframe the wing cannot task";
        }

        return CommanderAirCommandService.IsRotaryAirframe(definition)
            ? "a helicopter"
            : "a ground-attack airframe";
    }

    /// <summary>
    /// The operations side of the air-superiority refusal (user report, 2026-09-14: "seeing a lot
    /// of air superiority brawlers - SHOULDN'T BE, they're CAS aircraft"). The refusal itself is one
    /// rule in the commander service and every binding path reads it through the capability gate;
    /// what is checked HERE is the two things this file decides on its own — which mode a sortie's
    /// airframes carry while the package forms up, and that a ground-attack airframe is routed home
    /// by the idle sweep rather than onto the patrol.
    /// </summary>
    private static void CheckAirSuperiorityRefusal(List<string> failures)
    {
        CommanderAirSortie objective = new() { Kind = CommanderSortieKind.Objective };
        CommanderAirSortie arad = new() { Kind = CommanderSortieKind.Arad };
        Expect(
            failures,
            "a CAS airframe holding at the form-up point keeps its CAS mode, never AIR SUPERIORITY",
            HoldingMode(objective, asCap: false).ToString(),
            CommanderAirCommandService.AirCommandMode.Cas.ToString());
        Expect(
            failures,
            "an ARAD airframe holding at the form-up point keeps its ARAD mode",
            HoldingMode(arad, asCap: false).ToString(),
            CommanderAirCommandService.AirCommandMode.Arad.ToString());
        Expect(
            failures,
            "the escort holds on AIR SUPERIORITY, which is its job",
            HoldingMode(objective, asCap: true).ToString(),
            CommanderAirCommandService.AirCommandMode.AirGuard.ToString());

        // The idle sweep's routing, on the two airframes the user named. The sweep asks exactly this
        // question: an airframe that may not fly air superiority is sent home, never posted to the
        // standing patrol.
        AircraftDefinition brawler = ScriptableObject.CreateInstance<AircraftDefinition>();
        brawler.jsonKey = "CAS1";
        brawler.roleIdentity.antiAir = 0.30f;
        brawler.roleIdentity.antiSurface = 0.80f;
        AircraftDefinition revoker = ScriptableObject.CreateInstance<AircraftDefinition>();
        revoker.jsonKey = "Fighter1";
        revoker.roleIdentity.antiAir = 1.00f;
        revoker.roleIdentity.antiSurface = 0.46f;
        Expect(
            failures,
            "an idle A-19 Brawler is sent home, never posted to the home CAP",
            CommanderEnemyCommanderService.MayFlyAirSuperiority(brawler),
            false);
        Expect(
            failures,
            "an idle FS-12 Revoker still takes the home CAP",
            CommanderEnemyCommanderService.MayFlyAirSuperiority(revoker),
            true);
        Expect(
            failures,
            "an owned A-19 Brawler is not a candidate for a sortie's CAP or escort slot",
            CommanderEnemyCommanderService.ForRole(brawler, CommanderEnemyCommanderService.AirRole.Fighter)
                == CommanderEnemyCommanderService.AirframeTier.Strike,
            true);

        // The transport the user watched get bound to a platoon's patrol (2026-09-14). Its ratings
        // put it in the strike tier by arithmetic, so only the structural exclusion keeps it off.
        AircraftDefinition ibis = ScriptableObject.CreateInstance<AircraftDefinition>();
        ibis.jsonKey = "UtilityHelo1";
        ibis.captureCapacity = 8;
        ibis.roleIdentity.antiAir = 0.27f;
        ibis.roleIdentity.antiSurface = 0.90f;
        Expect(
            failures,
            "a UH-90 Ibis is refused the home CAP, the claim's fallback and the idle sweep's posture",
            CommanderEnemyCommanderService.MayHoldPatrol(ibis),
            false);
        Expect(
            failures,
            "a UH-90 Ibis is refused a platoon's CAP sortie slot as well",
            CommanderEnemyCommanderService.MayFillRole(ibis, CommanderEnemyCommanderService.AirRole.Fighter),
            false);
        Expect(
            failures,
            "a UH-90 Ibis is refused a CAS slot too, so it is not bound to a sortie at all",
            CommanderEnemyCommanderService.MayFillRole(ibis, CommanderEnemyCommanderService.AirRole.Strike),
            false);
        Expect(
            failures,
            "the idle sweep says a transport went home BECAUSE it is a transport",
            DescribePatrolRefusal(ibis),
            "a transport");
        Expect(
            failures,
            "the idle sweep names a ground-attack airframe for what it is",
            DescribePatrolRefusal(brawler),
            "an airframe the wing cannot task");
        Object.Destroy(ibis);
        Object.Destroy(brawler);
        Object.Destroy(revoker);
    }

    /// <summary>
    /// The Air Command mode an airframe on this sortie carries — including while the package is
    /// still forming up at its form-up point.
    /// <para>
    /// The escort's mode is AIR SUPERIORITY because that is its job. The CAS airframes keep their
    /// own CAS mode throughout, form-up hold included. They used to be put on AIR SUPERIORITY for
    /// the hold, purely because it was the mode that orbits a point without hunting ground targets —
    /// and the result was an A-19 Brawler sitting at a form-up point with <c>AIR SUPERIORITY</c>
    /// over it for the whole wait, on every package, which is what the user was looking at when they
    /// reported "seeing a lot of air superiority brawlers - SHOULDN'T BE, they're CAS aircraft"
    /// (2026-09-14). The position and the radius of the hold are unchanged; only the mode is, so the
    /// package geometry, the bounded wait and the go-in test all behave exactly as before.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static CommanderAirCommandService.AirCommandMode HoldingMode(CommanderAirSortie sortie, bool asCap)
    {
        return asCap ? CommanderAirCommandService.AirCommandMode.AirGuard : SortieCasMode(sortie);
    }

    // ---- Idle sweep (design.md, smarter-air-wing_20260914 Section 6) ----

    private readonly List<Aircraft> idleSweep = new();

    /// <summary>Airframes adopted by this review's sweep, so their one log line says they were
    /// reclaimed rather than that they were idle.</summary>
    private readonly HashSet<Aircraft> adoptedStrays = new();

    /// <summary>
    /// Take back the airframes a hot reload orphaned (design SS15). A reload rebuilds this service
    /// from nothing: the ownership set is empty and the Air Command mission table only restores the
    /// player's own recipe-launched missions, so everything the commander had bought is left in the
    /// sky owned by nobody and told nothing. They are put back into the owned set here, and the
    /// sweep below then tiers and tasks them exactly like any other idle airframe of ours.
    /// </summary>
    private void AdoptStrayAircraft(FactionHQ hq, OperationsState state, CommanderAirCommandService airCommand)
    {
        adoptedStrays.Clear();
        // The registration stamps outlive their aeroplanes otherwise: the set is written from a
        // Harmony postfix that has no matching "unregistered" hook, so it is pruned here.
        state.RegisteredWhileCommanded.RemoveWhere(static seen => seen == null || seen.disabled);
        if (hq.factionUnits == null)
        {
            return;
        }

        bool duel = CommanderEnemyCommanderService.IsDuelMission;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit is not Aircraft aircraft || unit.disabled)
            {
                continue;
            }

            if (IsUnderOtherService(state, aircraft))
            {
                ReportOtherService(hq, state, aircraft);
                continue;
            }

            if (!AdoptsStrayAircraft(
                    hasHumanPilot: aircraft.Player != null,
                    hasMission: airCommand.IsOnAnyMission(aircraft),
                    commanderOwned: state.CommanderAirframes.Contains(aircraft),
                    aiFlyable: aircraft.definition is AircraftDefinition definition
                        && CommanderAirCommandService.CanAiFly(definition),
                    duel,
                    registeredWhileCommanded: state.RegisteredWhileCommanded.Contains(aircraft)))
            {
                continue;
            }

            state.CommanderAirframes.Add(aircraft);
            adoptedStrays.Add(aircraft);
            // Its one line is printed by the sweep below, which knows what it was tasked with.
            state.IdleSweepReported.Remove(aircraft);
        }
    }

    /// <summary>
    /// No commander-owned airframe idles without a mission. Every review, anything this commander
    /// bought that carries no Air Command mission at all is given one: an airframe that can fight
    /// air joins the home CAP, a transport or one that has shot everything off its racks goes home,
    /// and everything else joins the home CAP too. One line per airframe — the AIR window's "Idle"
    /// list is what the user watched fill up, and the line is what says which airframe left it.
    /// <para>Never touches a player-ordered airframe: an aircraft the player has taken is no longer
    /// in <c>CommanderAirframes</c> (the hands-off release removes it), and one already carrying any
    /// mission is skipped here by definition.</para>
    /// </summary>
    private void SweepIdleAirframes(FactionHQ hq, OperationsState state)
    {
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null)
        {
            return;
        }

        AdoptStrayAircraft(hq, state, airCommand);

        idleSweep.Clear();
        foreach (Aircraft owned in state.CommanderAirframes)
        {
            if (owned == null || owned.disabled)
            {
                continue;
            }

            if (airCommand.IsOnAnyMission(owned))
            {
                // Back under orders: the next time it falls idle is news again.
                state.IdleSweepReported.Remove(owned);
                continue;
            }

            // Section 17: a supply run and a picket insertion are flown by another service, and
            // neither carries an Air Command mission — so an insertion halfway to its landing zone
            // read as idle and was ordered home.
            if (IsUnderOtherService(state, owned))
            {
                ReportOtherService(hq, state, owned);
                continue;
            }

            idleSweep.Add(owned);
        }

        for (int i = 0; i < idleSweep.Count; i++)
        {
            Aircraft aircraft = idleSweep[i];
            AircraftDefinition? definition = aircraft.definition as AircraftDefinition;
            // An aeroplane adopted this review says so instead of saying it was idle: it was never
            // idle, it was ours all along and the reload lost the paperwork.
            string lead = adoptedStrays.Contains(aircraft)
                ? $"adopts {CommanderGameAccess.GetUnitLabel(aircraft)} (stray after reload); tasked"
                : $"{CommanderGameAccess.GetUnitLabel(aircraft)} was idle;";
            bool transport = definition != null
                && CommanderEnemyCommanderService.GetAirRole(definition) == CommanderEnemyCommanderService.AirRole.Transport;
            bool winchester = CommanderAirCommandService.IsWinchester(aircraft);

            // An airframe with no mission carries no RTB record either, so the landing order is
            // re-issued every review — the rotary landing state hands itself back to combat whenever
            // the pad it wanted is busy (see RotaryBouncedBackToCombat). The LINE is printed once.
            if ((transport || winchester) && CommanderAirCommandService.TryReturnAiAircraftHome(aircraft))
            {
                if (state.IdleSweepReported.Add(aircraft))
                {
                    CommanderAiLog.Note(
                        hq,
                        $"{lead} sent home "
                            + $"({(transport ? "a transport with nothing to deliver" : "nothing left on its racks")}).");
                }

                continue;
            }

            // A ground-attack specialist NEVER holds the patrol (user report, 2026-09-14: "seeing a
            // lot of air superiority brawlers - SHOULDN'T BE, they're CAS aircraft"). The posture
            // task below is an AIR SUPERIORITY task, so an idle strike-tier airframe goes home to
            // its pad instead — where it is ready for the next CAS sortie rather than burning fuel
            // over friendly ground waiting for a fight it cannot win. It is never handed the patrol
            // as a fallback: an airframe that cannot RTB this review is simply left for the next
            // sweep, because a Brawler on CAP is the bug, not the safety net.
            bool mayPatrol = definition != null
                && CommanderEnemyCommanderService.MayHoldPatrol(definition);
            if (!mayPatrol)
            {
                if (CommanderAirCommandService.TryReturnAiAircraftHome(aircraft)
                    && state.IdleSweepReported.Add(aircraft))
                {
                    CommanderAiLog.Note(
                        hq,
                        $"{lead} sent home "
                            + $"({DescribePatrolRefusal(definition)} is never put on the home CAP).");
                }

                continue;
            }

            if (IssuePostureTask(hq, aircraft) && state.IdleSweepReported.Add(aircraft))
            {
                CommanderAiLog.Note(hq, $"{lead} on the home CAP.");
            }
        }

        idleSweep.Clear();
    }

    /// <summary>The sweep run from the enemy commander's own review as well as the operations one
    /// (Reuse rule 4: the residual posture loop in <c>TaskAirWing</c> used to duplicate it, one log
    /// line poorer). Does nothing for an HQ this service does not manage.</summary>
    internal static void SweepIdleAirframes(FactionHQ hq)
    {
        if (Instance != null && Instance.states.TryGetValue(hq, out OperationsState state))
        {
            Instance.SweepIdleAirframes(hq, state);
        }
    }

    private static int CountAtFormUp(List<Aircraft> bound, GlobalPosition formUp)
    {
        int count = 0;
        for (int i = 0; i < bound.Count; i++)
        {
            Aircraft aircraft = bound[i];
            if (aircraft != null
                && !aircraft.disabled
                && CommanderGameAccess.HorizontalDistance(
                    aircraft.transform.GlobalPosition().AsVector3(), formUp.AsVector3()) <= PackageArrivalMeters)
            {
                count++;
            }
        }

        return count;
    }

    private readonly List<CommanderAirSortie> airDemand = new();
    private readonly List<Aircraft> airStale = new();

    /// <summary>The review's sortie list in priority order (design SS1): attack targets whose
    /// groups have begun arriving (Approval 1 — the demand opens at first release-point arrival),
    /// then platoons in contact — marching, or holding their posts under attack (addendum
    /// 2026-09-14 §2) — then pickets and forward bases under attack with no platoon on them, then
    /// platoons marching near the enemy but not yet in contact (pre-emptive, user decision
    /// 2026-09-14), then threatened forward bases in ranked order. Quiet rear points and the
    /// reserve ask for nothing.</summary>
    private void BuildAirDemand(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        demand.Clear();

        // The AWACS first (design SS4): everything below it is sized from the tracking picture, and
        // the radar airframe is what fills that picture in.
        AddAwacsDemand(hq, state, demand);

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind != CommanderMissionKind.Attack || mission.FirstGroupArrivedAt < 0f)
            {
                continue;
            }

            AddDemand(hq, state, demand, mission, null, AttackCenter(mission), mission.Label, immediate: false);
        }

        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if ((platoon.State != CommanderPlatoonState.Moving && platoon.State != CommanderPlatoonState.Attacking
                    && platoon.State != CommanderPlatoonState.Holding)
                || platoon.InContactUntil < Time.time)
            {
                continue;
            }

            // Addendum 2026-09-14 §2: a platoon Holding its posts (a forward base garrison or the
            // reserve ring) that comes under attack is in contact without leaving them. Its
            // contact sortie is keyed to the mission as well as the platoon, so the
            // threatened-forward-base loop below does not open a second wing over the same ring,
            // and a second garrison platoon of the same mission joins the one sortie already
            // demanded for it instead of opening its own.
            CommanderOperationsMission? holdingMission =
                platoon.State == CommanderPlatoonState.Holding ? platoon.Mission : null;
            if (holdingMission != null && AlreadyDemanded(demand, holdingMission))
            {
                continue;
            }

            // Over the last tracked contact, not over the platoon: the CAS area filter picks its
            // targets out of its own ring, so the box has to sit on the enemy, not on the line
            // facing it.
            AddDemand(
                hq, state, demand, holdingMission, platoon, platoon.ContactBearingAnchor, platoon.Name, immediate: true);
        }

        // Addendum 2026-09-14 §2: a picket or forward base under attack with no platoon on it
        // carries its own contact clock (DetectMissionContact), so the point itself raises CAS at
        // contact priority — a picket's two vehicles are a detachment, not a platoon, and would
        // otherwise never open a sortie however hard they were hit.
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if ((mission.Kind != CommanderMissionKind.Picket && mission.Kind != CommanderMissionKind.ForwardBase)
                || mission.Point == null
                || mission.ContactUntil < Time.time
                || AlreadyDemanded(demand, mission))
            {
                continue;
            }

            AddDemand(hq, state, demand, mission, null, mission.Point.Position, mission.Label, immediate: true);
        }

        AddPreemptiveDemand(hq, state, demand);

        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            if (!ranked.HasThreatMark)
            {
                continue;
            }

            CommanderOperationsMission? mission = FindForwardBaseFor(state, ranked.Point);
            if (mission != null && !AlreadyDemanded(demand, mission))
            {
                AddDemand(hq, state, demand, mission, null, ranked.Point.Position, mission.Label, immediate: false);
            }
        }

        // Suppression and the platoon-requested CAP both read the objective list above, so they are
        // derived from it rather than woven into it.
        InsertAradDemand(hq, demand);
        AddPlatoonCapDemand(hq, state, demand);
    }

    /// <summary>
    /// Pre-emptive sorties (user decision 2026-09-14): every platoon under way — Moving or
    /// Attacking — whose leader or objective is inside <see cref="PreemptiveAirRangeMeters"/> of an
    /// enemy-held point or base or a tracked hostile gets a sortie over it BEFORE it is in contact,
    /// with a baseline airframe of each even at zero observed. A platoon already in contact was
    /// served by the loop above and is skipped here, so one platoon never opens two sorties.
    /// <para>The demand outlives the platoon leaving the ring by
    /// <see cref="PreemptiveAirHoldSeconds"/> — the hysteresis that stops a sortie being dissolved
    /// and re-bought review after review as a platoon skirts the ring — but ends at once when the
    /// platoon stops marching.</para>
    /// </summary>
    private void AddPreemptiveDemand(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (platoon.State != CommanderPlatoonState.Moving && platoon.State != CommanderPlatoonState.Attacking)
            {
                // Formed up, dug in or falling back: the pre-emptive reason is gone, and the hold
                // does not apply to it — the hold covers a moving platoon crossing the ring's edge.
                platoon.PreemptiveAirUntil = -1f;
                continue;
            }

            GlobalPosition center = PlatoonAirCenter(platoon);
            float distance = PreemptiveEnemyDistance(hq, platoon, center);
            if (WantsPreemptiveAir(platoon.State, distance, PreemptiveAirRangeMeters))
            {
                platoon.PreemptiveAirUntil = Time.time + PreemptiveAirHoldSeconds;
            }

            // A platoon in contact was served by the loop above, and one whose own mission already
            // opened a sortie (its attack's groups have reached their release points) is already
            // covered — the pre-emptive rule exists for the march that has no sortie over it yet,
            // not to task the wing twice over one fight.
            if (platoon.PreemptiveAirUntil < Time.time
                || platoon.InContactUntil >= Time.time
                || AlreadyDemanded(demand, platoon.Mission))
            {
                continue;
            }

            AddDemand(
                hq, state, demand, null, platoon, center, platoon.Name,
                immediate: false, preemptive: true, enemyDistanceMeters: distance);
        }
    }

    // ---- Platoon-requested CAP (design.md addendum 2026-09-14, Section 7) ----

    /// <summary>
    /// The counterpart to the home CAP's ceiling: a platoon under way or holding its posts, and a
    /// forward base or picket point, that has tracked hostile AIRCRAFT over it gets fighters of its
    /// own — even when nothing else makes it an active objective. The home CAP stops at four by
    /// decision; this is where the rest of the answer to enemy air comes from.
    /// <para>Anything that already has a sortie over it is skipped: its own CAP wing grows with the
    /// tracked hostiles by the ordinary ladder, so a second sortie would double-count the raid.</para>
    /// </summary>
    private void AddPlatoonCapDemand(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (platoon.State != CommanderPlatoonState.Moving
                && platoon.State != CommanderPlatoonState.Attacking
                && platoon.State != CommanderPlatoonState.Holding)
            {
                continue;
            }

            if (AlreadyServed(demand, platoon.Mission, platoon))
            {
                continue;
            }

            AddCapDemand(hq, demand, PlatoonAirCenter(platoon), platoon.Name, null, platoon);
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if ((mission.Kind != CommanderMissionKind.ForwardBase && mission.Kind != CommanderMissionKind.Picket)
                || mission.Point == null
                || AlreadyServed(demand, mission, null))
            {
                continue;
            }

            AddCapDemand(hq, demand, mission.Point.Position, mission.Label, mission, null);
        }
    }

    /// <summary>Whether this review's demand already has any sortie of any kind over this mission or
    /// this platoon — the platoon CAP only opens where nothing is flying yet.</summary>
    private static bool AlreadyServed(
        List<CommanderAirSortie> demand, CommanderOperationsMission? mission, CommanderPlatoon? platoon)
    {
        for (int i = 0; i < demand.Count; i++)
        {
            CommanderAirSortie sortie = demand[i];
            if ((mission != null && ReferenceEquals(sortie.Mission, mission))
                || (platoon != null && ReferenceEquals(sortie.ContactPlatoon, platoon)))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddCapDemand(
        FactionHQ hq,
        List<CommanderAirSortie> demand,
        GlobalPosition center,
        string label,
        CommanderOperationsMission? mission,
        CommanderPlatoon? platoon)
    {
        int hostileAir = CountHostileAirInRing(hq, center, ObservedRadiusMeters);
        if (hostileAir <= 0)
        {
            return;
        }

        demand.Add(new CommanderAirSortie
        {
            Kind = CommanderSortieKind.Cap,
            Mission = mission,
            ContactPlatoon = platoon,
            Center = center,
            Wanted = 0,
            CapsWanted = PlatoonCapWanted(hostileAir),
            LastHostileAir = hostileAir,
            NoCapWait = true,
            // It only opened because hostile aircraft are overhead, so it is in contact by
            // construction: a platoon CAP is a target for a retask and never a source.
            InContact = true,
            Label = label,
        });
    }

    // ---- ARAD (design.md, smarter-air-wing_20260914 Section 5) ----

    private readonly List<GlobalPosition> aradEmitters = new();
    private readonly List<int> aradClusterOf = new();
    private readonly List<CommanderAirSortie> aradDemand = new();

    /// <summary>
    /// Cluster this commander's tracked hostile air-defence vehicles and open an anti-radiation
    /// sortie over any belt of <c>AradClusterMinimum</c> or more that sits near an objective the
    /// wing is already flying against. Inserted directly after the AWACS so the demand queue, the
    /// fill and the buy all agree that suppression comes before the CAS it is clearing the way for.
    /// <para>The objective's own package is marked to wait at its form-up point until the ARAD
    /// sortie has gone in — bounded, like every other reason a package holds.</para>
    /// </summary>
    private void InsertAradDemand(FactionHQ hq, List<CommanderAirSortie> demand)
    {
        aradDemand.Clear();
        CollectTrackedAirDefence(hq, aradEmitters);
        if (aradEmitters.Count < CommanderSettings.AradClusterMinimum)
        {
            return;
        }

        int clusterCount = BuildAirDefenceClusters(aradEmitters, aradClusterOf);
        for (int cluster = 0; cluster < clusterCount; cluster++)
        {
            int size = 0;
            float sumX = 0f;
            float sumY = 0f;
            float sumZ = 0f;
            for (int i = 0; i < aradEmitters.Count; i++)
            {
                if (aradClusterOf[i] != cluster)
                {
                    continue;
                }

                size++;
                sumX += aradEmitters[i].x;
                sumY += aradEmitters[i].y;
                sumZ += aradEmitters[i].z;
            }

            int wanted = AradWanted(size, CommanderSettings.AradClusterMinimum);
            if (wanted <= 0)
            {
                continue;
            }

            GlobalPosition centroid = new(sumX / size, sumY / size, sumZ / size);
            CommanderAirSortie? served = null;
            for (int d = 0; d < demand.Count; d++)
            {
                CommanderAirSortie objective = demand[d];
                if (objective.Kind == CommanderSortieKind.Objective
                    && CommanderGameAccess.HorizontalDistance(objective.Center.AsVector3(), centroid.AsVector3())
                        <= ObservedRadiusMeters)
                {
                    served = objective;
                    break;
                }
            }

            if (served == null)
            {
                continue; // a belt nobody is attacking is not this wing's problem
            }

            served.AradPending = true;
            aradDemand.Add(new CommanderAirSortie
            {
                Kind = CommanderSortieKind.Arad,
                Mission = served.Mission,
                Center = centroid,
                Wanted = wanted,
                CapsWanted = 0,
                LastObserved = size,
                NoCapWait = true,
                // A belt that has been seen is contact by any reading, so a suppression sortie is
                // never a source for a retask.
                InContact = true,
                // The objective's own label: the "ARAD" word belongs to the line and to the review
                // segment, not to the name, or both read "ARAD ARAD Hilltop 12".
                Label = served.Label,
            });
        }

        if (aradDemand.Count == 0)
        {
            return;
        }

        // Straight after the AWACS entry, which is always first when it exists. The "opens ARAD"
        // line is printed by the reconcile, which is what can tell a new belt from an old one.
        int insertAt = demand.Count > 0 && demand[0].Kind == CommanderSortieKind.Awacs ? 1 : 0;
        demand.InsertRange(insertAt, aradDemand);
    }

    /// <summary>Last known positions of every tracked hostile air-defence vehicle — the
    /// <c>CountObserved</c> walk with the buyer's air-defence role test, gathering positions instead
    /// of counting inside one ring.</summary>
    private static void CollectTrackedAirDefence(FactionHQ hq, List<GlobalPosition> into)
    {
        into.Clear();
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > CommanderEnemyCommanderService.ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not GroundVehicle
                || unit is Building
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq)
                || unit.definition is not VehicleDefinition definition
                || !CommanderEnemyCommanderService.IsAirDefence(definition))
            {
                continue;
            }

            into.Add(info.lastKnownPosition);
        }
    }

    /// <summary>
    /// Single-link clustering at <see cref="AradClusterLinkMeters"/>: two emitters within the link
    /// distance are in the same belt, and a belt is whatever that relation connects. Writes each
    /// emitter's cluster index into <paramref name="clusterOf"/> and returns how many clusters there
    /// are. A flood fill rather than anything cleverer — a commander tracks tens of vehicles, not
    /// thousands.
    /// </summary>
    private static int BuildAirDefenceClusters(List<GlobalPosition> emitters, List<int> clusterOf)
    {
        clusterOf.Clear();
        for (int i = 0; i < emitters.Count; i++)
        {
            clusterOf.Add(-1);
        }

        int clusters = 0;
        for (int seed = 0; seed < emitters.Count; seed++)
        {
            if (clusterOf[seed] >= 0)
            {
                continue;
            }

            clusterOf[seed] = clusters;
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int i = 0; i < emitters.Count; i++)
                {
                    if (clusterOf[i] != clusters)
                    {
                        continue;
                    }

                    for (int j = 0; j < emitters.Count; j++)
                    {
                        if (clusterOf[j] >= 0)
                        {
                            continue;
                        }

                        if (CommanderGameAccess.HorizontalDistance(
                                emitters[i].AsVector3(), emitters[j].AsVector3()) <= AradClusterLinkMeters)
                        {
                            clusterOf[j] = clusters;
                            grew = true;
                        }
                    }
                }
            }

            clusters++;
        }

        return clusters;
    }

    /// <summary>Whether an ARAD sortie is still on its way in — what holds the CAS package for the
    /// same objective (design SS5). Gone in means the package released AND an airframe is over the
    /// belt; until then the CAS waits, bounded by the package clock.</summary>
    private static bool AradStillInbound(CommanderAirSortie sortie)
    {
        if (!sortie.GoneIn)
        {
            return true;
        }

        for (int i = 0; i < sortie.Cas.Count; i++)
        {
            Aircraft aircraft = sortie.Cas[i];
            if (aircraft != null
                && !aircraft.disabled
                && CommanderGameAccess.HorizontalDistance(
                    aircraft.transform.GlobalPosition().AsVector3(), sortie.Center.AsVector3()) <= CasSortieRadiusMeters)
            {
                return false;
            }
        }

        return true;
    }

    // ---- AWACS (design.md, smarter-air-wing_20260914 Section 4) ----

    /// <summary>
    /// One radar airframe per commander, on station behind the front. Opened whenever the roster
    /// holds an airframe the commander's own builder can give the game's radar pod to and a held
    /// strip will launch — otherwise the sortie would stand unfillable for the whole match and hold
    /// a slot in the demand queue ahead of the CAS that could have used the money.
    /// </summary>
    private void AddAwacsDemand(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        if (!CommanderEnemyCommanderService.HasRoleCandidate(hq, CommanderEnemyCommanderService.AirRole.Awacs))
        {
            return;
        }

        GlobalPosition station = AwacsStation(hq, state, out bool hasFront);
        demand.Add(new CommanderAirSortie
        {
            Kind = CommanderSortieKind.Awacs,
            Center = station,
            Wanted = 1,
            CapsWanted = 0,
            // The AWACS goes where it is told the moment it is airborne; there is nothing to form
            // up with and nothing to wait for.
            NoCapWait = true,
            Label = hasFront
                ? $"AWACS {AwacsFrontStandoffMeters / 1000f:0} km behind the front"
                : "AWACS over the main base",
        });
    }

    /// <summary>
    /// Where the AWACS orbits (design SS4): <see cref="AwacsFrontStandoffMeters"/> back from the
    /// centre of the front along the line toward the commander's main base, or the main base itself
    /// while no front exists yet. The front's centre is the mean of the front control points and the
    /// forward bases — the same two things the ground plan calls the front.
    /// </summary>
    private static GlobalPosition AwacsStation(FactionHQ hq, OperationsState state, out bool hasFront)
    {
        GlobalPosition home = MainBasePosition(hq);
        float sumX = 0f;
        float sumY = 0f;
        float sumZ = 0f;
        int count = 0;
        for (int i = 0; i < state.RankedPoints.Count; i++)
        {
            CommanderRankedPoint ranked = state.RankedPoints[i];
            if (!ranked.IsFront)
            {
                continue;
            }

            sumX += ranked.Point.Position.x;
            sumY += ranked.Point.Position.y;
            sumZ += ranked.Point.Position.z;
            count++;
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind != CommanderMissionKind.ForwardBase || mission.Point == null)
            {
                continue;
            }

            sumX += mission.Point.Position.x;
            sumY += mission.Point.Position.y;
            sumZ += mission.Point.Position.z;
            count++;
        }

        hasFront = count > 0;
        if (!hasFront)
        {
            return home;
        }

        GlobalPosition centre = new(sumX / count, sumY / count, sumZ / count);
        float dx = home.x - centre.x;
        float dz = home.z - centre.z;
        float length = Mathf.Sqrt(dx * dx + dz * dz);
        float standoff = AwacsStandoffMeters(hasFront: true, length);
        if (length < 1f)
        {
            return centre;
        }

        return new GlobalPosition(
            centre.x + dx / length * standoff,
            Mathf.Max(centre.y, home.y),
            centre.z + dz / length * standoff);
    }

    /// <summary>The commander's main base: the airbase it holds nearest its own territory centre —
    /// the aggregate every other "home ground" read in this service already uses (Reuse rule 4).
    /// Falls back to that centre when it holds no airbase at all.</summary>
    private static GlobalPosition MainBasePosition(FactionHQ hq)
    {
        GlobalPosition centre = HomeCAPCentre(hq);
        Airbase? best = null;
        float bestDistance = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                airbase.center.GlobalPosition().AsVector3(), centre.AsVector3());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = airbase;
            }
        }

        return best != null ? best.center.GlobalPosition() : centre;
    }

    /// <summary>Whether this review's demand already holds a sortie for <paramref name="mission"/>.
    /// Null (a platoon between missions) is never already demanded.</summary>
    private static bool AlreadyDemanded(List<CommanderAirSortie> demand, CommanderOperationsMission? mission)
    {
        if (mission == null)
        {
            return false;
        }

        for (int i = 0; i < demand.Count; i++)
        {
            // Objective sorties only: the ARAD and CAP sorties over the same mission are its
            // companions, not the CAS wing it is asking for.
            if (demand[i].Kind == CommanderSortieKind.Objective && ReferenceEquals(demand[i].Mission, mission))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Where a sortie over a platoon sits: the platoon's leader — the moving centre the
    /// 3 km retarget hysteresis (<see cref="CasRetargetMeters"/>) already smooths — or its
    /// objective once the leader is gone.</summary>
    private static GlobalPosition PlatoonAirCenter(CommanderPlatoon platoon)
    {
        return platoon.Leader != null && !platoon.Leader.disabled
            ? platoon.Leader.transform.GlobalPosition()
            : platoon.Objective;
    }

    /// <summary>How near the enemy this platoon is: the shortest distance from either its leader or
    /// its objective to the nearest enemy-held point or base
    /// (<see cref="NearestEnemyAssetDistance"/> — base points carry every airbase and resolve their
    /// owner live, so that one walk answers both) or to the last known position of a tracked
    /// hostile ground unit. The objective counts as well as the leader: a platoon ordered at a
    /// point deep in enemy ground is marching into the fight whether or not it has arrived.</summary>
    private static float PreemptiveEnemyDistance(FactionHQ hq, CommanderPlatoon platoon, GlobalPosition center)
    {
        float best = Mathf.Min(
            NearestEnemyAssetDistance(hq, center),
            NearestTrackedHostileGroundDistance(hq, center));

        // With no live leader the centre already IS the objective; measuring it twice costs two
        // tracking walks for the same answer.
        if (platoon.Leader == null || platoon.Leader.disabled)
        {
            return best;
        }

        return Mathf.Min(
            best,
            Mathf.Min(
                NearestEnemyAssetDistance(hq, platoon.Objective),
                NearestTrackedHostileGroundDistance(hq, platoon.Objective)));
    }

    /// <summary>Shortest distance to the last known position of a tracked hostile ground unit — the
    /// <c>CountObserved</c> walk with its building skip and its <c>ThreatMemorySeconds</c>
    /// freshness, answering "how far" instead of "how many inside the ring".
    /// <c>float.MaxValue</c> when the commander has nothing tracked.</summary>
    private static float NearestTrackedHostileGroundDistance(FactionHQ hq, GlobalPosition from)
    {
        float best = float.MaxValue;
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > CommanderEnemyCommanderService.ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not GroundVehicle
                || unit is Building
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq))
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(from.AsVector3(), info.lastKnownPosition.AsVector3());
            if (distance < best)
            {
                best = distance;
            }
        }

        return best;
    }

    private static CommanderOperationsMission? FindForwardBaseFor(OperationsState state, CommanderStrategicPoint point)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.ForwardBase && ReferenceEquals(mission.Point, point))
            {
                return mission;
            }
        }

        return null;
    }

    /// <summary>One demand entry: the CAP wing (baseline plus one per tracked hostile aircraft)
    /// is wanted over every active objective — even one with nothing observed, the tracking picture
    /// only shows what has already been spotted — and the CAS ladder reads the same effective
    /// observed count the ground attack sizing does (live picture, or the floor a failed attack
    /// left, whichever is larger).</summary>
    private void AddDemand(
        FactionHQ hq,
        OperationsState state,
        List<CommanderAirSortie> demand,
        CommanderOperationsMission? mission,
        CommanderPlatoon? platoon,
        GlobalPosition center,
        string label,
        bool immediate,
        bool preemptive = false,
        float enemyDistanceMeters = -1f,
        CommanderMissionKind kindForRotary = CommanderMissionKind.Attack)
    {
        int observed = EffectiveObserved(
            CountObserved(hq, center),
            GetObservedFloor(state, ObservedFloorKey(mission?.Point, mission?.TargetAirbase)));
        int hostileAir = CountHostileAirInRing(hq, center, ObservedRadiusMeters);
        (int cas, int cap) = preemptive
            ? PlanPreemptiveSortie(observed, hostileAir)
            : PlanSortie(observed, hostileAir);
        if (cas <= 0 && cap <= 0)
        {
            return;
        }

        demand.Add(new CommanderAirSortie
        {
            Kind = CommanderSortieKind.Objective,
            Mission = mission,
            ContactPlatoon = platoon,
            Center = center,
            Wanted = cas,
            LastObserved = observed,
            LastHostileAir = hostileAir,
            CapsWanted = cap,
            NoCapWait = immediate,
            Preemptive = preemptive,
            EnemyDistanceMeters = enemyDistanceMeters,
            // Helicopter CAS by mission kind (design SS2). A contact sortie counts as contact even
            // when it carries a mission, because the reason it exists is the close fight.
            WantsRotary = SortieWantsRotaryCas(
                mission?.Kind ?? kindForRotary, contact: immediate, preemptive: preemptive),
            // Section 10: an attack whose platoons have gone in is in contact whatever the tracking
            // picture has decayed to — they are on the objective.
            InContact = SortieIsInContact(
                objectiveInContact: immediate,
                attackGoneIn: mission != null && mission.Kind == CommanderMissionKind.Attack && mission.Launched,
                observed,
                hostileAir),
            Label = label,
        });
    }

    /// <summary>Where a sortie over an attack sits: the point, or the base's hold point — the
    /// same target <c>UpdateAttacks</c> drives the platoons at.</summary>
    private static GlobalPosition AttackCenter(CommanderOperationsMission mission)
    {
        return mission.Point != null
            ? mission.Point.Position
            : CommanderCaptureService.GetHoldPointFor(mission.TargetAirbase!);
    }

    /// <summary>Match the live sorties onto this review's demand — by mission, or by the
    /// in-contact platoon (a point does not move, a contact can): update the matched in place,
    /// dissolve the rest, create the new. The state's list ends in demand order, which is the
    /// priority order everything else reads.</summary>
    private void ReconcileSorties(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        for (int i = state.AirSorties.Count - 1; i >= 0; i--)
        {
            CommanderAirSortie live = state.AirSorties[i];
            bool matched = false;
            for (int d = 0; d < demand.Count; d++)
            {
                CommanderAirSortie wanted = demand[d];
                if (!SameSortie(live, wanted))
                {
                    continue;
                }

                wanted.Cas.AddRange(live.Cas);
                wanted.Caps.AddRange(live.Caps);
                wanted.CooldownUntil = live.CooldownUntil;
                // The package's own progress survives the review: a package that has gone in stays
                // gone in, and one still forming keeps the clock it started.
                wanted.GoneIn = live.GoneIn;
                wanted.FormUpPoint = live.FormUpPoint;
                wanted.HasFormUp = live.HasFormUp;
                wanted.FormUpFirstArrivalAt = live.FormUpFirstArrivalAt;
                wanted.FormUpReported = live.FormUpReported;
                wanted.Matched = true;
                matched = true;
                break;
            }

            if (!matched)
            {
                ReleaseSortie(hq, state, live);
            }

            state.AirSorties.RemoveAt(i);
        }

        state.AirSorties.AddRange(demand);

        // Announce the sorties that are new this review, not the ones that were already flying
        // (design SS5's "opens ARAD" line). The reconcile is the only place that knows which is
        // which.
        for (int i = 0; i < demand.Count; i++)
        {
            CommanderAirSortie sortie = demand[i];
            if (sortie.Matched || sortie.Kind != CommanderSortieKind.Arad)
            {
                continue;
            }

            CommanderAiLog.Note(
                hq,
                $"opens ARAD over {sortie.Label}: {sortie.LastObserved} air-defence vehicles clustered "
                    + $"({sortie.Wanted} airframe{(sortie.Wanted == 1 ? string.Empty : "s")}).");
        }
    }

    /// <summary>
    /// Whether a live sortie and a wanted one are the same sortie. The kind comes first: an ARAD
    /// sortie and the CAS sortie over the same objective share a mission and must never be matched
    /// onto each other. There is one AWACS per commander, so its kind alone identifies it; an ARAD
    /// cluster carries no mission of its own, so it is matched by how far its centroid has drifted.
    /// </summary>
    private static bool SameSortie(CommanderAirSortie live, CommanderAirSortie wanted)
    {
        if (live.Kind != wanted.Kind)
        {
            return false;
        }

        if (live.Kind == CommanderSortieKind.Awacs)
        {
            return true; // one per commander, so the kind alone identifies it
        }

        if (live.Kind == CommanderSortieKind.Arad)
        {
            // A belt carries no mission of its own, and one objective can have two belts near it,
            // so an ARAD sortie is matched by how far its centroid has drifted and nothing else.
            return CommanderGameAccess.HorizontalDistance(live.Center.AsVector3(), wanted.Center.AsVector3())
                <= AradClusterLinkMeters;
        }

        if (wanted.Mission != null && ReferenceEquals(live.Mission, wanted.Mission))
        {
            return true;
        }

        return wanted.ContactPlatoon != null && ReferenceEquals(live.ContactPlatoon, wanted.ContactPlatoon);
    }

    /// <summary>Design SS2: released airframes go to the next-hungry sortie (the fill pass below
    /// does that, in priority order) or to the standing home CAP posture — never RTB; they keep
    /// flying, and a Winchester one already went home by the existing rule.</summary>
    private static void ReleaseSortie(FactionHQ hq, OperationsState state, CommanderAirSortie sortie)
    {
        for (int i = 0; i < sortie.Cas.Count; i++)
        {
            Aircraft aircraft = sortie.Cas[i];
            if (aircraft != null && !aircraft.disabled)
            {
                IssueHomeCapTask(hq, aircraft);
                CommanderAiLog.Note(
                    hq, $"releases {CommanderGameAccess.GetUnitLabel(aircraft)}: {sortie.Label} no longer calls for air support.");
            }
        }

        for (int i = 0; i < sortie.Caps.Count; i++)
        {
            Aircraft aircraft = sortie.Caps[i];
            if (aircraft != null && !aircraft.disabled)
            {
                IssueHomeCapTask(hq, aircraft);
                CommanderAiLog.Note(
                    hq, $"releases {CommanderGameAccess.GetUnitLabel(aircraft)}: {sortie.Label} no longer calls for air support.");
            }
        }
    }

    /// <summary>Bind unbound owned airframes onto short sorties — the whole wing's CAP shortfall
    /// before any CAS (user decision 2026-09-13: CAP first), each in priority order — retasking
    /// what is already airborne before any new hull is bought. The first CAP of a sortie is the
    /// escort its CAS waits on (Approval 1), so a sortie that wants both gets its fighter bound in
    /// the same review as or before its CAS.</summary>
    private void FillSorties(FactionHQ hq, OperationsState state)
    {
        // One pass, sortie by sortie in priority order, each sortie filled escort → CAS → wingmen
        // (see NextSortieSlot). The two separate passes this replaced served EVERY sortie's whole
        // CAP demand before any sortie's CAS, which starved CAS completely once the wing had tens
        // of objectives open (2026-09-14 match: 37 CAP bindings, zero CAS).
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (Time.time < sortie.CooldownUntil)
            {
                continue; // a sortie standing down after a loss is not refilled either
            }

            while (true)
            {
                CommanderAirSlot slot = NextSortieSlot(
                    sortie.Caps.Count, sortie.CapsWanted, sortie.Cas.Count, sortie.Wanted);
                if (slot == CommanderAirSlot.None)
                {
                    break;
                }

                bool asCap = slot != CommanderAirSlot.Cas;
                Aircraft? aircraft = TakeUnboundOwned(
                    hq,
                    state,
                    asCap ? CommanderEnemyCommanderService.AirRole.Fighter : SortieAirRole(sortie));

                // Only when nothing else is free: the home CAP is lent forward rather than left
                // orbiting an empty base (design SS9), but a fighter that is already spare is
                // always the cheaper answer. A strike slot is never filled from the home patrol —
                // those fighters are bought to fight air.
                bool lent = false;
                if (aircraft == null && asCap)
                {
                    aircraft = TakeLendableHomeCapFighter(hq, state, sortie.Center);
                    lent = aircraft != null;
                }

                if (aircraft == null)
                {
                    break;
                }

                if (lent)
                {
                    CommanderAiLog.Note(
                        hq,
                        $"lends {CommanderGameAccess.GetUnitLabel(aircraft)} from home CAP to {sortie.Label} "
                            + $"(base quiet {(Time.time - state.HomeCapQuietSince) / 60f:0.#} min).");
                }

                if (asCap)
                {
                    BindCap(hq, state, sortie, aircraft);
                }
                else
                {
                    BindCas(hq, state, sortie, aircraft, origin: null);
                }
            }
        }
    }

    /// <summary>An owned airframe able to fill <paramref name="role"/> that no live sortie holds,
    /// live and flying either one of this commander's tasks or nothing — matched by capability, the
    /// same test the buy and the claim use (user decision 2026-09-14), so a CAP-bought Compass is
    /// fillable as a sortie's escort however its role identity reads. Anything carrying a mission
    /// this commander never issued is the player's now (an RTS order adopts in place): it is let go
    /// entirely, and never retasked by the posture either — decision 5, the air-side hands-off
    /// rule.</summary>
    /// <summary>What an airframe must be able to do to fill this sortie's main slot: ground attack
    /// for an objective, the radar pod for the AWACS, anti-radiation weapons for an ARAD sortie.
    /// A CAP-kind sortie has no main slot — its whole demand is fighters.</summary>
    private static CommanderEnemyCommanderService.AirRole SortieAirRole(CommanderAirSortie sortie)
    {
        return sortie.Kind switch
        {
            CommanderSortieKind.Awacs => CommanderEnemyCommanderService.AirRole.Awacs,
            CommanderSortieKind.Arad => CommanderEnemyCommanderService.AirRole.Arad,
            _ => CommanderEnemyCommanderService.AirRole.Strike,
        };
    }

    private Aircraft? TakeUnboundOwned(FactionHQ hq, OperationsState state, CommanderEnemyCommanderService.AirRole role)
    {
        airStale.Clear();
        foreach (Aircraft owned in state.CommanderAirframes)
        {
            if (owned != null
                && !owned.disabled
                && CommanderAirCommandService.Instance?.TryGetMission(owned) != null
                && !state.AirIssued.ContainsKey(owned))
            {
                airStale.Add(owned);
            }
        }

        for (int i = 0; i < airStale.Count; i++)
        {
            state.CommanderAirframes.Remove(airStale[i]);
            state.AirIssued.Remove(airStale[i]);
        }

        // The BEST-tier candidate, not the first one that fits (design.md,
        // airframe-selection_20260914 Section 3): an owned A-19 Brawler is not bound to a CAP sortie
        // while an owned FS-12 Revoker is sitting unbound. Ties inside a tier go to the airframe
        // encountered first, so a wing of identical hulls behaves exactly as it did before.
        Aircraft? best = null;
        CommanderEnemyCommanderService.AirframeTier bestTier = default;
        foreach (Aircraft aircraft in state.CommanderAirframes)
        {
            if (aircraft == null
                || aircraft.disabled)
            {
                continue;
            }

            AircraftDefinition definition = (aircraft.definition as AircraftDefinition)!;
            if (!CommanderEnemyCommanderService.FillsAirRole(hq, definition, role)
                || IsBoundToAnySortie(state, aircraft)
                // Section 17: somebody else is already flying it.
                || IsUnderOtherService(state, aircraft)
                // Rung-1 fighters never leave the home CAP for a sortie, however unbound they are
                // (design.md, commander-priorities_20260914 Section 2).
                || state.HomeCapAirframes.Contains(aircraft))
            {
                continue;
            }

            CommanderEnemyCommanderService.AirframeTier tier =
                CommanderEnemyCommanderService.ForRole(definition, role);
            if (best == null || CommanderEnemyCommanderService.TierBeats(tier, bestTier, role))
            {
                best = aircraft;
                bestTier = tier;
            }
        }

        return best;
    }

    private static bool IsBoundToAnySortie(OperationsState state, Aircraft aircraft)
    {
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            for (int c = 0; c < sortie.Caps.Count; c++)
            {
                if (ReferenceEquals(sortie.Caps[c], aircraft))
                {
                    return true;
                }
            }

            for (int c = 0; c < sortie.Cas.Count; c++)
            {
                if (ReferenceEquals(sortie.Cas[c], aircraft))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Walk every bound airframe and make its live mission match what the sortie wants
    /// now: held over home while the CAP is missing, over the objective once it is not, and a
    /// rewritten area when the objective moved past the hysteresis. A mission that moved in a way
    /// this commander did not issue is the player's order — the airframe is let go.</summary>
    private void SyncAirTasks(FactionHQ hq, OperationsState state)
    {
        for (int s = 0; s < state.AirSorties.Count; s++)
        {
            CommanderAirSortie sortie = state.AirSorties[s];
            // The form-up orbit replaces the old hold over home territory (design SS3): a package
            // that is not yet complete waits 12 km out on the friendly side, not back over the
            // commander's own ground, so going in costs one short leg instead of the whole transit.
            bool holdAtFormUp = SortieHoldsAtFormUp(sortie);
            for (int i = sortie.Cas.Count - 1; i >= 0; i--)
            {
                SyncBoundAirframe(
                    hq, state, sortie, sortie.Cas[i],
                    HoldingMode(sortie, asCap: false),
                    holdAtFormUp ? sortie.FormUpPoint : sortie.Center,
                    holdAtFormUp ? PackageArrivalMeters : SortieRadius(sortie),
                    capJoin: !holdAtFormUp && sortie.CapsWanted > 0);
            }

            for (int i = 0; i < sortie.Caps.Count; i++)
            {
                SyncBoundAirframe(
                    hq, state, sortie, sortie.Caps[i],
                    CommanderAirCommandService.AirCommandMode.AirGuard,
                    holdAtFormUp ? sortie.FormUpPoint : sortie.Center,
                    holdAtFormUp ? PackageArrivalMeters : CasSortieRadiusMeters,
                    capJoin: false);
            }
        }
    }

    /// <summary>The Air Command mode a sortie's own airframes fly over the objective: CAS for an
    /// objective, the anti-radiation mode for an ARAD sortie, and the standing patrol mode for the
    /// AWACS (design SS4: "flies AirGuard mode at the station").</summary>
    private static CommanderAirCommandService.AirCommandMode SortieCasMode(CommanderAirSortie sortie)
    {
        return sortie.Kind switch
        {
            CommanderSortieKind.Arad => CommanderAirCommandService.AirCommandMode.Arad,
            CommanderSortieKind.Awacs => CommanderAirCommandService.AirCommandMode.AirGuard,
            _ => CommanderAirCommandService.AirCommandMode.Cas,
        };
    }

    /// <summary>The mission-area radius a sortie's airframes are given: the sortie ring for
    /// everything that works a patch of ground, the wide orbit for the AWACS, which is there to
    /// look rather than to hold a point.</summary>
    private static float SortieRadius(CommanderAirSortie sortie)
    {
        return sortie.Kind == CommanderSortieKind.Awacs ? AwacsOrbitRadiusMeters : CasSortieRadiusMeters;
    }

    private void SyncBoundAirframe(
        FactionHQ hq,
        OperationsState state,
        CommanderAirSortie sortie,
        Aircraft aircraft,
        CommanderAirCommandService.AirCommandMode desiredMode,
        GlobalPosition desiredCenter,
        float desiredRadius,
        bool capJoin)
    {
        if (aircraft == null || aircraft.disabled)
        {
            return; // the prune next review unbinds it
        }

        CommanderAirCommandService.AirMission? mission = CommanderAirCommandService.Instance?.TryGetMission(aircraft);
        if (mission == null)
        {
            return; // the tasking went away under it (an RTB/recovery path); the prune unbinds it
        }

        // The hands-off read: our snapshot matches the live mission, or the player moved it.
        if (!state.AirIssued.TryGetValue(aircraft, out IssuedAirTask issued)
            || mission.Mode != issued.Mode
            || CommanderGameAccess.HorizontalDistance(mission.AreaCenter.AsVector3(), issued.Center.AsVector3()) > CasRetargetMeters)
        {
            ReleaseToPlayer(hq, state, sortie, aircraft);
            return;
        }

        if (mission.Mode == desiredMode
            && CommanderGameAccess.HorizontalDistance(mission.AreaCenter.AsVector3(), desiredCenter.AsVector3()) <= CasRetargetMeters)
        {
            return;
        }

        bool joinsCap = capJoin && mission.Mode == CommanderAirCommandService.AirCommandMode.AirGuard;
        IssueAirTask(hq, state, aircraft, desiredMode, desiredCenter, desiredRadius);
        if (joinsCap)
        {
            CommanderAiLog.Note(hq, $"{CommanderGameAccess.GetUnitLabel(aircraft)} joins its CAP over {sortie.Label}.");
        }
        else if (CommanderSettings.OperationsDebugLog && hq.faction != null)
        {
            CommanderPlugin.Log.LogInfo(
                $"Ops {CommanderPlayerCommanderService.CommanderLabel(hq)}: retasked {CommanderGameAccess.GetUnitLabel(aircraft)} onto {sortie.Label} (objective moved).");
        }
    }

    private static void ReleaseToPlayer(FactionHQ hq, OperationsState state, CommanderAirSortie sortie, Aircraft aircraft)
    {
        sortie.Cas.Remove(aircraft);
        sortie.Caps.Remove(aircraft);

        state.CommanderAirframes.Remove(aircraft);
        state.AirIssued.Remove(aircraft);
        state.IdleSweepReported.Remove(aircraft);
        state.AirRetaskedAt.Remove(aircraft);
        // Not in a sortie's lists, but a rung-1 fighter on the home CAP is let go the same way: the
        // player's order owns it now, so it stops counting toward the CAP and stops being tasked.
        state.HomeCapAirframes.Remove(aircraft);
        state.HomeCapEnemyAirNear.Remove(aircraft);
        state.LentHomeCap.Remove(aircraft);
        CommanderAiLog.Note(hq, $"lets {CommanderGameAccess.GetUnitLabel(aircraft)} go: the player has it under orders.");
    }

    private void BindCas(FactionHQ hq, OperationsState state, CommanderAirSortie sortie, Aircraft aircraft, Airbase? origin)
    {
        sortie.Cas.Add(aircraft);
        // The launch lead time (Approval 1): how long this airframe needs to reach the objective
        // from the strip it left, for the tasking line.
        float transitSeconds = -1f;
        if (origin != null && origin.center != null)
        {
            transitSeconds = CasTransitSeconds(
                CommanderGameAccess.HorizontalDistance(origin.center.GlobalPosition().AsVector3(), sortie.Center.AsVector3()),
                IsRotaryAircraft(aircraft));
        }

        if (SortieHoldsAtFormUp(sortie))
        {
            IssueAirTask(
                hq, state, aircraft, HoldingMode(sortie, asCap: false),
                sortie.FormUpPoint, PackageArrivalMeters);
            CommanderAiLog.Note(
                hq,
                $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} for {sortie.Label}, holding at the form-up point "
                    + $"until the package is up ({DescribeSortieReason(sortie, transitSeconds)}).");
        }
        else
        {
            IssueAirTask(hq, state, aircraft, SortieCasMode(sortie), sortie.Center, SortieRadius(sortie));
            CommanderAiLog.Note(
                hq,
                $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} with {DescribeSortieTask(sortie)} over {sortie.Label} "
                    + $"({DescribeSortieReason(sortie, transitSeconds)}).");
        }
    }

    /// <summary>What a tasking line calls the job: the sortie kind in plain words.</summary>
    private static string DescribeSortieTask(CommanderAirSortie sortie)
    {
        return sortie.Kind switch
        {
            CommanderSortieKind.Arad => "an anti-radiation strike",
            CommanderSortieKind.Awacs => "radar watch",
            _ => "CAS",
        };
    }

    /// <summary>The parenthesis on a CAS tasking line: why this sortie exists and, when the launch
    /// airbase is known, how long the airframe needs to get there. A pre-emptive sortie says so and
    /// gives the distance to the enemy that opened it, rounded to kilometres — the number a reader
    /// needs to see that the wing went up before the platoon was shot at.</summary>
    private static string DescribeSortieReason(CommanderAirSortie sortie, float transitSeconds)
    {
        string reason = sortie.Kind switch
        {
            CommanderSortieKind.Awacs => "one radar airframe per commander",
            CommanderSortieKind.Arad => $"{sortie.LastObserved} air-defence vehicles clustered",
            _ => sortie.Preemptive
                ? $"pre-emptive, enemy {sortie.EnemyDistanceMeters / 1000f:0.#} km"
                : $"{sortie.LastObserved} observed",
        };
        return transitSeconds > 0f ? $"{reason}; on station in ~{transitSeconds:0} s" : reason;
    }

    /// <summary>Bind a fighter onto the sortie's CAP. The first one is the escort — the airframe
    /// whose binding releases the held CAS to the objective (Approval 1, kept under CAP-first);
    /// later ones are wingmen.</summary>
    private static void BindCap(FactionHQ hq, OperationsState state, CommanderAirSortie sortie, Aircraft aircraft)
    {
        bool isEscort = sortie.Caps.Count == 0 && sortie.Cas.Count > 0;
        sortie.Caps.Add(aircraft);
        bool holdAtFormUp = SortieHoldsAtFormUp(sortie);
        IssueAirTask(
            hq, state, aircraft, CommanderAirCommandService.AirCommandMode.AirGuard,
            holdAtFormUp ? sortie.FormUpPoint : sortie.Center,
            holdAtFormUp ? PackageArrivalMeters : CasSortieRadiusMeters);
        if (isEscort)
        {
            CommanderAiLog.Note(
                hq,
                sortie.Preemptive
                    ? $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} as the escort over {sortie.Label} (pre-emptive)."
                    : $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} as the escort over {sortie.Label}.");
        }
        else
        {
            CommanderAiLog.Note(
                hq,
                $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} on CAP over {sortie.Label} "
                    + $"(wing {sortie.Caps.Count}/{sortie.CapsWanted}; {sortie.LastHostileAir} hostile air tracked).");
        }
    }

    /// <summary>The one tasking door (Reuse rule 4): every task this commander issues an airframe —
    /// bind, retarget, hold, release, posture — goes through here so the hands-off snapshot is
    /// always written with it. Returns whether the task took (a rotary airframe cannot take one).</summary>
    private static bool IssueAirTask(
        FactionHQ hq,
        OperationsState state,
        Aircraft aircraft,
        CommanderAirCommandService.AirCommandMode mode,
        GlobalPosition center,
        float radius)
    {
        if (CommanderAirCommandService.Instance?.TryTaskAiAircraft(aircraft, mode, center, radius, retaskExisting: true) == true)
        {
            state.AirIssued[aircraft] = new IssuedAirTask(mode, center);
            return true;
        }

        return false;
    }

    /// <summary>The release door: the standing home CAP box, retasked over whatever of ours the
    /// airframe is currently flying. Refuses an airframe whose mission this commander did not
    /// issue — that one is the player's (decision 5). Returns whether the task took.</summary>
    internal static bool IssueHomeCapTask(FactionHQ hq, Aircraft aircraft)
    {
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return false;
        }

        if (CommanderAirCommandService.Instance?.TryGetMission(aircraft) != null
            && !state.AirIssued.ContainsKey(aircraft))
        {
            return false;
        }

        IssueAirTask(
            hq, state, aircraft, CommanderAirCommandService.AirCommandMode.AirGuard,
            HomeCAPCentre(hq), CommanderEnemyCommanderService.HomeGuardRadiusMeters);
        return true;
    }

    /// <summary>The residual posture door: the standing home CAP onto an owned airframe that
    /// carries no mission at all. A sortie-bound one, a released one already holding the box and a
    /// player-ordered one are all somebody else's business — the posture never retasks.</summary>
    internal static bool IssuePostureTask(FactionHQ hq, Aircraft aircraft)
    {
        if (Instance == null
            || !Instance.states.TryGetValue(hq, out OperationsState state)
            || CommanderAirCommandService.Instance?.IsOnAnyMission(aircraft) == true)
        {
            return false;
        }

        IssueAirTask(
            hq, state, aircraft, CommanderAirCommandService.AirCommandMode.AirGuard,
            HomeCAPCentre(hq), CommanderEnemyCommanderService.HomeGuardRadiusMeters);
        return true;
    }

    /// <summary>The standing CAP centre: the HQ's territory centre — the same "home ground"
    /// aggregate the reserve ring and the old wing's home guard used (Reuse rule 4, one
    /// definition).</summary>
    private static GlobalPosition HomeCAPCentre(FactionHQ hq)
    {
        return CommanderCaptureService.GetTerritoryCenter(hq);
    }

    private static bool IsRotaryAircraft(Aircraft aircraft)
    {
        return aircraft.pilots != null
            && aircraft.pilots.Length > 0
            && aircraft.pilots[0] != null
            && CommanderAirCommandService.IsRotaryPilot(aircraft.pilots[0]);
    }

    /// <summary>Prune the air book: owned airframes that went away stop being ours, and a bound
    /// airframe that died over the objective stamps the sortie's cooldown — while one that turned
    /// for home (the existing out-of-ammo rule) only frees its slot: a rearm cycle is not a loss.</summary>
    private void PruneAirBook(FactionHQ hq, OperationsState state)
    {
        airStale.Clear();
        foreach (Aircraft owned in state.CommanderAirframes)
        {
            if (owned == null || owned.disabled)
            {
                airStale.Add(owned!);
            }
        }

        for (int i = 0; i < airStale.Count; i++)
        {
            state.CommanderAirframes.Remove(airStale[i]);
            state.AirIssued.Remove(airStale[i]);
            state.IdleSweepReported.Remove(airStale[i]);
            state.AirRetaskedAt.Remove(airStale[i]);
        }

        for (int s = 0; s < state.AirSorties.Count; s++)
        {
            CommanderAirSortie sortie = state.AirSorties[s];
            PruneSortieAirframes(hq, state, sortie, sortie.Cas);
            PruneSortieAirframes(hq, state, sortie, sortie.Caps);
        }
    }

    private void PruneSortieAirframes(FactionHQ hq, OperationsState state, CommanderAirSortie sortie, List<Aircraft> bound)
    {
        airStale.Clear();
        for (int i = 0; i < bound.Count; i++)
        {
            Aircraft aircraft = bound[i];
            if (aircraft == null || aircraft.disabled)
            {
                // Dead while bound and not marked for home below = lost over the objective (the
                // returning case is unbound here before it can disable, so what reaches here
                // disabled without ever Returning died in the fight or on the way in).
                StampAirLoss(hq, sortie);
                airStale.Add(aircraft!);
            }
            else if (IsReturning(aircraft))
            {
                if (CommanderSettings.OperationsDebugLog && hq.faction != null)
                {
                    CommanderPlugin.Log.LogInfo(
                        $"Ops {CommanderPlayerCommanderService.CommanderLabel(hq)}: {CommanderGameAccess.GetUnitLabel(aircraft)} goes home to rearm; its slot over {sortie.Label} reopens.");
                }

                airStale.Add(aircraft);
            }
        }

        for (int i = 0; i < airStale.Count; i++)
        {
            bound.Remove(airStale[i]);
            state.AirIssued.Remove(airStale[i]);
        }
    }

    private static bool IsReturning(Aircraft aircraft)
    {
        CommanderAirCommandService.AirMission? mission = CommanderAirCommandService.Instance?.TryGetMission(aircraft);
        return mission != null && mission.Returning;
    }

    private static void StampAirLoss(FactionHQ hq, CommanderAirSortie sortie)
    {
        int airDefence = CountObservedAirDefence(hq, sortie.Center);
        float minutes = CasLossCooldownMinutes(airDefence, CommanderSettings.CasLossCooldownMinutes);
        sortie.CooldownUntil = Time.time + minutes * 60f;
        CommanderAiLog.Note(
            hq,
            $"loses an airframe over {sortie.Label}: CAS stands down for {minutes:0} min ({airDefence} hostile air-defence observed).");
    }

    /// <summary>Tracked hostile air-defence ground vehicles within the sizing ring of
    /// <paramref name="center"/> — the <c>CountObserved</c> walk with the air-defence role test the
    /// buyer already uses, retargeted at an objective.</summary>
    private static int CountObservedAirDefence(FactionHQ hq, GlobalPosition center)
    {
        int count = 0;
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > CommanderEnemyCommanderService.ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not GroundVehicle
                || unit is Building
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq)
                || unit.definition is not VehicleDefinition definition
                || !CommanderEnemyCommanderService.IsAirDefence(definition))
            {
                continue;
            }

            if (CommanderGameAccess.HorizontalDistance(center.AsVector3(), info.lastKnownPosition.AsVector3()) <= ObservedRadiusMeters)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Tracked hostile aircraft inside a ring of the caller's choosing — the
    /// <c>CountObserved</c> walk with the aircraft type test and its freshness rule (design SS1; one
    /// CAP fighter per tracked hostile, on top of the baseline). Internal (one-word widening, Reuse
    /// rule 4): the sortie sizing reads it at <see cref="ObservedRadiusMeters"/> over an objective,
    /// and the home-CAP loss test reads the same walk at <see cref="CapLossRadiusMeters"/> around a
    /// fighter's own position — one definition, two callers.</summary>
    internal static int CountHostileAirInRing(FactionHQ hq, GlobalPosition center, float radiusMeters)
    {
        int count = 0;
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > CommanderEnemyCommanderService.ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not Aircraft
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq))
            {
                continue;
            }

            if (CommanderGameAccess.HorizontalDistance(center.AsVector3(), info.lastKnownPosition.AsVector3()) <= radiusMeters)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>What the buyer should put in the air next for the sorties: the wing's whole CAP
    /// shortfall first (user decision 2026-09-13: CAP first — when the fund covers one airframe
    /// and both are short, it buys the fighter), then the CAS shortfall, each in sortie priority
    /// order. A sortie inside its loss cooldown asks for nothing — that is the cooldown's whole
    /// effect.</summary>
    /// <summary>Whether the wing is short of anything at all — the ladder's rung-2 demand test,
    /// which needs the answer and none of the detail.</summary>
    internal static bool HasAirDemand(FactionHQ hq)
    {
        return TryGetAirDemand(hq, out _, out _, out _, out _, out _) != CommanderAirDemandKind.None;
    }

    /// <param name="observedHostiles">Hostile ground units the chosen sortie observed over its
    /// objective this review, and <paramref name="trackedAircraft"/> the hostile aircraft it tracked
    /// in the ring. Both are the numbers the sortie was already SIZED from
    /// (<see cref="CommanderAirSortie.LastObserved"/> / <see cref="CommanderAirSortie.LastHostileAir"/>),
    /// handed on rather than re-counted, so the airframe tier rule (design.md,
    /// airframe-selection_20260914 Section 2) judges the buy against exactly the threat that asked
    /// for it — one definition, two callers.</param>
    internal static CommanderAirDemandKind TryGetAirDemand(
        FactionHQ hq,
        out GlobalPosition objective,
        out bool wantsRotary,
        out string label,
        out int trackedAircraft,
        out int observedHostiles)
    {
        objective = default;
        wantsRotary = false;
        label = string.Empty;
        trackedAircraft = 0;
        observedHostiles = 0;
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return CommanderAirDemandKind.None;
        }

        // Each sortie asks for ONE thing — whatever its own escort → CAS → wingmen order wants next
        // (see NextSortieSlot) — and the highest-priority sortie's answer wins its kind's slot. The
        // walk this replaced recorded a CAP shortfall from ANY sortie and a CAS shortfall from any
        // other, so with tens of objectives open a CAP shortfall always existed and the buyer bought
        // nothing but fighters: 37 of them against 7 strike airframes in the 2026-09-14 match.
        CommanderAirSortie? awacsSortie = null;
        CommanderAirSortie? capSortie = null;
        CommanderAirSortie? aradSortie = null;
        CommanderAirSortie? casSortie = null;
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (Time.time < sortie.CooldownUntil)
            {
                continue;
            }

            CommanderAirSlot slot = NextSortieSlot(
                sortie.Caps.Count, sortie.CapsWanted, sortie.Cas.Count, sortie.Wanted);
            if (slot == CommanderAirSlot.None)
            {
                continue;
            }

            if (slot != CommanderAirSlot.Cas)
            {
                capSortie ??= sortie;
                continue;
            }

            switch (sortie.Kind)
            {
                case CommanderSortieKind.Awacs:
                    awacsSortie ??= sortie;
                    break;
                case CommanderSortieKind.Arad:
                    aradSortie ??= sortie;
                    break;
                default:
                    casSortie ??= sortie;
                    break;
            }
        }

        CommanderAirDemandKind kind = NextAirDemand(
            awacsSortie != null, capSortie != null, aradSortie != null, casSortie != null);
        CommanderAirSortie? chosen = kind switch
        {
            CommanderAirDemandKind.Awacs => awacsSortie,
            CommanderAirDemandKind.Cap => capSortie,
            CommanderAirDemandKind.Arad => aradSortie,
            CommanderAirDemandKind.Cas => casSortie,
            _ => null,
        };

        if (chosen != null)
        {
            objective = chosen.Center;
            wantsRotary = kind == CommanderAirDemandKind.Cas && chosen.WantsRotary;
            label = chosen.Label;
            trackedAircraft = chosen.LastHostileAir;
            observedHostiles = chosen.LastObserved;
        }

        return kind;
    }

    /// <summary>
    /// Which air demands are open right now — ALL of them, rather than the single highest-priority
    /// one <see cref="TryGetAirDemand"/> answers with. The air fund's ceiling reads this: the wing
    /// may bank up to the price of the dearest airframe an open demand actually wants, and not a
    /// penny more, so a quiet sky cannot sit on a fund the ground is asking for. The walk is
    /// <see cref="TryGetAirDemand"/>'s own, with every kind recorded instead of the first of each.
    /// </summary>
    internal static void ReadOpenAirDemandRoles(
        FactionHQ hq, out bool cap, out bool cas, out bool rotaryCas, out bool awacs, out bool arad)
    {
        cap = false;
        cas = false;
        rotaryCas = false;
        awacs = false;
        arad = false;
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (Time.time < sortie.CooldownUntil)
            {
                continue;
            }

            CommanderAirSlot slot = NextSortieSlot(
                sortie.Caps.Count, sortie.CapsWanted, sortie.Cas.Count, sortie.Wanted);
            if (slot == CommanderAirSlot.None)
            {
                continue;
            }

            // Escorts and wingmen are both CAP fighters, which is why every non-CAS slot lands here.
            if (slot != CommanderAirSlot.Cas)
            {
                cap = true;
                continue;
            }

            switch (sortie.Kind)
            {
                case CommanderSortieKind.Awacs:
                    awacs = true;
                    break;
                case CommanderSortieKind.Arad:
                    arad = true;
                    break;
                default:
                    cas = true;
                    if (sortie.WantsRotary)
                    {
                        rotaryCas = true;
                    }

                    break;
            }
        }
    }

    /// <summary>The wing's live demand counts for the once-per-review diagnostics line: bound and
    /// wanted CAP fighters and CAS airframes across every sortie that is not standing down after a
    /// loss — the same shortfalls <see cref="TryGetAirDemand"/> reads.</summary>
    internal static void ReadAirDemandCounts(
        FactionHQ hq, out int capBound, out int capWanted, out int casBound, out int casWanted)
    {
        capBound = 0;
        capWanted = 0;
        casBound = 0;
        casWanted = 0;
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (Time.time < sortie.CooldownUntil)
            {
                continue;
            }

            capBound += sortie.Caps.Count;
            capWanted += sortie.CapsWanted;
            casBound += sortie.Cas.Count;
            casWanted += sortie.Wanted;
        }
    }

    /// <summary>Whether this airframe was launched by this commander's buy loop — the tasking and
    /// posture steps touch nothing else (decision 5: the player's own Air Command missions and
    /// stock-mission authored aircraft are never claimed, bound or retasked).</summary>
    internal static bool IsCommanderAirframe(FactionHQ hq, Aircraft? aircraft)
    {
        return Instance != null
            && aircraft != null
            && Instance.states.TryGetValue(hq, out OperationsState state)
            && state.CommanderAirframes.Contains(aircraft);
    }

    // ---- Home CAP bookkeeping (design.md, commander-priorities_20260914, rung 1) ----

    private readonly List<Aircraft> homeCapStale = new();

    /// <summary>
    /// One maintenance pass per commanded HQ on the defence review's fast (10 s) clock: refresh each
    /// live home-CAP fighter's "enemy aircraft tracked within <see cref="CapLossRadiusMeters"/>" observation,
    /// and stamp a CAP loss for a fighter that died while that observation was true — losing aircraft
    /// to enemy air buys more CAP fighters, ground losses do not (design Section 2). Runs faster
    /// than the buy review on purpose: the observation has to be recent when the wreck is found, or
    /// every death would read as an ordinary ground loss.
    /// <para>
    /// The observation map is what decouples the loss test from the wreck: a destroyed Unity object
    /// still keys the dictionary (the managed reference survives), so the last known "was enemy air
    /// near" answers for the fighter even after its transform is gone.
    /// </para>
    /// </summary>
    internal static void MaintainHomeCap(FactionHQ hq)
    {
        CommanderOperationsService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        // The loan's quiet clock (design SS9), refreshed on this fast cadence rather than the 30 s
        // review so a raid that appears between reviews ends the loan at once.
        if (CommanderEnemyCommanderService.CountTrackedEnemyAircraftNearBases(hq) > 0)
        {
            state.HomeCapQuietSince = -1f;
        }
        else if (state.HomeCapQuietSince < 0f)
        {
            state.HomeCapQuietSince = Time.time;
        }

        float now = Time.time;
        float window = CapLossMemoryMinutes * 60f;
        for (int i = state.CapLossTimes.Count - 1; i >= 0; i--)
        {
            if (now - state.CapLossTimes[i] > window)
            {
                state.CapLossTimes.RemoveAt(i);
            }
        }

        service.homeCapStale.Clear();
        foreach (Aircraft fighter in state.HomeCapAirframes)
        {
            if (fighter == null || fighter.disabled)
            {
                // Unity-null (destroyed) still keys the dictionary — the managed reference survives
                // the wreck, which is the whole point of the observation map.
                if (state.HomeCapEnemyAirNear.TryGetValue(fighter!, out bool airNear) && airNear)
                {
                    state.CapLossTimes.Add(now);
                    CommanderAiLog.Note(
                        hq,
                        "lost a home-CAP fighter to enemy air: one more CAP fighter will be wanted for "
                            + $"{CapLossMemoryMinutes:0} min.");
                }

                service.homeCapStale.Add(fighter!);
                continue;
            }

            state.HomeCapEnemyAirNear[fighter] =
                CountHostileAirInRing(hq, fighter.transform.GlobalPosition(), CapLossRadiusMeters) > 0;
        }

        for (int i = 0; i < service.homeCapStale.Count; i++)
        {
            state.HomeCapAirframes.Remove(service.homeCapStale[i]);
            state.HomeCapEnemyAirNear.Remove(service.homeCapStale[i]);
            state.LentHomeCap.Remove(service.homeCapStale[i]);
        }
    }

    // ---- Lending the home CAP forward (design.md, smarter-air-wing_20260914 Section 9) ----

    private readonly List<Aircraft> homeCapRecall = new();

    /// <summary>Home-CAP fighters lent forward right now — the <c>ladder:</c> line's loan figure.
    /// They are still counted as held by <see cref="CountHomeCapFighters"/>, which is the whole
    /// point: lending must never read as a loss.</summary>
    internal static int CountLentHomeCap(FactionHQ hq)
    {
        CommanderOperationsService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out OperationsState state))
        {
            return 0;
        }

        int count = 0;
        foreach (Aircraft fighter in state.LentHomeCap)
        {
            if (fighter != null && !fighter.disabled)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Bring lent fighters home when the base needs them: a hostile aircraft tracked in the base
    /// ring again, or a loss that has dropped the CAP to where the minimum no longer allows the
    /// loan. The fighter leaves its sortie, so that sortie's CAP demand reopens this same review and
    /// is filled or bought the ordinary way.
    /// </summary>
    private void RecallLentHomeCap(FactionHQ hq, OperationsState state)
    {
        if (state.LentHomeCap.Count == 0)
        {
            return;
        }

        // A loan ends when the sortie does: a fighter released back to the posture, gone home to
        // rearm or dissolved out of its sortie is not lent any more, and must be lendable again.
        homeCapRecall.Clear();
        foreach (Aircraft fighter in state.LentHomeCap)
        {
            if (fighter == null || fighter.disabled || !IsBoundToAnySortie(state, fighter))
            {
                homeCapRecall.Add(fighter!);
            }
        }

        for (int i = 0; i < homeCapRecall.Count; i++)
        {
            state.LentHomeCap.Remove(homeCapRecall[i]);
        }

        int alive = CountHomeCapFighters(hq);
        int lent = CountLentHomeCap(hq);
        bool quiet = HomeCapIsQuiet(
            CommanderEnemyCommanderService.CountTrackedEnemyAircraftNearBases(hq),
            state.HomeCapQuietSince < 0f ? -1f : Time.time - state.HomeCapQuietSince,
            HomeCapQuietSeconds);
        int allowed = quiet ? LendableHomeCap(alive, HomeCapMinimumHeld) : 0;
        if (lent <= allowed)
        {
            return;
        }

        homeCapRecall.Clear();
        foreach (Aircraft fighter in state.LentHomeCap)
        {
            homeCapRecall.Add(fighter);
        }

        for (int i = 0; i < homeCapRecall.Count && lent > allowed; i++)
        {
            Aircraft fighter = homeCapRecall[i];
            state.LentHomeCap.Remove(fighter);
            lent--;
            for (int s = 0; s < state.AirSorties.Count; s++)
            {
                state.AirSorties[s].Caps.Remove(fighter);
                state.AirSorties[s].Cas.Remove(fighter);
            }

            if (fighter == null || fighter.disabled)
            {
                continue;
            }

            IssueAirTask(
                hq, state, fighter, CommanderAirCommandService.AirCommandMode.AirGuard,
                HomeCAPCentre(hq), CommanderEnemyCommanderService.HomeGuardRadiusMeters);
            CommanderAiLog.Note(
                hq,
                quiet
                    ? $"recalls {CommanderGameAccess.GetUnitLabel(fighter)} to home CAP: the base is down to its minimum patrol."
                    : $"recalls {CommanderGameAccess.GetUnitLabel(fighter)} to home CAP: hostile air near the base.");
        }

        homeCapRecall.Clear();
    }

    /// <summary>
    /// A home-CAP fighter the quiet base can spare for <paramref name="center"/>, nearest first, or
    /// null when the base is not quiet or is already down to its minimum. The borrowed fighter stays
    /// in the home-CAP set — it is lent, not transferred.
    /// </summary>
    private Aircraft? TakeLendableHomeCapFighter(FactionHQ hq, OperationsState state, GlobalPosition center)
    {
        if (!HomeCapIsQuiet(
                CommanderEnemyCommanderService.CountTrackedEnemyAircraftNearBases(hq),
                state.HomeCapQuietSince < 0f ? -1f : Time.time - state.HomeCapQuietSince,
                HomeCapQuietSeconds))
        {
            return null;
        }

        if (CountLentHomeCap(hq) >= LendableHomeCap(CountHomeCapFighters(hq), HomeCapMinimumHeld))
        {
            return null;
        }

        Aircraft? best = null;
        float bestDistance = float.MaxValue;
        foreach (Aircraft fighter in state.HomeCapAirframes)
        {
            if (fighter == null
                || fighter.disabled
                || state.LentHomeCap.Contains(fighter)
                || IsBoundToAnySortie(state, fighter)
                || IsUnderOtherService(state, fighter)
                || CommanderAirCommandService.Instance?.TryGetMission(fighter) == null
                || !state.AirIssued.ContainsKey(fighter))
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                fighter.transform.GlobalPosition().AsVector3(), center.AsVector3());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = fighter;
            }
        }

        if (best != null)
        {
            state.LentHomeCap.Add(best);
        }

        return best;
    }

    /// <summary>
    /// Home-CAP fighters this commander has alive — airborne or parked on deck, anything the faction
    /// still fields. Only rung-1 airframes count: released sortie fighters may happen to be flying
    /// the same box, but they can be borrowed again the moment a sortie wants them, so they are not
    /// the standing screen the strict rung exists to guarantee.
    /// </summary>
    internal static int CountHomeCapFighters(FactionHQ hq)
    {
        CommanderOperationsService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out OperationsState state))
        {
            return 0;
        }

        int count = 0;
        foreach (Aircraft fighter in state.HomeCapAirframes)
        {
            if (fighter != null && !fighter.disabled)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Home-CAP fighters lost to enemy air inside <see cref="CapLossMemoryMinutes"/> — the
    /// third term of the CAP formula (design Section 2). Counts the window itself rather than trusting
    /// the maintenance pass to have run first.</summary>
    internal static int HomeCapLosses(FactionHQ hq)
    {
        CommanderOperationsService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out OperationsState state))
        {
            return 0;
        }

        float now = Time.time;
        int count = 0;
        for (int i = 0; i < state.CapLossTimes.Count; i++)
        {
            if (now - state.CapLossTimes[i] <= CapLossMemoryMinutes * 60f)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The priority ladder grants the picket rung its share for this HQ (design Section 3, rung 3):
    /// insertion flights may charge up to this much until the next ladder review overwrites it.
    /// Called from the enemy commander's review — the single spend site — once rung 3's turn comes.
    /// </summary>
    internal static void GrantInsertionAllowance(FactionHQ hq, float allowance)
    {
        if (Instance != null && Instance.states.TryGetValue(hq, out OperationsState state))
        {
            state.InsertionAllowance = allowance;
        }
    }

    /// <summary>
    /// Insertion money charged since the last <c>ladder:</c> line, and clears the tally — the line
    /// reports each rung's spend when it happens, not when it was granted, so picket spending shows
    /// up on the review after the flight was charged.
    /// </summary>
    internal static float TakeInsertionSpend(FactionHQ hq)
    {
        if (Instance != null && Instance.states.TryGetValue(hq, out OperationsState state))
        {
            float spent = state.InsertionSpentSinceLadder;
            state.InsertionSpentSinceLadder = 0f;
            return spent;
        }

        return 0f;
    }

    /// <summary><c>UpdateAttacks</c>' go-in hold (Approval 1): true while this attack's sortie
    /// wants CAS, has nothing on station, can still be reached in time from a held airbase, and the
    /// attack's own form-up wait has not run out. The timeout is the attack's existing one — one
    /// bounded wait, no second clock.</summary>
    private static bool HoldsForCas(OperationsState state, FactionHQ hq, CommanderOperationsMission mission)
    {
        CommanderAirSortie? sortie = null;
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            if (state.AirSorties[i].Kind == CommanderSortieKind.Objective
                && ReferenceEquals(state.AirSorties[i].Mission, mission))
            {
                sortie = state.AirSorties[i];
                break;
            }
        }

        if (sortie == null)
        {
            return false;
        }

        // Design SS3: "CAS on station" now means the package has gone in AND its first airframe is
        // inside the objective ring. Before the package existed, one airframe arriving alone
        // released the attack; now the attack waits for the formation it is going in with.
        bool onStation = false;
        for (int i = 0; sortie.GoneIn && i < sortie.Cas.Count; i++)
        {
            Aircraft aircraft = sortie.Cas[i];
            if (aircraft != null
                && !aircraft.disabled
                && CommanderGameAccess.HorizontalDistance(aircraft.transform.GlobalPosition().AsVector3(), sortie.Center.AsVector3()) <= CasSortieRadiusMeters)
            {
                onStation = true;
                break;
            }
        }

        if (onStation || !CasCanArriveInTime(hq, sortie))
        {
            return false;
        }

        return AttackHoldsForCas(
            sortieExists: true,
            casWanted: sortie.Wanted > 0,
            casOnStation: onStation,
            waitedSeconds: Time.time - mission.FirstGroupArrivedAt,
            timeoutSeconds: AssaultFormUpTimeoutSeconds);
    }

    /// <summary>Whether CAS from the nearest held airbase can still reach the objective inside the
    /// attack's form-up window — an attack never holds its go-in for air support that cannot
    /// arrive in time. Jets' speed is the yardstick: the wing's strike airframes are jets.</summary>
    private static bool CasCanArriveInTime(FactionHQ hq, CommanderAirSortie sortie)
    {
        float best = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                airbase.center.GlobalPosition().AsVector3(), sortie.Center.AsVector3());
            if (distance < best)
            {
                best = distance;
            }
        }

        return best < float.MaxValue && CasTransitSeconds(best, rotary: false) <= AssaultFormUpTimeoutSeconds;
    }

    /// <summary>The air segment of the review diagnostics line, design SS4 under CAP-first:
    /// <c>air=[Hilltop 12 CAP 1/1 CAS 2/2; 3RD PLATOON CAP 1/1 CAS 1/1 pre; Maris CAP 0/1 CAS 1/2
    /// cooldown]</c> — bound/wanted per wing, the CAP first because that is the demand order too,
    /// and <c>pre</c> on a sortie opened ahead of contact (user decision 2026-09-14).</summary>
    private void DescribeAir(OperationsState state, System.Text.StringBuilder into)
    {
        if (state.AirSorties.Count == 0)
        {
            return;
        }

        into.Append(" air=[");
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (i > 0)
            {
                into.Append("; ");
            }

            switch (sortie.Kind)
            {
                case CommanderSortieKind.Awacs:
                    into.Append("AWACS ").Append(sortie.Cas.Count).Append('/').Append(sortie.Wanted);
                    break;
                case CommanderSortieKind.Arad:
                    into.Append("ARAD ").Append(sortie.Label)
                        .Append(' ').Append(sortie.Cas.Count).Append('/').Append(sortie.Wanted);
                    break;
                case CommanderSortieKind.Cap:
                    into.Append("CAP ").Append(sortie.Label)
                        .Append(' ').Append(sortie.Caps.Count).Append('/').Append(sortie.CapsWanted);
                    break;
                default:
                    into.Append(sortie.Label)
                        .Append(" CAP ").Append(sortie.Caps.Count).Append('/').Append(sortie.CapsWanted)
                        .Append(" CAS ").Append(sortie.Cas.Count).Append('/').Append(sortie.Wanted);
                    break;
            }

            if (sortie.Preemptive)
            {
                into.Append(" pre");
            }

            // Section 3: a package still forming shows how much of it has reached the form-up point.
            if (SortieHoldsAtFormUp(sortie))
            {
                into.Append(" pkg ").Append(CountAtFormUp(sortie.Cas, sortie.FormUpPoint))
                    .Append('/').Append(sortie.Wanted);
                if (sortie.AradPending)
                {
                    into.Append(" arad-wait");
                }
            }

            if (sortie.Kind == CommanderSortieKind.Objective && sortie.WantsRotary)
            {
                into.Append(" rotary");
            }

            // Section 10: this sortie took an airframe off quiet cover this review.
            if (sortie.RetaskedThisReview)
            {
                into.Append(" retask");
            }

            if (Time.time < sortie.CooldownUntil)
            {
                into.Append(" cooldown");
            }
        }

        into.Append(']');
    }
}

/// <summary>What the buy loop should put in the air next for the sorties (see
/// <c>CommanderOperationsService.TryGetAirDemand</c>): CAP before CAS, user decision
/// 2026-09-13.</summary>
internal enum CommanderAirDemandKind
{
    None,

    /// <summary>A sortie is short of CAS airframes.</summary>
    Cas,

    /// <summary>A sortie is short of CAP fighters.</summary>
    Cap,

    /// <summary>The commander has no radar airframe on station (design.md,
    /// smarter-air-wing_20260914 Section 4) — rung 2's first demand, because everything below it
    /// decides on a tracking picture the AWACS is what fills in.</summary>
    Awacs,

    /// <summary>An air-defence cluster near an objective has no anti-radiation sortie over it
    /// (Section 5) — bought before that objective's CAS, which is what the cluster kills.</summary>
    Arad,
}