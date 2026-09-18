using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The commander priority ladder (design.md, commander-priorities_20260914; user decision
/// 2026-09-14): one pot per review, spent in a fixed order. Rung 1 is the home CAP and it is
/// <b>strict up to the baseline only</b> (revised 2026-09-14) — while fewer than
/// <c>HomeCapBaseline</c> fighters are alive, nothing below it is bought; the threat and loss growth
/// above the baseline is ordinary CAP demand inside rung 2. Rungs 2–4 (platoons and their air,
/// air-delivered pickets, buildings) share the post-CAP remainder through a weighted draw each review,
/// with a floor for every rung that has open demand so the heavy platoon weight cannot starve the
/// other two. This partial holds the ladder's arithmetic; the review in
/// <see cref="CommanderEnemyCommanderService.ReviewPurchases"/> is the single spend site that walks it.
/// </summary>
/// <remarks>
/// The ladder replaced four unconnected pots, each with its own gate: the economy service's structure
/// hold-back (<c>GetEnemyBuildReserve</c>), the ground spender's quarter-of-balance floor
/// (<c>UnitSpendFloorShare</c>), the air wing's 40 % fund (<c>AccrueFund</c>/<c>AirFund</c>) and the
/// naval fund. Those were all fixes for the same starvation — spenders draining one shared
/// <c>factionFunds</c> balance faster than a big purchase could accumulate — and the ladder prevents
/// it structurally instead: priority is decided once, at the top, and everything below spends only
/// what its rung was granted.
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>
    /// How far from one of the commander's own airbases a tracked hostile aircraft still counts
    /// toward the home CAP's size. The same order of range as the radar that would see it
    /// (<c>ThreatRadiusMeters</c>, the "on the radar" range), so the CAP grows for raids the
    /// commander can actually know about — a low pass that has never been spotted grows nothing.
    /// </summary>
    internal const float HomeCapThreatRadiusMeters = 30000f;

    /// <summary>The ladder's three lower rungs, by draw position (rung 1 — home CAP — is not drawn;
    /// it is strict and always first).</summary>
    private const int RungPlatoons = 0;
    private const int RungPickets = 1;
    private const int RungBuildings = 2;

    /// <summary>Scratch for the draw, reused across reviews rather than allocated each time: the
    /// live weights (zeroed as rungs are drawn) and the resulting order.</summary>
    private readonly float[] ladderWeights = new float[3];
    private readonly List<int> ladderOrder = new();

    /// <summary>Scratch for the road-pair fallback price, reused across reviews rather than
    /// allocated each time: the commander's own vehicle catalog put through the insertion chooser.</summary>
    private readonly List<CommanderPlatoonRole> picketPairRoles = new();
    private readonly List<float> picketPairValues = new();
    private readonly List<int> picketPairPicks = new();

    /// <summary>
    /// What rung 3 is saving toward this review, with the live inputs: the cheapest complete
    /// insertion flight the supply side can price, falling back to the cheapest pair of ground
    /// vehicles this faction sells when no held airbase will launch a vehicle-carrying transport.
    /// The pure rule is <see cref="PicketSavingsTarget"/>'s; this reads the two prices.
    /// </summary>
    private float PicketFlightPrice(FactionHQ hq)
    {
        float flight = CommanderSupplyHeliService.Instance?.CheapestInsertionFlightValue(
            hq, CommanderOperationsService.MaxInsertionCargoVehicles) ?? float.MaxValue;
        // A launch refused with the bank at its target has proved the estimate low; the bank aims
        // at that flight's price until one gets away (pure rule PicketSavingsTargetWithFeedback).
        return PicketSavingsTargetWithFeedback(
            PicketSavingsTarget(flight, CheapestPicketPairValue()),
            CommanderOperationsService.RefusedInsertionFlightPrice(hq));
    }

    /// <summary>The bank's target with the refusal feedback folded in, pure: the estimate, or the
    /// refused flight's price when that is higher and the estimate is not "bank nothing" (zero — a
    /// commander that can fly nothing must not start banking on a stale refusal).</summary>
    internal static float PicketSavingsTargetWithFeedback(float estimate, float refusedPrice)
    {
        return estimate > 0f ? Mathf.Max(estimate, AffordablePrice(refusedPrice)) : 0f;
    }

    /// <summary>
    /// The cheapest pair of vehicles a picket could be built from at the depot, through the SAME
    /// chooser the insertion's cargo goes through (Reuse rule 4 — one
    /// <c>PickInsertionCargo</c>, three callers: the flight, the flight's price and this). Zero
    /// when the catalog cannot field a pair at all. The catalog is this review's, collected by
    /// <c>CollectCatalog</c> before the ladder walks.
    /// </summary>
    private float CheapestPicketPairValue()
    {
        picketPairRoles.Clear();
        picketPairValues.Clear();
        for (int i = 0; i < catalog.Count; i++)
        {
            picketPairRoles.Add(CommanderPlatoonRoles.Of(catalog[i]));
            picketPairValues.Add(Mathf.Max(0f, catalog[i].value));
        }

        CommanderOperationsService.PickInsertionCargo(
            picketPairRoles,
            picketPairValues,
            float.MaxValue,
            CommanderOperationsService.MaxInsertionCargoVehicles,
            picketPairPicks);
        float total = 0f;
        for (int i = 0; i < picketPairPicks.Count; i++)
        {
            total += picketPairValues[picketPairPicks[i]];
        }

        return total;
    }

    /// <summary>The weights' total — the draw's denominator. Pure, for the self-check.</summary>
    internal static float LadderWeightTotal(float[] weights)
    {
        float total = 0f;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] > 0f)
            {
                total += weights[i];
            }
        }

        return total;
    }

    /// <summary>The rung names the <c>ladder:</c> line prints, in draw order.</summary>
    private static string RungName(int rung)
    {
        return rung switch
        {
            RungPlatoons => "platoons",
            RungPickets => "pickets",
            _ => "buildings",
        };
    }

    /// <summary>
    /// Home-CAP fighters wanted: the baseline plus one per <paramref name="perEnemyAircraft"/> tracked
    /// hostile aircraft near the commander's bases plus one per recent CAP loss, capped at
    /// <paramref name="max"/> (user decision 2026-09-14, after watching both sides spam CAP: each
    /// side's CAP was the other's "enemy aircraft", so an uncapped formula fed itself). A max below
    /// the baseline still allows the baseline — the cap is on growth, not on the screen itself.
    /// Pure, for the self-check; the caller counts the aircraft and the losses.
    /// </summary>
    internal static int WantedHomeCap(int baseline, int perEnemyAircraft, int trackedEnemyAircraft, int losses, int max)
    {
        int grown = Mathf.Max(0, baseline)
            + HomeCapAirTerm(trackedEnemyAircraft, perEnemyAircraft)
            + Mathf.Max(0, losses);
        return Mathf.Min(grown, Mathf.Max(Mathf.Max(0, baseline), max));
    }

    /// <summary>
    /// The tracked-aircraft term of the CAP formula on its own: one more fighter per
    /// <paramref name="perEnemyAircraft"/> tracked, never negative. One definition, two callers
    /// (Reuse rule 4) — <see cref="WantedHomeCap"/> adds it up and the <c>ladder:</c> line reports
    /// it.
    /// <para>
    /// Reporting it is why it exists. The line used to DERIVE this number by subtracting the
    /// baseline and the losses from the wanted total, and the wanted total is clamped at
    /// <c>HomeCapMax</c> — so nine recent losses under a cap of four printed
    /// <c>CAP 4/4 (2 base +-7 air +9 losses)</c>, a negative count of enemy aircraft (user report,
    /// 2026-09-14). The terms have to be reported as they were computed, not reverse-engineered from
    /// a clamped sum.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static int HomeCapAirTerm(int trackedEnemyAircraft, int perEnemyAircraft)
    {
        return Mathf.Max(0, trackedEnemyAircraft) / Mathf.Max(1, perEnemyAircraft);
    }

    /// <summary>
    /// The floor reserved for every rung with open demand: <paramref name="floorPercent"/> percent of
    /// the post-CAP remainder, never negative and never more than the remainder itself. Pure, for the
    /// self-check. A rung with no demand is owed no floor at all — the caller only counts demanding
    /// rungs, which is the whole of that rule.
    /// </summary>
    internal static float LadderFloor(float remainder, float floorPercent)
    {
        float clamped = Mathf.Clamp(floorPercent, 0f, 100f);
        return Mathf.Min(Mathf.Max(0f, remainder) * clamped / 100f, Mathf.Max(0f, remainder));
    }

    /// <summary>
    /// What one drawn rung may spend this review: everything left of the post-CAP remainder, minus the
    /// floors still owed to the demanding rungs drawn after it — so however greedy the first rung's
    /// demand is, every later demanding rung keeps at least its floor. Never below zero. Pure, for
    /// the self-check.
    /// </summary>
    internal static float LadderRungBudget(float pool, float floor, int demandingAfter)
    {
        return Mathf.Max(0f, Mathf.Max(0f, pool) - Mathf.Max(0f, floor) * Mathf.Max(0, demandingAfter));
    }

    /// <summary>
    /// The share of rung 2's allocation set aside for the wing before any ground vehicle is bought
    /// (user decision 2026-09-14, "existing forces first"): one third while a sortie over something
    /// actually fighting is short of an airframe, nothing otherwise. The reserve is what makes
    /// "air support for what is already in contact comes first" true in money rather than only in
    /// intent — the wing's fund used to take its 40 % share of whatever the ground buyer LEFT, so a
    /// review that spent the rung on replacements left nothing for the platoon calling for help.
    /// </summary>
    internal const float Rung2AirReserveShare = 1f / 3f;

    /// <summary>
    /// How rung 2's allocation splits this review: <paramref name="airReserve"/> is set aside for
    /// the wing and <paramref name="groundShare"/> is what the order book and the platoon buyer may
    /// spend. Pure, for the self-check. Neither is ever negative, they always sum to the
    /// allocation, and with nothing in contact the whole allocation stays on the ground — the wing
    /// then takes its ordinary share out of it exactly as it does today.
    /// </summary>
    internal static void Rung2Split(
        float allocation, bool inContactShort, out float airReserve, out float groundShare)
    {
        float pot = Mathf.Max(0f, allocation);
        airReserve = inContactShort ? pot * Rung2AirReserveShare : 0f;
        groundShare = pot - airReserve;
    }

    /// <summary>
    /// Whether the ground's unspent share of rung 2 goes to the wing this review (user decision
    /// 2026-09-14). All three have to be true: the ground buyer is holding because every vehicle it
    /// fields is bought to order, its order book is empty, and the wing has an open request. Money
    /// nobody is going to spend is worth more in the air than in the bank — but a ground buyer that
    /// still has a line open is spending it, and a wing that has asked for nothing has no use for
    /// it. Pure, for the self-check; what the wing may actually keep is bounded by the fund's own
    /// ceiling at the call site (Reuse rule 4, one accumulator).
    /// </summary>
    internal static bool GroundShareGoesToWing(bool groundHoldsToBook, bool hasOpenBook, bool airDemandOpen)
    {
        return groundHoldsToBook && !hasOpenBook && airDemandOpen;
    }

    /// <summary>
    /// The share of the air side's own per-review allocation set aside for a radar airframe (user
    /// decision 2026-09-14): one quarter while the commander owns none and the loss cooldown is
    /// over, nothing otherwise. The counterweight to the turn-order fix of the same day — the radar
    /// airframe no longer outranks the fighters, so without a slice of its own it would simply never
    /// be bought on a front that always has fighter demand open.
    /// </summary>
    internal const float AwacsSliceShare = 0.25f;

    /// <summary>
    /// How the air side's allocation splits this review: <paramref name="awacsSlice"/> banks toward
    /// the radar airframe and <paramref name="turnShare"/> goes to the fighter/strike turn order.
    /// Pure, for the self-check. Neither is ever negative, they always sum to the allocation, and
    /// the slice is exactly zero once a radar airframe is owned — that is the whole of "it still
    /// arrives, and then it stops costing anything".
    /// </summary>
    internal static void AirSplit(
        float allocation, bool wantsAwacs, out float awacsSlice, out float turnShare)
    {
        float pot = Mathf.Max(0f, allocation);
        awacsSlice = wantsAwacs ? pot * AwacsSliceShare : 0f;
        turnShare = pot - awacsSlice;
    }

    /// <summary>
    /// The price rung 3 banks toward (departure 2026-09-14): one complete insertion flight — the
    /// cheapest launchable transport hull plus the vehicles the shared chooser loads for it — or,
    /// when no held airbase will launch a vehicle-carrying transport at all, the cheapest pair of
    /// ground vehicles the faction's own depot ladder offers, so a roster that cannot fly today is
    /// still saving something toward the picket it owes. Zero when neither is priceable, which
    /// reads as "bank nothing" everywhere it is used.
    /// <para>Both halves go through <see cref="AffordablePrice"/> (Reuse rule 4, one definition):
    /// an unlaunchable role reports <c>float.MaxValue</c>, and a ceiling of <c>float.MaxValue</c>
    /// would bank the whole remainder forever.</para>
    /// Pure, for the self-check; the caller prices the flight and the pair.
    /// </summary>
    internal static float PicketSavingsTarget(float flightPrice, float roadPairPrice)
    {
        float flight = AffordablePrice(flightPrice);
        return flight > 0f ? flight : AffordablePrice(roadPairPrice);
    }

    /// <summary>
    /// What a bank above its cap hands back to the pot this review, pure: everything past the cap,
    /// never negative. The rung's ceiling moves — a cheaper transport appears, a strip is lost, the
    /// roster stops fielding vehicle cargo — and money the bank may no longer hold must reach the
    /// rungs drawn after it rather than sit there for the rest of the match. The rule the air
    /// fund's own overflow follows, at rung 3.
    /// </summary>
    internal static float PicketSavingsExcess(float savings, float target)
    {
        return Mathf.Max(0f, savings - Mathf.Max(0f, target));
    }

    /// <summary>
    /// One pick of the weighted draw: <paramref name="t"/> in <c>[0, total)</c> lands on the rung whose
    /// weight range contains it, first index winning a tie. -1 when no weight is positive — a rung
    /// with no weight (or no demand: the caller zeroes both together) is never drawn. Pure, for the
    /// self-check; the caller draws the permutation by zeroing each winner and drawing again.
    /// </summary>
    internal static int LadderDrawPick(float[] weights, float t)
    {
        float total = 0f;
        int last = -1;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] > 0f)
            {
                total += weights[i];
                last = i;
            }
        }

        if (total <= 0f)
        {
            return -1;
        }

        float running = 0f;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] <= 0f)
            {
                continue;
            }

            if (t < running + weights[i])
            {
                return i;
            }

            running += weights[i];
        }

        // t landed on or past the total (only possible for t == total exactly): the last positive
        // rung, so a boundary draw is never lost and never returns -1 by accident.
        return last;
    }

    /// <summary>
    /// Whether a short home CAP ends the review. The strict hold applies to the BASELINE ONLY (user
    /// decision 2026-09-14, revised): while fewer than <paramref name="baseline"/> fighters are
    /// alive, nothing below rung 1 is bought, so the balance climbs toward the next fighter.
    /// Everything the formula wants ABOVE the baseline — the threat and loss terms, up to
    /// <c>HomeCapMax</c> — is ordinary CAP-type demand in rung 2 and holds nothing.
    /// <para>
    /// The revision is the whole point of this change (user report, 2026-09-14). The hold used to
    /// fire on the grown total, and the grown total includes a loss term that climbs all match:
    /// <c>CAP 2/4 (2 base +2 air +6 losses, capped at 4), home CAP short 2 — nothing below it is
    /// bought</c> printed on every review for many minutes, balance 3–44, and not one CAS airframe,
    /// helicopter or truck was bought in all that time. A commander that has lost six fighters is
    /// exactly the commander that must still be allowed to buy ground.
    /// </para>
    /// It does NOT hold while the CAP cannot be filled this review, for either known cause: the
    /// airborne ceiling (it counts every faction aircraft, the player's included, so a strict hold
    /// under it would let the player park their own wing at the cap and starve the enemy commander's
    /// entire economy), or no air-to-air-capable airframe any held base will launch (2026-09-14: a
    /// strict hold on a CAP nothing can ever fill is a deadlock — the first build sat on a 400+ fund
    /// all match because a highway-strip commander had no Fighter-identity candidate, while its
    /// strips would happily have launched a Compass with Scythes). An unfillable CAP reads as
    /// satisfied for the ladder; the <c>ladder:</c> line still shows the true count and the periodic
    /// holds line says why. Pure, for the self-check.
    /// </summary>
    internal static bool LadderHoldsForCap(int alive, int baseline, bool unfillable)
    {
        return alive < Mathf.Max(0, baseline) && !unfillable;
    }

    /// <summary>
    /// The home CAP rung 2 is allowed to buy: everything the formula wants above the strict baseline
    /// rung 1 owns, never negative (user decision 2026-09-14). This is the half that goes through the
    /// wing's ordinary CAP/CAS turn alternation, so a threat-grown or loss-grown CAP competes with
    /// the ground attack for the same buys instead of pre-empting the whole ladder. Pure, for the
    /// self-check; the demand walk reads it with the live counts.
    /// </summary>
    internal static int HomeCapExtraWanted(int wanted, int alive, int baseline)
    {
        return Mathf.Max(0, wanted - Mathf.Max(alive, Mathf.Max(0, baseline)));
    }

    /// <summary>
    /// One review's home-CAP read: the formula's inputs and results, for the strict hold, the
    /// <c>ladder:</c> line and the hold text.
    /// </summary>
    private readonly struct HomeCapRead
    {
        internal HomeCapRead(
            int wanted,
            int alive,
            int baseline,
            int trackedAircraft,
            int losses,
            int capShort,
            bool ceilingBlocked,
            bool impossible,
            float spent,
            int buys)
        {
            Wanted = wanted;
            Alive = alive;
            Baseline = baseline;
            TrackedAircraft = trackedAircraft;
            Losses = losses;
            Short = capShort;
            CeilingBlocked = ceilingBlocked;
            Impossible = impossible;
            Spent = spent;
            Buys = buys;
        }

        /// <summary>Home-CAP fighters the formula wants this review.</summary>
        internal readonly int Wanted;

        /// <summary>Home-CAP fighters alive (airborne or parked on deck).</summary>
        internal readonly int Alive;

        /// <summary>The strict baseline this review ran against — the only part of the CAP that can
        /// hold the ladder (<see cref="LadderHoldsForCap"/>).</summary>
        internal readonly int Baseline;

        /// <summary>Hostile aircraft tracked within <see cref="HomeCapThreatRadiusMeters"/> of the commander's airbases.</summary>
        internal readonly int TrackedAircraft;

        /// <summary>Home-CAP fighters lost to enemy air inside the memory window.</summary>
        internal readonly int Losses;

        /// <summary>Wanted minus alive, after this review's buys.</summary>
        internal readonly int Short;

        /// <summary>Whether the airborne ceiling is what stopped the buys (see <see cref="LadderHoldsForCap"/>).</summary>
        internal readonly bool CeilingBlocked;

        /// <summary>Whether NO air-to-air-capable airframe can launch from any base this commander
        /// holds, after the capability and strip tests (2026-09-14). The strict rung is skipped and
        /// the lower rungs proceed; the periodic holds line names the bases.</summary>
        internal readonly bool Impossible;

        /// <summary>What rung 1 spent this review.</summary>
        internal readonly float Spent;

        /// <summary>How many CAP fighters this review launched.</summary>
        internal readonly int Buys;
    }

    /// <summary>
    /// Rung 1: size the home CAP from the formula, then buy toward THE BASELINE out of this review's
    /// pot, while the shortfall, the budget and the airborne ceiling allow (the three-launch cap
    /// this rung used to carry is retired — user decision 2026-09-14, air buying is money-limited;
    /// <see cref="MaxAirBuysPerReviewSafety"/> is only a runaway guard). Only the baseline is rung 1's (user decision 2026-09-14, revised): the
    /// threat and loss terms above it are handed to rung 2 as ordinary CAP demand
    /// (<see cref="HomeCapExtraWanted"/>), bought through the wing's own CAP/CAS alternation out of
    /// the same pot. When NO air-to-air-capable airframe can launch from any base this commander
    /// holds, the rung is impossible: nothing is bought, the strict hold is skipped (a CAP nothing
    /// can ever fill is a deadlock, 2026-09-14), and the lower rungs proceed. Whatever is left of
    /// the pot is the lower rungs' remainder; the strict hold itself is the caller's
    /// (<see cref="ReviewPurchases"/>), decided through <see cref="LadderHoldsForCap"/>.
    /// </summary>
    private HomeCapRead ReviewHomeCap(FactionHQ hq, CommanderState state, float spendable)
    {
        int tracked = CountTrackedEnemyAircraftNearBases(hq);
        int losses = CommanderOperationsService.HomeCapLosses(hq);
        int baseline = Mathf.Max(0, CommanderSettings.HomeCapBaseline);
        int wanted = WantedHomeCap(
            CommanderSettings.HomeCapBaseline, CommanderSettings.HomeCapPerEnemyAircraft, tracked, losses,
            CommanderSettings.HomeCapMax);
        // A lent fighter is still held (design.md, smarter-air-wing_20260914 Section 9): the loan is
        // deliberately invisible to the count, or the rung would buy a replacement for an airframe
        // that is already flying and about to be recalled anyway — which is exactly what
        // CountHomeCapFighters already does, so the loan needs no subtraction here.
        int alive = CommanderOperationsService.CountHomeCapFighters(hq);

        // What rung 1 itself may buy: the strict baseline and nothing more.
        int strictShort = Mathf.Max(0, baseline - alive);

        // The deadlock valve (user follow-up, 2026-09-14): after the capability test (a loadout
        // that can fight air) and the strip test (the window's own acceptance pair), is there any
        // CAP candidate at all? Last resort included — a Cricket CAP beats a deadlocked ladder.
        bool impossible = strictShort > 0 && !HasAnyCapCandidate(hq);

        float spent = 0f;
        int buys = 0;
        bool ceilingBlocked = false;
        for (int buy = 0; buys < strictShort && !impossible && AirBuyContinues(buy) && spendable - spent > 0f; buy++)
        {
            // Checked per buy, not once: the wing's other launches this same review (rung 2's sortie
            // buys run after this) and the player's own launches all eat the same ceiling.
            if (!CommanderOperationsService.AirBuyAllowed(hq, standingPatrol: false))
            {
                ceilingBlocked = true;
                break;
            }

            float cost = BuyHomeCapFighter(hq, state, spendable - spent);
            if (cost <= 0f)
            {
                break;
            }

            spent += cost;
            buys++;
        }

        alive += buys;
        return new HomeCapRead(
            wanted,
            alive,
            baseline,
            tracked,
            losses,
            CommanderOperationsService.HomeCapShortfall(
                wanted, alive, CommanderOperationsService.CountLentHomeCap(hq)),
            ceilingBlocked,
            impossible,
            spent,
            buys);
    }

    /// <summary>
    /// The ladder's arithmetic, run once at plugin load beside the other enemy-commander checks: the
    /// CAP formula, the weights, the floor arithmetic and the strict hold. These are the numbers that
    /// decide what every commanded HQ buys, and a retune that inverts one of them changes every
    /// commander's behaviour with nothing to say so in the console.
    /// </summary>
    private static void CheckLadder()
    {
        // CAP formula (design Section 2): baseline 2, one per two tracked, no limit, plus losses.
        Expect("an empty sky leaves the baseline CAP", WantedHomeCap(2, 2, 0, 0, 4), 2);
        Expect("one tracked aircraft is still one CAP pair", WantedHomeCap(2, 2, 1, 0, 4), 2);
        Expect("two tracked aircraft buy the third fighter", WantedHomeCap(2, 2, 2, 0, 4), 3);
        Expect("three tracked aircraft still buy the third", WantedHomeCap(2, 2, 3, 0, 4), 3);
        Expect("four tracked aircraft buy the fourth", WantedHomeCap(2, 2, 4, 0, 4), 4);
        Expect("one CAP loss buys one replacement", WantedHomeCap(2, 2, 0, 1, 4), 3);
        Expect("two CAP losses buy two replacements", WantedHomeCap(2, 2, 0, 2, 4), 4);
        Expect("losses and tracked aircraft stop at the cap", WantedHomeCap(2, 2, 3, 2, 4), 4);
        Expect("the cap holds however many aircraft are tracked", WantedHomeCap(2, 2, 40, 5, 4), 4);
        Expect("a cap below the baseline still allows the baseline", WantedHomeCap(2, 2, 0, 0, 1), 2);

        // The reported air term (fix, 2026-09-14): reported as computed, never derived back out of
        // the clamped total. The observed failure printed "+-7 air +9 losses" under a cap of 4.
        Expect("no tracked aircraft is no air term", HomeCapAirTerm(0, 2), 0);
        Expect("one tracked aircraft is still no extra fighter", HomeCapAirTerm(1, 2), 0);
        Expect("two tracked aircraft are one extra fighter", HomeCapAirTerm(2, 2), 1);
        Expect("seven tracked aircraft are three extra fighters", HomeCapAirTerm(7, 2), 3);
        Expect("a negative tracking read is never a negative air term", HomeCapAirTerm(-4, 2), 0);
        Expect("a zero divisor still counts one per aircraft", HomeCapAirTerm(3, 0), 3);
        Expect(
            "the air term stays positive when losses push the formula past its cap (the +-7 bug)",
            HomeCapAirTerm(3, 2) >= 0 && WantedHomeCap(2, 2, 3, 9, 4) == 4,
            true);
        Expect("one aircraft per one tracked when the knob is turned to one, under a high cap", WantedHomeCap(2, 1, 3, 0, 9), 5);

        // The strict hold is BASELINE-ONLY (user decision 2026-09-14, revised), plus its ceiling
        // carve-out and its deadlock valve (departures 1 and the 2026-09-14 follow-up in plan.md):
        // an unfillable CAP must not freeze the ladder, and neither may a CAP grown by losses.
        Expect("a CAP below its baseline ends the review", LadderHoldsForCap(1, 2, unfillable: false), true);
        Expect("an empty CAP ends the review", LadderHoldsForCap(0, 2, unfillable: false), true);
        Expect("a CAP at its baseline does not hold, however much the formula wants above it",
            LadderHoldsForCap(2, 2, unfillable: false), false);
        Expect("a CAP above its baseline never holds", LadderHoldsForCap(3, 2, unfillable: false), false);
        Expect("a zero baseline can never hold the ladder", LadderHoldsForCap(0, 0, unfillable: false), false);
        Expect("a CAP below its baseline blocked by the airborne ceiling does not hold the whole economy",
            LadderHoldsForCap(0, 2, unfillable: true), false);
        Expect("a CAP below its baseline no launchable airframe can fill does not hold the whole economy",
            LadderHoldsForCap(1, 4, unfillable: true), false);

        // The half rung 2 serves: everything above the baseline, and nothing the baseline owns.
        Expect("a CAP at its baseline hands the growth to rung 2", HomeCapExtraWanted(4, 2, 2), 2);
        Expect("one fighter above the baseline leaves one for rung 2", HomeCapExtraWanted(4, 3, 2), 1);
        Expect("a full CAP asks rung 2 for nothing", HomeCapExtraWanted(4, 4, 2), 0);
        Expect("a surplus CAP asks rung 2 for nothing", HomeCapExtraWanted(2, 4, 2), 0);
        Expect("rung 2 never counts the baseline rung 1 still owes", HomeCapExtraWanted(4, 1, 2), 2);
        Expect("an ungrown CAP is entirely rung 1's", HomeCapExtraWanted(2, 0, 2), 0);

        // Floors (design Section 3): ten percent of the post-CAP remainder, never more, never less
        // than zero, and a rung owed no floor leaves the pool whole.
        Expect("ten percent of the remainder is the floor", LadderFloor(100f, 10f), 10f);
        Expect("an empty remainder has no floor", LadderFloor(0f, 10f), 0f);
        Expect("a zero floor setting reserves nothing", LadderFloor(100f, 0f), 0f);
        Expect("a hundred-percent floor takes the whole remainder and no more", LadderFloor(100f, 100f), 100f);
        Expect("a negative remainder has no floor", LadderFloor(-50f, 10f), 0f);
        Expect("the first drawn rung of three spends the remainder minus both floors",
            LadderRungBudget(100f, 10f, 2), 80f);
        Expect("the last demanding rung owes no floor after it and spends what is left",
            LadderRungBudget(100f, 10f, 0), 100f);
        Expect("a pool smaller than the owed floors clamps at zero, never below",
            LadderRungBudget(15f, 10f, 2), 0f);
        Expect("the floor is subtracted once per rung still owed, not once per rung alive",
            LadderRungBudget(60f, 10f, 1), 50f);

        // Rung 2's air reserve (user decision 2026-09-14, "existing forces first").
        Rung2Split(90f, inContactShort: true, out float contactAir, out float contactGround);
        Expect("a contact review reserves exactly a third of the rung for the wing", contactAir, 30f);
        Expect("the ground keeps the other two thirds", contactGround, 60f);
        Expect("the split always adds back up to the allocation", contactAir + contactGround, 90f);
        Rung2Split(90f, inContactShort: false, out float quietAir, out float quietGround);
        Expect("a quiet review reserves nothing", quietAir, 0f);
        Expect("a quiet review leaves the whole allocation on the ground", quietGround, 90f);
        Rung2Split(-40f, inContactShort: true, out float negativeAir, out float negativeGround);
        Expect("a negative allocation reserves nothing", negativeAir, 0f);
        Expect("a negative allocation never hands the ground a negative share", negativeGround, 0f);
        Rung2Split(0f, inContactShort: true, out float emptyAir, out float emptyGround);
        Expect("an empty allocation splits into nothing at all", emptyAir + emptyGround, 0f);

        // The radar airframe's reserved slice (user decision 2026-09-14).
        AirSplit(80f, wantsAwacs: true, out float awacsSlice, out float turnShare);
        Expect("a commander with no radar airframe banks a quarter of the wing's allocation", awacsSlice, 20f);
        Expect("the fighters and strike airframes get the other three quarters", turnShare, 60f);
        Expect("the air split always adds back up to the allocation", awacsSlice + turnShare, 80f);
        AirSplit(80f, wantsAwacs: false, out float ownedSlice, out float ownedTurn);
        Expect("a commander that already owns a radar airframe banks nothing toward one", ownedSlice, 0f);
        Expect("and the whole allocation goes to the turn order instead", ownedTurn, 80f);
        AirSplit(-30f, wantsAwacs: true, out float negativeSlice, out float negativeTurn);
        Expect("a negative allocation banks nothing toward a radar airframe", negativeSlice, 0f);
        Expect("a negative allocation never hands the turn order a negative share", negativeTurn, 0f);

        // The savings target: a price to aim at, or nothing to aim at at all.
        Expect("the savings aim at the cheapest launchable radar airframe", AffordablePrice(145f), 145f);
        Expect("strips that launch no radar airframe bank nothing toward one", AffordablePrice(float.MaxValue), 0f);
        Expect("a zero price is not a target either", AffordablePrice(0f), 0f);

        // Rung 3's savings cap (departure 2026-09-14): one whole insertion flight, with the road
        // pair as the fallback when nothing can fly. These are the numbers that decide whether a
        // picket flight is ever affordable — the 2026-09-14 match refused every one of them.
        Expect("the picket bank aims at one whole flight", PicketSavingsTarget(134f, 40f), 134f);
        Expect("a refused flight dearer than the estimate raises the bank's target", PicketSavingsTargetWithFeedback(31f, 34f), 34f);
        Expect("a refused flight cheaper than the estimate changes nothing", PicketSavingsTargetWithFeedback(31f, 20f), 31f);
        Expect("no refusal leaves the estimate", PicketSavingsTargetWithFeedback(31f, 0f), 31f);
        Expect("a commander that can fly nothing banks nothing whatever was refused", PicketSavingsTargetWithFeedback(0f, 34f), 0f);
        Expect("the picket bank aims at the dearer of landing and airdrop flights",
            CommanderSupplyHeliService.InsertionSavingsTargetPrice(31f, 34f), 34f);
        Expect("an unlaunchable airdrop leaves the landing price as the target",
            CommanderSupplyHeliService.InsertionSavingsTargetPrice(31f, float.MaxValue), 31f);
        Expect("an unlaunchable landing leaves the airdrop price as the target",
            CommanderSupplyHeliService.InsertionSavingsTargetPrice(float.MaxValue, 34f), 34f);
        Expect(
            "a commander that can launch no transport banks toward the road pair instead",
            PicketSavingsTarget(float.MaxValue, 40f), 40f);
        Expect(
            "a commander with neither a flight nor a pair to price banks nothing",
            PicketSavingsTarget(float.MaxValue, 0f), 0f);
        Expect("an unpriced flight is not a target", PicketSavingsTarget(0f, 40f), 40f);

        // The bank itself, through the one accumulator (Reuse rule 4): it fills to the cap and
        // stops, it never overshoots on a rich review, and a cap that has fallen releases the rest.
        float picketBank = 0f;
        Expect("a lean review banks its whole share", AccrueFund(ref picketBank, 30f, 134f), 30f);
        Expect("and the bank holds it across reviews", picketBank, 30f);
        Expect("a rich review banks only up to the flight's price", AccrueFund(ref picketBank, 400f, 134f), 104f);
        Expect("a full bank covers exactly one flight, never more", picketBank, 134f);
        Expect("a full bank takes nothing further", AccrueFund(ref picketBank, 80f, 134f), 0f);
        Expect(
            "a full bank pays for the flight it saved for",
            CommanderOperationsService.PicketSavingsCover(picketBank, 134f), true);
        Expect(
            "a bank one short of the price still refuses the flight",
            CommanderOperationsService.PicketSavingsCover(133f, 134f), false);
        Expect(
            "an empty bank refuses the flight",
            CommanderOperationsService.PicketSavingsCover(0f, 134f), false);
        Expect(
            "a flight with no price is never covered, however full the bank",
            CommanderOperationsService.PicketSavingsCover(400f, 0f), false);
        // The excess a fallen cap releases — what the rung hands back to the pot the same review.
        Expect("a cheaper flight releases the difference back to the pot", PicketSavingsExcess(picketBank, 90f), 44f);
        Expect("a bank at its cap releases nothing", PicketSavingsExcess(134f, 134f), 0f);
        Expect("a bank below its cap releases nothing", PicketSavingsExcess(30f, 134f), 0f);
        Expect("a cap that fell to nothing releases the whole bank", PicketSavingsExcess(134f, 0f), 134f);

        // The weighted draw: first index wins the boundary, zero weights are never drawn.
        Expect("the draw at zero lands on the first weighted rung", LadderDrawPick(new[] { 60f, 20f, 20f }, 0f), 0);
        Expect("the draw just inside the first weight stays on the first rung",
            LadderDrawPick(new[] { 60f, 20f, 20f }, 59.99f), 0);
        Expect("the draw exactly at the first boundary moves to the second rung",
            LadderDrawPick(new[] { 60f, 20f, 20f }, 60f), 1);
        Expect("the draw at the second boundary moves to the third rung",
            LadderDrawPick(new[] { 60f, 20f, 20f }, 80f), 2);
        Expect("the draw exactly at the total falls on the last weighted rung, never nowhere",
            LadderDrawPick(new[] { 60f, 20f, 20f }, 100f), 2);
        Expect("a rung with zero weight is never drawn, wherever the draw lands",
            LadderDrawPick(new[] { 0f, 20f, 0f }, 19.99f), 1);
        Expect("weights with nothing positive draw nothing", LadderDrawPick(new[] { 0f, 0f, 0f }, 0f), -1);

        // The home-CAP candidate order moved to the airframe fitness tiers on 2026-09-14
        // (airframe-selection_20260914, DECISION-011): the identity-first ordering that used to be
        // checked here let a T/A-30 Compass hold the patrol while an FS-12 Revoker was launchable.
        // Its replacement — the tier table, the highest launchable tier and the within-tier choice —
        // is checked in CheckAirframeTiers, beside the rest of the air buy's rules.

        // Config guards, read into locals first so a retune cannot be folded away (the CheckPressure
        // pattern): the ladder's knobs have to stay in the ranges the arithmetic above assumes.
        int baseline = CommanderSettings.HomeCapBaseline;
        int perAircraft = CommanderSettings.HomeCapPerEnemyAircraft;
        float platoons = CommanderSettings.LadderPlatoonWeight;
        float pickets = CommanderSettings.LadderPicketWeight;
        float buildings = CommanderSettings.LadderBuildingWeight;
        float floorPercent = CommanderSettings.LadderRungFloorPercent;
        Expect("the home CAP baseline is zero or more; check the Commander section of the config",
            baseline >= 0, true);
        Expect("one CAP fighter per at least one tracked aircraft; check the Commander section of the config",
            perAircraft >= 1, true);
        Expect("the ladder weights are all zero or more", platoons >= 0f && pickets >= 0f && buildings >= 0f, true);
        Expect("the ladder weights normalise (at least one is positive)",
            platoons + pickets + buildings > 0f, true);
        Expect("the rung floor is a percent, 0 to 100", floorPercent >= 0f && floorPercent <= 100f, true);
    }

    /// <summary>The commander's whole-number self-check comparison. Neutrally worded because it is
    /// shared: the ladder's rung arithmetic and the air side's tier selection both report through
    /// it (Reuse rule 4 — one definition, two callers). The case name says which rule failed.</summary>
    private static void Expect(string name, int actual, int expected)
    {
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError($"Enemy commander self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }

    private static void Expect(string name, float actual, float expected)
    {
        if (!Mathf.Approximately(actual, expected))
        {
            CommanderPlugin.Log.LogError($"Enemy ladder self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }

    /// <summary>
    /// The review's one <c>ladder:</c> line (design Section 4): the CAP's strength with its formula
    /// split, the draw order, what each rung took, and what was left unspent —
    /// <c>ladder: CAP 3/4 (2 base +1 air +1 losses), draw platoons&gt;buildings&gt;pickets, spent CAP 65
    /// / platoons 40 / pickets 0 / buildings 20, saved 35.</c> The pickets number is what insertion
    /// flights charged since the last line (they charge on the operations review's clock), and
    /// buildings is what the rung banked toward its next structure whether or not it built this
    /// review. The floor and pool detail rides behind <c>OperationsDebugLog</c>.
    /// </summary>
    private static void ReportLadder(
        FactionHQ hq,
        in HomeCapRead cap,
        List<int> drawOrder,
        float pot,
        float capSpent,
        float platoonSpent,
        float picketSpent,
        float buildingSpent,
        float airSaved,
        float airSavingsCap,
        bool airReserved,
        float awacsSaved,
        float awacsTarget,
        float picketSaved,
        float picketTarget,
        bool wantsAwacs = false)
    {
        float saved = Mathf.Max(0f, pot - capSpent - platoonSpent - buildingSpent);
        // Reported as computed, never derived back out of the clamped total (see HomeCapAirTerm).
        int airTerm = HomeCapAirTerm(cap.TrackedAircraft, CommanderSettings.HomeCapPerEnemyAircraft);
        bool clamped = CommanderSettings.HomeCapBaseline + airTerm + cap.Losses > cap.Wanted;
        string draw;
        if (cap.Impossible)
        {
            draw = "home CAP impossible — the rung is skipped and the lower rungs proceed";
        }
        else if (LadderHoldsForCap(cap.Alive, cap.Baseline, cap.CeilingBlocked))
        {
            draw = $"home CAP below its baseline ({cap.Alive}/{cap.Baseline}) — nothing below it is bought this review";
        }
        else if (drawOrder.Count == 0)
        {
            draw = "draw none";
        }
        else
        {
            System.Text.StringBuilder order = new();
            order.Append("draw ");
            for (int i = 0; i < drawOrder.Count; i++)
            {
                if (i > 0)
                {
                    order.Append('>');
                }

                order.Append(RungName(drawOrder[i]));
            }

            draw = order.ToString();
        }

        int lent = CommanderOperationsService.CountLentHomeCap(hq);
        CommanderAiLog.Note(
            hq,
            $"ladder: CAP {cap.Alive}/{cap.Wanted} (strict {Mathf.Min(cap.Alive, cap.Baseline)}/{cap.Baseline}, "
                + $"+{HomeCapExtraWanted(cap.Wanted, cap.Alive, cap.Baseline)} wanted; "
                + $"{CommanderSettings.HomeCapBaseline} base +{airTerm} air +{cap.Losses} losses"
                + (clamped ? $", capped at {CommanderSettings.HomeCapMax}" : string.Empty)
                + (lent > 0 ? $", {lent} lent" : string.Empty) + "), "
                + $"{draw}, spent CAP {capSpent:0} / platoons {platoonSpent:0} / pickets {picketSpent:0} / buildings {buildingSpent:0}, "
                // The savings AND what they are allowed to reach: a wing at its cap is saving for an
                // airframe it has actually asked for, a wing far below it is simply poor, and the
                // two used to look identical in the log.
                + (airSaved > 0f ? $"air saved {airSaved:0} (cap {airSavingsCap:0}), " : string.Empty)
                // Says WHY the ground got less this review (user decision 2026-09-14): a third of
                // rung 2 went to the wing because something in contact was calling for air.
                + (airReserved
                    ? $"rung2: air {Rung2AirReserveShare * 100f:0}% reserved (contact), "
                    : string.Empty)
                // The radar airframe's own slice against the price it is aiming at (user decision
                // 2026-09-14). Printed only while it is actually saving: a commander that owns one
                // banks nothing, and a zero target means its strips launch none at all.
                + (awacsTarget > 0f
                    ? $"awacs saved {awacsSaved:0}/{awacsTarget:0}, "
                    : (wantsAwacs ? "awacs: no held strip can launch one, nothing banked, " : string.Empty))
                // Rung 3's bank against the price of one flight (departure 2026-09-14). Printed
                // whenever there is a price to save toward, whether or not the rung was drawn: a
                // commander whose pickets are waiting for money and one whose pickets are waiting
                // for a landing zone used to read identically.
                + (picketTarget > 0f
                    ? $"pickets saved {picketSaved:0}/{picketTarget:0}, "
                    : string.Empty)
                + $"saved {saved:0}.");

        if (CommanderSettings.OperationsDebugLog && hq.faction != null)
        {
            CommanderPlugin.Log.LogInfo(
                $"Ops {CommanderPlayerCommanderService.CommanderLabel(hq)}: ladder detail: pool {pot - capSpent:0} "
                    + $"after the CAP, floor {LadderFloor(pot - capSpent, CommanderSettings.LadderRungFloorPercent):0} per demanding rung"
                    + (cap.CeilingBlocked ? ", CAP buys held by the airborne ceiling." : "."));
        }
    }
}