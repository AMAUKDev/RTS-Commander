using System;
using System.Collections.Generic;
using NuclearOption.SavedMission;

namespace GroundControlRts;

/// <summary>Which of the two stock sides an HQ belongs to, or neither.</summary>
internal enum CommanderFactionSide
{
    /// <summary>A third faction, a renamed one, or no HQ at all. Never restricted.</summary>
    Unknown,

    /// <summary>Boscali Defence Force, whose faction fields carry the tag BDF.</summary>
    Boscali,

    /// <summary>Primeva People's Army, whose faction fields carry the tag PALA.</summary>
    Primeva,
}

/// <summary>How a vehicle reaches the ground, which is what decides whether the faction may have
/// it. A depot or factory issues the faction's own convoy-group kit; an aeroplane's cargo mounts
/// carry an entirely separate family of light vehicles that appear in no convoy group at all.</summary>
internal enum CommanderFieldingRoute
{
    /// <summary>Bought at a depot or produced by a factory.</summary>
    Depot,

    /// <summary>Flown in as an aircraft's cargo and unloaded.</summary>
    AirLanded,
}

/// <summary>The capability axes a split must keep covered on BOTH sides. Flags, so a roster entry
/// can cover several and a side's cover is the union of what it may field.</summary>
[Flags]
internal enum CommanderRosterAxis
{
    None = 0,

    /// <summary>Can move a capture bar — the game's own <c>captureStrength</c> above zero, which is
    /// what <see cref="CommanderPlatoonRole.Carrier"/> already means.</summary>
    Capture = 1 << 0,

    /// <summary>Can sit on ground and shoot back at infantry and light vehicles.</summary>
    Hold = 1 << 1,

    /// <summary>Can kill armour.</summary>
    AntiArmour = 1 << 2,

    /// <summary>Can kill aircraft.</summary>
    AntiAir = 1 << 3,

    /// <summary>Carries an anti-radiation missile, so it can clear an air-defence belt.</summary>
    SuppressRadar = 1 << 4,

    /// <summary>Can attack ground targets from the air.</summary>
    GroundAttack = 1 << 5,

    /// <summary>Can carry a ground vehicle as cargo.</summary>
    CargoLift = 1 << 6,

    /// <summary>Sees further than it shoots — a radar aeroplane or a ground radar truck.</summary>
    Scout = 1 << 7,

    /// <summary>Repairs buildings and builds surface-to-air sites.</summary>
    Repair = 1 << 8,
}

/// <summary>One roster entry: the game's own key for the type, and what it lets a side do.</summary>
internal readonly struct CommanderRosterEntry
{
    internal CommanderRosterEntry(string key, CommanderRosterAxis axes)
    {
        Key = key;
        Axes = axes;
    }

    /// <summary>For a vehicle, <c>VehicleDefinition.unitName</c>; for an aircraft,
    /// <c>AircraftDefinition.jsonKey</c>. Both are what the mod's own roster log lines print, so a
    /// table entry that stops matching is visible in the BepInEx console rather than silent.</summary>
    internal string Key { get; }

    /// <summary>What this type lets its side do. Read only by <see cref="CommanderFactionRoster.SelfCheck"/>.</summary>
    internal CommanderRosterAxis Axes { get; }
}

/// <summary>
/// Who may field what. ONE predicate for ground (<see cref="MayFieldVehicle"/>) and ONE for air
/// (<see cref="MayFlyAircraft"/>), read by every buy and launch path in the mod, so the player's
/// depot window, the computer's ground buying, the air-landed insertion cargo, forward-base
/// construction and the platoon and picket recipes all ask the same question.
/// </summary>
/// <remarks>
/// <para>Background: <c>conductor/designs/2026-09-16-faction-asymmetry-study.md</c>. The game
/// already ships near-distinct GROUND rosters — each faction's convoy groups — and the mod was
/// flattening them in two places. It ships no per-faction AIR roster at all, so the air split is
/// the mod's own and lives in the tables below.</para>
/// <para>Every key and every axis here was read off the running game's own log lines
/// (<c>Ground roster</c>, <c>Air roster</c> and <c>Insertion cargo roster</c>, 2026-09-16), not
/// reasoned about from names. Those lines print every mission, so a key that stops matching an
/// asset shows up in the console.</para>
/// <para>The AIR split below is the USER'S OWN, taken from the copy of
/// <c>conductor/designs/FACTION-ROSTER.md</c> they edited and handed back on 2026-09-16. That file
/// and these tables are meant to agree, and the file names each aircraft twice — a readable name and
/// the game's data key in brackets. The NAME is what the user edits; the key is what this class
/// matches on, so when the two disagree the name wins and the bracket is corrected in the file. The
/// vehicle tables, air-landed and depot, are unchanged by that edit.</para>
/// <para>This is a static table-and-predicate class rather than an <c>ICommanderService</c>: it has
/// no tick, no per-session state and nothing to reset. Its one cache is keyed on the game's
/// <c>Faction</c> asset, whose convoy groups do not change at runtime, so it never goes stale.</para>
/// </remarks>
internal static class CommanderFactionRoster
{
    /// <summary>
    /// The ground types BOTH sides may always field, by either route. The M12 Jackknife is here
    /// because NEITHER faction's convoy groups contain a repair vehicle (study section 3), so
    /// without this line the repair-vehicle lookup returns nothing for both sides and building
    /// repair and surface-to-air site construction both stop working. There is exactly one repair
    /// vehicle in the game, so sharing it is not a choice between two.
    /// </summary>
    internal static readonly CommanderRosterEntry[] SharedVehicles =
    {
        new("M12 Jackknife", CommanderRosterAxis.Repair),
    };

    /// <summary>
    /// The air-landable ground types BOTH sides may field. The HLT Radar Truck is the only ground
    /// radar any transport in the game carries as cargo, so splitting it would leave one side with
    /// no air-landable eyes at all — the same reason the two transports stay shared. Its BDF-style
    /// name is the asset's, not a statement about who owns it.
    /// </summary>
    internal static readonly CommanderRosterEntry[] SharedAirLandedVehicles =
    {
        new("HLT Radar Truck", CommanderRosterAxis.Scout),
    };

    /// <summary>
    /// Boscali's air-landable light vehicles: the AFV6 family. These appear in NO convoy group —
    /// they exist only as aircraft cargo — so the split of the cargo manifest is the mod's own, and
    /// it is what stops both sides air-landing the identical load they do today.
    /// </summary>
    internal static readonly CommanderRosterEntry[] BoscaliAirLandedVehicles =
    {
        new("AFV6 APC", CommanderRosterAxis.Capture | CommanderRosterAxis.Hold),
        new("AFV6 IFV", CommanderRosterAxis.Capture | CommanderRosterAxis.Hold | CommanderRosterAxis.AntiArmour),
        new("AFV6 AT", CommanderRosterAxis.Capture | CommanderRosterAxis.Hold | CommanderRosterAxis.AntiArmour),
        new("AFV6 AA", CommanderRosterAxis.AntiAir),
    };

    /// <summary>
    /// Primeva's air-landable light vehicles: the LCV25 family and the Hexhound pair. Same story as
    /// <see cref="BoscaliAirLandedVehicles"/> — none of these is in a convoy group either.
    /// </summary>
    internal static readonly CommanderRosterEntry[] PrimevaAirLandedVehicles =
    {
        new("LCV25 AT", CommanderRosterAxis.Capture | CommanderRosterAxis.Hold | CommanderRosterAxis.AntiArmour),
        new("LCV25 AA", CommanderRosterAxis.AntiAir),
        new("Hexhound SAM", CommanderRosterAxis.AntiAir),
        new("Hexhound GMG", CommanderRosterAxis.Hold),
    };

    /// <summary>
    /// The four airframes that CANNOT be split, because the game ships only one of each job.
    /// The SAH-46 Chicane is the only airframe with both a helicopter and an aeroplane pilot, which
    /// is what the mod's rotary close-support role tests for. The EW-25 Medusa is the only aircraft
    /// carrying the radar pod the early-warning role requires. And there are only two transports,
    /// which stay shared on the user's explicit decision (2026-09-16): splitting them would cost
    /// whichever side lost the VL-49 Tarantula the ability to air-land any vehicle that can take
    /// ground, because the UH-90 Ibis carries no capture-capable vehicle at all.
    /// </summary>
    internal static readonly CommanderRosterEntry[] SharedAirframes =
    {
        new("AttackHelo1", CommanderRosterAxis.GroundAttack),
        new("EW1", CommanderRosterAxis.Scout),
        new("UtilityHelo1", CommanderRosterAxis.CargoLift),
        new("QuadVTOL1", CommanderRosterAxis.CargoLift),
    };

    /// <summary>
    /// Boscali's own airframes, as the USER wrote them into
    /// <c>conductor/designs/FACTION-ROSTER.md</c> on 2026-09-16 and handed the file back. This split
    /// is their decision, not the study's and not the mod's: FS-20 Vortex (90), FS-12 Revoker (65),
    /// VT-7 Vagrant (29) and Alkyon AB-4 (390), the numbers being the game's own worth ratings,
    /// which the mod uses as the price.
    /// <para>Boscali is the side with the deep radar-suppression bench — three of its four airframes
    /// carry an anti-radiation missile — and the only side with a bomber. It is also the cheaper
    /// side for a proper fighter, at 65 against Primeva's 126.</para>
    /// </summary>
    internal static readonly CommanderRosterEntry[] BoscaliAirframes =
    {
        new("SmallFighter1", CommanderRosterAxis.AntiAir | CommanderRosterAxis.GroundAttack | CommanderRosterAxis.SuppressRadar),
        new("Fighter1", CommanderRosterAxis.AntiAir | CommanderRosterAxis.GroundAttack | CommanderRosterAxis.SuppressRadar),
        new("VTOLTrainer1", CommanderRosterAxis.AntiAir | CommanderRosterAxis.GroundAttack),
        new("FastBomber1", CommanderRosterAxis.AntiAir | CommanderRosterAxis.GroundAttack | CommanderRosterAxis.SuppressRadar),
    };

    /// <summary>
    /// Primeva's own airframes, from the same user-edited file on the same date: KR-67 Ifrit (126),
    /// A-19 Brawler (36), SFB-81 Darkreach (225) and T/A-30 Compass (22).
    /// <para>Primeva is the cheaper side for ground attack, at 36 against Boscali's 29-rated Vagrant
    /// and 65-rated Revoker, and it fields the game's heaviest strike aeroplane. The cost of the
    /// user's split, recorded here because it is the one property worth watching: exactly ONE
    /// Primeva airframe carries an anti-radiation missile, the KR-67 Ifrit at 126. The
    /// "each side can clear an air-defence belt" check below still passes, but for Primeva it
    /// passes on a single airframe that the side must also be able to afford.</para>
    /// </summary>
    internal static readonly CommanderRosterEntry[] PrimevaAirframes =
    {
        new("Multirole1", CommanderRosterAxis.AntiAir | CommanderRosterAxis.GroundAttack | CommanderRosterAxis.SuppressRadar),
        new("CAS1", CommanderRosterAxis.GroundAttack),
        new("Darkreach", CommanderRosterAxis.GroundAttack),
        new("trainer", CommanderRosterAxis.AntiAir | CommanderRosterAxis.GroundAttack),
    };

    /// <summary>
    /// Airframes DELIBERATELY on no list at all, so neither side may ever buy or launch them. The
    /// user removed the CI-22 Cricket (12) from both sides when they edited
    /// <c>conductor/designs/FACTION-ROSTER.md</c> on 2026-09-16 and confirmed the omission was
    /// intended rather than an editing slip.
    /// <para>This table is NOT consulted by <see cref="MayFlyAircraft"/>, and must not be: that
    /// predicate is an allow-list, so an airframe on no list is already refused, and adding a
    /// deny-list would be a second rule saying the same thing. The table exists so the exclusion is
    /// a fact the self-check can name, rather than an absence nobody would notice being undone — and
    /// so the <c>Air roster</c> log line's <c>NOT ON THIS FACTION'S ROSTER, never bought or
    /// launched</c> tail on the Cricket reads as the split working rather than as a missing entry.
    /// </para>
    /// </summary>
    internal static readonly CommanderRosterEntry[] ExcludedAirframes =
    {
        new("COIN", CommanderRosterAxis.GroundAttack),
    };

    /// <summary>
    /// Every axis a side must be able to cover after the split, or something the mod does stops
    /// working for it: no capture means it cannot take ground; no radar suppression means it can
    /// never clear an air-defence belt, which matters more than it used to because a sortie now
    /// WAITS for a sweep; no cargo lift means flown-in forward bases and air-mobile platoons are
    /// dead for that side; no repair means building repair and site construction stop.
    /// </summary>
    internal const CommanderRosterAxis RequiredAxes =
        CommanderRosterAxis.Capture
        | CommanderRosterAxis.Hold
        | CommanderRosterAxis.AntiArmour
        | CommanderRosterAxis.AntiAir
        | CommanderRosterAxis.SuppressRadar
        | CommanderRosterAxis.GroundAttack
        | CommanderRosterAxis.CargoLift
        | CommanderRosterAxis.Scout
        | CommanderRosterAxis.Repair;

    /// <summary>Convoy-group membership by faction asset. The game's convoy groups are asset data
    /// that does not change at runtime, so this never needs invalidating.</summary>
    private static readonly Dictionary<Faction, HashSet<VehicleDefinition>> ConvoyMembership = new();

    /// <summary>
    /// The faction's own three name fields in one string, which is what the side test reads.
    /// Moved here from <c>CommanderSamSiteService</c>, where the same expression appeared twice
    /// (Reuse rule 5: the second instance is generalised, not forked); both of those call sites
    /// now read <see cref="SideOf"/> instead.
    /// </summary>
    internal static string FactionIdentity(FactionHQ? hq)
    {
        return $"{hq?.faction?.factionTag} {hq?.faction?.factionName} {hq?.faction?.factionExtendedName}";
    }

    /// <summary>
    /// Which stock side an HQ is, by its own faction tag and names. Unknown for anything else, and
    /// an Unknown side is NEVER restricted — a third faction, a renamed one or a mission the mod
    /// has not seen must keep every option it has today rather than silently ending up with an
    /// empty roster.
    /// </summary>
    internal static CommanderFactionSide SideOf(FactionHQ? hq)
    {
        return SideOfIdentity(FactionIdentity(hq));
    }

    /// <summary>The pure half of <see cref="SideOf"/>, so the self-check can exercise it without an
    /// HQ. PALA is tested first only because the two tags cannot both appear on a stock faction.</summary>
    internal static CommanderFactionSide SideOfIdentity(string? identity)
    {
        if (string.IsNullOrEmpty(identity))
        {
            return CommanderFactionSide.Unknown;
        }

        if (identity!.IndexOf("PALA", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return CommanderFactionSide.Primeva;
        }

        return identity.IndexOf("BDF", StringComparison.OrdinalIgnoreCase) >= 0
            ? CommanderFactionSide.Boscali
            : CommanderFactionSide.Unknown;
    }

    /// <summary>
    /// THE ground predicate: may this faction put this vehicle on the ground by this route?
    /// <para>A vehicle in the faction's own convoy groups is always yes — that is the game's own
    /// answer and the mod does not second-guess it. The shared list is yes by either route. The
    /// air-landed lists are yes only for cargo, because those families appear in no convoy group
    /// and letting a depot sell them would invent a ground roster the game never issued.</para>
    /// <para>Fails OPEN for a null HQ, a null definition or an unknown faction, so a mission the
    /// tables do not know about behaves exactly as it does today.</para>
    /// </summary>
    internal static bool MayFieldVehicle(FactionHQ? hq, VehicleDefinition? definition, CommanderFieldingRoute route)
    {
        if (hq?.faction == null || definition == null)
        {
            return true;
        }

        CommanderFactionSide side = SideOf(hq);
        if (side == CommanderFactionSide.Unknown)
        {
            return true;
        }

        if (IsInConvoyGroups(hq, definition))
        {
            return true;
        }

        string name = definition.unitName ?? string.Empty;
        if (Contains(SharedVehicles, name))
        {
            return true;
        }

        if (route != CommanderFieldingRoute.AirLanded)
        {
            return false;
        }

        return Contains(SharedAirLandedVehicles, name) || Contains(AirLandedVehiclesFor(side), name);
    }

    /// <summary>
    /// THE air predicate: may this faction fly this airframe? The game has no per-faction aircraft
    /// roster of its own — what a base launches is baked into the hangar prefab — so the answer is
    /// the mod's table and nothing else. Fails open for an unknown faction, as the ground predicate
    /// does.
    /// </summary>
    internal static bool MayFlyAircraft(FactionHQ? hq, AircraftDefinition? definition)
    {
        if (hq?.faction == null || definition == null)
        {
            return true;
        }

        CommanderFactionSide side = SideOf(hq);
        if (side == CommanderFactionSide.Unknown)
        {
            return true;
        }

        return MayFlyAirframeKey(side, definition.jsonKey ?? string.Empty);
    }

    /// <summary>
    /// The pure half of <see cref="MayFlyAircraft"/>, so the self-check can exercise it without an
    /// HQ or an <c>AircraftDefinition</c> — the same shape as <see cref="SideOfIdentity"/> under
    /// <see cref="SideOf"/>.
    /// <para>It is an ALLOW-LIST and nothing else: yes for the shared airframes, yes for the side's
    /// own, no for everything else. An airframe on no list at all is therefore a clean refusal, not
    /// an accidental allow — which is exactly what the CI-22 Cricket's deliberate exclusion depends
    /// on (see <see cref="ExcludedAirframes"/>). An Unknown side still gets everything, because the
    /// tables say nothing about a third or renamed faction.</para>
    /// </summary>
    internal static bool MayFlyAirframeKey(CommanderFactionSide side, string? key)
    {
        if (side == CommanderFactionSide.Unknown)
        {
            return true;
        }

        string airframeKey = key ?? string.Empty;
        return Contains(SharedAirframes, airframeKey) || Contains(AirframesFor(side), airframeKey);
    }

    /// <summary>The air-landable vehicles one side owns outright, empty for an unknown faction.</summary>
    internal static CommanderRosterEntry[] AirLandedVehiclesFor(CommanderFactionSide side)
    {
        return side switch
        {
            CommanderFactionSide.Boscali => BoscaliAirLandedVehicles,
            CommanderFactionSide.Primeva => PrimevaAirLandedVehicles,
            _ => Array.Empty<CommanderRosterEntry>(),
        };
    }

    /// <summary>The airframes one side owns outright, empty for an unknown faction.</summary>
    internal static CommanderRosterEntry[] AirframesFor(CommanderFactionSide side)
    {
        return side switch
        {
            CommanderFactionSide.Boscali => BoscaliAirframes,
            CommanderFactionSide.Primeva => PrimevaAirframes,
            _ => Array.Empty<CommanderRosterEntry>(),
        };
    }

    /// <summary>Everything one side may field that the MOD decides, as one set of axes: its own two
    /// lists plus both shared lists. The convoy groups are deliberately not in here — they are the
    /// game's data, checked live at mission start by <see cref="DescribeLiveCoverage"/>.</summary>
    internal static CommanderRosterAxis ModDecidedAxesFor(CommanderFactionSide side)
    {
        return AxesOf(AirLandedVehiclesFor(side))
            | AxesOf(AirframesFor(side))
            | AxesOf(SharedVehicles)
            | AxesOf(SharedAirLandedVehicles)
            | AxesOf(SharedAirframes);
    }

    private static CommanderRosterAxis AxesOf(CommanderRosterEntry[] entries)
    {
        CommanderRosterAxis axes = CommanderRosterAxis.None;
        for (int i = 0; i < entries.Length; i++)
        {
            axes |= entries[i].Axes;
        }

        return axes;
    }

    private static bool Contains(CommanderRosterEntry[] entries, string key)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            if (string.Equals(entries[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the vehicle is one of the faction's own convoy-group units — the game's own
    /// definition of a faction's ground roster. Cached on the faction asset, which cannot change.</summary>
    private static bool IsInConvoyGroups(FactionHQ hq, VehicleDefinition definition)
    {
        Faction faction = hq.faction;
        if (!ConvoyMembership.TryGetValue(faction, out HashSet<VehicleDefinition> members))
        {
            members = new HashSet<VehicleDefinition>();
            List<Faction.ConvoyGroup> convoyGroups = faction.GetConvoyGroups();
            for (int groupIndex = 0; groupIndex < convoyGroups.Count; groupIndex++)
            {
                List<Faction.ConvoyUnit> constituents = convoyGroups[groupIndex].Constituents;
                for (int unitIndex = 0; unitIndex < constituents.Count; unitIndex++)
                {
                    if (constituents[unitIndex].Type is VehicleDefinition convoyDefinition)
                    {
                        members.Add(convoyDefinition);
                    }
                }
            }

            ConvoyMembership[faction] = members;
        }

        return members.Contains(definition);
    }

    /// <summary>
    /// What this faction can actually do on the ground once the predicate has been applied, read
    /// from the LIVE roster rather than from the tables — the answer the tables cannot give, because
    /// the convoy groups belong to the game. Printed beside the existing roster log lines, so a
    /// faction left short of an axis says so in the console on the mission it happens.
    /// </summary>
    internal static CommanderRosterAxis DescribeLiveCoverage(IReadOnlyList<VehicleDefinition> fieldable)
    {
        CommanderRosterAxis axes = CommanderRosterAxis.None;
        for (int i = 0; i < fieldable.Count; i++)
        {
            axes |= LiveAxesOf(fieldable[i]);
        }

        return axes;
    }

    /// <summary>
    /// What ONE live vehicle definition covers, from the game's own fields rather than a name — the
    /// capture bar for <see cref="CommanderRosterAxis.Capture"/>, the platoon role for the rest, and
    /// an actual <c>Repairer</c> component for repair. Anything that can take ground can also hold
    /// it, which is why Capture always brings Hold with it.
    /// </summary>
    private static CommanderRosterAxis LiveAxesOf(VehicleDefinition? definition)
    {
        if (definition == null)
        {
            return CommanderRosterAxis.None;
        }

        CommanderRosterAxis axes = CommanderRosterAxis.None;
        if (definition.captureStrength > 0f)
        {
            axes |= CommanderRosterAxis.Capture | CommanderRosterAxis.Hold;
        }

        switch (CommanderPlatoonRoles.Of(definition))
        {
            case CommanderPlatoonRole.Armour:
                axes |= CommanderRosterAxis.AntiArmour | CommanderRosterAxis.Hold;
                break;
            case CommanderPlatoonRole.Carrier:
                axes |= CommanderRosterAxis.Hold;
                break;
            case CommanderPlatoonRole.AirDefence:
                axes |= CommanderRosterAxis.AntiAir;
                break;
        }

        if (definition.unitPrefab != null && definition.unitPrefab.GetComponentInChildren<Repairer>(true) != null)
        {
            axes |= CommanderRosterAxis.Repair;
        }

        return axes;
    }

    /// <summary>The missing axes named in plain words, or an empty string when nothing is missing —
    /// the tail of the roster log line.</summary>
    internal static string DescribeMissingAxes(CommanderRosterAxis covered, CommanderRosterAxis required)
    {
        CommanderRosterAxis missing = required & ~covered;
        return missing == CommanderRosterAxis.None ? string.Empty : missing.ToString();
    }

    /// <summary>
    /// The split's own safety properties, checked at plugin load against the tables rather than
    /// against the running game. Everything here is a property of the mod's own decision: the live
    /// half — whether a faction's convoy groups still contain what the axes need — is checked at
    /// mission start and printed beside the roster lines instead.
    /// </summary>
    internal static void SelfCheck()
    {
        List<string> failures = new();

        // No airframe may be on both sides' exclusive lists, and nothing shared may also be owned.
        for (int i = 0; i < BoscaliAirframes.Length; i++)
        {
            string key = BoscaliAirframes[i].Key;
            Expect(failures, $"{key} is not also Primeva's", Contains(PrimevaAirframes, key), false);
            Expect(failures, $"{key} is not also shared", Contains(SharedAirframes, key), false);
        }

        for (int i = 0; i < PrimevaAirframes.Length; i++)
        {
            string key = PrimevaAirframes[i].Key;
            Expect(failures, $"{key} is not also shared", Contains(SharedAirframes, key), false);
        }

        for (int i = 0; i < BoscaliAirLandedVehicles.Length; i++)
        {
            string key = BoscaliAirLandedVehicles[i].Key;
            Expect(failures, $"{key} is not also Primeva's", Contains(PrimevaAirLandedVehicles, key), false);
            Expect(failures, $"{key} is not also a shared vehicle", Contains(SharedVehicles, key), false);
        }

        // Every axis covered on BOTH sides, which is the whole point of the split.
        Expect(
            failures,
            "Boscali covers every required axis",
            DescribeMissingAxes(ModDecidedAxesFor(CommanderFactionSide.Boscali), RequiredAxes),
            string.Empty);
        Expect(
            failures,
            "Primeva covers every required axis",
            DescribeMissingAxes(ModDecidedAxesFor(CommanderFactionSide.Primeva), RequiredAxes),
            string.Empty);

        // The four risks the study named, one case each, so a retune that breaks one says which.
        Expect(
            failures,
            "Boscali can air-land something that takes ground",
            HasAxis(BoscaliAirLandedVehicles, CommanderRosterAxis.Capture),
            true);
        Expect(
            failures,
            "Primeva can air-land something that takes ground",
            HasAxis(PrimevaAirLandedVehicles, CommanderRosterAxis.Capture),
            true);
        Expect(
            failures,
            "Boscali can air-land air defence",
            HasAxis(BoscaliAirLandedVehicles, CommanderRosterAxis.AntiAir),
            true);
        Expect(
            failures,
            "Primeva can air-land air defence",
            HasAxis(PrimevaAirLandedVehicles, CommanderRosterAxis.AntiAir),
            true);
        Expect(
            failures,
            "Boscali can clear an air-defence belt",
            HasAxis(BoscaliAirframes, CommanderRosterAxis.SuppressRadar),
            true);
        Expect(
            failures,
            "Primeva can clear an air-defence belt",
            HasAxis(PrimevaAirframes, CommanderRosterAxis.SuppressRadar),
            true);
        Expect(
            failures,
            "both sides can lift cargo",
            HasAxis(SharedAirframes, CommanderRosterAxis.CargoLift),
            true);
        Expect(
            failures,
            "the repair vehicle is shared",
            HasAxis(SharedVehicles, CommanderRosterAxis.Repair),
            true);

        // A fighter ladder needs two rungs, or the side has nothing to climb to as threat rises. It
        // is a FLOOR, not an exact count: the user's 2026-09-16 split gives Boscali four airframes
        // that can fight in the air and Primeva two, and pinning the old exact 2 would have failed
        // on the richer side for being richer.
        ExpectAtLeast(failures, "Boscali has at least two air-superiority airframes", CountWithAxis(BoscaliAirframes, CommanderRosterAxis.AntiAir), 2);
        ExpectAtLeast(failures, "Primeva has at least two air-superiority airframes", CountWithAxis(PrimevaAirframes, CommanderRosterAxis.AntiAir), 2);

        // The CI-22 Cricket is on NO list on purpose (user, 2026-09-16). Because the air predicate
        // is an allow-list, "on no list" already means refused — these cases pin that it stays that
        // way, for both sides, by the same door the game asks through.
        for (int i = 0; i < ExcludedAirframes.Length; i++)
        {
            string key = ExcludedAirframes[i].Key;
            Expect(failures, $"{key} is not Boscali's", Contains(BoscaliAirframes, key), false);
            Expect(failures, $"{key} is not Primeva's", Contains(PrimevaAirframes, key), false);
            Expect(failures, $"{key} is not shared", Contains(SharedAirframes, key), false);
            Expect(failures, $"Boscali may not fly {key}", MayFlyAirframeKey(CommanderFactionSide.Boscali, key), false);
            Expect(failures, $"Primeva may not fly {key}", MayFlyAirframeKey(CommanderFactionSide.Primeva, key), false);
        }

        // The allow-list still answers yes to what each side owns and to the shared airframes, and
        // still fails OPEN for a faction the tables do not know. Without these three the exclusion
        // cases above would also pass on a predicate that refused everything.
        Expect(failures, "Boscali may fly its own Revoker", MayFlyAirframeKey(CommanderFactionSide.Boscali, "Fighter1"), true);
        Expect(failures, "Primeva may fly the shared Medusa", MayFlyAirframeKey(CommanderFactionSide.Primeva, "EW1"), true);
        Expect(failures, "an unknown faction is never restricted in the air", MayFlyAirframeKey(CommanderFactionSide.Unknown, "COIN"), true);

        // The side test, including the one that matters most: an unknown faction is never restricted.
        Expect(failures, "BDF reads as Boscali", SideOfIdentity("BDF Boscali Boscali Defence Force"), CommanderFactionSide.Boscali);
        Expect(failures, "PALA reads as Primeva", SideOfIdentity("PALA Primeva People's Army"), CommanderFactionSide.Primeva);
        Expect(failures, "a third faction reads as unknown", SideOfIdentity("CSA Contractors"), CommanderFactionSide.Unknown);
        Expect(failures, "an empty identity reads as unknown", SideOfIdentity(string.Empty), CommanderFactionSide.Unknown);

        if (failures.Count == 0)
        {
            CommanderPlugin.Log.LogDebug("Faction roster self-check passed.");
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Faction roster self-check FAILED: {failures[i]}");
        }
    }

    private static bool HasAxis(CommanderRosterEntry[] entries, CommanderRosterAxis axis)
    {
        return (AxesOf(entries) & axis) != CommanderRosterAxis.None;
    }

    private static int CountWithAxis(CommanderRosterEntry[] entries, CommanderRosterAxis axis)
    {
        int count = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            if ((entries[i].Axes & axis) != CommanderRosterAxis.None)
            {
                count++;
            }
        }

        return count;
    }

    private static void Expect(List<string> failures, string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, int actual, int expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    /// <summary>A floor rather than an exact count, for the properties where having MORE than the
    /// minimum is a legitimate roster and only having fewer is a defect.</summary>
    private static void ExpectAtLeast(List<string> failures, string name, int actual, int minimum)
    {
        if (actual < minimum)
        {
            failures.Add($"{name}: expected at least {minimum}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            failures.Add($"{name}: expected '{expected}', got '{actual}'");
        }
    }

    private static void Expect(List<string> failures, string name, CommanderFactionSide actual, CommanderFactionSide expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }
}
