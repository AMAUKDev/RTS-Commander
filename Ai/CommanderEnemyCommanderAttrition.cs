using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The air attrition read (user decision 2026-09-15, revised 2026-09-16): a rolling ten-minute
/// record of what each commander launched and lost per side of the wing — fighters, and strike
/// airframes — read by the airframe buy. A side that is losing more than a third of what it launches
/// is <b>bleeding</b>, and a bleeding side ESCALATES: it buys the best airframe in the tier it can
/// afford instead of the cheapest, the same switch the threat read already throws. That is all it
/// does. Losing aircraft means buy BETTER, never buy nothing (user decision 2026-09-16: "escalate,
/// never hold").
/// <para>
/// Why: the 2026-09-15 overnight match launched about 1,800 aircraft and lost about 1,100. Nothing
/// in the buy read losses at all — the fund, the demand and the ceiling all said "buy", so the wing
/// fed the same cheap airframe into the same sky for hours. The per-sortie loss cooldown
/// (<c>CommanderOperationsService.StampAirLoss</c>) stands a single objective down for two minutes;
/// this is the commander-wide read of the same signal.
/// </para>
/// <para>
/// <b>What was removed on 2026-09-16, and why.</b> Until then a bleeding side whose best affordable
/// airframe was no better than the type that was dying went further: it halved the commander's
/// airborne ceiling and held that side's buys until five quiet minutes. That fed itself and froze
/// both commanders. The air budget is sized as the room left under the airborne ceiling times the
/// cheapest wanted airframe, so halving the ceiling left no room, the budget collapsed to a single
/// airframe (<c>air saved 174 (cap 174)</c> against a balance of 2,376 with a dozen open requests),
/// and with one airframe's budget the best affordable pick is always the cheapest one — so the
/// escalation could never fire, the hold was re-applied every review, and nothing was ever quiet for
/// five minutes. One fighter bought every few minutes while the money piled up. The escalation is
/// the part that was asked for and it stays; the hold and the halving are gone, and "already flying
/// the best it can afford" is now simply a fact the commander reports once and buys through.
/// </para>
/// <para>
/// One definition, two callers: the enemy commander and the player-side AI commander both buy
/// through this service, keyed by HQ, so both get the brake. Launches are counted when the
/// operations claim owns the registered airframe (<c>TryClaimAircraft</c>), losses when a counted
/// airframe leaves the world without having been returned to inventory or written off by the
/// mod's own stuck-on-deck despawn — a recovery is not a loss.
/// </para>
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>
    /// Minutes of launches and losses the attrition read looks back over. Ten (user decision
    /// 2026-09-15: "losses in the last 10 min"): long enough that a package of four lost together
    /// is one event rather than a trend, short enough that the wing reacts inside the match rather
    /// than after it. Internal: the self-check reads it.
    /// </summary>
    internal const float AttritionWindowMinutes = 10f;

    /// <summary>
    /// A side is bleeding when its losses exceed one in this many launches over the window: three
    /// (user decision 2026-09-15: "exceed a third of launches"). Written as an integer denominator
    /// so the test is exact whole-number arithmetic — <c>losses * 3 &gt; launches</c> — and a
    /// retune cannot land on a float boundary. Internal: the self-check reads it.
    /// </summary>
    internal const int AttritionLossDenominator = 3;

    /// <summary>
    /// The fewest launches in the window before the ratio means anything: six. One loss out of two
    /// launches is a third; it is also one aeroplane. Six is a home patrol plus a package — the
    /// smallest wing that has actually been tested against the sky. Internal: the self-check reads it.
    /// </summary>
    internal const int AttritionMinLaunches = 6;

    /// <summary>Which half of the wing an airframe's losses count against. Transports and the radar
    /// airframe count for neither: the radar airframe has its own ten-minute loss cooldown
    /// (<c>AwacsLossCooldownMinutes</c>) and transports are the insertion machinery, not the wing
    /// that fights. Internal: the operations claim reports launches by it.</summary>
    internal enum AirAttritionSide
    {
        None,
        Fighter,
        Strike,
    }

    /// <summary>One side's rolling record. The launch and loss lists are appended in time order,
    /// so pruning the window is a removal from the front.</summary>
    private sealed class AirAttritionSideRecord
    {
        internal readonly List<float> LaunchTimes = new();
        internal readonly List<float> LossTimes = new();
        internal readonly List<AircraftDefinition> LossTypes = new();

        /// <summary>The side has crossed into bleeding and its buys are already escalating; the
        /// escalation line is printed once per crossing, not per buy.</summary>
        internal bool Escalating;

        /// <summary>The side is bleeding and the best airframe it can afford is no better than the
        /// one that is dying. Nothing is held — the buy goes on — but the fact is worth saying once
        /// per crossing rather than every review (user decision 2026-09-16).</summary>
        internal bool BestAffordableReported;

        internal void Prune(float now, float windowSeconds)
        {
            float oldest = now - windowSeconds;
            int drop = 0;
            while (drop < LaunchTimes.Count && LaunchTimes[drop] < oldest)
            {
                drop++;
            }

            LaunchTimes.RemoveRange(0, drop);

            drop = 0;
            while (drop < LossTimes.Count && LossTimes[drop] < oldest)
            {
                drop++;
            }

            LossTimes.RemoveRange(0, drop);
            LossTypes.RemoveRange(0, drop);
        }

        internal void Clear()
        {
            LaunchTimes.Clear();
            LossTimes.Clear();
            LossTypes.Clear();
        }

        /// <summary>The airframe type lost most often inside the window — "what is dying" — or null
        /// when nothing has been lost.</summary>
        internal AircraftDefinition? MostLostType()
        {
            AircraftDefinition? most = null;
            int mostCount = 0;
            for (int i = 0; i < LossTypes.Count; i++)
            {
                int count = 0;
                for (int j = 0; j < LossTypes.Count; j++)
                {
                    if (ReferenceEquals(LossTypes[j], LossTypes[i]))
                    {
                        count++;
                    }
                }

                if (count > mostCount)
                {
                    mostCount = count;
                    most = LossTypes[i];
                }
            }

            return most;
        }
    }

    /// <summary>What the claim recorded for one counted airframe: which side it flies for and what
    /// it is, kept here so a loss can be typed after the Unity object is gone.</summary>
    private readonly struct AirAttritionAirframe
    {
        internal AirAttritionAirframe(AirAttritionSide side, AircraftDefinition definition)
        {
            Side = side;
            Definition = definition;
        }

        internal AirAttritionSide Side { get; }
        internal AircraftDefinition Definition { get; }
    }

    /// <summary>One commander's attrition ledger.</summary>
    private sealed class AirAttritionLedger
    {
        internal readonly Dictionary<Aircraft, AirAttritionAirframe> Airborne = new();
        internal readonly AirAttritionSideRecord Fighter = new();
        internal readonly AirAttritionSideRecord Strike = new();

        internal AirAttritionSideRecord? Side(AirAttritionSide side)
        {
            return side switch
            {
                AirAttritionSide.Fighter => Fighter,
                AirAttritionSide.Strike => Strike,
                _ => null,
            };
        }
    }

    /// <summary>Per commander. Ledgers for HQs no longer in <see cref="states"/> are dropped by the
    /// review, so a session reset (which clears the states) empties this a review later.</summary>
    private readonly Dictionary<FactionHQ, AirAttritionLedger> airAttrition = new();
    private readonly List<Aircraft> attritionStale = new();
    private readonly List<FactionHQ> attritionStaleHqs = new();

    // ---- The pure rules, for the self-check ----

    /// <summary>Which side of the wing a buy role's launches and losses count against. Pure, for
    /// the self-check.</summary>
    internal static AirAttritionSide AttritionSideOf(AirRole role)
    {
        return role switch
        {
            AirRole.Fighter => AirAttritionSide.Fighter,
            AirRole.Strike or AirRole.RotaryCas or AirRole.Arad => AirAttritionSide.Strike,
            _ => AirAttritionSide.None,
        };
    }

    /// <summary>Whether a side is bleeding: at least <paramref name="minLaunches"/> launches in the
    /// window and losses exceeding one in <see cref="AttritionLossDenominator"/> of them. Pure, for
    /// the self-check.</summary>
    internal static bool AttritionBleeding(int losses, int launches, int minLaunches)
    {
        return launches >= minLaunches && losses * AttritionLossDenominator > launches;
    }

    /// <summary>Whether a bleeding side's buy is an escalation worth making: only when the airframe
    /// it is about to buy is rated better for the job than the one that is dying. An equal or
    /// lesser airframe is the same aeroplane into the same sky, which is the brake's case. Pure,
    /// for the self-check.</summary>
    internal static bool AttritionEscalates(float pickRating, float dyingRating)
    {
        return pickRating > dyingRating;
    }

    /// <summary>
    /// Whether a bleeding side buys at all, pure (user decision 2026-09-16: "escalate, never hold").
    /// Always yes. It exists as a named rule because the thing it replaced — a hold that bought
    /// nothing whenever the best affordable airframe was no better than the one dying — fed itself
    /// and froze both commanders for a whole match, and a rule with a name and a check cannot be
    /// reintroduced by accident.
    /// </summary>
    internal static bool AttritionBuysOn(bool bleeding, bool anythingBetterAffordable)
    {
        _ = bleeding;
        _ = anythingBetterAffordable;
        return true;
    }

    /// <summary>The one rating a side compares airframes by — the game's own air-to-air rating for
    /// the fighter side, its air-to-ground rating for the strike side — the same asset data the
    /// tier table (<see cref="ForRole"/>) reads, so the escalation and the tier can never disagree
    /// about which airframe is better. Pure, for the self-check.</summary>
    internal static float AttritionSideRating(AirAttritionSide side, AircraftDefinition definition)
    {
        return side == AirAttritionSide.Fighter
            ? definition.roleIdentity.antiAir
            : definition.roleIdentity.antiSurface;
    }

    /// <summary>The plain word the log uses for a side.</summary>
    private static string AttritionSideWord(AirAttritionSide side)
    {
        return side == AirAttritionSide.Fighter ? "fighter" : "strike airframe";
    }

    // ---- Recording ----

    /// <summary>Counts one launch: called by the operations claim the moment a registered airframe
    /// is matched to the commander's pending launch, with the role the buy chose it for. Transports
    /// and the radar airframe are not counted (<see cref="AttritionSideOf"/>).</summary>
    internal void RecordAttritionLaunch(FactionHQ hq, Aircraft aircraft, AirRole role)
    {
        AirAttritionSide side = AttritionSideOf(role);
        if (side == AirAttritionSide.None || aircraft.definition is not AircraftDefinition definition)
        {
            return;
        }

        if (!airAttrition.TryGetValue(hq, out AirAttritionLedger ledger))
        {
            ledger = new AirAttritionLedger();
            airAttrition[hq] = ledger;
        }

        ledger.Airborne[aircraft] = new AirAttritionAirframe(side, definition);
        ledger.Side(side)!.LaunchTimes.Add(Time.time);
    }

    /// <summary>An airframe left the world for a reason that is not a loss — returned to inventory
    /// after landing (the <c>Aircraft.ReturnToInventory</c> postfix) or despawned by the mod's own
    /// stuck-on-deck write-off — so it stops being counted before the loss sweep can see it gone.
    /// One definition, two callers.</summary>
    internal static void NoteAirframeNotLost(Aircraft? aircraft)
    {
        if (Instance == null || aircraft == null)
        {
            return;
        }

        foreach (KeyValuePair<FactionHQ, AirAttritionLedger> entry in Instance.airAttrition)
        {
            entry.Value.Airborne.Remove(aircraft);
        }

        // The loss line reads this too, so a recovery is reported as one (fix, 2026-09-15).
        Instance.recoveredAirframes.Add(aircraft);
    }

    /// <summary>
    /// The once-per-review sweep for one commander, beside <c>ReportLostAircraft</c> in
    /// <c>TaskAirWing</c>: every counted airframe that has left the world without being returned
    /// or written off is a loss on its side. Nothing is released here any more — there is no hold to
    /// release (user decision 2026-09-16); a side stops escalating when it stops bleeding, which the
    /// window's own pruning does on its own.
    /// </summary>
    private void ReviewAirAttrition(FactionHQ hq)
    {
        // Ledgers for commanders that are gone (a session reset clears the states; the local HQ's
        // state is dropped when the player commander is switched off).
        attritionStaleHqs.Clear();
        foreach (KeyValuePair<FactionHQ, AirAttritionLedger> entry in airAttrition)
        {
            if (entry.Key == null || !states.ContainsKey(entry.Key))
            {
                attritionStaleHqs.Add(entry.Key!);
            }
        }

        for (int i = 0; i < attritionStaleHqs.Count; i++)
        {
            airAttrition.Remove(attritionStaleHqs[i]);
        }

        if (!airAttrition.TryGetValue(hq, out AirAttritionLedger ledger))
        {
            return;
        }

        float now = Time.time;
        attritionStale.Clear();
        foreach (KeyValuePair<Aircraft, AirAttritionAirframe> entry in ledger.Airborne)
        {
            if (entry.Key == null || entry.Key.disabled)
            {
                attritionStale.Add(entry.Key!);
            }
        }

        for (int i = 0; i < attritionStale.Count; i++)
        {
            Aircraft aircraft = attritionStale[i];
            AirAttritionAirframe record = ledger.Airborne[aircraft];
            ledger.Airborne.Remove(aircraft);
            AirAttritionSideRecord side = ledger.Side(record.Side)!;
            side.LossTimes.Add(now);
            side.LossTypes.Add(record.Definition);
        }
    }

    // ---- What the buy reads ----

    /// <summary>Whether <paramref name="role"/>'s side is bleeding right now, with the window's two
    /// counts for the log. A side that has stopped bleeding while it was escalating says so once and
    /// goes back to the cheapest-when-quiet rule.</summary>
    private bool AttritionBleeding(FactionHQ hq, AirRole role, out int losses, out int launches)
    {
        losses = 0;
        launches = 0;
        AirAttritionSide side = AttritionSideOf(role);
        if (side == AirAttritionSide.None || !airAttrition.TryGetValue(hq, out AirAttritionLedger ledger))
        {
            return false;
        }

        AirAttritionSideRecord record = ledger.Side(side)!;
        record.Prune(Time.time, AttritionWindowMinutes * 60f);
        losses = record.LossTimes.Count;
        launches = record.LaunchTimes.Count;
        bool bleeding = AttritionBleeding(losses, launches, AttritionMinLaunches);
        if (!bleeding && record.Escalating)
        {
            record.Escalating = false;
            record.BestAffordableReported = false;
            CommanderAiLog.Note(
                hq,
                $"air attrition eased: {losses} of {launches} {AttritionSideWord(side)}s launched in the last "
                    + $"{AttritionWindowMinutes:0} min were lost; back to the cheapest in the tier when quiet.");
        }

        return bleeding;
    }

    /// <summary>
    /// What a bleeding side says about the airframe it is about to buy (user decision 2026-09-15,
    /// revised 2026-09-16). The buy ALWAYS goes ahead (<see cref="AttritionBuysOn"/>); this only
    /// writes the line. When <paramref name="choice"/> is rated better than the type that is dying
    /// that is the escalation, announced once per crossing. When it is not, the commander is already
    /// flying the best it can afford, which is a fact worth saying once and no reason to stop buying
    /// — stopping was what froze the wing (see this file's header).
    /// </summary>
    private void NoteAttritionPick(
        FactionHQ hq, AirRole role, AircraftDefinition choice, int losses, int launches)
    {
        AirAttritionSide side = AttritionSideOf(role);
        if (side == AirAttritionSide.None || !airAttrition.TryGetValue(hq, out AirAttritionLedger ledger))
        {
            return;
        }

        AirAttritionSideRecord record = ledger.Side(side)!;
        AircraftDefinition? dying = record.MostLostType();
        float dyingRating = dying == null ? float.NegativeInfinity : AttritionSideRating(side, dying);
        if (AttritionEscalates(AttritionSideRating(side, choice), dyingRating))
        {
            record.BestAffordableReported = false;
            if (!record.Escalating)
            {
                record.Escalating = true;
                CommanderAiLog.Note(
                    hq,
                    $"air attrition: {losses} of {launches} {AttritionSideWord(side)}s launched in the last "
                        + $"{AttritionWindowMinutes:0} min were lost; buying the best affordable {AttritionSideWord(side)} "
                        + $"({choice.unitName}) instead of the cheapest.");
            }

            return;
        }

        record.Escalating = true;
        if (record.BestAffordableReported)
        {
            return;
        }

        record.BestAffordableReported = true;
        CommanderAiLog.Note(
            hq,
            $"air attrition: {losses} of {launches} {AttritionSideWord(side)}s launched in the last "
                + $"{AttritionWindowMinutes:0} min were lost; already flying the best it can afford "
                + $"({choice.unitName}) — buying on.");
    }

    // ---- Self-check ----

    /// <summary>The attrition brake's rules at their named boundaries, called from
    /// <c>CheckAirBuyRules</c>.</summary>
    private static void CheckAirAttrition()
    {
        Expect("a CAP buy counts against the fighter side", AttritionSideOf(AirRole.Fighter) == AirAttritionSide.Fighter, true);
        Expect("a strike buy counts against the strike side", AttritionSideOf(AirRole.Strike) == AirAttritionSide.Strike, true);
        Expect("a helicopter CAS buy counts against the strike side", AttritionSideOf(AirRole.RotaryCas) == AirAttritionSide.Strike, true);
        Expect("a suppression buy counts against the strike side", AttritionSideOf(AirRole.Arad) == AirAttritionSide.Strike, true);
        Expect("a transport counts against neither side", AttritionSideOf(AirRole.Transport) == AirAttritionSide.None, true);
        Expect("the radar airframe counts against neither side: it has its own loss cooldown",
            AttritionSideOf(AirRole.Awacs) == AirAttritionSide.None, true);

        Expect("a third exactly is not bleeding", AttritionBleeding(losses: 10, launches: 30, minLaunches: 6), false);
        Expect("one more than a third is bleeding", AttritionBleeding(losses: 11, launches: 30, minLaunches: 6), true);
        Expect("the 2026-09-15 match (1,100 of 1,800) is bleeding", AttritionBleeding(1100, 1800, 6), true);
        Expect("one loss in two launches is too small a sample", AttritionBleeding(losses: 1, launches: 2, minLaunches: 6), false);
        Expect("three losses in six launches is a sample, and bleeding", AttritionBleeding(losses: 3, launches: 6, minLaunches: 6), true);
        Expect("no launches is never bleeding", AttritionBleeding(losses: 0, launches: 0, minLaunches: 6), false);
        Expect("the minimum sample is at least the launches a third can be read from",
            AttritionMinLaunches >= AttritionLossDenominator, true);

        Expect("a better-rated airframe is an escalation", AttritionEscalates(pickRating: 1.2f, dyingRating: 1.0f), true);
        Expect("the same airframe again is the brake", AttritionEscalates(pickRating: 1.0f, dyingRating: 1.0f), false);
        Expect("a worse airframe is the brake", AttritionEscalates(pickRating: 0.4f, dyingRating: 1.0f), false);
        Expect("with nothing yet lost every pick escalates", AttritionEscalates(pickRating: 0.4f, dyingRating: float.NegativeInfinity), true);

        // Escalate, never hold (user decision 2026-09-16). The rule that replaced the brake: a
        // bleeding side buys whatever its best affordable airframe is, including when that airframe
        // is the one already dying. The hold this replaced halved the ceiling, which collapsed the
        // air budget to a single airframe, which made the best affordable pick the cheapest one, which
        // re-applied the hold every review — a commander frozen on a growing balance.
        Expect("a bleeding side with something better affordable still buys", AttritionBuysOn(true, true), true);
        Expect("a bleeding side with nothing better affordable STILL buys", AttritionBuysOn(true, false), true);
        Expect("a quiet side buys", AttritionBuysOn(false, false), true);

        AircraftDefinition revoker = ScriptableObject.CreateInstance<AircraftDefinition>();
        revoker.roleIdentity.antiAir = 1.0f;
        revoker.roleIdentity.antiSurface = 0.3f;
        Expect("the fighter side rates by the air-to-air rating", AttritionSideRating(AirAttritionSide.Fighter, revoker), 1.0f);
        Expect("the strike side rates by the air-to-ground rating", AttritionSideRating(AirAttritionSide.Strike, revoker), 0.3f);
        Object.Destroy(revoker);

        // The window prune and the most-lost read, on a record built by hand.
        AirAttritionSideRecord record = new();
        AircraftDefinition cricket = ScriptableObject.CreateInstance<AircraftDefinition>();
        AircraftDefinition compass = ScriptableObject.CreateInstance<AircraftDefinition>();
        record.LaunchTimes.Add(0f);
        record.LaunchTimes.Add(500f);
        record.LaunchTimes.Add(700f);
        // 99 s, one second OLDER than the 600 s window at now = 700: the edge itself is kept, the
        // patient side of the boundary every other clock here uses (the check read 100 s and was
        // failing at plugin load, 2026-09-15).
        record.LossTimes.Add(99f);
        record.LossTypes.Add(compass);
        record.LossTimes.Add(650f);
        record.LossTypes.Add(cricket);
        record.LossTimes.Add(690f);
        record.LossTypes.Add(cricket);
        record.Prune(now: 700f, windowSeconds: 600f);
        Expect("a launch older than the window is forgotten", record.LaunchTimes.Count, 2);
        Expect("a loss older than the window is forgotten", record.LossTimes.Count, 2);
        Expect("the loss types are pruned in step with the loss times", record.LossTypes.Count, 2);
        Expect("what is dying is the type lost most inside the window", ReferenceEquals(record.MostLostType(), cricket), true);
        record.Clear();
        Expect("a cleared record has nothing dying", record.MostLostType() == null, true);
        Object.Destroy(cricket);
        Object.Destroy(compass);
    }
}
