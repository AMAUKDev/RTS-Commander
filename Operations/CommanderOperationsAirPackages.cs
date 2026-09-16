using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Packages: elements ordered together and kept together. Split out of <c>CommanderOperationsAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past six thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderOperationsService
{
    // ---- Packages (design.md, smarter-air-wing_20260914 Section 3) ----

    // ---- The strike package's shape (design.md, strike-packages_20260915 Section 2) ----

    /// <summary>
    /// Strike airframes in every strike package, whatever the target. Two: one aeroplane against a
    /// defended point is a single pass that either works or is a write-off, and the whole reason the
    /// package exists is that the 2026-09-15 match flew nothing but single reactive sorties. Above
    /// two the wing concentrates on one point while every objective that is actually being fought
    /// over goes without — the same reasoning as <see cref="CasPerObjectiveCap"/>.
    /// </summary>
    private const int StrikeElementSize = 2;

    /// <summary>
    /// Escorts a defended or hard target's package is owed before the hostile-air floor is applied.
    /// Two, the same number a transport flight is owed (<see cref="TransportEscortMinimum"/>) and
    /// for the same reason: one fighter alone is the first thing a pair of raiders kills, and a
    /// strike package deep in enemy ground is the most escorted thing this wing ever flies.
    /// </summary>
    private const int DefendedEscort = 2;

    /// <summary>Anti-radiation airframes a hard target's package expects. One: the belt itself is
    /// sized by <see cref="AradWanted"/>, which already buys a second airframe for a thick one — this
    /// is only whether the package waits for suppression at all.</summary>
    private const int HardArad = 1;

    /// <summary>Bombers against a base when the roster holds a bomber-class type. One is the floor:
    /// a base is buildings, and a single bomber's pass is what a point does not need and a base
    /// does.</summary>
    private const int BaseBomberMin = 1;

    /// <summary>Bombers against a base once the base is also defended in the air. Two, and no more:
    /// a bomber is the dearest ground-attack airframe on the roster and a third would cost the wing
    /// its escorts.</summary>
    private const int BaseBomberMax = 2;

    /// <summary>Observed defenders at or below which a target is light. Two, the same number the CAS
    /// ladder already treats as a picket one airframe can service
    /// (<see cref="CasWanted"/>) — a light target is precisely one the ladder would not escalate
    /// for.</summary>
    private const int LightDefendersMax = 2;

    /// <summary>
    /// Reviews the bomber element may go unbought before the package flies without it (fix,
    /// 2026-09-15). Two, a minute: long enough that a bomber the wing simply has not reached yet —
    /// the escort is bought before it, and the buy makes one purchase a side per review — still gets
    /// its chance, short enough that a base package whose roster holds a bomber it can never afford
    /// does not sit at its form-up point for twelve minutes buying nothing at all. Dropping the
    /// element is the right answer rather than dropping the package: two strike airframes against a
    /// base is worth flying, and nothing against a base is not.
    /// </summary>
    private const int StrikeBomberRefusalReviews = 2;

    /// <summary>
    /// Whether the package gives up on its bomber element (fix, 2026-09-15): once it has gone
    /// <paramref name="limit"/> reviews wanting one and getting none. Exactly at the limit counts as
    /// given up, the convention the rest of the mod uses. A limit of zero or less never gives up, so
    /// the rule can be turned off by its own constant rather than by deleting the call. Pure, for the
    /// self-check.
    /// </summary>
    internal static bool StrikeGivesUpBomber(int refusedReviews, int limit)
    {
        return limit > 0 && refusedReviews >= limit;
    }

    /// <summary>
    /// The escorts a package wants RIGHT NOW (fix, 2026-09-15): its scale's own floor, never fewer
    /// than the hostile aircraft tracked over the target this review, and never more than the room
    /// left under the airborne ceiling.
    /// <para>
    /// Decision 4 — "escorts scale with enemy threat, at least one-for-one with hostile aircraft
    /// tracked" — is a rule about the sky the package is flying into, and the sky changes. Sizing it
    /// once when the sortie was ordered meant a package ordered over an empty point flew into a
    /// four-ship raid with no escort at all, and a package ordered while the ceiling was full never
    /// got the escort it was owed however much room opened up afterwards.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static int StrikeEscortWanted(int escortFloor, int hostileAirNow, int headroom)
    {
        // The floor-and-growth half is the shared envelope every element reads (Reuse rule 5,
        // generalised 2026-09-16); the ceiling clamp stays here, where it was proven, because it is
        // the escort's own rule and not every element's — see SortieElementWanted for why.
        int wanted = SortieElementWanted(escortFloor, hostileAirNow, cap: 0);
        return Mathf.Min(wanted, Mathf.Max(0, headroom));
    }

    /// <summary>One strike package's four elements and the scale they were sized from.</summary>
    internal readonly struct StrikePackage
    {
        internal StrikePackage(int strike, int escort, int escortFloor, int arad, int bomber, CommanderStrikeScale scale)
        {
            Strike = strike;
            Escort = escort;
            EscortFloor = escortFloor;
            Arad = arad;
            Bomber = bomber;
            Scale = scale;
        }

        /// <summary>The escort this scale is owed before the hostile-air floor is applied — what the
        /// live re-sizing (<see cref="StrikeEscortWanted"/>) keeps as its own floor every review.</summary>
        internal int EscortFloor { get; }

        /// <summary>Airframes that attack the target.</summary>
        internal int Strike { get; }

        /// <summary>Fighters over the package — never fewer than the hostile aircraft tracked.</summary>
        internal int Escort { get; }

        /// <summary>Anti-radiation airframes the package waits for.</summary>
        internal int Arad { get; }

        /// <summary>Bomber-class airframes, against a base only.</summary>
        internal int Bomber { get; }

        /// <summary>How hard the target read.</summary>
        internal CommanderStrikeScale Scale { get; }
    }

    /// <summary>
    /// The package a target's scale asks for (design.md, strike-packages_20260915 Section 2, the
    /// table read straight down). Three rows:
    /// <list type="bullet">
    /// <item>LIGHT — two or fewer defenders, nothing tracked in the air and no air defence: two
    /// strike airframes and only as much escort as there is hostile air, which is normally none.</item>
    /// <item>DEFENDED — three or more defenders, or hostile aircraft tracked: the same strike
    /// element with a real escort behind it.</item>
    /// <item>HARD — air defence tracked, or the target is a base: suppression as well, and bombers
    /// against a base when the roster holds one.</item>
    /// </list>
    /// The escort is NEVER below <paramref name="hostileAir"/> (user decision 4, 2026-09-15: "CAP
    /// escorts scale with enemy threat, at least one-for-one with hostile aircraft tracked"); the
    /// caller caps it against the airborne ceiling's headroom, which is a live number and so not
    /// part of this rule. Pure, for the self-check.
    /// </summary>
    internal static StrikePackage StrikePackageFor(
        int defenders, bool airDefence, int hostileAir, bool isBase, bool hasBomber)
    {
        int air = Mathf.Max(0, hostileAir);
        if (airDefence || isBase)
        {
            int bomber = 0;
            if (isBase && hasBomber)
            {
                // A base that is also defended in the air is worth the second bomber; a quiet one is
                // not, and the airframe is better spent on the escort.
                bomber = air > 0 || defenders >= LightDefendersMax + 1 ? BaseBomberMax : BaseBomberMin;
            }

            return new StrikePackage(
                StrikeElementSize, Mathf.Max(DefendedEscort, air), DefendedEscort, HardArad, bomber,
                CommanderStrikeScale.Hard);
        }

        if (defenders > LightDefendersMax || air > 0)
        {
            return new StrikePackage(
                StrikeElementSize, Mathf.Max(DefendedEscort, air), DefendedEscort, 0, 0,
                CommanderStrikeScale.Defended);
        }

        return new StrikePackage(StrikeElementSize, air, 0, 0, 0, CommanderStrikeScale.Light);
    }

    /// <summary>The package table, every row and the escort floor that runs across all of them. Each
    /// of these numbers can be retuned into a package that is no package at all — a strike element
    /// of one, an escort below the raid it is flying into — and nothing in the running game would
    /// say so until a playtest watched the wing die.</summary>
    private static void CheckStrikePackages(List<string> failures)
    {
        StrikePackage light = StrikePackageFor(0, airDefence: false, hostileAir: 0, isBase: false, hasBomber: true);
        Expect(failures, "an undefended point still gets the whole strike element", light.Strike, StrikeElementSize);
        Expect(failures, "an undefended point with nothing in the air needs no escort", light.Escort, 0);
        Expect(failures, "an undefended point needs no suppression", light.Arad, 0);
        Expect(failures, "a point is never bombed, however good the roster is", light.Bomber, 0);
        Expect(failures, "an undefended point reads as light", (int)light.Scale, (int)CommanderStrikeScale.Light);
        Expect(
            failures,
            "two defenders is still a light target, the same picket the CAS ladder services with one airframe",
            (int)StrikePackageFor(LightDefendersMax, false, 0, false, false).Scale,
            (int)CommanderStrikeScale.Light);

        StrikePackage lightRaided = StrikePackageFor(0, airDefence: false, hostileAir: 1, isBase: false, hasBomber: false);
        // One hostile aircraft turns an undefended target into a DEFENDED one (design Section 2), so
        // the escort is the defended pair, not one-for-one (the check read 1 and failed at load,
        // 2026-09-15).
        Expect(failures, "one hostile aircraft over an undefended target buys the defended pair of escorts", lightRaided.Escort, 2);
        Expect(
            failures,
            "hostile air makes even an undefended target a defended one",
            (int)lightRaided.Scale,
            (int)CommanderStrikeScale.Defended);

        StrikePackage defended = StrikePackageFor(3, airDefence: false, hostileAir: 0, isBase: false, hasBomber: true);
        Expect(failures, "three defenders is a defended target", (int)defended.Scale, (int)CommanderStrikeScale.Defended);
        Expect(failures, "a defended target keeps the same strike element", defended.Strike, StrikeElementSize);
        Expect(failures, "a defended target gets a real escort", defended.Escort, DefendedEscort);
        Expect(failures, "a defended point still needs no suppression", defended.Arad, 0);

        Expect(
            failures,
            "four hostile aircraft over a defended target buy four escorts, not the minimum",
            StrikePackageFor(3, false, 4, false, false).Escort,
            4);

        StrikePackage hard = StrikePackageFor(3, airDefence: true, hostileAir: 0, isBase: false, hasBomber: true);
        Expect(failures, "tracked air defence makes a target hard", (int)hard.Scale, (int)CommanderStrikeScale.Hard);
        Expect(failures, "a hard target is preceded by suppression", hard.Arad, HardArad);
        Expect(failures, "a hard target gets the escort minimum", hard.Escort, DefendedEscort);
        Expect(failures, "a hard POINT is still never bombed", hard.Bomber, 0);

        StrikePackage bareBase = StrikePackageFor(0, airDefence: false, hostileAir: 0, isBase: true, hasBomber: false);
        Expect(failures, "a base is a hard target however quiet it looks", (int)bareBase.Scale, (int)CommanderStrikeScale.Hard);
        Expect(failures, "a base with no bomber on the roster is struck without one", bareBase.Bomber, 0);
        Expect(failures, "a base still gets its suppression", bareBase.Arad, HardArad);
        Expect(failures, "a base still gets the escort minimum", bareBase.Escort, DefendedEscort);

        Expect(
            failures,
            "a quiet base with a bomber on the roster gets one",
            StrikePackageFor(0, false, 0, true, true).Bomber,
            BaseBomberMin);
        Expect(
            failures,
            "a base with hostile air over it gets the pair",
            StrikePackageFor(0, false, 2, true, true).Bomber,
            BaseBomberMax);
        Expect(
            failures,
            "a well-defended base gets the pair too",
            StrikePackageFor(9, false, 0, true, true).Bomber,
            BaseBomberMax);

        // The escort floor, across every row: this is decision 4, and it is the one rule a retune of
        // any other number could quietly break.
        Expect(failures, "a light target's escort still matches the hostile air", StrikePackageFor(0, false, 5, false, false).Escort >= 5, true);
        Expect(failures, "a defended target's escort still matches the hostile air", StrikePackageFor(6, false, 5, false, false).Escort >= 5, true);
        Expect(failures, "a hard target's escort still matches the hostile air", StrikePackageFor(6, true, 5, false, false).Escort >= 5, true);
        Expect(failures, "a base's escort still matches the hostile air", StrikePackageFor(0, false, 5, true, true).Escort >= 5, true);
        Expect(failures, "a negative tracking read never lowers the escort below zero", StrikePackageFor(0, false, -4, false, false).Escort, 0);
        Expect(failures, "every package flies a strike element, or it is not a strike", StrikeElementSize >= 2, true);
        Expect(failures, "the bomber pair is never smaller than the single bomber", BaseBomberMax >= BaseBomberMin, true);

        // The escort floor each scale carries, which the live re-sizing keeps (fix, 2026-09-15).
        Expect(failures, "a light target's escort floor is nothing", light.EscortFloor, 0);
        Expect(failures, "a defended target's escort floor is the minimum", defended.EscortFloor, DefendedEscort);
        Expect(failures, "a hard target's escort floor is the minimum too", hard.EscortFloor, DefendedEscort);
        Expect(failures, "a base's escort floor is the minimum", bareBase.EscortFloor, DefendedEscort);
        Expect(
            failures,
            "the floor never exceeds the escort the package was ordered with",
            defended.EscortFloor <= defended.Escort,
            true);

        // Re-sizing the escort each review against the sky it is flying into (fix, 2026-09-15).
        Expect(failures, "a quiet sky leaves a defended package on its floor", StrikeEscortWanted(2, 0, 20), 2);
        Expect(failures, "a four-ship raid over the target buys four escorts", StrikeEscortWanted(2, 4, 20), 4);
        Expect(failures, "one hostile aircraft never lowers the floor", StrikeEscortWanted(2, 1, 20), 2);
        Expect(failures, "a light package with hostile air over it gets an escort it was not ordered with", StrikeEscortWanted(0, 3, 20), 3);
        Expect(failures, "a full sky caps the escort at the room there is", StrikeEscortWanted(2, 6, 1), 1);
        Expect(failures, "no room at all means no escort, never a negative one", StrikeEscortWanted(2, 6, 0), 0);
        Expect(failures, "a negative headroom is still no escort", StrikeEscortWanted(2, 6, -5), 0);
        Expect(failures, "a negative tracking read never lowers the floor", StrikeEscortWanted(2, -3, 20), 2);
        Expect(
            failures,
            "the live escort rule agrees with the table it was sized from when nothing has changed",
            StrikeEscortWanted(defended.EscortFloor, 0, 20),
            defended.Escort);

        // Giving up on an unaffordable bomber (fix, 2026-09-15).
        Expect(failures, "a bomber refused once is still waited for", StrikeGivesUpBomber(1, StrikeBomberRefusalReviews), false);
        Expect(failures, "a bomber refused twice is given up on", StrikeGivesUpBomber(2, StrikeBomberRefusalReviews), true);
        Expect(failures, "a bomber never refused is never given up on", StrikeGivesUpBomber(0, StrikeBomberRefusalReviews), false);
        Expect(failures, "a long run of refusals stays given up on", StrikeGivesUpBomber(40, StrikeBomberRefusalReviews), true);
        Expect(failures, "a limit of zero turns the rule off rather than giving up at once", StrikeGivesUpBomber(9, 0), false);
        Expect(
            failures,
            "the bomber is waited for at least one review, or the element could never be bought at all",
            StrikeBomberRefusalReviews >= 1,
            true);
    }

    /// <summary>
    /// Give every package its form-up point, count who has reached it, and decide whether it goes in
    /// this review. Runs after the fill so this review's new bindings are counted, and before the
    /// task sync so the sync writes the answer straight onto the airframes.
    /// </summary>
    private void UpdatePackages(FactionHQ hq, OperationsState state)
    {
        RefreshAradHolds(hq, state);
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie sortie = state.AirSorties[i];
            if (sortie.GoneIn)
            {
                // Already in, or never gathering in the first place. The decision was taken when the
                // sortie opened (SortieFormsUp, read in AddDemand) and carried across the reconcile;
                // a sortie that has gone in takes its reinforcements at the objective, not back at a
                // form-up point.
                sortie.HasFormUp = false;
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
                // Both elements, because a fighter patrol has no strike element at all and would
                // otherwise report "forming 0/0" for its whole wait (user decision 2026-09-16).
                int atFormUp = casAtFormUp + capAtFormUp;
                if (sortie.FormUpReported != atFormUp)
                {
                    sortie.FormUpReported = atFormUp;
                    CommanderAiLog.Note(
                        hq,
                        $"{sortie.Label}: forming {atFormUp}/{sortie.Wanted + sortie.CapsWanted} at the form-up point"
                            + (sortie.AradPending ? ", waiting on its ARAD sortie." : "."));
                }

                continue;
            }

            if (sortie.FallingBack)
            {
                // Its escort is falling back (design.md, air-fallback-posture_20260916 Section 4.3):
                // the package does not go in behind fighters that have just left. It goes in on the
                // first review after the escort re-engages.
                continue;
            }

            sortie.GoneIn = true;
            bool complete = casAtFormUp >= sortie.Wanted && capAtFormUp >= sortie.CapsWanted;
            CommanderAiLog.Note(
                hq,
                complete
                    ? $"{sortie.Label}: goes in whole ({casAtFormUp + capAtFormUp} aircraft)."
                    : $"{sortie.Label}: goes in after {timeout:0} s wait "
                        + $"({casAtFormUp + capAtFormUp}/{sortie.Wanted + sortie.CapsWanted}).");
        }
    }

    // ---- Running the strike package (design.md, strike-packages_20260915 Section 5) ----

    /// <summary>
    /// Whether the strike sortie is finished (design Section 5). Three ways, and the first of them
    /// is the ordinary one:
    /// <list type="bullet">
    /// <item>the strike was DELIVERED and the escorts have finished their loiter over the target —
    /// the package has done what it was ordered to do. Without this case (fix, 2026-09-15) a
    /// successful package held its airframes for the whole twelve-minute maximum, which is eight
    /// minutes of a strike element orbiting ground it had already hit while the next strike could
    /// not be ordered and the wing could not have the aeroplanes back;</item>
    /// <item>the strike element is spent or lost, so there is nothing left to deliver with;</item>
    /// <item>the package has been open <paramref name="maxMinutes"/> — the backstop for one that is
    /// never going to finish.</item>
    /// </list>
    /// The loiter clock is read through <see cref="EscortReleases"/> rather than compared again, so
    /// the escorts going home and the sortie closing can never disagree about when the package is
    /// over. Pure, for the self-check.
    /// </summary>
    /// <param name="strikeAlive">The package still has a strike airframe to use — one bound and
    /// alive, or none yet bound and the package still forming up.</param>
    /// <param name="minutesSinceGoIn">Minutes since the package went in, or negative while it has
    /// not.</param>
    internal static bool StrikeEnds(
        bool strikeAlive,
        bool delivered,
        float minutesSinceGoIn,
        float loiterMinutes,
        float minutesOpen,
        float maxMinutes)
    {
        if (delivered && EscortReleases(minutesSinceGoIn, loiterMinutes))
        {
            return true;
        }

        if (!strikeAlive)
        {
            return true;
        }

        // A package that has only just gone in is given its loiter before the maximum closes it
        // (fix, 2026-09-15). The form-up wait and the maximum are both clocks that start when the
        // sortie is ORDERED, so a package that spent its whole form-up wait assembling went in at
        // the deadline and was closed in the same breath: the 2026-09-15 match logged `goes in
        // (2 strike airframe(s), 6 escort)` and `abandoned: 12 min open without going in` one line
        // apart, with the aeroplanes still climbing out. Once the package is actually on its way,
        // the loiter is the clock that matters, and it is the same one the escorts go home on.
        if (minutesSinceGoIn >= 0f && minutesSinceGoIn < loiterMinutes)
        {
            return false;
        }

        // The wing's one "waited long enough, give up" rule (LiftWaitedTooLong, design.md
        // air-survival-layer_20260916; the fallback's own give-up already forwards to it). Same
        // convention, same answer: limit-inclusive, and a maximum of zero or less would wait for ever
        // — which the config check below (`the package's maximum life is positive`) already refuses at
        // load, so the merge changes nothing for any configuration the mod accepts.
        return LiftWaitedTooLong(minutesOpen * 60f, maxMinutes);
    }

    /// <summary>Whether the escorts have held over the target long enough to be let go (design
    /// Section 5). A negative <paramref name="minutesSinceGoIn"/> means the package has not gone in,
    /// and the escorts are not loitering at all. Pure, for the self-check.</summary>
    internal static bool EscortReleases(float minutesSinceGoIn, float loiterMinutes)
    {
        return minutesSinceGoIn >= 0f && minutesSinceGoIn >= loiterMinutes;
    }

    /// <summary>The live strike sortie flying against this attack's target, or null. Matched on the
    /// target itself — the point or the airbase — because the strike and the attack are two
    /// different things aimed at one place.</summary>
    private static CommanderAirSortie? FindStrikeFor(OperationsState state, CommanderOperationsMission mission)
    {
        CommanderAirSortie? strike = state.StrikeSortie;
        if (strike == null)
        {
            return null;
        }

        if (mission.Point != null)
        {
            return ReferenceEquals(strike.Point, mission.Point) ? strike : null;
        }

        return mission.TargetAirbase != null && ReferenceEquals(strike.TargetAirbase, mission.TargetAirbase)
            ? strike
            : null;
    }

    /// <summary>Whether the strike ahead of this attack has actually gone in and put an airframe over
    /// the target — what the attack's go-in waits for (<see cref="AttackMayGoIn"/>).</summary>
    private static bool StrikeDeliveredFor(OperationsState state, CommanderOperationsMission mission)
    {
        return FindStrikeFor(state, mission)?.Delivered == true;
    }

    /// <summary>
    /// The strike package's own run, once the ordinary package machinery has decided whether it goes
    /// in this review: stamp the go-in, mark the strike delivered the moment an airframe reaches the
    /// target ring, and let the escorts go once they have loitered their time. Called from the air
    /// step straight after <see cref="UpdatePackages"/>, so the go-in this review decided is acted on
    /// in the same review rather than the next.
    /// </summary>
    private void UpdateStrikePackage(FactionHQ hq, OperationsState state)
    {
        CommanderAirSortie? sortie = state.StrikeSortie;
        if (sortie == null || !sortie.GoneIn)
        {
            return;
        }

        if (sortie.WentInAt < 0f)
        {
            sortie.WentInAt = Time.time;
            CommanderAiLog.Note(
                hq,
                $"strike on {sortie.Label} goes in ({sortie.Cas.Count} strike airframe(s), "
                    + $"{sortie.Caps.Count} escort).");
        }

        // "Delivered" is the same read the attack's own CAS hold makes (CountOnStation): the package
        // has gone in AND an airframe of it is inside the target ring. Never unset once set — the
        // attack behind it must not be made to wait a second time.
        if (!sortie.Delivered && CountOnStation(sortie, sortie.Cas) > 0)
        {
            sortie.Delivered = true;
            CommanderAiLog.Note(hq, $"strike on {sortie.Label} is over the target.");
        }

        float minutesSinceGoIn = (Time.time - sortie.WentInAt) / 60f;
        if (sortie.Caps.Count > 0
            && EscortReleases(minutesSinceGoIn, CommanderSettings.StrikeLoiterMinutes))
        {
            // Through the ordinary release door, so a fighter another sortie wants is retasked
            // rather than sent home (ReleaseDisposal is the rule; this is one more caller of it).
            CommanderAiLog.Note(
                hq,
                $"strike on {sortie.Label}: its {sortie.Caps.Count} escort(s) have held "
                    + $"{CommanderSettings.StrikeLoiterMinutes:0} min and are released.");
            ReleaseBoundAirframes(hq, state, sortie, sortie.Caps, state.AirSorties);
            sortie.Caps.Clear();
            sortie.CapsWanted = 0;
            sortie.EscortWanted = 0;
        }
    }

    /// <summary>Drops the commander's one strike sortie and restarts the strike clock. The bookkeeping
    /// <see cref="CloseFinishedStrike"/> always did, in one place now that the air fallback posture's
    /// stand-down also abandons a strike (2026-09-16, Reuse rule 4). Clearing <c>state.StrikeSortie</c>
    /// is what makes the next review's demand walk leave it out.</summary>
    private static void ForgetStrikeSortie(OperationsState state)
    {
        state.StrikeSortie = null;
        state.LastStrikeAt = Time.time;
        state.StrikeClockMinutes = 0f;
        state.StrikeClockReported = -1;
    }

    /// <summary>
    /// Closes the strike sortie once it is finished (design Section 5) and starts the struck point's
    /// cooldown. Called at the top of the strike clock, which runs before the air step: clearing
    /// <c>state.StrikeSortie</c> is what makes this review's demand walk leave it out, and the
    /// reconcile then releases its airframes through the ordinary path.
    /// </summary>
    private void CloseFinishedStrike(FactionHQ hq, OperationsState state)
    {
        CommanderAirSortie? sortie = state.StrikeSortie;
        if (sortie == null)
        {
            return;
        }

        int aliveStrike = 0;
        for (int i = 0; i < sortie.Cas.Count; i++)
        {
            Aircraft aircraft = sortie.Cas[i];
            if (aircraft != null && !aircraft.disabled)
            {
                aliveStrike++;
            }
        }

        // A package that has not gone in yet and has nothing bound is still forming, not spent: the
        // buy may not have been able to afford the element this review.
        bool strikeAlive = aliveStrike > 0 || sortie.WentInAt < 0f;
        float minutesOpen = sortie.OpenedAt < 0f ? 0f : (Time.time - sortie.OpenedAt) / 60f;
        float minutesSinceGoIn = sortie.WentInAt < 0f ? -1f : (Time.time - sortie.WentInAt) / 60f;
        if (!StrikeEnds(
                strikeAlive,
                sortie.Delivered,
                minutesSinceGoIn,
                CommanderSettings.StrikeLoiterMinutes,
                minutesOpen,
                CommanderSettings.StrikeSortieMaxMinutes))
        {
            return;
        }

        ForgetStrikeSortie(state);

        if (sortie.Delivered)
        {
            // The cooldown is the reward for a strike that actually landed (fix, 2026-09-15): a
            // package that never reached the target has told the commander nothing about the point,
            // so putting it out of reach for ten minutes would punish the wing for the buy it could
            // not afford and leave the most valuable enemy point unstruck for the rest of the match.
            if (sortie.Point != null)
            {
                state.StrikeCooldownUntil[sortie.Point] =
                    Time.time + Mathf.Max(0f, CommanderSettings.StrikePointCooldownMinutes) * 60f;
            }

            // Both sides through the same read (fix, 2026-09-15). DefendersAtOrder was the EFFECTIVE
            // count — the live picture or the floor a failed attack left, whichever is larger — and
            // comparing it against a raw live count credited the strike with every vehicle the floor
            // was carrying, so a package that destroyed nothing could report four kills.
            int left = EffectiveObserved(
                CountObserved(hq, sortie.Center),
                GetObservedFloor(state, ObservedFloorKey(sortie.Point, sortie.TargetAirbase)));
            int destroyed = Mathf.Max(0, sortie.DefendersAtOrder - left);
            CommanderAiLog.Note(
                hq, $"strike on {sortie.Label} done: {destroyed} defenders destroyed ({left} still observed).");
            return;
        }

        // The reason has to match what actually happened (fix, 2026-09-15). "Without going in" was
        // printed whenever the strike element was still alive, including for a package that HAD gone
        // in and simply never put an airframe over the target — which read as a flat contradiction
        // of the go-in line above it.
        string reason;
        if (!strikeAlive)
        {
            reason = sortie.WentInAt < 0f
                ? "its strike element was lost before it went in"
                : "its strike element was lost before it reached the target";
        }
        else if (sortie.WentInAt >= 0f)
        {
            reason = $"{minutesOpen:0} min open (went in {minutesSinceGoIn:0} min ago, nothing delivered)";
        }
        else
        {
            reason = $"{minutesOpen:0} min open without going in";
        }

        CommanderAiLog.Note(hq, $"strike on {sortie.Label} abandoned: {reason}.");
    }

    /// <summary>The strike package's run: when it ends and when its escorts go home. Both numbers are
    /// settings a player may retune into a package that never ends or an escort that leaves before
    /// the strike it is covering has made its pass.</summary>
    private static void CheckStrikeRun(List<string> failures)
    {
        float max = CommanderSettings.StrikeSortieMaxMinutes;
        float loiter = CommanderSettings.StrikeLoiterMinutes;

        Expect(
            failures,
            "a package with its strike element alive and time left keeps flying",
            StrikeEnds(true, delivered: false, -1f, 4f, 1f, 12f),
            false);
        Expect(
            failures,
            "a package that has lost its strike element is over",
            StrikeEnds(false, delivered: false, -1f, 4f, 1f, 12f),
            true);
        Expect(
            failures,
            "a package at its maximum life is closed",
            StrikeEnds(true, delivered: false, -1f, 4f, 12f, 12f),
            true);
        Expect(
            failures,
            "a package one second short of its maximum is not",
            StrikeEnds(true, delivered: false, -1f, 4f, 11.9f, 12f),
            false);
        Expect(
            failures,
            "a package that has lost everything is closed even at zero minutes",
            StrikeEnds(false, delivered: false, -1f, 4f, 0f, 12f),
            true);

        // The delivered case (fix, 2026-09-15): a package that has done its job goes home rather
        // than orbiting a struck point for the rest of its twelve minutes.
        Expect(
            failures,
            "a delivered package whose escorts have finished their loiter is done",
            StrikeEnds(true, delivered: true, 4f, 4f, 5f, 12f),
            true);
        Expect(
            failures,
            "a delivered package still inside its loiter keeps flying",
            StrikeEnds(true, delivered: true, 3.9f, 4f, 5f, 12f),
            false);
        Expect(
            failures,
            "a package that has gone in but delivered nothing is not done by the loiter clock",
            StrikeEnds(true, delivered: false, 40f, 4f, 5f, 12f),
            false);
        Expect(
            failures,
            "a delivered package that has not gone in at all cannot be finished by a loiter it never started",
            StrikeEnds(true, delivered: true, -1f, 4f, 5f, 12f),
            false);
        Expect(
            failures,
            "a delivered package closes on the same clock that sends its escorts home, never a different one",
            StrikeEnds(true, delivered: true, 4f, 4f, 5f, 12f),
            EscortReleases(4f, 4f));
        Expect(
            failures,
            "a delivered package still ends at its maximum even with a loiter it can never finish",
            StrikeEnds(true, delivered: true, -1f, 4f, 12f, 12f),
            true);

        // A package that went in at the form-up deadline is not closed in the same breath (fix,
        // 2026-09-15). The live log that found this read `goes in` and `abandoned: 12 min open
        // without going in` one line apart.
        Expect(
            failures,
            "a package that goes in exactly at its maximum is given its loiter, not closed at once",
            StrikeEnds(true, delivered: false, 0f, 4f, 12f, 12f),
            false);
        Expect(
            failures,
            "one review later it is still flying its loiter",
            StrikeEnds(true, delivered: false, 0.5f, 4f, 12.5f, 12f),
            false);
        Expect(
            failures,
            "once the loiter is up an undelivered package past its maximum is closed",
            StrikeEnds(true, delivered: false, 4f, 4f, 16f, 12f),
            true);
        Expect(
            failures,
            "the loiter grace never keeps a package whose strike element is gone",
            StrikeEnds(false, delivered: false, 0f, 4f, 12f, 12f),
            true);
        Expect(
            failures,
            "the grace is the loiter clock, not a second one",
            StrikeEnds(true, delivered: false, CommanderSettings.StrikeLoiterMinutes, CommanderSettings.StrikeLoiterMinutes, 99f, 12f),
            true);
        Expect(
            failures,
            "a package still forming is closed at its maximum exactly as before",
            StrikeEnds(true, delivered: false, -1f, 4f, 12f, 12f),
            true);

        Expect(failures, "escorts that have loitered their time are released", EscortReleases(4f, 4f), true);
        Expect(failures, "escorts one moment short of it hold on", EscortReleases(3.9f, 4f), false);
        Expect(failures, "escorts over a package that has not gone in are not loitering at all", EscortReleases(-1f, 4f), false);
        Expect(failures, "escorts well past the loiter are released", EscortReleases(40f, 4f), true);

        Expect(failures, "the package's maximum life is positive; check the Operations section of the config", max > 0f, true);
        Expect(
            failures,
            "the package lives longer than it takes to form up, or it would be closed before it flew; check the Operations section of the config",
            max * 60f > CommanderSettings.PackageFormUpSeconds,
            true);
        Expect(failures, "the escort loiter is positive; check the Operations section of the config", loiter > 0f, true);
        Expect(
            failures,
            "the escorts go home before the package itself is closed; check the Operations section of the config",
            loiter < max,
            true);
    }

    /// <summary>
    /// An objective's package holds only while an ARAD sortie near it is genuinely still inbound
    /// (design SS5). The demand pass raises the flag the moment a belt is found; this clears it the
    /// moment the suppression airframe is over the belt, or the belt dissolves and its sortie is
    /// gone.
    /// </summary>
    private static void RefreshAradHolds(FactionHQ hq, OperationsState state)
    {
        for (int i = 0; i < state.AirSorties.Count; i++)
        {
            CommanderAirSortie objective = state.AirSorties[i];
            // The strike package waits for its suppression exactly as a CAS package does (design.md,
            // strike-packages_20260915 Section 5: "the anti-radiation element goes in one review
            // before the rest"), so it reads the same hold rather than a second copy of it. A lift
            // cover that expects a sweep is held and released on the same clock
            // (air-mobile-platoons_20260915 Section 3) — it is the transport that waits on it.
            if (objective.Kind != CommanderSortieKind.Objective
                && objective.Kind != CommanderSortieKind.Strike
                && !(objective.Kind == CommanderSortieKind.Cap && objective.AradWanted > 0))
            {
                continue;
            }

            bool inbound = false;
            bool onStation = false;
            for (int a = 0; a < state.AirSorties.Count; a++)
            {
                CommanderAirSortie arad = state.AirSorties[a];
                if (arad.Kind != CommanderSortieKind.Arad
                    || CommanderGameAccess.HorizontalDistance(arad.Center.AsVector3(), objective.Center.AsVector3())
                        > ObservedRadiusMeters)
                {
                    continue;
                }

                if (AradStillInbound(arad))
                {
                    inbound = true;
                }
                else
                {
                    onStation = true;
                }
            }

            // The same read, two answers (Reuse rule 4). "Still inbound" is what holds the CAS
            // package at its form-up point; "over the belt" is what releases this objective's
            // attack helicopters (user decision 2026-09-14), and it is remembered from here on.
            if (onStation && !objective.AradFlown)
            {
                objective.AradFlown = true;
                CommanderAiLog.Note(
                    hq,
                    $"{objective.Label}: suppression is on station; its CAS may be flown by helicopters again.");
            }

            if (objective.AradPending)
            {
                objective.AradPending = inbound;
            }
        }
    }

    /// <summary>Whether this sortie is still holding at its form-up point — what the task sync and
    /// the binding read to decide where an airframe is sent. Two facts and no third: it has a point
    /// to hold at, and it has not gone in. The rule itself (<see cref="SortieFormsUp"/>) was asked
    /// once, when the sortie opened, and its answer lives in <c>GoneIn</c>; re-deriving it here off
    /// <c>Wanted</c> and <c>CapsWanted</c>, which the sizing rebuilds every thirty seconds, could
    /// drop a sortie out of its own form-up halfway through it with nothing logged.</summary>
    private static bool SortieHoldsAtFormUp(CommanderAirSortie sortie)
    {
        return sortie.HasFormUp && !sortie.GoneIn;
    }

    /// <summary>Step the form-up search walks back toward the base when the point it wanted has the
    /// enemy inside the standoff: 2 km, a fraction of the 20 km standoff so the point lands as far
    /// forward as the standoff allows, and coarse enough that the search is at most six probes on the
    /// nominal 12 km leg.</summary>
    private const float FormUpPullBackStepMeters = 2000f;

    /// <summary>
    /// The first distance along the base-to-objective line, walking back from
    /// <paramref name="along"/> toward the base in steps of <paramref name="step"/>, at which
    /// <paramref name="clearAt"/> says the point is clear of the enemy; 0 (over the base) when none
    /// is. Pure apart from the probe it is handed.
    /// </summary>
    internal static float FirstClearAlong(float along, float step, System.Func<float, bool> clearAt)
    {
        for (float candidate = Mathf.Max(0f, along); candidate > 0f; candidate -= Mathf.Max(1f, step))
        {
            if (clearAt(candidate))
            {
                return candidate;
            }
        }

        return 0f;
    }

    /// <summary>
    /// The package's orbit (design SS3): <see cref="PackageFormUpDistanceMeters"/> from the held
    /// base nearest the objective, along the line toward it, and NOWHERE NEAR THE ENEMY (user, 2026-09-16):
    /// the point keeps <c>PackageFormUpStandoffMeters</c> (20 km) from every enemy-held point and base, every
    /// spotted hostile ground unit and every tracked hostile aircraft, walking back toward the base in
    /// <see cref="FormUpPullBackStepMeters"/> steps until it does, and forming up over the base itself
    /// (under its own air defence) when nothing on the leg is clear. Before this the only pull-back
    /// was for a hostile ON the line within 8 km, which left packages orbiting beside enemy bases.
    /// False when the commander holds no base to measure from.
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

        // Its own setting since 2026-09-16 (audit Section 2): this used to read AirPostureRingMeters,
        // so retuning the outnumbered retreat trigger silently moved every package's orbit. Same
        // shipped number, so the split changed nothing by itself.
        float standoff = CommanderSettings.PackageFormUpStandoffMeters;
        float along = PackageFormUpDistance(
            length,
            PackageFormUpDistanceMeters,
            NearestTrackedHostileAlongLine(hq, origin, dx / length, dz / length, length, standoff),
            standoff);
        float ux = dx / length;
        float uz = dz / length;
        float y = Mathf.Max(origin.y, objective.y);
        along = FirstClearAlong(
            along,
            FormUpPullBackStepMeters,
            candidate => FormUpClearOfEnemy(hq, new GlobalPosition(origin.x + ux * candidate, y, origin.z + uz * candidate), standoff));
        formUp = new GlobalPosition(origin.x + ux * along, y, origin.z + uz * along);
        return true;
    }

    /// <summary>Whether a candidate form-up point has no enemy within <paramref name="standoff"/>:
    /// no enemy-held point or base and no spotted hostile ground unit (<c>NearestEnemyMeters</c>, the
    /// siting rule's own measure) and no tracked hostile aircraft of any kind.</summary>
    private static bool FormUpClearOfEnemy(FactionHQ hq, GlobalPosition candidate, float standoff)
    {
        return NearestEnemyMeters(hq, candidate, out _, out _) > standoff
            && CountHostileAirInRing(hq, candidate, standoff) == 0;
    }

    /// <summary>
    /// How far along the base-to-objective line the nearest tracked hostile sits, or -1 when nothing
    /// tracked lies ahead of the base on it. Only hostiles whose sideways offset is inside the
    /// stand-off ring count: one sitting 40 km off the flank is not on this route. Aircraft as well
    /// as ground units — a fighter sweep is exactly what a forming package must not orbit into.
    /// </summary>
    private static float NearestTrackedHostileAlongLine(
        FactionHQ hq, GlobalPosition origin, float dirX, float dirZ, float legMeters, float sidewaysMeters)
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
            if (sideways > sidewaysMeters)
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
}
