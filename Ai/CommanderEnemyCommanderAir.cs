using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The enemy commander's air force: buying airframes, launching them, and — the part that was
/// missing — telling them what to bomb.
/// </summary>
/// <remarks>
/// A bought aircraft used to be handed straight to the Basegame pilot AI, which is why the enemy
/// flew nothing worth calling an airstrike. <c>AIPilotCombatModes.NoTarget</c> counts fifteen ticks
/// without a target and switches to the landing state, and a freshly launched aircraft that has not
/// yet flown within sensor range of anything hits that counter long before it finds the player. The
/// mod already solves this for the player's aircraft with an Air Command mission — the
/// <c>NoTargetPrefix</c> zeroes the counter for anything carrying one — so the enemy's airframes now
/// get the same missions out of the same service.
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>Radius of the combat air patrol the commander holds over its own ground. Internal
    /// (one-word widening): the operations air step's hold-over-home and release-to-posture paths
    /// task the same box — one definition, two callers.</summary>
    internal const float HomeGuardRadiusMeters = 15000f;

    /// <summary>Reviews' worth of saving a fund may hold before the surplus goes back to the
    /// spender. Without a ceiling, a commander that can never buy a hull — no dock, none it can
    /// afford — would quietly withhold its naval share from the rest of the rung forever.
    /// Internal (one-word widening): the naval fund ceiling's self-check reads it.</summary>
    internal const int FundSaveReviews = 6;

    /// <summary>
    /// The most buy CALLS one review may make before the loop stops on principle rather than on
    /// evidence (user decision 2026-09-14, superseding the hard three-per-review cap of
    /// 2026-09-13). This is a runaway guard and nothing else: what actually stops the wing buying
    /// is money and the airborne ceiling (<see cref="AirBuyContinues(int, float, float, int, int)"/>),
    /// because a review that can pay for six airframes the sorties are asking for should field six.
    /// Twelve is comfortably above any real review — the fund is bounded by what the open demands
    /// are short and the room under the airborne ceiling
    /// (<see cref="AirFundCeiling(float, float, int, int, float)"/>) — so reaching it means something
    /// is looping, not that a commander is spending well.
    /// Internal: the self-check reads it.
    /// </summary>
    internal const int MaxAirBuysPerReviewSafety = 12;

    /// <summary>
    /// Share of rung 2's grant the wing sets aside each review toward its next airframe (fix,
    /// 2026-09-14). Forty percent, the share the retired air fund used to take: the rung's other
    /// half is the platoons, and an air wing that takes more than the ground it exists to support is
    /// the monoculture the role composition already exists to prevent. Bounded by
    /// <see cref="AirFundCeiling(float, float)"/> — the price of the dearest airframe an open demand
    /// actually wants, not a multiple of the grant.
    /// </summary>
    private const float AirBudgetShare = 0.4f;

    /// <summary>
    /// The ceiling on the air fund: the price of the dearest airframe an OPEN air demand actually
    /// wants — CAP, escort, CAS, AWACS or anti-radiation — and never less than the dearest fighter
    /// the commander's own strips will launch, so the wing can always replace the home patrol.
    /// Saving past the dearest thing the wing has asked for is hoarding, not saving: the ceiling it
    /// replaced was <see cref="FundSaveReviews"/> reviews of the share, which in the 2026-09-14
    /// match banked 372 out of the ground's rung while the order book sat six lines deep and one
    /// platoon held a contested front. Zero when no strip this commander holds will launch anything
    /// at all, which correctly sends the whole rung to the ground. Pure, for the self-check.
    /// </summary>
    internal static float AirFundCeiling(float dearestWantedValue, float dearestFighterValue)
    {
        return Mathf.Max(Mathf.Max(0f, dearestWantedValue), Mathf.Max(0f, dearestFighterValue));
    }

    /// <summary>
    /// The fund ceiling sized to THIS review's demand, pure (fix, 2026-09-14): the one-airframe
    /// ceiling above, or the cheapest wanted airframe times the number the open demands are short
    /// — whichever is more — where that number is also capped by the room left under the airborne
    /// ceiling. The one-airframe rule let the wing bank for exactly one purchase a review while the
    /// balance sat at 8,000 and thirty requests were open: `air saved 61 (cap 126)` on the enemy's
    /// ladder line with `budget 8253`. Saving for what is asked and can fly is not hoarding.
    /// <para><paramref name="cheapestWantedValue"/> is <c>float.MaxValue</c> when nothing open can
    /// launch; that adds nothing, so the ceiling falls back to the one-airframe rule.</para>
    /// </summary>
    internal static float AirFundCeiling(
        float dearestWantedValue, float dearestFighterValue, int openShortfall, int ceilingRoom, float cheapestWantedValue)
    {
        float oneAirframe = AirFundCeiling(dearestWantedValue, dearestFighterValue);
        int buyable = Mathf.Max(0, Mathf.Min(openShortfall, ceilingRoom));
        if (buyable == 0 || cheapestWantedValue <= 0f || cheapestWantedValue >= float.MaxValue)
        {
            return oneAirframe;
        }

        return Mathf.Max(oneAirframe, buyable * cheapestWantedValue);
    }

    /// <summary>The runaway guard on its own — all the home-CAP rung needs, because that loop is
    /// already bounded by how many fighters the patrol is short of and by its own budget. Pure, for
    /// the self-check.</summary>
    internal static bool AirBuyContinues(int boughtThisReview)
    {
        return boughtThisReview < MaxAirBuysPerReviewSafety;
    }

    /// <summary>
    /// Whether the wing's buy loop may shop again this review (user decision 2026-09-14): while its
    /// money covers the cheapest airframe any OPEN demand actually wants and the sky has room under
    /// the airborne ceiling. Money and the ceiling, not a count — the rule this replaces stopped at
    /// three buy calls a review, so the 2026-09-14 match launched two to four aircraft a review with
    /// the balance climbing past 600 and thirteen CAS requests unfilled.
    /// <para><paramref name="cheapestWantedValue"/> is <c>float.MaxValue</c> when no open demand can
    /// be launched at all and zero when nothing is open: both stop the loop, because there is
    /// nothing to buy either way.</para>
    /// Pure, for the self-check.
    /// </summary>
    internal static bool AirBuyContinues(
        int boughtThisReview, float fund, float cheapestWantedValue, int airborne, int airborneCeiling)
    {
        return AirBuyContinues(boughtThisReview)
            && airborne < airborneCeiling
            && cheapestWantedValue > 0f
            && cheapestWantedValue < float.MaxValue
            && fund >= cheapestWantedValue;
    }

    /// <summary>
    /// Sets a slice of what its rung granted aside, and returns what it actually took so the caller
    /// can deduct exactly that. This is the naval fund's accumulator — the one pot the ladder kept
    /// on purpose (design Reuse: "naval keeps its share as part of rung 2's air side, unchanged
    /// behaviour otherwise"): a hull is worth several ground vehicles, so a per-review slice has to
    /// accumulate or it never buys one. The air fund that used to live beside it is retired — the
    /// wing buys straight out of its rung's grant now.
    /// </summary>
    private static float AccrueFund(ref float fund, float share, float ceiling)
    {
        float taken = Mathf.Clamp(ceiling - fund, 0f, Mathf.Max(0f, share));
        fund += taken;
        return taken;
    }

    /// <summary>What an airframe is for, read off its own role data rather than a name table.</summary>
    /// <remarks>
    /// <c>UnitDefinition.roleIdentity</c> is the game's own answer to "what does this thing kill",
    /// and <c>captureCapacity</c> is its answer to "does it carry troops". Both are asset data that
    /// survives a patch; a list of aircraft names does not. Internal (one-word widening): the
    /// operations air step binds sorties by the same role — one mapping, two callers, like
    /// <see cref="CommanderPlatoonRoles.Of"/>.
    /// </remarks>
    internal enum AirRole
    {
        /// <summary>Ground attack. Expensive per airframe, which is why it cannot be the only buy.</summary>
        Strike,

        /// <summary>Air-to-air. Bought as a counter to the player being in the sky, not by default.</summary>
        Fighter,

        /// <summary>Carries troops or cargo. Rotary, and the Basegame flies it without any help.</summary>
        Transport,

        /// <summary>
        /// Ground attack flown by an attack helicopter (design.md, smarter-air-wing_20260914
        /// Section 2). A capability, not an identity: the candidate has a rotary flight model on its
        /// prefab AND a plane pilot the Air Command mission can steer — the SAH-46 Chicane carries
        /// both, which is why the wing can task it at all.
        /// </summary>
        RotaryCas,

        /// <summary>Carries the game's radar pod: the commander's single AWACS (Section 4).</summary>
        Awacs,

        /// <summary>Carries anti-radiation weapons: the suppression sortie (Section 5).</summary>
        Arad,
    }

    /// <summary>Internal (one-word widening, alongside <see cref="AirRole"/>): the operations air
    /// step binds a claimed or retasked airframe by the role the buy chose it for.</summary>
    internal static AirRole GetAirRole(AircraftDefinition definition)
    {
        if (definition.captureCapacity > 0 && !CommanderAirCommandService.HasPlanePilot(definition))
        {
            return AirRole.Transport;
        }

        return definition.roleIdentity.antiAir > definition.roleIdentity.antiSurface
            ? AirRole.Fighter
            : AirRole.Strike;
    }

    /// <summary>The CI-22 Cricket is LAST RESORT (user decision, 2026-09-13, corrected the same day
    /// from "Compass": "Cricket should almost never be used"): never bought or flown by a commander
    /// while any other AI-flyable airframe on the roster can fill the role. Keyed on the game's own
    /// data key — <c>jsonKey == "COIN"</c>, which is what the <c>Air roster</c> line prints for the
    /// Cricket, so the log and the rule always agree. Internal: the self-check builds one.</summary>
    internal static bool IsLastResortAirframe(AircraftDefinition definition)
    {
        return string.Equals(definition.jsonKey, "COIN", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A trainer airframe — the bottom tier alongside the last resort (design.md,
    /// airframe-selection_20260914 Section 1, DECISION-011). Keyed on the game's own data key the
    /// same way <see cref="IsLastResortAirframe"/> keys on <c>COIN</c>, so the <c>Air roster</c>
    /// line and the rule can never disagree: the T/A-30 Compass is <c>trainer</c> and the VT-7
    /// Vagrant is <c>VTOLTrainer1</c>. Both are aeroplanes a commander flies only because nothing
    /// built for the job can launch from the strips it holds. Internal: the self-check builds one.
    /// </summary>
    internal static bool IsTrainerAirframe(AircraftDefinition definition)
    {
        return definition.jsonKey != null
            && definition.jsonKey.IndexOf("trainer", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// How well an airframe fits a job, from the game's own role ratings (design.md,
    /// airframe-selection_20260914 Section 1, DECISION-011). The capability tests answer "can it
    /// hang the weapon"; this answers "is it the aeroplane for the job", which is what stopped a
    /// Cricket winning a CAP buy on price while a Revoker sat on the strip.
    /// </summary>
    internal enum AirframeTier
    {
        /// <summary>Air-superiority specialist: FS-12 Revoker, FS-20 Vortex.</summary>
        Fighter,

        /// <summary>
        /// Neither rating dominates the other by <see cref="FighterRatio"/>: the Alkyon AB-4, at
        /// 0.70 anti-air against 1.00 anti-surface. The KR-67 Ifrit this comment used to name is
        /// NOT multirole — the match of 2026-09-18 bought it at Fighter tier 212 times — and the
        /// mistake mattered, because a refusal keyed on this tier would have read as grounding the
        /// enemy's main fighter when it does nothing of the kind.
        /// </summary>
        Multirole,

        /// <summary>Ground-attack specialist: A-19 Brawler, SAH-46 Chicane, Alkyon AB-4.</summary>
        Strike,

        /// <summary>Trainers and the last resort — flown only when nothing above can launch.</summary>
        LastResort,

        /// <summary>
        /// Not a combat candidate for this role at all (design.md, airframe-selection_20260914
        /// Section 1: transports and radar/EW special-system airframes "are excluded from both").
        /// This tier appears in NO tier order, so <see cref="HighestLaunchableTier"/> can never
        /// choose it and <see cref="SelectInTier"/> is never asked for it — the exclusion is
        /// structural rather than a comparison that could be retuned past.
        /// <para>
        /// It exists because reading a transport's role ratings gave it a real tier: the UH-90 Ibis
        /// is rated A/A 0.27 / A/G 0.90, which is the strike tier by arithmetic, and the log duly
        /// printed <c>CAP tier Strike / CAS tier Strike</c> for a troop helicopter. The user then
        /// watched one get bound to a platoon's patrol (2026-09-14). A transport is not a poor
        /// fighter; it is not a fighter.
        /// </para>
        /// </summary>
        Excluded,
    }

    /// <summary>
    /// How far one role rating must lead the other before the airframe counts as a specialist
    /// rather than a multirole. Half again (1.5) is the smallest lead that separates the game's own
    /// rosters cleanly: it puts the Revoker and the Vortex in <see cref="AirframeTier.Fighter"/> and
    /// the Brawler and the Chicane in <see cref="AirframeTier.Strike"/> while leaving the KR-67
    /// Ifrit — an airframe the game itself names <c>Multirole1</c> — in the middle. A smaller ratio
    /// would call the Ifrit a fighter and put it ahead of the Revoker on CAP; a larger one would
    /// collapse the whole roster into one tier and hand the choice back to price, which is the bug
    /// this table exists to fix. Internal: the self-check reads it.
    /// </summary>
    internal const float FighterRatio = 1.5f;

    /// <summary>The CAP preference order: the aeroplane built for air-to-air first, the specialist
    /// built for the other job last but one, and the trainers and the last resort at the bottom.
    /// Static so the walk allocates nothing per review.</summary>
    private static readonly AirframeTier[] CapTierOrder =
    {
        AirframeTier.Fighter, AirframeTier.Multirole, AirframeTier.Strike, AirframeTier.LastResort,
    };

    /// <summary>The CAS preference order — the CAP order reversed, except that the bottom tier
    /// stays the bottom: a trainer is the last choice for every job, not the first choice for the
    /// opposite one.</summary>
    private static readonly AirframeTier[] CasTierOrder =
    {
        AirframeTier.Strike, AirframeTier.Multirole, AirframeTier.Fighter, AirframeTier.LastResort,
    };

    /// <summary>
    /// The tier order a role buys down (design Section 2). The ground-attack roles walk the CAS
    /// order; everything else walks the CAP order — including AWACS, whose candidates
    /// <see cref="ForRole"/> collapses into one tier, so the order only has to put that tier above
    /// the bottom. Pure, for the self-check.
    /// </summary>
    internal static AirframeTier[] TierOrder(AirRole role)
    {
        return role is AirRole.Strike or AirRole.RotaryCas or AirRole.Arad ? CasTierOrder : CapTierOrder;
    }

    /// <summary>
    /// Whether an airframe may be given an AIR SUPERIORITY task at all — the standing home patrol,
    /// a sortie's escort, a sortie's CAP slot, a retask onto a platoon in contact, or the idle
    /// sweep's posture. A ground-attack specialist may NOT, ever, whatever else is available.
    /// <para>
    /// This is the user's complaint twice over, the second time from a live match: "We should never
    /// have Crickets or Brawlers flying CAP (they're CAS aircraft)", and then "seeing a lot of air
    /// superiority brawlers - SHOULDN'T BE, they're CAS aircraft" (2026-09-14). Preferring the right
    /// tier was not enough, because preference only decides between candidates that are all
    /// admissible: whenever no fighter-tier airframe was owned or launchable — a commander holding
    /// only highway strips, or a wing that had lost its fighters — an A-19 Brawler was still the
    /// best remaining CAP candidate and flew the patrol. A refusal is the only thing that holds.
    /// </para>
    /// <para>
    /// It is applied at the two capability gates rather than at each of the six call sites, so a
    /// strike-tier airframe is invisible to every air-superiority path at once (Reuse rule 4):
    /// <see cref="PassesRoleCapability"/> for the buy and <see cref="FillsAirRole"/> for the claim,
    /// the sortie fill, the retask and the role counts.
    /// </para>
    /// <para>
    /// Rung 1 does not deadlock on this. The ladder's valve — <c>HasAnyCapCandidate</c>, which reads
    /// the same gate — already answers "can this commander field a patrol at all", and a commander
    /// whose strips launch nothing but ground-attack airframes now reports no CAP candidate, so the
    /// strict rung is SKIPPED and the rungs below it proceed. That path is the one the valve was
    /// built for. The strike airframes themselves are not wasted: they fill CAS sorties, and the
    /// idle sweep sends them home rather than parking them on a patrol they cannot fly.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    /// <remarks>This is the TIER half of the refusal only, so it stays answerable from the two role
    /// ratings alone. The prefab half — no plane pilot, no helicopters on patrol — lives in
    /// <see cref="MayFillRole"/>, and both gates apply both.</remarks>
    internal static bool MayFlyAirSuperiority(AircraftDefinition definition)
    {
        AirframeTier tier = ForRole(definition, AirRole.Fighter);
        return tier != AirframeTier.Strike
            && tier != AirframeTier.Excluded
            // Multirole joined the refusal on 2026-09-18 (user decision, from watching a match): the
            // Alkyon AB-4 is "a large, fast, high-altitude large-payload delivery system" and is
            // wanted as ground-attack ONLY. It is the sole multirole ground-attack airframe either
            // roster fields — the EW-25 Medusa is the other multirole entry and is already reserved
            // to the radar station by RadarAirframeMayFill — so this refusal costs the patrol and the
            // escort nothing real. Every airframe actually flying fighter work in the measured match
            // (FS-12 Revoker, FS-20 Vortex, KR-67 Ifrit) is Fighter tier and is untouched.
            && tier != AirframeTier.Multirole;
    }

    /// <summary>
    /// Whether an airframe may be put on the standing patrol — both halves of the refusal in one
    /// question, for the operations paths that hand out a patrol without going through a role gate:
    /// the registration claim's "no sortie wants it" fallback and the idle sweep's posture. One
    /// definition, so a transport, a helicopter, an airframe nothing can task and a ground-attack
    /// specialist are all kept off the patrol by the same rule the buy uses.
    /// </summary>
    internal static bool MayHoldPatrol(AircraftDefinition definition)
    {
        return MayFillRole(definition, AirRole.Fighter) && MayFlyAirSuperiority(definition);
    }

    /// <summary>
    /// Whether an airframe is a candidate for a role at all, before any loadout is considered. Three
    /// refusals, and every path reads them here rather than remembering them for itself:
    /// <list type="bullet">
    /// <item>the structural tier exclusion — a transport for a combat role, or anything else for the
    /// transport role (see <see cref="AirframeTier.Excluded"/>);</item>
    /// <item>no plane pilot on the prefab. <c>TryTaskAiAircraft</c> refuses such an airframe, so a
    /// candidate the commander could never actually task is not a candidate. This is the rule that
    /// the VL-49 Tarantula's roster line already announced and the buy went on ignoring;</item>
    /// <item>a helicopter asked to fly air superiority (team lead, 2026-09-14, after an SAH-46
    /// Chicane turned up on the home patrol). It keeps every ground-attack role — the rotary CAS
    /// pass exists to buy it for exactly those.</item>
    /// </list>
    /// The last two read the airframe's PREFAB, which is why they live here rather than in the pure
    /// <see cref="ForRole"/>: the tier table has to stay answerable from the two role ratings alone.
    /// </summary>
    internal static bool MayFillRole(AircraftDefinition definition, AirRole role)
    {
        if (ForRole(definition, role) == AirframeTier.Excluded)
        {
            return false;
        }

        if (role == AirRole.Transport)
        {
            return CommanderAirCommandService.CanAiFly(definition);
        }

        return CommanderAirCommandService.HasPlanePilot(definition)
            && (role != AirRole.Fighter || !CommanderAirCommandService.IsRotaryAirframe(definition));
    }

    /// <summary>
    /// Which tier an airframe sits in for a role, from the game's own <c>roleIdentity</c> ratings
    /// (design Section 1). Pure: two comparisons on asset data, so a game patch that retunes an
    /// airframe moves it between tiers on its own and a hand-written aircraft list never goes stale.
    /// <para>
    /// Trainers and the last resort go to the bottom whatever they are rated, because their ratings
    /// are not the reason a commander flies them. AWACS candidates all return
    /// <see cref="AirframeTier.Multirole"/>: the radar pod is the whole qualification, so there is
    /// one tier to choose within and the air-to-air rating of a radar aeroplane says nothing about
    /// how well it carries the pod. ARAD keeps the ordinary ratings and walks the CAS order, which
    /// is the design's "ARAD tiers by the CAS order among anti-radiation-capable airframes".
    /// </para>
    /// </summary>
    internal static AirframeTier ForRole(AircraftDefinition definition, AirRole role)
    {
        // The transport question first, and both ways round: a troop carrier is the ONLY candidate
        // for the transport role and is excluded from every combat role. Transports have one tier
        // among themselves, so the transport buy still chooses on price the way it always did.
        bool isTransport = GetAirRole(definition) == AirRole.Transport;
        if (role == AirRole.Transport)
        {
            return isTransport ? AirframeTier.Multirole : AirframeTier.Excluded;
        }

        // A troop carrier is not a combat candidate at any tier (user report, 2026-09-14: "it's
        // spawned a UH-90 transport for CAP for a platoon"). The Ibis is rated A/A 0.27 / A/G 0.90,
        // which is the strike tier by arithmetic, so only an explicit exclusion keeps it out.
        if (isTransport)
        {
            return AirframeTier.Excluded;
        }

        if (IsLastResortAirframe(definition) || IsTrainerAirframe(definition))
        {
            return AirframeTier.LastResort;
        }

        if (role == AirRole.Awacs)
        {
            return AirframeTier.Multirole;
        }

        float antiAir = definition.roleIdentity.antiAir;
        float antiSurface = definition.roleIdentity.antiSurface;

        // An airframe the game rates at nothing in both directions has no specialism to read, so it
        // sits in the middle rather than winning the top tier off a 0 >= 0 comparison.
        if (antiAir <= 0f && antiSurface <= 0f)
        {
            return AirframeTier.Multirole;
        }

        if (antiAir >= antiSurface * FighterRatio)
        {
            return AirframeTier.Fighter;
        }

        return antiSurface >= antiAir * FighterRatio ? AirframeTier.Strike : AirframeTier.Multirole;
    }

    /// <summary>
    /// Tracked hostile aircraft near the objective or the commander's own bases at which a CAP buy
    /// stops shopping for a bargain and buys the best aeroplane it can afford (design Section 2).
    /// Two, because one tracked contact is a scout or a straggler and the commander already keeps a
    /// standing patrol for those; two together is the smallest thing that reads as a raid, and a
    /// raid met by the cheapest airframe in the tier is a raid that gets through. Internal: the
    /// self-check reads it.
    /// </summary>
    internal const int CapThreatAircraft = 2;

    /// <summary>
    /// Effective observed hostile ground units over an objective at which a CAS buy stops shopping
    /// for a bargain (design Section 2). Three, because the sortie sizing already treats one or two
    /// observed vehicles as a picket the cheapest strike airframe can service; three is where the
    /// objective is a defended position and the difference between a Brawler and a Cricket decides
    /// whether the sortie achieves anything. Internal: the self-check reads it.
    /// </summary>
    internal const int CasThreatObserved = 3;

    /// <summary>
    /// Whether the commander buys the best airframe in the tier it can afford rather than the
    /// cheapest one (design Section 2, decision 2: "strong preference for the best airframe when
    /// money is available; the cheapest in the tier when the sky or ground is quiet"). The air-to-air
    /// roles read the tracked-aircraft count, the ground-attack roles the observed-hostile count, so
    /// each role is judged on the threat it actually flies against. Pure, for the self-check.
    /// </summary>
    internal static bool ThreatHigh(AirRole role, int trackedAircraft, int observedHostiles)
    {
        // A transport is never bought "up" for a threat: it is not going to fight, and every
        // transport sits in one tier, so the choice is price alone — the behaviour the transport
        // top-up always had.
        if (role == AirRole.Transport)
        {
            return false;
        }

        return role is AirRole.Strike or AirRole.RotaryCas or AirRole.Arad
            ? observedHostiles >= CasThreatObserved
            : trackedAircraft >= CapThreatAircraft;
    }

    /// <summary>
    /// The highest tier in <paramref name="role"/>'s order that holds at least one airframe able to
    /// launch from a base this commander holds (design Section 2). <paramref name="launchableTiers"/>
    /// is the tier of every candidate that has already passed the role's capability gate AND found
    /// an accepting strip, so a tier with no launchable airframe is skipped rather than waited on.
    /// Null when nothing at all can launch. Pure, for the self-check.
    /// </summary>
    internal static AirframeTier? HighestLaunchableTier(IReadOnlyList<AirframeTier> launchableTiers, AirRole role)
    {
        AirframeTier[] order = TierOrder(role);
        for (int i = 0; i < order.Length; i++)
        {
            for (int j = 0; j < launchableTiers.Count; j++)
            {
                if (launchableTiers[j] == order[i])
                {
                    return order[i];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether one tier is preferred over another for a role — the same order
    /// <see cref="HighestLaunchableTier"/> walks, exposed for the callers that are choosing between
    /// airframes they ALREADY own rather than buying one (the sortie fill and the idle sweep), where
    /// there is no candidate list to take the highest tier of. Internal, for those callers and the
    /// self-check. Pure.
    /// </summary>
    internal static bool TierBeats(AirframeTier tier, AirframeTier other, AirRole role)
    {
        AirframeTier[] order = TierOrder(role);
        int tierRank = int.MaxValue;
        int otherRank = int.MaxValue;
        for (int i = 0; i < order.Length; i++)
        {
            if (order[i] == tier)
            {
                tierRank = i;
            }

            if (order[i] == other)
            {
                otherRank = i;
            }
        }

        return tierRank < otherRank;
    }

    /// <summary>
    /// Which element of a package a buy is filling (design.md, strike-packages_20260915 Section 3).
    /// The tier rule already knows WHICH TIER to shop in; this is what tells it what the aeroplane is
    /// actually for, which is the difference between "the cheapest fighter in the tier" — the rule
    /// that bought the same airframe every review of the 2026-09-15 match — and "the fighter that
    /// gives this element the most of what it needs".
    /// </summary>
    internal enum ElementKind
    {
        /// <summary>No element in particular: today's rule, unchanged. Every ordinary CAS buy, the
        /// radar airframe and the suppression airframe all stay here.</summary>
        Other,

        /// <summary>The standing home patrol: the most air-to-air per unit of money in the tier, so a
        /// commander with money fields more of a cheaper good fighter rather than one dear one.</summary>
        HighCap,

        /// <summary>A sortie's escort: multirole first, because an escort that can also hit the
        /// ground is worth more over a strike than a pure interceptor, and because it is the one
        /// choice that breaks a roster of identical fighters.</summary>
        Escort,

        /// <summary>A strike package's attacking element: the most anti-surface the element's budget
        /// covers.</summary>
        Strike,

        /// <summary>A strike package's bomber element against a base: a bomber-class airframe first,
        /// then anti-surface.</summary>
        Bomber,
    }

    /// <summary>
    /// Whether an airframe is a bomber by the game's own data key — the key the <c>Air roster</c>
    /// line already prints (the Alkyon AB-4 is <c>FastBomber1</c>). Keyed the same way
    /// <see cref="IsTrainerAirframe"/> keys on <c>trainer</c>, so the roster line and the rule can
    /// never disagree, and so a game patch that adds a bomber is picked up without an edit here.
    /// Internal: the self-check builds one.
    /// </summary>
    internal static bool IsBomberAirframe(AircraftDefinition definition)
    {
        return definition.jsonKey != null
            && definition.jsonKey.IndexOf("bomber", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Whether this commander's strips can launch a bomber-class ground-attack airframe at all — the
    /// "is a bomber element possible" read the strike package's sizing makes before it asks for one
    /// (design.md, strike-packages_20260915 Section 2). <see cref="HasRoleCandidate"/>'s own walk with
    /// the bomber test added: one definition, two callers.
    /// </summary>
    internal static bool HasBomberCandidate(FactionHQ hq)
    {
        CommanderEnemyCommanderService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out CommanderState state))
        {
            return false;
        }

        service.RefreshAirCatalog();
        foreach (AircraftDefinition definition in service.airCatalog)
        {
            if (definition != null
                && IsBomberAirframe(definition)
                && PassesRoleCapability(hq, state, definition, AirRole.Strike)
                && FindAcceptingAirbase(hq, definition) != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether one airframe TYPE already makes up too much of a side's sky (design.md,
    /// strike-packages_20260915 Section 3, user decision 3). A type past
    /// <paramref name="cap"/> of the side's airborne airframes is skipped while another launchable,
    /// affordable candidate exists in the same tier — never refused outright, because a wing that can
    /// only fly one aeroplane must still fly it.
    /// <para>A sample of fewer than <see cref="TypeShareSampleFloor"/> airframes never exceeds
    /// anything: the first airframe of a match is 100 percent of the side by arithmetic, and a cap
    /// that fired on it would make the opening buy impossible.</para>
    /// Pure, for the self-check.
    /// </summary>
    internal static bool TypeShareExceeded(int countOfType, int sideTotal, float cap)
    {
        if (sideTotal < TypeShareSampleFloor || countOfType <= 0)
        {
            return false;
        }

        return countOfType > cap * sideTotal;
    }

    /// <summary>
    /// The fewest airborne airframes a side must have before the diversity cap means anything.
    /// Three: with one or two aircraft up, every type is a majority of the sky by arithmetic and the
    /// cap would be refusing the only airframe the wing has. Three is the smallest sample in which
    /// "more than sixty percent" says something about a fleet rather than about a coin toss.
    /// </summary>
    internal const int TypeShareSampleFloor = 3;

    /// <summary>
    /// One airframe the tier rule is choosing between, reduced to the numbers the choice reads. A
    /// plain struct rather than the <c>AircraftDefinition</c> itself so <see cref="SelectInTier"/>
    /// stays pure and the self-check can build a roster without the game loaded;
    /// <see cref="Index"/> carries the caller's own position back so it can recover the definition
    /// and the strip it found.
    /// </summary>
    internal readonly struct AirframeCandidate
    {
        internal AirframeCandidate(
            int index,
            AirframeTier tier,
            float price,
            float rating,
            bool lastResort,
            float antiAir = 0f,
            float antiSurface = 0f,
            bool bomberClass = false,
            bool shareExceeded = false)
        {
            Index = index;
            Tier = tier;
            Price = price;
            Rating = rating;
            LastResort = lastResort;
            AntiAir = antiAir;
            AntiSurface = antiSurface;
            BomberClass = bomberClass;
            ShareExceeded = shareExceeded;
        }

        /// <summary>The game's own air-to-air rating, for the element preferences that read it
        /// directly rather than through <see cref="Rating"/> (which is the rating for the ROLE being
        /// bought, and so is the wrong number for an escort chosen on value for money).</summary>
        internal float AntiAir { get; }

        /// <summary>The game's own ground-attack rating, for the strike and bomber elements.</summary>
        internal float AntiSurface { get; }

        /// <summary>Whether the game's data key names this a bomber
        /// (<see cref="IsBomberAirframe"/>).</summary>
        internal bool BomberClass { get; }

        /// <summary>This type is already past the diversity cap in the side's sky, so it is skipped
        /// while anything else in the tier is available (<see cref="TypeShareExceeded"/>).</summary>
        internal bool ShareExceeded { get; }

        /// <summary>The caller's index for this candidate.</summary>
        internal int Index { get; }

        /// <summary>Where <see cref="ForRole"/> put it for the role being bought.</summary>
        internal AirframeTier Tier { get; }

        /// <summary>What the faction is charged for it.</summary>
        internal float Price { get; }

        /// <summary>How good it is at this role — the game's own rating for the air-to-air and
        /// ground-attack roles, the loadout score for AWACS and ARAD.</summary>
        internal float Rating { get; }

        /// <summary>Whether <see cref="IsLastResortAirframe"/> holds for it.</summary>
        internal bool LastResort { get; }
    }

    /// <summary>
    /// The airframe to buy from inside one tier (design Section 2): the best-rated one whose price
    /// fits <paramref name="allocation"/> when the threat is high, the cheapest one that fits when
    /// it is quiet. Returns the winner's <see cref="AirframeCandidate.Index"/>, or -1 when nothing
    /// in the tier fits — which is the WAIT, not a licence to drop a tier: the caller reports the
    /// shortfall and buys nothing this review (decision 3, "never drop a tier for price").
    /// <para>
    /// A last-resort airframe is ranked below every ordinary candidate in the same tier whatever the
    /// threat and whatever the price. That keeps the 2026-09-13 LAST RESORT rule as the single
    /// definition it already was rather than forking it: the design puts the CI-22 Cricket and the
    /// T/A-30 Compass in the same bottom tier, and cheapest-when-quiet would otherwise buy the
    /// Cricket at 12 over the Compass at 22 — the opposite of the design's own "the Cricket flies
    /// only when the Compass cannot".
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    /// <param name="elementKind">Which element of a package this buy is filling (design.md,
    /// strike-packages_20260915 Section 3). <see cref="ElementKind.Other"/> — every caller that had
    /// no element to name before — keeps <see cref="BetterInTier"/> exactly as it was.</param>
    internal static int SelectInTier(
        IReadOnlyList<AirframeCandidate> candidates,
        AirframeTier tier,
        bool threatHigh,
        float allocation,
        ElementKind elementKind = ElementKind.Other)
    {
        // Two passes: the first skips any type already past the diversity cap, the second lets it
        // back in. A cap that could leave the wing with nothing to buy is not a cap, it is a
        // grounding (design Section 3: "skipped when another launchable, affordable type exists").
        for (int pass = 0; pass < 2; pass++)
        {
            bool skipOverrepresented = pass == 0;
            int chosen = -1;
            AirframeCandidate best = default;
            for (int i = 0; i < candidates.Count; i++)
            {
                AirframeCandidate candidate = candidates[i];
                if (candidate.Tier != tier
                    || candidate.Price > allocation
                    || (skipOverrepresented && candidate.ShareExceeded))
                {
                    continue;
                }

                if (chosen < 0 || PreferForElement(elementKind, candidate, best, threatHigh))
                {
                    chosen = candidate.Index;
                    best = candidate;
                }
            }

            if (chosen >= 0)
            {
                return chosen;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether one candidate beats another FOR THE ELEMENT being filled (design.md,
    /// strike-packages_20260915 Section 3). The last-resort airframe is still ranked below every
    /// ordinary candidate first, whatever the element: that rule is older than this one and is not
    /// forked here.
    /// <list type="bullet">
    /// <item>HIGH CAP — the most air-to-air per unit of money, so a commander with a full fund buys
    /// the fighter that gives the patrol the most for it rather than the dearest one on the roster.</item>
    /// <item>ESCORT — the tier choice has already preferred multirole; inside the tier this is the
    /// ordinary rule, so an escort is still cheap when the sky is quiet.</item>
    /// <item>STRIKE — the most ground attack the allocation covers, always: a package flown into
    /// defended ground once is not the place to save money.</item>
    /// <item>BOMBER — a bomber-class airframe ahead of anything else, then ground attack.</item>
    /// <item>OTHER — <see cref="BetterInTier"/>, unchanged.</item>
    /// </list>
    /// Pure, for the self-check.
    /// </summary>
    /// <summary>Whether an element kind is flown by fixed wing only, pure: the deliberate strike
    /// and the bomber elements cross tens of kilometres to a form-up point on a 180 s clock, which a
    /// helicopter cannot make; CAP and platoon CAS keep their own rotary rules.</summary>
    internal static bool RotaryExcludedForElement(ElementKind kind)
    {
        return kind == ElementKind.Strike || kind == ElementKind.Bomber;
    }

    internal static bool PreferForElement(
        ElementKind kind, AirframeCandidate candidate, AirframeCandidate best, bool threatHigh)
    {
        if (candidate.LastResort != best.LastResort)
        {
            return !candidate.LastResort;
        }

        switch (kind)
        {
            case ElementKind.HighCap:
            {
                float candidateValue = candidate.AntiAir / Mathf.Max(1f, candidate.Price);
                float bestValue = best.AntiAir / Mathf.Max(1f, best.Price);
                if (candidateValue != bestValue)
                {
                    return candidateValue > bestValue;
                }

                return candidate.Price < best.Price;
            }

            case ElementKind.Strike:
                if (candidate.AntiSurface != best.AntiSurface)
                {
                    return candidate.AntiSurface > best.AntiSurface;
                }

                return candidate.Price < best.Price;

            case ElementKind.Bomber:
                if (candidate.BomberClass != best.BomberClass)
                {
                    return candidate.BomberClass;
                }

                if (candidate.AntiSurface != best.AntiSurface)
                {
                    return candidate.AntiSurface > best.AntiSurface;
                }

                return candidate.Price < best.Price;

            default:
                return BetterInTier(candidate, best, threatHigh);
        }
    }

    /// <summary>
    /// The tier an ESCORT buy shops in (design.md, strike-packages_20260915 Section 3): the multirole
    /// tier when a multirole airframe can actually launch and is affordable, and otherwise whatever
    /// the ordinary rule chose. Every other element keeps
    /// <see cref="HighestLaunchableTier"/>'s answer untouched.
    /// <para>This is the single choice that breaks a wing of identical interceptors: the tier rule's
    /// own order puts the Fighter tier first for every air-to-air buy, so a roster with one
    /// fighter-tier airframe on it flew that one airframe and nothing else, match after match.</para>
    /// Pure, for the self-check.
    /// </summary>
    internal static AirframeTier? EscortTier(
        ElementKind kind, bool multiroleLaunchable, bool multiroleAffordable, AirframeTier? ordinary)
    {
        return PreferTier(
            kind == ElementKind.Escort,
            multiroleLaunchable,
            multiroleAffordable,
            AirframeTier.Multirole,
            ordinary);
    }

    /// <summary>
    /// A deliberate override of the tier the ordinary order chose: when <paramref name="prefer"/>
    /// holds AND the preferred tier holds an airframe that can actually launch from a strip this
    /// commander holds AND the element can pay for it, the buy shops <paramref name="tier"/> instead
    /// of <paramref name="ordinary"/>. Otherwise nothing changes at all.
    /// <para>
    /// Generalised out of <see cref="EscortTier"/> on 2026-09-16 (Reuse rule 5), when the easy-job
    /// rule became the second thing wanting the same shape. One definition, two callers: an escort
    /// preferring the multirole tier to break a wing of identical interceptors, and an easy job
    /// preferring the bottom tier so the cheap aeroplane takes work it is good enough for.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static AirframeTier? PreferTier(
        bool prefer, bool launchable, bool affordable, AirframeTier tier, AirframeTier? ordinary)
    {
        if (!prefer || !launchable || !affordable)
        {
            return ordinary;
        }

        return tier;
    }

    /// <summary>Whether one candidate inside a tier beats another: never-last-resort first, then
    /// the better rating when the threat is high or the lower price when it is quiet, with the
    /// cheaper of two equal ratings winning so the choice is never arbitrary. Pure, for the
    /// self-check.</summary>
    internal static bool BetterInTier(AirframeCandidate candidate, AirframeCandidate best, bool threatHigh)
    {
        if (candidate.LastResort != best.LastResort)
        {
            return !candidate.LastResort;
        }

        if (!threatHigh)
        {
            return candidate.Price < best.Price;
        }

        return candidate.Rating > best.Rating
            || (candidate.Rating == best.Rating && candidate.Price < best.Price);
    }

    /// <summary>The plain-words half of a launch line's tier note — <c>best affordable</c> when the
    /// threat pushed the buy up the tier, <c>cheapest</c> when it was quiet (design Section 4). One
    /// definition, every launch site.</summary>
    private static string TierChoiceNote(
        FactionHQ hq,
        CommanderState state,
        AircraftDefinition choice,
        AirRole role,
        bool threatHigh,
        int threatCount,
        string threatWord,
        string? attritionNote = null)
    {
        // The tier text comes from the SAME describer the roster line uses (Reuse rule 4), so a
        // launch line can never claim a tier for an airframe that has none: a transport bought by
        // the supply path once printed "Multirole tier, best affordable" while its roster line, two
        // hundred lines above, read "excluded (transport)".
        string tier = DescribeTier(hq, state, choice, role);
        // A buy pushed up by attrition says so in place of the threat count, which may well be
        // zero — "best affordable — 0 in the sky" would read as a broken threat rule.
        if (attritionNote != null)
        {
            return $"{tier}, best affordable — {attritionNote}";
        }

        return threatHigh
            ? $"{tier}, best affordable — {threatCount} {threatWord}"
            : $"{tier}, cheapest — quiet";
    }

    /// <summary>
    /// Whether a last-resort airframe may be bought at all right now (user decision 2026-09-13;
    /// bug found and fixed 2026-09-14): only when the roster holds NO ordinary airframe that can
    /// fill the role from a strip this commander holds, <b>at any price</b>. An ordinary candidate
    /// the review's remaining slice cannot afford is still a candidate — the commander saves for it
    /// and buys nothing this review.
    /// <para>
    /// The bug this encodes: both buyers filtered candidates by <c>value &gt; budget</c> before the
    /// last-resort preference ever ran, so the rule degraded from "nothing else CAN fill the role"
    /// to "nothing else is affordable this instant". Rung 1 buys several fighters out of one pot,
    /// bounded only by the shortfall and the budget, so the second buy of a review
    /// routinely had a slice that covered the 12-value Cricket and not the 22-value Compass:
    /// <c>LogOutput.log</c> 2026-09-14 shows exactly that pair, "launched a T/A-30 Compass … for 22
    /// (home CAP)" followed by "launched a CI-22 Cricket … for 12 (home CAP)" with the rung
    /// reporting "spent CAP 33 … saved 10".
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static bool LastResortAllowed(bool ordinaryCandidateAtAnyPrice)
    {
        return !ordinaryCandidateAtAnyPrice;
    }

    /// <summary>
    /// Hostile ground units observed over an objective at which its work stops being EASY (user
    /// instruction 2026-09-16, "cheap aircraft take the easy jobs"). Two, because two is the top of
    /// the close-air-support ladder's own first step - one airframe for one or two observed vehicles
    /// (<c>CommanderOperationsService.CasWanted</c>) - which is the mod's existing answer to "this is
    /// a picket, not a position". At three the ladder starts asking for a second airframe, and an
    /// objective worth two airframes is worth a proper one. Internal: the self-check pins it to that
    /// step, so the two numbers cannot drift apart.
    /// </summary>
    internal const int CheapAirframeMaxObserved = 2;

    /// <summary>
    /// Whether a cheap bottom-tier airframe may be considered for this buy AT ALL, before the job
    /// itself is looked at. The shared veto behind both halves of the 2026-09-16 instruction - the
    /// easy-job substitution (<see cref="AirJobIsEasy"/>) and the padding
    /// (<see cref="MayPadWithCheapAirframes"/>) - so the two can never disagree about when a cheap
    /// aeroplane is out of the question. Four refusals:
    /// <list type="bullet">
    /// <item>NO SORTIE ASKED. The standing buys - the threat fighter, the home patrol's growth -
    /// have no objective to judge, and the home patrol is the side's last line of air defence.</item>
    /// <item>SUPPRESSION OR RADAR. No cheap airframe on either faction's roster carries an
    /// anti-radiation missile or the radar pod, so there is nothing to substitute.</item>
    /// <item>THE SIDE IS BLEEDING. Losing more than a third of what it launches means buying the
    /// best it can afford (<see cref="AttritionBleeding"/>), and that rule wins outright: attrition
    /// is a measured fact about aircraft that have actually died, where "easy" is an estimate from
    /// what the commander can currently see - and a bleeding side is precisely the one whose picture
    /// is wrong. It also closes the loop this change opens: more cheap aircraft mean more losses,
    /// more losses set the bleeding flag, and the flag switches the cheap buying off until it
    /// eases.</item>
    /// <item>A SUPPRESSION SORTIE IS WAITING FOR ITS AEROPLANE. The anti-radiation airframe is the
    /// only thing on the roster that can clear an air-defence belt; a sortie behind a belt now waits
    /// for the sweep instead of flying through it; and this review's air fund is shared. A cheap
    /// airframe bought out of it while the sweep is unbought is a belt that stays up and a package
    /// that never goes in.</item>
    /// </list>
    /// Pure, for the self-check.
    /// </summary>
    internal static bool CheapAirframesAdmissible(
        bool forSortie, AirRole role, bool bleeding, bool suppressionSortieWaiting)
    {
        if (!forSortie || bleeding || suppressionSortieWaiting)
        {
            return false;
        }

        return role is AirRole.Fighter or AirRole.Strike or AirRole.RotaryCas;
    }

    /// <summary>
    /// Whether this sortie's work is EASY - easy enough that the cheap bottom-tier airframe should
    /// take it rather than the aeroplane built for the job (user instruction 2026-09-16, "cheap
    /// aircraft take the easy jobs"). The T/A-30 Compass is rated 0.64 against the ground where the
    /// FS-12 Revoker is rated 0.46, at 22 against 65; over a picket with nothing in the sky above it,
    /// the difference between the two is money rather than outcome.
    /// <para>
    /// Built only from what the sortie is ALREADY sized from, so there is no new measurement to go
    /// stale: hostile aircraft tracked in its ring, hostile ground units observed over it,
    /// air-defence vehicles observed near it, and whether its objective is in contact. ANY hostile
    /// aircraft tracked, ANY air-defence vehicle observed, contact of any kind, or more than
    /// <see cref="CheapAirframeMaxObserved"/> observed on the ground, and the sortie draws a proper
    /// aircraft.
    /// </para>
    /// <para>
    /// Distance from the enemy is deliberately NOT read. The only per-sortie distance the mod keeps,
    /// <c>CommanderAirSortie.EnemyDistanceMeters</c>, is set for a pre-emptive platoon sortie and left
    /// at -1 for every other kind, so a rule that every sortie passes through cannot honestly ask it.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static bool AirJobIsEasy(
        bool enabled,
        bool admissible,
        int trackedHostileAir,
        int observedHostiles,
        int observedAirDefence,
        bool objectiveInContact)
    {
        if (!enabled || !admissible || objectiveInContact)
        {
            return false;
        }

        return trackedHostileAir <= 0
            && observedAirDefence <= 0
            && observedHostiles <= CheapAirframeMaxObserved;
    }

    /// <summary>
    /// Whether this buy may fill the slots its allocation cannot cover at the proper tier with the
    /// cheapest thing that can do the job (user instruction 2026-09-16: cheap aircraft "pad out
    /// sorties that cannot afford full strength"). The same veto the easy-job rule stands behind,
    /// plus its own setting.
    /// <para>
    /// How hard the job is is deliberately NOT asked here. Padding serves a hard sortie as much as an
    /// easy one: a sortie flying at two-thirds strength into a defended objective is exactly the one
    /// that should not go in short-handed, and what happens today instead is that it buys nothing at
    /// all and flies with the slot empty.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static bool MayPadWithCheapAirframes(bool enabled, bool admissible)
    {
        return enabled && admissible;
    }

    /// <summary>
    /// The candidate a padding slot is filled with: the cheapest airframe the leftover allocation
    /// covers, across every tier, because padding is explicitly "the cheapest thing that can fill the
    /// role" rather than a tier decision. Returns the winner's
    /// <see cref="AirframeCandidate.Index"/>, or -1 when the leftover covers nothing.
    /// <para>
    /// It keeps the two rules that are not about price. A type already past the diversity cap is
    /// skipped while anything else is affordable - the same two passes <see cref="SelectInTier"/>
    /// makes, for the same reason: a cap that can leave the wing with nothing to buy is a grounding
    /// rather than a cap. And a last-resort airframe is ranked below every ordinary candidate whatever
    /// it costs, which is the 2026-09-13 rule this must not fork. Two candidates at one price are
    /// separated by the better rating, so the choice is never arbitrary.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static int CheapestPaddingIndex(IReadOnlyList<AirframeCandidate> candidates, float allocation)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool skipOverrepresented = pass == 0;
            int chosen = -1;
            AirframeCandidate best = default;
            for (int i = 0; i < candidates.Count; i++)
            {
                AirframeCandidate candidate = candidates[i];
                if (candidate.Price > allocation
                    || candidate.Tier == AirframeTier.Excluded
                    || (skipOverrepresented && candidate.ShareExceeded))
                {
                    continue;
                }

                if (chosen < 0 || CheaperForPadding(candidate, best))
                {
                    chosen = candidate.Index;
                    best = candidate;
                }
            }

            if (chosen >= 0)
            {
                return chosen;
            }
        }

        return -1;
    }

    /// <summary>Whether one candidate is the better PADDING buy than another: never-last-resort
    /// first, then the lower price, then the better rating for the role. Pure, for the
    /// self-check.</summary>
    internal static bool CheaperForPadding(AirframeCandidate candidate, AirframeCandidate best)
    {
        if (candidate.LastResort != best.LastResort)
        {
            return !candidate.LastResort;
        }

        if (candidate.Price != best.Price)
        {
            return candidate.Price < best.Price;
        }

        return candidate.Rating > best.Rating;
    }

    /// <summary>
    /// The aircraft candidate list — the SAME list the player's AIR window builds its options from
    /// (<see cref="CommanderAirCommandService.CollectAircraftDefinitions"/>, one definition, two
    /// callers), resolved once per mission because the encyclopedia and resource scans behind it are
    /// expensive. The old buy walked <c>hq.AircraftSupply</c> instead — the faction's ISSUED list —
    /// so an airframe the faction was never issued was invisible to every commander however much
    /// its own hangars would accept it: the first ladder build sat on a 400+ fund all match, logged
    /// "its strips accept no AI-flyable Fighter airframe at all", and bought no CAP fighter, while
    /// the player launched Compasses and VT-7 Vagrants with Scythes from the same highway strips by
    /// hand (user report, 2026-09-14). The strips decide what exists, not the issue table.
    /// </summary>
    private readonly List<AircraftDefinition> airCatalog = new();
    private bool airCatalogResolved;

    /// <summary>Scratch for one buy's tier walk, reused across reviews rather than allocated each
    /// time (the ladder's <c>ladderWeights</c> pattern): the reduced candidates the pure rule reads,
    /// and the definition, accepting strip and tier each one came from so the winner's index can be
    /// mapped back. Cleared at the top of every pass.</summary>
    /// <summary>How many airframes of each type this commander's faction has in the world, tallied
    /// once per buy by <c>TallyAirborneByType</c> for the diversity cap. A field rather than a local
    /// so the tally costs no allocation per review, the same reason the tier scratch lists below are
    /// fields.</summary>
    private readonly Dictionary<AircraftDefinition, int> airborneByType = new();

    private readonly List<AirframeCandidate> tierCandidates = new();
    private readonly List<AircraftDefinition> tierDefinitions = new();
    private readonly List<Airbase> tierAirbases = new();
    private readonly List<AirframeTier> tierTiers = new();
}
