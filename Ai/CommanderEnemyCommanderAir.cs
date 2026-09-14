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

    /// <summary>Most airframes one buy review may launch (user decision 2026-09-13). The old
    /// one-airframe-per-review throttle let the wing field only thirty aircraft a match even with
    /// the fund full and five sorties short — a wing that waits a match to form is no wing. Three is
    /// a CAP fighter, a CAS airframe and a wingman in one review without emptying the grant in a
    /// single flush; the ceiling still bounds the rest. Internal: the self-check reads it.</summary>
    internal const int MaxAirBuysPerReview = 3;

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

    /// <summary>Whether the buy loop may launch another airframe this review — the
    /// buys-per-review bound. Pure, for the self-check.</summary>
    internal static bool AirBuyContinues(int boughtThisReview)
    {
        return boughtThisReview < MaxAirBuysPerReview;
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

        /// <summary>Neither rating dominates the other: KR-67 Ifrit.</summary>
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
        return tier != AirframeTier.Strike && tier != AirframeTier.Excluded;
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
    /// One airframe the tier rule is choosing between, reduced to the four numbers the choice reads.
    /// A plain struct rather than the <c>AircraftDefinition</c> itself so
    /// <see cref="SelectInTier"/> stays pure and the self-check can build a roster without the game
    /// loaded; <see cref="Index"/> carries the caller's own position back so it can recover the
    /// definition and the strip it found.
    /// </summary>
    internal readonly struct AirframeCandidate
    {
        internal AirframeCandidate(int index, AirframeTier tier, float price, float rating, bool lastResort)
        {
            Index = index;
            Tier = tier;
            Price = price;
            Rating = rating;
            LastResort = lastResort;
        }

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
    internal static int SelectInTier(
        IReadOnlyList<AirframeCandidate> candidates, AirframeTier tier, bool threatHigh, float allocation)
    {
        int chosen = -1;
        AirframeCandidate best = default;
        for (int i = 0; i < candidates.Count; i++)
        {
            AirframeCandidate candidate = candidates[i];
            if (candidate.Tier != tier || candidate.Price > allocation)
            {
                continue;
            }

            if (chosen < 0 || BetterInTier(candidate, best, threatHigh))
            {
                chosen = candidate.Index;
                best = candidate;
            }
        }

        return chosen;
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
        string threatWord)
    {
        // The tier text comes from the SAME describer the roster line uses (Reuse rule 4), so a
        // launch line can never claim a tier for an airframe that has none: a transport bought by
        // the supply path once printed "Multirole tier, best affordable" while its roster line, two
        // hundred lines above, read "excluded (transport)".
        string tier = DescribeTier(hq, state, choice, role);
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
    /// to "nothing else is affordable this instant". Rung 1 buys up to
    /// <see cref="MaxAirBuysPerReview"/> fighters out of one pot, so the second buy of a review
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
    private readonly List<AirframeCandidate> tierCandidates = new();
    private readonly List<AircraftDefinition> tierDefinitions = new();
    private readonly List<Airbase> tierAirbases = new();
    private readonly List<AirframeTier> tierTiers = new();

    private void RefreshAirCatalog()
    {
        if (airCatalogResolved)
        {
            return;
        }

        CommanderAirCommandService.CollectAircraftDefinitions(airCatalog);
        airCatalogResolved = true;
    }

    /// <summary>
    /// The live ceiling for this commander's air fund: the dearest airframe any open demand wants,
    /// floored at the dearest fighter its strips accept (see <see cref="AirFundCeiling(float, float)"/>).
    /// Walks the same catalog, the same role gate and the same accepting-strip pair the buy itself
    /// walks, so the fund can never save for an airframe the buy would refuse (Reuse rule 4).
    /// </summary>
    private float AirFundCeiling(FactionHQ hq, CommanderState state)
    {
        RefreshAirCatalog();
        CommanderOperationsService.ReadOpenAirDemandRoles(
            hq, out bool wantsCap, out bool wantsCas, out bool wantsRotaryCas, out bool wantsAwacs, out bool wantsArad);

        // The fighter price is both the CAP demand's answer and the floor, so it is read once.
        float dearestFighter = DearestLaunchableValue(hq, state, AirRole.Fighter);
        float wanted = wantsCap ? dearestFighter : 0f;
        if (wantsCas)
        {
            wanted = Mathf.Max(wanted, DearestLaunchableValue(hq, state, AirRole.Strike));
        }

        if (wantsRotaryCas)
        {
            wanted = Mathf.Max(wanted, DearestLaunchableValue(hq, state, AirRole.RotaryCas));
        }

        if (wantsAwacs)
        {
            wanted = Mathf.Max(wanted, DearestLaunchableValue(hq, state, AirRole.Awacs));
        }

        if (wantsArad)
        {
            wanted = Mathf.Max(wanted, DearestLaunchableValue(hq, state, AirRole.Arad));
        }

        return AirFundCeiling(wanted, dearestFighter);
    }

    /// <summary>The dearest airframe that can fill <paramref name="role"/> from a strip this
    /// commander holds, or zero when none can. The last-resort airframe is excluded for the same
    /// reason the buy hides it: the commander never saves toward a Cricket.</summary>
    private float DearestLaunchableValue(FactionHQ hq, CommanderState state, AirRole role)
    {
        float best = 0f;
        foreach (AircraftDefinition definition in airCatalog)
        {
            if (definition == null
                || definition.value <= best
                || IsLastResortAirframe(definition)
                || !PassesRoleCapability(hq, state, definition, role)
                || FindAcceptingAirbase(hq, definition) == null)
            {
                continue;
            }

            best = definition.value;
        }

        return best;
    }

    /// <summary>
    /// Whether one of the commander's held airbases will launch <paramref name="definition"/> — the
    /// AIR window's own pair (<see cref="CommanderAirCommandService.IsCompatibleAirbase"/> plus a
    /// free compatible hangar), so the commander's candidate gate and the window's launch gate can
    /// never disagree about what a strip accepts. Returns the first accepting base, or null.
    /// </summary>
    /// <param name="near">When given with <paramref name="withinMeters"/>, only bases inside that
    /// range of this position are considered, and the nearest one wins — the rotary CAS gate
    /// (design.md, smarter-air-wing_20260914 Section 2: an attack helicopter launches from a pad or
    /// strip within <c>RotaryCasRangeMeters</c> of the objective).</param>
    private static Airbase? FindAcceptingAirbase(
        FactionHQ hq, AircraftDefinition definition, GlobalPosition? near = null, float withinMeters = 0f)
    {
        Airbase? best = null;
        float bestDistance = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null
                || airbase.disabled
                || airbase.center == null
                || !CommanderAirCommandService.IsCompatibleAirbase(airbase, hq, definition)
                || !airbase.CanSpawnAircraft(definition))
            {
                continue;
            }

            if (near == null || withinMeters <= 0f)
            {
                return airbase;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                airbase.center.GlobalPosition().AsVector3(), near.Value.AsVector3());
            if (distance <= withinMeters && distance < bestDistance)
            {
                best = airbase;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// The AIR window's AIR SUPERIORITY score of the best air-to-air loadout this airframe's own
    /// picker can build for it — 0 when it has no A/A-capable option at all. Memoized per mission.
    /// This is THE capability test (user decision 2026-09-14): a CAP or escort candidate is an
    /// airframe whose loadout can fight air, not one whose role data says "Fighter" — the identity
    /// test excluded a Compass carrying Scythes, which is exactly the CAP a highway-strip commander
    /// can actually field.
    /// </summary>
    private static float CapLoadoutScore(FactionHQ hq, CommanderState state, AircraftDefinition definition)
    {
        if (state.CapScores.TryGetValue(definition, out float cached))
        {
            return cached;
        }

        float score = CommanderAirCommandService.TryBuildRoleLoadout(
            definition, hq, CommanderAirCommandService.AirCommandMode.AirGuard,
            preferArhMissiles: true, out _, out float built)
            ? built
            : 0f;
        state.CapScores[definition] = score;
        return score;
    }

    /// <summary>The same read on the CAS scorer — 0 when the airframe has no ground-attack option.
    /// Memoized per mission.</summary>
    private static float CasLoadoutScore(FactionHQ hq, CommanderState state, AircraftDefinition definition)
    {
        if (state.CasScores.TryGetValue(definition, out float cached))
        {
            return cached;
        }

        float score = CommanderAirCommandService.TryBuildRoleLoadout(
            definition, hq, CommanderAirCommandService.AirCommandMode.Cas,
            preferArhMissiles: false, out _, out float built)
            ? built
            : 0f;
        state.CasScores[definition] = score;
        return score;
    }

    /// <summary>The same read on the ARAD scorer — 0 when the airframe can mount no anti-radiation
    /// weapon at all. Memoized per mission (design.md, smarter-air-wing_20260914 Section 5).</summary>
    private static float AradLoadoutScore(FactionHQ hq, CommanderState state, AircraftDefinition definition)
    {
        if (state.AradScores.TryGetValue(definition, out float cached))
        {
            return cached;
        }

        float score = CommanderAirCommandService.TryBuildRoleLoadout(
            definition, hq, CommanderAirCommandService.AirCommandMode.Arad,
            preferArhMissiles: false, out _, out float built)
            ? built
            : 0f;
        state.AradScores[definition] = score;
        return score;
    }

    /// <summary>Whether the commander's own builder can give this airframe the game's radar pod —
    /// the AWACS candidate test (Section 4). Memoized per mission.</summary>
    private static bool HasRadarLoadout(FactionHQ hq, CommanderState state, AircraftDefinition definition)
    {
        if (state.AwacsCapable.TryGetValue(definition, out bool cached))
        {
            return cached;
        }

        bool capable = CommanderAirCommandService.TryBuildRoleLoadout(
                definition, hq, CommanderAirCommandService.AirCommandMode.AwacsJammer,
                preferArhMissiles: false, out Loadout loadout, out _)
            && CommanderAirCommandService.LoadoutHasRadarSystem(loadout);
        state.AwacsCapable[definition] = capable;
        return capable;
    }

    /// <summary>Whether this airframe can fight air at all — the best A/A loadout its picker can
    /// build scores above zero. Internal (one-word widening, Reuse rule 4): the operations claim and
    /// the sortie fill bind by the SAME capability the buy bought on, or a CAP-bought Compass would
    /// bind to the first CAS sortie instead of the CAP it was bought for.</summary>
    internal static bool IsAntiAirCapable(FactionHQ hq, AircraftDefinition definition)
    {
        CommanderEnemyCommanderService? service = Instance;
        if (service != null && service.states.TryGetValue(hq, out CommanderState state))
        {
            return CapLoadoutScore(hq, state, definition) > 0f;
        }

        return CommanderAirCommandService.TryBuildRoleLoadout(
            definition, hq, CommanderAirCommandService.AirCommandMode.AirGuard,
            preferArhMissiles: true, out _, out float score) && score > 0f;
    }

    /// <summary>
    /// Whether an airframe can fill <paramref name="role"/> at all — capability, not identity.
    /// Internal (one-word widening, Reuse rule 4): the wing's own count reads it, and so do the
    /// operations claim and the sortie fill, so an airframe is only ever bound to a sortie it can
    /// actually fly — a radar airframe to the AWACS, an anti-radiation one to an ARAD sortie.
    /// </summary>
    internal static bool FillsAirRole(FactionHQ hq, AircraftDefinition definition, AirRole role)
    {
        // The structural exclusion, ahead of every capability test: a transport, an airframe with no
        // plane pilot, or a helicopter asked to fly air superiority is not a candidate whatever its
        // loadout can carry. This is the gate the owned-airframe binding, the registration claim,
        // the sortie fill, the retask and the role counts all read (user report, 2026-09-14: a
        // UH-90 Ibis bound to a platoon's CAP).
        if (!MayFillRole(definition, role))
        {
            return false;
        }

        CommanderEnemyCommanderService? service = Instance;
        return role switch
        {
            AirRole.Transport => GetAirRole(definition) == AirRole.Transport,
            // The same air-superiority refusal as the buy, so the claim, the sortie fill, the
            // retask and the role counts all agree with it (see MayFlyAirSuperiority).
            AirRole.Fighter => IsAntiAirCapable(hq, definition) && MayFlyAirSuperiority(definition),
            AirRole.Awacs => service != null
                && service.states.TryGetValue(hq, out CommanderState awacsState)
                && HasRadarLoadout(hq, awacsState, definition),
            AirRole.Arad => service != null
                && service.states.TryGetValue(hq, out CommanderState aradState)
                && AradLoadoutScore(hq, aradState, definition) > 0f,
            AirRole.RotaryCas => IsAntiSurfaceCapable(hq, definition)
                && CommanderAirCommandService.IsRotaryAirframe(definition),
            _ => IsAntiSurfaceCapable(hq, definition),
        };
    }

    /// <summary>
    /// Whether any airframe on the roster can fill <paramref name="role"/> from a strip this
    /// commander holds — the "is this sortie kind possible at all" test the AWACS demand reads
    /// before it opens a sortie nothing can ever fly (design.md, smarter-air-wing_20260914
    /// Section 4). The deadlock valve's shape (<see cref="HasAnyCapCandidate"/>) widened to any
    /// role: one definition, two callers.
    /// </summary>
    internal static bool HasRoleCandidate(FactionHQ hq, AirRole role)
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
                && PassesRoleCapability(hq, state, definition, role)
                && FindAcceptingAirbase(hq, definition) != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether this airframe can attack ground at all — the CAS-scored twin of
    /// <see cref="IsAntiAirCapable"/>, for the CAS side of the claim and the fill.</summary>
    internal static bool IsAntiSurfaceCapable(FactionHQ hq, AircraftDefinition definition)
    {
        CommanderEnemyCommanderService? service = Instance;
        if (service != null && service.states.TryGetValue(hq, out CommanderState state))
        {
            return CasLoadoutScore(hq, state, definition) > 0f;
        }

        return CommanderAirCommandService.TryBuildRoleLoadout(
            definition, hq, CommanderAirCommandService.AirCommandMode.Cas,
            preferArhMissiles: false, out _, out float score) && score > 0f;
    }

    /// <summary>
    /// Whether <paramref name="definition"/> is a candidate for <paramref name="role"/> — capability,
    /// not identity (user decision 2026-09-14). Combat roles additionally require a plane pilot:
    /// every combat buy is tasked through Air Command, whose <c>TryTaskAiAircraft</c> already
    /// refuses anything without one, so a rotary or VTOL combat buy would be money spent on an
    /// airframe nothing can fly for us. Transports keep the identity test (carrying capacity IS
    /// their capability) and the looser <see cref="CommanderAirCommandService.CanAiFly"/> gate,
    /// because the Basegame's own helo state flies them.
    /// </summary>
    private static bool PassesRoleCapability(FactionHQ hq, CommanderState state, AircraftDefinition definition, AirRole role)
    {
        // The shared candidate gate — the structural exclusion, the plane-pilot test and the
        // no-helicopters-on-patrol rule — so the buy and the binding can never disagree about what
        // is a candidate. The transport role ends here: carrying capacity IS its capability.
        if (!MayFillRole(definition, role))
        {
            return false;
        }

        if (role == AirRole.Transport)
        {
            return true;
        }

        // The radar aeroplane is the AWACS and nothing else (design.md,
        // airframe-selection_20260914 Section 1: "radar/EW special-system airframes are excluded
        // from both"). The EW-25 Medusa reads as a Fighter on its role identity and can hang an
        // air-to-air missile, so without this it would be a CAP candidate at 145 — spending the
        // patrol's whole slice on the one airframe the wing needs standing off behind the front.
        if (role is AirRole.Fighter or AirRole.Strike or AirRole.RotaryCas
            && HasRadarLoadout(hq, state, definition))
        {
            return false;
        }

        return role switch
        {
            // The air-superiority refusal, applied here so the buy can never pick a
            // ground-attack specialist for a patrol or an escort (see MayFlyAirSuperiority).
            AirRole.Fighter => CapLoadoutScore(hq, state, definition) > 0f && MayFlyAirSuperiority(definition),
            AirRole.Awacs => HasRadarLoadout(hq, state, definition),
            AirRole.Arad => AradLoadoutScore(hq, state, definition) > 0f,
            // The rotary CAS pass is the Strike pass with the flight model added (design SS2): the
            // airframe has to attack ground AND fly like a helicopter.
            AirRole.RotaryCas => CasLoadoutScore(hq, state, definition) > 0f
                && CommanderAirCommandService.IsRotaryAirframe(definition),
            _ => CasLoadoutScore(hq, state, definition) > 0f,
        };
    }

    /// <summary>
    /// The loadout a combat buy launches with — the AIR window's own scorer, never the airframe's
    /// default (user decision 2026-09-14): max air-to-air missiles with an active-radar missile
    /// preferred for a CAP/escort buy, max ground-attack for a strike buy. The score memo already
    /// proved the build succeeds, so this cannot come back empty for a candidate that passed
    /// <see cref="PassesRoleCapability"/>.
    /// </summary>
    private static Loadout? BuildRoleLoadout(FactionHQ hq, AircraftDefinition definition, AirRole role)
    {
        bool isAirToAir = role == AirRole.Fighter;
        CommanderAirCommandService.AirCommandMode mode = role switch
        {
            AirRole.Fighter => CommanderAirCommandService.AirCommandMode.AirGuard,
            AirRole.Awacs => CommanderAirCommandService.AirCommandMode.AwacsJammer,
            AirRole.Arad => CommanderAirCommandService.AirCommandMode.Arad,
            _ => CommanderAirCommandService.AirCommandMode.Cas,
        };
        return CommanderAirCommandService.TryBuildRoleLoadout(
            definition,
            hq,
            mode,
            preferArhMissiles: isAirToAir,
            out Loadout loadout,
            out _)
            ? loadout
            : null;
    }

    /// <summary>
    /// Whether ANY air-to-air-capable airframe can launch from a base this commander holds — the
    /// ladder's deadlock valve (user follow-up, 2026-09-14), read after the capability test and the
    /// window's own strip acceptance. Last resort included: a Cricket CAP beats a deadlocked
    /// ladder. Memoized loadout scores keep the walk cheap after the first review.
    /// </summary>
    private static bool HasAnyCapCandidate(FactionHQ hq)
    {
        return HasRoleCandidate(hq, AirRole.Fighter);
    }

    /// <summary>The names of the bases this commander holds, for the holds line's
    /// CAP-impossible reason — the reader has to be able to see WHICH strips were found
    /// incapable.</summary>
    private static string HeldBaseNames(FactionHQ hq)
    {
        string names = string.Empty;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled)
            {
                continue;
            }

            names = names.Length == 0 ? airbase.name : names + ", " + airbase.name;
        }

        return names.Length > 0 ? names : "no base it holds";
    }

    /// <summary>Whether a built loadout carries an active-radar-homing air-to-air missile — the
    /// launch line's "active-radar loadout" tag, read off the same test the loadout preference used.</summary>
    private static bool LoadoutHasArhMissile(Loadout loadout)
    {
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount?.info != null && CommanderAirCommandService.IsArhAirToAirMissile(mount.info))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What the wing is short of, with live sortie demand ahead of the standing rules (design SS3,
    /// user decision 2026-09-13: platoon/operations tasking takes priority): CAP demand first,
    /// CAS demand after — the demand queue answers <see cref="CommanderAirDemandKind.Cap"/> before
    /// <see cref="CommanderAirDemandKind.Cas"/>, so when the fund covers one airframe and both are
    /// wanted it buys the fighter — then air superiority once the player is actually flying, then
    /// a couple of transports, and nothing when no sortie is asking: with StrategicStrike tasking
    /// gone, an untasked strike airframe has nowhere to go but home.
    /// Pure (counts in, not the HQ): the priority order itself is what the self-check encodes.
    /// </summary>
    internal static AirRole? ChooseAirRole(
        CommanderAirDemandKind demand, int fighters, in ForceRead opponentForce, bool wantsRotary = false)
    {
        if (demand == CommanderAirDemandKind.Awacs)
        {
            return AirRole.Awacs;
        }

        if (demand == CommanderAirDemandKind.Cap)
        {
            return AirRole.Fighter;
        }

        if (demand == CommanderAirDemandKind.Arad)
        {
            return AirRole.Arad;
        }

        if (demand == CommanderAirDemandKind.Cas)
        {
            // Helicopter CAS by mission kind (design SS2); the caller falls back to the jet pass
            // when no pad or strip in range will launch one.
            return wantsRotary ? AirRole.RotaryCas : AirRole.Strike;
        }

        if (opponentForce.Aircraft > 0 && fighters == 0)
        {
            return AirRole.Fighter;
        }

        // The wing buys NO transports of its own (user decision, 2026-09-14). It used to top up to a
        // standing pair whenever the opponent fielded ground units, and every one of them was bought
        // empty: the idle sweep saw an aeroplane with no mission and sent it straight home again as
        // "a transport with nothing to deliver", so the whole rule did nothing but spend the air
        // budget on a round trip. Every transport the mod actually needs is bought by the supply and
        // insertion path, with its cargo already decided — see the insertion cargo roster. The
        // transport ROLE stays: that path still buys, claims and tasks through it.
        return null;
    }

    /// <summary>The live-roster wrapper around the pure rule above.</summary>
    private static AirRole? ChooseAirRole(
        FactionHQ hq, CommanderState state, in ForceRead opponentForce, CommanderAirDemandKind demand, bool wantsRotary)
    {
        return ChooseAirRole(demand, CountRole(hq, state, AirRole.Fighter), opponentForce, wantsRotary);
    }

    /// <summary>
    /// How many airframes of one role this faction already has in the world — by capability, like
    /// the buy that fields them (user decision 2026-09-14): a CAP-bought Compass counts toward the
    /// wing's fighters exactly as a Kestrel does, or the standing air-superiority counter would
    /// never see the wing it is meant to be counting.
    /// </summary>
    private static int CountRole(FactionHQ hq, CommanderState state, AirRole role)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit)
                || unit is not Aircraft aircraft
                || unit.disabled
                || aircraft.definition is not AircraftDefinition definition)
            {
                continue;
            }

            if (FillsAirRole(hq, definition, role))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Buys one aircraft and puts it in the air itself, rather than topping up stock and hoping
    /// <c>FactionHQ.DeployAIAircraft</c> launches something. That automatic tap is off in the duel
    /// (see <c>PrepareDuel</c>), and it was never controllable anyway: it picked a random airframe
    /// from the whole encyclopedia and a random airbase, so a heavy jet would take the only spot at
    /// a highway strip and write itself off. Here the airbase is chosen first and only aircraft that
    /// airbase will actually accept are considered, so nothing is bought that cannot fly. Returns
    /// what it spent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to buy the <b>dearest</b> airframe the strip would take, every single review, which
    /// is how the fourth playtest ended up watching two ground-attack jets at the start and then
    /// nothing else all match: one airframe was always the most expensive, so one airframe was
    /// always the answer, and once the air fund could not clear that price again nothing was bought
    /// at all. The wing is now composed by role, and inside a role by FITNESS TIER (design.md,
    /// airframe-selection_20260914, DECISION-011): the highest tier a held strip can launch always
    /// wins, and price decides only between airframes already inside that tier — the cheapest one
    /// when the sky and the ground are quiet, the best one the allocation covers when they are not.
    /// The rule it replaced counted airframes instead of reading the threat, so it bought two cheap
    /// ones of every role whatever was happening, which is how a CI-22 Cricket came to fly CAP at 12
    /// with an FS-12 Revoker available at 65.
    /// </para>
    /// <para>
    /// Helicopters are bought now; they were not before. The old ban was aimed at the right problem
    /// and hit the wrong target: a rotary airframe handed an <c>AirMission</c> gets the target half
    /// of it and none of the flying half, which is why the enemy's rotary wing used to dive into the
    /// ground. But <c>TryTaskAiAircraft</c> already refuses anything that is not a plane, so a
    /// bought helicopter gets no mission at all — and <c>AIHeloCombatState</c> is a complete flight
    /// AI on its own, right down to handing itself to <c>AIHeloTransportState</c> when it is
    /// carrying cargo, which is the game flying its own troop placements with no help from the mod.
    /// What is still banned is <c>PilotType.VTOL</c>, which <c>Pilot.SetStartingAiState</c> gives no
    /// AI state whatsoever: see <c>CommanderAirCommandService.CanAiFly</c>.
    /// </para>
    /// <para>
    /// Every path out of here that buys nothing says why, once, in the BepInEx log. An enemy air
    /// force that silently stops is indistinguishable from one that is broken, and telling those
    /// two apart from a playtest was not possible before.
    /// </para>
    /// <para>
    /// The old "buy anything that will fly" fallback pass is gone (user decision 2026-09-13): it
    /// bought strike airframes nobody had asked for — a transport top-up that found no transport
    /// fell through to the cheapest jet on the roster — and an airframe bought with no demand
    /// idled unowned and untasked to the ceiling (that playtest: 3 A-19 Brawler and 5 CI-22
    /// Cricket launched, zero tasking lines, zero sorties). No demand, no airframe.
    /// </para>
    /// </remarks>
    private float BuyAirframe(FactionHQ hq, CommanderState state, float budget, in ForceRead opponentForce)
    {
        LogAirRosterOnce(hq);
        if (CountAirborne(hq) >= CommanderSettings.AirborneCeiling)
        {
            ReportAirDenial(hq, state, $"it is at the {CommanderSettings.AirborneCeiling}-aircraft ceiling");
            return 0f;
        }

        // The sortie list drives the buy (design SS3): CAP demand first, CAS demand after — the
        // demand queue's own order — and no airframe at all when no sortie is asking. The ceiling
        // itself moved to a setting with the duel gate's removal — it applies on every mission now.
        CommanderAirDemandKind demand = CommanderOperationsService.TryGetAirDemand(
            hq, out GlobalPosition objective, out bool wantsRotary, out string sortieLabel,
            out int sortieTrackedAircraft, out int sortieObservedHostiles);

        // The threat the tier rule reads (design.md, airframe-selection_20260914 Section 2). A
        // sortie buy is judged against its own objective's numbers; a standing buy — the threat
        // fighter, the transport top-up — has no objective, so it reads the hostile aircraft tracked
        // around the commander's own bases, which is the same number the home CAP is sized from.
        int trackedAircraft = demand != CommanderAirDemandKind.None
            ? sortieTrackedAircraft
            : CountTrackedEnemyAircraftNearBases(hq);
        int observedHostiles = sortieObservedHostiles;
        AirRole? wanted = ChooseAirRole(hq, state, opponentForce, demand, wantsRotary);
        if (wanted == null)
        {
            ReportAirDenial(hq, state, "no sortie is asking for air support and the wing has its fighters and transports");
            return 0f;
        }

        // A sortie buy faces the objective it was bought for; the standing buys (threat fighters,
        // transports) face the opponent as before. With no local HQ there is no opponent to face, so
        // the old airbase-heading fallback applies — TryLaunchAiAircraft takes the strip's own
        // forward when the facing vector is degenerate.
        FactionHQ? player = CommanderGameAccess.GetLocalHq();
        GlobalPosition facing = demand != CommanderAirDemandKind.None
            ? objective
            : GetStrikeTarget(state, player == null ? hq : CommanderPlayerCommanderService.ChooseOpponent(hq, player));
        GlobalPosition? sortieObjective = demand == CommanderAirDemandKind.None ? null : objective;

        // Helicopter CAS by mission kind (design SS2): the rotary pass is tried first for a sortie
        // that wants it, and a sortie whose objective no pad or strip can reach falls back to the
        // jet pass with one line per objective — the reader has to be able to see WHY a forward
        // base got a jet.
        if (wanted == AirRole.RotaryCas)
        {
            float rotarySpent = TryBuyRole(
                hq, state, budget, AirRole.RotaryCas, facing, sortieObjective,
                allowLastResort: false, reportDenial: false, trackedAircraft, observedHostiles,
                out bool anyRotary,
                withinMeters: CommanderSettings.RotaryCasRangeMeters, near: objective);
            if (rotarySpent > 0f)
            {
                return rotarySpent;
            }

            // The fallback fires on ANY failed rotary buy, not only on "no pad in range" (fix,
            // 2026-09-14). Gating it on the candidate walk meant a roster that HAS an attack
            // helicopter it cannot currently afford never fell back and never logged: the
            // 2026-09-14 match ran a whole match of `CAS 0/4 rotary` with not one fallback line and
            // not one strike airframe bought for a rotary sortie.
            if (state.RotaryFallbackLogged.Add(sortieLabel))
            {
                CommanderAiLog.Note(
                    hq,
                    anyRotary
                        ? $"cannot afford an attack helicopter for {sortieLabel} this review; its CAS falls back to a jet."
                        : $"no attack helicopter can launch within {CommanderSettings.RotaryCasRangeMeters / 1000f:0} km of "
                            + $"{sortieLabel}; its CAS falls back to a jet.");
            }

            wanted = AirRole.Strike;
        }

        float spent = TryBuyRole(
            hq, state, budget, wanted.Value, facing, sortieObjective,
            allowLastResort: false, reportDenial: false, trackedAircraft, observedHostiles,
            out bool ordinaryCandidateAtAnyPrice);
        if (spent > 0f)
        {
            return spent;
        }

        // The last-resort airframe is admissible only when the roster has no ordinary airframe that
        // can fill the role from a held strip AT ANY PRICE (user decision 2026-09-13; bug fixed
        // 2026-09-14, see LastResortAllowed). One that exists but is dearer than this review's
        // slice means the commander saves for it, not that it settles for the Cricket.
        if (!LastResortAllowed(ordinaryCandidateAtAnyPrice))
        {
            ReportAirDenial(
                hq,
                state,
                $"its air budget is short of the cheapest {RoleCapabilityLabel(wanted.Value)} airframe its strips accept, "
                    + "and it saves for one rather than launching the last-resort airframe");
            return 0f;
        }

        return TryBuyRole(
            hq, state, budget, wanted.Value, facing, sortieObjective,
            allowLastResort: true, reportDenial: true, trackedAircraft, observedHostiles, out _);
    }

    /// <summary>
    /// One pass of the role buy, over the SAME candidate list the player's AIR window lists (see
    /// <see cref="airCatalog"/>) gated by the SAME airbase acceptance pair (see
    /// <see cref="FindAcceptingAirbase"/>) — capability, not identity: a Fighter-role pass buys any
    /// A/A-capable airframe, a Strike pass any ground-attack-capable one (user decision
    /// 2026-09-14). <paramref name="allowLastResort"/> admits the last-resort (Cricket) airframes —
    /// only BuyAirframe's second pass sets it, which is the whole of the LAST RESORT rule. Failure
    /// reasons are STABLE STRINGS (no balances in them) so the denial stays recognisable between
    /// reviews; ReportAirDenial re-logs a repeated reason on the holds cadence.
    /// </summary>
    /// <param name="reportDenial">Whether a failure to buy says why. Only the pass that actually
    /// ends the buy reports, so a first attempt that falls through to a successful last-resort buy
    /// prints no denial in between the two.</param>
    /// <param name="sawCandidateAtAnyPrice">Whether this pass found an airframe that can fill the
    /// role from a strip this commander holds, IGNORING the budget — what
    /// <see cref="LastResortAllowed"/> reads (bug fixed 2026-09-14).</param>
    /// <param name="trackedAircraft">Hostile aircraft tracked around the objective, and
    /// <paramref name="observedHostiles"/> the hostile ground units observed over it: the threat the
    /// tier rule reads to decide between the best airframe in the tier and the cheapest one
    /// (design.md, airframe-selection_20260914 Section 2).</param>
    private float TryBuyRole(
        FactionHQ hq,
        CommanderState state,
        float budget,
        AirRole role,
        GlobalPosition facing,
        GlobalPosition? sortieObjective,
        bool allowLastResort,
        bool reportDenial,
        int trackedAircraft,
        int observedHostiles,
        out bool sawCandidateAtAnyPrice,
        float withinMeters = 0f,
        GlobalPosition? near = null)
    {
        RefreshAirCatalog();
        float cheapestInRole = float.MaxValue;
        bool sawAnyAirbase = false;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled && airbase.center != null)
            {
                sawAnyAirbase = true;
                break;
            }
        }

        // The fitness tier rule (design.md, airframe-selection_20260914, DECISION-011) replaces the
        // old price walk: collect every candidate that passes the role's capability gate AND finds
        // an accepting strip, take the highest tier among them in the role's own order, and choose
        // inside that tier alone. Price decides WITHIN a tier and never between two of them — which
        // is what stopped a CI-22 Cricket winning a CAP buy at 12 while an FS-12 Revoker sat on the
        // strip at 65.
        tierCandidates.Clear();
        tierDefinitions.Clear();
        tierAirbases.Clear();
        tierTiers.Clear();
        foreach (AircraftDefinition definition in airCatalog)
        {
            if (definition == null
                || !PassesRoleCapability(hq, state, definition, role)
                // The LAST RESORT gate (user decision 2026-09-13): the Cricket is invisible to the
                // buy until the roster's other airframes cannot fill the role at all.
                || (!allowLastResort && IsLastResortAirframe(definition)))
            {
                continue;
            }

            Airbase? accepted = FindAcceptingAirbase(hq, definition, near, withinMeters);
            if (accepted == null)
            {
                continue;
            }

            if (definition.value < cheapestInRole)
            {
                cheapestInRole = definition.value;
            }

            AirframeTier tier = ForRole(definition, role);
            tierCandidates.Add(new AirframeCandidate(
                tierDefinitions.Count, tier, definition.value, RoleRating(hq, state, definition, role),
                IsLastResortAirframe(definition)));
            tierDefinitions.Add(definition);
            tierAirbases.Add(accepted);
            tierTiers.Add(tier);
        }

        sawCandidateAtAnyPrice = tierDefinitions.Count > 0;
        AirframeTier? tierWanted = HighestLaunchableTier(tierTiers, role);
        bool threatHigh = ThreatHigh(role, trackedAircraft, observedHostiles);
        int picked = -1;
        AirframeTier chosenTier = default;
        if (tierWanted != null)
        {
            chosenTier = tierWanted.Value;
            picked = SelectInTier(tierCandidates, chosenTier, threatHigh, budget);
        }

        // Reported on the pass that ends the buy alone, so a first attempt that falls through to a
        // successful last-resort buy does not print a denial in between the two.
        if (picked < 0)
        {
            if (reportDenial)
            {
                if (!sawAnyAirbase)
                {
                    ReportAirDenial(hq, state, "it holds no airbase to launch from");
                }
                else if (tierWanted == null)
                {
                    ReportAirDenial(
                        hq, state, $"its strips accept no {RoleCapabilityLabel(role)} airframe at all");
                }
                else
                {
                    // The ONE denial that carries live numbers rather than a stable string: the
                    // whole point of it is that the commander is waiting for a specific shortfall to
                    // close, and a reader cannot tell a near miss from a hopeless one without them.
                    // It therefore re-logs whenever either number moves, which is the intent.
                    ReportAirDenial(
                        hq,
                        state,
                        $"{chosenTier} tier unaffordable this review "
                            + $"(cheapest {CheapestInTier(tierCandidates, chosenTier):0}, allocation {budget:0}); "
                            + "it waits rather than dropping a tier");
                }
            }

            return 0f;
        }

        AircraftDefinition choice = tierDefinitions[picked];
        Airbase choiceAirbase = tierAirbases[picked];

        // Say so, once, when the strips this commander holds cannot launch the tier the role really
        // wants (design Section 4). Without this line a wing flying Compasses on CAP looks like a
        // broken rule rather than a map position — which is exactly how the 2026-09-14 highway-strip
        // report read before the tiers existed.
        ReportTierFallback(hq, state, role, chosenTier);

        // Combat buys launch with the scorer's own loadout, never the airframe's default (user
        // decision 2026-09-14); transports keep the game's random standard loadout.
        Loadout? forced = role == AirRole.Transport ? null : BuildRoleLoadout(hq, choice, role);
        // A transport buy has no tier to report and no threat to weigh — every transport sits in the
        // one tier and the choice is price alone — so it says what it is instead of borrowing the
        // combat wording, which read as "UH-90 Ibis (Multirole tier, best affordable — 5 in the sky)"
        // for an aeroplane that is not being bought to fight anything.
        string note = TierChoiceNote(
            hq, state, choice, role, threatHigh,
            role is AirRole.Strike or AirRole.RotaryCas or AirRole.Arad ? observedHostiles : trackedAircraft,
            role is AirRole.Strike or AirRole.RotaryCas or AirRole.Arad ? "observed" : "in the sky");
        if (IsLastResortAirframe(choice))
        {
            note += "; last resort: nothing else can fill the role";
        }

        if (CommanderAirCommandService.LoadoutHasPreferredCasOrdnance(forced))
        {
            note += "; AGM-68/AGM-48 loadout";
        }

        float spent = LaunchBoughtAirframe(
            hq, state, choiceAirbase, choice, facing, sortieObjective,
            forced, forHomeCap: false, $" ({note}).");
        return spent;
    }

    /// <summary>
    /// Names the tier the role wanted and the tier it settled for, once per commander per pair
    /// (design Section 4): <c>no Fighter-tier airframe can launch from its strips; Multirole flies
    /// CAP</c>. Silent when the role got the tier at the top of its own order, which is the ordinary
    /// case and would otherwise be a line every review.
    /// </summary>
    private static void ReportTierFallback(FactionHQ hq, CommanderState state, AirRole role, AirframeTier flying)
    {
        AirframeTier[] order = TierOrder(role);
        if (order.Length == 0 || flying == order[0])
        {
            return;
        }

        if (!state.TierFallbackLogged.Add($"{role}:{flying}"))
        {
            return;
        }

        string job = role is AirRole.Strike or AirRole.RotaryCas or AirRole.Arad ? "CAS" : "CAP";
        CommanderAiLog.Note(
            hq,
            $"no {order[0]}-tier airframe can launch from its strips; {flying} flies {job}.");
    }

    /// <summary>The cheapest candidate sitting in one tier, for the unaffordable denial's shortfall
    /// pair. Returns 0 for an empty tier, which the caller never asks for.</summary>
    private static float CheapestInTier(List<AirframeCandidate> candidates, AirframeTier tier)
    {
        float cheapest = float.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Tier == tier && candidates[i].Price < cheapest)
            {
                cheapest = candidates[i].Price;
            }
        }

        return cheapest == float.MaxValue ? 0f : cheapest;
    }

    /// <summary>
    /// How good an airframe is at a role, for the tier rule's "best affordable" pick (design
    /// Section 2). The air-to-air and ground-attack roles read the game's own
    /// <c>roleIdentity</c> rating — the same asset data <see cref="ForRole"/> tiers on, so the
    /// ranking inside a tier and the tier itself can never disagree. AWACS and ARAD have no rating
    /// of their own in the asset data, so they read the score their loadout builder already
    /// memoised, which is the nearest thing the game has to "how well does it do this job".
    /// </summary>
    private static float RoleRating(FactionHQ hq, CommanderState state, AircraftDefinition definition, AirRole role)
    {
        return role switch
        {
            AirRole.Fighter => definition.roleIdentity.antiAir,
            AirRole.Strike or AirRole.RotaryCas => definition.roleIdentity.antiSurface,
            AirRole.Arad => AradLoadoutScore(hq, state, definition),
            AirRole.Awacs => CapLoadoutScore(hq, state, definition),
            _ => definition.roleIdentity.antiSurface,
        };
    }

    /// <summary>The plain-words name of what a role's candidates must be able to do — the denial
    /// lines say the capability, not the internal role word, and never guess a cause: the old text
    /// blamed VTOLs for every empty candidate list, whatever the real reason was.</summary>
    private static string RoleCapabilityLabel(AirRole role)
    {
        return role switch
        {
            AirRole.Fighter => "air-to-air-capable",
            AirRole.Strike => "ground-attack-capable",
            AirRole.RotaryCas => "ground-attack-capable helicopter",
            AirRole.Awacs => "radar-carrying",
            AirRole.Arad => "anti-radiation-capable",
            _ => "AI-flyable transport",
        };
    }

    /// <summary>
    /// Launch-and-charge, the shared tail of every airframe buy (Reuse rule 4 — one definition, two
    /// callers: the sortie buyer in <see cref="TryBuyRole"/> and the home-CAP buyer in
    /// <see cref="BuyHomeCapFighter"/>): take the scorer-built loadout the caller picked — or, for a
    /// transport, the game's random standard loadout — record the launch expectation, fly the
    /// airframe, charge the faction, log. Returns the price charged, or 0 when the launch was
    /// refused.
    /// </summary>
    private float LaunchBoughtAirframe(
        FactionHQ hq,
        CommanderState state,
        Airbase airbase,
        AircraftDefinition choice,
        GlobalPosition facing,
        GlobalPosition? sortieObjective,
        Loadout? forcedLoadout,
        bool forHomeCap,
        string noteSuffix)
    {
        Loadout loadout = null!;
        float fuel = choice.aircraftParameters.DefaultFuelLevel;
        if (forcedLoadout != null)
        {
            // Same INTERNAL CANNONS rule as the player's AIR window, so an AI airframe with its
            // missiles spent goes home instead of strafing on its gun ammunition.
            loadout = CommanderAirCommandService.WithoutInternalCannons(forcedLoadout);
        }
        else
        {
            StandardLoadout? standard = choice.aircraftParameters.GetRandomStandardLoadout(choice, hq);
            if (standard != null)
            {
                loadout = CommanderAirCommandService.WithoutInternalCannons(standard.loadout);
                fuel = standard.FuelRatio;
            }
        }

        LiveryKey livery = new(choice.aircraftParameters.GetRandomLiveryForFaction(hq.faction));

        // Set before EVERY launch (user decision 2026-09-13): the registration that fires inside it
        // is matched back and the airframe OWNED — bound into the sortie that wanted it, parked on
        // the home CAP when no sortie did, or marked a rung-1 fighter that never leaves it.
        // Cleared below if the spawn is refused.
        CommanderOperationsService.RecordCommanderLaunch(hq, choice, airbase, sortieObjective, forHomeCap);

        if (!CommanderAirCommandService.TryLaunchAiAircraft(hq, airbase, choice, livery, loadout, fuel, facing))
        {
            CommanderOperationsService.ClearCommanderLaunch(hq);
            return 0f;
        }

        float cost = Mathf.Max(0f, choice.value);
        hq.AddFunds(-cost);
        RecordPurchase(hq);
        state.LastAirDenial = string.Empty;
        CommanderAiLog.Note(
            hq,
            $"launched a {choice.unitName} ({GetAirRole(choice)}) from {airbase.name} for {cost:0}{noteSuffix}");
        return cost;
    }

    /// <summary>
    /// Rung 1's buy (design.md, commander-priorities_20260914 Section 2, revised 2026-09-14): one
    /// airframe for the standing home CAP, chosen by CAPABILITY — at least one of its loadout
    /// options mounts an air-to-air missile, whatever its role data calls it — and then by FITNESS
    /// TIER (design.md, airframe-selection_20260914, DECISION-011): the highest CAP tier a held
    /// strip can launch wins outright, and inside it the best fighter the slice covers when hostile
    /// aircraft are tracked near the bases, the cheapest one when the sky is quiet. The tier rule
    /// replaced a "Fighter identity first, then the cheaper" order that let a T/A-30 Compass hold
    /// the patrol at 22 while an FS-12 Revoker was launchable at 65 — the exact case the user
    /// reported. The loadout is the AIR window's AIR SUPERIORITY scorer with an active-radar missile
    /// preferred, never the airframe's default. Returns the price charged, or 0.
    /// </summary>
    private float BuyHomeCapFighter(FactionHQ hq, CommanderState state, float budget)
    {
        LogAirRosterOnce(hq);
        RefreshAirCatalog();
        GlobalPosition facing = CommanderCaptureService.GetTerritoryCenter(hq);

        tierCandidates.Clear();
        tierDefinitions.Clear();
        tierAirbases.Clear();
        tierTiers.Clear();
        bool ordinaryCandidateAtAnyPrice = false;
        foreach (AircraftDefinition definition in airCatalog)
        {
            // The SHARED capability gate, not a hand-rolled pair (Reuse rule 4). It carries the
            // plane-pilot test this buy always needed — the claim parks the airframe on an Air
            // Command CAP task, which refuses anything without one — and, since 2026-09-14, the
            // radar/EW exclusion and the air-superiority refusal as well. Walking the catalogue
            // with its own two conditions is exactly how this buyer came to put an A-19 Brawler on
            // the home CAP after every other path had already been taught not to.
            if (definition == null || !PassesRoleCapability(hq, state, definition, AirRole.Fighter))
            {
                continue;
            }

            Airbase? accepted = FindAcceptingAirbase(hq, definition);
            if (accepted == null)
            {
                continue;
            }

            bool lastResort = IsLastResortAirframe(definition);

            // Counted BEFORE the price filter (bug fixed 2026-09-14, see LastResortAllowed): an
            // ordinary airframe this review's slice cannot afford is still an airframe that can
            // fill the role, and the last-resort rule asks whether one exists, not whether one is
            // affordable this instant.
            if (!lastResort)
            {
                ordinaryCandidateAtAnyPrice = true;
            }

            AirframeTier tier = ForRole(definition, AirRole.Fighter);
            tierCandidates.Add(new AirframeCandidate(
                tierDefinitions.Count, tier, definition.value, definition.roleIdentity.antiAir, lastResort));
            tierDefinitions.Add(definition);
            tierAirbases.Add(accepted);
            tierTiers.Add(tier);
        }

        AirframeTier? tierWanted = HighestLaunchableTier(tierTiers, AirRole.Fighter);
        if (tierWanted == null)
        {
            return 0f;
        }

        // The home CAP's threat read is the same count rung 1 sizes the patrol from — hostile
        // aircraft tracked near the commander's own bases (Reuse rule 4, one definition).
        int trackedAircraft = CountTrackedEnemyAircraftNearBases(hq);
        bool threatHigh = ThreatHigh(AirRole.Fighter, trackedAircraft, observedHostiles: 0);
        int picked = SelectInTier(tierCandidates, tierWanted.Value, threatHigh, budget);
        if (picked < 0)
        {
            // See TryBuyRole: the one denial that carries live numbers, because a reader cannot
            // tell a near miss from a hopeless one without them.
            ReportAirDenial(
                hq,
                state,
                $"its home-CAP slice cannot reach the {tierWanted.Value} tier this review "
                    + $"(cheapest {CheapestInTier(tierCandidates, tierWanted.Value):0}, allocation {budget:0}); "
                    + "it saves rather than dropping a tier");
            return 0f;
        }

        AircraftDefinition best = tierDefinitions[picked];
        Airbase bestAirbase = tierAirbases[picked];
        ReportTierFallback(hq, state, AirRole.Fighter, tierWanted.Value);
        if (IsLastResortAirframe(best) && !LastResortAllowed(ordinaryCandidateAtAnyPrice))
        {
            ReportAirDenial(
                hq,
                state,
                "its home-CAP slice is short of the cheapest ordinary fighter its strips accept, and it saves for one "
                    + "rather than launching the last-resort airframe");
            return 0f;
        }

        Loadout? loadout = BuildRoleLoadout(hq, best, AirRole.Fighter);
        string note = "home CAP, "
            + TierChoiceNote(hq, state, best, AirRole.Fighter, threatHigh, trackedAircraft, "in the sky");
        if (loadout != null && LoadoutHasArhMissile(loadout))
        {
            note += ", active-radar loadout";
        }

        return LaunchBoughtAirframe(
            hq, state, bestAirbase, best, facing, null, loadout, forHomeCap: true, $" ({note}).");
    }

    /// <summary>
    /// Hostile aircraft this commander has tracked within <see cref="HomeCapThreatRadiusMeters"/> of
    /// any airbase it holds — the second term of the CAP formula (design Section 2). The
    /// <c>IsUnderThreat</c> walk (freshness, own-HQ skip) with the aircraft filter and the base ring
    /// widened to the CAP's threat radius: the CAP answers what the commander can know about, not
    /// what exists — a raid that has never been spotted grows nothing.
    /// </summary>
    /// <remarks>Internal (one-word widening, Reuse rule 4): the home-CAP loan's quiet clock reads
    /// the same walk the CAP formula sizes itself from, so "the base is threatened" means one thing
    /// in both places (design.md, smarter-air-wing_20260914 Section 9).</remarks>
    internal static int CountTrackedEnemyAircraftNearBases(FactionHQ hq)
    {
        int count = 0;
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not Aircraft
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq))
            {
                continue;
            }

            if (IsNearOwnBase(hq, info.lastKnownPosition, HomeCapThreatRadiusMeters))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Names, once per mission, every airframe on the candidate list — the same list the player's
    /// AIR window lists (2026-09-14; it used to walk the faction's issued supply, which is why a
    /// strip-acceptable Vagrant never appeared here) — with the things the buy decides on: the pilot
    /// types on its prefab (the AI flight model it will actually get, and the runtime check for
    /// whether a Vagrant can be a commander aircraft at all), the role its identity reads, and its
    /// price.
    /// </summary>
    /// <remarks>
    /// All of these live in the game's asset files, not in its code, so a decompile cannot answer
    /// any of them and neither can reasoning about aircraft names. This line in the BepInEx console
    /// is the answer. It is also the only way to tell a genuinely unflyable airframe from one this
    /// filter is wrongly excluding: anything logged as VTOL is one the mod refuses to buy, and if
    /// something you have watched the Basegame AI fly appears there, the filter is wrong, not the
    /// aeroplane.
    /// </remarks>
    private void LogAirRosterOnce(FactionHQ hq)
    {
        if (!loggedAirRoster.Add(hq))
        {
            return;
        }

        // The radar/EW exclusion is per-faction — whether an airframe can build a radar loadout
        // depends on the stores this faction may hang — so the roster line needs the memo state to
        // report it. Without the state the line still prints; it just cannot name that one reason.
        states.TryGetValue(hq, out CommanderState rosterState);
        RefreshAirCatalog();
        foreach (AircraftDefinition definition in airCatalog)
        {
            if (definition == null)
            {
                continue;
            }

            CommanderPlugin.Log.LogInfo(
                $"Air roster ({hq.faction.name}): {definition.unitName} [{definition.jsonKey}] "
                    + $"pilot {DescribePilotTypes(definition)}, role {GetAirRole(definition)}, "
                    // The fitness tiers the buy actually chooses on (design.md,
                    // airframe-selection_20260914 Section 1). Printed per airframe so the whole
                    // table is readable in the log: a tier that looks wrong here is a retuned
                    // FighterRatio or a patched role rating, and nothing else could show it. An
                    // airframe that is not a combat candidate at all says so in words instead of
                    // printing a tier it can never be chosen from — a troop helicopter labelled
                    // "CAP tier Strike" is what made the UH-90 Ibis bug look like a tuning problem.
                    + $"CAP {DescribeTier(hq, rosterState, definition, AirRole.Fighter)} / "
                    + $"CAS {DescribeTier(hq, rosterState, definition, AirRole.Strike)} "
                    // The two ratings the tier was computed FROM. Without them a tier that looks
                    // surprising cannot be told apart from a broken rule: the KR-67 Ifrit and the
                    // Alkyon AB-4 land in different tiers from the ones the design predicted, and
                    // only the game's own numbers say whether that is the rule or the asset data.
                    + $"(A/A {definition.roleIdentity.antiAir:0.00}, A/G {definition.roleIdentity.antiSurface:0.00}), "
                    + $"value {definition.value:0}"
                    + (IsLastResortAirframe(definition) ? "  — LAST RESORT, bought only when nothing else can fill the role" : string.Empty)
                    + (CommanderAirCommandService.CanAiFly(definition) ? string.Empty : "  — NOT AI-FLYABLE, never bought")
                    + (CommanderAirCommandService.HasPlanePilot(definition) ? string.Empty : "  — NO PLANE PILOT, never tasked"));
        }

        ReportCasOrdnance(hq);
    }

    /// <summary>
    /// One line beside the roster saying whether this commander's CAS loadouts can actually carry
    /// the ordnance the doctrine prefers (design.md, smarter-air-wing_20260914 Section 1). Weapon
    /// availability is asset data gated per faction (<c>WeaponChecker.MountAllowedHQ</c>), so
    /// reasoning about it from the aircraft names is not possible — this line is the answer, and it
    /// is what tells a preference that is not firing from a roster that never had the missiles.
    /// </summary>
    private void ReportCasOrdnance(FactionHQ hq)
    {
        RefreshAirCatalog();
        string carriers = string.Empty;
        string fallback = string.Empty;
        foreach (AircraftDefinition definition in airCatalog)
        {
            if (definition == null
                || !CommanderAirCommandService.CanAiFly(definition)
                || !CommanderAirCommandService.TryDescribeCasOrdnance(definition, hq, out bool preferred, out string bestStore))
            {
                continue;
            }

            if (preferred)
            {
                carriers = carriers.Length == 0 ? definition.unitName : carriers + ", " + definition.unitName;
            }
            else if (fallback.Length == 0 && bestStore.Length > 0)
            {
                fallback = bestStore;
            }
        }

        CommanderPlugin.Log.LogInfo(
            $"Air roster ({hq.faction.name}): CAS ordnance: "
                + (carriers.Length > 0
                    ? $"AGM-68/AGM-48 available on {carriers}"
                    : $"none — falls back to {(fallback.Length > 0 ? fallback : "whatever the scorer finds")}"));
    }

    /// <summary>
    /// What the roster line says about an airframe's standing in one role: its tier, or — when it is
    /// not a combat candidate at all — the reason in plain words. The reason matters more than the
    /// word "excluded": "excluded (transport)" tells a reader the rule is working, where a bare
    /// tier on a troop helicopter told them nothing was.
    /// </summary>
    private static string DescribeTier(
        FactionHQ hq, CommanderState? state, AircraftDefinition definition, AirRole role)
    {
        if (MayFillRole(definition, role))
        {
            // The radar aeroplane is reserved for the AWACS station, which is decided at the
            // capability gate rather than in the pure tier table because it reads per-faction
            // stores. Named here so the roster line agrees with what the buy will actually do.
            return state != null && HasRadarLoadout(hq, state, definition)
                ? "excluded (radar/EW)"
                : $"{ForRole(definition, role)} tier";
        }

        if (GetAirRole(definition) == AirRole.Transport)
        {
            return "excluded (transport)";
        }

        if (!CommanderAirCommandService.HasPlanePilot(definition))
        {
            return "excluded (no plane pilot)";
        }

        return "excluded (rotary)";
    }

    /// <summary>The pilot types on an airframe's prefab, which is what decides its AI flight model.</summary>
    private static string DescribePilotTypes(AircraftDefinition definition)
    {
        Aircraft? prefab = definition.unitPrefab != null ? definition.unitPrefab.GetComponent<Aircraft>() : null;
        if (prefab?.pilots == null || prefab.pilots.Length == 0)
        {
            return "none";
        }

        string types = string.Empty;
        for (int i = 0; i < prefab.pilots.Length; i++)
        {
            Pilot? pilot = prefab.pilots[i];
            if (pilot == null)
            {
                continue;
            }

            string name = pilot.pilotType.ToString();
            if (types.IndexOf(name, System.StringComparison.Ordinal) < 0)
            {
                types = types.Length == 0 ? name : types + "+" + name;
            }
        }

        return types.Length == 0 ? "none" : types;
    }

    /// <summary>
    /// Asserts the role rule still reads the way the buy loop assumes. It is two comparisons on
    /// asset data, so a game patch that retunes an airframe's role identity, or an edit that flips
    /// the comparison, silently turns the mixed wing back into a monoculture with nothing to notice
    /// it. Run once at plugin load beside the plan self-check.
    /// </summary>
    private static void CheckAirRoles()
    {
        CheckAirRole("interceptor", antiAir: 0.9f, antiSurface: 0.1f, captureCapacity: 0, AirRole.Fighter);
        CheckAirRole("attack jet", antiAir: 0.1f, antiSurface: 0.9f, captureCapacity: 0, AirRole.Strike);
        CheckAirRole("transport", antiAir: 0f, antiSurface: 0f, captureCapacity: 8, AirRole.Transport);
    }

    private static void CheckAirRole(
        string name, float antiAir, float antiSurface, int captureCapacity, AirRole expected)
    {
        // No prefab, so HasPlanePilot is false and the transport branch is reachable, which is the
        // branch worth checking: a plane that carries troops must still count as what it shoots at.
        AircraftDefinition definition = ScriptableObject.CreateInstance<AircraftDefinition>();
        definition.roleIdentity.antiAir = antiAir;
        definition.roleIdentity.antiSurface = antiSurface;
        definition.captureCapacity = captureCapacity;

        AirRole actual = GetAirRole(definition);
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError(
                $"Enemy air role self-check FAILED ({name}): expected {expected}, got {actual}.");
        }

        Object.Destroy(definition);
    }

    /// <summary>
    /// The air buy rules that live in this file, run once at plugin load beside the role rule:
    /// the demand priority (operations tasking outranks the standing rules — user decision
    /// 2026-09-13) and the Cricket LAST RESORT classification. An edit that reorders
    /// <see cref="ChooseAirRole"/> or retypes the Cricket silently changes what every commander
    /// fields; this is what says so in the BepInEx console.
    /// </summary>
    private static void CheckAirBuyRules()
    {
        ForceRead flyingOpponent = new() { Aircraft = 2, Ground = 6 };
        ForceRead groundOpponent = new() { Ground = 6 };

        // The air savings ceiling (fix, 2026-09-14). A wing may bank the price of the dearest thing
        // an open demand wants, floored at one dearest fighter, and never a multiple of either.
        Expect("a quiet wing saves no more than one dearest fighter", AirFundCeiling(0f, 65f), 65f);
        Expect("an open strike demand raises the ceiling to that airframe", AirFundCeiling(145f, 65f), 145f);
        Expect("a demand cheaper than a fighter never lowers the floor", AirFundCeiling(12f, 65f), 65f);
        Expect("a commander whose strips launch nothing banks nothing", AirFundCeiling(0f, 0f), 0f);
        Expect("a negative price is never a ceiling", AirFundCeiling(-50f, 0f), 0f);
        Expect(
            "the ceiling is the price of one airframe, never several",
            AirFundCeiling(145f, 65f) < 145f * 2f,
            true);

        Expect("CAP demand buys a fighter even with transports short and the opponent on the ground",
            ChooseAirRole(CommanderAirDemandKind.Cap, fighters: 2, flyingOpponent), AirRole.Fighter);
        Expect("CAS demand buys a strike airframe even with transports short",
            ChooseAirRole(CommanderAirDemandKind.Cas, fighters: 2, groundOpponent), AirRole.Strike);
        Expect("no demand and a flying opponent with no fighters buys the air-superiority counter",
            ChooseAirRole(CommanderAirDemandKind.None, fighters: 0, flyingOpponent), AirRole.Fighter);
        // The wing buys no transports of its own (user decision, 2026-09-14): the supply and
        // insertion path buys those, with cargo already aboard. NOTHING may yield a transport buy —
        // asserted over every demand kind, both opponent shapes and an empty and a stocked wing,
        // because ChooseAirRole is the only thing that names the role a buy will shop for.
        foreach (CommanderAirDemandKind kind in System.Enum.GetValues(typeof(CommanderAirDemandKind)))
        {
            foreach (bool flying in new[] { true, false })
            {
                for (int fighters = 0; fighters <= 2; fighters++)
                {
                    Expect(
                        $"no buy ever asks for a transport ({kind}, "
                            + $"{(flying ? "flying" : "ground")} opponent, {fighters} fighters up)",
                        ChooseAirRole(kind, fighters, flying ? flyingOpponent : groundOpponent) == AirRole.Transport,
                        false);
                }
            }
        }

        Expect("a ground opponent no longer makes the wing buy a transport of its own",
            ChooseAirRole(CommanderAirDemandKind.None, fighters: 1, groundOpponent), null);
        Expect("nor does an empty wing facing a ground opponent buy one",
            ChooseAirRole(CommanderAirDemandKind.None, fighters: 0, groundOpponent), null);
        Expect("no demand buys nothing", ChooseAirRole(CommanderAirDemandKind.None, fighters: 1, groundOpponent), null);
        Expect("AWACS demand buys the radar airframe",
            ChooseAirRole(CommanderAirDemandKind.Awacs, fighters: 2, flyingOpponent), AirRole.Awacs);
        Expect("ARAD demand buys the anti-radiation airframe",
            ChooseAirRole(CommanderAirDemandKind.Arad, fighters: 2, groundOpponent), AirRole.Arad);
        Expect("a CAS sortie that wants helicopters buys a helicopter",
            ChooseAirRole(CommanderAirDemandKind.Cas, fighters: 2, groundOpponent, wantsRotary: true), AirRole.RotaryCas);
        Expect("a CAS sortie that wants jets buys a jet",
            ChooseAirRole(CommanderAirDemandKind.Cas, fighters: 2, groundOpponent, wantsRotary: false), AirRole.Strike);
        Expect("the rotary preference never changes what a CAP demand buys",
            ChooseAirRole(CommanderAirDemandKind.Cap, fighters: 2, groundOpponent, wantsRotary: true), AirRole.Fighter);

        AircraftDefinition cricket = ScriptableObject.CreateInstance<AircraftDefinition>();
        cricket.jsonKey = "COIN";
        AircraftDefinition trainer = ScriptableObject.CreateInstance<AircraftDefinition>();
        trainer.jsonKey = "trainer";
        AircraftDefinition fighter = ScriptableObject.CreateInstance<AircraftDefinition>();
        fighter.jsonKey = "Fighter1";
        Expect("the Cricket's COIN data key is last resort", IsLastResortAirframe(cricket), true);
        Expect("the Compass trainer is NOT last resort (the user's correction)", IsLastResortAirframe(trainer), false);
        Expect("a normal airframe is not last resort", IsLastResortAirframe(fighter), false);
        Object.Destroy(cricket);
        Object.Destroy(trainer);
        Object.Destroy(fighter);

        // The home-CAP ARH rule (user decision 2026-09-14): the classification keys on the game's own
        // ARHSeeker component on the missile prefab, exactly the way the ARM test keys on ARMSeeker.
        // A GameObject with the seeker stands in for the missile prefab the real mounts carry.
        GameObject seekerPrefab = new("ArhSeekerProbe");
        seekerPrefab.AddComponent<ARHSeeker>();
        WeaponInfo arhMissile = ScriptableObject.CreateInstance<WeaponInfo>();
        arhMissile.missile = true;
        arhMissile.effectiveness.antiAir = 1f;
        arhMissile.weaponPrefab = seekerPrefab;
        WeaponInfo noSeeker = ScriptableObject.CreateInstance<WeaponInfo>();
        noSeeker.missile = true;
        noSeeker.effectiveness.antiAir = 1f;
        WeaponInfo groundMissile = ScriptableObject.CreateInstance<WeaponInfo>();
        groundMissile.missile = true;
        groundMissile.weaponPrefab = seekerPrefab;
        WeaponInfo arhBomb = ScriptableObject.CreateInstance<WeaponInfo>();
        arhBomb.missile = false;
        arhBomb.effectiveness.antiAir = 1f;
        arhBomb.weaponPrefab = seekerPrefab;
        Expect("a missile carrying the game's ARH seeker and scored against air is an ARH air-to-air missile",
            CommanderAirCommandService.IsArhAirToAirMissile(arhMissile), true);
        Expect("a missile with no seeker on its prefab is not ARH, however anti-air it is",
            CommanderAirCommandService.IsArhAirToAirMissile(noSeeker), false);
        Expect("a seeker on a weapon with no anti-air score is not an air-to-air missile",
            CommanderAirCommandService.IsArhAirToAirMissile(groundMissile), false);
        Expect("a bomb carrying a seeker is not a missile at all",
            CommanderAirCommandService.IsArhAirToAirMissile(arhBomb), false);
        Object.Destroy(seekerPrefab);
        Object.Destroy(arhMissile);
        Object.Destroy(noSeeker);
        Object.Destroy(groundMissile);
        Object.Destroy(arhBomb);

        CheckCasOrdnancePreference();
        CheckLastResortRule();
        CheckAirframeTiers();
    }

    /// <summary>
    /// The CAS ordnance preference (design.md, smarter-air-wing_20260914 Section 1): AGM-68 beats
    /// AGM-48, either beats any other air-to-ground store however heavy its rack, and a store with
    /// no ground effectiveness is never preferred whatever it is called. The bonus is what decides
    /// which store every commander-built CAS hardpoint carries, so a retune that shrinks it below
    /// an ordinary rack's score silently ends the preference.
    /// </summary>
    private static void CheckCasOrdnancePreference()
    {
        WeaponMount heavy = MakeCasMount("AGM-68", antiSurface: 0.9f, ammo: 2);
        WeaponMount light = MakeCasMount("AGM-48 ", antiSurface: 0.9f, ammo: 4);
        WeaponMount rockets = MakeCasMount("S-24 Rocket Pod", antiSurface: 0.9f, ammo: 20);
        WeaponMount recon = MakeCasMount("AGM-48 Recon", antiSurface: 0f, ammo: 1);
        WeaponMount nothing = MakeCasMount("R-73 Archer", antiSurface: 0f, ammo: 4);

        Expect("the AGM-68 is the first-tier CAS store", CommanderAirCommandService.PreferredCasOrdnanceRank(heavy), 0);
        Expect("the AGM-48 is the second-tier CAS store", CommanderAirCommandService.PreferredCasOrdnanceRank(light), 1);
        Expect("an ordinary rocket pod is not preferred CAS ordnance", CommanderAirCommandService.PreferredCasOrdnanceRank(rockets), -1);
        Expect("the recon AGM-48 carries no warhead, so it is not preferred CAS ordnance", CommanderAirCommandService.PreferredCasOrdnanceRank(recon), -1);
        Expect("an air-to-air missile is not preferred CAS ordnance", CommanderAirCommandService.PreferredCasOrdnanceRank(nothing), -1);

        float heavyScore = CommanderAirCommandService.ScoreCasMountForCommander(heavy);
        float lightScore = CommanderAirCommandService.ScoreCasMountForCommander(light);
        float rocketScore = CommanderAirCommandService.ScoreCasMountForCommander(rockets);
        float noneScore = CommanderAirCommandService.ScoreCasMountForCommander(nothing);
        Expect("the AGM-68 outranks the AGM-48", heavyScore > lightScore, true);
        Expect("the AGM-48 outranks a twenty-store rocket pod", lightScore > rocketScore, true);
        Expect("an ordinary ground store still outranks a weapon that cannot hit the ground", rocketScore > noneScore, true);
        Expect("a weapon with no ground effectiveness scores nothing for CAS", noneScore, 0f);

        Object.Destroy(heavy);
        Object.Destroy(light);
        Object.Destroy(rockets);
        Object.Destroy(recon);
        Object.Destroy(nothing);
    }

    /// <summary>
    /// The LAST RESORT admissibility rule (addendum 2026-09-14). The observed failure is the case
    /// worth naming: a Compass was on the roster and launchable, the review's remaining slice could
    /// not cover a second one, and the Cricket went up instead. The rule now asks only whether an
    /// ordinary airframe EXISTS, so that case buys nothing and saves.
    /// </summary>
    private static void CheckLastResortRule()
    {
        Expect("the last-resort airframe flies when the roster holds nothing else for the role",
            LastResortAllowed(ordinaryCandidateAtAnyPrice: false), true);
        Expect("an ordinary airframe on the roster keeps the last-resort airframe on the ground",
            LastResortAllowed(ordinaryCandidateAtAnyPrice: true), false);
        Expect("an ordinary airframe this review cannot afford still keeps the last-resort airframe grounded "
                + "(the T/A-30 Compass / CI-22 Cricket bug, 2026-09-14)",
            LastResortAllowed(ordinaryCandidateAtAnyPrice: true), false);
    }

    /// <summary>A probe mount for the ordnance checks: a named, non-nuclear conventional missile
    /// with a rack size, which is everything <c>PreferredCasOrdnanceRank</c> and the CAS scorer
    /// read.</summary>
    private static WeaponMount MakeCasMount(string weaponName, float antiSurface, int ammo)
    {
        WeaponInfo info = ScriptableObject.CreateInstance<WeaponInfo>();
        info.weaponName = weaponName;
        info.missile = true;
        info.effectiveness.antiSurface = antiSurface;
        WeaponMount mount = ScriptableObject.CreateInstance<WeaponMount>();
        mount.mountName = weaponName;
        mount.info = info;
        mount.ammo = ammo;
        return mount;
    }

    /// <summary>
    /// The airframe fitness tiers and the rule that picks inside one (design.md,
    /// airframe-selection_20260914, DECISION-011). Every number here can be retuned into nonsense —
    /// a <see cref="FighterRatio"/> nudged down puts the Ifrit ahead of the Revoker on CAP, a
    /// threshold nudged up buys Crickets into a raid — and nothing in the running game would say so
    /// until a playtest went badly. This is what says so at plugin load.
    /// </summary>
    private static void CheckAirframeTiers()
    {
        // Section 1: the tier table, read at the ratio boundary in both directions.
        Expect("an airframe rated exactly the ratio above its other role is a fighter",
            TierOf(antiAir: 1.5f, antiSurface: 1f, AirRole.Fighter), AirframeTier.Fighter);
        Expect("just under the ratio is a multirole, not a fighter",
            TierOf(antiAir: 1.49f, antiSurface: 1f, AirRole.Fighter), AirframeTier.Multirole);
        Expect("the mirror of the ratio is a strike airframe",
            TierOf(antiAir: 1f, antiSurface: 1.5f, AirRole.Fighter), AirframeTier.Strike);
        Expect("just under the mirrored ratio is a multirole too",
            TierOf(antiAir: 1f, antiSurface: 1.49f, AirRole.Fighter), AirframeTier.Multirole);
        Expect("an airframe the game rates at nothing either way is a multirole, never a fighter",
            TierOf(antiAir: 0f, antiSurface: 0f, AirRole.Fighter), AirframeTier.Multirole);
        Expect("a tier is the same airframe fact whichever role is asking",
            TierOf(antiAir: 1f, antiSurface: 1.5f, AirRole.Strike), AirframeTier.Strike);

        // The bottom tier: the last resort and the trainers, whatever their ratings say.
        AircraftDefinition cricket = ScriptableObject.CreateInstance<AircraftDefinition>();
        cricket.jsonKey = "COIN";
        cricket.roleIdentity.antiSurface = 1f;
        AircraftDefinition compass = ScriptableObject.CreateInstance<AircraftDefinition>();
        compass.jsonKey = "trainer";
        compass.roleIdentity.antiSurface = 1f;
        AircraftDefinition vagrant = ScriptableObject.CreateInstance<AircraftDefinition>();
        vagrant.jsonKey = "VTOLTrainer1";
        vagrant.roleIdentity.antiSurface = 1f;
        AircraftDefinition revoker = ScriptableObject.CreateInstance<AircraftDefinition>();
        revoker.jsonKey = "Fighter1";
        revoker.roleIdentity.antiAir = 1f;
        Expect("the CI-22 Cricket is bottom tier for CAP", ForRole(cricket, AirRole.Fighter), AirframeTier.LastResort);
        Expect("the CI-22 Cricket is bottom tier for CAS too", ForRole(cricket, AirRole.Strike), AirframeTier.LastResort);
        Expect("the T/A-30 Compass trainer is bottom tier however it is rated",
            ForRole(compass, AirRole.Fighter), AirframeTier.LastResort);
        Expect("the VT-7 Vagrant's trainer data key puts it in the bottom tier as well",
            ForRole(vagrant, AirRole.Strike), AirframeTier.LastResort);
        Expect("the T/A-30 Compass is a trainer", IsTrainerAirframe(compass), true);
        Expect("the FS-12 Revoker is not a trainer", IsTrainerAirframe(revoker), false);
        Expect("every AWACS candidate sits in one tier, because the radar pod is the qualification",
            ForRole(revoker, AirRole.Awacs), AirframeTier.Multirole);
        Object.Destroy(cricket);
        Object.Destroy(compass);
        Object.Destroy(vagrant);
        Object.Destroy(revoker);

        // Section 2: the CAS order is the CAP order reversed, except that the bottom stays bottom.
        AirframeTier[] cap = TierOrder(AirRole.Fighter);
        AirframeTier[] cas = TierOrder(AirRole.Strike);
        Expect("CAP shops for a fighter first", cap[0], AirframeTier.Fighter);
        Expect("CAS shops for a strike airframe first", cas[0], AirframeTier.Strike);
        Expect("the CAS order is the CAP order reversed in the middle", cas[1], cap[1]);
        Expect("the CAS order ends on the CAP order's first tier", cas[2], cap[0]);
        Expect("the bottom tier is the bottom of both orders", cas[3], cap[3]);
        Expect("the bottom of the CAP order is the last resort", cap[3], AirframeTier.LastResort);
        Expect("ARAD walks the CAS order", TierOrder(AirRole.Arad)[0], AirframeTier.Strike);
        Expect("rotary CAS walks the CAS order", TierOrder(AirRole.RotaryCas)[0], AirframeTier.Strike);

        // The highest launchable tier, including the empty-tier skip the design asks for.
        AirframeTier[] noFighterOnTheStrips = { AirframeTier.Strike, AirframeTier.LastResort };
        Expect("a CAP buy with no fighter and no multirole launchable falls to the strike tier",
            HighestLaunchableTier(noFighterOnTheStrips, AirRole.Fighter), AirframeTier.Strike);
        AirframeTier[] fullRoster =
        {
            AirframeTier.Strike, AirframeTier.LastResort, AirframeTier.Fighter, AirframeTier.Multirole,
        };
        Expect("a strike airframe is never the CAP answer while a fighter can launch",
            HighestLaunchableTier(fullRoster, AirRole.Fighter), AirframeTier.Fighter);
        Expect("a fighter is never the CAS answer while a strike airframe can launch",
            HighestLaunchableTier(fullRoster, AirRole.Strike), AirframeTier.Strike);
        AirframeTier[] trainersOnly = { AirframeTier.LastResort };
        Expect("a roster of trainers still launches something",
            HighestLaunchableTier(trainersOnly, AirRole.Fighter), AirframeTier.LastResort);
        Expect("nothing launchable is nothing chosen",
            HighestLaunchableTier(System.Array.Empty<AirframeTier>(), AirRole.Fighter), null);

        // The air-superiority REFUSAL (user report, 2026-09-14: "seeing a lot of air superiority
        // brawlers - SHOULDN'T BE, they're CAS aircraft"). Every path that can hand an airframe a
        // patrol, an escort or a sortie CAP slot reads this through one of the two capability
        // gates, so these cases stand for all of them.
        AircraftDefinition brawler = ScriptableObject.CreateInstance<AircraftDefinition>();
        brawler.jsonKey = "CAS1";
        brawler.roleIdentity.antiAir = 0.30f;
        brawler.roleIdentity.antiSurface = 0.80f;
        AircraftDefinition chicane = ScriptableObject.CreateInstance<AircraftDefinition>();
        chicane.jsonKey = "AttackHelo1";
        chicane.roleIdentity.antiAir = 0.27f;
        chicane.roleIdentity.antiSurface = 0.90f;
        AircraftDefinition revoker2 = ScriptableObject.CreateInstance<AircraftDefinition>();
        revoker2.jsonKey = "Fighter1";
        revoker2.roleIdentity.antiAir = 1.00f;
        revoker2.roleIdentity.antiSurface = 0.46f;
        AircraftDefinition alkyon = ScriptableObject.CreateInstance<AircraftDefinition>();
        alkyon.jsonKey = "FastBomber1";
        alkyon.roleIdentity.antiAir = 0.70f;
        alkyon.roleIdentity.antiSurface = 1.00f;
        AircraftDefinition compass2 = ScriptableObject.CreateInstance<AircraftDefinition>();
        compass2.jsonKey = "trainer";
        compass2.roleIdentity.antiAir = 0.62f;
        compass2.roleIdentity.antiSurface = 0.64f;
        Expect("the A-19 Brawler is refused every air-superiority task, at the game's own ratings",
            MayFlyAirSuperiority(brawler), false);
        Expect("the SAH-46 Chicane is refused every air-superiority task",
            MayFlyAirSuperiority(chicane), false);
        Expect("the FS-12 Revoker flies air superiority", MayFlyAirSuperiority(revoker2), true);
        Expect("a multirole bomber is still allowed to escort", MayFlyAirSuperiority(alkyon), true);
        Expect("the T/A-30 Compass may hold the patrol when nothing better can launch",
            MayFlyAirSuperiority(compass2), true);
        Expect("the A-19 Brawler is exactly the strike tier the refusal keys on",
            ForRole(brawler, AirRole.Fighter), AirframeTier.Strike);
        Expect("refusing the patrol does not refuse the job it IS for",
            ForRole(brawler, AirRole.Strike), AirframeTier.Strike);

        // The structural exclusions (user report, 2026-09-14: "it's spawned a UH-90 transport for
        // CAP for a platoon"). A transport carries real role ratings — the Ibis is 0.27 / 0.90,
        // which is the strike tier by arithmetic — so nothing but an explicit exclusion keeps it
        // out of the combat tiers.
        AircraftDefinition ibis = ScriptableObject.CreateInstance<AircraftDefinition>();
        ibis.jsonKey = "UtilityHelo1";
        ibis.captureCapacity = 8;
        ibis.roleIdentity.antiAir = 0.27f;
        ibis.roleIdentity.antiSurface = 0.90f;
        Expect("a UH-90 Ibis troop helicopter is no CAP candidate, whatever its ratings say",
            ForRole(ibis, AirRole.Fighter), AirframeTier.Excluded);
        Expect("a transport is no CAS candidate either", ForRole(ibis, AirRole.Strike), AirframeTier.Excluded);
        Expect("a transport is no ARAD candidate", ForRole(ibis, AirRole.Arad), AirframeTier.Excluded);
        Expect("a transport is no AWACS candidate", ForRole(ibis, AirRole.Awacs), AirframeTier.Excluded);
        Expect("a transport is still the candidate for the transport role",
            ForRole(ibis, AirRole.Transport), AirframeTier.Multirole);
        Expect("a transport is refused every combat role through the shared gate",
            MayFillRole(ibis, AirRole.Fighter), false);
        Expect("a transport is refused air superiority", MayFlyAirSuperiority(ibis), false);
        Expect("the roster line says WHY a transport has no tier",
            DescribeTier(hq: null!, state: null, ibis, AirRole.Fighter), "excluded (transport)");
        Expect("a combat airframe is not offered for the transport role",
            ForRole(brawler, AirRole.Transport), AirframeTier.Excluded);
        Object.Destroy(ibis);

        // No plane pilot, no tasking. These probe definitions carry no prefab at all, so
        // HasPlanePilot is false for every one of them — which is the condition being asserted, and
        // also why the tier table itself must NOT read the prefab: ForRole has to stay answerable
        // from the two role ratings alone, and the prefab refusals live in MayFillRole beside it.
        Expect("an airframe nothing can task is refused every combat role, however it is rated",
            MayFillRole(revoker2, AirRole.Fighter), false);
        Expect("the tier table still answers for it, because a tier is a rating fact",
            ForRole(revoker2, AirRole.Fighter), AirframeTier.Fighter);
        Expect("the roster line names the reason rather than printing a tier it can never be picked from",
            DescribeTier(hq: null!, state: null, revoker2, AirRole.Fighter), "excluded (no plane pilot)");
        Expect("the tier half of the refusal is separate, so a real fighter still passes it",
            MayFlyAirSuperiority(revoker2), true);
        Object.Destroy(brawler);
        Object.Destroy(chicane);
        Object.Destroy(revoker2);
        Object.Destroy(alkyon);
        Object.Destroy(compass2);

        // The same order, asked one pair at a time — what the sortie fill and the idle sweep read
        // when they are choosing between airframes the commander already owns.
        Expect("an owned fighter is preferred to an owned strike airframe for a CAP slot",
            TierBeats(AirframeTier.Fighter, AirframeTier.Strike, AirRole.Fighter), true);
        Expect("an owned strike airframe never displaces an owned fighter on CAP",
            TierBeats(AirframeTier.Strike, AirframeTier.Fighter, AirRole.Fighter), false);
        Expect("an owned strike airframe is preferred for a CAS slot",
            TierBeats(AirframeTier.Strike, AirframeTier.Fighter, AirRole.Strike), true);
        Expect("a multirole beats a trainer for either job",
            TierBeats(AirframeTier.Multirole, AirframeTier.LastResort, AirRole.Strike), true);
        Expect("a tier never beats itself", TierBeats(AirframeTier.Fighter, AirframeTier.Fighter, AirRole.Fighter), false);

        // The threat read, checked either side of both thresholds.
        Expect("one tracked hostile aircraft is a quiet sky for a CAP buy",
            ThreatHigh(AirRole.Fighter, trackedAircraft: 1, observedHostiles: 0), false);
        Expect("two tracked hostile aircraft buy the best fighter the tier holds",
            ThreatHigh(AirRole.Fighter, trackedAircraft: 2, observedHostiles: 0), true);
        Expect("two observed hostiles are a quiet objective for a CAS buy",
            ThreatHigh(AirRole.Strike, trackedAircraft: 0, observedHostiles: 2), false);
        Expect("three observed hostiles buy the best strike airframe the tier holds",
            ThreatHigh(AirRole.Strike, trackedAircraft: 0, observedHostiles: 3), true);
        Expect("a CAS buy reads the ground, not the sky",
            ThreatHigh(AirRole.Strike, trackedAircraft: 9, observedHostiles: 0), false);
        Expect("a CAP buy reads the sky, not the ground",
            ThreatHigh(AirRole.Fighter, trackedAircraft: 0, observedHostiles: 9), false);
        Expect("a transport is bought on price whatever is happening, in the sky or on the ground",
            ThreatHigh(AirRole.Transport, trackedAircraft: 9, observedHostiles: 9), false);

        // Choosing inside a tier: the FS-12 Revoker (65, rated 1.0) against the FS-20 Vortex
        // (90, rated 1.2), both fighter tier, with the CI-22 Cricket sitting in the bottom tier.
        AirframeCandidate[] roster =
        {
            new(0, AirframeTier.Fighter, price: 65f, rating: 1.0f, lastResort: false),
            new(1, AirframeTier.Fighter, price: 90f, rating: 1.2f, lastResort: false),
            new(2, AirframeTier.LastResort, price: 12f, rating: 0.3f, lastResort: true),
            new(3, AirframeTier.LastResort, price: 22f, rating: 0.4f, lastResort: false),
        };
        Expect("a quiet sky buys the cheapest fighter in the tier",
            SelectInTier(roster, AirframeTier.Fighter, threatHigh: false, allocation: 500f), 0);
        Expect("a threatened sky buys the best fighter the allocation covers",
            SelectInTier(roster, AirframeTier.Fighter, threatHigh: true, allocation: 500f), 1);
        Expect("a candidate priced above the allocation is never chosen",
            SelectInTier(roster, AirframeTier.Fighter, threatHigh: true, allocation: 80f), 0);
        Expect("a tier the allocation cannot reach waits rather than dropping a tier",
            SelectInTier(roster, AirframeTier.Fighter, threatHigh: true, allocation: 40f), -1);
        Expect("the CI-22 Cricket stays on the ground while the T/A-30 Compass can fly, however much cheaper it is",
            SelectInTier(roster, AirframeTier.LastResort, threatHigh: false, allocation: 500f), 3);
        Expect("the CI-22 Cricket flies when the Compass is out of reach",
            SelectInTier(roster, AirframeTier.LastResort, threatHigh: false, allocation: 15f), 2);
        Expect("an empty tier chooses nothing",
            SelectInTier(roster, AirframeTier.Multirole, threatHigh: false, allocation: 500f), -1);
    }

    /// <summary>Builds a throwaway definition with the two ratings the tier table reads and asks
    /// which tier it lands in — the self-check's own probe, so the boundary cases are written as
    /// numbers rather than as aircraft names.</summary>
    private static AirframeTier TierOf(float antiAir, float antiSurface, AirRole role)
    {
        AircraftDefinition definition = ScriptableObject.CreateInstance<AircraftDefinition>();
        definition.jsonKey = "TierProbe";
        definition.roleIdentity.antiAir = antiAir;
        definition.roleIdentity.antiSurface = antiSurface;
        AirframeTier tier = ForRole(definition, role);
        Object.Destroy(definition);
        return tier;
    }

    private static void Expect(string name, string actual, string expected)
    {
        if (!string.Equals(actual, expected, System.StringComparison.Ordinal))
        {
            CommanderPlugin.Log.LogError($"Enemy air buy self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }

    private static void Expect(string name, AirframeTier? actual, AirframeTier? expected)
    {
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError($"Enemy air buy self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }

    private static void Expect(string name, AirRole? actual, AirRole? expected)
    {
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError($"Enemy air buy self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }

    private static void Expect(string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError($"Enemy air buy self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }

    /// <summary>
    /// Says why no aircraft was bought. A NEW reason logs at once; a reason that keeps repeating —
    /// which is the interesting one, because it means the wing is stuck — re-logs on the holds
    /// cadence (<see cref="HoldReportEveryReviews"/>), the same as <c>ReportHold</c>. Pure
    /// once-per-reason proved to mean once per MATCH for a reason that never changes: the first
    /// ladder build logged "its strips accept no … Fighter airframe at all" once and then went
    /// silent with a 400+ fund and a whole match of unfilled CAP demand (user report,
    /// 2026-09-14). A successful buy resets the reason, so the next refusal is newsworthy again.
    /// </summary>
    private static void ReportAirDenial(FactionHQ hq, CommanderState state, string reason)
    {
        if (state.LastAirDenial == reason)
        {
            state.AirDenialReviews++;
            if (state.AirDenialReviews % HoldReportEveryReviews != 0)
            {
                return;
            }
        }
        else
        {
            state.AirDenialReviews = 0;
        }

        state.LastAirDenial = reason;
        CommanderAiLog.Note(hq, $"bought no aircraft: {reason}.");
    }

    /// <summary>
    /// The wing's standing posture: every airframe this commander bought that carries no mission at
    /// all holds <see cref="CommanderAirCommandService.AirCommandMode.AirGuard"/> over home
    /// territory — a commander with nothing overhead loses its mines and its factories to the first
    /// thing that flies over, and the player asked to be met on the way in rather than only shot at
    /// once on top of the enemy.
    /// <para>
    /// Sortie tasking lives in the operations air step; this is only the residual, and it never
    /// touches anything outside the commander's own set (decision 5: the player's Air Command
    /// missions are already mission-bound, and a stock mission's authored free aircraft carry no
    /// claim). The old StrategicStrike over the opponent's opening airbase is gone — that was the
    /// separate air brain.
    /// </para>
    /// </summary>
    private void TaskAirWing(FactionHQ hq)
    {
        ReportLostAircraft(hq);
        if (hq.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is Aircraft aircraft && !unit.disabled)
            {
                TrackAircraft(aircraft);
            }
        }

        // The residual posture is the operations air step's idle sweep now (design.md,
        // smarter-air-wing_20260914 Section 6, Reuse rule 4): the loop that used to live here did
        // the same walk with one fewer decision — it could not send a transport or a Winchester
        // airframe home — so the two were merged rather than kept side by side.
        CommanderOperationsService.SweepIdleAirframes(hq);
    }

    /// <summary>
    /// Where a commander sends its strikes: the airbase the opponent started the mission holding.
    /// </summary>
    /// <remarks>
    /// This used to be <c>GetTerritoryCenter</c> — the average position of every airbase the player
    /// holds. On a duel that starts one-base-each it is the right answer for about five minutes, and
    /// then the player takes Maris or Sandrift and the "target" slides off into open desert halfway
    /// between their bases, so a strike package flies to an empty patch of ground, finds nothing
    /// inside its area filter and goes home. The opening base does not move, the enemy is told about
    /// it at the first review (see <c>RevealPlayerBase</c>), and it is what the player means by
    /// their main base — so it is remembered once and kept. It is only re-picked if the player loses
    /// it outright.
    /// </remarks>
    private static GlobalPosition GetStrikeTarget(CommanderState state, FactionHQ opponent)
    {
        if (state.StrikeBase == null
            || state.StrikeBase.disabled
            || state.StrikeBase.center == null
            || !opponent.ContainsAirbase(state.StrikeBase))
        {
            state.StrikeBase = null;
            foreach (Airbase airbase in opponent.GetAirbases())
            {
                if (airbase != null && !airbase.disabled && airbase.center != null)
                {
                    state.StrikeBase = airbase;
                    break;
                }
            }
        }

        return state.StrikeBase != null && state.StrikeBase.center != null
            ? state.StrikeBase.center.GlobalPosition()
            : CommanderCaptureService.GetTerritoryCenter(opponent);
    }

    private void TrackAircraft(Aircraft aircraft)
    {
        if (!airborneSince.ContainsKey(aircraft))
        {
            airborneSince[aircraft] = Time.time;
        }
    }

    /// <summary>
    /// Says how long each enemy airframe lasted once it leaves the world.
    /// </summary>
    /// <remarks>
    /// The fifth playtest logged twenty-six launches over twenty minutes and the player never saw an
    /// airstrike, which the log could not explain: a launch line and then silence looks identical
    /// whether the aeroplane was shot down on the way in, flew home and was recovered into the
    /// inventory, or hit a hill. Without this line the next playtest reports the same nothing.
    /// </remarks>
    private void ReportLostAircraft(FactionHQ hq)
    {
        lostAircraft.Clear();
        foreach (KeyValuePair<Aircraft, float> entry in airborneSince)
        {
            if (entry.Key == null || entry.Key.disabled)
            {
                lostAircraft.Add(entry.Key!);
            }
        }

        for (int i = 0; i < lostAircraft.Count; i++)
        {
            Aircraft aircraft = lostAircraft[i];
            CommanderAiLog.Note(hq, $"lost an airframe after {Time.time - airborneSince[aircraft]:0} s in the air.");
            airborneSince.Remove(aircraft);
        }

        lostAircraft.Clear();
    }

    /// <summary>Aircraft this faction currently has in the world, pilots and AI alike.</summary>
    private static int CountAirborne(FactionHQ hq)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is Aircraft && !unit.disabled)
            {
                count++;
            }
        }

        return count;
    }
}
