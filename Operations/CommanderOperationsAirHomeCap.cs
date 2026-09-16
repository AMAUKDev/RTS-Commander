using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Home CAP bookkeeping and lending the home CAP forward. Split out of <c>CommanderOperationsAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past six thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderOperationsService
{
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
    /// ring again. That is the only recall left — the "down to its minimum patrol" recall went with
    /// the minimum itself (user decision 2026-09-16). The fighter leaves its sortie, so that sortie's
    /// CAP demand reopens this same review and is filled or bought the ordinary way.
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
        bool quiet = HomeCapIsQuiet(CommanderEnemyCommanderService.CountTrackedEnemyAircraftNearBases(hq));
        // A quiet base allows every fighter it has out, so only a raid can put the loan over the
        // allowance; the allowance is still read through the one definition the loan uses, so the
        // recall and the loan can never disagree about it.
        int allowed = quiet ? LendableHomeCap(alive) : 0;
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
                HomeCAPCentre(hq), CommanderEnemyCommanderService.HomeGuardRadiusMeters,
                HomeCapAltitude(state, fighter));
            CommanderAiLog.Note(
                hq, $"recalls {CommanderGameAccess.GetUnitLabel(fighter)} to home CAP: hostile air near the base.");
        }

        homeCapRecall.Clear();
    }

    /// <summary>
    /// A home-CAP fighter the base can spare for <paramref name="center"/>, nearest first, or null
    /// while a hostile aircraft is tracked in the base ring. Every fighter on the patrol is lendable,
    /// the last one included, the moment the ring is clear (user decision 2026-09-16); the in-contact
    /// read is the only gate. The borrowed fighter stays in the home-CAP set — it is lent, not
    /// transferred.
    /// </summary>
    private Aircraft? TakeLendableHomeCapFighter(FactionHQ hq, OperationsState state, GlobalPosition center)
    {
        if (!HomeCapIsQuiet(CommanderEnemyCommanderService.CountTrackedEnemyAircraftNearBases(hq)))
        {
            return null;
        }

        if (CountLentHomeCap(hq) >= LendableHomeCap(CountHomeCapFighters(hq)))
        {
            return null;
        }

        Aircraft? best = null;
        float bestDistance = float.MaxValue;
        foreach (Aircraft fighter in state.HomeCapAirframes)
        {
            if (fighter == null
                || fighter.disabled
                // Nothing lends the radar aeroplane forward (user decision 2026-09-14). It should
                // never be on the home patrol to begin with — the claim below refuses to park it
                // there — and this is the second door on the same rule.
                || IsAwacsAirframe(hq, fighter)
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
    /// The priority ladder grants the picket rung its SAVINGS for this HQ (design Section 3, rung 3;
    /// departure 2026-09-14): insertion flights may charge up to this much until the next ladder
    /// review overwrites it. Called from the enemy commander's review — the single spend site —
    /// once rung 3's turn comes.
    /// <para>The allowance is the whole bank, not one review's share. A flight costs a transport
    /// hull plus two vehicles and a per-review share is a fraction of that, so a share that was
    /// recomputed from scratch every review could never pay for one: the whole 2026-09-14 match
    /// refused every insertion with "the ladder's picket share cannot cover the flight". The rung
    /// banks across reviews now, and <paramref name="target"/> is the price it is banking
    /// toward — what the request compares its allowance against.</para>
    /// </summary>
    internal static void GrantInsertionAllowance(FactionHQ hq, float allowance, float target)
    {
        if (Instance != null && Instance.states.TryGetValue(hq, out OperationsState state))
        {
            state.InsertionAllowance = allowance;
            state.InsertionSavingsTarget = target;
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
        bool onStation = CountOnStation(sortie, sortie.Cas) > 0;

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

    /// <summary>
    /// How many of a sortie's bound airframes are actually OVER it: alive, and inside
    /// <see cref="CasSortieRadiusMeters"/> of the sortie's centre, and only once the package has
    /// gone in — a formation still holding at its form-up point is not on station however close it
    /// is. THE definition of "on station" (Reuse rule 3, moved not paraphrased): the attack's
    /// go-in check and the marker's <c>CAS overhead</c> flag read the same answer, so the player can
    /// never be told air is overhead by a rule the attack does not believe.
    /// </summary>
    internal static int CountOnStation(CommanderAirSortie sortie, List<Aircraft> bound)
    {
        if (!sortie.GoneIn)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < bound.Count; i++)
        {
            Aircraft aircraft = bound[i];
            if (aircraft != null
                && !aircraft.disabled
                && CommanderGameAccess.HorizontalDistance(
                    aircraft.transform.GlobalPosition().AsVector3(), sortie.Center.AsVector3()) <= CasSortieRadiusMeters)
            {
                count++;
            }
        }

        return count;
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
    /// <summary>
    /// The element's pinned airframe type on the review line, as a short tag behind its counts —
    /// <c>CAS 2/2 A-19</c> (user decision 2026-09-14). Empty until the element's first order picks
    /// a type, which is what tells a reader "this package has not been ordered yet" apart from
    /// "this package is two of the same thing". The tag is the first word of the unit name, so a
    /// line with six sorties on it stays readable.
    /// </summary>
    private static string PackageTypeTag(AircraftDefinition? type)
    {
        if (type == null || string.IsNullOrEmpty(type.unitName))
        {
            return string.Empty;
        }

        string name = type.unitName;
        int space = name.IndexOf(' ');
        return " " + (space > 0 ? name.Substring(0, space) : name);
    }

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
                        .Append(' ').Append(sortie.Cas.Count).Append('/').Append(sortie.Wanted)
                        .Append(PackageTypeTag(sortie.PackageStrikeType));
                    break;
                case CommanderSortieKind.Strike:
                    // The one sortie the commander decided to fly rather than was shown, so the
                    // review line names its elements separately (design.md,
                    // strike-packages_20260915 Section 6).
                    into.Append("STRIKE ").Append(sortie.Label)
                        .Append(" S ").Append(sortie.Cas.Count).Append('/').Append(sortie.Wanted)
                        .Append(PackageTypeTag(sortie.PackageStrikeType))
                        .Append(" E ").Append(sortie.Caps.Count).Append('/').Append(sortie.CapsWanted)
                        .Append(PackageTypeTag(sortie.PackageCapType))
                        .Append(" A ").Append(sortie.AradWanted > 0 ? (sortie.AradPending ? "wait" : "clear") : "0");
                    if (sortie.BomberWanted > 0)
                    {
                        into.Append(" B ").Append(sortie.BomberWanted).Append(PackageTypeTag(sortie.PackageBomberType));
                    }

                    if (sortie.Delivered)
                    {
                        into.Append(" delivered");
                    }

                    break;
                case CommanderSortieKind.Cap:
                    // The tracked-hostile count that opened it rides the line (fix, 2026-09-14).
                    // A CAP-only sortie exists ONLY because hostile aircraft were tracked over that
                    // platoon or point in the last 45 s (AddCapDemand refuses to open one
                    // otherwise), and with five or six of them on a review line the reader had no
                    // way to see that from the log and could only conclude the wing was opening
                    // cover over empty sky.
                    into.Append("CAP ").Append(sortie.Label)
                        .Append(' ').Append(sortie.Caps.Count).Append('/').Append(sortie.CapsWanted)
                        .Append(" air").Append(sortie.LastHostileAir);
                    break;
                default:
                    into.Append(sortie.Label)
                        .Append(" CAP ").Append(sortie.Caps.Count).Append('/').Append(sortie.CapsWanted)
                        .Append(PackageTypeTag(sortie.PackageCapType))
                        .Append(" CAS ").Append(sortie.Cas.Count).Append('/').Append(sortie.Wanted)
                        .Append(PackageTypeTag(sortie.PackageStrikeType));
                    break;
            }

            if (sortie.Preemptive)
            {
                into.Append(" pre");
            }

            // The air fallback posture (design.md, air-fallback-posture_20260916): the sortie's
            // fighters are holding toward the base and have called for more.
            if (sortie.FallingBack)
            {
                into.Append(" fallback");
            }

            // The station band its fighters are flying (design.md, strike-packages_20260915
            // Section 4): three sorties on one line reading @1500, @4000 and @7500 is how a reader
            // sees the rotation working at all.
            if (sortie.CapBand >= 0 && sortie.CapsWanted > 0)
            {
                into.Append(" @").Append(CapBandMetersFor(sortie.CapBand).ToString("0"));
            }

            // Section 3: a sortie still forming shows how much of it has reached the form-up point.
            // Both elements since 2026-09-16, because a fighter patrol forms up too and would
            // otherwise read "pkg 0/0" for its whole wait.
            if (SortieHoldsAtFormUp(sortie))
            {
                into.Append(" pkg ").Append(CountGathered(sortie))
                    // The FROZEN bar the go-in test reads, not this review's demand (2026-09-16):
                    // the line printed `pkg 0/4`, `0/6`, `0/8` while the package never moved, because
                    // the fall-back's call for fighters was raising the number it was measured by.
                    .Append('/').Append(SortieGoInTotal(sortie));
                if (sortie.AradPending)
                {
                    into.Append(" arad-wait");
                }
            }

            if (sortie.Kind == CommanderSortieKind.Objective && sortie.WantsRotary)
            {
                // Which of the two this sortie is on: helicopters, or jets while the belt is up
                // (user decision 2026-09-14).
                if (RotaryCasAllowed(sortie.LastAirDefence, sortie.AradFlown))
                {
                    into.Append(" rotary");
                }
                else
                {
                    into.Append(" held: ARAD first (").Append(sortie.LastAirDefence).Append(" AD)");
                }
            }

            // Section 10: this sortie took an airframe off quiet cover this review.
            if (sortie.RetaskedThisReview)
            {
                into.Append(" retask");
            }

            // Its demand has closed and it is inside the minimum hold (team lead, 2026-09-14).
            if (sortie.Held)
            {
                into.Append(" held");
            }

            if (Time.time < sortie.CooldownUntil)
            {
                into.Append(" cooldown");
            }
        }

        into.Append(']');
    }
}
