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
    /// <summary>
    /// Radius of the strike box the enemy works over. Wide enough to cover a base and the ground
    /// around it, because the mission area is also the target filter for a strike sortie.
    /// </summary>
    private const float StrikeRadiusMeters = 25000f;

    /// <summary>Radius of the combat air patrol the commander holds over its own ground.</summary>
    private const float HomeGuardRadiusMeters = 15000f;

    /// <summary>One airframe in this many is held back on home CAP instead of sent on a strike.</summary>
    private const int HomeGuardEveryNth = 3;

    /// <summary>Reviews' worth of saving a fund may hold before the surplus goes back to the
    /// ground spender. Without a ceiling, a commander that can never buy an aircraft — no compatible
    /// strip, no hull it can afford — would quietly withhold its air share from the convoys forever.</summary>
    private const int FundSaveReviews = 6;

    /// <summary>
    /// Sets a slice of this review's pot aside, and returns what it actually took so the caller can
    /// deduct exactly that. Airframes are dear and ground vehicles are cheap, so a flat per-review
    /// slice never added up to an aircraft once the commander was also buying convoys — it kept the
    /// balance too low for its own air share to ever clear a purchase price. Saving is the whole fix:
    /// it is the difference between an enemy that flew for the first few minutes and then never
    /// again, and one that keeps a wing up all match.
    /// </summary>
    private static float AccrueFund(ref float fund, float share)
    {
        float taken = Mathf.Clamp(share * FundSaveReviews - fund, 0f, Mathf.Max(0f, share));
        fund += taken;
        return taken;
    }

    /// <summary>What an airframe is for, read off its own role data rather than a name table.</summary>
    /// <remarks>
    /// <c>UnitDefinition.roleIdentity</c> is the game's own answer to "what does this thing kill",
    /// and <c>captureCapacity</c> is its answer to "does it carry troops". Both are asset data that
    /// survives a patch; a list of aircraft names does not.
    /// </remarks>
    private enum AirRole
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

    private static AirRole GetAirRole(AircraftDefinition definition)
    {
        if (definition.captureCapacity > 0 && !CommanderAirCommandService.HasPlanePilot(definition))
        {
            return AirRole.Transport;
        }

        return definition.roleIdentity.antiAir > definition.roleIdentity.antiSurface
            ? AirRole.Fighter
            : AirRole.Strike;
    }

    /// <summary>
    /// What the wing is short of. Air superiority first once the player is actually flying, because
    /// a strike package with nothing covering it is a free kill; a couple of transports so ground
    /// units can be put somewhere useful instead of driven there; strike the rest of the time,
    /// which is what actually hurts the player's base.
    /// </summary>
    private static AirRole ChooseAirRole(FactionHQ hq, in ForceRead opponentForce)
    {
        int fighters = CountRole(hq, AirRole.Fighter);
        int transports = CountRole(hq, AirRole.Transport);
        if (opponentForce.Aircraft > 0 && fighters == 0)
        {
            return AirRole.Fighter;
        }

        return transports < TransportLimit && opponentForce.Ground > 0 ? AirRole.Transport : AirRole.Strike;
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
    /// </remarks>
    private float BuyAirframe(FactionHQ hq, CommanderState state, float budget, in ForceRead opponentForce)
    {
        LogAirRosterOnce(hq);
        if (CountAirborne(hq) >= DuelAirborneLimit)
        {
            ReportAirDenial(hq, state, $"it is at the {DuelAirborneLimit}-aircraft ceiling");
            return 0f;
        }

        AirRole wanted = ChooseAirRole(hq, opponentForce);
        float spent = TryBuyRole(hq, state, budget, wanted, CountRole(hq, wanted) >= CheapAirframesPerRole);
        if (spent > 0f)
        {
            return spent;
        }

        // Nothing in the wanted role fits the budget or the strip — or this faction's roster has no
        // such airframe at all, which is the usual reason. Buying the wrong kind of aircraft beats
        // buying none, so the second pass takes anything that will fly, and it measures "am I past
        // the cheap opening" against the whole wing rather than against a role it cannot field.
        return TryBuyRole(hq, state, budget, null, CountAirborne(hq) >= CheapAirframesPerRole);
    }

    private float TryBuyRole(FactionHQ hq, CommanderState state, float budget, AirRole? role, bool escalate)
    {
        float cheapestSeen = float.MaxValue;
        bool sawAnyAirbase = false;
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
                    || !airbase.CanSpawnAircraft(definition))
                {
                    continue;
                }

                if (definition.value < cheapestSeen)
                {
                    cheapestSeen = definition.value;
                }

                if (definition.value > budget
                    || (role.HasValue && GetAirRole(definition) != role.Value)
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
            // Launched facing whoever this commander is up against. Only the review loop ever gets
            // here, so for every commander but the player's own ChooseOpponent answers the local HQ,
            // which is the value this used to pass straight in.
            FactionHQ? player = CommanderGameAccess.GetLocalHq();
            GlobalPosition facing = player == null
                ? airbase.center.GlobalPosition()
                : GetStrikeTarget(state, CommanderPlayerCommanderService.ChooseOpponent(hq, player));
            if (!CommanderAirCommandService.TryLaunchAiAircraft(hq, airbase, choice, livery, loadout, fuel, facing))
            {
                continue;
            }

            float cost = Mathf.Max(0f, choice.value);
            hq.AddFunds(-cost);
            RecordPurchase(hq);
            state.LastAirDenial = string.Empty;
            CommanderAiLog.Note(
                hq, $"launched a {choice.unitName} ({GetAirRole(choice)}) from {airbase.name} for {cost:0}.");
            return cost;
        }

        if (role == null)
        {
            ReportAirDenial(
                hq,
                state,
                !sawAnyAirbase
                    ? "it holds no airbase to launch from"
                    : cheapestSeen == float.MaxValue
                        ? "no airframe its strips accept has an AI flight model (VTOLs have none)"
                        : $"its air fund is {budget:0} and the cheapest airframe its strips accept costs {cheapestSeen:0}");
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
    /// Gives every airframe this faction owns that is not already on a mission something to do.
    /// A strike box over the player's territory by default; air superiority over the same box for
    /// part of the wing once the player is actually flying, because a strike package with nothing
    /// escorting it is a free kill.
    /// </summary>
    private void TaskAirWing(FactionHQ hq, CommanderState state, FactionHQ opponent, in ForceRead opponentForce)
    {
        ReportLostAircraft(hq);
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null || hq.factionUnits == null)
        {
            return;
        }

        GlobalPosition strikeTarget = GetStrikeTarget(state, opponent);
        GlobalPosition homeCentre = CommanderCaptureService.GetTerritoryCenter(hq);
        int tasked = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit unit) || unit is not Aircraft aircraft || unit.disabled)
            {
                continue;
            }

            TrackAircraft(aircraft);

            // One airframe in three sits on the fence over its own ground; the rest go to the
            // player's base. Unconditional on purpose: a commander with nothing overhead loses its
            // mines and its factories to the first thing that flies over, and the player asked to be
            // met on the way in rather than only shot at once they are on top of the enemy.
            bool guard = tasked % HomeGuardEveryNth == HomeGuardEveryNth - 1;
            CommanderAirCommandService.AirCommandMode mode = guard
                ? CommanderAirCommandService.AirCommandMode.AirGuard
                : CommanderAirCommandService.AirCommandMode.StrategicStrike;
            GlobalPosition centre = guard ? homeCentre : strikeTarget;
            float radius = guard ? HomeGuardRadiusMeters : StrikeRadiusMeters;
            if (airCommand.TryTaskAiAircraft(aircraft, mode, centre, radius))
            {
                tasked++;
                CommanderAiLog.Note(
                    hq,
                    $"tasked {CommanderGameAccess.GetUnitLabel(aircraft)} with {CommanderAirCommandService.GetModeLabel(mode)}.");
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
