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

    /// <summary>A CAS sortie over one objective, bound to the airframes flying it.</summary>
    internal sealed class CommanderAirSortie
    {
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
        internal CommanderPendingAirLaunch(AircraftDefinition definition, Airbase origin, GlobalPosition? objective, float expiresAt)
        {
            Definition = definition;
            Origin = origin;
            Objective = objective;
            ExpiresAt = expiresAt;
        }

        internal AircraftDefinition Definition { get; }
        internal Airbase Origin { get; }

        /// <summary>The sortie centre the airframe was bought to face; null when no sortie wanted it.</summary>
        internal GlobalPosition? Objective { get; }
        internal float ExpiresAt { get; }
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
        if (state != CommanderPlatoonState.Moving && state != CommanderPlatoonState.Attacking)
        {
            return false;
        }

        return distanceToEnemyMeters <= rangeMeters;
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

    /// <summary>What the buy loop should put in the air first when the wing is short of both:
    /// CAP before CAS at equal demand (user decision 2026-09-13) — when the fund covers one
    /// airframe and both are wanted, it buys the fighter. Pure, for the self-check; the demand
    /// walk gathers the shortfalls and reads its answer through here.</summary>
    internal static CommanderAirDemandKind NextAirDemand(bool capShort, bool casShort)
    {
        if (capShort)
        {
            return CommanderAirDemandKind.Cap;
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

        Expect(failures, "CAP is served before CAS when both are short (user 2026-09-13)", (int)NextAirDemand(capShort: true, casShort: true), (int)CommanderAirDemandKind.Cap);
        Expect(failures, "CAS is served when only CAS is short", (int)NextAirDemand(capShort: false, casShort: true), (int)CommanderAirDemandKind.Cas);
        Expect(failures, "CAP is served when only CAP is short", (int)NextAirDemand(capShort: true, casShort: false), (int)CommanderAirDemandKind.Cap);
        Expect(failures, "nothing is short means nothing is bought", (int)NextAirDemand(capShort: false, casShort: false), (int)CommanderAirDemandKind.None);

        Expect(failures, "the first buy of a review may proceed", CommanderEnemyCommanderService.AirBuyContinues(0), true);
        Expect(failures, "the buys-per-review bound stops the third buy", CommanderEnemyCommanderService.AirBuyContinues(CommanderEnemyCommanderService.MaxAirBuysPerReview), false);
        Expect(failures, "the buys-per-review bound is at least one and at most a review's worth", CommanderEnemyCommanderService.MaxAirBuysPerReview >= 1 && CommanderEnemyCommanderService.MaxAirBuysPerReview <= 5, true);
        Expect(failures, "the air fund ceiling covers three of the dearest fighter when the pot is small", CommanderEnemyCommanderService.AirFundCeiling(20f, 145f), 3f * 145f);
        Expect(failures, "the air fund ceiling keeps its reviews-of-saving cap for a rich commander", CommanderEnemyCommanderService.AirFundCeiling(200f, 145f), 200f * CommanderEnemyCommanderService.FundSaveReviews);

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
    }

    // ---- Runtime: demand, binding, the claim, and what the buyer reads ----

    /// <summary>Registered faction units forwarded from the shared <c>RegisterFactionUnit</c>
    /// postfix; claims an airframe the buy loop just launched into the hungry sortie.</summary>
    internal static void NotifyAircraftRegistered(FactionHQ hq, Unit unit)
    {
        Instance?.TryClaimAircraft(hq, unit);
    }

    /// <summary>The buy loop calls this just before EVERY launch, so the registration that fires
    /// inside the launch can be matched back and the airframe owned — the same shape the player's
    /// own <c>PendingAircraftSpawn</c> uses, for the same reason. A null objective is a standing
    /// buy: the claim owns the airframe and parks it on the home CAP; a sortie claim may retask it
    /// later.</summary>
    internal static void RecordCommanderLaunch(FactionHQ hq, AircraftDefinition definition, Airbase origin, GlobalPosition? objective)
    {
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        state.PendingAirLaunch = new CommanderPendingAirLaunch(
            definition, origin, objective, Time.time + CommanderAirCommandService.PendingSpawnTimeoutSeconds);
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

        Airbase origin = state.PendingAirLaunch.Origin;
        state.PendingAirLaunch = null;
        state.CommanderAirframes.Add(aircraft);

        // Bind to the hungry sortie that matches what was bought (the buyer picks the role from
        // the same demand order this walks); a sortie standing down after a loss takes no new
        // airframes either. With no hungry sortie the airframe is owned and parked on the home CAP
        // right here (user decision 2026-09-13) — before the fix it was bought unowned, tasked by
        // nobody (the posture only reaches owned airframes) and idled to the ceiling.
        if (CommanderEnemyCommanderService.GetAirRole(aircraft.definition as AircraftDefinition) == CommanderEnemyCommanderService.AirRole.Fighter)
        {
            for (int i = 0; i < state.AirSorties.Count; i++)
            {
                if (Time.time >= state.AirSorties[i].CooldownUntil
                    && state.AirSorties[i].Caps.Count < state.AirSorties[i].CapsWanted)
                {
                    BindCap(hq, state, state.AirSorties[i], aircraft);
                    return;
                }
            }
        }
        else
        {
            for (int i = 0; i < state.AirSorties.Count; i++)
            {
                if (Time.time >= state.AirSorties[i].CooldownUntil
                    && state.AirSorties[i].Cas.Count < state.AirSorties[i].Wanted)
                {
                    BindCas(hq, state, state.AirSorties[i], aircraft, origin);
                    return;
                }
            }
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
        BuildAirDemand(hq, state, airDemand);
        ReconcileSorties(hq, state, airDemand);
        FillSorties(hq, state);
        SyncAirTasks(hq, state);
    }

    private readonly List<CommanderAirSortie> airDemand = new();
    private readonly List<Aircraft> airStale = new();

    /// <summary>The review's sortie list in priority order (design SS1): attack targets whose
    /// groups have begun arriving (Approval 1 — the demand opens at first release-point arrival),
    /// then platoons in contact, then platoons marching near the enemy but not yet in contact
    /// (pre-emptive, user decision 2026-09-14), then threatened forward bases in ranked order.
    /// Pickets, reserve and quiet rear points ask for nothing.</summary>
    private void BuildAirDemand(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        demand.Clear();
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
            if ((platoon.State != CommanderPlatoonState.Moving && platoon.State != CommanderPlatoonState.Attacking)
                || platoon.InContactUntil < Time.time)
            {
                continue;
            }

            // Over the last tracked contact, not over the platoon: the CAS area filter picks its
            // targets out of its own ring, so the box has to sit on the enemy, not on the line
            // facing it.
            AddDemand(hq, state, demand, null, platoon, platoon.ContactBearingAnchor, platoon.Name, immediate: true);
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
            if (mission != null)
            {
                AddDemand(hq, state, demand, mission, null, ranked.Point.Position, mission.Label, immediate: false);
            }
        }
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
            if (ReferenceEquals(demand[i].Mission, mission))
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
        float enemyDistanceMeters = -1f)
    {
        int observed = EffectiveObserved(
            CountObserved(hq, center),
            GetObservedFloor(state, ObservedFloorKey(mission?.Point, mission?.TargetAirbase)));
        int hostileAir = CountHostileAirInRing(hq, center);
        (int cas, int cap) = preemptive
            ? PlanPreemptiveSortie(observed, hostileAir)
            : PlanSortie(observed, hostileAir);
        if (cas <= 0 && cap <= 0)
        {
            return;
        }

        demand.Add(new CommanderAirSortie
        {
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
                if ((wanted.Mission != null && ReferenceEquals(live.Mission, wanted.Mission))
                    || (wanted.ContactPlatoon != null && ReferenceEquals(live.ContactPlatoon, wanted.ContactPlatoon)))
                {
                    wanted.Cas.AddRange(live.Cas);
                    wanted.Caps.AddRange(live.Caps);
                    wanted.CooldownUntil = live.CooldownUntil;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                ReleaseSortie(hq, state, live);
            }

            state.AirSorties.RemoveAt(i);
        }

        state.AirSorties.AddRange(demand);
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
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (Time.time < sortie.CooldownUntil)
            {
                continue; // a sortie standing down after a loss is not refilled either
            }

            while (sortie.Caps.Count < sortie.CapsWanted)
            {
                Aircraft? fighter = TakeUnboundOwned(state, CommanderEnemyCommanderService.AirRole.Fighter);
                if (fighter == null)
                {
                    break;
                }

                BindCap(hq, state, sortie, fighter);
            }
        }

        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (Time.time < sortie.CooldownUntil)
            {
                continue;
            }

            while (sortie.Cas.Count < sortie.Wanted)
            {
                Aircraft? strike = TakeUnboundOwned(state, CommanderEnemyCommanderService.AirRole.Strike);
                if (strike == null)
                {
                    break;
                }

                BindCas(hq, state, sortie, strike, origin: null);
            }
        }
    }

    /// <summary>An owned airframe of <paramref name="role"/> that no live sortie holds, live and
    /// flying either one of this commander's tasks or nothing. Anything carrying a mission this
    /// commander never issued is the player's now (an RTS order adopts in place): it is let go
    /// entirely, and never retasked by the posture either — decision 5, the air-side hands-off
    /// rule.</summary>
    private Aircraft? TakeUnboundOwned(OperationsState state, CommanderEnemyCommanderService.AirRole role)
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

        foreach (Aircraft aircraft in state.CommanderAirframes)
        {
            if (aircraft == null
                || aircraft.disabled
                || CommanderEnemyCommanderService.GetAirRole(aircraft.definition as AircraftDefinition) != role
                || IsBoundToAnySortie(state, aircraft))
            {
                continue;
            }

            return aircraft;
        }

        return null;
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
        GlobalPosition home = HomeCAPCentre(hq);
        for (int s = 0; s < state.AirSorties.Count; s++)
        {
            CommanderAirSortie sortie = state.AirSorties[s];
            bool holdCas = CasIsHeldForCap(sortie);
            for (int i = sortie.Cas.Count - 1; i >= 0; i--)
            {
                SyncBoundAirframe(
                    hq, state, sortie, sortie.Cas[i],
                    holdCas ? CommanderAirCommandService.AirCommandMode.AirGuard : CommanderAirCommandService.AirCommandMode.Cas,
                    holdCas ? home : sortie.Center,
                    holdCas ? CommanderEnemyCommanderService.HomeGuardRadiusMeters : CasSortieRadiusMeters,
                    capJoin: !holdCas && sortie.CapsWanted > 0);
            }

            for (int i = 0; i < sortie.Caps.Count; i++)
            {
                SyncBoundAirframe(
                    hq, state, sortie, sortie.Caps[i],
                    CommanderAirCommandService.AirCommandMode.AirGuard, sortie.Center, CasSortieRadiusMeters, capJoin: false);
            }
        }
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
        CommanderAiLog.Note(hq, $"lets {CommanderGameAccess.GetUnitLabel(aircraft)} go: the player has it under orders.");
    }

    /// <summary>Approval 1, kept under CAP-first: a sortie that wants a CAP and has none yet holds
    /// its CAS over the home CAP centre — except contact sorties, which task immediately.</summary>
    private static bool CasIsHeldForCap(CommanderAirSortie sortie)
    {
        return sortie.CapsWanted > 0 && sortie.Caps.Count == 0 && !sortie.NoCapWait;
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

        if (CasIsHeldForCap(sortie))
        {
            IssueAirTask(
                hq, state, aircraft, CommanderAirCommandService.AirCommandMode.AirGuard,
                HomeCAPCentre(hq), CommanderEnemyCommanderService.HomeGuardRadiusMeters);
            CommanderAiLog.Note(
                hq, $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} for {sortie.Label}, held over home territory until its CAP is up.");
        }
        else
        {
            IssueAirTask(hq, state, aircraft, CommanderAirCommandService.AirCommandMode.Cas, sortie.Center, CasSortieRadiusMeters);
            CommanderAiLog.Note(
                hq,
                $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} with CAS over {sortie.Label} ({DescribeSortieReason(sortie, transitSeconds)}).");
        }
    }

    /// <summary>The parenthesis on a CAS tasking line: why this sortie exists and, when the launch
    /// airbase is known, how long the airframe needs to get there. A pre-emptive sortie says so and
    /// gives the distance to the enemy that opened it, rounded to kilometres — the number a reader
    /// needs to see that the wing went up before the platoon was shot at.</summary>
    private static string DescribeSortieReason(CommanderAirSortie sortie, float transitSeconds)
    {
        string reason = sortie.Preemptive
            ? $"pre-emptive, enemy {sortie.EnemyDistanceMeters / 1000f:0.#} km"
            : $"{sortie.LastObserved} observed";
        return transitSeconds > 0f ? $"{reason}; on station in ~{transitSeconds:0} s" : reason;
    }

    /// <summary>Bind a fighter onto the sortie's CAP. The first one is the escort — the airframe
    /// whose binding releases the held CAS to the objective (Approval 1, kept under CAP-first);
    /// later ones are wingmen.</summary>
    private static void BindCap(FactionHQ hq, OperationsState state, CommanderAirSortie sortie, Aircraft aircraft)
    {
        bool isEscort = sortie.Caps.Count == 0 && sortie.Cas.Count > 0;
        sortie.Caps.Add(aircraft);
        IssueAirTask(hq, state, aircraft, CommanderAirCommandService.AirCommandMode.AirGuard, sortie.Center, CasSortieRadiusMeters);
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

    /// <summary>Tracked hostile aircraft inside the sizing ring (design SS1; one CAP fighter per
    /// tracked hostile, on top of the baseline) — the <c>CountObserved</c> walk with the aircraft
    /// type test, the same freshness rule.</summary>
    private static int CountHostileAirInRing(FactionHQ hq, GlobalPosition center)
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

            if (CommanderGameAccess.HorizontalDistance(center.AsVector3(), info.lastKnownPosition.AsVector3()) <= ObservedRadiusMeters)
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
    internal static CommanderAirDemandKind TryGetAirDemand(FactionHQ hq, out GlobalPosition objective)
    {
        objective = default;
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return CommanderAirDemandKind.None;
        }

        GlobalPosition capObjective = default;
        GlobalPosition casObjective = default;
        bool capShort = false;
        bool casShort = false;
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (Time.time < sortie.CooldownUntil)
            {
                continue;
            }

            if (!capShort && sortie.Caps.Count < sortie.CapsWanted)
            {
                capShort = true;
                capObjective = sortie.Center;
            }

            if (!casShort && sortie.Cas.Count < sortie.Wanted)
            {
                casShort = true;
                casObjective = sortie.Center;
            }
        }

        CommanderAirDemandKind kind = NextAirDemand(capShort, casShort);
        if (kind == CommanderAirDemandKind.Cap)
        {
            objective = capObjective;
        }
        else if (kind == CommanderAirDemandKind.Cas)
        {
            objective = casObjective;
        }

        return kind;
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

    /// <summary><c>UpdateAttacks</c>' go-in hold (Approval 1): true while this attack's sortie
    /// wants CAS, has nothing on station, can still be reached in time from a held airbase, and the
    /// attack's own form-up wait has not run out. The timeout is the attack's existing one — one
    /// bounded wait, no second clock.</summary>
    private static bool HoldsForCas(OperationsState state, FactionHQ hq, CommanderOperationsMission mission)
    {
        CommanderAirSortie? sortie = null;
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            if (ReferenceEquals(state.AirSorties[i].Mission, mission))
            {
                sortie = state.AirSorties[i];
                break;
            }
        }

        if (sortie == null)
        {
            return false;
        }

        bool onStation = false;
        for (int i = 0; i < sortie.Cas.Count; i++)
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

            into.Append(sortie.Label)
                .Append(" CAP ").Append(sortie.Caps.Count).Append('/').Append(sortie.CapsWanted)
                .Append(" CAS ").Append(sortie.Cas.Count).Append('/').Append(sortie.Wanted);
            if (sortie.Preemptive)
            {
                into.Append(" pre");
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

    /// <summary>A sortie is short of CAP fighters — the wing's first demand.</summary>
    Cap,
}