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
    /// ground spender. Without a ceiling, a commander that can never buy an aircraft — no compatible
    /// strip, no hull it can afford — would quietly withhold its air share from the convoys forever.
    /// Internal (one-word widening): the air fund ceiling's self-check reads it.</summary>
    internal const int FundSaveReviews = 6;

    /// <summary>Most airframes one buy review may launch (user decision 2026-09-13). The old
    /// one-airframe-per-review throttle let the wing field only thirty aircraft a match even with
    /// the fund full and five sorties short — a wing that waits a match to form is no wing. Three is
    /// a CAP fighter, a CAS airframe and a wingman in one review without emptying the fund in a
    /// single flush; the ceiling and the fund still bound the rest. Internal: the self-check reads
    /// it.</summary>
    internal const int MaxAirBuysPerReview = 3;

    /// <summary>The air fund's ceiling: the reviews-of-saving cap, or enough for
    /// <see cref="MaxAirBuysPerReview"/> of the dearest fighter on the roster, whichever is larger.
    /// The flat cap sat below three fighters for any commander whose pot was small (share x 6), so
    /// the multi-buy review could never actually run. Pure, for the self-check.</summary>
    internal static float AirFundCeiling(float share, float dearestFighterPrice)
    {
        return Mathf.Max(share * FundSaveReviews, MaxAirBuysPerReview * Mathf.Max(0f, dearestFighterPrice));
    }

    /// <summary>Whether the buy loop may launch another airframe this review — the
    /// buys-per-review bound. Pure, for the self-check.</summary>
    internal static bool AirBuyContinues(int boughtThisReview)
    {
        return boughtThisReview < MaxAirBuysPerReview;
    }

    /// <summary>The dearest AI-flyable fighter this faction's roster offers, read off the asset
    /// data at review time — the yardstick for the air fund's floor (<see cref="AirFundCeiling"/>).
    /// Zero when the roster has no fighter at all, which leaves the plain saving cap in force.</summary>
    private static float DearestFighterPrice(FactionHQ hq)
    {
        float dearest = 0f;
        foreach (KeyValuePair<AircraftDefinition, FactionHQ.RuntimeSupply> entry in hq.AircraftSupply)
        {
            AircraftDefinition? definition = entry.Key;
            if (definition != null
                && CommanderAirCommandService.CanAiFly(definition)
                && GetAirRole(definition) == AirRole.Fighter
                && definition.value > dearest)
            {
                dearest = definition.value;
            }
        }

        return dearest;
    }

    /// <summary>
    /// Sets a slice of this review's pot aside, and returns what it actually took so the caller can
    /// deduct exactly that. Airframes are dear and ground vehicles are cheap, so a flat per-review
    /// slice never added up to an aircraft once the commander was also buying convoys — it kept the
    /// balance too low for its own air share to ever clear a purchase price. Saving is the whole fix:
    /// it is the difference between an enemy that flew for the first few minutes and then never
    /// again, and one that keeps a wing up all match.
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
    }

    /// <summary>Airframes of one role the commander runs before it starts buying the dear ones.</summary>
    private const int CheapAirframesPerRole = 2;

    /// <summary>Transports the commander will keep in the air at once. They place units; they do not
    /// win fights, so a wing of them is a wasted budget.</summary>
    private const int TransportLimit = 2;

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
        CommanderAirDemandKind demand, int fighters, int transports, in ForceRead opponentForce)
    {
        if (demand == CommanderAirDemandKind.Cap)
        {
            return AirRole.Fighter;
        }

        if (demand == CommanderAirDemandKind.Cas)
        {
            return AirRole.Strike;
        }

        if (opponentForce.Aircraft > 0 && fighters == 0)
        {
            return AirRole.Fighter;
        }

        if (transports < TransportLimit && opponentForce.Ground > 0)
        {
            return AirRole.Transport;
        }

        return null;
    }

    /// <summary>The live-roster wrapper around the pure rule above.</summary>
    private static AirRole? ChooseAirRole(FactionHQ hq, in ForceRead opponentForce, CommanderAirDemandKind demand)
    {
        return ChooseAirRole(demand, CountRole(hq, AirRole.Fighter), CountRole(hq, AirRole.Transport), opponentForce);
    }

    /// <summary>How many airframes of one role this faction already has in the world.</summary>
    private static int CountRole(FactionHQ hq, AirRole role)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is Aircraft aircraft
                && !unit.disabled
                && GetAirRole(aircraft.definition) == role)
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
    /// at all. The wing is now composed by role, and inside a role it buys the cheapest airframe
    /// until it is running <see cref="CheapAirframesPerRole"/> of them and only then starts spending
    /// up. Cheap fighters early, expensive strike aircraft once the economy can carry them.
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
        CommanderAirDemandKind demand = CommanderOperationsService.TryGetAirDemand(hq, out GlobalPosition objective);
        AirRole? wanted = ChooseAirRole(hq, opponentForce, demand);
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

        bool escalate = CountRole(hq, wanted.Value) >= CheapAirframesPerRole;
        float spent = TryBuyRole(hq, state, budget, wanted.Value, escalate, facing, sortieObjective, allowLastResort: false);
        if (spent > 0f)
        {
            return spent;
        }

        // The same role once more with the last-resort airframe allowed — the LAST RESORT pass
        // (user decision 2026-09-13): the Cricket flies only when it is literally the only airframe
        // that can fill the role on this roster, within the fund and what the strips accept.
        return TryBuyRole(hq, state, budget, wanted.Value, escalate, facing, sortieObjective, allowLastResort: true);
    }

    /// <summary>One pass of the role buy. <paramref name="allowLastResort"/> admits the last-resort (Cricket)
    /// airframes — only BuyAirframe's second pass sets it, which is the whole of the LAST RESORT
    /// rule. Failure reasons are STABLE STRINGS (no balances in them) so the once-per-reason
    /// denial stays once per reason and not once per fluctuating fund.</summary>
    private float TryBuyRole(
        FactionHQ hq,
        CommanderState state,
        float budget,
        AirRole role,
        bool escalate,
        GlobalPosition facing,
        GlobalPosition? sortieObjective,
        bool allowLastResort)
    {
        float cheapestInRole = float.MaxValue;
        bool sawAnyAirbase = false;
        bool sawAnyAirframe = false;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            sawAnyAirbase = true;
            AircraftDefinition? choice = null;
            float chosenValue = escalate ? 0f : float.MaxValue;
            foreach (KeyValuePair<AircraftDefinition, FactionHQ.RuntimeSupply> entry in hq.AircraftSupply)
            {
                AircraftDefinition? definition = entry.Key;
                if (definition == null
                    || !CommanderAirCommandService.CanAiFly(definition)
                    || !airbase.CanSpawnAircraft(definition)
                    || GetAirRole(definition) != role)
                {
                    continue;
                }

                // The LAST RESORT gate (user decision 2026-09-13): the Cricket is invisible to the
                // buy until the roster's other airframes cannot fill the role at all.
                if (!allowLastResort && IsLastResortAirframe(definition))
                {
                    continue;
                }

                sawAnyAirframe = true;
                if (definition.value < cheapestInRole)
                {
                    cheapestInRole = definition.value;
                }

                if (definition.value > budget
                    || (escalate ? definition.value <= chosenValue : definition.value >= chosenValue))
                {
                    continue;
                }

                choice = definition;
                chosenValue = definition.value;
            }

            if (choice == null)
            {
                continue;
            }

            Loadout loadout = null!;
            float fuel = choice.aircraftParameters.DefaultFuelLevel;
            StandardLoadout? standard = choice.aircraftParameters.GetRandomStandardLoadout(choice, hq);
            if (standard != null)
            {
                // Same INTERNAL CANNONS rule as the player's AIR window, so an AI airframe with its
                // missiles spent goes home instead of strafing on its gun ammunition.
                loadout = CommanderAirCommandService.WithoutInternalCannons(standard.loadout);
                fuel = standard.FuelRatio;
            }

            LiveryKey livery = new(choice.aircraftParameters.GetRandomLiveryForFaction(hq.faction));

            // Set before EVERY launch (user decision 2026-09-13): the registration that fires
            // inside it is matched back and the airframe OWNED — bound into the sortie that wanted
            // it, or parked on the home CAP when no sortie did. Before this, only sortie buys
            // recorded an expectation, so a standing buy registered unowned and nothing ever
            // tasked it. Cleared below if the spawn is refused.
            CommanderOperationsService.RecordCommanderLaunch(hq, choice, airbase, sortieObjective);

            if (!CommanderAirCommandService.TryLaunchAiAircraft(hq, airbase, choice, livery, loadout, fuel, facing))
            {
                CommanderOperationsService.ClearCommanderLaunch(hq);
                continue;
            }

            float cost = Mathf.Max(0f, choice.value);
            hq.AddFunds(-cost);
            RecordPurchase(hq);
            state.LastAirDenial = string.Empty;
            CommanderAiLog.Note(
                hq,
                $"launched a {choice.unitName} ({GetAirRole(choice)}) from {airbase.name} for {cost:0}"
                    + (IsLastResortAirframe(choice) ? " (last resort: nothing else can fill the role)." : "."));
            return cost;
        }

        // Stable reasons only — a number in the text makes every balance change a "new" reason and
        // the once-per-reason line fires every review. Reported on the FINAL pass alone
        // (allowLastResort), so a first attempt that falls through to a successful last-resort
        // buy does not print a denial in between the two.
        if (!allowLastResort)
        {
            return 0f;
        }

        if (!sawAnyAirbase)
        {
            ReportAirDenial(hq, state, "it holds no airbase to launch from");
        }
        else if (!sawAnyAirframe)
        {
            ReportAirDenial(
                hq, state, $"its strips accept no AI-flyable {role} airframe at all (VTOLs have no AI flight model)");
        }
        else if (cheapestInRole > budget)
        {
            ReportAirDenial(hq, state, $"its air fund is short of the cheapest {role} airframe its strips accept");
        }

        return 0f;
    }

    /// <summary>
    /// Names, once per mission, every airframe on this faction's list with the three things the buy
    /// loop decides on: the pilot type the Basegame AI will fly it with, the role this reads off its
    /// role identity, and its price.
    /// </summary>
    /// <remarks>
    /// All three live in the game's asset files, not in its code, so a decompile cannot answer any
    /// of them and neither can reasoning about aircraft names. This line in the BepInEx console is
    /// the answer. It is also the only way to tell a genuinely unflyable airframe from one this
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

        foreach (KeyValuePair<AircraftDefinition, FactionHQ.RuntimeSupply> entry in hq.AircraftSupply)
        {
            AircraftDefinition? definition = entry.Key;
            if (definition == null)
            {
                continue;
            }

            CommanderPlugin.Log.LogInfo(
                $"Air roster ({hq.faction.name}): {definition.unitName} [{definition.jsonKey}] "
                    + $"pilot {DescribePilotTypes(definition)}, role {GetAirRole(definition)}, "
                    + $"value {definition.value:0}"
                    + (IsLastResortAirframe(definition) ? "  — LAST RESORT, bought only when nothing else can fill the role" : string.Empty)
                    + (CommanderAirCommandService.CanAiFly(definition) ? string.Empty : "  — NOT AI-FLYABLE, never bought"));
        }
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

        Expect("CAP demand buys a fighter even with transports short and the opponent on the ground",
            ChooseAirRole(CommanderAirDemandKind.Cap, fighters: 2, transports: 0, flyingOpponent), AirRole.Fighter);
        Expect("CAS demand buys a strike airframe even with transports short",
            ChooseAirRole(CommanderAirDemandKind.Cas, fighters: 2, transports: 0, groundOpponent), AirRole.Strike);
        Expect("no demand and a flying opponent with no fighters buys the air-superiority counter",
            ChooseAirRole(CommanderAirDemandKind.None, fighters: 0, transports: 2, flyingOpponent), AirRole.Fighter);
        Expect("no demand tops up transports while the opponent fields ground units",
            ChooseAirRole(CommanderAirDemandKind.None, fighters: 1, transports: 1, groundOpponent), AirRole.Transport);
        Expect("no demand buys nothing", ChooseAirRole(CommanderAirDemandKind.None, fighters: 1, transports: 2, groundOpponent), null);

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

    /// <summary>Says why no aircraft was bought, but only when the reason changes — a review runs
    /// every 30 s and the same line every time is noise nobody reads.</summary>
    private static void ReportAirDenial(FactionHQ hq, CommanderState state, string reason)
    {
        if (state.LastAirDenial == reason)
        {
            return;
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
            if (!id.TryGetUnit(out Unit unit) || unit is not Aircraft aircraft || unit.disabled)
            {
                continue;
            }

            TrackAircraft(aircraft);
            if (!CommanderOperationsService.IsCommanderAirframe(hq, aircraft))
            {
                continue;
            }

            // Refuses anything already missioned (a bound sortie airframe, a released one already
            // holding the box, the player's own) — the posture only fills the empty ones.
            if (CommanderOperationsService.IssuePostureTask(hq, aircraft))
            {
                CommanderAiLog.Note(
                    hq,
                    $"tasked {CommanderGameAccess.GetUnitLabel(aircraft)} with "
                        + $"{CommanderAirCommandService.GetModeLabel(CommanderAirCommandService.AirCommandMode.AirGuard)} over home territory.");
            }
        }
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
