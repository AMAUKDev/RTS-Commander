using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The commander priority ladder (design.md, commander-priorities_20260914; user decision
/// 2026-09-14): one pot per review, spent in a fixed order. Rung 1 is the home CAP and it is
/// <b>strict</b> — while it is short, nothing below it is bought. Rungs 2–4 (platoons and their air,
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
    /// Whether a short home CAP ends the review (user decision 2026-09-14: strict — while the CAP is
    /// short, nothing below it is bought, so the balance climbs toward the next fighter). It does NOT
    /// hold while the CAP cannot be filled this review, for either known cause: the airborne ceiling
    /// (it counts every faction aircraft, the player's included, so a strict hold under it would let
    /// the player park their own wing at the cap and starve the enemy commander's entire economy), or
    /// no air-to-air-capable airframe any held base will launch (2026-09-14: a strict hold on a CAP
    /// nothing can ever fill is a deadlock — the first build sat on a 400+ fund all match because a
    /// highway-strip commander had no Fighter-identity candidate, while its strips would happily have
    /// launched a Compass with Scythes). An unfillable CAP reads as satisfied for the ladder; the
    /// <c>ladder:</c> line still shows the true count and the periodic holds line says why. Pure, for
    /// the self-check.
    /// </summary>
    internal static bool LadderHoldsForCap(int capShort, bool unfillable)
    {
        return capShort > 0 && !unfillable;
    }

    /// <summary>
    /// One review's home-CAP read: the formula's inputs and results, for the strict hold, the
    /// <c>ladder:</c> line and the hold text.
    /// </summary>
    private readonly struct HomeCapRead
    {
        internal HomeCapRead(
            int wanted, int alive, int trackedAircraft, int losses, int capShort, bool ceilingBlocked, bool impossible, float spent, int buys)
        {
            Wanted = wanted;
            Alive = alive;
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
    /// Rung 1: size the home CAP from the formula, then buy toward it out of this review's pot — up
    /// to <see cref="MaxAirBuysPerReview"/> launches, while the budget and the airborne ceiling allow.
    /// When NO air-to-air-capable airframe can launch from any base this commander holds, the rung
    /// is impossible: nothing is bought, the strict hold is skipped (a CAP nothing can ever fill is
    /// a deadlock, 2026-09-14), and the lower rungs proceed. Whatever is left of the pot is the
    /// lower rungs' remainder; the strict hold itself is the caller's
    /// (<see cref="ReviewPurchases"/>), decided through <see cref="LadderHoldsForCap"/>.
    /// </summary>
    private HomeCapRead ReviewHomeCap(FactionHQ hq, CommanderState state, float spendable)
    {
        int tracked = CountTrackedEnemyAircraftNearBases(hq);
        int losses = CommanderOperationsService.HomeCapLosses(hq);
        int wanted = WantedHomeCap(
            CommanderSettings.HomeCapBaseline, CommanderSettings.HomeCapPerEnemyAircraft, tracked, losses,
            CommanderSettings.HomeCapMax);
        // A lent fighter is still held (design.md, smarter-air-wing_20260914 Section 9): the loan is
        // deliberately invisible to the shortfall, or the rung would buy a replacement for an
        // airframe that is already flying and about to be recalled anyway.
        int capShort = CommanderOperationsService.HomeCapShortfall(
            wanted, CommanderOperationsService.CountHomeCapFighters(hq), CommanderOperationsService.CountLentHomeCap(hq));

        // The deadlock valve (user follow-up, 2026-09-14): after the capability test (a loadout
        // that can fight air) and the strip test (the window's own acceptance pair), is there any
        // CAP candidate at all? Last resort included — a Cricket CAP beats a deadlocked ladder.
        bool impossible = capShort > 0 && !HasAnyCapCandidate(hq);

        float spent = 0f;
        int buys = 0;
        bool ceilingBlocked = false;
        for (int buy = 0; capShort > 0 && !impossible && AirBuyContinues(buy) && spendable - spent > 0f; buy++)
        {
            // Checked per buy, not once: the wing's other launches this same review (rung 2's sortie
            // buys run after this) and the player's own launches all eat the same ceiling.
            if (CountAirborne(hq) >= CommanderSettings.AirborneCeiling)
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
            capShort--;
        }

        return new HomeCapRead(wanted, wanted - capShort, tracked, losses, capShort, ceilingBlocked, impossible, spent, buys);
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

        // The strict hold, its ceiling carve-out and its deadlock valve (departures 1 and the
        // 2026-09-14 follow-up in plan.md): an unfillable CAP must not freeze the ladder.
        Expect("a short CAP ends the review", LadderHoldsForCap(1, unfillable: false), true);
        Expect("a full CAP does not hold", LadderHoldsForCap(0, unfillable: false), false);
        Expect("a surplus never holds", LadderHoldsForCap(-1, unfillable: false), false);
        Expect("a short CAP blocked by the airborne ceiling does not hold the whole economy",
            LadderHoldsForCap(2, unfillable: true), false);
        Expect("a short CAP no launchable airframe can fill does not hold the whole economy",
            LadderHoldsForCap(3, unfillable: true), false);

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
        float airSavingsCap)
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
        else if (cap.Short > 0 && LadderHoldsForCap(cap.Short, cap.CeilingBlocked))
        {
            draw = $"home CAP short {cap.Short} — nothing below it is bought this review";
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
            $"ladder: CAP {cap.Alive}/{cap.Wanted} ({CommanderSettings.HomeCapBaseline} base +{airTerm} air +{cap.Losses} losses"
                + (clamped ? $", capped at {CommanderSettings.HomeCapMax}" : string.Empty)
                + (lent > 0 ? $", {lent} lent" : string.Empty) + "), "
                + $"{draw}, spent CAP {capSpent:0} / platoons {platoonSpent:0} / pickets {picketSpent:0} / buildings {buildingSpent:0}, "
                // The savings AND what they are allowed to reach: a wing at its cap is saving for an
                // airframe it has actually asked for, a wing far below it is simply poor, and the
                // two used to look identical in the log.
                + (airSaved > 0f ? $"air saved {airSaved:0} (cap {airSavingsCap:0}), " : string.Empty)
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