using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The airframe buy: role choice, tier pick, launch. Split out of <c>CommanderEnemyCommanderAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past three thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
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
    /// <summary>
    /// The buy, wrapped so the log can say which half of the wing this one was for and what the
    /// previous buy of the same review did (fix, 2026-09-14). The two halves alternate, so the
    /// previous buy is normally the other side — and "the fighters are saving for a radar airframe"
    /// reads very differently depending on whether the ground attack got its aeroplane or was stuck
    /// as well. <see cref="BuyAirframeForTurn"/> is the buy itself.
    /// </summary>
    private float BuyAirframe(FactionHQ hq, CommanderState state, float budget, in ForceRead opponentForce)
    {
        string previous = state.AirLastBuyOutcome;
        state.AirBuySide = string.Empty;
        float spent = BuyAirframeForTurn(hq, state, budget, opponentForce, previous);
        string side = string.IsNullOrEmpty(state.AirBuySide) ? "the wing" : $"the {state.AirBuySide} side";
        state.AirLastBuyOutcome = spent > 0f
            ? $"{side} bought an airframe for {spent:0}"
            : $"{side} bought nothing";
        return spent;
    }

    /// <param name="previousBuy">What the previous air buy of this same review did, for the denial
    /// line; empty on the first buy of a review.</param>
    private float BuyAirframeForTurn(
        FactionHQ hq, CommanderState state, float budget, in ForceRead opponentForce, string previousBuy)
    {
        LogAirRosterOnce(hq);
        if (CountAirborne(hq) >= CommanderOperationsService.EffectiveAirborneCeiling(hq))
        {
            ReportAirDenial(hq, state, $"it is at the {CommanderOperationsService.EffectiveAirborneCeiling(hq)}-aircraft ceiling");
            return 0f;
        }

        // The sortie list drives the buy (design SS3): CAP demand first, CAS demand after — the
        // demand queue's own order — and no airframe at all when no sortie is asking. The ceiling
        // itself moved to a setting with the duel gate's removal — it applies on every mission now.
        // advanceTurn: this is the one read that is about to spend money, so it is the one that
        // moves the CAP/CAS alternation on (fix, 2026-09-14).
        // Whether the radar airframe is within reach of THIS review's fund (fix, 2026-09-14). When
        // it is not and a fighter is wanted, the turn order hands the buy to the fighter rather than
        // letting the dearest airframe in the roster hold the CAP side for the rest of the match.
        RefreshAirCatalog();
        // The radar airframe spends its own savings as well as the wing's fund (user decision
        // 2026-09-14), so that is the budget its affordability is judged against — and the budget
        // its buy is given below. The fighters never see the savings.
        float awacsBudget = budget + state.AwacsSavings;
        float cheapestAwacs = CheapestLaunchableValue(hq, state, AirRole.Awacs);
        bool awacsLaunchable = cheapestAwacs < float.MaxValue;
        bool awacsAffordable = cheapestAwacs <= awacsBudget;
        bool capAffordable = CheapestLaunchableValue(hq, state, AirRole.Fighter) <= budget;
        CommanderAirDemandKind demand = CommanderOperationsService.TryGetAirDemand(
            hq, out GlobalPosition objective, out bool wantsRotary, out string sortieLabel,
            out int sortieTrackedAircraft, out int sortieObservedHostiles, out bool homeCapDemand,
            out CommanderOperationsService.CommanderAirSortie? demandSortie,
            out ElementKind elementKind,
            advanceTurn: true,
            awacsAffordable: awacsAffordable,
            capAffordable: capAffordable,
            awacsLaunchable: awacsLaunchable);

        // Which element of the package this buy is filling, and how much of it is still empty (user
        // decision 2026-09-14). The escort slots are the CAP side's; the strike, suppression and
        // radar slots are all the other side's, which is how the sortie itself counts them.
        // Which pot this buy will be charged to, read by the caller after the buy returns.
        state.LastAirBuyWasAwacs = demand == CommanderAirDemandKind.Awacs;
        if (state.LastAirBuyWasAwacs)
        {
            budget = awacsBudget;
        }

        bool capElement = demand == CommanderAirDemandKind.Cap;
        int elementShort = 1;
        bool sortieInContact = false;
        if (demandSortie != null)
        {
            elementShort = capElement
                ? demandSortie.CapsWanted - demandSortie.Caps.Count
                : demandSortie.Wanted - demandSortie.Cas.Count;
            // A strike package's ground-attack slots are TWO elements, not one (design.md,
            // strike-packages_20260915 Section 2): the bombers are ordered as their own whole-or-
            // nothing element, and the strike airframes as theirs, or one order would price a
            // bomber and a strike jet as if they were the same aeroplane.
            if (elementKind == ElementKind.Bomber)
            {
                elementShort = demandSortie.BomberWanted - demandSortie.Cas.Count;
            }
            else if (elementKind == ElementKind.Strike)
            {
                elementShort = demandSortie.Wanted - Mathf.Max(demandSortie.Cas.Count, demandSortie.BomberWanted);
            }

            elementShort = Mathf.Max(1, elementShort);
            sortieInContact = demandSortie.InContact;
        }

        // The home patrol's growth above the strict baseline takes its turn here rather than in
        // rung 1 (user decision 2026-09-14): it is bought out of the same pot, through the same
        // CAP/CAS alternation, by the same buyer rung 1 uses — so the fighter is chosen by the
        // airframe tier rule and parked on the base's own patrol, not over an objective it has not
        // got.
        if (homeCapDemand)
        {
            state.AirBuySide = "CAP";
            state.AirDenialContext = previousBuy;
            return BuyHomeCapFighter(hq, state, budget, elementKind);
        }

        // Which half of the wing this buy is for, for the denial line. The radar airframe is a CAP
        // -side buy: it is a supporting aeroplane, not a ground-attack one.
        state.AirBuySide = demand switch
        {
            CommanderAirDemandKind.Cas or CommanderAirDemandKind.Arad => "CAS",
            CommanderAirDemandKind.Cap or CommanderAirDemandKind.Awacs => "CAP",
            _ => string.Empty,
        };
        state.AirDenialContext = previousBuy;

        // Whether a suppression sortie is waiting for its aeroplane, read ONCE for this buy (user
        // instruction 2026-09-16). While one is, no cheap airframe may take money the only
        // belt-clearing airframe on the roster needs - see CheapAirframesAdmissible. The walk is
        // the air fund's own open-demand read, not a second opinion about it (Reuse rule 4).
        CommanderOperationsService.ReadOpenAirDemandRoles(
            hq, out _, out _, out _, out _, out bool suppressionSortieWaiting);

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

        // Nothing is read here any more. The attrition hold that used to refuse every buy for a
        // bleeding side was removed on 2026-09-16 (user decision: "escalate, never hold"); losing
        // aircraft now means buying BETTER, which the tier pick below does on its own. See the header
        // of Ai/CommanderEnemyCommanderAttrition.cs for what the hold did to the air budget.

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
                demandSortie, capElement, elementShort, sortieInContact, sortieLabel,
                withinMeters: CommanderSettings.HeliCasRangeMeters, near: objective,
                elementKind: elementKind, suppressionSortieWaiting: suppressionSortieWaiting);
            if (rotarySpent > 0f)
            {
                return rotarySpent;
            }

            // The fallback fires on ANY failed rotary buy, not only on "no pad in range" (fix,
            // 2026-09-14). Gating it on the candidate walk meant a roster that HAS an attack
            // helicopter it cannot currently afford never fell back and never logged: the
            // 2026-09-14 match ran a whole match of `CAS 0/4 rotary` with not one fallback line and
            // not one strike airframe bought for a rotary sortie.
            // Logged EVERY time it happens rather than once per objective (fix, 2026-09-14): the
            // once-per-objective set hid how persistent the refusal was, and the range it quotes is
            // a setting the reader is being asked to judge — one line per review per rotary sortie
            // is what makes "raise the range" or "this roster has no attack helicopter" an obvious
            // call rather than a guess.
            if (anyRotary)
            {
                CommanderAiLog.Note(
                    hq, $"cannot afford an attack helicopter for {sortieLabel} this review; its CAS falls back to a jet.");
            }
            else
            {
                float nearestPad = NearestRotaryLaunchMeters(hq, state, objective);
                string pad = nearestPad >= float.MaxValue
                    ? "no pad at all"
                    : $"nearest pad {nearestPad / 1000f:0} km";
                CommanderAiLog.Note(
                    hq,
                    $"no attack helicopter can launch within {CommanderSettings.HeliCasRangeMeters / 1000f:0} km of "
                        + $"{sortieLabel} ({pad}); CAS falls back to a jet.");
            }

            wanted = AirRole.Strike;
        }

        float spent = TryBuyRole(
            hq, state, budget, wanted.Value, facing, sortieObjective,
            allowLastResort: false, reportDenial: false, trackedAircraft, observedHostiles,
            out bool ordinaryCandidateAtAnyPrice,
            demandSortie, capElement, elementShort, sortieInContact, sortieLabel,
            elementKind: elementKind, suppressionSortieWaiting: suppressionSortieWaiting);
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
            allowLastResort: true, reportDenial: true, trackedAircraft, observedHostiles, out _,
            demandSortie, capElement, elementShort, sortieInContact, sortieLabel,
            elementKind: elementKind, suppressionSortieWaiting: suppressionSortieWaiting);
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
        CommanderOperationsService.CommanderAirSortie? sortie = null,
        bool capElement = false,
        int elementShort = 1,
        bool inContact = false,
        string sortieLabel = "",
        float withinMeters = 0f,
        GlobalPosition? near = null,
        ElementKind elementKind = ElementKind.Other,
        bool suppressionSortieWaiting = false)
    {
        RefreshAirCatalog();
        // Which of the sortie's three element pins this buy belongs to (user decision 2026-09-14,
        // one type per element, extended to the bomber element by design.md,
        // strike-packages_20260915 Section 2: a bomber is a different element from the strike
        // airframes beside it and so pins its own type and base).
        bool bomberElement = !capElement && elementKind == ElementKind.Bomber;
        AircraftDefinition? pinnedType = sortie == null
            ? null
            : capElement ? sortie.PackageCapType
            : bomberElement ? sortie.PackageBomberType
            : sortie.PackageStrikeType;
        Airbase? pinnedBase = sortie == null
            ? null
            : capElement ? sortie.PackageCapBase
            : bomberElement ? sortie.PackageBomberBase
            : sortie.PackageStrikeBase;
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
        // One walk of the faction's aircraft before the candidate loop, not two inside it
        // (fix, 2026-09-15): the diversity cap needs the same two numbers for every candidate.
        int airborneTotal = TallyAirborneByType(hq);
        foreach (AircraftDefinition definition in airCatalog)
        {
            if (definition == null
                || !PassesRoleCapability(hq, state, definition, role)
                // The LAST RESORT gate (user decision 2026-09-13): the Cricket is invisible to the
                // buy until the roster's other airframes cannot fill the role at all.
                || (!allowLastResort && IsLastResortAirframe(definition))
                // A deliberate strike flies jets (fix, 2026-09-15): the attack helicopter's
                // anti-surface rating out-scored the jets, so every strike element was two
                // helicopters that never reached the form-up point 40 km out inside the package
                // wait — `package goes in after 180 s wait (0/2)`. Rotary CAS over a platoon is
                // untouched; this is the strike and bomber elements only.
                || (RotaryExcludedForElement(elementKind) && CommanderAirCommandService.IsRotaryAirframe(definition)))
            {
                continue;
            }

            Airbase? accepted = FindAcceptingAirbase(hq, definition, near, withinMeters);
            if (accepted == null)
            {
                continue;
            }

            AirframeTier tier = ForRole(definition, role);
            // The diversity cap (design.md, strike-packages_20260915 Section 3, user decision 3):
            // how much of this side's sky this very type already is. Counted here, once per
            // candidate, off the same faction unit list the airborne ceiling reads.
            bool overrepresented = TypeShareExceeded(
                AirborneOfType(definition), airborneTotal, CommanderSettings.TypeShareCap);
            tierCandidates.Add(new AirframeCandidate(
                tierDefinitions.Count, tier, definition.value, RoleRating(hq, state, definition, role),
                IsLastResortAirframe(definition),
                definition.roleIdentity.antiAir, definition.roleIdentity.antiSurface,
                IsBomberAirframe(definition), overrepresented));
            tierDefinitions.Add(definition);
            tierAirbases.Add(accepted);
            tierTiers.Add(tier);
        }

        sawCandidateAtAnyPrice = tierDefinitions.Count > 0;
        // The attrition read (user decision 2026-09-15, CommanderEnemyCommanderAttrition.cs): a side
        // losing more than a third of what it launches buys the best airframe in the tier it can
        // afford, exactly as a high threat does — one switch, two reasons to throw it. Read here,
        // ahead of the tier choice, from 2026-09-16: the cheap-airframe rules below stand aside for
        // it, so it has to be known before they are asked.
        bool bleeding = AttritionBleeding(hq, role, out int attritionLosses, out int attritionLaunches);
        bool threatHigh = ThreatHigh(role, trackedAircraft, observedHostiles) || bleeding;

        // Whether a cheap bottom-tier airframe is admissible for this buy at all (user instruction
        // 2026-09-16). One veto, two rules: the easy-job substitution just below, and the padding
        // that fills the slots the allocation could not cover further down.
        bool cheapAdmissible = CheapAirframesAdmissible(
            sortie != null, role, bleeding, suppressionSortieWaiting);
        bool easyJob = AirJobIsEasy(
            CommanderSettings.CheapAirframeEasyJobs,
            cheapAdmissible,
            trackedAircraft,
            observedHostiles,
            sortie?.LastAirDefence ?? 0,
            inContact);
        bool padsWithCheap = MayPadWithCheapAirframes(
            CommanderSettings.CheapAirframePadding, cheapAdmissible);

        // An escort shops the multirole tier first when one can actually launch and be paid for
        // (design Section 3). Every other element keeps the ordinary tier order untouched. An EASY
        // job then shops the BOTTOM tier ahead of whatever that chose, the escort included: an
        // objective with nothing tracked over it, no air defence near it and a picket on the ground
        // is work the cheap aeroplane is good enough for, and the money saved buys another one.
        float perSlot = budget / Mathf.Max(1, elementShort);
        AirframeTier? tierWanted = EscortTier(
            elementKind,
            TierIsLaunchable(tierTiers, AirframeTier.Multirole),
            CheapestInTier(tierCandidates, AirframeTier.Multirole) > 0f
                && CheapestInTier(tierCandidates, AirframeTier.Multirole) <= perSlot,
            HighestLaunchableTier(tierTiers, role));
        tierWanted = PreferTier(
            easyJob,
            TierIsLaunchable(tierTiers, AirframeTier.LastResort),
            CheapestInTier(tierCandidates, AirframeTier.LastResort) > 0f
                && CheapestInTier(tierCandidates, AirframeTier.LastResort) <= perSlot,
            AirframeTier.LastResort,
            tierWanted);
        int picked = -1;
        AirframeTier chosenTier = default;

        // The element this buy is serving (user decision 2026-09-14). One airframe when no sortie
        // asked — the home patrol's growth, a standing buy — and the sortie's whole remaining slot
        // count when one did, so a package of two CAS slots is chosen, priced and ordered as a pair
        // rather than one aeroplane per review.
        int elementSize = Mathf.Max(1, elementShort);

        // A pinned element re-orders its OWN type from its OWN base for as long as both still work
        // (KeepsPackageChoice), so a replacement for a loss matches the airframe beside it.
        int pinnedIndex = -1;
        Airbase? pinnedAirbase = null;
        // A bleeding side is released from its package pin: the pin exists to keep an element
        // homogeneous, and re-ordering the type that is dying to match the type that died is the
        // one thing the escalation exists to stop.
        if (bleeding)
        {
            pinnedType = null;
        }

        if (pinnedType != null)
        {
            for (int i = 0; i < tierDefinitions.Count; i++)
            {
                if (ReferenceEquals(tierDefinitions[i], pinnedType))
                {
                    pinnedIndex = i;
                    break;
                }
            }

            bool baseAccepts = pinnedBase != null
                && !pinnedBase.disabled
                && pinnedBase.center != null
                && CommanderAirCommandService.IsCompatibleAirbase(pinnedBase, hq, pinnedType)
                && pinnedBase.CanSpawnAircraft(pinnedType);
            if (KeepsPackageChoice(hasPinnedType: true, pinnedIndex >= 0, baseAccepts))
            {
                picked = pinnedIndex;
                pinnedAirbase = pinnedBase;
                chosenTier = tierTiers[pinnedIndex];
            }
        }

        if (picked < 0 && tierWanted != null)
        {
            chosenTier = tierWanted.Value;
            // The element's own allocation, not the whole one: the type chosen has to be one the
            // WHOLE element can afford, or the package is mixed the moment the second slot is filled.
            picked = SelectInTier(tierCandidates, chosenTier, threatHigh, budget / elementSize, elementKind);
            if (picked < 0 && inContact && elementSize > 1)
            {
                // A sortie under fire takes what it can get now and pins that type for the rest of
                // the element, which still makes the package homogeneous — just later.
                picked = SelectInTier(tierCandidates, chosenTier, threatHigh, budget, elementKind);
            }
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
        // Escalate, never hold (user decision 2026-09-15, revised 2026-09-16): a bleeding side buys
        // the best airframe it can afford. When that is better than the one dying it is an
        // escalation; when it is not, the commander is already flying the best it can afford and buys
        // it anyway. Only the line differs.
        if (bleeding)
        {
            NoteAttritionPick(hq, role, choice, attritionLosses, attritionLaunches);
        }

        // The base the element is ordered from: the pinned one while it stands, otherwise the
        // accepting base nearest the objective (user decision 2026-09-14, "from the same location").
        // With no objective — a standing buy — the candidate walk's own base stands.
        Airbase choiceAirbase = pinnedAirbase
            ?? (sortieObjective != null
                ? FindAcceptingAirbase(hq, choice, sortieObjective, withinMeters) ?? tierAirbases[picked]
                : tierAirbases[picked]);

        // How much of the element this review can actually order (design: the whole element or
        // none, unless the sortie is in contact) - or, when padding is allowed, as many of the proper
        // type as the allocation covers, with the slots it could not cover filled cheaply below
        // (user instruction 2026-09-16). The proper count is taken FIRST, so padding can never
        // displace an airframe this sortie could otherwise have afforded.
        int orders = ElementProperBuys(elementSize, choice.value, budget, inContact, padsWithCheap);
        int paddingSlots = ElementPaddingSlots(elementSize, orders, padsWithCheap);
        if (orders <= 0)
        {
            if (reportDenial)
            {
                // With padding on, this denial means something narrower than it used to: not "the
                // allocation cannot cover the whole element" but "it cannot cover even one of the
                // proper airframe", because the slots it could not cover would have been padded.
                ReportAirDenial(
                    hq,
                    state,
                    padsWithCheap
                        ? $"its allocation cannot cover even one airframe of the {elementSize}-airframe "
                            + $"element ({choice.unitName} at {choice.value:0} each, allocation "
                            + $"{budget:0}); it saves rather than padding an element it has not started"
                        : $"its allocation cannot cover the whole {elementSize}-airframe element "
                            + $"({choice.unitName} at {choice.value:0} each, allocation {budget:0}); "
                            + "it saves rather than splitting the package across two types");
            }

            return 0f;
        }

        // Say so, once, when the strips this commander holds cannot launch the tier the role really
        // wants (design Section 4). Without this line a wing flying Compasses on CAP looks like a
        // broken rule rather than a map position — which is exactly how the 2026-09-14 highway-strip
        // report read before the tiers existed.
        // Not for a tier the buy CHOSE (design.md, strike-packages_20260915 Section 3, widened on
        // 2026-09-16 to the easy job): this line exists to say a commander's strips cannot launch the
        // tier the role wanted, and reporting a deliberate preference - the escort's multirole, the
        // easy job's cheap airframe - as a shortage would read as a broken rule.
        bool deliberateTier =
            (elementKind == ElementKind.Escort && chosenTier == AirframeTier.Multirole)
            || (easyJob && chosenTier == AirframeTier.LastResort);
        if (!deliberateTier)
        {
            ReportTierFallback(hq, state, role, chosenTier);
        }

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
            role is AirRole.Strike or AirRole.RotaryCas or AirRole.Arad ? "observed" : "in the sky",
            bleeding ? $"attrition, {attritionLosses} of {attritionLaunches} lost" : null);
        // Which element this was for, and whether the diversity cap is what moved the choice
        // (design.md, strike-packages_20260915 Section 3). The share is named in the LOG only; no
        // rule anywhere reads an aircraft name.
        bool skippedOverrepresented = !tierCandidates[picked].ShareExceeded
            && AnyOverrepresentedInTier(tierCandidates, chosenTier, budget / elementSize);
        note += ElementChoiceNote(
            elementKind, choice, skippedOverrepresented, AirborneOfType(choice), airborneTotal);
        if (easyJob && chosenTier == AirframeTier.LastResort)
        {
            note += "; easy job: nothing tracked over the objective, no air defence near it and "
                + $"{observedHostiles} observed, so the cheap airframe takes it";
        }

        if (IsLastResortAirframe(choice))
        {
            note += "; last resort: nothing else can fill the role";
        }

        if (CommanderAirCommandService.LoadoutHasPreferredCasOrdnance(forced))
        {
            note += "; AGM-68/AGM-48 loadout";
        }

        // The suppression load, named and counted, on the line that launches it (user decision
        // 2026-09-14). The reader's only way to tell a suppression sortie that is armed for the job
        // from one that is not was the roster line's guess at what the builder WOULD hang.
        if (role == AirRole.Arad)
        {
            string aradStores = CommanderAirCommandService.DescribeAradWeapons(forced, withCounts: true);
            note += string.IsNullOrEmpty(aradStores)
                ? "; NO anti-radiation store on the pylons"
                : $"; anti-radiation loadout: {aradStores}";
        }

        // The whole element leaves together and is announced as one order (user decision
        // 2026-09-14), so a reader sees a package being formed rather than a run of unrelated buys.
        if (orders > 1)
        {
            CommanderAiLog.Note(
                hq,
                $"orders {orders}x {choice.unitName} from {choiceAirbase.name} for {sortieLabel} "
                    + $"({(capElement ? "package escort element" : "package strike element")}).");
        }

        float spent = 0f;
        int launched = 0;
        for (int order = 0; order < orders; order++)
        {
            // The ceiling is every faction's aircraft, the player's included, so it is read before
            // each airframe of the element rather than once for the order.
            if (CountAirborne(hq) >= CommanderOperationsService.EffectiveAirborneCeiling(hq))
            {
                break;
            }

            // A fresh loadout per airframe: one Loadout object handed to two aircraft is one
            // network-synced reference shared between them.
            Loadout? loadoutForThis = order == 0
                ? forced
                : (role == AirRole.Transport ? null : BuildRoleLoadout(hq, choice, role));
            float cost = LaunchBoughtAirframe(
                hq, state, choiceAirbase, choice, facing, sortieObjective,
                loadoutForThis, role, forHomeCap: false,
                orders > 1 ? $" ({note}; {order + 1} of {orders} in the element)." : $" ({note}).");
            if (cost <= 0f)
            {
                break;
            }

            spent += cost;
            launched++;
        }

        // The slots the allocation could not cover at the proper tier are filled with the cheapest
        // thing that can do the job rather than left empty (user instruction 2026-09-16). It spends
        // only what is LEFT after the proper airframes have flown, and it fills nothing when none of
        // them did: padding pads an element, it does not replace one.
        if (paddingSlots > 0 && launched > 0)
        {
            spent += PadElementWithCheapAirframes(
                hq, state, role, facing, sortieObjective, choice, paddingSlots, budget - spent,
                sortieLabel, withinMeters);
        }

        // Pin the element to what actually flew, not to what was chosen: an order that launched
        // nothing must leave the next review free to pick again.
        if (launched > 0 && sortie != null)
        {
            if (capElement)
            {
                sortie.PackageCapType = choice;
                sortie.PackageCapBase = choiceAirbase;
            }
            else if (bomberElement)
            {
                sortie.PackageBomberType = choice;
                sortie.PackageBomberBase = choiceAirbase;
            }
            else
            {
                sortie.PackageStrikeType = choice;
                sortie.PackageStrikeBase = choiceAirbase;
            }
        }

        return spent;
    }

    /// <summary>
    /// Fills an element's remaining slots with the cheapest role-capable airframe the leftover
    /// allocation covers (user instruction 2026-09-16: cheap aircraft "pad out sorties that cannot
    /// afford full strength"). Returns what it spent.
    /// <para>
    /// It reads the SAME candidate list the element itself was chosen from, so the faction roster
    /// split, the role capability gate, the accepting-strip test and the diversity cap all apply to a
    /// padding airframe exactly as they apply to a proper one; and it launches through the same
    /// <see cref="LaunchBoughtAirframe"/> tail, so the airborne ceiling, the charge, the claim that
    /// binds the airframe into the sortie and the attrition ledger are all the existing ones.
    /// </para>
    /// <para>
    /// It deliberately breaks the element's one-type rule, which is the price of the instruction: a
    /// mixed pair that flies beats a matched pair that was never bought. The element's PIN is not
    /// touched, so the next review still tops the element up with the proper type.
    /// </para>
    /// </summary>
    private float PadElementWithCheapAirframes(
        FactionHQ hq,
        CommanderState state,
        AirRole role,
        GlobalPosition facing,
        GlobalPosition? sortieObjective,
        AircraftDefinition properChoice,
        int slots,
        float leftover,
        string sortieLabel,
        float withinMeters)
    {
        float spent = 0f;
        string forSortie = string.IsNullOrEmpty(sortieLabel) ? "the element" : sortieLabel;
        for (int slot = 0; slot < slots; slot++)
        {
            // The ceiling is read before every padding airframe exactly as it is before every proper
            // one: this rule fills slots the wing had already asked for, and it must never be the
            // thing that takes the last place under the ceiling.
            if (CountAirborne(hq) >= CommanderOperationsService.EffectiveAirborneCeiling(hq))
            {
                break;
            }

            int picked = CheapestPaddingIndex(tierCandidates, leftover - spent);
            if (picked < 0)
            {
                break;
            }

            AircraftDefinition padding = tierDefinitions[picked];
            Airbase paddingAirbase = sortieObjective != null
                ? FindAcceptingAirbase(hq, padding, sortieObjective, withinMeters) ?? tierAirbases[picked]
                : tierAirbases[picked];
            float cost = LaunchBoughtAirframe(
                hq,
                state,
                paddingAirbase,
                padding,
                facing,
                sortieObjective,
                BuildRoleLoadout(hq, padding, role),
                role,
                forHomeCap: false,
                $" (pads the {properChoice.unitName} element for {forSortie}: slot {slot + 1} of "
                    + $"{slots} the allocation could not cover at full strength).");
            if (cost <= 0f)
            {
                break;
            }

            spent += cost;
        }

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

    /// <summary>Whether the chosen tier held an affordable candidate the diversity cap skipped — what
    /// tells the launch line that the cap is the reason this airframe and not that one.</summary>
    private static bool AnyOverrepresentedInTier(
        List<AirframeCandidate> candidates, AirframeTier tier, float allocation)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Tier == tier && candidates[i].ShareExceeded && candidates[i].Price <= allocation)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any candidate this pass collected sits in one tier — the escort's "can a
    /// multirole actually launch" read (<see cref="EscortTier"/>).</summary>
    private static bool TierIsLaunchable(List<AirframeTier> tiers, AirframeTier tier)
    {
        for (int i = 0; i < tiers.Count; i++)
        {
            if (tiers[i] == tier)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How many airframes of EACH type this faction currently has in the world, and how many it has
    /// in total — the diversity cap's two numbers (design.md, strike-packages_20260915 Section 3).
    /// <see cref="CountAirborne"/>'s own walk with a tally hung off it, so the share and the total it
    /// is a share OF are counted in one pass and can never be counted differently (Reuse rule 4).
    /// <para>
    /// One walk per BUY, not one per candidate (fix, 2026-09-15). The candidate loop runs over the
    /// whole aircraft catalogue and the faction's unit list is hundreds of entries long on a busy
    /// match, so counting inside it walked that list twice for every airframe in the game, every
    /// buy, every review.
    /// </para>
    /// </summary>
    private int TallyAirborneByType(FactionHQ hq)
    {
        airborneByType.Clear();
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int total = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit is not Aircraft || unit.disabled)
            {
                continue;
            }

            total++;
            if (unit.definition is not AircraftDefinition definition)
            {
                continue;
            }

            airborneByType.TryGetValue(definition, out int count);
            airborneByType[definition] = count + 1;
        }

        return total;
    }

    /// <summary>How much of the side's sky one type is, read out of the tally above.</summary>
    private int AirborneOfType(AircraftDefinition definition)
    {
        return airborneByType.TryGetValue(definition, out int count) ? count : 0;
    }

    /// <summary>
    /// The plain-words half of a launch line that says which element a buy was for and, when the
    /// diversity cap actually moved the choice, that it did (design Section 3). Empty for an
    /// ordinary buy, so nothing changes on the lines that had no element to name.
    /// </summary>
    private static string ElementChoiceNote(
        ElementKind kind,
        AircraftDefinition choice,
        bool skippedOverrepresented,
        int airborneOfChoice,
        int airborneTotal)
    {
        string element = kind switch
        {
            ElementKind.HighCap => "; home patrol: most air-to-air per credit",
            ElementKind.Escort => "; escort: multirole preferred",
            ElementKind.Strike => "; strike element: most anti-surface affordable",
            ElementKind.Bomber => "; bomber element",
            _ => string.Empty,
        };

        if (!skippedOverrepresented)
        {
            return element;
        }

        int total = Mathf.Max(1, airborneTotal);
        int share = Mathf.RoundToInt(100f * airborneOfChoice / total);
        return element
            + $"; diversity: another type was past {CommanderSettings.TypeShareCap * 100f:0} percent of the sky, "
            + $"so {choice.unitName} was bought instead ({share} percent)";
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
        AirRole role,
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
        // The role rides along so the claim can count the launch on the right side of the
        // attrition ledger (user decision 2026-09-15, see CommanderEnemyCommanderAttrition.cs).
        CommanderOperationsService.RecordCommanderLaunch(hq, choice, airbase, sortieObjective, role, forHomeCap);

        if (!CommanderAirCommandService.TryLaunchAiAircraft(hq, airbase, choice, livery, loadout, fuel, facing))
        {
            CommanderOperationsService.ClearCommanderLaunch(hq);
            return 0f;
        }

        float cost = Mathf.Max(0f, choice.value);
        hq.AddFunds(-cost);
        RecordPurchase(hq);
        state.LastAirDenial = string.Empty;

        // The launch line says the cannon is empty (user, 2026-09-14), because "it flew off and
        // strafed something" is otherwise indistinguishable from "the rule did not run". A loadout
        // that still carries a gun mount at this point is a bug in whichever path built it, and it
        // reports as a named self-check failure rather than quietly taking off armed.
        string cannonNote = " (cannon 0 rounds)";
        if (CommanderSettings.AirIncludeInternalCannons)
        {
            cannonNote = " (cannon loaded: INTERNAL CANNONS is on)";
        }
        else if (CommanderAirCommandService.LoadoutHasGunMount(loadout))
        {
            CommanderPlugin.Log.LogError(
                "Enemy air buy self-check FAILED (the role loadout builder never returns a gun mount "
                    + $"while INTERNAL CANNONS is off): {choice.unitName} launched carrying one.");
            cannonNote = " (cannon MOUNT PRESENT — see the self-check failure above)";
        }

        CommanderAiLog.Note(
            hq,
            $"launched a {choice.unitName} ({GetAirRole(choice)}) from {airbase.name} ({CountOnDeckNear(hq, airbase)} waiting on the deck) for {cost:0}{cannonNote}{noteSuffix}");
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
    /// <param name="elementKind">The home patrol's own element (design.md,
    /// strike-packages_20260915 Section 3): the most air-to-air per credit, so a commander with money
    /// fields more of a good cheap fighter rather than one dear one.</param>
    private float BuyHomeCapFighter(
        FactionHQ hq, CommanderState state, float budget, ElementKind elementKind = ElementKind.HighCap)
    {
        LogAirRosterOnce(hq);
        RefreshAirCatalog();
        // The exemption that used to live here is gone with the thing it worked around (2026-09-16).
        // The attrition hold refused every fighter, which deadlocked the ladder against rung 1's
        // "buy nothing below the home patrol's baseline" rule and left the commander sitting on a
        // balance of 1,100 buying nothing at all; the baseline was exempted from the hold to break
        // it. There is no hold now, so there is nothing to exempt: a bleeding side escalates its
        // pick and buys, here and everywhere else.

        GlobalPosition facing = CommanderCaptureService.GetTerritoryCenter(hq);

        tierCandidates.Clear();
        tierDefinitions.Clear();
        tierAirbases.Clear();
        tierTiers.Clear();
        int airborneTotal = TallyAirborneByType(hq);
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

            // The base nearest the middle of what this commander holds, not the first one in the
            // list (fix, 2026-09-14): every home-CAP fighter of the 2026-09-14 match launched from
            // airbase_boscali_north while the commander held three other strips, so the patrol over
            // a captured forward field was always flown in from the back of the map. The sortie
            // buyer has picked the nearest accepting base to its objective since 2026-09-14; this
            // is the same rule for the buy that has no objective, anchored on the territory the
            // patrol exists to cover.
            Airbase? accepted = FindAcceptingAirbase(hq, definition, facing);
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
                tierDefinitions.Count, tier, definition.value, definition.roleIdentity.antiAir, lastResort,
                definition.roleIdentity.antiAir, definition.roleIdentity.antiSurface,
                IsBomberAirframe(definition),
                TypeShareExceeded(
                    AirborneOfType(definition), airborneTotal, CommanderSettings.TypeShareCap)));
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
        // ... and the attrition read, the same switch the sortie buyer throws (user decision
        // 2026-09-15, CommanderEnemyCommanderAttrition.cs).
        bool bleeding = AttritionBleeding(hq, AirRole.Fighter, out int attritionLosses, out int attritionLaunches);
        bool threatHigh = ThreatHigh(AirRole.Fighter, trackedAircraft, observedHostiles: 0) || bleeding;
        int picked = SelectInTier(tierCandidates, tierWanted.Value, threatHigh, budget, elementKind);
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
        if (bleeding)
        {
            NoteAttritionPick(hq, AirRole.Fighter, best, attritionLosses, attritionLaunches);
        }

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
            + TierChoiceNote(
                hq, state, best, AirRole.Fighter, threatHigh, trackedAircraft, "in the sky",
                bleeding ? $"attrition, {attritionLosses} of {attritionLaunches} lost" : null)
            + ElementChoiceNote(
                elementKind,
                best,
                !tierCandidates[picked].ShareExceeded
                    && AnyOverrepresentedInTier(tierCandidates, tierWanted.Value, budget),
                AirborneOfType(best),
                airborneTotal);
        if (loadout != null && LoadoutHasArhMissile(loadout))
        {
            note += ", active-radar loadout";
        }

        return LaunchBoughtAirframe(
            hq, state, bestAirbase, best, facing, null, loadout, AirRole.Fighter, forHomeCap: true, $" ({note}).");
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
}
