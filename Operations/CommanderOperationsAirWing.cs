using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The smarter air wing: sortie kinds, demand rules and ceilings. Split out of <c>CommanderOperationsAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past six thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderOperationsService
{
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

    /// <summary>How far from the commander's own main airbase the radar airframe orbits, measured
    /// along the line toward the fight. 15 km (departure 2026-09-14, user report: "I think we should
    /// have a hard limit on one AWACS and it stays near the airbase"): the orbit stays inside the
    /// air defence the base already carries, while an airborne radar sees 150 km and more past it,
    /// so the whole front is still in the picture. It replaces a 30 km stand-off measured back from
    /// the FRONT, which on a far-start map put the station in the middle of the map.</summary>
    private const float AwacsBaseOffsetMeters = 15000f;

    /// <summary>The AWACS orbit's own radius, and the whole of its station when there is nothing to
    /// aim it at (design SS4: "20 km over the main base"). A wide box on purpose — the airframe is
    /// there to look, not to hold a point.</summary>
    private const float AwacsOrbitRadiusMeters = 20000f;

    /// <summary>The narrowest the radar orbit is ever squeezed to in order to keep the whole of it
    /// outside <c>CommanderSettings.AwacsMinEnemyDistanceMeters</c> of everything hostile (user
    /// report, 2026-09-14). 8 km: still a box rather than a point, so the airframe keeps turning
    /// inside its own station instead of orbiting a waypoint at bank angle. Below it the station is
    /// not worth flying and the radar watch is held on the deck instead.</summary>
    private const float AwacsMinOrbitRadiusMeters = 8000f;

    /// <summary>How far the safety search moves the radar station each try as it slides back along
    /// the base-to-front line. 1 km: finer than the 3 km retarget hysteresis
    /// (<see cref="CasRetargetMeters"/>), so the answer never oscillates inside the hysteresis, and
    /// coarse enough that the whole search is a few dozen tries per review.</summary>
    private const float AwacsStationSlideStepMeters = 1000f;

    /// <summary>Hostile positions the radar station's safety check will ever consider in one review.
    /// The tracking database can hold hundreds of contacts on a long match and the search walks the
    /// list once per candidate station; this is the ceiling that keeps that walk bounded. The
    /// NEAREST contacts are the ones kept, which are the only ones that can bind the answer.</summary>
    private const int AwacsThreatSampleCap = 64;

    /// <summary>Slack on the radar station's clearance comparison. 1 m: the distances involved are
    /// tens of kilometres, and a 32-bit square root at that size is only good to a few centimetres,
    /// so without it a station EXACTLY at its limit reads as a hair inside it and the search slides
    /// a whole extra kilometre for nothing. One metre is below anything the rule is about.</summary>
    private const float AwacsClearanceToleranceMeters = 1f;

    /// <summary>
    /// How long a sortie whose demand has closed is kept before its airframes are let go (team
    /// lead, 2026-09-14). Two minutes — four operations reviews
    /// (<see cref="ReviewIntervalSeconds"/>). The ground line's contact clock is a 20 s memory
    /// sampled every 30 s, so a fight that is still going reads as over on most reviews: a
    /// 2026-09-14 match logged eight "no longer calls for air support" releases against five
    /// taskings in twenty minutes, and every one of them cost the wing a transit each way. Two
    /// minutes outlasts the gap between two bursts of the same engagement without pinning a wing
    /// over ground the enemy has genuinely left.
    /// </summary>
    internal const float SortieMinHoldSeconds = 120f;

    /// <summary>Radar airframes one commander ever owns at once. One (user decision 2026-09-14):
    /// a second adds nothing to a picture the first already covers past the far side of the map,
    /// and every one of them is a strike airframe the ground did not get.</summary>
    private const int AwacsPerCommander = 1;

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
    /// <summary>
    /// Why a sortie's fighters are holding back instead of flying their objective (design.md,
    /// air-survival-layer_20260916 Layer 3). One field where the posture used to have an implicit
    /// answer — "falling back means outnumbered" — because there are now two reasons and they clear
    /// on different evidence.
    /// </summary>
    internal enum CommanderAirHoldReason
    {
        /// <summary>Flying the objective.</summary>
        None,

        /// <summary>Tracked hostile fighters outnumber ours by the fallback margin.</summary>
        Outnumbered,

        /// <summary>Tracked hostile air defence covers the objective and no sweep of ours has gone
        /// in.</summary>
        Belt,
    }

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

        /// <summary>
        /// A deliberate multi-type strike package against an enemy-held control point or base
        /// (design.md, strike-packages_20260915 Section 1). The only sortie kind the commander opens
        /// on its own initiative rather than in answer to something it has been shown: one ahead of
        /// every ground attack, and one every <c>StrikeIntervalMinutes</c> while no attack is open.
        /// At most one per commander at a time.
        /// </summary>
        Strike,
    }

    /// <summary>
    /// How hard the ground a strike package is going against is (design.md,
    /// strike-packages_20260915 Section 2). Read once when the sortie is posted and never
    /// re-derived: the package that was ordered is the package that flies, or the escort count would
    /// move under the element every time a contact decayed.
    /// </summary>
    internal enum CommanderStrikeScale
    {
        /// <summary>Two defenders or fewer, no tracked air defence and no tracked hostile air.</summary>
        Light,

        /// <summary>Three defenders or more, or hostile aircraft tracked over the target.</summary>
        Defended,

        /// <summary>Tracked air defence near the target, or the target is an airbase.</summary>
        Hard,
    }

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

        /// <summary>
        /// The strike and escort counts this package's go-in test must see gathered — the numbers it
        /// was ORDERED with, frozen for as long as it is forming (user report 2026-09-16: a defended
        /// strike read <c>pkg 0/4</c>, then <c>0/6</c>, then <c>0/8</c>, because the fall-back posture
        /// raised <see cref="CapsWanted"/> every time it saw another hostile and the bar ran away from
        /// the package). Negative until the first review gives the sortie a form-up point; set through
        /// <see cref="PackageGoInBar"/>, which is where the rule and its reasons live. Carried across
        /// the review by the reconcile, like the form-up clock.
        /// </summary>
        internal int GoInCasWanted = -1;

        internal int GoInCapsWanted = -1;

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

        /// <summary>Scaled <c>Time.time</c> since this sortie's fighters have been falling back, or
        /// -1 while they hold their ground (design.md, air-fallback-posture_20260916 Section 4.2).
        /// Carried across the review by the reconcile like the form-up clock: it is a fact about the
        /// fighters in the air, not about this review's demand.</summary>
        internal float FallingBackSince = -1f;

        /// <summary>Where the falling-back fighters hold: <c>AirFallbackDistanceMeters</c> from the
        /// centre toward the nearest own airbase. Only meaningful while <see cref="FallingBack"/>.</summary>
        internal GlobalPosition FallbackPoint;

        /// <summary>The "ours v hostiles" pair last logged for this fallback, so the posture line is
        /// written on change only rather than every five seconds.</summary>
        internal string FallbackReported = string.Empty;

        /// <summary>Why this sortie is holding back, or <c>None</c> while it flies its objective
        /// (design.md, air-survival-layer_20260916 Layer 3). Set and cleared together with
        /// <see cref="FallingBackSince"/>, which is the clock the give-up test reads; carried across
        /// the review by the reconcile for the same reason that clock is.</summary>
        internal CommanderAirHoldReason HoldReason;

        /// <summary>Whether this sortie's fighters are holding back right now, for any reason.</summary>
        internal bool FallingBack => HoldReason != CommanderAirHoldReason.None;

        /// <summary>
        /// The one airframe type this package's STRIKE element flies, and the one base it is ordered
        /// from (user decision 2026-09-14: "when building mission packages, aircraft should be of the
        /// same type… and ordered at the same time from the same location"). Null until the element's
        /// first order picks them. A later top-up after a loss buys the same type from the same base
        /// for as long as that base still accepts it; when it does not, the element re-picks and both
        /// fields move together.
        /// <para>The escort element pins its own pair below, and may fly a different type: an escort
        /// is a fighter and the thing it escorts is not.</para>
        /// </summary>
        internal AircraftDefinition? PackageStrikeType;

        internal Airbase? PackageStrikeBase;

        /// <summary>The escort element's pinned type and base — <see cref="PackageStrikeType"/>'s
        /// rule applied to the fighters.</summary>
        internal AircraftDefinition? PackageCapType;

        internal Airbase? PackageCapBase;

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

        /// <summary>Tracked hostile AIR-DEFENCE vehicles in this objective's ring this review — the
        /// number the ARAD-first rule reads (user decision 2026-09-14). Counted once per review
        /// beside the rest of the sizing, so the rule, the review line and the loss cooldown all
        /// quote the same figure.</summary>
        internal int LastAirDefence;

        /// <summary>An anti-radiation sortie has been on station over this objective's belt at least
        /// once since the air-defence count rose to <see cref="AirDefenceHesitationCount"/> (user
        /// decision 2026-09-14). Until it has, this objective's close air support is flown by jets
        /// standing off, never by attack helicopters. Carried across the review by the reconcile and
        /// cleared the moment the belt thins out, so a belt that comes back has to be suppressed
        /// again.</summary>
        internal bool AradFlown;

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

        /// <summary>The radar orbit's radius for the station this sortie was given THIS review. It
        /// is <see cref="AwacsOrbitRadiusMeters"/> unless the safety search had to squeeze it to
        /// keep the whole orbit clear of the enemy, and it is what the airframe's mission area is
        /// actually written with (<see cref="SortieRadius"/>). Meaningless for every other kind.</summary>
        internal float OrbitRadiusMeters = AwacsOrbitRadiusMeters;

        /// <summary>Scaled <c>Time.time</c> of the last review that asked for this sortie. The
        /// minimum-hold hysteresis (<see cref="SortieMinHoldSeconds"/>) measures from here, so a
        /// fight whose contact clock blinks out for one review does not dissolve its whole wing.</summary>
        internal float LastDemandedAt = -1f;

        /// <summary>This sortie's demand closed and it is being kept alive by the minimum hold: it
        /// asks for nothing more, and it is the first place a fight in contact takes an airframe
        /// from. The <c>held</c> mark on the review line.</summary>
        internal bool Held;

        /// <summary>What the tasking log calls this sortie.</summary>
        internal string Label = string.Empty;

        // ---- The strike package (design.md, strike-packages_20260915) ----
        // Meaningless on every other sortie kind. They live here rather than in a record of their
        // own because a strike sortie IS a sortie: the demand walk, the binding, the package
        // form-up, the loss cooldown and the review line are all written once, for every kind
        // (design Section 6, "a new sortie kind inside the existing wing; no new service").

        /// <summary>Strike airframes this package wants — the element that actually attacks the
        /// target. Mirrored into <see cref="Wanted"/> together with <see cref="BomberWanted"/>, which
        /// is what the demand walk, the fill and the buy all read.</summary>
        internal int StrikeWanted;

        /// <summary>Escort fighters this package wants (design Section 2: never below the hostile
        /// aircraft tracked over the target or its route). Recomputed every review by
        /// <c>AddStrikeDemand</c> from <see cref="EscortFloor"/> and the live tracking picture, and
        /// mirrored into <see cref="CapsWanted"/>.</summary>
        internal int EscortWanted;

        /// <summary>The escort this package's SCALE is owed, before the hostile-air floor and the
        /// airborne ceiling are applied. Fixed when the package is ordered; the rest of the escort
        /// rule is live (<see cref="StrikeEscortWanted"/>).</summary>
        internal int EscortFloor;

        /// <summary>Reviews this package has wanted a bomber and been given none. Once it reaches
        /// <c>StrikeBomberRefusalReviews</c> the element is dropped and the package flies without
        /// one, rather than buying nothing at all for the whole of its life.</summary>
        internal int BomberRefusedReviews;

        /// <summary>Anti-radiation airframes the package's scale asks for: one for a hard target,
        /// none otherwise. The sortie itself is opened by the ordinary belt clustering
        /// (<c>InsertAradDemand</c>), so this is what says the package EXPECTS suppression.</summary>
        internal int AradWanted;

        /// <summary>Bomber-class airframes the package wants — only against a base, and only when
        /// the roster holds a bomber-class type at all.</summary>
        internal int BomberWanted;

        /// <summary>How hard the target read when the package was ordered.</summary>
        internal CommanderStrikeScale StrikeScale = CommanderStrikeScale.Light;

        /// <summary>Hostile ground units observed over the target when the package was ordered, so
        /// the closing line can say how many of them are gone.</summary>
        internal int DefendersAtOrder;

        /// <summary>Scaled <c>Time.time</c> the strike sortie was opened, or negative on every other
        /// kind. The sortie's own maximum life runs from here.</summary>
        internal float OpenedAt = -1f;

        /// <summary>Scaled <c>Time.time</c> the package went in, or negative while it has not. The
        /// escorts' loiter runs from here.</summary>
        internal float WentInAt = -1f;

        /// <summary>The package has gone in AND a strike airframe has reached the target ring —
        /// "strike delivered", which is what the ground attack's go-in waits for (design
        /// Section 5). Never unset once set: the attack behind it must not be made to wait twice.</summary>
        internal bool Delivered;

        /// <summary>The control point this strike is against, or null when it is against a base.</summary>
        internal CommanderStrategicPoint? Point;

        /// <summary>The airbase this strike is against, or null when it is against a point.</summary>
        internal Airbase? TargetAirbase;

        /// <summary>The bomber element's pinned type and base — <see cref="PackageStrikeType"/>'s
        /// rule applied to the bombers, which are a different element and so a different type from
        /// the strike airframes beside them.</summary>
        internal AircraftDefinition? PackageBomberType;

        internal Airbase? PackageBomberBase;

        /// <summary>
        /// Which station band this sortie's CAP flies (design Section 4): an index into the three
        /// bands, not a height — the heights themselves are settings. -1 until a band is assigned,
        /// which is every sortie that has never wanted a fighter.
        /// </summary>
        internal int CapBand = -1;
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
            AircraftDefinition definition,
            Airbase origin,
            GlobalPosition? objective,
            float expiresAt,
            CommanderEnemyCommanderService.AirRole role,
            bool forHomeCap)
        {
            Definition = definition;
            Origin = origin;
            Objective = objective;
            ExpiresAt = expiresAt;
            Role = role;
            ForHomeCap = forHomeCap;
        }

        /// <summary>The role the buy chose the airframe for, so the claim can count the launch on
        /// the right side of the attrition ledger (user decision 2026-09-15,
        /// <c>CommanderEnemyCommanderService.RecordAttritionLaunch</c>).</summary>
        internal CommanderEnemyCommanderService.AirRole Role { get; }

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

    /// <summary>
    /// How many aircraft ONE element of a sortie wants right now — the shape every element of every
    /// sortie shares (user decision 2026-09-16: "every sortie's size scales with what it faces").
    /// Never below the element's own floor, never below what its own growth term asked for, never
    /// above its own cap; and the cap never cuts BELOW the floor, because a floor is a promise and a
    /// cap is only a ceiling. A cap of zero or less means the element has no cap of its own.
    /// <para>
    /// Generalised from <see cref="StrikeEscortWanted"/> (Reuse rule 5) and retrofitted onto the
    /// patrol ladder, the close-air-support ladder and the falling-back sortie's call for help, so
    /// all four read one envelope and differ only in the growth term they hand it. The retrofits are
    /// behaviour-neutral; the call for help is the one that gains a cap, and it gains it because the
    /// 2026-09-16 session had sorties asking for a median of eleven fighters and one asking for 31.
    /// </para>
    /// The room left under the airborne ceiling is deliberately NOT part of this rule. It belongs to
    /// the escort, whose caller applies it, because an escort that cannot be bought is not worth
    /// wanting — but a sortie's demand also drives the RETASK, which moves aircraft already airborne
    /// and already under the ceiling, so clamping a patrol by headroom would stop a contested
    /// objective drawing fighters off quiet ones at exactly the moment the sky is full. Pure, for the
    /// self-check.
    /// </summary>
    internal static int SortieElementWanted(int floor, int grown, int cap)
    {
        int low = Mathf.Max(0, floor);
        int wanted = Mathf.Max(low, Mathf.Max(0, grown));
        return cap > 0 ? Mathf.Max(low, Mathf.Min(wanted, cap)) : wanted;
    }

    /// <summary>The CAS ladder (design SS1): observed hostiles at the objective → airframes. The
    /// step ladder is this element's growth term; the floor of none and the per-objective cap are
    /// the shared envelope's (<see cref="SortieElementWanted"/>). Pure, for the self-check.</summary>
    internal static int CasWanted(int observed)
    {
        return SortieElementWanted(0, CasLadder(observed), CasPerObjectiveCap);
    }

    /// <summary>The close-air-support growth term alone: observed hostiles → airframes, before the
    /// envelope's floor and cap. Split out of <see cref="CasWanted"/> on 2026-09-16 so the ladder and
    /// the envelope are separately readable; the numbers are unchanged.</summary>
    private static int CasLadder(int observed)
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
    /// <see cref="CapPerObjectiveCap"/>. The baseline is the envelope's floor and the growth term
    /// both, because an active objective is owed its fighter whatever is tracked. Pure, for the
    /// self-check.</summary>
    internal static int CapWanted(int hostileAirTrackedInRing)
    {
        return SortieElementWanted(
            CapBaselinePerObjective,
            CapBaselinePerObjective + Mathf.Max(0, hostileAirTrackedInRing),
            CapPerObjectiveCap);
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
    /// Whether a sortie gathers at a form-up point before it goes in (design SS3, widened to every
    /// sortie kind on the user's decision of 2026-09-16: "every sortie kind goes in whole"). Was
    /// <c>SortieIsPackage</c>, which asked the same question of a strike package only; one
    /// definition with more callers, not a second rule.
    /// <para>
    /// A sortie forms up when it wants more than one aircraft — a fighter patrol, a ground-attack
    /// sortie and an escort alike, because feeding fighters into a fight one at a time is how the
    /// 2026-09-16 match lost every aircraft of ninety-six sorties. Four things are exempt by their
    /// nature: the radar aeroplane, which never fights; a suppression sortie that has already gone
    /// in, which is over the belt and cannot go back and wait; a sortie whose objective is in GROUND
    /// contact, because a platoon being shot at now cannot wait three minutes for a formation; and a
    /// single-aircraft sortie, which has nobody to form up with.
    /// </para>
    /// <para>
    /// Hostile AIRCRAFT near the objective are deliberately NOT an exemption, though ground contact
    /// is. Arriving together is what facing enemy fighters demands, not a reason to trickle in; and
    /// <see cref="SortieIsInContact"/> reads true whenever anything hostile is tracked in the ring,
    /// so exempting on it would switch this rule off in precisely the fights it exists for.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static bool SortieFormsUp(
        CommanderSortieKind kind, int casWanted, int capWanted, bool groundContact, bool aradGoneIn)
    {
        if (kind == CommanderSortieKind.Awacs)
        {
            return false;
        }

        if (kind == CommanderSortieKind.Arad && aradGoneIn)
        {
            return false;
        }

        if (groundContact)
        {
            return false;
        }

        return Mathf.Max(0, casWanted) + Mathf.Max(0, capWanted) > 1;
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
    /// The number of aircraft of one element a forming package must gather before it goes in, pure
    /// (user report 2026-09-16). The number the package was ORDERED with, frozen while it forms: a
    /// fall-back is free to call for all the reinforcements it likes, but it must not move the target
    /// the package has to reach, or the bar grows away from the package every time another hostile is
    /// tracked. A demand that has since SHRUNK does lower the bar, because waiting for aircraft the
    /// wing will no longer buy is the same stall by another route. A negative
    /// <paramref name="ordered"/> means this is the first review to give the package a bar, and it
    /// takes the demand as it stands.
    /// <para>
    /// A genuine top-up after a loss is untouched. The bar counts aircraft AT the gathering point, so
    /// an escort shot down drops the count back below the frozen bar and the package waits again —
    /// and the replacement is still ordered, because the sizing, the retask and the buy all read
    /// <see cref="CommanderAirSortie.CapsWanted"/>, which the fall-back may raise as far as it needs.
    /// </para>
    /// </summary>
    internal static int PackageGoInBar(int ordered, int wantedNow)
    {
        int now = Mathf.Max(0, wantedNow);
        return ordered < 0 ? now : Mathf.Min(ordered, now);
    }

    /// <summary>
    /// Whether a package that has GATHERED actually launches this review, pure: never while its
    /// escort is still being held back (design.md, air-fallback-posture_20260916 Section 4.3).
    /// Gathering at the retreat point and going in from it are two different things — the package
    /// assembles while it is held and leaves on the first review after the hold clears.
    /// </summary>
    internal static bool PackageLaunches(bool gathered, bool fallingBack)
    {
        return gathered && !fallingBack;
    }

    /// <summary>
    /// Anti-radiation airframes one cluster of tracked hostile air-defence vehicles is owed (design
    /// SS5): none below <paramref name="minimum"/>, one for a small belt, two once the belt is
    /// <see cref="AradSecondAirframeCount"/> emitters or more. Pure, for the self-check.
    /// <para>"Is this a belt at all" is <see cref="BeltWorthSuppressing"/>, in
    /// <c>CommanderOperationsAirArad.cs</c>: the sortie posture's belt hold asks the same question of
    /// the same numbers, so the sweep and the hold can never disagree about what a belt is (one
    /// definition, two callers; user decision 2026-09-16).</para>
    /// </summary>
    internal static int AradWanted(int clusterSize, int minimum)
    {
        if (!BeltWorthSuppressing(clusterSize, minimum))
        {
            return 0;
        }

        return clusterSize >= AradSecondAirframeCount ? 2 : 1;
    }

    /// <summary>
    /// How far from the main airbase the AWACS station sits, along the line toward whatever it is
    /// aimed at (design SS4, departure 2026-09-14): the 15 km offset, never so far out that it
    /// passes the thing it is aimed at. With nothing to aim at — no front and no enemy asset known
    /// — the station is the airbase itself and the answer is zero, so the 20 km of "20 km over the
    /// main base" is the orbit (<see cref="AwacsOrbitRadiusMeters"/>), not an offset. Pure, for the
    /// self-check.
    /// </summary>
    internal static float AwacsStandoffMeters(bool hasAim, float baseToAimMeters)
    {
        if (!hasAim)
        {
            return 0f;
        }

        return Mathf.Clamp(AwacsBaseOffsetMeters, 0f, Mathf.Max(0f, baseToAimMeters));
    }

    /// <summary>
    /// Where the radar airframe may actually orbit, once everything hostile the commander can see is
    /// taken into account (user report, 2026-09-14: "an AWACS was just tasked straight into the
    /// enemy and killed because the front-line was close to the airbase. AWACS should never be
    /// tasked closer than 15 km to the enemy").
    /// <para>
    /// The candidate station is the base offset <paramref name="candidateOffsetMeters"/> along
    /// <paramref name="towardFront"/> (<see cref="AwacsStandoffMeters"/> decides that offset). The
    /// WHOLE orbit has to clear every hostile position by
    /// <paramref name="minEnemyDistanceMeters"/>, so the centre has to stand at least that distance
    /// plus the orbit's own radius from all of them. Three remedies, in order:
    /// </para>
    /// <list type="number">
    /// <item>slide the station back along the same line toward the base, and on past it away from
    /// the front — never further from the base than the forward offset it started from
    /// (<see cref="AwacsBaseOffsetMeters"/>), because the whole point of the 2026-09-14 design is
    /// that the station stays NEAR the airbase;</item>
    /// <item>only if no offset on that line works at the full orbit, squeeze the orbit — down to
    /// <see cref="AwacsMinOrbitRadiusMeters"/> and no further;</item>
    /// <item>give up: false, and the caller holds the radar watch on the deck rather than launching
    /// into the fight.</item>
    /// </list>
    /// Sliding is tried before squeezing because a full-width orbit in the right place sees more
    /// than a pinched one in the wrong place. Pure, for the self-check.
    /// </summary>
    /// <param name="towardFront">The direction the station is offset in; any length, normalised
    /// here. Zero means there was nothing to aim at, and the search then only ever considers the
    /// base itself.</param>
    /// <param name="offsetMeters">How far along <paramref name="towardFront"/> the station ended up.
    /// Negative means behind the base, away from the front.</param>
    /// <param name="orbitMeters">The orbit radius the station was granted — the one asked for
    /// unless remedy 2 had to squeeze it.</param>
    internal static bool TryAwacsStation(
        Vector2 basePoint,
        Vector2 towardFront,
        float candidateOffsetMeters,
        IReadOnlyList<Vector2> enemyPositions,
        float minEnemyDistanceMeters,
        float orbitRadiusMeters,
        out float offsetMeters,
        out float orbitMeters)
    {
        offsetMeters = candidateOffsetMeters;
        orbitMeters = orbitRadiusMeters;

        Vector2 direction = towardFront.sqrMagnitude > 0f ? towardFront.normalized : Vector2.zero;
        bool haveSqueezed = false;
        float squeezedOffset = 0f;
        float squeezedOrbit = 0f;

        for (float offset = candidateOffsetMeters;
            offset >= -AwacsBaseOffsetMeters;
            offset -= AwacsStationSlideStepMeters)
        {
            // The widest orbit this station could carry and still stand the required distance off
            // everything hostile. Negative means the centre itself is already too close.
            float allowedOrbit = NearestEnemyMeters(basePoint + (direction * offset), enemyPositions)
                - minEnemyDistanceMeters;

            // The tolerance is on the COMPARISON only, never on the radius granted: a station at
            // exactly its limit has to read as compliant, and it still flies the orbit it earned.
            if (allowedOrbit + AwacsClearanceToleranceMeters >= orbitRadiusMeters)
            {
                offsetMeters = offset;
                orbitMeters = orbitRadiusMeters;
                return true;
            }

            // Remembered, not taken: a full-width orbit further back still beats a squeezed one
            // here, so the slide is allowed to run out first.
            if (!haveSqueezed && allowedOrbit + AwacsClearanceToleranceMeters >= AwacsMinOrbitRadiusMeters)
            {
                haveSqueezed = true;
                squeezedOffset = offset;
                squeezedOrbit = Mathf.Clamp(allowedOrbit, AwacsMinOrbitRadiusMeters, orbitRadiusMeters);
            }
        }

        if (haveSqueezed)
        {
            offsetMeters = squeezedOffset;
            orbitMeters = squeezedOrbit;
            return true;
        }

        return false;
    }

    /// <summary>Distance from <paramref name="station"/> to the nearest hostile position, or
    /// <c>float.MaxValue</c> when the commander can see nothing at all. Pure, for
    /// <see cref="TryAwacsStation"/>.</summary>
    private static float NearestEnemyMeters(Vector2 station, IReadOnlyList<Vector2> enemyPositions)
    {
        float best = float.MaxValue;
        for (int i = 0; i < enemyPositions.Count; i++)
        {
            float distance = Vector2.Distance(station, enemyPositions[i]);
            if (distance < best)
            {
                best = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether a sortie whose demand closed this review may let its airframes go yet (team lead,
    /// 2026-09-14). A holdable sortie is kept for <paramref name="holdSeconds"/> past the last
    /// review that asked for it; anything else goes the moment its demand does.
    /// <paramref name="secondsSinceLastDemand"/> is negative for a sortie that was never demanded at
    /// all, which is released immediately. Pure, for the self-check.
    /// </summary>
    internal static bool MayReleaseSortie(bool holdable, float secondsSinceLastDemand, float holdSeconds)
    {
        return !holdable || secondsSinceLastDemand < 0f || secondsSinceLastDemand >= holdSeconds;
    }

    /// <summary>
    /// Which sortie kinds the minimum hold applies to. The two that FLAP: a CAS objective and a
    /// platoon CAP both open and close on the tracking picture, which blinks. The radar watch closes
    /// only when the commander owns its airframe or the station is unsafe, and a suppression sortie
    /// closes when the belt it was opened on is gone — neither is a flap, and holding either would
    /// keep an airframe over something that is genuinely finished. Pure, for the self-check.
    /// </summary>
    internal static bool SortieIsHoldable(CommanderSortieKind kind)
    {
        return kind == CommanderSortieKind.Objective || kind == CommanderSortieKind.Cap;
    }

    /// <summary>What becomes of an airframe whose sortie has just dissolved.</summary>
    internal enum CommanderReleaseDisposal
    {
        /// <summary>Another open sortie is short of a slot it can fill, so it goes straight there.</summary>
        Retask,

        /// <summary>Nothing wants it and it is a fighter, so it holds the standing home patrol.</summary>
        HomeCap,

        /// <summary>Nothing wants it and it cannot fly the patrol — a helicopter or a ground-attack
        /// specialist — so it goes to the nearest pad or strip.</summary>
        ReturnToBase,
    }

    /// <summary>
    /// The release order (team lead, 2026-09-14). A retask always wins: an airframe already airborne
    /// and already paid for is the cheapest answer another sortie will get. Only what nothing wants
    /// is disposed of, and then the patrol is for aeroplanes that can actually fly it —
    /// <c>MayHoldPatrol</c> refuses a helicopter and a ground-attack specialist alike, which is
    /// exactly the case that left a released SAH-46 Chicane with no task at all. Pure, for the
    /// self-check.
    /// </summary>
    internal static CommanderReleaseDisposal ReleaseDisposal(bool somethingElseWantsIt, bool mayHoldPatrol)
    {
        if (somethingElseWantsIt)
        {
            return CommanderReleaseDisposal.Retask;
        }

        return mayHoldPatrol ? CommanderReleaseDisposal.HomeCap : CommanderReleaseDisposal.ReturnToBase;
    }

    /// <summary>
    /// How many radar airframes the commander may still buy, given how many it already owns. The
    /// hard limit is one per commander (<see cref="AwacsPerCommander"/>) and it counts every owned
    /// radar airframe that is still alive in ANY state — on station, on its way out, turning for
    /// home, on the deck — so the slot never reopens while one exists. Before this rule the count
    /// was "airframes currently BOUND to the AWACS sortie", and an airframe that unbound itself to
    /// go home reopened the slot immediately: the commander bought a second radar aeroplane while
    /// the first was still in the air (user report, 2026-09-14). A replacement is bought only once
    /// the airframe is actually gone, and then only after
    /// <see cref="AwacsLossCooldownMinutes"/>. Pure, for the self-check.
    /// </summary>
    internal static int AwacsWanted(int ownedRadarAirframes)
    {
        return Mathf.Max(0, AwacsPerCommander - Mathf.Max(0, ownedRadarAirframes));
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
    /// How many home-CAP fighters may be lent forward while the base is quiet: all of them (user
    /// decision 2026-09-16: "remove the minimum Home CAP - they should all be reassignable by
    /// something with higher priority"), never below zero. Until 2026-09-16 this was alive minus a
    /// minimum of one the base always kept; the self-check now pins the opposite, so a floor cannot
    /// come back without the console saying so. The one definition the loan and the recall both
    /// read. Pure, for the self-check.
    /// </summary>
    internal static int LendableHomeCap(int alive)
    {
        return Mathf.Max(0, alive);
    }

    /// <summary>
    /// The home patrol's own in-contact read, and the ONLY thing that still holds a fighter at the
    /// base: nothing hostile tracked inside the base ring right now. A base with hostile air tracked
    /// over it is the patrol in contact, and an in-contact holder is never a retask source
    /// (Section 10); the moment the ring is clear the patrol is the lowest-priority holder of
    /// fighters there is and lends at once. Two older gates were removed on 2026-09-16 (user
    /// decision: "remove the minimum Home CAP - they should all be reassignable by something with
    /// higher priority"): the minimum of one the base always kept (<c>HomeCapMinimumHeld</c>,
    /// 2026-09-14) and the four-review (120 s) quiet clock (<c>HomeCapQuietSeconds</c>, 2026-09-14),
    /// which made a higher-priority demand wait two minutes after the ring cleared. The clock's
    /// worry — a track lost for one review reading as "the raid is over" — is met by the recall
    /// instead: the loan comes home the review the hostile is tracked again. Pure, for the
    /// self-check.
    /// </summary>
    internal static bool HomeCapIsQuiet(int trackedNearBases)
    {
        return trackedNearBases <= 0;
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
    /// The same read with the sortie's KIND in front of it (fix, 2026-09-15). A deliberate strike
    /// package is never in contact, whatever is observed over the point it is flying against.
    /// <para>
    /// "In contact" is not a description here, it is a claim on money: <c>HasInContactAirShortfall</c>
    /// sets a third of the priority ladder's rung aside for the wing while anything in contact is
    /// short of an airframe, and that reserve exists for the men who are already being shot at. A
    /// strike is aimed at ground nobody of ours is standing on, and it lives up to twelve minutes, so
    /// reading it as contact took that third away from the platoons for the whole of every package —
    /// and the flag was doing no work besides, since the retask rules refuse a strike package as a
    /// SOURCE by its kind and take it as a target off its unfilled slots.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static bool SortieIsInContact(
        CommanderSortieKind kind, bool objectiveInContact, bool attackGoneIn, int observed, int hostileAir)
    {
        if (kind == CommanderSortieKind.Strike)
        {
            return false;
        }

        return SortieIsInContact(objectiveInContact, attackGoneIn, observed, hostileAir);
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
    /// <param name="preferCas">Set on the CAS side's turn of the buy alternation (fix, 2026-09-14,
    /// <see cref="PrefersCasThisBuy"/>): the escort gives way to the sortie's own CAS for that one
    /// binding, so a strike airframe bought on a CAS turn has somewhere to go. Every other caller
    /// leaves it false and keeps the escort-first order exactly as Approval 1 wrote it.</param>
    internal static CommanderAirSlot NextSortieSlot(
        int capsBound, int capsWanted, int casBound, int casWanted, bool preferCas = false)
    {
        if (preferCas && casBound < casWanted)
        {
            return CommanderAirSlot.Cas;
        }

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

    /// <summary>
    /// Which side of the wing this buy serves when both are asking, pure (fix, 2026-09-14): the
    /// fighters and the ground-attack airframes take turns, one buy each.
    /// <para>"CAP first" (user decision 2026-09-13) was written as an absolute, and with tens of
    /// objectives open it stopped being a tie-break and became a permanent veto: every sortie that
    /// wants CAS wants an escort BEFORE it (see <see cref="NextSortieSlot"/>), and every platoon
    /// with a hostile aeroplane over it opens a CAP-only sortie of its own, so some CAP slot was
    /// open on every single review of the 2026-09-14 match and the CAS side was never reached. Worse,
    /// a CAP demand the roster cannot fill at all — the commander whose strips "accept no
    /// air-to-air-capable airframe" — blocked the CAS side for the whole match while spending
    /// nothing. Alternating keeps the intent (a contested sortie gets its escort early) and bounds
    /// the damage to one buy.</para>
    /// The AWACS keeps its outright priority above this; it is neither side's turn.
    /// </summary>
    internal static bool PrefersCasThisBuy(bool capOpen, bool casOpen, bool lastBuyWasCas)
    {
        if (!capOpen)
        {
            return casOpen;
        }

        return casOpen && !lastBuyWasCas;
    }

    /// <summary>What the buy loop should put in the air first when the wing is short of both: the
    /// side whose turn it is (<see cref="PrefersCasThisBuy"/> decides, and the caller's
    /// <paramref name="preferCas"/> carries the answer). ARAD travels with the CAS side — it is
    /// suppression for the CAS behind it — and is served before that CAS either way. Pure, for the
    /// self-check; the demand walk gathers the shortfalls and reads its answer through here.</summary>
    internal static CommanderAirDemandKind NextAirDemand(
        bool awacsShort,
        bool capShort,
        bool aradShort,
        bool casShort,
        bool preferCas = false,
        bool awacsAffordable = true,
        bool capAffordable = true)
    {
        if (!preferCas)
        {
            // The CAP side's turn, in its own order. AWACS first (design SS4: rung 2, immediately
            // after the CAP baseline): every sortie below it is sized from the tracking picture,
            // and the radar airframe is what fills that in. Then the fighters (user decision
            // 2026-09-13: CAP first).
            // ... except when the radar airframe is out of reach this review and a fighter is not
            // (fix, 2026-09-14). The radar airframe is the dearest thing the wing ever wants, and
            // the ground-attack side spends the shared fund every review, so saving for it never
            // finished: the 2026-09-14 `Ground Control Duel Far` match refused it 51 times, and the
            // fighter demand behind it was bound `CAP 0/N` on EVERY review of the match — not one
            // platoon escort or point patrol was ever bought. An unaffordable radar airframe now
            // stands aside for a fighter the wing CAN buy, and keeps the turn when no fighter demand
            // is open OR when the fighter is out of reach too — both are reviews in which saving for
            // the radar airframe costs the wing nothing, and standing aside for a fighter that
            // cannot be bought either would burn the turn and save nothing.
            if (awacsShort && (awacsAffordable || !capShort || !capAffordable))
            {
                return CommanderAirDemandKind.Awacs;
            }

            if (capShort)
            {
                return CommanderAirDemandKind.Cap;
            }
        }
        else
        {
            // The CAS side's turn: suppression before the CAS it is clearing the way for
            // (design SS5), then the CAS itself.
            if (aradShort)
            {
                return CommanderAirDemandKind.Arad;
            }

            if (casShort)
            {
                return CommanderAirDemandKind.Cas;
            }
        }

        // The side whose turn it was has nothing on it after all, so the other side takes the buy
        // rather than the wing buying nothing. The AWACS sits on the CAP side of this: it is a
        // supporting airframe, not a ground-attack one, and it used to outrank the whole queue
        // unconditionally — which is how the 2026-09-14 match logged nine "saves for one rather
        // than launching the last-resort airframe" refusals for the radar airframe, bought fourteen
        // fighters, and bought exactly one strike airframe in the whole match. The AWACS is worth
        // saving for; it is not worth every airframe the ground was asking for.
        if (aradShort)
        {
            return CommanderAirDemandKind.Arad;
        }

        if (casShort)
        {
            return CommanderAirDemandKind.Cas;
        }

        if (awacsShort)
        {
            return CommanderAirDemandKind.Awacs;
        }

        return capShort ? CommanderAirDemandKind.Cap : CommanderAirDemandKind.None;
    }

    /// <summary>Replacement cooldown after a loss: the base minutes, doubled while the objective's
    /// ring shows <see cref="AirDefenceHesitationCount"/> or more tracked hostile air-defence
    /// units. Pure, for the self-check.</summary>
    internal static float CasLossCooldownMinutes(int observedAirDefence, float baseMinutes)
    {
        return observedAirDefence >= AirDefenceHesitationCount ? baseMinutes * 2f : baseMinutes;
    }

    /// <summary>
    /// Whether an objective's close air support may be flown by ATTACK HELICOPTERS yet (user
    /// decision 2026-09-14). A belt of <see cref="AirDefenceHesitationCount"/> or more tracked
    /// air-defence vehicles holds the helicopters on the ground until an anti-radiation sortie has
    /// actually been on station over it: a helicopter in a live engagement envelope is a write-off,
    /// and the 2026-09-14 match fed cheap SAH-46 Chicanes into seven to twenty-three observed
    /// launchers until every sortie on the review line read `cooldown`. Below the threshold, or once
    /// the suppression sortie has flown, the ordinary choice stands.
    /// <para>The objective still gets CAS meanwhile — jets, which stand off. This gate decides who
    /// flies it, never whether it is flown.</para>
    /// Pure, for the self-check.
    /// </summary>
    internal static bool RotaryCasAllowed(int observedAirDefence, bool aradFlown)
    {
        return observedAirDefence < AirDefenceHesitationCount || aradFlown;
    }

    /// <summary>
    /// Whether an objective's "a suppression sortie has flown here" memory survives into the next
    /// review: only while the belt is still there. A belt that has thinned below the hesitation
    /// count and comes back is a new belt, and the helicopters wait for a new suppression sortie --
    /// which is what "since the count rose" means in a rule that is evaluated once a review.
    /// Pure, for the self-check.
    /// </summary>
    internal static bool AradMemorySurvives(bool aradFlown, int observedAirDefence)
    {
        return aradFlown && observedAirDefence >= AirDefenceHesitationCount;
    }

    /// <summary>
    /// How long the wing waits before buying another radar airframe after losing one: ten minutes,
    /// the same wait a picket point takes after losing an insertion flight
    /// (<c>OperationsHeliInsertionCooldownMinutes</c>), and for the same reason — a loss is
    /// evidence about the sky, not bad luck, and the replacement is the most expensive airframe the
    /// wing buys. Without it the AWACS demand reopened the instant the aeroplane died and the
    /// commander went straight back to saving its whole air fund for another one.
    /// </summary>
    internal const float AwacsLossCooldownMinutes = 10f;

    /// <summary>The replacement cooldown for any sortie, pure: the radar airframe's own flat wait,
    /// every other sortie's air-defence-sensitive one. Pure, for the self-check.</summary>
    internal static float SortieLossCooldownMinutes(
        bool awacs, int observedAirDefence, float baseMinutes, float awacsMinutes)
    {
        return awacs ? awacsMinutes : CasLossCooldownMinutes(observedAirDefence, baseMinutes);
    }

    /// <summary>Launch lead time: how long a freshly launched airframe needs to reach the
    /// objective at its nominal transit speed (design Approval 1). Pure, for the self-check.</summary>
    internal static float CasTransitSeconds(float distanceMeters, bool rotary)
    {
        return distanceMeters / Mathf.Max(1f, rotary ? RotaryTransitMetersPerSecond : JetTransitMetersPerSecond);
    }

    // ---- CAP station bands (design.md, strike-packages_20260915 Section 4) ----

    /// <summary>
    /// How many station bands the rotation walks. Three (low, medium, high): two would give a wing
    /// of any size only one height above and one below, and four would put a band inside the
    /// station-keeping slop of its neighbour. The heights themselves are settings
    /// (<c>CommanderSettings.CapBandLowMeters</c> and its pair), because a map with different
    /// air-defence belts wants different heights; how many of them there are is structural.
    /// </summary>
    internal const int CapBandCount = 3;

    /// <summary>The next band in the rotation (design Section 4): 0 → 1 → 2 → 0. A negative
    /// <paramref name="previous"/> — nothing has been assigned yet — starts at the bottom. Pure, for
    /// the self-check.</summary>
    internal static int NextCapBand(int previous, int bands)
    {
        int count = Mathf.Max(1, bands);
        if (previous < 0)
        {
            return 0;
        }

        return (previous + 1) % count;
    }

    /// <summary>The height one band stands for, in metres above ground. An index outside the table
    /// reads as the middle band, which is the one a patrol with nothing said about it should fly.
    /// Pure, for the self-check.</summary>
    internal static float CapBandMeters(int band, float low, float mid, float high)
    {
        return band switch
        {
            0 => low,
            2 => high,
            _ => mid,
        };
    }

    /// <summary>
    /// Which of the strike package's two ground-attack elements the next airframe fills (design.md,
    /// strike-packages_20260915 Section 3): the bombers are ordered before the strike airframes,
    /// because they are the element a base is being attacked FOR and the one the roster is least
    /// likely to be able to fill. The bomber element is counted from the top of the CAS slots, so a
    /// package of two strike airframes and one bomber reads BOMBER, STRIKE, STRIKE.
    /// </summary>
    private static CommanderEnemyCommanderService.ElementKind StrikeElementFor(CommanderAirSortie sortie)
    {
        return sortie.Cas.Count < sortie.BomberWanted
            ? CommanderEnemyCommanderService.ElementKind.Bomber
            : CommanderEnemyCommanderService.ElementKind.Strike;
    }

    /// <summary>
    /// Gives a sortie the next band from one rotation, once, when it is first opened. A sortie
    /// matched onto an existing one keeps the band it already had (the reconcile carries it across),
    /// so a patrol is never moved up and down every 30 s.
    /// <para>
    /// The cursor is passed by reference because there are two of them (fix, 2026-09-15). The
    /// ordinary patrols share one; the deliberate strike packages have their own, so that
    /// CONSECUTIVE STRIKES are guaranteed to fly different heights. Sharing a single cursor made a
    /// strike's band depend on how many platoon patrols happened to open between one strike and the
    /// next, and with three bands any multiple of three in between put two strikes at the same
    /// height — which is what a reader sees as "the rotation is broken" whether or not it is.
    /// </para>
    /// </summary>
    private static void AssignCapBand(ref int cursor, CommanderAirSortie sortie)
    {
        cursor = NextCapBand(cursor, CapBandCount);
        sortie.CapBand = cursor;
    }

    /// <summary>The height a band index stands for, read off this commander's live settings — the
    /// one place the three settings are turned into a number (Reuse rule 4).</summary>
    internal static float CapBandMetersFor(int band)
    {
        return CapBandMeters(
            band,
            CommanderSettings.CapBandLowMeters,
            CommanderSettings.CapBandMidMeters,
            CommanderSettings.CapBandHighMeters);
    }

    /// <summary>The station altitude a sortie's CAP is given: its band's height, or nothing at all
    /// for a sortie that was never assigned one — which leaves the mission at the game's own
    /// standard height exactly as it was before the bands existed.</summary>
    private static float SortieCapAltitude(CommanderAirSortie sortie)
    {
        return sortie.CapBand < 0 ? 0f : CapBandMetersFor(sortie.CapBand);
    }

    /// <summary>
    /// The station altitude one home-CAP fighter flies. The band is remembered per airframe rather
    /// than rotated on every task: the standing patrol is re-issued its box whenever the posture
    /// runs, and a height that changed every review would have the fighter climbing and descending
    /// instead of patrolling.
    /// </summary>
    private static float HomeCapAltitude(OperationsState state, Aircraft aircraft)
    {
        if (!state.HomeCapBand.TryGetValue(aircraft, out int band))
        {
            state.CapBandCursor = NextCapBand(state.CapBandCursor, CapBandCount);
            band = state.CapBandCursor;
            state.HomeCapBand[aircraft] = band;
        }

        return CapBandMetersFor(band);
    }

    /// <summary>The band rotation and the heights it maps to. A rotation that stopped rotating, or a
    /// table that gave every band the same height, would put the whole wing back on one altitude —
    /// which is the thing this section exists to fix, and which nothing in the running game would
    /// report.</summary>
    private static void CheckCapBands(List<string> failures)
    {
        Expect(failures, "the first band assigned is the lowest", NextCapBand(-1, CapBandCount), 0);
        Expect(failures, "the low band is followed by the middle one", NextCapBand(0, CapBandCount), 1);
        Expect(failures, "the middle band is followed by the high one", NextCapBand(1, CapBandCount), 2);
        Expect(failures, "the high band wraps back to the low one", NextCapBand(2, CapBandCount), 0);
        Expect(failures, "a single-band rotation never leaves its one band", NextCapBand(0, 1), 0);
        Expect(failures, "a rotation of no bands at all is still an index, never a divide by zero", NextCapBand(0, 0), 0);

        float low = CommanderSettings.CapBandLowMeters;
        float mid = CommanderSettings.CapBandMidMeters;
        float high = CommanderSettings.CapBandHighMeters;
        Expect(failures, "band 0 is the low station", CapBandMeters(0, low, mid, high), low);
        Expect(failures, "band 1 is the medium station", CapBandMeters(1, low, mid, high), mid);
        Expect(failures, "band 2 is the high station", CapBandMeters(2, low, mid, high), high);
        Expect(failures, "an unassigned band flies the ordinary middle station", CapBandMeters(-1, low, mid, high), mid);
        Expect(failures, "a band past the top of the table still flies a real height", CapBandMeters(9, low, mid, high), mid);

        // The rotation has to MOVE, or every sortie flies the same height (fix, 2026-09-15: four
        // strike orders in a row at 7,500 m). Two consecutive assignments never share a band, and
        // three consecutive ones cover the whole table.
        Expect(failures, "two consecutive bands are never the same", NextCapBand(0, CapBandCount) != 0, true);
        Expect(failures, "nor are the next two", NextCapBand(1, CapBandCount) != 1, true);
        Expect(failures, "nor the pair that wraps", NextCapBand(2, CapBandCount) != 2, true);
        int first = NextCapBand(-1, CapBandCount);
        int second = NextCapBand(first, CapBandCount);
        int third = NextCapBand(second, CapBandCount);
        Expect(failures, "the first two sorties of a match fly different heights", first != second, true);
        Expect(failures, "so do the second and the third", second != third, true);
        Expect(failures, "and the first and the third", first != third, true);
        Expect(
            failures,
            "three consecutive sorties cover every band the settings name",
            CapBandMetersFor(first) + CapBandMetersFor(second) + CapBandMetersFor(third),
            CommanderSettings.CapBandLowMeters + CommanderSettings.CapBandMidMeters + CommanderSettings.CapBandHighMeters);

        Expect(failures, "the low station is below the medium one; check the Operations section of the config", low < mid, true);
        Expect(failures, "the medium station is below the high one; check the Operations section of the config", mid < high, true);
        Expect(failures, "the low station is above the ground; check the Operations section of the config", low > 0f, true);
        Expect(failures, "the rotation has a band for every height the settings name", CapBandCount, 3);
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

    /// <summary>
    /// The shared sizing envelope at its named boundaries, and proof that retrofitting the two
    /// ladders onto it changed nothing (user decision 2026-09-16). Every one of these numbers can be
    /// retuned into a sortie that asks for nothing, or for the whole wing, and nothing in the
    /// running game would say so until a playtest watched it happen.
    /// </summary>
    private static void CheckSortieSizing(List<string> failures)
    {
        // Which sorties gather before they go in (user decision 2026-09-16: every kind, not just
        // the strike package).
        Expect(failures, "two ground-attack airframes form up", SortieFormsUp(CommanderSortieKind.Objective, 2, 0, false, false), true);
        Expect(failures, "one ground-attack airframe with an escort forms up", SortieFormsUp(CommanderSortieKind.Objective, 1, 1, false, false), true);
        Expect(failures, "one unescorted airframe has nobody to form up with", SortieFormsUp(CommanderSortieKind.Objective, 1, 0, false, false), false);
        Expect(failures, "a lone fighter has nobody to form up with either", SortieFormsUp(CommanderSortieKind.Objective, 0, 1, false, false), false);
        Expect(failures, "a sortie wanting nothing never forms up", SortieFormsUp(CommanderSortieKind.Objective, 0, 0, false, false), false);
        // The widening itself: a pair of fighters used to fly straight at the objective one by one.
        Expect(failures, "a fighter patrol of two forms up, where it used not to", SortieFormsUp(CommanderSortieKind.Cap, 0, 2, false, false), true);
        Expect(failures, "a fighter patrol of three forms up", SortieFormsUp(CommanderSortieKind.Cap, 0, 3, false, false), true);
        Expect(failures, "a strike package still forms up", SortieFormsUp(CommanderSortieKind.Strike, 2, 2, false, false), true);
        // The four exemptions, each on its own.
        Expect(failures, "a sortie whose objective is in ground contact never forms up: it is needed now", SortieFormsUp(CommanderSortieKind.Objective, 4, 1, true, false), false);
        Expect(failures, "that same sortie out of ground contact does form up", SortieFormsUp(CommanderSortieKind.Objective, 4, 1, false, false), true);
        Expect(failures, "hostile aircraft alone never exempt a sortie: that is when arriving together matters most", SortieFormsUp(CommanderSortieKind.Cap, 0, 3, false, false), true);
        Expect(failures, "the radar aeroplane never forms up, whatever it is flying with", SortieFormsUp(CommanderSortieKind.Awacs, 2, 2, false, false), false);
        Expect(failures, "a suppression sortie that has gone in never goes back to form up", SortieFormsUp(CommanderSortieKind.Arad, 2, 2, false, true), false);
        Expect(failures, "a suppression sortie that has not gone in yet does form up", SortieFormsUp(CommanderSortieKind.Arad, 2, 2, false, false), true);

        // The envelope: floor, growth, cap, and the floor winning over the cap.
        Expect(failures, "an element with nothing to face still gets its floor", SortieElementWanted(2, 0, 6), 2);
        Expect(failures, "an element facing more than its floor grows to meet it", SortieElementWanted(2, 5, 6), 5);
        Expect(failures, "an element never grows past its own cap", SortieElementWanted(2, 11, 6), 6);
        Expect(failures, "an element exactly on its cap keeps it", SortieElementWanted(2, 6, 6), 6);
        Expect(failures, "a cap of zero means the element has no cap of its own", SortieElementWanted(2, 30, 0), 30);
        Expect(failures, "a negative cap is no cap either", SortieElementWanted(2, 30, -1), 30);
        Expect(failures, "a cap never cuts below a floor already promised", SortieElementWanted(9, 3, 6), 9);
        Expect(failures, "a negative floor asks for nothing rather than for less than nothing", SortieElementWanted(-3, 0, 6), 0);
        Expect(failures, "a negative growth read never lowers the floor", SortieElementWanted(2, -7, 6), 2);

        // The retrofits are behaviour-neutral: both ladders still answer exactly what they answered
        // before the envelope existed, at every input the game can hand them.
        for (int observed = 0; observed <= 12; observed++)
        {
            Expect(
                failures,
                $"the close-air-support ladder at {observed} observed is unchanged by the shared envelope",
                CasWanted(observed),
                Mathf.Min(CasLadder(observed), CasPerObjectiveCap));
        }

        for (int air = 0; air <= 12; air++)
        {
            Expect(
                failures,
                $"the patrol ladder at {air} tracked aircraft is unchanged by the shared envelope",
                CapWanted(air),
                Mathf.Clamp(CapBaselinePerObjective + air, CapBaselinePerObjective, CapPerObjectiveCap));
        }

        // What each sortie kind asks for as the sky over it fills up. A QUIET objective is
        // deliberately unchanged from what it asked for before this track (user instruction
        // 2026-09-16): one fighter, and no ground-attack airframe.
        Expect(failures, "a quiet objective still asks for exactly one fighter", CapWanted(0), 1);
        Expect(failures, "a quiet objective still asks for no ground-attack airframe", CasWanted(0), 0);
        Expect(failures, "an objective with a pair of raiders over it asks for three fighters", CapWanted(2), 3);
        Expect(failures, "a contested objective draws more fighters than a quiet one", CapWanted(3) > CapWanted(0), true);
        Expect(failures, "a contested objective draws more airframes than a quiet one", CasWanted(6) > CasWanted(0), true);
        Expect(
            failures,
            "the patrol cap is above the baseline, or a patrol could never grow at all",
            CapPerObjectiveCap > CapBaselinePerObjective,
            true);
    }

    private static void CheckAirSupport(List<string> failures)
    {
        CheckAirSuperiorityRefusal(failures);
        CheckAntiRadiationWeapon(failures);
        CheckStrikePackages(failures);
        CheckStrikeClock(failures);
        CheckStrikeRun(failures);
        CheckCapBands(failures);
        CheckSortieSizing(failures);
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
        Expect(failures, "a transport with no hostile air tracked still gets two escorts", TransportEscortWanted(0), TransportEscortMinimum);
        Expect(failures, "a missioned aircraft four minutes on the deck that never flew is stuck", StuckOnDeck(true, false, 240f, 4f), true);
        Expect(failures, "a parked aircraft that has flown is recovered, not stuck", StuckOnDeck(true, true, 900f, 4f), false);
        Expect(failures, "an aircraft with no mission is the idle sweep's, not stuck", StuckOnDeck(false, false, 900f, 4f), false);
        Expect(failures, "zero minutes disables the stuck rule", StuckOnDeck(true, false, 900f, 0f), false);
        Expect(failures, "four on the deck in hangar mode spawn the next at the map edge", LaunchesFromMapEdge(true, 4, 3), true);
        Expect(failures, "three on the deck still taxi", LaunchesFromMapEdge(true, 3, 3), false);
        Expect(failures, "air-spawn mode never uses the map edge rule", LaunchesFromMapEdge(false, 9, 3), false);
        Expect(failures, "a deck used ten seconds ago is busy", CommanderAirCommandService.DeckBusy(10f, 20f), true);
        Expect(failures, "a deck used exactly the spacing ago is free", CommanderAirCommandService.DeckBusy(20f, 20f), false);
        Vector3 door = new(0f, 0f, 0f);
        Vector3 exitEnd = new(0f, 0f, 150f);
        Expect(failures, "a unit twenty metres beside the door blocks the hangar", CommanderAirCommandService.SpawnPathBlocked(new Vector3(20f, 0f, 0f), door, exitEnd, 40f), true);
        Expect(failures, "a unit on the roll-out a hundred metres out blocks the hangar", CommanderAirCommandService.SpawnPathBlocked(new Vector3(10f, 0f, 100f), door, exitEnd, 40f), true);
        Expect(failures, "a unit two hundred metres to the side does not block", CommanderAirCommandService.SpawnPathBlocked(new Vector3(200f, 0f, 50f), door, exitEnd, 40f), false);
        Expect(failures, "a unit well past the roll-out does not block", CommanderAirCommandService.SpawnPathBlocked(new Vector3(0f, 0f, 300f), door, exitEnd, 40f), false);

        // The airborne entry: a helicopter low and slow, a tiltwing on its wing, a jet high and fast.
        Expect(failures, "a helicopter enters at 40 m/s", Mathf.Approximately(CommanderAirCommandService.AirborneEntrySpeed(true, false, 100f, 50f), 40f), true);
        Expect(failures, "a tiltwing enters at a wing's speed, not a rotor's", CommanderAirCommandService.AirborneEntrySpeed(true, true, 100f, 50f) >= 120f, true);
        Expect(failures, "a fast jet enters at its own reference speed when that is higher", Mathf.Approximately(CommanderAirCommandService.AirborneEntrySpeed(false, false, 180f, 60f), 180f), true);
        Expect(failures, "a slow type enters no slower than the wing floor", Mathf.Approximately(CommanderAirCommandService.AirborneEntrySpeed(false, false, 80f, 50f), 120f), true);
        Expect(failures, "a tiltwing enters between a helicopter and a jet", CommanderAirCommandService.AirborneEntryAltitude(true, true) > CommanderAirCommandService.AirborneEntryAltitude(true, false) && CommanderAirCommandService.AirborneEntryAltitude(true, true) < CommanderAirCommandService.AirborneEntryAltitude(false, false), true);
        Expect(
            failures,
            "consecutive airborne spawns never share a point",
            CommanderAirCommandService.AirborneSpawnOffset(0, Vector3.right, 400f, 150f, 4)
                != CommanderAirCommandService.AirborneSpawnOffset(1, Vector3.right, 400f, 150f, 4),
            true);
        Expect(
            failures,
            "the airborne spawn slots cycle after four",
            CommanderAirCommandService.AirborneSpawnOffset(0, Vector3.right, 400f, 150f, 4)
                == CommanderAirCommandService.AirborneSpawnOffset(4, Vector3.right, 400f, 150f, 4),
            true);
        Expect(failures, "a base near the east edge enters from the east", NearestMapEdgePoint(new Vector3(30_000f, 0f, 1_000f), new Vector2(80_000f, 80_000f)).x, 40_000f);
        Expect(failures, "a base near the south edge enters from the south", NearestMapEdgePoint(new Vector3(1_000f, 0f, -35_000f), new Vector2(80_000f, 80_000f)).z, -40_000f);
        Expect(failures, "a transport under heavy tracked hostile air gets the platoon CAP number", TransportEscortWanted(9), PlatoonCapWanted(9));
        Expect(failures, "the escort minimum wins over a small platoon CAP number", TransportEscortWanted(3) >= TransportEscortMinimum, true);
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
        Expect(failures, "the AWACS is served before everything else on the CAP side's turn", (int)NextAirDemand(true, true, true, true), (int)CommanderAirDemandKind.Awacs);

        // The radar airframe stands aside for a fighter it cannot outbid (fix, 2026-09-14). Without
        // this the CAP side was bound 0/N on every review of the 2026-09-14 match.
        Expect(
            failures,
            "an unaffordable AWACS gives the fighter turn to a CAP demand the wing can afford",
            (int)NextAirDemand(awacsShort: true, capShort: true, false, false, preferCas: false, awacsAffordable: false, capAffordable: true),
            (int)CommanderAirDemandKind.Cap);
        Expect(
            failures,
            "an affordable AWACS still outranks the CAP demand",
            (int)NextAirDemand(awacsShort: true, capShort: true, false, false, preferCas: false, awacsAffordable: true, capAffordable: true),
            (int)CommanderAirDemandKind.Awacs);
        Expect(
            failures,
            "an unaffordable AWACS keeps the turn when no fighter is wanted, so it can still save",
            (int)NextAirDemand(awacsShort: true, capShort: false, false, false, preferCas: false, awacsAffordable: false, capAffordable: true),
            (int)CommanderAirDemandKind.Awacs);
        Expect(
            failures,
            "a wing that can afford neither saves for the AWACS rather than burning the turn",
            (int)NextAirDemand(awacsShort: true, capShort: true, false, false, preferCas: false, awacsAffordable: false, capAffordable: false),
            (int)CommanderAirDemandKind.Awacs);
        Expect(
            failures,
            "an unaffordable AWACS passed over for a fighter never hands the turn to the ground-attack side",
            (int)NextAirDemand(awacsShort: true, capShort: true, aradShort: true, casShort: true, preferCas: false, awacsAffordable: false, capAffordable: true),
            (int)CommanderAirDemandKind.Cap);
        Expect(
            failures,
            "affordability never changes the ground-attack side's own turn",
            (int)NextAirDemand(awacsShort: true, capShort: true, aradShort: false, casShort: true, preferCas: true, awacsAffordable: false, capAffordable: true),
            (int)CommanderAirDemandKind.Cas);
        Expect(failures, "ARAD is served before the CAS it clears the way for", (int)NextAirDemand(false, false, aradShort: true, casShort: true), (int)CommanderAirDemandKind.Arad);
        Expect(failures, "CAP still outranks ARAD", (int)NextAirDemand(false, capShort: true, aradShort: true, casShort: false), (int)CommanderAirDemandKind.Cap);
        Expect(failures, "ARAD alone is served", (int)NextAirDemand(false, false, aradShort: true, casShort: false), (int)CommanderAirDemandKind.Arad);

        // The CAP/CAS alternation (fix, 2026-09-14). "CAP first" above is now the tie-break for the
        // CAP side's own turn; these cases are what stops it becoming a permanent veto.
        Expect(failures, "with only CAP open it is never the CAS side's turn", PrefersCasThisBuy(true, false, false), false);
        Expect(failures, "with only CAP open a CAS turn is not carried over", PrefersCasThisBuy(true, false, true), false);
        Expect(failures, "with only CAS open the CAS side takes the buy", PrefersCasThisBuy(false, true, false), true);
        Expect(failures, "with only CAS open a CAP turn does not block it", PrefersCasThisBuy(false, true, true), true);
        Expect(failures, "with nothing open there is no turn to take", PrefersCasThisBuy(false, false, false), false);
        Expect(failures, "both open after a CAP buy gives the buy to CAS", PrefersCasThisBuy(true, true, false), true);
        Expect(failures, "both open after a CAS buy gives the buy back to CAP", PrefersCasThisBuy(true, true, true), false);
        Expect(
            failures,
            "the CAS side's turn beats CAP when both are short",
            (int)NextAirDemand(false, capShort: true, false, casShort: true, preferCas: true),
            (int)CommanderAirDemandKind.Cas);
        Expect(
            failures,
            "the CAP side's turn keeps CAP first when both are short",
            (int)NextAirDemand(false, capShort: true, false, casShort: true, preferCas: false),
            (int)CommanderAirDemandKind.Cap);
        Expect(
            failures,
            "a CAS turn with only CAP open still buys the fighter rather than nothing",
            (int)NextAirDemand(false, capShort: true, false, casShort: false, preferCas: true),
            (int)CommanderAirDemandKind.Cap);
        Expect(
            failures,
            "the radar airframe waits its turn behind suppression on the CAS side's turn",
            (int)NextAirDemand(true, true, true, true, preferCas: true),
            (int)CommanderAirDemandKind.Arad);
        Expect(
            failures,
            "the radar airframe never blocks the strike on the CAS side's turn",
            (int)NextAirDemand(awacsShort: true, capShort: true, aradShort: false, casShort: true, preferCas: true),
            (int)CommanderAirDemandKind.Cas);
        Expect(
            failures,
            "a CAS turn with only the radar airframe open still buys it rather than nothing",
            (int)NextAirDemand(awacsShort: true, capShort: false, aradShort: false, casShort: false, preferCas: true),
            (int)CommanderAirDemandKind.Awacs);
        Expect(
            failures,
            "the radar airframe still comes before the fighters on the CAP side's turn",
            (int)NextAirDemand(awacsShort: true, capShort: true, aradShort: false, casShort: false, preferCas: false),
            (int)CommanderAirDemandKind.Awacs);

        // The radar airframe's own replacement wait (fix, 2026-09-14).
        Expect(
            failures,
            "a lost radar airframe is not re-bought for ten minutes",
            SortieLossCooldownMinutes(awacs: true, 0, 2f, AwacsLossCooldownMinutes),
            AwacsLossCooldownMinutes);
        Expect(
            failures,
            "a busy sky does not shorten the radar airframe's wait",
            SortieLossCooldownMinutes(awacs: true, 9, 2f, AwacsLossCooldownMinutes),
            AwacsLossCooldownMinutes);
        Expect(
            failures,
            "every other sortie keeps the air-defence-sensitive wait",
            SortieLossCooldownMinutes(awacs: false, AirDefenceHesitationCount, 2f, AwacsLossCooldownMinutes),
            4f);
        Expect(
            failures,
            "a quiet objective keeps the base wait",
            SortieLossCooldownMinutes(awacs: false, 0, 2f, AwacsLossCooldownMinutes),
            2f);
        // Not compared against OperationsHeliInsertionCooldownMinutes, though 10 is where the
        // number came from: that one is a setting a player may retune, and a self-check that fails
        // because someone moved a slider is a false alarm.
        Expect(
            failures,
            "the radar airframe's wait outlasts several reviews, or it is not a wait at all",
            AwacsLossCooldownMinutes * 60f > ReviewIntervalSeconds * 4f,
            true);
        Expect(
            failures,
            "a CAS turn still puts suppression ahead of the CAS it clears the way for",
            (int)NextAirDemand(false, capShort: true, aradShort: true, casShort: true, preferCas: true),
            (int)CommanderAirDemandKind.Arad);

        // The air buy loop is money-limited, not count-limited (user decision 2026-09-14,
        // superseding the three-per-review cap of 2026-09-13). Every one of these is a way the loop
        // could silently go back to buying one or two aircraft a review, or to never stopping.
        Expect(
            failures,
            "a fund that covers the cheapest wanted airframe with room in the sky buys again",
            CommanderEnemyCommanderService.AirBuyContinues(3, fund: 200f, cheapestWantedValue: 31f, airborne: 7, airborneCeiling: 20),
            true);
        Expect(
            failures,
            "a fund one short of the cheapest wanted airframe stops the review",
            CommanderEnemyCommanderService.AirBuyContinues(0, fund: 30f, cheapestWantedValue: 31f, airborne: 7, airborneCeiling: 20),
            false);
        Expect(
            failures,
            "a fund exactly at the price still buys",
            CommanderEnemyCommanderService.AirBuyContinues(0, fund: 31f, cheapestWantedValue: 31f, airborne: 7, airborneCeiling: 20),
            true);
        Expect(
            failures,
            "the airborne ceiling stops the review however rich the wing is",
            CommanderEnemyCommanderService.AirBuyContinues(0, fund: 5000f, cheapestWantedValue: 31f, airborne: 20, airborneCeiling: 20),
            false);
        Expect(
            failures,
            "one place under the ceiling is still room for an airframe",
            CommanderEnemyCommanderService.AirBuyContinues(0, fund: 5000f, cheapestWantedValue: 31f, airborne: 19, airborneCeiling: 20),
            true);
        Expect(
            failures,
            "a demand no strip can launch is never affordable",
            CommanderEnemyCommanderService.AirBuyContinues(0, fund: 5000f, cheapestWantedValue: float.MaxValue, airborne: 0, airborneCeiling: 20),
            false);
        Expect(
            failures,
            "no open demand at all buys nothing",
            CommanderEnemyCommanderService.AirBuyContinues(0, fund: 5000f, cheapestWantedValue: 0f, airborne: 0, airborneCeiling: 20),
            false);
        Expect(
            failures,
            "the runaway guard stops a loop that money and the ceiling would not",
            CommanderEnemyCommanderService.AirBuyContinues(
                CommanderEnemyCommanderService.MaxAirBuysPerReviewSafety, fund: 5000f, cheapestWantedValue: 31f, airborne: 0, airborneCeiling: 20),
            false);
        Expect(
            failures,
            "the runaway guard sits far above any review a bounded fund can pay for",
            CommanderEnemyCommanderService.MaxAirBuysPerReviewSafety >= 6,
            true);
        Expect(failures, "the first buy of a review may proceed", CommanderEnemyCommanderService.AirBuyContinues(0), true);

        // The ground's unspent share goes to the wing (user decision 2026-09-14).
        Expect(
            failures,
            "a ground buyer holding on an empty book hands its share to a wing that is asking",
            CommanderEnemyCommanderService.GroundShareGoesToWing(groundHoldsToBook: true, hasOpenBook: false, airDemandOpen: true),
            true);
        Expect(
            failures,
            "an open order line keeps the ground's share on the ground",
            CommanderEnemyCommanderService.GroundShareGoesToWing(groundHoldsToBook: true, hasOpenBook: true, airDemandOpen: true),
            false);
        Expect(
            failures,
            "a wing that has asked for nothing is handed nothing",
            CommanderEnemyCommanderService.GroundShareGoesToWing(groundHoldsToBook: true, hasOpenBook: false, airDemandOpen: false),
            false);
        Expect(
            failures,
            "a commander whose ground buyer is not held to its book hands over nothing",
            CommanderEnemyCommanderService.GroundShareGoesToWing(groundHoldsToBook: false, hasOpenBook: false, airDemandOpen: true),
            false);

        // ARAD first (user decision 2026-09-14): who flies an objective's CAS while a belt is up.
        Expect(
            failures,
            "one air-defence vehicle observed still lets the attack helicopters fly",
            RotaryCasAllowed(AirDefenceHesitationCount - 1, aradFlown: false),
            true);
        Expect(
            failures,
            "a belt at the hesitation count holds the helicopters until suppression has flown",
            RotaryCasAllowed(AirDefenceHesitationCount, aradFlown: false),
            false);
        Expect(
            failures,
            "a thick belt holds them too",
            RotaryCasAllowed(23, aradFlown: false),
            false);
        Expect(
            failures,
            "a suppression sortie that has been on station releases the helicopters",
            RotaryCasAllowed(23, aradFlown: true),
            true);
        Expect(
            failures,
            "a belt that has thinned below the count releases them whether or not suppression flew",
            RotaryCasAllowed(0, aradFlown: false),
            true);
        Expect(
            failures,
            "the suppression memory survives while the belt is still up",
            AradMemorySurvives(aradFlown: true, observedAirDefence: AirDefenceHesitationCount),
            true);
        Expect(
            failures,
            "a belt that thinned out forgets it was suppressed, so a new one is suppressed again",
            AradMemorySurvives(aradFlown: true, observedAirDefence: AirDefenceHesitationCount - 1),
            false);
        Expect(
            failures,
            "an objective that has never been suppressed remembers nothing",
            AradMemorySurvives(aradFlown: false, observedAirDefence: 23),
            false);
        Expect(
            failures,
            "the hesitation count is at least a pair: one launcher is not a belt",
            AirDefenceHesitationCount >= 2,
            true);

        // A suppression sortie never binds an airframe with nothing anti-radiation aboard
        // (user report 2026-09-14).
        Expect(
            failures,
            "a suppression slot refuses an airframe carrying no anti-radiation store",
            CommanderEnemyCommanderService.MayBindToRole(
                CommanderEnemyCommanderService.AirRole.Arad, typeFillsRole: true, carriesAntiRadiation: false),
            false);
        Expect(
            failures,
            "a suppression slot takes an airframe that is carrying one",
            CommanderEnemyCommanderService.MayBindToRole(
                CommanderEnemyCommanderService.AirRole.Arad, typeFillsRole: true, carriesAntiRadiation: true),
            true);
        Expect(
            failures,
            "anti-radiation stores never make an airframe eligible for a role its type cannot fill",
            CommanderEnemyCommanderService.MayBindToRole(
                CommanderEnemyCommanderService.AirRole.Arad, typeFillsRole: false, carriesAntiRadiation: true),
            false);
        Expect(
            failures,
            "every other slot is unchanged by the suppression rule",
            CommanderEnemyCommanderService.MayBindToRole(
                CommanderEnemyCommanderService.AirRole.Strike, typeFillsRole: true, carriesAntiRadiation: false),
            true);
        Expect(
            failures,
            "a null aeroplane is carrying nothing",
            CommanderAirCommandService.CarriesAntiRadiation(null),
            false);

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
        Expect(failures, "a poor commander keeps the configured airborne ceiling", AirborneCeilingFor(20, 130f, 15f, 60), 20);
        Expect(failures, "a 460/min commander may keep thirty aircraft up", AirborneCeilingFor(20, 460f, 15f, 60), 30);
        Expect(failures, "the income-scaled ceiling stops at its maximum", AirborneCeilingFor(20, 5000f, 15f, 60), 60);
        Expect(failures, "a zero income rate disables the scaling", AirborneCeilingFor(20, 5000f, 0f, 60), 20);
        Expect(failures, "a maximum below the floor never lowers the floor", AirborneCeilingFor(20, 5000f, 15f, 10), 20);

        // The attrition brake's half is gone (user decision 2026-09-16: "escalate, never hold"). It
        // halved this ceiling, which collapsed the air budget to a single airframe and froze the
        // commander; the checks that pinned the halving went with it. The ceiling the wing flies to
        // is now the income-scaled one and nothing else.

        // Packages (design SS3, smarter-air-wing_20260914).
        // The idle sweep's patrol adoption (fix, 2026-09-14: adopted strays never counted).
        Expect(failures, "a fighter joins the standing patrol while it is short", AdoptIntoHomeCap(1, 2, true), true);
        Expect(failures, "a fighter is a spare once the patrol is full", AdoptIntoHomeCap(2, 2, true), false);
        Expect(failures, "a ground-attack airframe never joins the standing patrol", AdoptIntoHomeCap(0, 2, false), false);

        Expect(failures, "the form-up point sits at the nominal distance on a long leg", PackageFormUpDistance(60000f, 12000f, -1f, 8000f), 12000f);
        Expect(failures, "the form-up point never passes the objective on a short leg", PackageFormUpDistance(5000f, 12000f, -1f, 8000f), 5000f);
        Expect(failures, "a tracked hostile ahead pulls the form-up point back to its stand-off", PackageFormUpDistance(60000f, 12000f, 15000f, 8000f), 7000f);
        Expect(failures, "a distant hostile leaves the nominal form-up distance alone", PackageFormUpDistance(60000f, 12000f, 40000f, 8000f), 12000f);
        Expect(failures, "a hostile on top of the strip forms the package up over the strip", PackageFormUpDistance(60000f, 12000f, 2000f, 8000f), 0f);

        // The walk back from the wanted form-up point (user, 2026-09-16: "form-up points are nowhere
        // near the enemy"): the first clear step wins, and nothing clear means over the base.
        Expect(failures, "a clear form-up point stays where it was wanted", FirstClearAlong(12000f, 2000f, _ => true), 12000f);
        Expect(failures, "a form-up point with the enemy near it walks back to the first clear step", FirstClearAlong(12000f, 2000f, along => along <= 6000f), 6000f);
        Expect(failures, "a leg with no clear point forms the package up over the base", FirstClearAlong(12000f, 2000f, _ => false), 0f);
        Expect(failures, "a form-up already over the base stays there", FirstClearAlong(0f, 2000f, _ => false), 0f);

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

        // AWACS station geometry (design SS4, departure 2026-09-14: measured from the BASE).
        Expect(failures, "the AWACS stands 15 km out from its own base toward a distant front", AwacsStandoffMeters(hasAim: true, 90000f), AwacsBaseOffsetMeters);
        Expect(failures, "the AWACS never stands off past the thing it is aimed at", AwacsStandoffMeters(hasAim: true, 10000f), 10000f);
        Expect(failures, "with nothing to aim at the AWACS orbits the airbase itself", AwacsStandoffMeters(hasAim: false, 0f), 0f);
        Expect(failures, "the AWACS orbit is the 20 km the design asks for over the main base", AwacsOrbitRadiusMeters, 20000f);
        Expect(failures, "the AWACS station stays well inside the 30 km it used to stand off behind the front", AwacsBaseOffsetMeters < 30000f, true);

        // The station safety search (user report, 2026-09-14: "an AWACS was just tasked straight
        // into the enemy and killed because the front-line was close to the airbase"). One base at
        // the origin, the front due east, the candidate 15 km out, the orbit 20 km, 15 km of
        // required clearance: a station complies when the nearest enemy is at least 35 km from its
        // centre.
        Vector2 awacsBase = Vector2.zero;
        Vector2 awacsFront = Vector2.right;
        const float awacsClearance = 15000f;
        List<Vector2> farEnemy = new() { new Vector2(100000f, 0f) };
        Expect(
            failures,
            "a station with the enemy 100 km away is left exactly where the design puts it",
            TryAwacsStation(awacsBase, awacsFront, AwacsBaseOffsetMeters, farEnemy, awacsClearance, AwacsOrbitRadiusMeters, out float awacsOffset, out float awacsOrbit),
            true);
        Expect(failures, "that station keeps the design's 15 km offset", awacsOffset, AwacsBaseOffsetMeters);
        Expect(failures, "that station keeps the design's full 20 km orbit", awacsOrbit, AwacsOrbitRadiusMeters);

        List<Vector2> nearEnemy = new() { new Vector2(45000f, 0f) };
        Expect(
            failures,
            "a station the enemy is inside 35 km of is slid back toward its own base",
            TryAwacsStation(awacsBase, awacsFront, AwacsBaseOffsetMeters, nearEnemy, awacsClearance, AwacsOrbitRadiusMeters, out awacsOffset, out awacsOrbit),
            true);
        Expect(failures, "it is slid back exactly far enough and no further", awacsOffset, 10000f);
        Expect(failures, "sliding back keeps the full orbit rather than squeezing it", awacsOrbit, AwacsOrbitRadiusMeters);

        List<Vector2> closeEnemy = new() { new Vector2(32000f, 0f) };
        Expect(
            failures,
            "a station no forward offset can make safe is slid past the base, away from the front",
            TryAwacsStation(awacsBase, awacsFront, AwacsBaseOffsetMeters, closeEnemy, awacsClearance, AwacsOrbitRadiusMeters, out awacsOffset, out awacsOrbit),
            true);
        Expect(failures, "it stands 3 km behind the base", awacsOffset, -3000f);
        Expect(failures, "standing behind the base still keeps the full orbit", awacsOrbit, AwacsOrbitRadiusMeters);

        // 8 km from the base along the line: the furthest the station may slide is 15 km behind it,
        // which puts the enemy 23 km away — exactly 15 km of clearance plus the 8 km minimum orbit.
        List<Vector2> squeezeEnemy = new() { new Vector2(AwacsMinOrbitRadiusMeters, 0f) };
        Expect(
            failures,
            "a station that cannot be made safe at full width is flown at a squeezed orbit rather than not at all",
            TryAwacsStation(awacsBase, awacsFront, AwacsBaseOffsetMeters, squeezeEnemy, awacsClearance, AwacsOrbitRadiusMeters, out awacsOffset, out awacsOrbit),
            true);
        Expect(failures, "the squeeze is taken at the furthest the station may slide", awacsOffset, -AwacsBaseOffsetMeters);
        Expect(failures, "the orbit is squeezed to its floor and no further", awacsOrbit, AwacsMinOrbitRadiusMeters);

        List<Vector2> tooCloseEnemy = new() { new Vector2(AwacsMinOrbitRadiusMeters - 10f, 0f) };
        Expect(
            failures,
            "ten metres inside the squeeze boundary grounds the radar watch instead of flying it",
            TryAwacsStation(awacsBase, awacsFront, AwacsBaseOffsetMeters, tooCloseEnemy, awacsClearance, AwacsOrbitRadiusMeters, out _, out _),
            false);

        List<Vector2> enemyOnTheBase = new() { Vector2.zero };
        Expect(
            failures,
            "an enemy standing on the airbase itself grounds the radar watch",
            TryAwacsStation(awacsBase, awacsFront, AwacsBaseOffsetMeters, enemyOnTheBase, awacsClearance, AwacsOrbitRadiusMeters, out _, out _),
            false);

        Expect(
            failures,
            "with nothing tracked at all the station is the one the design asks for",
            TryAwacsStation(awacsBase, awacsFront, AwacsBaseOffsetMeters, System.Array.Empty<Vector2>(), awacsClearance, AwacsOrbitRadiusMeters, out awacsOffset, out _),
            true);
        Expect(failures, "an empty picture never moves the station", awacsOffset, AwacsBaseOffsetMeters);
        Expect(failures, "the squeezed orbit is still a box, not a waypoint", AwacsMinOrbitRadiusMeters >= 8000f, true);
        Expect(failures, "the orbit is never squeezed up past the one the design asks for", AwacsMinOrbitRadiusMeters < AwacsOrbitRadiusMeters, true);
        Expect(failures, "the station search steps finer than the retarget hysteresis, or the answer would oscillate", AwacsStationSlideStepMeters < CasRetargetMeters, true);
        Expect(failures, "the safety distance is positive; check the Operations section of the config", CommanderSettings.AwacsMinEnemyDistanceMeters > 0f, true);

        // The minimum hold on a sortie whose demand has closed (team lead, 2026-09-14: eight
        // releases against five taskings in twenty minutes).
        Expect(failures, "a CAS objective is held when its demand blinks out", SortieIsHoldable(CommanderSortieKind.Objective), true);
        Expect(failures, "a platoon CAP is held when its demand blinks out", SortieIsHoldable(CommanderSortieKind.Cap), true);
        Expect(failures, "the radar watch is never held: it closes for a reason, not a blink", SortieIsHoldable(CommanderSortieKind.Awacs), false);
        Expect(failures, "a suppression sortie is never held: its belt is gone", SortieIsHoldable(CommanderSortieKind.Arad), false);
        Expect(failures, "a sortie nothing may hold is released the moment its demand closes", MayReleaseSortie(holdable: false, 0f, SortieMinHoldSeconds), true);
        Expect(failures, "a sortie that has never been demanded is released at once", MayReleaseSortie(holdable: true, -1f, SortieMinHoldSeconds), true);
        Expect(failures, "a sortie whose demand closed a moment ago keeps its wing", MayReleaseSortie(holdable: true, 0f, SortieMinHoldSeconds), false);
        Expect(failures, "one second inside the hold still keeps its wing", MayReleaseSortie(holdable: true, SortieMinHoldSeconds - 1f, SortieMinHoldSeconds), false);
        Expect(failures, "exactly at the hold the wing is let go", MayReleaseSortie(holdable: true, SortieMinHoldSeconds, SortieMinHoldSeconds), true);
        Expect(failures, "the hold outlasts the 20 s contact clock that made objectives flap", SortieMinHoldSeconds > 20f, true);
        Expect(failures, "the hold outlasts several reviews, or it would give no hysteresis at all", SortieMinHoldSeconds > 2f * ReviewIntervalSeconds, true);

        // The release order: retask before anything else (team lead, 2026-09-14).
        Expect(failures, "a released airframe another sortie wants is retasked, not sent anywhere", (int)ReleaseDisposal(somethingElseWantsIt: true, mayHoldPatrol: true), (int)CommanderReleaseDisposal.Retask);
        Expect(failures, "a retask outranks the patrol even for an airframe that could hold it", (int)ReleaseDisposal(true, true), (int)CommanderReleaseDisposal.Retask);
        Expect(failures, "a released helicopter nothing wants goes to its pad, never to the patrol", (int)ReleaseDisposal(false, mayHoldPatrol: false), (int)CommanderReleaseDisposal.ReturnToBase);
        Expect(failures, "a released fighter nothing wants holds the standing patrol", (int)ReleaseDisposal(false, mayHoldPatrol: true), (int)CommanderReleaseDisposal.HomeCap);
        Expect(failures, "a released helicopter something wants is still retasked", (int)ReleaseDisposal(true, false), (int)CommanderReleaseDisposal.Retask);

        // One AWACS per commander, counting every owned radar airframe alive in any state
        // (user report, 2026-09-14: "a hard limit on one AWACS").
        Expect(failures, "a commander with no radar airframe buys one", AwacsWanted(0), 1);
        Expect(failures, "a commander that already owns a radar airframe buys none", AwacsWanted(1), 0);
        Expect(failures, "an airframe on its way home still holds the only AWACS slot", AwacsWanted(1), 0);
        Expect(failures, "two radar airframes never ask for a third", AwacsWanted(2), 0);
        Expect(failures, "a negative count is still one commander, one AWACS", AwacsWanted(-1), AwacsPerCommander);
        Expect(failures, "the hard limit is one radar airframe per commander", AwacsPerCommander, 1);

        // The radar airframe flies radar watch and nothing else, both directions
        // (user decision 2026-09-14: "AWACS aircraft should never be retasked from being AWACS").
        Expect(failures, "a radar airframe may fly the radar watch", MayBindToSortie(radarAirframe: true, awacsSortie: true), true);
        Expect(failures, "a radar airframe is never retasked onto another sortie", MayBindToSortie(radarAirframe: true, awacsSortie: false), false);
        Expect(failures, "an ordinary airframe is never put on the radar watch", MayBindToSortie(radarAirframe: false, awacsSortie: true), false);
        Expect(failures, "an ordinary airframe fills an ordinary sortie", MayBindToSortie(radarAirframe: false, awacsSortie: false), true);

        // The radar watch is exempt from the out-of-ammo RTB (user report, 2026-09-14: the rearm
        // loop that bought a second AWACS).
        Expect(
            failures,
            "an airframe carrying no expendable store for its mission is never out of ammo",
            CommanderAirCommandService.MissionIsOutOfAmmo(carriesEligibleStore: false, anyLoadedStore: false, radarWatch: false),
            false);
        Expect(
            failures,
            "a radar watch is never out of ammo even with stores aboard",
            CommanderAirCommandService.MissionIsOutOfAmmo(carriesEligibleStore: true, anyLoadedStore: false, radarWatch: true),
            false);
        Expect(
            failures,
            "a strike airframe with empty racks is still out of ammo",
            CommanderAirCommandService.MissionIsOutOfAmmo(carriesEligibleStore: true, anyLoadedStore: false, radarWatch: false),
            true);
        Expect(
            failures,
            "a strike airframe with rounds left keeps its mission",
            CommanderAirCommandService.MissionIsOutOfAmmo(carriesEligibleStore: true, anyLoadedStore: true, radarWatch: false),
            false);

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

        // Lending the home CAP forward (design SS9, user decision 2026-09-14; the minimum held at the
        // base removed by user decision 2026-09-16 — every home-CAP fighter is takeable).
        Expect(failures, "a four-fighter home CAP can lend all four forward", LendableHomeCap(4), 4);
        Expect(failures, "the last fighter at the base is lent like any other (no minimum, 2026-09-16)", LendableHomeCap(1), 1);
        Expect(failures, "an empty home CAP lends nothing, never a negative count", LendableHomeCap(0), 0);
        Expect(failures, "a negative fighter count lends nothing, never a negative count", LendableHomeCap(-1), 0);

        // The patrol's in-contact read (the 120 s quiet clock removed by user decision 2026-09-16).
        Expect(failures, "a clear base ring lends at once, with no quiet wait (2026-09-16)", HomeCapIsQuiet(0), true);
        Expect(failures, "a base with hostile air tracked over it never lends", HomeCapIsQuiet(1), false);
        Expect(failures, "a base with a raid tracked over it never lends", HomeCapIsQuiet(6), false);
        Expect(failures, "a negative tracking read is a clear ring", HomeCapIsQuiet(-1), true);

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

        // The CAS side's turn (fix, 2026-09-14): the escort waits one binding, and nothing else moves.
        Expect(
            failures,
            "on the CAS side's turn the escort gives way to the strike",
            (int)NextSortieSlot(0, 3, 0, 4, preferCas: true),
            (int)CommanderAirSlot.Cas);
        Expect(
            failures,
            "a CAP-only sortie still takes fighters on the CAS side's turn",
            (int)NextSortieSlot(0, 2, 0, 0, preferCas: true),
            (int)CommanderAirSlot.Cap);
        Expect(
            failures,
            "a sortie with its strike complete takes wingmen on either turn",
            (int)NextSortieSlot(1, 3, 4, 4, preferCas: true),
            (int)CommanderAirSlot.Cap);
        Expect(
            failures,
            "a full sortie asks for nothing on either turn",
            (int)NextSortieSlot(3, 3, 4, 4, preferCas: true),
            (int)CommanderAirSlot.None);
        Expect(
            failures,
            "the CAP side's turn is the order Approval 1 wrote",
            (int)NextSortieSlot(0, 3, 0, 4, preferCas: false),
            (int)NextSortieSlot(0, 3, 0, 4));

        // Retasking cover onto contact (design SS10, user decision 2026-09-14).
        Expect(failures, "a platoon being shot at is in contact", SortieIsInContact(true, false, 0, 0), true);
        Expect(failures, "an attack that has gone in is in contact whatever the tracking has decayed to", SortieIsInContact(false, true, 0, 0), true);
        Expect(failures, "a tracked hostile ground unit in the ring is contact", SortieIsInContact(false, false, 1, 0), true);
        Expect(failures, "a tracked hostile aircraft in the ring is contact", SortieIsInContact(false, false, 0, 1), true);
        Expect(failures, "cover over empty ground is not in contact", SortieIsInContact(false, false, 0, 0), false);

        // A strike package never claims the ground's funding reserve (fix, 2026-09-15).
        Expect(
            failures,
            "a strike package over observed defenders is still not in contact",
            SortieIsInContact(CommanderSortieKind.Strike, false, false, 9, 4),
            false);
        Expect(
            failures,
            "nothing at all makes a strike package read as in contact",
            SortieIsInContact(CommanderSortieKind.Strike, true, true, 9, 9),
            false);
        Expect(
            failures,
            "a CAS objective over the same picture still reads as in contact",
            SortieIsInContact(CommanderSortieKind.Objective, false, false, 9, 4),
            true);
        Expect(
            failures,
            "the kinded rule agrees with the plain one for every kind but the strike",
            SortieIsInContact(CommanderSortieKind.Cap, false, false, 0, 2),
            SortieIsInContact(false, false, 0, 2));

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

        float formUp = CommanderSettings.PackageFormUpSeconds;
        float rotaryRange = CommanderSettings.HeliCasRangeMeters;
        int aradMinimum = CommanderSettings.AradClusterMinimum;
        Expect(failures, "the package form-up wait is positive; check the Operations section of the config", formUp > 0f, true);
        Expect(failures, "the package form-up wait fits inside the attack's own form-up timeout; check the Operations section of the config", formUp <= AssaultFormUpTimeoutSeconds, true);
        Expect(failures, "the helicopter CAS range is positive; check the Operations section of the config", rotaryRange > 0f, true);
        Expect(failures, "an ARAD cluster is at least a pair; check the Operations section of the config", aradMinimum >= 2, true);
    }
}
