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