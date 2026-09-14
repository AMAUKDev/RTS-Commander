using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// An opposing commander that buys reinforcements out of <c>factionFunds</c> — the same pot the
/// player's depot buys out of, under the same rules. The Basegame only ever deploys the vehicle
/// reserve a mission was authored with and never buys anything, so a faction that converts income
/// into reinforcements is the difference between a sandbox and a game.
/// </summary>
/// <remarks>
/// The design goal is a fair duel: in MATCHED mode both commanders start on the same balance and
/// earn at the same rate, and neither gets a stipend, so the only thing that separates them is
/// what they spend it on. That choice is the <see cref="EnemyPlan"/> — the enemy reads what the
/// player is fielding and commits to the plan that counters it, which the player then has to
/// counter back.
///
/// ponytail: this buys and shapes the composition, then hands the units to the Basegame depot
/// deployment and its objective-seeking ground AI rather than issuing routes of its own.
/// Upgrade to real orders only once buying alone stops being enough of a fight.
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService : ICommanderTickPersistent, ICommanderResetSession
{
    internal const int ModeOff = 0;
    internal const int ModeMatched = 1;
    internal const int ModeMission = 2;

    private const float ReviewIntervalSeconds = 30f;

    /// <summary>
    /// Share of the pot spent per review. Scale-free on purpose: faction funds are denominated in
    /// millions and every mission authors its own balance, so an absolute reserve floor cannot be
    /// calibrated. A quarter every 30 s is the tempo knob, not an advantage — it is what stops the
    /// commander dumping its whole balance the moment the mission starts.
    /// </summary>
    private const float SpendFraction = 0.25f;
    private const int PurchasesPerReview = 3;

    /// <summary>
    /// Reviews a new plan has to stay indicated before the commander switches to it. Without this
    /// the enemy re-counters inside one review and the player never gets to cash in a counter.
    /// </summary>
    private const int PlanCommitReviews = 2;

    /// <summary>
    /// The mod's own duel map is the one mission where the enemy commander is not an option the
    /// player has to find in a menu — it is the opponent, so it runs whether or not the setting is
    /// on and it plays harder. These are the knobs for "harder": a bigger opening balance, a
    /// faster spend, more purchases per review, and an air force that is allowed to be bigger than
    /// the mission authored. Everything else is still the same economy the player is on.
    /// </summary>
    private const float DuelHeadStart = 1.5f;
    private const float DuelSpendFraction = 0.45f;
    private const int DuelPurchasesPerReview = 5;

    /// <summary>Purchases per review while an operations attack requisition is open, in place of
    /// the 3 / 5 the tempo knob normally uses (design SS4) — an attack asking for bodies needs more
    /// than one purchase a review to actually field them before the pressure clock fires again.</summary>
    private const int OffensivePurchasesPerReview = 5;

    /// <summary>
    /// Share of the balance the unit spender may draw on even while the economy service is saving
    /// for a structure. Structures cost 250–500+ and vehicles 7–13, so "balance minus the whole
    /// structure reserve" was zero or negative for every review of a commander with a long build
    /// list — the player's own commander bought nothing for a whole match while its balance sat in
    /// the hundreds. A quarter keeps the balance climbing (income per review outruns a quarter of
    /// it times the tempo fraction) while never leaving the platoons unfunded.
    /// </summary>
    private const float UnitSpendFloorShare = 0.25f;

    /// <summary>Skipped buy reviews between two "holds" log lines. Every review would be noise;
    /// never would leave a silent commander looking broken, which is how this constant came to be.</summary>
    private const int HoldReportEveryReviews = 4;

    /// <summary>Share of one review's budget set aside for the air fund — an air force must not
    /// starve the convoys. Was the duel-only <c>DuelAirframeBudgetShare</c>; the duel gate on the
    /// air leg is gone, so the share lost its prefix and kept its value.</summary>
    private const float AirframeBudgetShare = 0.4f;

    private readonly Dictionary<FactionHQ, CommanderState> states = new();

    /// <summary>HQs whose airframe list has already been written to the log. See LogAirRosterOnce.</summary>
    private readonly HashSet<FactionHQ> loggedAirRoster = new();

    /// <summary>HQs whose ground vehicle list has already been written to the log. See CollectCatalog.</summary>
    private readonly HashSet<FactionHQ> loggedGroundRoster = new();

    /// <summary>Airframes the enemy has in the air, and when each entered it. See ReportLostAircraft.</summary>
    private readonly Dictionary<Aircraft, float> airborneSince = new();
    private readonly List<Aircraft> lostAircraft = new();

    private readonly List<FactionHQ> staleHqs = new();
    private readonly List<VehicleDefinition> catalog = new();
    private readonly List<VehicleDefinition> candidates = new();

    /// <summary>Order-book roles for the current purchase, best first, and what this review has
    /// already bought per role; both reused across reviews rather than allocated each time.</summary>
    private readonly List<CommanderPlatoonRole> openRoles = new();
    private readonly int[] boughtThisReview = new int[CommanderOperationsService.RoleCount];
    private float nextReviewAt;
    private float nextDefenceAt;

    internal static CommanderEnemyCommanderService? Instance { get; private set; }

    /// <summary>True on the mission the mod ships, which plays itself.</summary>
    internal static bool IsDuelMission =>
        CommanderFeatureGate.MissionName.IndexOf("Ground Control Duel", System.StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// The mode actually in force. The duel forces MATCHED when the setting is off, so the built-in
    /// map has an opponent out of the box; every other mission still does exactly what the setting says.
    /// </summary>
    internal static int EffectiveMode
    {
        get
        {
            int mode = Mathf.Clamp(CommanderSettings.EnemyCommanderMode, ModeOff, ModeMission);
            return mode == ModeOff && IsDuelMission ? ModeMatched : mode;
        }
    }

    internal CommanderEnemyCommanderService()
    {
        Instance = this;
        nextReviewAt = 0f;
    }

    /// <summary>Units this session's enemy commanders have bought, for the settings readout. Enemy
    /// commanders only: what the player's own commander bought is counted separately, so the ENEMY
    /// COMMANDER button keeps reading what it always read.</summary>
    internal int TotalPurchases { get; private set; }

    /// <summary>Units the player's own commander has bought this session, for the PLAYER COMMANDER
    /// button.</summary>
    internal int PlayerPurchases { get; private set; }

    /// <summary>Plan and balance of the best-funded enemy commander, for the HUD readout.</summary>
    internal string StatusLine { get; private set; } = string.Empty;

    /// <summary>Plan and balance of the player's own commander, for the second HUD row. Empty
    /// whenever the player commander switch is off, which is what hides that row.</summary>
    internal string PlayerStatusLine { get; private set; } = string.Empty;

    /// <summary>Books a purchase against whichever commander made it. One definition so the two
    /// readouts can never drift apart.</summary>
    private void RecordPurchase(FactionHQ hq)
    {
        if (ReferenceEquals(hq, CommanderGameAccess.GetLocalHq()))
        {
            PlayerPurchases++;
            return;
        }

        TotalPurchases++;
    }

    internal static string GetModeLabel(int mode)
    {
        return mode switch
        {
            ModeMatched => "MATCHED",
            ModeMission => "MISSION FUNDS",
            _ => "OFF",
        };
    }

    internal static string GetPlanLabel(EnemyPlan plan)
    {
        return plan switch
        {
            EnemyPlan.AirDefence => "AIR DEFENCE",
            EnemyPlan.FireSupport => "FIRE SUPPORT",
            EnemyPlan.Spearhead => "SPEARHEAD",
            _ => "RECON SCREEN",
        };
    }

    /// <summary>The COMMANDER LOG header's PLAN readout for a given HQ, "NONE" before its first
    /// review has ever run.</summary>
    internal string GetPlanLabel(FactionHQ hq)
    {
        return states.TryGetValue(hq, out CommanderState state) ? GetPlanLabel(state.Plan) : "NONE";
    }

    /// <summary>
    /// One runnable check for the counter triangle, run once at plugin load next to
    /// <see cref="CommanderServiceRegistryCheck"/>, because a Unity plugin has nowhere else to
    /// run a test. If a threshold in <see cref="ChoosePlan"/> is retuned and a whole plan stops
    /// being reachable, this is what says so in the BepInEx console.
    /// </summary>
    internal static void SelfCheck()
    {
        CheckPlan("air wing", new ForceRead { Aircraft = 4, Ground = 2, Armour = 2 }, EnemyPlan.AirDefence);
        CheckPlan("armoured push", new ForceRead { Armour = 6, Ground = 8 }, EnemyPlan.FireSupport);
        CheckPlan("static line", new ForceRead { Guns = 2, AirDefence = 3, Ground = 6 }, EnemyPlan.Spearhead);
        CheckPlan("nothing dominant", new ForceRead { Ground = 2, Armour = 1, Guns = 1 }, EnemyPlan.ReconScreen);
        CheckPlan("empty field", default, EnemyPlan.ReconScreen);
        CheckAirRoles();
        CheckDefencePosture();
    }

    private static void CheckPlan(string name, in ForceRead force, EnemyPlan expected)
    {
        EnemyPlan actual = ChoosePlan(force);
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError(
                $"Enemy plan self-check FAILED ({name}): expected {GetPlanLabel(expected)}, got {GetPlanLabel(actual)}.");
        }
    }

    public void TickPersistent()
    {
        int mode = EffectiveMode;

        // The HQ check comes before the schedule on purpose. This ticks from the menu onwards, and
        // consuming a review while no mission is loaded is what put the first purchase up to a
        // review behind the start of the match — the enemy has to be spending from minute zero.
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (!CommanderPlayerCommanderService.AnyCommanderOn || localHq == null)
        {
            // Cleared here as well as by PruneStates: with the enemy commander OFF the review loop
            // below never runs again once the player switch goes off, so without this the YOU row
            // would keep showing the last plan for the rest of the mission. StatusLine is left
            // alone on purpose — the ENEMY row's behaviour is unchanged.
            PlayerStatusLine = string.Empty;
            return;
        }

        // The home guard runs on its own, faster clock. An attack the commander can see coming has
        // to move units before it lands, and the buy review is half a minute wide.
        if (CommanderScheduler.IsDue(ref nextDefenceAt, DefenceReviewIntervalSeconds))
        {
            ReviewDefences(localHq);
        }

        if (!CommanderScheduler.IsDue(ref nextReviewAt, ReviewIntervalSeconds))
        {
            return;
        }

        FactionHQ? primary = null;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null
                || !CommanderPlayerCommanderService.IsCommanded(hq, localHq)
                || !hq.IsServer
                || hq.faction == null)
            {
                continue;
            }

            // Read per commander rather than once before the loop: every commander but the player's
            // is playing the local HQ, so this is the same struct it used to be, but the player's
            // own commander has to read whoever it is actually up against.
            FactionHQ opponent = CommanderPlayerCommanderService.ChooseOpponent(hq, localHq);
            ForceRead opponentForce = ReadForce(opponent);
            Review(hq, localHq, opponent, mode, opponentForce);

            // The ENEMY row stays about the enemy. The player's own commander has its own HUD row,
            // so it must never win this pick and hide the plan the player is supposed to counter.
            if (!ReferenceEquals(hq, localHq) && (primary == null || hq.factionFunds > primary.factionFunds))
            {
                primary = hq;
            }
        }

        PruneStates(localHq);
        StatusLine = primary != null && states.TryGetValue(primary, out CommanderState primaryState)
            ? $"{GetPlanLabel(primaryState.Plan)}   {FundsLabel(primary.factionFunds)}"
                + (primaryState.Defending ? "   DEFENDING" : string.Empty)
            : string.Empty;

        // PruneStates has just dropped the local HQ's state unless the player commander is on, so
        // this row empties itself the review after the switch goes off.
        PlayerStatusLine = states.TryGetValue(localHq, out CommanderState playerState)
            ? $"{GetPlanLabel(playerState.Plan)}   {FundsLabel(localHq.factionFunds)}"
                + (playerState.Defending ? "   DEFENDING" : string.Empty)
            : string.Empty;
    }

    public void ResetSession()
    {
        states.Clear();
        loggedAirRoster.Clear();
        loggedGroundRoster.Clear();
        airborneSince.Clear();
        lostAircraft.Clear();
        staleHqs.Clear();
        catalog.Clear();
        candidates.Clear();
        reconUnits.Clear();
        shipCatalog.Clear();
        defenceCandidates.Clear();
        staleDefenders.Clear();
        TotalPurchases = 0;
        PlayerPurchases = 0;
        StatusLine = string.Empty;
        PlayerStatusLine = string.Empty;
        nextReviewAt = 0f;
        nextDefenceAt = 0f;
    }

    /// <summary>
    /// This commander's whole review: economy prep, plan, posture, then spend. With an operations
    /// order book open the buy loop spends on what its missions asked for; with the book empty it
    /// buys the plan-based counter triangle exactly as before (design SS4).
    /// </summary>
    private void Review(FactionHQ hq, FactionHQ localHq, FactionHQ opponent, int mode, in ForceRead opponentForce)
    {
        if (!states.TryGetValue(hq, out CommanderState state))
        {
            state = new CommanderState();
            states[hq] = state;
        }

        bool duel = IsDuelMission;
        bool isLocal = ReferenceEquals(hq, localHq);
        if (!state.Prepared)
        {
            // Both openers are about putting an opposing faction on the player's economy, so both
            // are skipped for the player's own commander: no matched balance copied onto itself, no
            // duel head start. The switch hands the player a staff officer, not a different mission.
            if (!isLocal)
            {
                if (mode == ModeMatched)
                {
                    LevelEconomy(hq, localHq);
                }
                if (duel)
                {
                    PrepareDuel(hq);
                }
            }
            else
            {
                CommanderAiLog.Note(hq, "keeps the player's own economy: no head start, no fund reset.");
            }

            state.Prepared = true;
        }

        if (duel)
        {
            RevealPlayerBase(hq, opponent);
        }

        UpdatePlan(hq, state, opponentForce);

        // Posture comes before spending on purpose: a commander with an empty balance still has to
        // fly the aircraft and drive the radars it already owns.
        ReviewPosture(hq, opponent, state, opponentForce);

        // Whatever the economy service is saving for is off limits here. Both spenders draw on the
        // one factionFunds pool, and this one takes a fixed share of the balance every review — so
        // without the hold-back the balance never climbed to a factory or a dock and the enemy
        // commander built nothing but its opening mines all match.
        float reserve = CommanderEconomyService.GetEnemyBuildReserve(hq);
        float pot = Mathf.Max(hq.factionFunds - reserve, hq.factionFunds * UnitSpendFloorShare);
        // An open attack requisition raises the tempo for this review only (design SS4): an attack
        // waiting on bodies needs more of the pot than the duel/matched tempo knob normally allows.
        float fraction = CommanderOperationsService.HasOpenAttackRequisition(hq)
            ? CommanderSettings.OperationsOffensiveSpendFraction
            : (duel ? DuelSpendFraction : SpendFraction);
        float spendable = pot * fraction;
        CollectCatalog(hq);
        if (spendable <= 0f || catalog.Count == 0)
        {
            ReportHold(hq, state, reserve, spendable);
            return;
        }

        int boughtCount = ReviewPurchases(hq, state, duel, opponent, opponentForce, spendable);
        if (boughtCount == 0)
        {
            ReportHold(hq, state, reserve, spendable);
        }
        else
        {
            state.SkippedBuyReviews = 0;
        }
    }

    /// <summary>
    /// One log line every <see cref="HoldReportEveryReviews"/> reviews in which the commander bought
    /// no vehicle, saying why in numbers: the balance, what the economy service is saving for, what
    /// was left for units, and how many vehicle types were on offer. Diagnostic only.
    /// </summary>
    private void ReportHold(FactionHQ hq, CommanderState state, float reserve, float spendable)
    {
        state.SkippedBuyReviews++;
        if (state.SkippedBuyReviews % HoldReportEveryReviews != 1)
        {
            return;
        }

        CommanderAiLog.Note(
            hq,
            $"holds: balance {hq.factionFunds:0}, saving {reserve:0} for structures, unit budget {spendable:0}, "
                + $"{catalog.Count} vehicle types on offer ({state.SkippedBuyReviews} quiet reviews).");
    }

    /// <summary>The buy loop proper. Returns how many vehicles it bought this review.</summary>
    private int ReviewPurchases(FactionHQ hq, CommanderState state, bool duel, FactionHQ opponent, in ForceRead opponentForce, float spendable)
    {
        int boughtCount = 0;

        // Whatever the plan says, a commander with no air defence at all while the player is
        // flying is not playing the same game. One launcher first, then back to the plan.
        bool blindToAir = opponentForce.Aircraft > 0 && CountAirDefence(hq) == 0;
        int purchases = CommanderOperationsService.HasOpenAttackRequisition(hq)
            ? OffensivePurchasesPerReview
            : (duel ? DuelPurchasesPerReview : PurchasesPerReview);
        // The air share is set aside rather than spent-or-lost. An airframe costs several ground
        // vehicles, so a flat slice of a pot the commander keeps draining on convoys never once
        // added up to an aircraft after the opening minutes — which is exactly how an enemy that
        // flew at the start ended up with no air force at all. No longer duel-only (user answer
        // 2026-09-13): every commanded HQ accrues a fund and fields its wing, and what it buys
        // flies the sorties the ground plan asked for. The ceiling keeps a flush commander from
        // hoarding, but never sits below one review's buys of the dearest fighter (2026-09-13).
        float airShare = spendable * AirframeBudgetShare;
        spendable -= AccrueFund(ref state.AirFund, airShare, AirFundCeiling(airShare, DearestFighterPrice(hq)));

        // The once-per-review demand read (design SS4, chatty detail behind OperationsDebugLog):
        // what the wing is short of and what caps it, so a quiet sky in a rich match is explained
        // by one line instead of silence.
        if (CommanderSettings.OperationsDebugLog && hq.faction != null)
        {
            CommanderOperationsService.ReadAirDemandCounts(hq, out int capBound, out int capWanted, out int casBound, out int casWanted);
            CommanderPlugin.Log.LogInfo(
                $"Ops {CommanderPlayerCommanderService.CommanderLabel(hq)}: air demand: CAP {capBound}/{capWanted}, "
                    + $"CAS {casBound}/{casWanted}, ceiling {CountAirborne(hq)}/{CommanderSettings.AirborneCeiling}, "
                    + $"fund {state.AirFund:0}.");
        }

        // Buy while the air fund covers the next wanted airframe and the ceiling allows, bounded by
        // MaxAirBuysPerReview (user decision 2026-09-13: the one-airframe-per-review throttle left
        // five short sorties waiting half the match). A buy that comes back empty — fund short,
        // ceiling reached, nothing fits the strip — ends the loop with its once-per-reason line.
        for (int airBuy = 0; AirBuyContinues(airBuy); airBuy++)
        {
            float airSpent = BuyAirframe(hq, state, state.AirFund, opponentForce);
            if (airSpent <= 0f)
            {
                break;
            }

            state.AirFund -= airSpent;
        }

        spendable -= ReviewNaval(hq, opponent, state, spendable * NavalBudgetShare);

        // An expansion with nothing that can take ground is an expansion that never happens, so a
        // capture unit outranks the plan exactly the way the first air-defence launcher does.
        bool needsCaptureUnit = CommanderCaptureService.Instance?.WantsCaptureUnit(hq) == true;
        bool hasOpenBook = CommanderOperationsService.HasOpenRequisition(hq);
        bool groundCapped = CommanderOperationsService.GroundBuyingCapped(
            CommanderOperationsService.OwnsGroundForce(hq),
            CommanderOperationsService.PlatoonCount(hq),
            CommanderSettings.OperationsMaxPlatoons,
            hasOpenBook);
        if (groundCapped != state.GroundCapped)
        {
            state.GroundCapped = groundCapped;
            CommanderAiLog.Note(
                hq,
                groundCapped
                    ? $"holds ground purchases: {CommanderOperationsService.PlatoonCount(hq)} platoons at the cap of {CommanderSettings.OperationsMaxPlatoons} and no open requisition."
                    : "resumes ground purchases: a requisition is open or a platoon was lost.");
        }

        if (groundCapped)
        {
            return boughtCount;
        }

        System.Array.Clear(boughtThisReview, 0, boughtThisReview.Length);
        for (int purchase = 0; purchase < purchases && spendable > 0f; purchase++)
        {
            // Two overrides on the plan, both about a base rather than a front: no air defence at
            // all while the player flies, and a home-guard ring the commander cannot fill out of
            // what it already owns.
            EnemyPlan buyPlan = (purchase == 0 && blindToAir) || (purchase == 1 && state.WantsDefenceUnit)
                ? EnemyPlan.AirDefence
                : state.Plan;

            // Purchase order (user decision 2026-09-13, "platoon tasking should take priority over
            // generic commander actions"): 1. the operations order book — a platoon, forward base
            // or picket that asked for bodies; 2. the capture-unit override; 3. the recon override;
            // 4. the plan-based counter triangle (with the blind-to-air / home-guard plan swaps).
            // The book used to sit behind the two overrides, so a base wanting an expansion unit
            // or overwatch delayed every platoon's replacements by a purchase each review.
            CommanderPlatoonRole? role = null;
            VehicleDefinition? choice = null;
            if (hasOpenBook)
            {
                // Try every open line the book has, truck first then largest first, and take the
                // first affordable one. Falling back to the plan on the first unaffordable role
                // bought a cheap vehicle the recipe could not use, and the truck line never won
                // "largest" so no forward base ever got its truck.
                CommanderOperationsService.OpenRolesByPriority(hq, boughtThisReview, openRoles);
                for (int r = 0; r < openRoles.Count && choice == null; r++)
                {
                    choice = ChooseForRole(spendable, openRoles[r]);
                    if (choice != null)
                    {
                        role = openRoles[r];
                        boughtThisReview[(int)openRoles[r]]++;
                    }
                }
            }

            if (choice == null)
            {
                if (purchase == 0 && needsCaptureUnit)
                {
                    choice = ChooseCaptureUnit(spendable) ?? Choose(spendable, buyPlan);
                }
                else if (state.WantsReconUnit && purchase == purchases - 1)
                {
                    choice = ChooseReconUnit(spendable) ?? Choose(spendable, buyPlan);
                }
                else
                {
                    choice = Choose(spendable, buyPlan);
                }
            }

            if (choice == null)
            {
                return boughtCount;
            }

            float cost = Mathf.Max(0f, choice.value);
            hq.AddFunds(-cost);
            hq.ModifyUnitSupply(choice, 1);
            spendable -= cost;
            boughtCount++;
            RecordPurchase(hq);
            CommanderAiLog.Note(
                hq,
                $"bought {CommanderGameAccess.GetVehicleLabel(choice)} for {cost:0}.",
                role == null ? GetPlanLabel(buyPlan) : $"{GetPlanLabel(buyPlan)}, for {GetRoleLabel(role.Value)}");
        }

        return boughtCount;
    }

    /// <summary>
    /// What the commander does with what it already owns, as opposed to what it buys: park the radar
    /// screen on the approaches, and give every idle airframe a mission. Both run every review and
    /// on every mission the commander is switched on for, because both are about units that are
    /// already paid for.
    /// </summary>
    private void ReviewPosture(FactionHQ hq, FactionHQ opponent, CommanderState state, in ForceRead opponentForce)
    {
        ReviewRecon(hq, opponent, state);
        // The duel gate is gone (user answer 2026-09-13): the wing's posture runs on every commanded
        // HQ, stock missions included. A stock mission's own authored aircraft are never touched —
        // the posture only reaches airframes in the commander's own set, and those exist only once
        // the buy leg below buys them.
        TaskAirWing(hq);
    }

    /// <summary>
    /// The duel's head start, applied once per enemy HQ: a slightly bigger opening balance, and
    /// nothing else. Deliberately small — the enemy still earns at the player's rates from here on,
    /// this only stops the first ten minutes being a walkover.
    /// <para>
    /// <c>AIAircraftLimit</c> is pinned to zero on purpose. That field is the Basegame's free-air-
    /// force tap: <c>FactionHQ.DeployAIAircraft</c> launches a random airframe out of the mission's
    /// authored stock every few seconds until the cap is met, which is where the aircraft nobody
    /// bought came from — and, on a map whose only airbases are highway strips, where the fighters
    /// that immediately went looking for somewhere to land came from too. Every aircraft in the
    /// duel is now one a commander paid for and launched: <see cref="BuyAirframe"/> for the enemy,
    /// the AIR window for the player.
    /// </para>
    /// </summary>
    private static void PrepareDuel(FactionHQ hq)
    {
        hq.SetFunds(hq.factionFunds * DuelHeadStart);
        hq.AIAircraftLimit = 0;
        hq.reserveAirframes = 0;
        CommanderAiLog.Note(
            hq,
            $"takes the duel head start: {hq.factionFunds:0} funds. "
                + "Automatic AI aircraft are off; every airframe is bought and launched.");
    }

    /// <summary>
    /// Hands the enemy standing intel on every building the player owns. Buildings do not move, so
    /// one registration each is enough and nothing has to be refreshed. This is what turns the
    /// enemy from a faction that owns units into one that attacks: the Basegame ground AI drives at
    /// the nearest tracked enemy when it has no closer objective, and <c>CombatAI</c> only ever
    /// picks targets out of <c>trackingDatabase</c>, so an empty database is an enemy that flies
    /// over your base without seeing it. The player's mobile units are left unrevealed on purpose —
    /// the enemy knows where your base is, not where your army is.
    /// </summary>
    private static void RevealPlayerBase(FactionHQ hq, FactionHQ opponent)
    {
        if (opponent.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in opponent.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit != null
                && !unit.disabled
                && unit is Building
                && hq.GetTrackingData(id) == null)
            {
                hq.RpcUpdateTrackingInfo(id);
            }
        }
    }

    /// <summary>
    /// Puts the enemy on the player's economy: same opening balance and the same kill/tax rates,
    /// once, the first time this commander is reviewed. The balance comes from
    /// <c>excessFundsThreshold</c> (the player faction's authored starting balance) rather than
    /// its live funds, so turning the commander on mid-mission still mirrors the opening position
    /// instead of whatever the player happens to be holding.
    /// </summary>
    private static void LevelEconomy(FactionHQ hq, FactionHQ localHq)
    {
        float baseline = localHq.excessFundsThreshold > 0f
            ? localHq.excessFundsThreshold
            : localHq.factionFunds;
        hq.SetFunds(baseline);
        hq.excessFundsThreshold = localHq.excessFundsThreshold;
        hq.killReward = localHq.killReward;
        hq.playerTaxRate = localHq.playerTaxRate;
        CommanderAiLog.Note(hq, $"matched to the player economy at {baseline:0}.");
    }

    private void UpdatePlan(FactionHQ hq, CommanderState state, in ForceRead opponentForce)
    {
        EnemyPlan wanted = ChoosePlan(opponentForce);
        if (wanted == state.Plan)
        {
            state.Pending = wanted;
            state.PendingReviews = 0;
            return;
        }

        if (wanted == state.Pending)
        {
            state.PendingReviews++;
        }
        else
        {
            state.Pending = wanted;
            state.PendingReviews = 1;
        }

        if (state.PendingReviews < PlanCommitReviews)
        {
            return;
        }

        state.Plan = wanted;
        state.PendingReviews = 0;
        CommanderAiLog.Note(hq, $"switches plan to {GetPlanLabel(wanted)}.");
    }

    /// <summary>
    /// The counter triangle. Each plan beats the force that provokes it and is soft against the
    /// one that provokes the next, so shifting your own composition flips theirs — that swap is
    /// the game.
    /// </summary>
    private static EnemyPlan ChoosePlan(in ForceRead force)
    {
        // Leaning on air power: build the umbrella.
        if (force.Aircraft >= 2 && force.Aircraft * 2 >= force.Ground)
        {
            return EnemyPlan.AirDefence;
        }

        // Massed armour: guns break a column faster than trading tank for tank.
        if (force.Armour >= 3 && force.Armour * 2 >= force.Ground)
        {
            return EnemyPlan.FireSupport;
        }

        // A static line of guns and launchers: run armour through it before it can range.
        int staticLine = force.Guns + force.AirDefence;
        if (staticLine >= 3 && staticLine * 2 >= force.Ground)
        {
            return EnemyPlan.Spearhead;
        }

        // Nothing dominant to counter: cheap mass, take ground.
        return EnemyPlan.ReconScreen;
    }

    private static bool MatchesPlan(EnemyPlan plan, VehicleDefinition definition)
    {
        return plan switch
        {
            EnemyPlan.AirDefence => definition.vehicleType
                is VehicleType.R_SAM or VehicleType.IR_SAM or VehicleType.AAA,
            EnemyPlan.FireSupport => definition.vehicleType is VehicleType.ART or VehicleType.AFV,
            EnemyPlan.Spearhead => definition.vehicleType is VehicleType.MBT or VehicleType.AFV,
            _ => definition.vehicleType is VehicleType.LCV or VehicleType.AFV,
        };
    }

    private VehicleDefinition? Choose(float budget, EnemyPlan plan)
    {
        candidates.Clear();
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.value <= budget && IsCombatVehicle(definition) && MatchesPlan(plan, definition))
            {
                candidates.Add(definition);
            }
        }

        if (candidates.Count == 0)
        {
            for (int i = 0; i < catalog.Count; i++)
            {
                if (catalog[i].value <= budget && IsCombatVehicle(catalog[i]))
                {
                    candidates.Add(catalog[i]);
                }
            }
        }

        return candidates.Count == 0 ? null : candidates[Random.Range(0, candidates.Count)];
    }

    /// <summary>
    /// The cheapest vehicle in the catalogue that can actually move an airbase's capture bar.
    /// Cheapest on purpose: a capture unit's job is to sit in the ring, not to win the fight for it.
    /// </summary>
    private VehicleDefinition? ChooseCaptureUnit(float budget)
    {
        VehicleDefinition? best = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.captureStrength > 0f
                && definition.value <= budget
                && (best == null || definition.value < best.value))
            {
                best = definition;
            }
        }

        return best;
    }

    /// <summary>
    /// The cheapest catalogue entry that fills <paramref name="role"/> — an order line wants a body
    /// in a slot, not the best vehicle the faction can field, which is why this is not
    /// <see cref="Choose"/> with a role filter bolted on. Within <see cref="CommanderPlatoonRole.Carrier"/>
    /// prefers a capture-capable entry (the same test <see cref="ChooseCaptureUnit"/> uses); within
    /// <see cref="CommanderPlatoonRole.Truck"/> the munitions-truck test is already what
    /// <see cref="CommanderPlatoonRoles.Of"/> matched the role on, so every truck candidate already
    /// qualifies.
    /// </summary>
    private VehicleDefinition? ChooseForRole(float budget, CommanderPlatoonRole role)
    {
        VehicleDefinition? cheapest = null;
        VehicleDefinition? cheapestPreferred = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.value > budget || CommanderPlatoonRoles.Of(definition) != role)
            {
                continue;
            }

            // The `continue` above already guarantees definition.value <= budget for anything that
            // reaches here, so the clause the reviewer flagged is not restated (provably
            // behaviour-neutral: removing a condition already implied by an earlier `continue`).
            if (cheapest == null || definition.value < cheapest.value)
            {
                cheapest = definition;
            }

            bool preferred = role == CommanderPlatoonRole.Carrier
                ? definition.captureStrength > 0f
                // Always true once Of(definition) has already matched Truck — restated here so the
                // preference the design-facts table asks for ("preferring … IsMunitionsTruckDefinition
                // within Truck") is visible at this call site too, not only inside Of.
                : role == CommanderPlatoonRole.Truck && CommanderGameAccess.IsMunitionsTruckDefinition(definition);
            if (preferred && (cheapestPreferred == null || definition.value < cheapestPreferred.value))
            {
                cheapestPreferred = definition;
            }
        }

        return cheapestPreferred ?? cheapest;
    }

    private static string GetRoleLabel(CommanderPlatoonRole role)
    {
        return role switch
        {
            CommanderPlatoonRole.Armour => "ARMOUR",
            CommanderPlatoonRole.Carrier => "CARRIER",
            CommanderPlatoonRole.AirDefence => "AIR DEFENCE",
            CommanderPlatoonRole.Truck => "TRUCK",
            _ => "OTHER",
        };
    }

    private void CollectCatalog(FactionHQ hq)
    {
        catalog.Clear();
        bool logRoster = loggedGroundRoster.Add(hq);
        List<Faction.ConvoyGroup> convoyGroups = hq.faction.GetConvoyGroups();
        for (int groupIndex = 0; groupIndex < convoyGroups.Count; groupIndex++)
        {
            List<Faction.ConvoyUnit> constituents = convoyGroups[groupIndex].Constituents;
            for (int unitIndex = 0; unitIndex < constituents.Count; unitIndex++)
            {
                if (constituents[unitIndex].Type is VehicleDefinition definition
                    && CommanderGameAccess.IsSpawnableVehicleDefinition(definition)
                    && !catalog.Contains(definition))
                {
                    catalog.Add(definition);
                    if (logRoster)
                    {
                        // Once per HQ, like LogAirRosterOnce: which vehicles exist, what the game
                        // types them as and which platoon role that maps to — the only way to tell
                        // from the log why a recipe slot stays empty (no LCV in this faction's list).
                        CommanderPlugin.Log.LogInfo(
                            $"Ground roster ({hq.faction.name}): {CommanderGameAccess.GetVehicleLabel(definition)} "
                                + $"[{definition.vehicleType}] role {GetRoleLabel(CommanderPlatoonRoles.Of(definition))}, "
                                + $"capture {definition.captureStrength:0.##}, value {definition.value:0}");
                    }
                }
            }
        }
    }

    /// <summary>Drops state for HQs that went away with a scene the reset did not catch, and for the
    /// local HQ while nothing is commanding it — its state is only meaningful while the player
    /// commander switch is on, and leaving it behind would keep a stale plan on the HUD.</summary>
    private void PruneStates(FactionHQ localHq)
    {
        bool dropLocal = !CommanderPlayerCommanderService.IsCommanded(localHq, localHq);
        staleHqs.Clear();
        foreach (KeyValuePair<FactionHQ, CommanderState> entry in states)
        {
            if (entry.Key == null || (dropLocal && ReferenceEquals(entry.Key, localHq)))
            {
                staleHqs.Add(entry.Key!);
            }
        }
        for (int i = 0; i < staleHqs.Count; i++)
        {
            states.Remove(staleHqs[i]);
        }
    }

    private static string FundsLabel(float funds)
    {
        return UnitConverter.ValueReading(funds) ?? funds.ToString("F1");
    }

    /// <summary>Internal (one-word widening): the operations air step's loss cooldown counts the
    /// same air-defence vehicles with the same test — one definition, two callers.</summary>
    internal static bool IsAirDefence(VehicleDefinition definition)
    {
        return definition.vehicleType is VehicleType.AAA or VehicleType.IR_SAM or VehicleType.R_SAM;
    }

    private static bool IsCombatVehicle(VehicleDefinition definition)
    {
        return definition.value > 0f
            && definition.vehicleType is VehicleType.AAA
                or VehicleType.IR_SAM
                or VehicleType.R_SAM
                or VehicleType.MBT
                or VehicleType.AFV
                or VehicleType.ART
                or VehicleType.LCV;
    }

    private static int CountAirDefence(FactionHQ hq)
    {
        int count = 0;
        if (hq.factionUnits == null)
        {
            return 0;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit != null
                && !unit.disabled
                && unit.definition is VehicleDefinition definition
                && IsAirDefence(definition))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>What the opponent is actually fielding, which is the only input to the plan choice.</summary>
    private static ForceRead ReadForce(FactionHQ hq)
    {
        ForceRead read = default;
        if (hq.factionUnits == null)
        {
            return read;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit == null || unit.disabled)
            {
                continue;
            }

            if (unit is Aircraft)
            {
                read.Aircraft++;
                continue;
            }

            if (unit.definition is not VehicleDefinition definition)
            {
                continue;
            }

            switch (definition.vehicleType)
            {
                case VehicleType.MBT:
                case VehicleType.AFV:
                    read.Armour++;
                    read.Ground++;
                    break;
                case VehicleType.ART:
                    read.Guns++;
                    read.Ground++;
                    break;
                case VehicleType.AAA:
                case VehicleType.IR_SAM:
                case VehicleType.R_SAM:
                    read.AirDefence++;
                    read.Ground++;
                    break;
                case VehicleType.LCV:
                    read.Ground++;
                    break;
            }
        }

        return read;
    }

    internal enum EnemyPlan
    {
        ReconScreen,
        Spearhead,
        FireSupport,
        AirDefence,
    }

    internal struct ForceRead
    {
        internal int Aircraft;
        internal int Armour;
        internal int Guns;
        internal int AirDefence;
        internal int Ground;
    }

    private sealed class CommanderState
    {
        internal EnemyPlan Plan;
        internal EnemyPlan Pending;
        internal int PendingReviews;
        internal bool Prepared;

        /// <summary>Funds set aside for the next airframe and the next hull. Both are worth several
        /// ground vehicles, so a per-review slice has to accumulate or it never buys anything.</summary>
        internal float AirFund;
        internal float NavalFund;

        /// <summary>Consecutive buy reviews that bought nothing; drives the "holds" log line.</summary>
        internal int SkippedBuyReviews;

        /// <summary>Whether the last review found the platoon cap binding (see GroundBuyingCapped),
        /// so the hold/resume log line fires once per transition rather than every review.</summary>
        internal bool GroundCapped;

        /// <summary>Short of a radar vehicle for the overwatch screen.</summary>
        internal bool WantsReconUnit;

        /// <summary>Short of vehicles for the home guard, so the buy loop should get one.</summary>
        internal bool WantsDefenceUnit;

        /// <summary>The home guard: each pinned vehicle and the ring post it holds.</summary>
        internal readonly Dictionary<Unit, int> Defenders = new();

        /// <summary>Ring stations around every base this commander holds, and how many bases that
        /// was when they were picked.</summary>
        internal readonly List<GlobalPosition> DefencePosts = new();
        internal int DefenceBaseCount = -1;

        /// <summary>Defence posture: it holds until this time, and whether it is on right now.</summary>
        internal float ThreatUntil;
        internal bool Defending;

        /// <summary>Standing overwatch posts, picked once and then kept.</summary>
        internal readonly List<GlobalPosition> ReconPosts = new();

        /// <summary>The opponent's opening airbase. See GetStrikeTarget. Per commander rather than
        /// one field for the whole service: with the local HQ commanded too, two commanders remember
        /// two different opening bases, and the shared field would be rewritten every review.</summary>
        internal Airbase? StrikeBase;

        /// <summary>Last reason this commander bought no aircraft, so the log says it once and not
        /// twice a minute for the rest of the match.</summary>
        internal string LastAirDenial = string.Empty;
    }
}
