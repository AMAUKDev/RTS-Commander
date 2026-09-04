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

    /// <summary>Aircraft this commander may have airborne at once, and the share of one review's
    /// budget it will spend putting them there — an air force must not starve the convoys.
    /// Raised from 4 with the move to a role-composed wing: four airframes is one of each role and
    /// no depth, so the ceiling was being hit before the mix was ever assembled.</summary>
    private const int DuelAirborneLimit = 8;
    private const float DuelAirframeBudgetShare = 0.4f;

    private readonly Dictionary<FactionHQ, CommanderState> states = new();

    /// <summary>HQs whose airframe list has already been written to the log. See LogAirRosterOnce.</summary>
    private readonly HashSet<FactionHQ> loggedAirRoster = new();

    /// <summary>Airframes the enemy has in the air, and when each entered it. See ReportLostAircraft.</summary>
    private readonly Dictionary<Aircraft, float> airborneSince = new();
    private readonly List<Aircraft> lostAircraft = new();

    /// <summary>The player's opening airbase. See GetStrikeTarget.</summary>
    private Airbase? playerHomeBase;
    private readonly List<FactionHQ> staleHqs = new();
    private readonly List<VehicleDefinition> catalog = new();
    private readonly List<VehicleDefinition> candidates = new();
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

    /// <summary>Units this session's enemy commanders have bought, for the settings readout.</summary>
    internal int TotalPurchases { get; private set; }

    /// <summary>Plan and balance of the best-funded enemy commander, for the HUD readout.</summary>
    internal string StatusLine { get; private set; } = string.Empty;

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
        if (mode == ModeOff || localHq == null)
        {
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

        ForceRead playerForce = ReadForce(localHq);
        FactionHQ? primary = null;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || ReferenceEquals(hq, localHq) || !hq.IsServer || hq.faction == null)
            {
                continue;
            }

            Review(hq, localHq, mode, playerForce);
            if (primary == null || hq.factionFunds > primary.factionFunds)
            {
                primary = hq;
            }
        }

        PruneStates(localHq);
        StatusLine = primary != null && states.TryGetValue(primary, out CommanderState primaryState)
            ? $"{GetPlanLabel(primaryState.Plan)}   {FundsLabel(primary.factionFunds)}"
                + (primaryState.Defending ? "   DEFENDING" : string.Empty)
            : string.Empty;
    }

    public void ResetSession()
    {
        states.Clear();
        loggedAirRoster.Clear();
        airborneSince.Clear();
        lostAircraft.Clear();
        playerHomeBase = null;
        staleHqs.Clear();
        catalog.Clear();
        candidates.Clear();
        reconUnits.Clear();
        shipCatalog.Clear();
        defenceCandidates.Clear();
        staleDefenders.Clear();
        TotalPurchases = 0;
        StatusLine = string.Empty;
        nextReviewAt = 0f;
        nextDefenceAt = 0f;
    }

    private void Review(FactionHQ hq, FactionHQ localHq, int mode, in ForceRead playerForce)
    {
        if (!states.TryGetValue(hq, out CommanderState state))
        {
            state = new CommanderState();
            states[hq] = state;
        }

        bool duel = IsDuelMission;
        if (!state.Prepared)
        {
            if (mode == ModeMatched)
            {
                LevelEconomy(hq, localHq);
            }
            if (duel)
            {
                PrepareDuel(hq);
            }
            state.Prepared = true;
        }

        if (duel)
        {
            RevealPlayerBase(hq, localHq);
        }

        UpdatePlan(hq, state, playerForce);

        // Posture comes before spending on purpose: a commander with an empty balance still has to
        // fly the aircraft and drive the radars it already owns.
        ReviewPosture(hq, localHq, state, playerForce);

        // Whatever the economy service is saving for is off limits here. Both spenders draw on the
        // one factionFunds pool, and this one takes a fixed share of the balance every review — so
        // without the hold-back the balance never climbed to a factory or a dock and the enemy
        // commander built nothing but its opening mines all match.
        float pot = hq.factionFunds - CommanderEconomyService.GetEnemyBuildReserve(hq);
        float spendable = pot * (duel ? DuelSpendFraction : SpendFraction);
        if (spendable <= 0f)
        {
            return;
        }

        CollectCatalog(hq);
        if (catalog.Count == 0)
        {
            return;
        }

        // Whatever the plan says, a commander with no air defence at all while the player is
        // flying is not playing the same game. One launcher first, then back to the plan.
        bool blindToAir = playerForce.Aircraft > 0 && CountAirDefence(hq) == 0;
        int purchases = duel ? DuelPurchasesPerReview : PurchasesPerReview;
        if (duel)
        {
            // The air share is set aside rather than spent-or-lost. An airframe costs several ground
            // vehicles, so a flat slice of a pot the commander keeps draining on convoys never once
            // added up to an aircraft after the opening minutes — which is exactly how an enemy that
            // flew at the start ended up with no air force at all.
            spendable -= AccrueFund(ref state.AirFund, spendable * DuelAirframeBudgetShare);
            state.AirFund -= BuyAirframe(hq, state, state.AirFund, playerForce);
        }

        spendable -= ReviewNaval(hq, localHq, state, spendable * NavalBudgetShare);

        // An expansion with nothing that can take ground is an expansion that never happens, so a
        // capture unit outranks the plan exactly the way the first air-defence launcher does.
        bool needsCaptureUnit = CommanderCaptureService.Instance?.WantsCaptureUnit(hq) == true;
        for (int purchase = 0; purchase < purchases && spendable > 0f; purchase++)
        {
            // Two overrides on the plan, both about a base rather than a front: no air defence at
            // all while the player flies, and a home-guard ring the commander cannot fill out of
            // what it already owns.
            EnemyPlan buyPlan = (purchase == 0 && blindToAir) || (purchase == 1 && state.WantsDefenceUnit)
                ? EnemyPlan.AirDefence
                : state.Plan;
            VehicleDefinition? choice = purchase == 0 && needsCaptureUnit
                ? ChooseCaptureUnit(spendable) ?? Choose(spendable, buyPlan)
                : state.WantsReconUnit && purchase == purchases - 1
                    ? ChooseReconUnit(spendable) ?? Choose(spendable, buyPlan)
                    : Choose(spendable, buyPlan);
            if (choice == null)
            {
                return;
            }

            float cost = Mathf.Max(0f, choice.value);
            hq.AddFunds(-cost);
            hq.ModifyUnitSupply(choice, 1);
            spendable -= cost;
            TotalPurchases++;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}, {GetPlanLabel(buyPlan)}) bought "
                    + $"{CommanderGameAccess.GetVehicleLabel(choice)} for {cost:0}.");
        }
    }

    /// <summary>
    /// What the commander does with what it already owns, as opposed to what it buys: park the radar
    /// screen on the approaches, and give every idle airframe a mission. Both run every review and
    /// on every mission the commander is switched on for, because both are about units that are
    /// already paid for.
    /// </summary>
    private void ReviewPosture(FactionHQ hq, FactionHQ localHq, CommanderState state, in ForceRead playerForce)
    {
        ReviewRecon(hq, localHq, state);
        if (IsDuelMission)
        {
            // Only on the mod's own map. Every other mission launches its own AI aircraft off
            // AIAircraftLimit and may script what they do; overriding that is not a bug fix.
            TaskAirWing(hq, localHq, playerForce);
        }
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
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) takes the duel head start: {hq.factionFunds:0} funds. "
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
    private static void RevealPlayerBase(FactionHQ hq, FactionHQ localHq)
    {
        if (localHq.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in localHq.factionUnits)
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
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) matched to the player economy at {baseline:0}.");
    }

    private void UpdatePlan(FactionHQ hq, CommanderState state, in ForceRead playerForce)
    {
        EnemyPlan wanted = ChoosePlan(playerForce);
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
        CommanderPlugin.Log.LogInfo(
            $"Enemy commander ({hq.faction.name}) switches plan to {GetPlanLabel(wanted)}.");
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

    private void CollectCatalog(FactionHQ hq)
    {
        catalog.Clear();
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
                }
            }
        }
    }

    /// <summary>Drops state for HQs that went away with a scene the reset did not catch.</summary>
    private void PruneStates(FactionHQ localHq)
    {
        staleHqs.Clear();
        foreach (KeyValuePair<FactionHQ, CommanderState> entry in states)
        {
            if (entry.Key == null || ReferenceEquals(entry.Key, localHq))
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

    private static bool IsAirDefence(VehicleDefinition definition)
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

    /// <summary>What the player is actually fielding, which is the only input to the plan choice.</summary>
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

    private struct ForceRead
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

        /// <summary>Last reason this commander bought no aircraft, so the log says it once and not
        /// twice a minute for the rest of the match.</summary>
        internal string LastAirDenial = string.Empty;
    }
}
