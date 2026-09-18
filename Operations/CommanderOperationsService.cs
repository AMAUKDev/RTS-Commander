using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Claims every ground vehicle an AI-commanded HQ owns into named six-vehicle platoons, sites them
/// as forward bases on the points nearest the enemy (with a munitions truck) and two-vehicle
/// pickets on the quiet points behind, and launches two- or three-axis offensives sized to what the
/// commander has actually tracked — replacing "buy a vehicle, the game's convoy brain drives it at
/// the nearest enemy" with a front line the commander forms, holds and pushes (design.md,
/// <c>platoon-operations_20260913</c>).
/// </summary>
/// <remarks>
/// ponytail: nothing here persists across a mission reload — a platoon roster, a mission's state
/// and the pressure clock are all rebuilt from the live vehicle roster the next time discovery
/// finishes, the same stance <see cref="CommanderStrategicPointService"/> takes on point ownership.
/// No player platoon tools, no truck money-on-arrival, no air or naval tasking: all later tracks
/// (design.md "Out of scope").
/// </remarks>
internal sealed partial class CommanderOperationsService : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>Mission re-planning cadence — matches the buy review
    /// (<c>Ai/CommanderEnemyCommanderService.cs:30</c>) so the buyer's order book is never a review
    /// behind the missions that filled it.</summary>
    private const float ReviewIntervalSeconds = 30f;

    /// <summary>Destination re-issue cadence. Fast enough that a platoon that loses its leader is
    /// re-formed inside one hold tick; slow enough that it is one networked RPC per vehicle per 5 s
    /// at worst.</summary>
    private const float MovementIntervalSeconds = 5f;

    /// <summary>
    /// Platoon display names, <c>"1ST PLATOON"</c> style. Design SS1 gives the format and no
    /// ceiling; twelve is <c>MaxPlatoonsPerAttack</c> (T12) twice over, and past the twelfth a
    /// commander wraps back to the first name with a numeric suffix (<see cref="TryFormPlatoon"/>) —
    /// planner-chosen.
    /// </summary>
    private static readonly string[] PlatoonNames =
    {
        "1ST", "2ND", "3RD", "4TH", "5TH", "6TH", "7TH", "8TH", "9TH", "10TH", "11TH", "12TH",
    };

    private readonly Dictionary<FactionHQ, OperationsState> states = new();

    /// <summary>Scratch for <see cref="BuildRecipeArrays"/>, reused across every platoon formed or
    /// reinforced in a review rather than allocated per platoon.</summary>
    private readonly List<CommanderPlatoonRole> recipeRoles = new();
    private readonly List<bool> recipePrefers = new();
    private readonly List<int> recipePicks = new();

    private float nextReviewAt = CommanderScheduler.Stagger("operations.review", ReviewIntervalSeconds);
    private float nextMovementAt = CommanderScheduler.Stagger("operations.movement", MovementIntervalSeconds);

    /// <summary>When the logistics watch next runs. Zero rather than a staggered start because its
    /// interval is a setting and the settings are not bound when this field is initialised; the
    /// stagger is applied by <see cref="ResetSession"/>, which runs with a live config.</summary>
    private float nextLogisticsAt;

    internal static CommanderOperationsService? Instance { get; private set; }

    internal CommanderOperationsService()
    {
        Instance = this;
    }

    /// <summary>Per-managed-HQ state: the vehicle pool, the platoons formed from it, the live
    /// missions and the pressure clock that eventually forces an attack.</summary>
    private sealed class OperationsState
    {
        /// <summary>Every claimed vehicle not yet, or no longer, a platoon member.</summary>
        internal readonly List<Unit> Pool = new();

        /// <summary>
        /// The staging post each pooled vehicle has been sent to, the same bookkeeping
        /// <c>CommanderPlatoon.Issued</c> keeps for a platoon's formation slots, so a vehicle
        /// already standing on its post is not re-issued every movement tick. Without a standing
        /// order of its own a pooled vehicle has no <c>commandedDestination</c>, and
        /// <c>GroundVehicle.CheckObstacles</c> walks it at the nearest objective on its own — see
        /// <see cref="StagePool"/>.
        /// </summary>
        internal readonly Dictionary<Unit, GlobalPosition> PoolIssued = new();

        /// <summary>Game time each pool vehicle was first staged with nothing to do, for the
        /// idle-pool sale (<c>SellSurplusPool</c>). Pruned with the staging posts the moment a
        /// vehicle leaves the pool (<c>PruneStagingPosts</c>), so a vehicle that comes back starts
        /// a fresh clock.</summary>
        internal readonly Dictionary<Unit, float> PoolIdleSince = new();

        /// <summary>What already covered each role's order-book lines at the buyer's last pass
        /// (<c>OpenRolesByPriority</c>): bought that review, banked as depot supply, idle in the
        /// pool. Kept only for the review line's <c>covered:</c> fields; the buyer recomputes it
        /// every purchase.</summary>
        internal readonly int[] CoveredByRole = new int[RoleCount];

        internal readonly List<CommanderPlatoon> Platoons = new();
        /// <summary>How many marching platoons the pre-emptive air cap held back the last time the
        /// number changed, so the queue line is logged on a change rather than every review.
        /// -1 until the first review, so a first review with nothing queued stays quiet.</summary>
        internal int PreemptiveQueuedReported = -1;


        internal readonly List<CommanderOperationsMission> Missions = new();

        /// <summary>This review's control points, ranked by value descending (T6's <c>RankPoints</c>).</summary>
        internal readonly List<CommanderRankedPoint> RankedPoints = new();

        /// <summary>The order book: every open (or recently closed) requisition line for this HQ
        /// (T9).</summary>
        internal readonly List<CommanderRequisition> Requisitions = new();

        /// <summary>
        /// Picket missions on a point away from the front still short of <c>PointsMinGarrison</c>
        /// after this review's <c>FillPickets</c> — what <c>PicketsNeedThePool</c> reads
        /// (DECISION-013: pickets take pool vehicles ahead of new platoon formation).
        /// </summary>
        internal int ShortPicketMissions = 0;

        /// <summary>
        /// Pool size the last "no purpose for a new platoon" line reported, or -1 while the gate is
        /// open. The <c>ReportInsertionDenial</c> de-duplication convention: a review runs every
        /// 30 s and the same line every time is noise nobody reads, so it is logged once per change.
        /// </summary>
        internal int NoPurposeLoggedPool = -1;

        /// <summary>Minutes of pressure accrued since the last attack (design SS3).</summary>
        internal float Pressure = 0f;

        /// <summary>Control points held as of the last review, so the next one can tell how many
        /// were lost in between (feeds the pressure clock).</summary>
        internal int LastPointsHeld = -1;

        /// <summary>Next index into <c>PlatoonNames</c>, wrapping with a numeric suffix past the
        /// end of the table.</summary>
        internal int NextPlatoonNumber = 0;

        /// <summary>
        /// B5 fix: per-target observed-enemy floor, keyed by the <see cref="CommanderStrategicPoint"/>
        /// or <see cref="Airbase"/> a failed attack was aimed at. <c>CountObserved</c> is a live read
        /// of <c>ThreatMemorySeconds</c>-old tracking contacts; withdrawing out of contact after a
        /// failed attack lets that tracking decay to near zero well before the next review, so
        /// without a floor the very next attempt on the same target would be sized as if the first
        /// attempt had never happened. Cleared with the rest of this HQ's state on a session reset
        /// (<see cref="states"/> itself is cleared in <c>ResetSession</c>), so a floor never survives
        /// into a new match.
        /// </summary>
        internal readonly Dictionary<object, int> ObservedFloors = new();

        /// <summary>
        /// Air support (design.md, air-support-tasking_20260913; CAP first, 2026-09-13): the live
        /// sorties over this HQ's objectives — each one objective's CAP wing plus its CAS — kept in
        /// priority order (attack, contact, threatened forward base).
        /// </summary>
        internal readonly List<CommanderAirSortie> AirSorties = new();

        /// <summary>
        /// Every airframe this commander's buy loop launched, added at the registration claim. Only
        /// these are ever tasked or retasked — the player's own Air Command launches are already
        /// mission-bound at registration, and stock-mission authored free aircraft carry no claim,
        /// so neither ever enters this set (decision 5).
        /// </summary>
        internal readonly HashSet<Aircraft> CommanderAirframes = new();

        /// <summary>The buy loop's launch just made, waiting for the airframe to register so it can
        /// be claimed into the hungry sortie; null when nothing is in flight from the buying call.</summary>
        internal CommanderPendingAirLaunch? PendingAirLaunch;

        /// <summary>The last task this commander issued per owned airframe, so a mission that
        /// moved for any other reason is read as the player's order and the airframe is let go —
        /// the air-side hands-off rule (decision 5).</summary>
        internal readonly Dictionary<Aircraft, IssuedAirTask> AirIssued = new();

        /// <summary>
        /// Airframes the idle sweep has already reported (design.md, smarter-air-wing_20260914
        /// Section 6). An airframe with no mission has no RTB record, so the landing order is
        /// re-issued every review; the log line is not. An airframe that comes back under orders is
        /// dropped from here, so falling idle again is news again.
        /// </summary>
        internal readonly HashSet<Aircraft> IdleSweepReported = new();

        /// <summary>
        /// Aircraft that registered into this faction while a commander was already running
        /// (design.md, smarter-air-wing_20260914 Section 15). A stock mission's authored free
        /// aircraft exist before that moment and are never in here, which is what stops the stray
        /// adoption below from seizing aircraft the mission author put in the sky on purpose.
        /// </summary>
        internal readonly HashSet<Aircraft> RegisteredWhileCommanded = new();

        /// <summary>
        /// Picket insertion (design.md, heli-picket-insertion_20260913): the open insertion records
        /// — one per requested flight, bound to the picket mission it delivers for.
        /// </summary>
        internal readonly List<CommanderInsertion> Insertions = new();

        /// <summary>
        /// Home CAP (design.md, commander-priorities_20260914, rung 1): the fighters the ladder bought
        /// to hold the standing patrol over the commander's own airbases. Only these count toward the
        /// home CAP's size. While the base is quiet every one of them may be lent to a sortie (design
        /// SS9; the minimum of one kept at the base removed by user decision 2026-09-16) — the patrol
        /// is the lowest-priority holder of fighters, and a sortie's escort is otherwise bought
        /// separately in rung 2.
        /// </summary>
        internal readonly HashSet<Aircraft> HomeCapAirframes = new();

        /// <summary>
        /// Last "enemy aircraft tracked within <c>CapLossRadiusMeters</c> of this fighter" observation
        /// per home-CAP fighter, refreshed by <c>MaintainHomeCap</c> on the defence review's fast
        /// clock. When the fighter dies, its last observation decides whether the death was a loss to
        /// enemy air (which buys another fighter) or an ordinary ground loss (which does not).
        /// </summary>
        internal readonly Dictionary<Aircraft, bool> HomeCapEnemyAirNear = new();

        /// <summary>
        /// Scaled <c>Time.time</c> of each home-CAP fighter lost to enemy air, pruned to the
        /// <c>CapLossMemoryMinutes</c> window — one more wanted CAP fighter per loss inside it.
        /// </summary>
        internal readonly List<float> CapLossTimes = new();

        /// <summary>
        /// Home-CAP fighters currently lent forward to a sortie (design.md,
        /// smarter-air-wing_20260914 Section 9). They are still in <see cref="HomeCapAirframes"/>,
        /// so the ladder counts them as held; this set is what the recall walks and what the
        /// <c>ladder:</c> line reports.
        /// </summary>
        internal readonly HashSet<Aircraft> LentHomeCap = new();

        /// <summary>Why the radar watch is currently held on the deck, or empty while it is flying
        /// (user report, 2026-09-14). Compared rather than re-derived so the grounding line is
        /// logged once per change instead of once per review.</summary>
        internal string AwacsGroundedReason = string.Empty;

        /// <summary>
        /// Scaled <c>Time.time</c> at which each airframe was last moved from a quiet sortie to one
        /// in contact (design.md, smarter-air-wing_20260914 Section 10). The hysteresis that stops
        /// two short fights taking the same aeroplane off each other every review.
        /// </summary>
        internal readonly Dictionary<Aircraft, float> AirRetaskedAt = new();

        /// <summary>Game time each owned aircraft was first seen on a deck, for the stuck-on-deck
        /// refund; cleared the moment it is airborne.</summary>
        internal readonly Dictionary<Aircraft, float> OnDeckSince = new();

        /// <summary>Owned aircraft that have been airborne at least once — a parked one that has
        /// flown is recovered, not stuck.</summary>
        internal readonly HashSet<Aircraft> EverAirborne = new();

        /// <summary>
        /// The priority ladder's picket share for this HQ, granted at each ladder review and spent
        /// down by insertion flights as they charge (design Section 3, rung 3). Zero until the
        /// ladder grants one; the next ladder review overwrites whatever was not consumed.
        /// </summary>
        internal float InsertionAllowance;

        /// <summary>
        /// What one complete insertion flight costs this commander — the price rung 3's savings are
        /// capped at, granted with the allowance each ladder review (departure 2026-09-14). Zero
        /// until the ladder has granted one, which reads as "no flight is priced yet" and holds the
        /// request exactly as an empty share does.
        /// </summary>
        internal float InsertionSavingsTarget;

        /// <summary>Insertion money actually charged since the last <c>ladder:</c> line, so the line
        /// reports the picket rung's spend when it happens rather than when it was granted.</summary>
        internal float InsertionSpentSinceLadder;

        /// <summary>Scaled <c>Time.time</c> until which a point that lost an insertion flight will
        /// not ask for another, keyed by the point (the ObservedFloors key convention).</summary>
        internal readonly Dictionary<object, float> InsertionCooldownUntil = new();

        /// <summary>Last decline reason per point, so a declined insertion logs once per reason
        /// rather than once per 30 s review (the ReportAirDenial convention).</summary>
        internal readonly Dictionary<CommanderStrategicPoint, string> InsertionDenials = new();

        /// <summary>How many insertion flights this commander has lost in a row with no successful
        /// drop in between — the commander-wide pause's counter, reset by the next drop.</summary>
        internal int InsertionLossStreak;

        /// <summary>Scaled <c>Time.time</c> until which this commander sends no insertion at all
        /// after a run of losses; zero when no pause is running. The per-point cooldown alone only
        /// moves the bleeding to the next hilltop.</summary>
        internal float InsertionPauseUntil;

        /// <summary>
        /// Which side of the wing the last air buy served — the alternation's whole memory (fix,
        /// 2026-09-14, <c>PrefersCasThisBuy</c>). Set only by a buy that actually read the demand,
        /// so the "is anything open" queries never disturb the turn.
        /// </summary>
        internal bool LastAirBuyWasCas;

        /// <summary>
        /// Points this review reserved for a transport helicopter (<c>PicketDeliveryMode</c>
        /// answered <c>Air</c>): the drive fill skips them, they post no road-stock requisition, and
        /// the review line tags them <c>air</c>. Rebuilt from scratch at the top of every
        /// <c>PlanPickets</c>, so a commander that loses its transports reverts to driving on the
        /// very next review.
        /// </summary>
        internal readonly HashSet<CommanderStrategicPoint> AirDeliveredPickets = new();

        /// <summary>
        /// Ranked points this review put out of reach of every vehicle depot this commander owns
        /// (reach-and-points Section 2, user decision 2026-09-14). No forward base, no road picket
        /// and no platoon is sent to one of these; a helicopter insertion is the only way to staff
        /// it until a depot comes within <c>DepotReachMeters</c>. Rebuilt from scratch by
        /// <c>RefreshReach</c> at the top of every review, so a commander that builds or loses a
        /// depot sees the change on the very next one.
        /// </summary>
        internal readonly HashSet<CommanderStrategicPoint> OutOfReach = new();

        /// <summary>
        /// Forward operating bases (design.md, fob-construction_20260914): this commander's open
        /// FOB orders. One at a time while any is still delivering (design Section 1); an order that
        /// has come online stays in the list so the teardown watch can measure its buildings.
        /// </summary>
        internal readonly List<CommanderFobOrder> FobOrders = new();

        /// <summary>The FOB deliveries currently in the air. Kept apart from
        /// <see cref="Insertions"/> because a picket insertion is swept against a picket mission and
        /// a FOB flight has none, but counted against the same airborne limit.</summary>
        internal readonly List<CommanderFobFlight> FobFlights = new();

        /// <summary>The last lift-flight slot id handed out for this commander, so the next one is
        /// this plus one (lift-wave_20260916). A platoon lift launches every load it owes in one
        /// review, and the supply side must be able to recall, re-drop or credit ONE of those loads
        /// without touching the others — which it can only do if each carries its own id. Ids are
        /// never reused within a session and never reset while the session runs; the counter only
        /// has to outrun the flights alive at any moment, which is at most the airborne ceiling.</summary>
        internal int LastLiftSlotId;

        /// <summary>Scaled <c>Time.time</c> until which a point that lost a FOB order will not be
        /// offered another, keyed by the point (the <see cref="InsertionCooldownUntil"/>
        /// convention).</summary>
        internal readonly Dictionary<CommanderStrategicPoint, float> FobCooldownUntil = new();

        /// <summary>
        /// Landing zones a platoon lift was cancelled on, and when they may be flown to again. A
        /// table of its own rather than a share of <see cref="FobCooldownUntil"/> (fix, 2026-09-15):
        /// that one says "do not put a BASE on this ground yet", and a lift turned back from a point
        /// was stopping the commander building a forward operating base there for ten minutes.
        /// </summary>
        internal readonly Dictionary<CommanderStrategicPoint, float> LiftCooldownUntil = new();

        /// <summary>Points this commander currently reads as FRONT because of something it has
        /// spotted rather than because of ground the enemy holds. The set is what throttles the line
        /// that says so to once per point per spell, rather than once per review.</summary>
        internal readonly HashSet<CommanderStrategicPoint> FrontBySpotting = new();

        /// <summary>Last FOB decline reason per point, so a refusal logs once per reason rather than
        /// once per 30 s review (the <c>ReportInsertionDenial</c> convention).</summary>
        internal readonly Dictionary<CommanderStrategicPoint, string> FobDenials = new();
        /// <summary>Why the last review picked no FOB site, so the reason is logged once per change
        /// rather than every 30 s (diagnostic, 2026-09-14: a whole match passed with no FOB line at
        /// all and nothing to say which rule refused).</summary>
        internal string FobSiteReason = string.Empty;

        /// <summary>When each site's clearance nudge was last logged (see RequestSiteClearance).</summary>
        internal readonly Dictionary<CommanderStrategicPoint, float> SiteClearanceLoggedAt = new();

        /// <summary>The dearest flight price a launch has refused for want of bank while the bank
        /// stood at its target — proof the target was priced too low. The ladder banks toward at
        /// least this much until a flight gets away (fix, 2026-09-14: `saving for the flight
        /// (31/31)` on every request while the airdrop load the request demanded cost more).</summary>
        internal float InsertionRefusedFlightPrice;

        /// <summary>Scaled <c>Time.time</c> until which this commander builds no FOB anywhere after
        /// losing one — destroyed or captured (user instruction 2026-09-14). A base it abandons on
        /// purpose does not start this clock; that is the point of abandoning one.</summary>
        internal float FobLossCooldownUntil;

        /// <summary>Scaled <c>Time.time</c> of the last deliberate abandonment, so a commander cannot
        /// chase a moving front by tearing a base down every review.</summary>
        internal float LastFobAbandonAt = -1f;

        // ---- Strike packages (design.md, strike-packages_20260915) ----

        /// <summary>
        /// The one open strike sortie, or null while none is. Held here as well as in
        /// <see cref="AirSorties"/> because a strike is the commander's OWN initiative: nothing in
        /// the world re-derives it each review the way a platoon in contact re-derives its CAS, so
        /// the demand walk re-posts this very object rather than building a fresh one and losing the
        /// package's progress with it.
        /// </summary>
        internal CommanderAirSortie? StrikeSortie;

        /// <summary>Minutes accrued on the strike clock since the last deliberate strike went in —
        /// <c>StepPressure</c>'s shape applied to the strike interval (design Section 1).</summary>
        internal float StrikeClockMinutes;

        /// <summary>Whole minutes the strike clock last REPORTED, so the countdown line prints once
        /// a minute rather than once a review. -1 until the first line.</summary>
        internal int StrikeClockReported = -1;

        /// <summary>Scaled <c>Time.time</c> the last strike sortie ended, or negative while none
        /// ever has — which reads as "never struck" and makes the first strike due at once.</summary>
        internal float LastStrikeAt = -1f;

        /// <summary>Scaled <c>Time.time</c> until which a point that has just been struck will not
        /// be struck again, keyed by the point (the <see cref="FobCooldownUntil"/> convention).</summary>
        internal readonly Dictionary<CommanderStrategicPoint, float> StrikeCooldownUntil = new();

        /// <summary>Where the CAP band rotation has reached (design Section 4). Every sortie that
        /// opens a CAP takes the next band from here, so consecutive patrols stack in height
        /// instead of every fighter in the wing orbiting at the same altitude.</summary>
        internal int CapBandCursor = -1;

        /// <summary>The deliberate strikes' own band rotation (fix, 2026-09-15). A strike is rare —
        /// one every six minutes — and the patrols between two of them would otherwise decide its
        /// height: with three bands, any multiple of three patrols in between put two consecutive
        /// strikes at the same altitude, and the 2026-09-15 match logged four strike orders in a row
        /// at 7,500 m. A cursor of their own makes consecutive strike packages differ by
        /// construction.</summary>
        internal int StrikeCapBandCursor = -1;

        /// <summary>The station band each home-CAP fighter was given, so a patrol re-tasked on a
        /// later review keeps the height it was sent to rather than rotating every 30 s.</summary>
        internal readonly Dictionary<Aircraft, int> HomeCapBand = new();

        /// <summary>
        /// Which of the unit economy's reductions this commander was last reported to be on
        /// (design.md, <c>unit-economy_20260918</c> §3), so the line is written once per change
        /// rather than once per 30 s review — the <c>ReportInsertionDenial</c> convention.
        /// <see cref="CommanderUnitEconomyStep.None"/> until the ceiling first binds, which is the
        /// state a commander with room to grow is genuinely in, so a match that never reaches the
        /// ceiling stays silent.
        /// </summary>
        internal CommanderUnitEconomyStep UnitEconomyStepReported = CommanderUnitEconomyStep.None;
    }

    public void TickPersistent()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null || !localHq.IsServer)
        {
            return;
        }

        if (CommanderScheduler.IsDue(ref nextReviewAt, ReviewIntervalSeconds))
        {
            Review();
        }

        if (CommanderScheduler.IsDue(ref nextMovementAt, MovementIntervalSeconds))
        {
            TickMovement();
        }

        // The logistics watch (user decision 2026-09-16): the cheap in-flight re-checks, six times a
        // review. On the scaled clock like everything else here, so it pauses with the game and
        // keeps pace at 2x and 4x — a transport covers four times the ground per second there too.
        if (CommanderScheduler.IsDue(ref nextLogisticsAt, Mathf.Max(0.5f, CommanderSettings.LogisticsWatchSeconds)))
        {
            TickLogistics();
        }
    }

    public void ResetSession()
    {
        states.Clear();
        // The point objects a stale hold-post cache keys on do not survive a mission reload
        // (discovery reruns from scratch), the same reason the garrison step's own cache was
        // cleared here.
        holdPosts.Clear();
        airfieldClearedUnits.Clear();
        nextReviewAt = CommanderScheduler.Stagger("operations.review", ReviewIntervalSeconds);
        nextMovementAt = CommanderScheduler.Stagger("operations.movement", MovementIntervalSeconds);
        nextLogisticsAt = CommanderScheduler.Stagger(
            "operations.logistics", Mathf.Max(0.5f, CommanderSettings.LogisticsWatchSeconds));
    }

    private void Review()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return;
        }

        EnsureStates(localHq);
        foreach (KeyValuePair<FactionHQ, OperationsState> entry in states)
        {
            FactionHQ hq = entry.Key;
            OperationsState state = entry.Value;
            SweepPool(hq, state);
            FoldWithdrawnPlatoons(hq, state);
            RankPoints(hq, state);
            // Reach before every planner that sends ground vehicles anywhere (reach-and-points
            // Section 2): it reads the ranking RankPoints has just built, and PlanForwardBases,
            // PlanPickets and the FOB site picker below all consult the set it fills.
            RefreshReach(hq, state);
            PlanForwardBases(hq, state);
            FillMissionTrucks(hq, state);
            PlanPickets(hq, state);
            // BEFORE PlanInsertions (user, 2026-09-14, "still no FOB!?"): a construction flight
            // opens a base that brings a dozen points into reach, while a picket flight fills one
            // point — so the FOB takes the first free transport slot and the pickets share the rest.
            // The reverse order let the picket flights fill every slot before the FOB step ran.
            ReviewFobs(hq, state);
            // After PlanPickets so the picket missions it reads are this review's, and before the
            // offensive so an insertion never competes with an attack for the same review's pot.
            PlanInsertions(hq, state);
            // BEFORE the offensive planner (fix, 2026-09-15): an attack opens its own strike on the
            // same review it is planned, and a strike that has already finished still occupies the
            // one-per-commander slot until it is closed. Closing it here means an attack planned
            // this review gets its package this review; closing it only inside UpdateStrikeClock,
            // which runs after this line, left that attack with no strike at all and nothing to
            // retry with, because TryOpenAttack is not called again for a mission that already
            // exists. The call inside UpdateStrikeClock stays: it is what closes a strike that
            // finishes while no attack is being planned, and a second call is a no-op.
            CloseFinishedStrike(hq, state);
            PlanOffensive(hq, state);
            UpdatePressure(hq, state);
            // Straight after the pressure clock (design.md, strike-packages_20260915 Section 1): an
            // attack the clock has just forced already counts as open, so the strike ahead of it and
            // the deliberate strike can never both fire on one review.
            UpdateStrikeClock(hq, state);
            // Addendum 2026-09-14 §3: reinforcement requests are sized here — after the planning
            // passes that decide which missions exist this review, before AssignPlatoons so a
            // request raised now is manned by the assignment pass of this same review.
            ReviewReinforcements(hq, state);
            // Before AssignPlatoons, after the forward-base planner: a platoon still driving from the
            // depot it was bought at, which a forward operating base has since overtaken, is taken off
            // the road here and re-raised from that base, as is a picket detachment still driving to
            // the point it garrisons (user instruction 2026-09-16, both halves). It has to run
            // before the assignment pass so the platoon it empties is already carrying its re-raise
            // bookkeeping when the empty-platoon rule there looks at it.
            ReviewGroundReseats(hq, state);
            // B1 fix: every mission this review's planning may have just opened (a forward base, a
            // picket or an attack, whether from PlanOffensive above or from the pressure clock inside
            // UpdatePressure) is manned here, before UpdateAttacks below ever tests an axis for
            // arrival. Matching platoons to missions was previously the LAST step of a review, so a
            // brand-new attack mission's axes were always empty on the very review UpdateAttacks first
            // saw them; a group with no platoon then read as vacuously "arrived", so the attack
            // launched with zero vehicles and stayed permanently Launched. The forward-base/picket/
            // offensive PLANNING order above (design SS2's pipeline) is untouched by this move.
            AssignPlatoons(hq, state);
            UpdateAttacks(hq, state);
            // After UpdateAttacks so this review's attack resolutions are already known when the
            // sortie list is derived, and its first-arrival stamps open the CAS demand (Approval 1).
            PlanAirSupport(hq, state);
            PostRequisitions(hq, state);
            // Prune again after the fills so the review line's staged= counts only vehicles still
            // in the pool, not the ones a platoon or picket took this review.
            PruneStagingPosts(state);
            // Design §3's order of application, and the order is the specification rather than a
            // preference: the idle reserve is cashed in FIRST because nothing is lost by it — those
            // vehicles were not fighting — and only then is a garrison taken off quiet ground.
            // Refusing to grow is neither of these; it happens at the buyer, which reviews after
            // this and reads the ceiling against the count these two have just reduced.
            SellSurplusPool(hq, state);
            RetireQuietGround(hq, state);
            LogReviewDiagnostics(hq, state);
        }

        PruneStates(localHq);
    }

    /// <summary>
    /// Design SS1's arrival rule: the leader is inside the objective ring and at least half the
    /// members are within <c>FormationCohesionMeters</c> of their slot. Pure, for the self-check.
    /// Inclusive on both boundaries — the convention the rest of the mod uses.
    /// </summary>
    internal static bool HasArrived(
        float leaderDistanceMeters, float objectiveRadiusMeters, int membersInCohesion, int memberCount)
    {
        return leaderDistanceMeters <= objectiveRadiusMeters && membersInCohesion * 2 >= memberCount;
    }

    // T7 extends the Holding branch below with each member's own hold post.
    private void TickMovement()
    {
        foreach (KeyValuePair<FactionHQ, OperationsState> entry in states)
        {
            List<CommanderPlatoon> platoons = entry.Value.Platoons;
            for (int i = 0; i < platoons.Count; i++)
            {
                CommanderPlatoon platoon = platoons[i];
                if (platoon.Members.Count == 0)
                {
                    continue;
                }

                // Re-elect when the leader is gone, was swept out of the platoon (a player order
                // hands the vehicle back to the player), or is driving under a player order this
                // instant: the marker and the formation anchor must follow a vehicle the platoon
                // still owns, not the one the player just drove off with.
                if (platoon.Leader == null
                    || platoon.Leader.disabled
                    || !platoon.Members.Contains(platoon.Leader)
                    || CommanderMoveService.Instance?.HasPlayerOrder(platoon.Leader) == true)
                {
                    platoon.Leader = FirstLiveMember(platoon);
                }

                // ground-tactics §4, ahead of every other branch: a platoon answering a
                // reinforcement request at a point that is STILL HELD never joins its ring — it
                // counter-attacks from a flank, or screens the approach — so this has to be decided
                // before the march branch drives it onto the point and before the hold branch puts
                // it on a post.
                if (DriveReinforcement(entry.Key, platoon))
                {
                    continue;
                }

                if (platoon.State == CommanderPlatoonState.Holding)
                {
                    // Addendum 2026-09-14 §2: a quiet platoon holding a point (or the reserve
                    // ring) that comes under attack is marked in contact here, detection only —
                    // it keeps standing on the posts below.
                    DetectHoldingContact(entry.Key, platoon);

                    // Root cause of the reserve thrash: these must feed platoon.Issued too, the same
                    // as IssuePlatoonMove below, or CountInCohesion keeps comparing a holding
                    // platoon's real position against the stale formation slot it arrived on rather
                    // than the post it is actually standing on.
                    if (platoon.Mission?.Point != null)
                    {
                        // ground-tactics §2: while the point is in contact the garrison stands on a
                        // defence arc facing the threat instead of its ring; the ring is what it
                        // returns to when the fight is over.
                        if (!DriveDefenceArc(entry.Key, platoon, platoon.Mission.Point))
                        {
                            DriveToHoldPosts(entry.Key, platoon.Mission.Point, platoon.Members, platoon.Issued);
                        }
                    }
                    else if (platoon.Mission?.Kind == CommanderMissionKind.Reserve)
                    {
                        DriveToReserveRing(entry.Key, platoon.Members, platoon.Issued);
                    }

                    continue;
                }

                if ((platoon.State == CommanderPlatoonState.Moving || platoon.State == CommanderPlatoonState.Attacking)
                    && ContactDrill(entry.Key, platoon))
                {
                    continue;
                }

                // ground-tactics §3: an attack past its release point, and anything whose
                // destination is within a kilometre of a tracked enemy, crosses the ground in
                // bounds rather than taking one long leg the game's routing drives down a road.
                if (DriveBounds(entry.Key, platoon))
                {
                    continue;
                }

                if (platoon.Posture != CommanderGroundPosture.Ring)
                {
                    platoon.ClearGroundPosture();
                }

                CommanderMoveService.IssuePlatoonMove(
                    platoon.Members, platoon.Objective, CommanderFormationShape.Wedge, platoon.Issued);
            }

            // Addendum 2026-09-14 §2: the same detection for points nobody is holding a platoon
            // on — a picket detachment or an un-manned forward base.
            DetectMissionContact(entry.Key, entry.Value);
            StagePool(entry.Key, entry.Value);
            // Last, so its order is the one that stands: anything already parked on a runway or a
            // taxiway is driven off it (user instruction, 2026-09-16). The hold-post drives above
            // will not do it — they skip a member that is already near its post, which is exactly
            // the vehicle sitting on the strip beside a clean one.
            ClearVehiclesOffAirfield(entry.Key, entry.Value);
        }
    }

    /// <summary>
    /// Parks every vehicle in the free pool on the commander's reserve ring until a platoon, a
    /// forward base's truck slot or a picket takes it.
    /// </summary>
    /// <remarks>
    /// Claiming a vehicle into the pool is bookkeeping; it issues no order, so until this ran the
    /// pool was the one set of claimed vehicles with no <c>commandedDestination</c> of its own.
    /// <c>GroundVehicle.CheckObstacles</c> re-targets exactly those on the nearest mission
    /// objective or tracked enemy every four seconds (the engine behaviour the home guard exists
    /// for, <c>Ai/CommanderEnemyCommanderDefence.cs</c> remarks), so a freshly spawned vehicle drove
    /// off alone the moment it left the depot ramp and was still walking at the enemy when the next
    /// review tried to form a platoon on it. A unit factory makes that a permanent stream, and a
    /// factory producing munitions trucks makes it endless: the recipe fill never puts a
    /// <see cref="CommanderPlatoonRole.Truck"/> in a platoon, so those sit in the pool until a
    /// forward base wants one and walk at the enemy the whole time.
    /// <para>
    /// The reserve ring is reused rather than a staging point of its own (Reuse rule 4): it is
    /// already the commander's "nothing to do, stand in the rear" position, it is derived from the
    /// territory centre so it moves with the front, and <c>DriveMembersToPosts</c> already skips a
    /// vehicle that is on station, so this costs no extra network traffic once the pool has settled.
    /// </para>
    /// </remarks>
    private void StagePool(FactionHQ hq, OperationsState state)
    {
        if (state.Pool.Count == 0)
        {
            return;
        }

        int newlyStaged = 0;
        for (int i = 0; i < state.Pool.Count; i++)
        {
            Unit unit = state.Pool[i];
            if (unit != null && !unit.disabled && !state.PoolIssued.ContainsKey(unit))
            {
                newlyStaged++;
            }

            if (unit != null && !state.PoolIdleSince.ContainsKey(unit))
            {
                state.PoolIdleSince[unit] = Time.time;
            }
        }

        DriveToReserveRing(hq, state.Pool, state.PoolIssued);
        LogStaged(hq, newlyStaged, state.Pool.Count);
    }

    // PoolSurplusToSell lived here: "how many pool vehicles are surplus to the idle cap". It was
    // removed by unit-economy_20260918 §2.1, which sells on the idle CLOCK alone. The cap it read,
    // CommanderSettings.PoolIdleCap, keeps its other job — holding the game's own depot deployment
    // loop and the buyer while the pool is full (Ai/CommanderEnemyCommanderService.cs).

    /// <summary>Whether one pool vehicle has stood idle long enough to be sold, pure.</summary>
    internal static bool PoolUnitSellable(float idleSeconds, float minutes)
    {
        return minutes > 0f && idleSeconds >= minutes * 60f;
    }

    /// <summary>
    /// Whether one sale candidate of <paramref name="role"/> is kept back for an open order-book
    /// line instead of sold, pure (user decision 2026-09-15, fix B): while
    /// <paramref name="openByRole"/> still has a line for the role, the vehicle is wanted, the line's
    /// count is decremented and the answer is true. Counted, not blanket: only as many vehicles as
    /// the book asks for are kept, so twenty idle tanks against a three-tank line still sell
    /// seventeen — a blanket skip would have pinned the whole pool over its cap, which also holds
    /// the buyer, with no way out. Called youngest-idle first so the vehicles kept are the ones
    /// most recently delivered for those lines and the sale still takes the oldest.
    /// </summary>
    internal static bool PoolUnitWanted(int[] openByRole, CommanderPlatoonRole role)
    {
        int index = (int)role;
        if (index < 0 || index >= openByRole.Length || openByRole[index] <= 0)
        {
            return false;
        }

        openByRole[index]--;
        return true;
    }

    /// <summary>Scratch for the sale, reused per review.</summary>
    private readonly List<Unit> poolSaleScratch = new();

    /// <summary>Scratch for the sale's open-line counts by role, reused per review.</summary>
    private readonly int[] poolSaleOpenByRole = new int[RoleCount];

    /// <summary>
    /// Cashes in the idle reserve (user, 2026-09-14; widened by design.md,
    /// <c>unit-economy_20260918</c> §2.1): once every picket, platoon and truck slot has taken what
    /// it wants, any vehicle that has stood on the reserve ring with nothing to do for
    /// <c>IdleReserveMinutes</c> is despawned and <c>PoolSellRefundFraction</c> of its price
    /// refunded, oldest idle first. Reassignment already runs ahead of this every review (the picket
    /// fill, platoon formation and reinforcement all draw from the pool), so what is left here is
    /// what nothing on the map wants — except a vehicle whose role the order book still has an open
    /// line for (fix B, 2026-09-15): that one is the answer to a line the buyer would otherwise buy
    /// again, and as many of them as the book asks for are kept (<see cref="PoolUnitWanted"/>).
    /// Server-only: the despawn and the refund both are.
    /// <para>
    /// The POOL CAP no longer gates the sale. It used to sell only the vehicles past
    /// <c>PoolIdleCap</c>, which left twelve idle vehicles per commander standing for the whole
    /// match; forty-five were measured sitting in reserve at the point a match reached 120 ms a
    /// frame, and the game's per-frame cost counts those exactly as it counts a tank in a fight.
    /// Every vehicle nothing wants now goes, and the cap keeps only its other job — holding the
    /// game's own depot deployment loop and the buyer while the pool is full.
    /// </para>
    /// </summary>
    private void SellSurplusPool(FactionHQ hq, OperationsState state)
    {
        if (!hq.IsServer || state.Pool.Count == 0)
        {
            return;
        }

        poolSaleScratch.Clear();
        float minutes = CommanderSettings.IdleReserveMinutes;
        for (int i = 0; i < state.Pool.Count; i++)
        {
            Unit unit = state.Pool[i];
            // Only the pool is ever sold: a platoon member, a picket vehicle and a forward base's
            // truck are not in it by construction. A pooled vehicle the PLAYER has given an order
            // to is theirs now and is skipped too (user, 2026-09-14).
            if (unit != null
                && !unit.disabled
                && CommanderMoveService.Instance?.HasPlayerOrder(unit) != true
                && state.PoolIdleSince.TryGetValue(unit, out float since)
                && PoolUnitSellable(Time.time - since, minutes))
            {
                poolSaleScratch.Add(unit);
            }
        }

        poolSaleScratch.Sort((a, b) => state.PoolIdleSince[a].CompareTo(state.PoolIdleSince[b]));

        // Keep back, youngest idle first, as many candidates of each role as the book has open
        // lines for; what remains is sold oldest first below.
        SumOrderBook(state.Requisitions, poolSaleOpenByRole);
        for (int i = poolSaleScratch.Count - 1; i >= 0; i--)
        {
            CommanderPlatoonRole role = CommanderPlatoonRoles.Of(poolSaleScratch[i].definition as VehicleDefinition);
            if (PoolUnitWanted(poolSaleOpenByRole, role))
            {
                poolSaleScratch.RemoveAt(i);
            }
        }

        int sold = 0;
        float refund = 0f;
        for (int i = 0; i < poolSaleScratch.Count; i++)
        {
            // CashInVehicle is the one definition of a sale, shared with the quiet-ground
            // retirement (Operations/CommanderOperationsUnitEconomy.cs, Reuse rule 5). It prices the
            // vehicle through CommanderEconomyService.StrategicUnitValue, the mod's single answer to
            // "what is this unit worth", which for a vehicle is the same definition value this loop
            // used to read inline.
            if (!CashInVehicle(poolSaleScratch[i], out float one))
            {
                continue;
            }

            Unit unit = poolSaleScratch[i];
            state.Pool.Remove(unit);
            state.PoolIssued.Remove(unit);
            state.PoolIdleSince.Remove(unit);
            refund += one;
            sold++;
        }

        if (sold > 0)
        {
            hq.AddFunds(refund);
            CommanderAiLog.Note(
                hq,
                $"cashes in {sold} idle vehicle(s) from the reserve for {refund:0}: nothing on the map wants them "
                    + $"({state.Pool.Count} left, idle timeout {minutes:0} minute(s)).");
        }

        poolSaleScratch.Clear();
    }

    /// <summary>
    /// Range at which a marching platoon counts a tracked hostile ground vehicle as contact and
    /// deploys. Inside the game's ground engagement ranges (tank guns and short-range missiles reach
    /// 2–4 km) so the line is formed before the shooting starts, outside the platoon's own formation
    /// spacing so a deployed line never triggers on its own dust.
    /// </summary>
    private const float ContactRangeMeters = 2500f;

    /// <summary>How long after the last tracked contact a deployed platoon stays in its line before
    /// resuming the march: one <c>ThreatMemorySeconds</c> is too long (a column re-forms only after
    /// the tracking has decayed), one movement tick too short (a contact blinking in and out of
    /// tracking would flip the platoon between line and column every 5 s).</summary>
    private const float ContactHoldSeconds = 20f;

    /// <summary>
    /// How fresh a member loss must be to count as contact evidence for a platoon or detachment
    /// standing on its posts (addendum 2026-09-14 §2): 60 s is two reviews — long enough that a
    /// loss the once-per-review sweep stamps up to 30 s after the kill still carries the 20 s
    /// contact hold through the next review — and short enough that a loss in a skirmish that is
    /// already over does not keep a point "in contact" for a fifth of the threat memory window.
    /// </summary>
    private const float LossContactSeconds = 60f;

    /// <summary>
    /// The odds at which a platoon in contact is considered outnumbered and asks for
    /// reinforcements (addendum 2026-09-14 §3): 1.0 — equal numbers is already a fair fight for
    /// the defender, and below it the platoon is outnumbered. Every point of it above 1.0 makes a
    /// request that much harder to open; the value is deliberately not a setting, it is the
    /// doctrine's own number.
    /// </summary>
    private const float ReinforceOddsRatio = 1f;

    /// <summary>
    /// The most platoons one reinforcement request may ask for. Six since the pickets-first doctrine
    /// (DECISION-013): the cap of three bound 24 times in one match, so a forward base facing a real
    /// push simply never got the answer it had measured. Six is a counter-attack in its own right,
    /// and it is affordable now for the reason the old three was not — platoons no longer form for
    /// every point on the map, so the ones that exist are available to send.
    /// </summary>
    private const int MaxReinforcementPlatoons = 6;

    /// <summary>
    /// How long every assigned platoon must stay un-outnumbered on paper before an open
    /// reinforcement request closes and the reinforcing platoons return to reserve (addendum
    /// 2026-09-14 §3): 120 s is four reviews — long enough that one quiet review while tracking
    /// blinks cannot close a request and send the reinforcements home, short enough that a fight
    /// that is genuinely over releases them inside two minutes.
    /// </summary>
    private const float ReinforceReleaseSeconds = 120f;

    /// <summary>
    /// The contact drill. A platoon on the march travels the game's road graph in whatever order
    /// the pathing leaves it — effectively a column — and drove that column straight into any enemy
    /// it met. Now, while a tracked hostile ground vehicle is inside <see cref="ContactRangeMeters"/>
    /// of the leader, the platoon deploys where it stands into a line abreast facing the contact
    /// (tanks at the centre, slot 0), and holds that line for <see cref="ContactHoldSeconds"/> after
    /// the last contact before marching on. Returns true when it issued the line this tick.
    /// </summary>
    private bool ContactDrill(FactionHQ hq, CommanderPlatoon platoon)
    {
        Unit? leader = platoon.Leader;
        if (leader == null || leader.disabled)
        {
            return false;
        }

        GlobalPosition here = leader.transform.GlobalPosition();
        bool wasInContact = platoon.InContactUntil >= Time.time;
        if (TryNearestTrackedHostile(hq, here, ContactRangeMeters, out GlobalPosition hostile, out float distance))
        {
            platoon.InContactUntil = Time.time + ContactHoldSeconds;
            if (!wasInContact)
            {
                LogContact(hq, platoon, distance);
            }
        }
        else if (!wasInContact)
        {
            return false;
        }
        else
        {
            // Holding the line on memory: face where the contact last was, which is where the
            // platoon's own formation already points.
            hostile = platoon.ContactBearingAnchor;
        }

        platoon.ContactBearingAnchor = hostile;
        float facing = CommanderMoveService.HeadingDegrees(here.ToLocalPosition(), hostile.ToLocalPosition());
        CommanderMoveService.IssuePlatoonMove(
            platoon.Members, platoon.LineAnchor(here), CommanderFormationShape.Line, platoon.Issued, facing);
        return true;
    }

    /// <summary>
    /// Nearest tracked hostile ground vehicle to <paramref name="from"/> within <paramref name="range"/>,
    /// from the commander's own tracking database with the same freshness and unit filters as
    /// <c>CountObserved</c> (design SS3: the commander acts on what it has tracked, not the truth).
    /// </summary>
    private static bool TryNearestTrackedHostile(
        FactionHQ hq,
        GlobalPosition from,
        float range,
        out GlobalPosition position,
        out float distance)
    {
        return TryNearestTrackedHostile(
            hq, from, range, CommanderEnemyCommanderService.ThreatMemorySeconds, out position, out distance, out _);
    }

    /// <summary>
    /// Whether a contact seen <paramref name="secondsSinceSeen"/> ago still counts, pure. Exactly on
    /// the window still counts — the inclusive-on-the-patient-side convention the rest of the mod
    /// uses. A window of zero or less counts nothing, so a rule can be turned off by its own setting
    /// rather than by deleting the call.
    /// </summary>
    internal static bool ContactStillCounts(float secondsSinceSeen, float memorySeconds)
    {
        return memorySeconds > 0f && secondsSinceSeen <= memorySeconds;
    }

    /// <param name="memorySeconds">How long a contact still counts. The home defence reads its own
    /// 45 s here, which is the right window for reacting to a raid; the siting and standoff rules
    /// read <c>CommanderSettings.StandoffContactMemorySeconds</c>, because a column seen five minutes
    /// ago is still somewhere near that ground when the question is where to put a base or land a
    /// transport (user report 2026-09-15).</param>
    /// <param name="label">What the nearest contact is, for the log line that says why a point has
    /// gone front or a flight has been turned round. Empty when none was found.</param>
    private static bool TryNearestTrackedHostile(
        FactionHQ hq,
        GlobalPosition from,
        float range,
        float memorySeconds,
        out GlobalPosition position,
        out float distance,
        out string label)
    {
        position = default;
        distance = float.MaxValue;
        label = string.Empty;
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (!ContactStillCounts(now - info.lastSpottedTime, memorySeconds)
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

            float d = CommanderGameAccess.HorizontalDistance(from.AsVector3(), info.lastKnownPosition.AsVector3());
            if (d <= range && d < distance)
            {
                distance = d;
                position = info.lastKnownPosition;
                label = CommanderGameAccess.GetUnitLabel(unit);
            }
        }

        return distance <= range;
    }

    /// <summary>
    /// Addendum 2026-09-14 §2, the detection-only half of the contact drill: a platoon Holding
    /// its posts — a forward base garrison or the reserve ring — that sees a tracked hostile
    /// ground unit inside <see cref="ContactRangeMeters"/> of its leader, or has lost a member
    /// inside <see cref="LossContactSeconds"/>, is marked in contact for the same
    /// <see cref="ContactHoldSeconds"/> hold the marching drill uses, and is NOT moved off its
    /// posts: the posts it already stands on are its fighting position. The mark is what the
    /// "platoon in contact" air demand, the reinforcement odds rule and the marker's
    /// <c>In contact</c> flag all read.
    /// </summary>
    private void DetectHoldingContact(FactionHQ hq, CommanderPlatoon platoon)
    {
        Unit? leader = platoon.Leader;
        if (leader == null || leader.disabled)
        {
            return;
        }

        GlobalPosition here = leader.transform.GlobalPosition();
        bool wasInContact = platoon.InContactUntil >= Time.time;
        if (TryNearestTrackedHostile(hq, here, ContactRangeMeters, out GlobalPosition hostile, out float distance))
        {
            platoon.InContactUntil = Time.time + ContactHoldSeconds;
            platoon.ContactBearingAnchor = hostile;
            // The ground this platoon is standing on is being contested, so its point is not quiet
            // (unit-economy_20260918 §2.4). Null when the platoon is holding the reserve ring, which
            // is not a point and is never retired from.
            if (platoon.Mission != null)
            {
                platoon.Mission.QuietSince = Time.time;
            }

            if (!wasInContact)
            {
                LogPostContact(hq, platoon, distance);
            }

            return;
        }

        if (LossIsRecent(platoon.LastLossAt, Time.time, LossContactSeconds))
        {
            platoon.InContactUntil = Time.time + ContactHoldSeconds;
            if (platoon.Mission != null)
            {
                platoon.Mission.QuietSince = Time.time;
            }

            // Nothing is tracked to face: the contact sortie orbits the platoon itself, the only
            // evidence of where the fight is.
            platoon.ContactBearingAnchor = here;
            if (!wasInContact)
            {
                LogPostContact(hq, platoon, -1f);
            }
        }
    }

    /// <summary>
    /// The same detection for a point nobody is holding a platoon on (addendum 2026-09-14 §2):
    /// a picket detachment, or a forward base waiting for its garrison, is in contact while a
    /// tracked hostile ground unit is inside <see cref="ContactRangeMeters"/> of the point or a
    /// picket member was lost inside <see cref="LossContactSeconds"/>. The clock it sets is the
    /// mission's own (<see cref="CommanderOperationsMission.ContactUntil"/>), which the air
    /// demand reads so a point under attack raises CAS without a platoon on it.
    /// </summary>
    private void DetectMissionContact(FactionHQ hq, OperationsState state)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if ((mission.Kind != CommanderMissionKind.Picket && mission.Kind != CommanderMissionKind.ForwardBase)
                || mission.Point == null)
            {
                continue;
            }

            if (TryNearestTrackedHostile(hq, mission.Point.Position, ContactRangeMeters, out _, out _)
                || LossIsRecent(mission.LastLossAt, Time.time, LossContactSeconds))
            {
                mission.ContactUntil = Time.time + ContactHoldSeconds;
                // The quiet-ground clock is restarted by the SAME evidence, at the five-second
                // cadence this detection already runs at rather than at the thirty-second review, so
                // a contact between two reviews can never be missed by the retirement
                // (unit-economy_20260918 §2.4).
                mission.QuietSince = Time.time;
            }
        }
    }

    /// <summary>
    /// True while <paramref name="members"/> holds at least one vehicle the sweep is about to
    /// remove for dying, disappearing or changing hands — the loss that counts as contact
    /// evidence. A vehicle the player has merely taken orders on is a hand-over, not a loss, and
    /// does not count.
    /// </summary>
    private static bool HasCombatLoss(FactionHQ hq, IReadOnlyList<Unit> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            Unit? unit = members[i];
            if (unit == null || unit.disabled || unit.NetworkHQ != hq)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Pure, for the self-check. A member lost at <paramref name="lastLossAt"/> counts as contact
    /// evidence for <paramref name="windowSeconds"/> after it happened, inclusive at the boundary
    /// — the convention the rest of the mod uses. A negative timestamp (never lost one) never
    /// counts.
    /// </summary>
    internal static bool LossIsRecent(float lastLossAt, float now, float windowSeconds)
    {
        return lastLossAt >= 0f && now - lastLossAt <= windowSeconds;
    }

    /// <summary>
    /// Pure, for the self-check. Platoons of reinforcement one platoon outnumbered on paper asks
    /// for (addendum 2026-09-14 §3): none while the hostiles it has observed do not exceed its own
    /// live strength × <paramref name="oddsRatio"/> — equal numbers is already a fair fight for
    /// the defender — otherwise one platoon per <paramref name="platoonSize"/> of the deficit,
    /// rounded up, capped at <see cref="MaxReinforcementPlatoons"/>.
    /// </summary>
    internal static int ReinforcementsFor(int observedHostiles, int strength, int platoonSize, float oddsRatio)
    {
        if (observedHostiles <= strength * oddsRatio)
        {
            return 0;
        }

        int deficit = observedHostiles - strength;
        return Mathf.Min(MaxReinforcementPlatoons, Mathf.CeilToInt(deficit / Mathf.Max(1f, platoonSize)));
    }

    /// <summary>
    /// Pure, for the self-check. An open reinforcement request releases once the release clock
    /// has run <paramref name="releaseSeconds"/> from <paramref name="belowSince"/> — inclusive at
    /// the boundary, the convention the rest of the mod uses. A negative start (never
    /// un-outnumbered) never releases.
    /// </summary>
    internal static bool ReinforcementReleases(float belowSince, float now, float releaseSeconds)
    {
        return belowSince >= 0f && now - belowSince >= releaseSeconds;
    }

    /// <summary>
    /// Addendum 2026-09-14 §3, once per review per mission: a holding or attacking platoon in
    /// contact whose observed hostiles exceed its own live strength ×
    /// <see cref="ReinforceOddsRatio"/> opens a reinforcement request on its mission, and the
    /// request rides <see cref="CommanderOperationsMission.WantedPlatoons"/> — raised by the
    /// capped count, lowered again when it closes — so the existing assignment pass, the strip
    /// step beside it and the order book all serve a request with no changes.
    /// <para>Readings are per platoon, the spec's own rule, and the mission takes the WORST
    /// in-contact platoon's request rather than the sum: two platoons at one point observe the
    /// same hostiles, and summing would count every one of them twice. The release timer runs
    /// while NO assigned platoon is outnumbered on paper — reinforcing platoons join the test
    /// once they are assigned — and the request closes when that has held for
    /// <see cref="ReinforceReleaseSeconds"/>, sending the reinforcing platoons back to reserve.</para>
    /// </summary>
    private void ReviewReinforcements(FactionHQ hq, OperationsState state)
    {
        for (int m = 0; m < state.Missions.Count; m++)
        {
            CommanderOperationsMission mission = state.Missions[m];
            if (mission.Kind != CommanderMissionKind.ForwardBase && mission.Kind != CommanderMissionKind.Attack)
            {
                continue;
            }

            int requested = 0;
            CommanderPlatoon? requester = null;
            int observedForLog = 0;
            int strengthForLog = 0;
            bool anyReadable = false;
            bool allBelow = true;
            for (int i = 0; i < mission.Assigned.Count; i++)
            {
                CommanderPlatoon platoon = mission.Assigned[i];
                if (platoon.State != CommanderPlatoonState.Holding && platoon.State != CommanderPlatoonState.Attacking)
                {
                    continue;
                }

                Unit? leader = platoon.Leader;
                if (leader == null || leader.disabled)
                {
                    // Unreadable this instant; the sweep settles it next review.
                    continue;
                }

                anyReadable = true;
                int observed = EffectiveObserved(
                    CountObserved(hq, leader.transform.GlobalPosition()),
                    GetObservedFloor(state, ObservedFloorKey(mission.Point, mission.TargetAirbase)));
                int strength = platoon.Members.Count;
                if (platoon.InContactUntil >= Time.time)
                {
                    int need = ReinforcementsFor(
                        observed, strength, Mathf.Max(1, CommanderSettings.OperationsPlatoonSize), ReinforceOddsRatio);
                    if (need > requested)
                    {
                        requested = need;
                        requester = platoon;
                        observedForLog = observed;
                        strengthForLog = strength;
                    }
                }

                // The release test reads every platoon, in contact or not: the spec's release is
                // about the odds picture, not about the shooting still going.
                if (observed >= strength)
                {
                    allBelow = false;
                }
            }

            if (requested > 0)
            {
                if (requested != mission.ReinforcePlatoons && requester != null)
                {
                    CommanderAiLog.Note(
                        hq,
                        $"{requester.Name} requests {requested} platoon(s) of reinforcements at {mission.Label} "
                            + $"({observedForLog} observed vs {strengthForLog}).");
                    mission.WantedPlatoons += requested - mission.ReinforcePlatoons;
                    mission.ReinforcePlatoons = requested;
                }

                // A fight that still needs platoons keeps the ones it has, however quiet.
                mission.ReinforceBelowSince = -1f;
            }
            else if (mission.ReinforcePlatoons > 0)
            {
                if (!anyReadable)
                {
                    // Nothing left to read the fight with: the garrison is gone and the request
                    // closes now rather than holding platoons against a fight nobody is in.
                    CloseReinforcementRequest(hq, mission);
                }
                else if (allBelow)
                {
                    if (mission.ReinforceBelowSince < 0f)
                    {
                        mission.ReinforceBelowSince = Time.time;
                    }

                    if (ReinforcementReleases(mission.ReinforceBelowSince, Time.time, ReinforceReleaseSeconds))
                    {
                        CloseReinforcementRequest(hq, mission);
                    }
                }
                else
                {
                    mission.ReinforceBelowSince = -1f;
                }
            }
        }
    }

    /// <summary>
    /// Closes an open reinforcement request (addendum 2026-09-14 §3): the wanted count falls back
    /// to its baseline, and every platoon that was answering the request is released — the
    /// assignment pass's reserve bucket picks them up later in the same review and sends them
    /// home.
    /// </summary>
    private static void CloseReinforcementRequest(FactionHQ hq, CommanderOperationsMission mission)
    {
        mission.WantedPlatoons = Mathf.Max(0, mission.WantedPlatoons - mission.ReinforcePlatoons);
        mission.ReinforcePlatoons = 0;
        mission.ReinforceBelowSince = -1f;
        for (int i = mission.Assigned.Count - 1; i >= 0; i--)
        {
            if (mission.Assigned[i].ReinforcesLabel == mission.Label)
            {
                ReleaseFromMission(mission.Assigned[i]);
            }
        }

        CommanderAiLog.Note(hq, $"{mission.Label}: reinforcement request closed.");
    }

    /// <summary>First live member the player is not driving; failing that, any live member.</summary>
    private static Unit? FirstLiveMember(CommanderPlatoon platoon)
    {
        Unit? fallback = null;
        for (int i = 0; i < platoon.Members.Count; i++)
        {
            Unit member = platoon.Members[i];
            if (member == null || member.disabled)
            {
                continue;
            }

            if (CommanderMoveService.Instance?.HasPlayerOrder(member) != true)
            {
                return member;
            }

            fallback ??= member;
        }

        return fallback;
    }

    /// <summary>The arrival rule at its five named boundaries (design SS1).</summary>
    /// <summary>The march order the wedge relies on: armour at the point, air defence behind.</summary>
    private static void CheckMarchOrder(List<string> failures)
    {
        Expect(failures, "armour leads the march", MarchRank(CommanderPlatoonRole.Armour) == 0, true);
        Expect(
            failures,
            "carriers ride behind the armour",
            MarchRank(CommanderPlatoonRole.Carrier) > MarchRank(CommanderPlatoonRole.Armour),
            true);
        Expect(
            failures,
            "air defence rides behind the carriers",
            MarchRank(CommanderPlatoonRole.AirDefence) > MarchRank(CommanderPlatoonRole.Carrier),
            true);
        Expect(failures, "a truck brings up the rear", MarchRank(CommanderPlatoonRole.Truck) == MarchOrder.Length - 1, true);
    }

    private static void CheckArrival(List<string> failures)
    {
        Expect(failures, "a leader outside the objective ring has not arrived", !HasArrived(401f, 400f, 6, 6), true);
        Expect(failures, "a leader exactly on the ring edge counts as inside", HasArrived(400f, 400f, 6, 6), true);
        Expect(
            failures,
            "a leader on the objective with fewer than half the platoon closed up has not arrived",
            !HasArrived(100f, 400f, 2, 6),
            true);
        Expect(failures, "exactly half the platoon closed up counts as arrived", HasArrived(100f, 400f, 3, 6), true);
        Expect(failures, "a one-vehicle platoon arrives when its leader does", HasArrived(0f, 400f, 1, 1), true);
    }

    /// <summary>
    /// The loss window that carries contact for a platoon standing on its posts (addendum
    /// 2026-09-14 §2), at its named boundaries.
    /// </summary>
    private static void CheckContactEvidence(List<string> failures)
    {
        Expect(failures, "a member lost exactly at the loss window's edge still counts as contact", LossIsRecent(0f, 60f, 60f), true);
        Expect(failures, "a member lost just past the loss window no longer counts", LossIsRecent(0f, 60.5f, 60f), false);
        Expect(failures, "a loss right now counts", LossIsRecent(60f, 60f, 60f), true);
        Expect(failures, "a platoon that never lost a member is not in contact by loss", LossIsRecent(-1f, 0f, 60f), false);
        Expect(
            failures,
            "the loss window outlasts the contact hold, or a loss would carry no contact at all",
            LossContactSeconds > ContactHoldSeconds,
            true);
        Expect(
            failures,
            "the loss window covers a sweep-stamped loss plus one hold, so evidence is never stale on arrival",
            LossContactSeconds >= ReviewIntervalSeconds + ContactHoldSeconds,
            true);
    }

    /// <summary>
    /// The reinforcement odds rule, count arithmetic, cap and release timer (addendum
    /// 2026-09-14 §3), at their named boundaries.
    /// </summary>
    private static void CheckReinforcements(List<string> failures)
    {
        Expect(failures, "equal numbers is already a fair fight for the defender: no reinforcements", ReinforcementsFor(6, 6, 6, ReinforceOddsRatio), 0);
        Expect(failures, "fewer observed than strength asks for nothing", ReinforcementsFor(5, 6, 6, ReinforceOddsRatio), 0);
        Expect(failures, "one hostile over strength asks for one platoon", ReinforcementsFor(7, 6, 6, ReinforceOddsRatio), 1);
        Expect(failures, "a deficit of one platoon and a bit rounds up to two platoons", ReinforcementsFor(13, 6, 6, ReinforceOddsRatio), 2);
        Expect(failures, "a deficit of exactly two platoons asks for exactly two", ReinforcementsFor(18, 6, 6, ReinforceOddsRatio), 2);
        Expect(failures, "an enormous deficit is capped at the maximum", ReinforcementsFor(100, 1, 6, ReinforceOddsRatio), MaxReinforcementPlatoons);
        Expect(failures, "a deficit of exactly six platoons asks for all six", ReinforcementsFor(42, 6, 6, ReinforceOddsRatio), 6);
        Expect(failures, "a deficit of seven platoons is still capped at six", ReinforcementsFor(48, 6, 6, ReinforceOddsRatio), 6);
        Expect(failures, "the cap is large enough to answer a push a garrison can measure", MaxReinforcementPlatoons >= 6, true);
        Expect(failures, "a sterner odds ratio raises the bar", ReinforcementsFor(9, 6, 6, 1.5f), 0);
        Expect(failures, "a sterner odds ratio still asks once the bar is cleared", ReinforcementsFor(10, 6, 6, 1.5f), 1);
        Expect(failures, "the release timer fires exactly at its boundary", ReinforcementReleases(0f, 120f, ReinforceReleaseSeconds), true);
        Expect(failures, "the release timer holds one moment short", ReinforcementReleases(0f, 119.9f, ReinforceReleaseSeconds), false);
        Expect(failures, "a request never un-outnumbered never releases", ReinforcementReleases(-1f, 1000f, ReinforceReleaseSeconds), false);
        Expect(
            failures,
            "the release timer outlasts one review, or one quiet review would close a request",
            ReinforceReleaseSeconds > ReviewIntervalSeconds,
            true);
        Expect(failures, "the reinforcement cap is at least one platoon", MaxReinforcementPlatoons >= 1, true);
    }

    /// <summary>
    /// Fills a platoon's recipe from a candidate list, pure so the self-check can drive it with
    /// synthetic data. Pass 1 takes up to <paramref name="want"/>[role] candidates per role in
    /// Armour/Carrier/AirDefence order, preferring an entry whose <paramref name="prefers"/> is
    /// true (design SS1: a capture-capable carrier beats one that cannot take ground). Pass 2 fills
    /// any slot the recipe left open with any remaining non-<see cref="CommanderPlatoonRole.Truck"/>
    /// candidate (design SS1: "Unfillable slot -&gt; any combat vehicle"; a munitions truck is
    /// requisitioned separately and never fills a combat slot). Never exceeds
    /// <paramref name="platoonSize"/> picks and never repeats an index.
    /// </summary>
    internal static void FillRecipe(
        IReadOnlyList<CommanderPlatoonRole> roles,
        IReadOnlyList<bool> prefers,
        IReadOnlyList<int> want,
        int platoonSize,
        List<int> picks)
    {
        picks.Clear();
        bool[] taken = new bool[roles.Count];
        CommanderPlatoonRole[] recipeOrder =
        {
            CommanderPlatoonRole.Armour, CommanderPlatoonRole.Carrier, CommanderPlatoonRole.AirDefence,
        };

        for (int r = 0; r < recipeOrder.Length && picks.Count < platoonSize; r++)
        {
            CommanderPlatoonRole role = recipeOrder[r];
            int wanted = (int)role < want.Count ? want[(int)role] : 0;
            int filled = 0;

            for (int i = 0; i < roles.Count && filled < wanted && picks.Count < platoonSize; i++)
            {
                if (!taken[i] && roles[i] == role && i < prefers.Count && prefers[i])
                {
                    taken[i] = true;
                    picks.Add(i);
                    filled++;
                }
            }

            for (int i = 0; i < roles.Count && filled < wanted && picks.Count < platoonSize; i++)
            {
                if (!taken[i] && roles[i] == role)
                {
                    taken[i] = true;
                    picks.Add(i);
                    filled++;
                }
            }
        }

        for (int i = 0; i < roles.Count && picks.Count < platoonSize; i++)
        {
            if (!taken[i] && roles[i] != CommanderPlatoonRole.Truck)
            {
                taken[i] = true;
                picks.Add(i);
            }
        }
    }

    /// <summary>Role and capture-preference arrays for every free-pool vehicle, scratch for
    /// <see cref="FillRecipe"/>.</summary>
    private void BuildRecipeArrays(List<Unit> pool)
    {
        recipeRoles.Clear();
        recipePrefers.Clear();
        for (int i = 0; i < pool.Count; i++)
        {
            VehicleDefinition? definition = pool[i]?.definition as VehicleDefinition;
            CommanderPlatoonRole role = CommanderPlatoonRoles.Of(definition);
            recipeRoles.Add(role);
            recipePrefers.Add(role == CommanderPlatoonRole.Carrier && definition != null && definition.captureStrength > 0f);
        }
    }

    /// <summary>
    /// The next platoon name this commander has not used, and the counter moved on. Reserved
    /// separately from <see cref="CreatePlatoon"/> because an air-mobile platoon is NAMED when its
    /// lift is ordered and CREATED when its first load lands, several minutes later: the log line
    /// that announces the raise, the lift's own lines and the review line all have to name the same
    /// platoon, and a name read without reserving it would be handed to whatever formed in between.
    /// </summary>
    private static string ReservePlatoonName(OperationsState state)
    {
        string name = $"{PlatoonNames[state.NextPlatoonNumber % PlatoonNames.Length]} PLATOON";
        state.NextPlatoonNumber++;
        return name;
    }

    /// <summary>
    /// The empty platoon every formation path starts from: named, on the roster, forming from this
    /// moment, and carrying the establishment and the armour slot count of the RECIPE it was raised
    /// on (the teeth rule reads that rather than the live setting — see
    /// <see cref="CommanderPlatoon.ArmourWanted"/>). Moved out of <see cref="TryFormPlatoon"/> when
    /// the air-mobile lift became its second caller (Reuse rule 5): the lift has no free pool to
    /// draw from, its vehicles arrive two at a time from the sky, but the platoon they join must be
    /// the same object with the same bookkeeping as one raised at a depot.
    /// </summary>
    private static CommanderPlatoon CreatePlatoon(
        OperationsState state, string name, int establishment, int armourWanted)
    {
        CommanderPlatoon platoon = new()
        {
            Name = name,
            Establishment = establishment,
            ArmourWanted = armourWanted,
            State = CommanderPlatoonState.Forming,
            FormingSince = Time.time,
        };
        state.Platoons.Add(platoon);
        return platoon;
    }

    /// <summary>
    /// Forms a new platoon from the free pool, up to <paramref name="wantedSize"/> vehicles by
    /// recipe. Forms on whatever the recipe fill returns as long as at least one member came back
    /// (design SS1: a platoon forms on what it has and requisitions the rest — a platoon of one is
    /// <see cref="CommanderPlatoonState.Forming"/>, not a platoon that never exists).
    /// </summary>
    /// <param name="purpose">What this platoon is being built for, already phrased for the log —
    /// <c>for ForwardBase CROSSROADS 13</c>, <c>for Attack Maris Airport</c> or <c>as the
    /// reserve</c> (DECISION-013: a platoon forms only for a purpose facing the enemy, and the log
    /// has to say which one, or a match cannot be read back from it).</param>
    private CommanderPlatoon? TryFormPlatoon(FactionHQ hq, OperationsState state, int wantedSize, string purpose)
    {
        BuildRecipeArrays(state.Pool);
        int[] want =
        {
            CommanderSettings.OperationsRecipeArmour,
            CommanderSettings.OperationsRecipeCarrier,
            CommanderSettings.OperationsRecipeAirDefence,
        };
        FillRecipe(recipeRoles, recipePrefers, want, wantedSize, recipePicks);
        if (recipePicks.Count == 0)
        {
            return null;
        }

        CommanderPlatoon platoon = CreatePlatoon(
            state, ReservePlatoonName(state), wantedSize, CommanderSettings.OperationsRecipeArmour);

        TakePicksFromPool(state.Pool, recipePicks, platoon.Members);
        OrderForMarch(platoon.Members);
        platoon.Leader = platoon.Members.Count > 0 ? platoon.Members[0] : null;
        // A forming platoon gathers where its first vehicle stands, on clear ground off the road
        // and off the runway, and waits there for the rest. Without an objective of its own the
        // movement tick drove every forming platoon toward the default position: the map origin.
        platoon.Objective = platoon.Leader != null
            ? ChooseFormUpPoint(platoon.Leader.transform.GlobalPosition())
            : CommanderCaptureService.GetTerritoryCenter(hq);
        CommanderAiLog.Note(hq, $"forms {platoon.Name} {purpose}: {platoon.Members.Count}/{wantedSize} vehicles.");
        return platoon;
    }

    /// <summary>
    /// How long a forming platoon waits for its missing vehicles before it moves out with what it
    /// has. Three minutes is six buy reviews: if the buyer has not filled the slot by then it is
    /// not going to soon, and a five-vehicle platoon at the front beats a full one at the depot.
    /// </summary>
    private const float FormUpTimeoutSeconds = 180f;

    /// <summary>
    /// Whether a platoon may take a mission: it has no mission, is not withdrawing, and has either
    /// finished forming (full) or waited out <see cref="FormUpTimeoutSeconds"/>. The gate is what
    /// makes a platoon leave the form-up point together instead of the first vehicle driving off
    /// alone the review it was bought. Pure, for the self-check.
    /// </summary>
    internal static bool IsReadyToMoveOut(int strength, int establishment, float formingSeconds, float timeoutSeconds)
    {
        return strength >= establishment || formingSeconds >= timeoutSeconds;
    }

    private static bool IsAvailableForMission(CommanderPlatoon platoon)
    {
        if (platoon.Mission != null || platoon.State == CommanderPlatoonState.Withdrawing)
        {
            return false;
        }

        return platoon.State != CommanderPlatoonState.Forming
            || IsReadyToMoveOut(platoon.Members.Count, platoon.Establishment, Time.time - platoon.FormingSince, FormUpTimeoutSeconds);
    }

    /// <summary>Ring the form-up spot is looked for in around the first vehicle: far enough to be
    /// off the depot apron and the road it drove out on, near enough that the rest of the platoon
    /// reaches it in under a minute.</summary>
    private const float FormUpMinMeters = 150f;
    private const float FormUpMaxMeters = 400f;

    /// <summary>
    /// A form-up spot near <paramref name="around"/> that the shared siting rule accepts with no
    /// faction attached (the same probe discovery uses for a resource site: clear of roads, runways
    /// and other units' footprints). Falls back to the anchor itself when every probe fails, which
    /// is still better than the origin.
    /// </summary>
    private static GlobalPosition ChooseFormUpPoint(GlobalPosition around)
    {
        CommanderEconomyService? economy = CommanderEconomyService.Instance;
        BuildingDefinition? probe = economy?.MineDefinition;
        if (economy == null || probe == null)
        {
            return OffAirfieldStandingPoint(around, around, out _);
        }

        for (int attempt = 0; attempt < CommanderEconomyService.EnemySiteAttempts; attempt++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(FormUpMinMeters, FormUpMaxMeters);
            GlobalPosition candidate = new(
                around.x + Mathf.Cos(angle) * distance,
                around.y,
                around.z + Mathf.Sin(angle) * distance);
            if (economy.IsSiteAllowed(probe, candidate, null, out _))
            {
                return CommanderGameAccess.SnapToTerrain(candidate);
            }
        }

        // Every probe failed, so the anchor itself is the answer — and the anchor is wherever the
        // first vehicle happens to stand, which on a base built round a depot is the apron. The
        // probes were the only airfield check on this path (user instruction, 2026-09-16).
        return OffAirfieldStandingPoint(around, around, out _);
    }

    /// <summary>
    /// Tops up a below-establishment platoon from the free pool, wanting only the roles it is
    /// still short of (so a platoon that already has its full complement of armour is not handed a
    /// fourth one while its carrier slot sits empty).
    /// </summary>
    private void ReinforcePlatoon(OperationsState state, CommanderPlatoon platoon)
    {
        int need = platoon.Establishment - platoon.Members.Count;
        if (need <= 0 || state.Pool.Count == 0)
        {
            return;
        }

        int[] have = new int[3];
        for (int i = 0; i < platoon.Members.Count; i++)
        {
            CommanderPlatoonRole role = CommanderPlatoonRoles.Of(platoon.Members[i]?.definition as VehicleDefinition);
            if ((int)role < have.Length)
            {
                have[(int)role]++;
            }
        }

        int[] want =
        {
            Mathf.Max(0, CommanderSettings.OperationsRecipeArmour - have[(int)CommanderPlatoonRole.Armour]),
            Mathf.Max(0, CommanderSettings.OperationsRecipeCarrier - have[(int)CommanderPlatoonRole.Carrier]),
            Mathf.Max(0, CommanderSettings.OperationsRecipeAirDefence - have[(int)CommanderPlatoonRole.AirDefence]),
        };

        BuildRecipeArrays(state.Pool);
        FillRecipe(recipeRoles, recipePrefers, want, need, recipePicks);
        if (recipePicks.Count == 0)
        {
            return;
        }

        TakePicksFromPool(state.Pool, recipePicks, platoon.Members);
        OrderForMarch(platoon.Members);
        // A tank bought to replace a lost one takes the point back from whoever held it meanwhile.
        platoon.Leader = platoon.Members.Count > 0 ? platoon.Members[0] : null;
    }

    /// <summary>
    /// March order of a platoon's roles, front to rear: the formation slot is the member's index
    /// (<c>CommanderDestinationFormation.ApplyOffset</c>, slot 0 the point of the wedge), so this
    /// is what puts the tanks in front and the air defence behind them. Anything unclassified
    /// rides between the carriers and the air defence; a truck, if one ever joins, brings up the rear.
    /// </summary>
    private static readonly CommanderPlatoonRole[] MarchOrder =
    {
        CommanderPlatoonRole.Armour,
        CommanderPlatoonRole.Carrier,
        CommanderPlatoonRole.Other,
        CommanderPlatoonRole.AirDefence,
        CommanderPlatoonRole.Truck,
    };

    /// <summary>
    /// Stable re-order of <paramref name="members"/> into <see cref="MarchOrder"/>. Stable so two
    /// tanks keep their relative slots between reviews and are not re-issued destinations for a
    /// swap. The self-check covers the rank table via <see cref="MarchRank"/>.
    /// </summary>
    internal static void OrderForMarch(List<Unit> members)
    {
        // Insertion sort: six vehicles, already mostly ordered, and List.Sort is not stable.
        for (int i = 1; i < members.Count; i++)
        {
            // A member slot can read null between a unit's death and the next review's sweep. The
            // sort carries such entries as rank-Other exactly as it did before the nullable pass
            // noticed — the `!` below is that behaviour kept, not a promise about the list.
            Unit? moving = members[i];
            int rank = MarchRank(CommanderPlatoonRoles.Of(moving?.definition as VehicleDefinition));
            int j = i - 1;
            while (j >= 0 && MarchRank(CommanderPlatoonRoles.Of(members[j]?.definition as VehicleDefinition)) > rank)
            {
                members[j + 1] = members[j];
                j--;
            }

            members[j + 1] = moving!;
        }
    }

    /// <summary>Position of <paramref name="role"/> in <see cref="MarchOrder"/>; unknown roles last.</summary>
    internal static int MarchRank(CommanderPlatoonRole role)
    {
        int index = System.Array.IndexOf(MarchOrder, role);
        return index < 0 ? MarchOrder.Length : index;
    }

    /// <summary>Moves the picked pool indices into <paramref name="destination"/>, highest index
    /// first so an earlier removal never invalidates a later index.</summary>
    private static void TakePicksFromPool(List<Unit> pool, List<int> picks, List<Unit> destination)
    {
        List<int> sortedPicks = new(picks);
        sortedPicks.Sort();
        List<Unit> chosen = new(sortedPicks.Count);
        for (int i = sortedPicks.Count - 1; i >= 0; i--)
        {
            int index = sortedPicks[i];
            chosen.Add(pool[index]);
            pool.RemoveAt(index);
        }

        chosen.Reverse();
        destination.AddRange(chosen);
    }

    /// <summary>
    /// Recipe fill with fallbacks, at the default 3 armour / 1 carrier / 2 air defence and
    /// <c>platoonSize = 6</c>.
    /// </summary>
    private static void CheckRecipe(List<string> failures)
    {
        List<int> picks = new();
        CommanderPlatoonRole[] noPreferenceRoles =
        {
            CommanderPlatoonRole.Armour, CommanderPlatoonRole.Armour, CommanderPlatoonRole.Armour,
            CommanderPlatoonRole.Carrier, CommanderPlatoonRole.AirDefence, CommanderPlatoonRole.AirDefence,
        };
        bool[] noPreferences = { false, false, false, false, false, false };
        int[] defaultWant = { 3, 1, 2 };

        FillRecipe(noPreferenceRoles, noPreferences, defaultWant, 6, picks);
        ExpectSequence(failures, "a pool that matches the recipe fills it exactly", picks, 0, 1, 2, 3, 4, 5);

        FillRecipe(
            new[] { CommanderPlatoonRole.Carrier, CommanderPlatoonRole.Carrier },
            new[] { false, true },
            new[] { 0, 1, 0 },
            6,
            picks);
        ExpectSequence(failures, "a capture-capable carrier is taken ahead of one that cannot take ground", picks, 1, 0);

        FillRecipe(
            new[]
            {
                CommanderPlatoonRole.Armour, CommanderPlatoonRole.Armour, CommanderPlatoonRole.Armour,
                CommanderPlatoonRole.Armour, CommanderPlatoonRole.Armour, CommanderPlatoonRole.Armour,
            },
            noPreferences,
            defaultWant,
            6,
            picks);
        ExpectSequence(failures, "a pool of nothing but armour still forms a full platoon", picks, 0, 1, 2, 3, 4, 5);

        FillRecipe(
            new[] { CommanderPlatoonRole.Armour, CommanderPlatoonRole.AirDefence },
            new[] { false, false },
            defaultWant,
            6,
            picks);
        ExpectSequence(failures, "a short pool forms a short platoon rather than none", picks, 0, 1);

        FillRecipe(
            new[]
            {
                CommanderPlatoonRole.Armour, CommanderPlatoonRole.Truck, CommanderPlatoonRole.Truck,
                CommanderPlatoonRole.Truck, CommanderPlatoonRole.Truck, CommanderPlatoonRole.Truck,
            },
            noPreferences,
            defaultWant,
            6,
            picks);
        ExpectSequence(failures, "a munitions truck never fills a combat slot", picks, 0);

        // Read into locals first: CommanderSettings.CheckDefencePosture's reasoning applies just as
        // well to a config-file value as to a const — the failure branch has to survive a retune.
        int armour = CommanderSettings.OperationsRecipeArmour;
        int carrier = CommanderSettings.OperationsRecipeCarrier;
        int airDefence = CommanderSettings.OperationsRecipeAirDefence;
        int platoonSize = CommanderSettings.OperationsPlatoonSize;
        Expect(
            failures,
            "the platoon recipe adds up to the platoon size; check the Operations section of the config",
            armour + carrier + airDefence == platoonSize,
            true);
    }

    /// <summary>
    /// The COMMANDER LOG's OPERATIONS block for <paramref name="hq"/>: one summary line, then one
    /// line per live mission in design SS5's wording. ASCII only — the IMGUI font guarantees nothing
    /// else. Adds nothing when this HQ has no operations state (design SS1: host-only, and only once
    /// discovery has something to plan against).
    /// </summary>
    internal void DescribeOperations(FactionHQ hq, List<string> lines)
    {
        if (!states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        int fobs = 0;
        int orders = 0;
        for (int i = 0; i < state.Missions.Count; i++)
        {
            if (state.Missions[i].Kind == CommanderMissionKind.ForwardBase)
            {
                fobs++;
            }
        }

        for (int i = 0; i < state.Requisitions.Count; i++)
        {
            if (state.Requisitions[i].Filled < state.Requisitions[i].Wanted)
            {
                orders++;
            }
        }

        lines.Add(
            $"PRESSURE {state.Pressure:0}/{CommanderSettings.OperationsPressureIntervalMinutes:0}  "
                + $"PLATOONS {state.Platoons.Count}  FOBS {fobs}  ORDERS {orders}");

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            switch (mission.Kind)
            {
                case CommanderMissionKind.Attack:
                    lines.Add(DescribeAttack(hq, state, mission));
                    break;
                case CommanderMissionKind.ForwardBase:
                    lines.Add(DescribeForwardBase(mission));
                    break;
                case CommanderMissionKind.Picket:
                    lines.Add(DescribePicket(mission));
                    break;
            }
        }
    }

    /// <summary>
    /// Design SS5's exact wording (<c>"axes FOB Hilltop 12 + Crossroads 4"</c>): one label per
    /// distinct axis (deduplicated by release point, the way <see cref="AssignPlatoonsToAxes"/>
    /// shares one form-up/release pair across extra platoons on the same axis), and a
    /// platoons-forming-up count rather than a groups count. Previously this printed placeholder
    /// <c>AXIS 1</c>/<c>AXIS 2</c> labels and counted every <see cref="CommanderAssaultGroup"/> —
    /// including ones with no platoon yet — against the denominator.
    /// </summary>
    private static string DescribeAttack(FactionHQ hq, OperationsState state, CommanderOperationsMission mission)
    {
        List<GlobalPosition> seenReleases = new();
        List<string> axisLabels = new();
        for (int a = 0; a < mission.Axes.Count; a++)
        {
            GlobalPosition release = mission.Axes[a].ReleasePoint;
            bool found = false;
            for (int s = 0; s < seenReleases.Count; s++)
            {
                if (seenReleases[s].Equals(release))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                seenReleases.Add(release);
                axisLabels.Add(AxisLabel(hq, state, mission.Axes[a].FormUpPoint, axisLabels.Count + 1));
            }
        }

        string axesLabel = axisLabels.Count == 0 ? "none yet" : string.Join(" + ", axisLabels);

        int arrivedPlatoons = 0;
        int totalPlatoons = 0;
        for (int a = 0; a < mission.Axes.Count; a++)
        {
            if (mission.Axes[a].Platoon == null)
            {
                continue;
            }

            totalPlatoons++;
            if (mission.Axes[a].Arrived)
            {
                arrivedPlatoons++;
            }
        }

        return $"ATTACK {mission.Label}: {mission.Assigned.Count} platoons, axes {axesLabel}, "
            + $"forming up {arrivedPlatoons}/{totalPlatoons}";
    }

    /// <summary>An axis's own name: <c>"FOB " + label</c> for a live forward base of this HQ's,
    /// the airbase's label for a held base, or a numbered fallback for a flank point (design SS3)
    /// that is neither.</summary>
    private static string AxisLabel(FactionHQ hq, OperationsState state, GlobalPosition formUp, int fallbackIndex)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission candidate = state.Missions[i];
            if (candidate.Kind == CommanderMissionKind.ForwardBase
                && candidate.Point != null
                && candidate.Point.Position.Equals(formUp))
            {
                return "FOB " + candidate.Point.Label;
            }
        }

        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled && airbase.center != null
                && airbase.center.GlobalPosition().Equals(formUp))
            {
                return CommanderCaptureService.GetAirbaseLabel(airbase);
            }
        }

        return $"AXIS {fallbackIndex}";
    }

    private static string DescribeForwardBase(CommanderOperationsMission mission)
    {
        int platoons = mission.Assigned.Count;
        string truckNote = mission.Truck == null ? ", no truck" : string.Empty;
        return $"FOB {mission.Label}: {platoons}/{mission.WantedPlatoons} platoons{truckNote}";
    }

    private static string DescribePicket(CommanderOperationsMission mission)
    {
        return $"PICKET {mission.Label}: {mission.PicketMembers.Count}/{CommanderSettings.PointsMinGarrison}";
    }

    /// <summary>
    /// One runnable check at plugin load, next to every other service's. <c>ExpectSequence</c> and
    /// the three <c>Expect</c> overloads below are a second harness copied from
    /// <c>Points/CommanderStrategicPointService.cs:1060-1098</c> rather than shared — both are
    /// <c>private</c> to that class and both are load-time-only harnesses with no runtime caller,
    /// so sharing them would mean making a load-time implementation detail public for no second
    /// real use (Reuse rule 4 does not apply to two things that are not actually the same thing).
    /// </summary>
    internal static void SelfCheck()
    {
        List<string> failures = new();
        CheckRecipe(failures);
        CheckMarchOrder(failures);
        CheckArrival(failures);
        CheckFront(failures);
        CheckStrength(failures);
        CheckForwardBaseShare(failures);
        CheckOrderBook(failures);
        CheckSizing(failures);
        CheckConcurrentAttacks(failures);
        CheckObservedFloor(failures);
        CheckAxes(failures);
        CheckPressure(failures);
        CheckAirSupport(failures);
        CheckInsertion(failures);
        CheckMarkerLabels(failures);
        CheckDetachmentMarkerLabels(failures);
        CheckAirMarkerLabels(failures);
        CheckContactEvidence(failures);
        CheckReinforcements(failures);
        CheckGroundTactics(failures);
        CheckAirfieldClearance(failures);
        CheckFob(failures);
        CheckAirMobile(failures);
        CheckLogistics(failures);
        CheckAirPosture(failures);
        CheckSortieGathering(failures);
        CheckSurvival(failures);
        CheckReseat(failures);
        CheckStrategicRestore(failures);
        CheckUnitEconomy(failures);

        if (failures.Count == 0)
        {
            CommanderPlugin.Log.LogDebug("Operations self-check passed.");
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Operations self-check FAILED: {failures[i]}");
        }
    }

    private static void ExpectSequence(List<string> failures, string name, List<int> actual, params int[] expected)
    {
        bool equal = actual.Count == expected.Length;
        for (int i = 0; equal && i < expected.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                equal = false;
            }
        }

        if (!equal)
        {
            failures.Add($"{name}: expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]");
        }
    }

    private static void Expect(List<string> failures, string name, float actual, float expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, int actual, int expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, string actual, string expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected \"{expected}\", got \"{actual}\"");
        }
    }

    /// <summary>The picket delivery rule's own overload (departure 2026-09-14): the decision table
    /// answers Drive or Air, and a self-check that had to stringify it would read worse than the
    /// rule it is checking.</summary>
    private static void Expect(
        List<string> failures, string name, CommanderPicketDelivery actual, CommanderPicketDelivery expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }
}
