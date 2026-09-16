using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The air roster log lines. Split out of <c>CommanderEnemyCommanderAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past three thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
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
                    + (CommanderAirCommandService.HasPlanePilot(definition) ? string.Empty : "  — NO PLANE PILOT, never tasked")
                    // The faction split (2026-09-16). The line deliberately still prints EVERY
                    // airframe in the game and marks the ones this side may not have, rather than
                    // printing a short list: a reader needs to be able to tell "the split excluded
                    // it" from "the asset scan never found it".
                    + (CommanderFactionRoster.MayFlyAircraft(hq, definition)
                        ? string.Empty
                        : "  — NOT ON THIS FACTION'S ROSTER, never bought or launched")
                    // Which anti-radiation missile, by name, this airframe would actually fly
                    // suppression with (user report 2026-09-14: four Eyeball Mk.II sensor rounds
                    // were being hung on suppression helicopters). "none" is now a real answer, and
                    // an airframe that reads "none" opens no suppression sortie.
                    + $", ARAD: {DescribeAradLoadout(hq, definition)}");
        }

        ReportCasOrdnance(hq);
    }

    /// <summary>The anti-radiation stores the commander's own loadout builder would hang on this
    /// airframe, by name, or "none" — the roster line's ARAD field (user report 2026-09-14). Weapon
    /// availability is faction-gated asset data, so this is the only way to tell "this roster has
    /// no anti-radiation missile" from "the suppression rule is broken".</summary>
    private static string DescribeAradLoadout(FactionHQ hq, AircraftDefinition definition)
    {
        if (!CommanderAirCommandService.TryBuildRoleLoadout(
                definition, hq, CommanderAirCommandService.AirCommandMode.Arad,
                preferArhMissiles: false, out Loadout loadout, out _))
        {
            return "none";
        }

        string named = CommanderAirCommandService.DescribeAradWeapons(loadout);
        return string.IsNullOrEmpty(named) ? "none" : named;
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

        // The evidence line for the warhead rule (2026-09-14). The rule keys on the game's own
        // pierce/blast damage figures, and those values cannot be read out of the asset files at
        // build time — so the roster prints them once per faction, beside the rank the scorer gave
        // each store. A preferred missile reading `pierce 0, blast 0, rank -1` here would mean the
        // rule is reading the wrong field and the CAS preference has been switched off in play.
        string ordnanceData = string.Empty;
        foreach (AircraftDefinition definition in airCatalog)
        {
            string described = CommanderAirCommandService.DescribePreferredCasOrdnanceData(definition);
            if (described.Length > 0 && ordnanceData.IndexOf(described, System.StringComparison.Ordinal) < 0)
            {
                ordnanceData = ordnanceData.Length == 0 ? described : ordnanceData + "; " + described;
            }
        }

        CommanderPlugin.Log.LogInfo(
            $"Air roster ({hq.faction.name}): CAS ordnance data: "
                + (ordnanceData.Length > 0 ? ordnanceData : "no hardpoint on this roster offers a named preferred store"));
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
            return state != null && !RadarAirframeMayFill(role) && HasRadarLoadout(hq, state, definition)
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
}
