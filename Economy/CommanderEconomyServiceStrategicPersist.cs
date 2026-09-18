using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The strategic save's money side (track <c>strategic-save_20260917</c>, user decisions
/// 2026-09-17): what a faction is WORTH when the developer stops a long match, putting that number
/// back when the mission is restarted, and rebuilding the mines, factories and docks it was
/// standing on.
/// </summary>
/// <remarks>
/// <para>
/// The scope decision that shapes all of this: units are refunded at their value and never
/// recreated. A mission restart puts the designer's opening layout back and nothing the commander
/// bought comes with it, so a faithful world is not on the table (feasibility study). What IS on
/// the table is not losing the value of the force, and that is a number.
/// </para>
/// <para>
/// The property that matters most, and the one a later edit is most likely to break: <b>a
/// save-and-reload leaves a faction no richer and no poorer</b>, apart from what it deliberately
/// spends on its garrisons. Everything here is arranged so that holds in every branch. A building
/// that comes back in kind is NOT cashed in — cashing it in as well is the double-count that would
/// make a faction richer by the price of its whole economy on every save — and one that could not
/// be put back has its sunk cost credited instead, so it is not quietly lost either.
/// <see cref="StrategicValueConserved"/> states the property and is checked at plugin load.
/// </para>
/// </remarks>
internal sealed partial class CommanderEconomyService : ICommanderPersistStrategic
{
    /// <summary>The war chests read out of the strategic save, by faction name, waiting to be
    /// applied. <see cref="RestoreStrategic"/> is load-only by contract; the store calls
    /// <see cref="ApplyStrategicTreasuries"/> once the whole save is in, because the money has to be
    /// on the table before anything is bought out of it.</summary>
    private readonly Dictionary<string, float> strategicTreasuries = new(StringComparer.Ordinal);

    /// <summary>The mines, factories and docks read out of the strategic save, waiting for
    /// <see cref="ApplyStrategicEconomyBuildings"/>.</summary>
    private readonly List<CommanderStrategicEconomyBuildingRecord> strategicEconomyBuildings = new();

    /// <summary>Money held outside the treasury, by faction name, waiting for
    /// <see cref="ApplyStrategicSavings"/>.</summary>
    private readonly List<CommanderStrategicSavingsRecord> strategicSavings = new();

    /// <summary>What the last strategic load credited back for economy buildings it could NOT put
    /// back, for the conservation line in the log. Zero on a load where everything was rebuilt,
    /// which is the normal case: these buildings come back in kind and cost nothing.</summary>
    internal float StrategicEconomyRefund { get; private set; }

    /// <summary>
    /// What one live unit is worth to the war chest. ONE definition, over the two places the mod
    /// already priced a live unit inline — the stuck-on-deck aircraft refund
    /// (<c>CommanderOperationsAirPlatoonCap</c>) and the idle-pool vehicle sale
    /// (<c>CommanderOperationsService</c>), both of which read <c>UnitDefinition.value</c>, the
    /// game's own worth rating carried by aircraft, vehicle, ship and building definitions alike.
    /// </summary>
    /// <remarks>
    /// Buildings are the whole difficulty, because three of them were not bought at that rating
    /// (study, Part 3):
    /// <list type="number">
    /// <item>A catalogue structure carries a cost multiplier, so it was bought at
    /// <see cref="GetStructureCost"/>, not at its rating.</item>
    /// <item>A mine, factory or naval dock was bought at a FLAT configured price, and the upgrades
    /// paid on top are sunk cost — <see cref="StrategicFlatSunkCost"/> is the ladder. That figure is
    /// NOT what they are cashed in for, because they are rebuilt in kind; it is what is credited
    /// back on the one branch where the rebuild fails.</item>
    /// <item>A building the commander did not build is worth nothing, because a fresh mission puts
    /// every mission-authored structure back and paying for one would be paying for property the
    /// faction still owns.</item>
    /// </list>
    /// A forward base's structures are worth nothing either, and for the same reason the mines are
    /// not: it is rebuilt in kind and free, so cashing its buildings in would pay for them twice.
    /// <para>
    /// A disabled unit is worth nothing. This matters more than it looks: a building knocked out
    /// when its base changed hands stays in the old faction's unit list, so a refund walk that
    /// trusts that list alone pays the wrong treasury (study, Part 3).
    /// </para>
    /// </remarks>
    internal static float StrategicUnitValue(Unit? unit)
    {
        if (unit == null || unit.disabled || unit.definition == null)
        {
            return 0f;
        }

        if (unit.definition is not BuildingDefinition building)
        {
            // Aircraft, ground vehicles and ships all carry the game's own worth rating.
            return Mathf.Max(0f, unit.definition.value);
        }

        return StrategicBuildingValue(
            IsCommanderBuilt(unit),
            StrategicRebuiltInKind(StrategicBuildKindOf(unit), CommanderOperationsService.IsForwardBaseBuilding(unit)),
            GetStructureCost(building));
    }

    /// <summary>
    /// The building decision table, pure so it can be checked at load. Two ways to be worth nothing
    /// to the war chest, and they are different reasons: a building the commander did not build is
    /// property the faction still owns after the restart, and a building that is REBUILT IN KIND is
    /// coming back for free — cashing it in as well is the double-count that would make a faction
    /// richer by the price of its whole economy on every save.
    /// </summary>
    internal static float StrategicBuildingValue(bool commanderBuilt, bool rebuiltInKind, float catalogueCost)
    {
        if (!commanderBuilt || rebuiltInKind)
        {
            return 0f;
        }

        return Mathf.Max(0f, catalogueCost);
    }

    /// <summary>
    /// Whether a building comes back in kind rather than as money (user decision 2026-09-17, which
    /// put mines, factories and docks on the same footing as forward bases). Both are the strategic
    /// picture rather than units: a forward base is ten minutes and a delivery flight, and a mine is
    /// why a resource site earns anything at all, so cashing them in would collapse each faction's
    /// income on every load until the commander rebuilt them — resuming a match differently rather
    /// than resuming it. Pure, and the one place the rule is decided.
    /// </summary>
    internal static bool StrategicRebuiltInKind(CommanderBuildKind kind, bool forwardBaseBuilding)
    {
        return forwardBaseBuilding
            || kind == CommanderBuildKind.Mine
            || kind == CommanderBuildKind.Factory
            || kind == CommanderBuildKind.NavalDock;
    }

    /// <summary>
    /// What rebuilding one economy building costs the faction: NOTHING. It is put back the way a
    /// forward base is, through the mod's own spawn, before either commander starts spending
    /// (user decision 2026-09-17). Stated as a function rather than as the absence of a line so a
    /// later edit that starts charging has to change it and fail the check that pins it.
    /// </summary>
    internal static float StrategicEconomyRebuildCharge(CommanderBuildKind kind, int level)
    {
        _ = kind;
        _ = level;
        return 0f;
    }

    /// <summary>
    /// Everything a mine, factory or naval dock cost its owner to get to
    /// <paramref name="level"/>: the flat purchase price plus every upgrade paid on the way. Pure,
    /// and the reason it is not <see cref="GetStructureCost"/> is that none of these three was ever
    /// bought at the game's worth rating (study, Part 3). A level below one is treated as one,
    /// because a building that exists was bought.
    /// </summary>
    internal static float StrategicFlatSunkCost(CommanderBuildKind kind, int level)
    {
        if (kind == CommanderBuildKind.Structure || kind == CommanderBuildKind.None)
        {
            return 0f;
        }

        float total = Mathf.Max(0f, GetBuildCost(kind));
        int reached = Mathf.Clamp(level, 1, MaxLevel);
        for (int paidFrom = 1; paidFrom < reached; paidFrom++)
        {
            total += Mathf.Max(0f, StrategicUpgradeCost(kind, paidFrom));
        }

        return total;
    }

    /// <summary>The price of taking one of the three flat-priced buildings from
    /// <paramref name="level"/> to the next, dispatched by kind over the mod's existing ladders so
    /// there is no second price list. Pure.</summary>
    internal static float StrategicUpgradeCost(CommanderBuildKind kind, int level)
    {
        return kind switch
        {
            CommanderBuildKind.Mine => GetMineUpgradeCost(level),
            CommanderBuildKind.Factory => GetFactoryUpgradeCost(level),
            CommanderBuildKind.NavalDock => GetNavalDockUpgradeCost(level),
            _ => 0f,
        };
    }

    /// <summary>
    /// The conservation rule, pure and the property this whole track is judged on: after a
    /// save-and-reload a faction holds exactly what it held, plus what its units were cashed in for,
    /// plus anything credited back for an economy building that could not be put back, minus what it
    /// deliberately spent on garrisons. Anything else breaks it — a garrison handed over free, a
    /// building both cashed in AND rebuilt (the double-count that would make a faction richer by the
    /// price of its whole economy on every save), or a refund that simply vanished.
    /// </summary>
    internal static bool StrategicValueConserved(
        float fundsBefore, float cashedIn, float garrisonSpend, float economyRefund, float fundsAfter)
    {
        return Mathf.Approximately(fundsAfter, fundsBefore + cashedIn + economyRefund - garrisonSpend);
    }

    /// <summary>Which of the three flat-priced kinds a live building is, or
    /// <see cref="CommanderBuildKind.Structure"/> for an ordinary catalogue building. Membership of
    /// the level tables is the mod's own discriminator (<see cref="IsBuiltMine"/>,
    /// <see cref="IsBuiltNavalDock"/>); a factory is the game's own <c>Factory</c> component, which
    /// is what <c>GetFactoryLevel</c> already keys on.</summary>
    internal static CommanderBuildKind StrategicBuildKindOf(Unit? unit)
    {
        CommanderEconomyService? service = Instance;
        if (unit == null || service == null)
        {
            return CommanderBuildKind.Structure;
        }

        if (service.IsBuiltMine(unit))
        {
            return CommanderBuildKind.Mine;
        }

        if (service.IsBuiltNavalDock(unit))
        {
            return CommanderBuildKind.NavalDock;
        }

        return unit.TryGetComponent(out Factory _) ? CommanderBuildKind.Factory : CommanderBuildKind.Structure;
    }

    /// <summary>The upgrade level of one of the three flat-priced buildings, defaulting to one —
    /// the level a freshly bought building is at, and the level a factory absent from the table is
    /// at, which is the same default <c>GetFactoryLevel</c> already applies.</summary>
    private static int StrategicLevelOf(Unit unit, CommanderBuildKind kind)
    {
        CommanderEconomyService? service = Instance;
        if (service == null)
        {
            return 1;
        }

        return kind switch
        {
            CommanderBuildKind.Mine => service.mineLevels.TryGetValue(unit, out int mine) ? mine : 1,
            CommanderBuildKind.NavalDock => service.dockLevels.TryGetValue(unit, out int dock) ? dock : 1,
            CommanderBuildKind.Factory => service.factoryLevels.TryGetValue(unit, out int factory) ? factory : 1,
            _ => 1,
        };
    }

    /// <summary>
    /// The whole war chest for one faction: the money in the treasury plus the cash value of
    /// everything of its own that is standing. A forward-base order still DELIVERING contributes
    /// nothing — it is cancelled rather than refunded, which is the rule the mod already applies
    /// everywhere else on the delivery path (study, Part 3).
    /// </summary>
    internal static float StrategicWarChest(FactionHQ hq, out int unitsCashedIn, out float cashedInValue)
    {
        unitsCashedIn = 0;
        cashedInValue = 0f;
        if (hq == null)
        {
            return 0f;
        }

        if (hq.factionUnits != null)
        {
            foreach (PersistentID id in hq.factionUnits)
            {
                if (!id.TryGetUnit(out Unit unit))
                {
                    continue;
                }

                float value = StrategicUnitValue(unit);
                if (value > 0f)
                {
                    cashedInValue += value;
                    unitsCashedIn++;
                }
            }
        }

        return hq.factionFunds + cashedInValue;
    }

    public void SnapshotStrategic(CommanderStrategicWriter w)
    {
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq?.faction == null || string.IsNullOrEmpty(hq.faction.factionName))
            {
                continue;
            }

            float chest = StrategicWarChest(hq, out int cashedIn, out float cashedInValue);
            w.Snapshot.Treasuries.Add(new CommanderStrategicTreasuryRecord
            {
                Faction = hq.faction.factionName,
                Funds = chest,
            });
            w.Snapshot.Savings.Add(new CommanderStrategicSavingsRecord
            {
                Faction = hq.faction.factionName,
                StructureSavings = StructureSavingsFor(hq),
            });
            CommanderPlugin.Log.LogInfo(
                $"Strategic save: {hq.faction.factionName} holds {hq.factionFunds:0} and cashes in "
                    + $"{cashedIn} units worth {cashedInValue:0}, for a war chest of {chest:0}.");
        }

        SnapshotStrategicEconomyBuildings(w);
    }

    /// <summary>
    /// Every mine, factory and naval dock the commander built, with its level and its ground. These
    /// are the buildings that are bought BACK rather than left as money, because a mine is what
    /// owns a resource site: site ownership follows the building standing on it, so without the
    /// mine the site is neutral however much cash the faction holds.
    /// </summary>
    private void SnapshotStrategicEconomyBuildings(CommanderStrategicWriter w)
    {
        foreach (Unit unit in builtUnits)
        {
            if (unit == null || unit.disabled || unit.definition is not BuildingDefinition)
            {
                continue;
            }

            CommanderBuildKind kind = StrategicBuildKindOf(unit);
            if (kind == CommanderBuildKind.Structure || kind == CommanderBuildKind.None)
            {
                // An ordinary catalogue structure is cashed in and not rebuilt: nothing in the
                // strategic picture hangs off where a radar or a bunker stood.
                continue;
            }

            FactionHQ? owner = unit.NetworkHQ;
            if (owner?.faction == null || string.IsNullOrEmpty(owner.faction.factionName))
            {
                continue;
            }

            GlobalPosition position = unit.transform.GlobalPosition();
            w.Snapshot.EconomyBuildings.Add(new CommanderStrategicEconomyBuildingRecord
            {
                Faction = owner.faction.factionName,
                Kind = kind.ToString(),
                Level = StrategicLevelOf(unit, kind),
                X = position.x,
                Y = position.y,
                Z = position.z,
                ProductionJsonKey = StrategicFactoryProductionKey(unit, kind),
            });
        }
    }

    /// <summary>The <c>jsonKey</c> of what a factory was producing, so the rebuilt one produces the
    /// same thing. Empty for anything that is not a factory.</summary>
    private static string StrategicFactoryProductionKey(Unit unit, CommanderBuildKind kind)
    {
        if (kind != CommanderBuildKind.Factory || !unit.TryGetComponent(out Factory factory))
        {
            return string.Empty;
        }

        // The game's own back-reference to what the factory makes. A jsonKey is the one identity
        // that survives a mission restart, which is why the key is saved rather than the definition.
        return factory.ProductionUnit?.jsonKey ?? string.Empty;
    }

    public void RestoreStrategic(CommanderStrategicReader r)
    {
        strategicEconomyBuildings.Clear();
        strategicEconomyBuildings.AddRange(r.Snapshot.EconomyBuildings);
    }

    /// <summary>
    /// Loads the MONEY half of a strategic save, separately from the load-only fan-out and much
    /// earlier than it. The enemy commander's first review fires on its own first tick and opens its
    /// treasury there, so the war chest and the out-of-treasury pots have to be in hand before the
    /// world rebuild's settle, not after it.
    /// </summary>
    internal void LoadStrategicMoney(CommanderStrategicSnapshot snapshot)
    {
        strategicTreasuries.Clear();
        List<CommanderStrategicTreasuryRecord> records = snapshot.Treasuries;
        for (int i = 0; i < records.Count; i++)
        {
            if (!string.IsNullOrEmpty(records[i].Faction))
            {
                strategicTreasuries[records[i].Faction] = Mathf.Max(0f, records[i].Funds);
            }
        }

        strategicSavings.Clear();
        strategicSavings.AddRange(snapshot.Savings);
    }

    /// <summary>
    /// Puts the saved war chests on the table. Called by
    /// <see cref="CommanderStrategicSaveStore"/> after the whole save is loaded and BEFORE anything
    /// is bought back out of it. Returns how many factions were funded, for the log.
    /// </summary>
    internal int ApplyStrategicTreasuries()
    {
        int funded = 0;
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq?.faction == null || !hq.IsServer)
            {
                // factionFunds is a server SyncVar, so this is a no-op on a pure multiplayer
                // client — the host runs the economy for everyone.
                continue;
            }

            if (!strategicTreasuries.TryGetValue(hq.faction.factionName, out float chest))
            {
                continue;
            }

            hq.SetFunds(chest);
            funded++;
            CommanderAiLog.Note(hq, $"strategic load: war chest set to {FundsLabel(chest)}.");
            CommanderPlugin.Log.LogInfo(
                $"Strategic load: {hq.faction.factionName} war chest set to {chest:0}.");
        }

        strategicTreasuries.Clear();
        return funded;
    }

    /// <summary>Puts each commander's out-of-treasury pots back, so a load does not reset every
    /// saving-up decision it had made.</summary>
    internal void ApplyStrategicSavings()
    {
        for (int i = 0; i < strategicSavings.Count; i++)
        {
            CommanderStrategicSavingsRecord record = strategicSavings[i];
            FactionHQ? hq = StrategicHqByName(record.Faction);
            if (hq == null || !hq.IsServer)
            {
                continue;
            }

            structureSavings[hq] = Mathf.Max(0f, record.StructureSavings);
        }

        CommanderEnemyCommanderService.Instance?.ApplyStrategicSavings(strategicSavings);
        strategicSavings.Clear();
    }

    /// <summary>
    /// Rebuilds the mines, factories and docks, buying each one back at exactly the sunk cost it
    /// was cashed in for. Mines first, because a mine is what owns a resource site and the garrison
    /// step that follows must see those sites already owned; then factories, then docks, which is
    /// the same order the commander's own structure rung spends in. Whole or nothing per building,
    /// and a building that cannot be afforded or cannot be placed leaves its money in the
    /// treasury — which is what keeps the value conserved either way.
    /// </summary>
    internal void ApplyStrategicEconomyBuildings()
    {
        StrategicEconomyRefund = 0f;
        int rebuilt = 0;
        int lost = 0;

        CommanderBuildKind[] order =
        {
            CommanderBuildKind.Mine,
            CommanderBuildKind.Factory,
            CommanderBuildKind.NavalDock,
        };

        for (int pass = 0; pass < order.Length; pass++)
        {
            for (int i = 0; i < strategicEconomyBuildings.Count; i++)
            {
                CommanderStrategicEconomyBuildingRecord record = strategicEconomyBuildings[i];
                if (!Enum.TryParse(record.Kind, out CommanderBuildKind kind) || kind != order[pass])
                {
                    continue;
                }

                FactionHQ? hq = StrategicHqByName(record.Faction);
                if (hq == null || !hq.IsServer)
                {
                    continue;
                }

                GlobalPosition position = new(record.X, record.Y, record.Z);
                if (TryRebuildStrategicEconomyBuilding(hq, kind, position, record))
                {
                    rebuilt++;
                    hq.AddFunds(-StrategicEconomyRebuildCharge(kind, record.Level));
                    CommanderAiLog.Note(
                        hq, $"strategic load: {kind} level {record.Level} rebuilt where it stood.");
                    continue;
                }

                // It could not be put back. Its value was deliberately NOT cashed into the war chest
                // — it was supposed to come back in kind — so without this the faction would simply
                // be poorer by the whole price of the building and nothing would say so. Crediting
                // the sunk cost here is the fallback, not the normal path, and it is what keeps the
                // conservation property true in the failure branch as well as the happy one.
                float credit = StrategicFlatSunkCost(kind, record.Level);
                hq.AddFunds(credit);
                StrategicEconomyRefund += credit;
                lost++;
                CommanderAiLog.Note(
                    hq,
                    $"strategic load: {kind} could NOT be rebuilt at {record.X:0}, {record.Z:0} — "
                        + $"{credit:0} credited back instead.");
                CommanderPlugin.Log.LogWarning(
                    $"Strategic load: {hq.faction?.factionName} could not rebuild its {kind} at "
                        + $"{record.X:0}, {record.Z:0}; {credit:0} credited back instead.");
            }
        }

        strategicEconomyBuildings.Clear();
        CommanderPlugin.Log.LogInfo(
            $"Strategic load: {rebuilt} economy buildings rebuilt free, {lost} could not be and were "
                + $"credited back for {StrategicEconomyRefund:0}.");
    }

    /// <summary>
    /// Puts one economy building back through the mod's own spawn for its kind, then restores its
    /// upgrade level. The spawns are the same ones the commander's structure rung uses; only the
    /// charge is made here, because those spawns deliberately do not debit.
    /// </summary>
    private bool TryRebuildStrategicEconomyBuilding(
        FactionHQ hq,
        CommanderBuildKind kind,
        GlobalPosition position,
        CommanderStrategicEconomyBuildingRecord record)
    {
        Unit? built = null;
        switch (kind)
        {
            case CommanderBuildKind.Mine:
                // A mine is tied to a fixed resource site, and SpawnMine enforces that itself: it
                // snaps to the nearest site and REFUSES unless the builder holds that site outright
                // (TrySnapMineSite passes the builder through to SiteMinePermitted). That is exactly
                // the agreement wanted here — a mine is never restored onto ground the game says is
                // somebody else's — and it is why this step must run AFTER the point owners have
                // been restored: with the site still neutral the snap refuses and nothing is built.
                built = SpawnMine(hq, position);
                if (built != null)
                {
                    mineLevels[built] = Mathf.Clamp(record.Level, 1, MaxLevel);
                    WarnIfMineDidNotTakeItsSite(hq, position);
                }
                break;

            case CommanderBuildKind.NavalDock:
                built = SpawnNavalDock(hq, position);
                if (built != null)
                {
                    dockLevels[built] = Mathf.Clamp(record.Level, 1, MaxLevel);
                }
                break;

            case CommanderBuildKind.Factory:
                VehicleDefinition? production = StrategicResolveProduction(hq, record.ProductionJsonKey);
                if (production == null)
                {
                    // Nothing to produce means nothing worth building: the money stays put rather
                    // than buying a factory that would sit idle for the rest of the match.
                    return false;
                }

                built = SpawnFactory(hq, position, production);
                if (built != null)
                {
                    factoryLevels[built] = Mathf.Clamp(record.Level, 1, MaxLevel);
                }
                break;
        }

        return built != null;
    }

    /// <summary>
    /// Says so in the log if a rebuilt mine did not end up owning the site it was put back on.
    /// Ownership of a resource site is not the mod's own state — it follows the mine standing there
    /// — so this is the one place the restore can be checked against what the world actually
    /// believes, and a silent mismatch would leave the commander thinking it owns income it does not
    /// have.
    /// </summary>
    private static void WarnIfMineDidNotTakeItsSite(FactionHQ hq, GlobalPosition position)
    {
        CommanderStrategicPointService? pointService = CommanderStrategicPointService.Instance;
        if (pointService == null)
        {
            return;
        }

        if (!pointService.TryGetPointAt(position, out CommanderStrategicPoint? site) || site == null)
        {
            CommanderPlugin.Log.LogWarning(
                $"Strategic load: {hq.faction?.factionName}'s mine at {position.x:0}, {position.z:0} was "
                    + "rebuilt but no resource site stands there, so it earns nothing.");
            return;
        }

        if (site.Mine == null || site.Mine.disabled)
        {
            CommanderPlugin.Log.LogWarning(
                $"Strategic load: {hq.faction?.factionName}'s mine on {site.Label} was rebuilt but the "
                    + "site does not read as taken; its income will not be paid.");
            return;
        }

        CommanderPlugin.Log.LogInfo(
            $"Strategic load: {site.Label} is {hq.faction?.factionName}'s again — the mine standing on it "
                + "is what owns it.");
    }

    /// <summary>The vehicle a rebuilt factory should produce, resolved out of the faction's own
    /// catalogue by <c>jsonKey</c> — the one identity that survives a mission restart.</summary>
    private static VehicleDefinition? StrategicResolveProduction(FactionHQ hq, string jsonKey)
    {
        if (string.IsNullOrEmpty(jsonKey))
        {
            return null;
        }

        List<VehicleDefinition> catalogue = new();
        CommanderGameAccess.CollectFactionVehicleDefinitions(catalogue, hq);
        for (int i = 0; i < catalogue.Count; i++)
        {
            if (string.Equals(catalogue[i].jsonKey, jsonKey, StringComparison.Ordinal))
            {
                return catalogue[i];
            }
        }

        return null;
    }

    internal static FactionHQ? StrategicHqByName(string factionName)
    {
        if (string.IsNullOrEmpty(factionName))
        {
            return null;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq?.faction != null
                && string.Equals(hq.faction.factionName, factionName, StringComparison.Ordinal))
            {
                return hq;
            }
        }

        return null;
    }

    /// <summary>
    /// The decision table for the war chest, checked at plugin load beside the rest of the
    /// economy's checks. The valuation over a LIVE unit cannot run outside the game, so what is
    /// pinned here is every pure rule underneath it: which buildings count, what the three
    /// flat-priced ones really cost, and the conservation property the whole track is judged on.
    /// </summary>
    private static void CheckStrategicValuation(List<string> failures)
    {
        Expect(failures, "a building the commander built is cashed in at what it charged for it",
            StrategicBuildingValue(commanderBuilt: true, rebuiltInKind: false, catalogueCost: 640f), 640f);
        Expect(failures, "a mission-authored building is not cashed in",
            StrategicBuildingValue(commanderBuilt: false, rebuiltInKind: false, catalogueCost: 640f), 0f);
        Expect(failures, "a building that comes back in kind is not also cashed in",
            StrategicBuildingValue(commanderBuilt: true, rebuiltInKind: true, catalogueCost: 640f), 0f);
        Expect(failures, "a mission-authored building that comes back in kind is not cashed in either",
            StrategicBuildingValue(commanderBuilt: false, rebuiltInKind: true, catalogueCost: 640f), 0f);
        Expect(failures, "a building priced into the red is worth nothing rather than a debt",
            StrategicBuildingValue(commanderBuilt: true, rebuiltInKind: false, catalogueCost: -50f), 0f);

        // WHICH buildings come back in kind. Each of these four is a double-count waiting to happen:
        // cash one in as well as rebuilding it and the faction is richer by its price on every save.
        Expect(failures, "a forward base's buildings come back in kind",
            StrategicRebuiltInKind(CommanderBuildKind.Structure, forwardBaseBuilding: true), true);
        Expect(failures, "a mine comes back in kind, because a site earns nothing without one",
            StrategicRebuiltInKind(CommanderBuildKind.Mine, forwardBaseBuilding: false), true);
        Expect(failures, "a factory comes back in kind",
            StrategicRebuiltInKind(CommanderBuildKind.Factory, forwardBaseBuilding: false), true);
        Expect(failures, "a naval dock comes back in kind",
            StrategicRebuiltInKind(CommanderBuildKind.NavalDock, forwardBaseBuilding: false), true);
        Expect(failures, "an ordinary catalogue structure does not come back in kind, so it is cashed in",
            StrategicRebuiltInKind(CommanderBuildKind.Structure, forwardBaseBuilding: false), false);

        // And the whole chain, which is the leak stated end to end: a mine is worth nothing to the
        // war chest precisely because it is coming back.
        Expect(failures, "a mine is never both cashed in and rebuilt",
            StrategicBuildingValue(
                commanderBuilt: true,
                rebuiltInKind: StrategicRebuiltInKind(CommanderBuildKind.Mine, forwardBaseBuilding: false),
                catalogueCost: 640f),
            0f);

        // Rebuilding costs the faction nothing: these are the strategic picture, put back before
        // either commander starts spending.
        Expect(failures, "rebuilding a mine costs nothing",
            StrategicEconomyRebuildCharge(CommanderBuildKind.Mine, 3), 0f);
        Expect(failures, "rebuilding a fully upgraded dock costs nothing either",
            StrategicEconomyRebuildCharge(CommanderBuildKind.NavalDock, MaxLevel), 0f);

        // The three flat-priced ladders. A building that exists was bought, so level one is the
        // purchase price and nothing else; each level above adds the upgrade paid to reach it.
        float minePurchase = GetBuildCost(CommanderBuildKind.Mine);
        Expect(failures, "a new mine is worth its purchase price and no more",
            StrategicFlatSunkCost(CommanderBuildKind.Mine, 1), minePurchase);
        Expect(failures, "a mine at level two is worth its purchase plus the first upgrade",
            StrategicFlatSunkCost(CommanderBuildKind.Mine, 2),
            minePurchase + GetMineUpgradeCost(1));
        Expect(failures, "a mine at level three is worth its purchase plus both upgrades",
            StrategicFlatSunkCost(CommanderBuildKind.Mine, 3),
            minePurchase + GetMineUpgradeCost(1) + GetMineUpgradeCost(2));
        Expect(failures, "a fully upgraded mine is worth more than a new one",
            StrategicFlatSunkCost(CommanderBuildKind.Mine, MaxLevel)
                > StrategicFlatSunkCost(CommanderBuildKind.Mine, 1), true);
        Expect(failures, "a factory at level three is worth its purchase plus both upgrades",
            StrategicFlatSunkCost(CommanderBuildKind.Factory, 3),
            GetBuildCost(CommanderBuildKind.Factory) + GetFactoryUpgradeCost(1) + GetFactoryUpgradeCost(2));
        Expect(failures, "a dock at level three is worth its purchase plus both upgrades",
            StrategicFlatSunkCost(CommanderBuildKind.NavalDock, 3),
            GetBuildCost(CommanderBuildKind.NavalDock) + GetNavalDockUpgradeCost(1) + GetNavalDockUpgradeCost(2));
        Expect(failures, "a level beyond the ceiling is not paid for twice",
            StrategicFlatSunkCost(CommanderBuildKind.Mine, MaxLevel + 5),
            StrategicFlatSunkCost(CommanderBuildKind.Mine, MaxLevel));
        Expect(failures, "a level below one is still a building that was bought",
            StrategicFlatSunkCost(CommanderBuildKind.Mine, 0), minePurchase);
        Expect(failures, "an ordinary catalogue structure has no flat price",
            StrategicFlatSunkCost(CommanderBuildKind.Structure, 3), 0f);

        // The property the track is judged on, in every branch it can take.
        Expect(failures, "a save and reload that spends nothing leaves the faction exactly as rich",
            StrategicValueConserved(
                fundsBefore: 1000f, cashedIn: 4000f, garrisonSpend: 0f, economyRefund: 0f, fundsAfter: 5000f),
            true);
        Expect(failures, "a save and reload is poorer by exactly what it spent on garrisons",
            StrategicValueConserved(
                fundsBefore: 1000f, cashedIn: 4000f, garrisonSpend: 1500f, economyRefund: 0f, fundsAfter: 3500f),
            true);
        Expect(failures, "an economy building that could not be rebuilt is credited back, not lost",
            StrategicValueConserved(
                fundsBefore: 1000f, cashedIn: 4000f, garrisonSpend: 1500f, economyRefund: 500f, fundsAfter: 4000f),
            true);
        Expect(failures, "a garrison handed over free would leave the faction richer, and is caught",
            StrategicValueConserved(
                fundsBefore: 1000f, cashedIn: 4000f, garrisonSpend: 0f, economyRefund: 0f, fundsAfter: 6500f),
            false);
        Expect(failures, "a mine both cashed in AND rebuilt would leave the faction richer, and is caught",
            StrategicValueConserved(
                fundsBefore: 1000f, cashedIn: 4000f + 1500f, garrisonSpend: 0f, economyRefund: 0f, fundsAfter: 5000f),
            false);
        Expect(failures, "an economy building charged for on rebuild would leave the faction poorer, and is caught",
            StrategicValueConserved(
                fundsBefore: 1000f, cashedIn: 4000f, garrisonSpend: 1500f, economyRefund: 0f, fundsAfter: 3000f),
            false);
        Expect(failures, "value that was cashed in and never handed back would be caught",
            StrategicValueConserved(
                fundsBefore: 1000f, cashedIn: 0f, garrisonSpend: 0f, economyRefund: 0f, fundsAfter: 5000f),
            false);

        CheckStrategicFobNaming(failures);
    }

    /// <summary>
    /// The forward-base name counter seed. The counter restarts at zero on a mission change while
    /// the restored bases still carry their old numbers, so without seeding it past the highest one
    /// standing the first rebuilt base claims a name already in use.
    /// </summary>
    private static void CheckStrategicFobNaming(List<string> failures)
    {
        Expect(failures, "a forward base's trailing number is read off its name",
            CommanderFobBuilderTrailingNumber("FOB VILLAGE 4 7"), 7);
        Expect(failures, "a forward base numbered in double figures is read whole",
            CommanderFobBuilderTrailingNumber("FOB HILLTOP 2 13"), 13);
        Expect(failures, "a name with no trailing number seeds nothing",
            CommanderFobBuilderTrailingNumber("FOB CROSSROADS"), 0);
        Expect(failures, "an empty name seeds nothing", CommanderFobBuilderTrailingNumber(string.Empty), 0);
        Expect(failures, "a null name seeds nothing", CommanderFobBuilderTrailingNumber(null), 0);
        Expect(failures, "a number buried mid-name is not mistaken for the counter",
            CommanderFobBuilderTrailingNumber("FOB 12 OUTPOST"), 0);
    }

    /// <summary>Alias so the naming check reads the same rule the rebuild calls, without the check
    /// block needing to know which partial file it lives in.</summary>
    private static int CommanderFobBuilderTrailingNumber(string? uniqueName)
    {
        return TrailingNumber(uniqueName);
    }

    private static void Expect(List<string> failures, string name, int actual, int expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, float actual, float expected)
    {
        if (!Mathf.Approximately(actual, expected))
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }
}
