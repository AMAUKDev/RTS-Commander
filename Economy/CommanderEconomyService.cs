using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

/// <summary>What the next placement click builds.</summary>
internal enum CommanderBuildKind
{
    None,
    Mine,
    Factory,

    /// <summary>Any other building out of the encyclopedia catalogue.</summary>
    Structure,

    /// <summary>The shoreline dock that unlocks naval purchases, three levels of them.</summary>
    NavalDock,
}

/// <summary>
/// The economy layer: gold mines that pay their owner a standing income, factories that feed the
/// unit reserve, and the upgrades that multiply what each of them produces.
/// <para>
/// Both sides play by these rules. The player spends through the BUILD window; hostile factions
/// spend through <see cref="ReviewEnemy"/>, gated on the same ENEMY COMMANDER setting as
/// <see cref="CommanderEnemyCommanderService"/>. The two AI spenders deliberately share one pot
/// and take separate slices of it — one buys units, this one buys the ability to buy more units.
/// </para>
/// </summary>
/// <remarks>
/// ponytail: levels are keyed on the live <see cref="Unit"/>, so they die with the building and do
/// not survive a mission reload. That is the intended stake — bomb the mine, take the income away.
/// Persisting them would mean writing our own state into the mission save.
/// </remarks>
internal sealed partial class CommanderEconomyService
    : ICommanderTickActive, ICommanderTickPersistent, ICommanderDeactivate, ICommanderResetSession
{
    internal const int MaxLevel = 3;

    /// <summary>What a built mine calls itself, whatever industrial prefab it is wearing.</summary>
    internal const string MineDisplayName = "Gold Mine";

    private const float IncomeIntervalSeconds = 15f;
    private const float EnemyReviewIntervalSeconds = 30f;
    private const float FactoryRefreshIntervalSeconds = 5f;

    /// <summary>Mines an enemy commander builds before it starts upgrading what it has.</summary>
    private const int EnemyMineTarget = 2;

    /// <summary>Factories an enemy commander builds before it starts upgrading what it has.</summary>
    private const int EnemyFactoryTarget = 1;

    /// <summary>
    /// The duel's targets. A commander capped at two mines and one factory falls behind a player
    /// who keeps building, so on the mod's own map it is allowed to keep pace.
    /// </summary>
    private const int DuelEnemyMineTarget = 4;
    private const int DuelEnemyFactoryTarget = 2;

    /// <summary>
    /// Reserve multiple a commander keeps clear of an economy purchase. Faction balances are
    /// mission-authored and scale-free, so this is a ratio rather than an absolute floor.
    /// </summary>
    private const float EnemyReserveMultiple = 4f;

    /// <summary>Buildings the mine prefers to look like, best first.</summary>
    private static readonly string[] MinePrefabKeys =
    {
        "refinery_main",
        "enrichmentPlant1",
        "storageTank",
        "factory_large",
    };

    /// <summary>Buildings a built factory prefers to look like, best first.</summary>
    private static readonly string[] FactoryPrefabKeys =
    {
        "factory_large",
        "factory_tall",
    };

    /// <summary>What a built naval dock calls itself, whatever prefab it is wearing.</summary>
    internal const string NavalDockDisplayName = "Naval Dock";

    /// <summary>
    /// Buildings a naval dock prefers to look like, best first. The game ships no dock, so the
    /// harbour crane stands in for one — it is the only structure that reads as a quayside.
    /// </summary>
    private static readonly string[] NavalDockPrefabKeys =
    {
        "HarborCrane",
        "TowerCrane",
        "Platform_large",
        "VehicleDepot1",
    };

    private readonly Dictionary<Unit, int> mineLevels = new();
    private readonly Dictionary<Unit, int> factoryLevels = new();
    private readonly Dictionary<Unit, int> dockLevels = new();
    private readonly List<Factory> friendlyFactories = new();
    private readonly List<Unit> friendlyMines = new();
    private readonly List<Unit> friendlyDocks = new();
    private readonly List<Unit> staleUnits = new();
    private readonly List<VehicleDefinition> productionOptions = new();
    private readonly List<VehicleDefinition> enemyProductionOptions = new();
    private readonly HashSet<Unit> builtUnits = new();
    private readonly CommanderBuildPreview preview = new();

    private BuildingDefinition? mineDefinition;
    private bool mineDefinitionResolved;
    private BuildingDefinition? factoryDefinition;
    private bool factoryDefinitionResolved;
    private BuildingDefinition? dockDefinition;
    private bool dockDefinitionResolved;
    private CommanderBuildKind pendingBuild;
    private BuildingDefinition? pendingStructure;
    private int productionIndex;
    private int builtNameCounter;
    private float nextIncomeAt;
    private float nextEnemyReviewAt;
    private float nextFactoryRefreshAt;

    internal static CommanderEconomyService? Instance { get; private set; }

    internal CommanderEconomyService()
    {
        Instance = this;
        nextIncomeAt = CommanderScheduler.Stagger("economy.income", IncomeIntervalSeconds);
        nextEnemyReviewAt = 0f;
    }

    internal string StatusText { get; private set; } = string.Empty;

    internal CommanderBuildKind PendingBuild => pendingBuild;

    internal bool AwaitingPlacement => pendingBuild != CommanderBuildKind.None;

    /// <summary>
    /// What the status line says while a placement is armed: the reason the site under the cursor
    /// is blocked, or the go-ahead. Falls back to <see cref="StatusText"/> when nothing is armed.
    /// </summary>
    internal string PlacementStatus
    {
        get
        {
            if (!AwaitingPlacement)
            {
                return StatusText;
            }

            return preview.SiteValid
                ? $"Click to site the {PendingBuildLabel()}."
                : preview.BlockedReason;
        }
    }

    /// <summary>The building the armed placement would put down, whichever button armed it.</summary>
    private BuildingDefinition? PendingDefinition => pendingBuild switch
    {
        CommanderBuildKind.None => null,
        CommanderBuildKind.Structure => pendingStructure,
        _ => ResolveDefinition(pendingBuild),
    };

    /// <summary>
    /// Anything either commander built. The marker layer reads this so a mine you put down can be
    /// clicked and boxed like a vehicle — without it, the Basegame rule that buildings are not
    /// selectable made your own economy invisible to selection.
    /// </summary>
    internal static bool IsCommanderBuilt(Unit? unit)
    {
        return unit != null && Instance != null && Instance.builtUnits.Contains(unit);
    }

    internal bool IsBuiltMine(Unit? unit)
    {
        return unit != null && mineLevels.ContainsKey(unit);
    }

    internal bool IsBuiltNavalDock(Unit? unit)
    {
        return unit != null && dockLevels.ContainsKey(unit);
    }

    /// <summary>Naval docks the local faction owns, for the BUILD window list.</summary>
    internal IReadOnlyList<Unit> NavalDocks => friendlyDocks;

    /// <summary>
    /// The best dock a faction holds, 0 for none. This is the whole naval gate: level 0 buys no
    /// ships at all, and each level up opens the next class. Both commanders read it.
    /// </summary>
    internal static int GetNavalDockLevel(FactionHQ? hq)
    {
        CommanderEconomyService? service = Instance;
        if (service == null || hq == null)
        {
            return 0;
        }

        int best = 0;
        foreach (KeyValuePair<Unit, int> entry in service.dockLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq && entry.Value > best)
            {
                best = entry.Value;
            }
        }

        return best;
    }

    internal int GetNavalDockLevel(Unit dock)
    {
        return dockLevels.TryGetValue(dock, out int level) ? level : 0;
    }

    /// <summary>
    /// Where a faction's best dock stands. Ships enter the map at a sea lane, and the lane picked
    /// used to be scored off the nearest airbase, which on a map whose coast runs the other way put
    /// a purchased hull an entire map away from the dock that paid for it. The dock is the harbour,
    /// so the dock is the anchor.
    /// </summary>
    internal static bool TryGetNavalDockPosition(FactionHQ? hq, out GlobalPosition position)
    {
        position = default;
        CommanderEconomyService? service = Instance;
        if (service == null || hq == null)
        {
            return false;
        }

        int best = 0;
        foreach (KeyValuePair<Unit, int> entry in service.dockLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq && entry.Value > best)
            {
                best = entry.Value;
                position = entry.Key.GlobalPosition();
            }
        }

        return best > 0;
    }

    /// <summary>True for the prefab the dock button places, which is what gives it the shore rule.</summary>
    internal static bool IsNavalDockDefinition(BuildingDefinition? definition)
    {
        return definition != null
            && Instance != null
            && ReferenceEquals(definition, Instance.ResolveDefinition(CommanderBuildKind.NavalDock));
    }

    internal static float NavalDockBuildCost => Mathf.Max(0f, CommanderSettings.NavalDockCost);

    /// <summary>Cost of taking a dock from <paramref name="level"/> to the next one.</summary>
    internal static float GetNavalDockUpgradeCost(int level)
    {
        return Mathf.Max(0f, CommanderSettings.NavalDockUpgradeCost) * level;
    }

    /// <summary>Friendly factories that actually produce a vehicle, refreshed while RTS mode is up.</summary>
    internal IReadOnlyList<Factory> Factories => friendlyFactories;

    internal IReadOnlyList<Unit> Mines => friendlyMines;

    internal static float MineBuildCost => Mathf.Max(0f, CommanderSettings.GoldMineCost);

    internal static float FactoryBuildCost => Mathf.Max(0f, CommanderSettings.FactoryBuildCost);

    /// <summary>Seconds between production cycles at a factory this mod built.</summary>
    internal static float FactoryProductionSeconds => Mathf.Max(30f, CommanderSettings.FactoryProductionSeconds);

    internal static float GetBuildCost(CommanderBuildKind kind)
    {
        return kind switch
        {
            CommanderBuildKind.Factory => FactoryBuildCost,
            CommanderBuildKind.NavalDock => NavalDockBuildCost,
            _ => MineBuildCost,
        };
    }

    /// <summary>What the armed placement will charge, catalogue prices included.</summary>
    internal float PendingBuildCost => pendingBuild == CommanderBuildKind.Structure
        ? GetStructureCost(pendingStructure)
        : GetBuildCost(pendingBuild);

    /// <summary>Cost of taking a mine from <paramref name="level"/> to the next one.</summary>
    internal static float GetMineUpgradeCost(int level)
    {
        return MineBuildCost * (level + 1);
    }

    internal static float GetMineIncomePerMinute(int level)
    {
        return Mathf.Max(0f, CommanderSettings.GoldMineIncomePerMinute) * level;
    }

    /// <summary>Cost of taking a factory from <paramref name="level"/> to the next one.</summary>
    internal static float GetFactoryUpgradeCost(int level)
    {
        return Mathf.Max(0f, CommanderSettings.FactoryUpgradeCost) * level;
    }

    /// <summary>Units a factory adds to the reserve per production cycle. Stock factories are 1.</summary>
    internal static int GetFactoryOutput(Unit? attachedUnit)
    {
        CommanderEconomyService? service = Instance;
        if (service == null || attachedUnit == null)
        {
            return 1;
        }

        return service.factoryLevels.TryGetValue(attachedUnit, out int level) ? level : 1;
    }

    /// <summary>
    /// One runnable check for the price ladder, run at plugin load next to the other self-checks
    /// because a Unity plugin has nowhere else to run a test. The two things that actually break
    /// the economy if a default is retuned badly: an upgrade that gets cheaper as it gets stronger,
    /// and a factory nobody upgraded reporting anything other than its stock output of one.
    /// </summary>
    internal static void SelfCheck()
    {
        for (int level = 1; level < MaxLevel; level++)
        {
            if (GetMineUpgradeCost(level) <= GetMineUpgradeCost(level - 1)
                || GetMineIncomePerMinute(level + 1) <= GetMineIncomePerMinute(level)
                || GetFactoryUpgradeCost(level + 1) <= GetFactoryUpgradeCost(level))
            {
                CommanderPlugin.Log.LogError(
                    $"Economy self-check FAILED: the level {level} price ladder is not increasing. "
                        + "Check the Economy section of the config.");
                return;
            }
        }

        if (GetFactoryOutput(null) != 1)
        {
            CommanderPlugin.Log.LogError("Economy self-check FAILED: an unupgraded factory is not stock.");
        }

        // The build buttons charge whatever this returns, so a swapped case would bill a factory
        // at mine prices.
        if (GetBuildCost(CommanderBuildKind.Mine) != MineBuildCost
            || GetBuildCost(CommanderBuildKind.Factory) != FactoryBuildCost
            || GetBuildCost(CommanderBuildKind.NavalDock) != NavalDockBuildCost
            || GetBuildCost(CommanderBuildKind.None) != MineBuildCost)
        {
            CommanderPlugin.Log.LogError("Economy self-check FAILED: build costs are crossed.");
        }

        // The dock ladder is the naval gate: every ship class has to be reachable at some level,
        // and level 0 has to buy nothing, or the dock is either pointless or a hard wall.
        if (CommanderNavalPurchaseService.CountShipTypesAtLevel(0) != 0
            || CommanderNavalPurchaseService.CountShipTypesAtLevel(MaxLevel)
                != System.Enum.GetValues(typeof(ShipType)).Length)
        {
            CommanderPlugin.Log.LogError(
                "Economy self-check FAILED: the naval dock ladder does not run from nothing to every ship.");
        }

        // The catalogue files every building under its own BuildingType, so a game patch that adds
        // one would silently file a whole category under OTHER.
        foreach (BuildingType type in System.Enum.GetValues(typeof(BuildingType)))
        {
            if (GetCategoryLabel(type) == "OTHER")
            {
                CommanderPlugin.Log.LogError(
                    $"Economy self-check FAILED: building category {type} has no label.");
                return;
            }
        }
    }

    internal int GetMineLevel(Unit mine)
    {
        return mineLevels.TryGetValue(mine, out int level) ? level : 0;
    }

    internal int GetFactoryLevel(Factory factory)
    {
        return factory.attachedUnit != null && factoryLevels.TryGetValue(factory.attachedUnit, out int level)
            ? level
            : 1;
    }

    /// <summary>Standing income the local faction earns from its mines, for the readout.</summary>
    internal float FriendlyIncomePerMinute
    {
        get
        {
            float income = 0f;
            for (int i = 0; i < friendlyMines.Count; i++)
            {
                income += GetMineIncomePerMinute(GetMineLevel(friendlyMines[i]));
            }
            return income;
        }
    }

    public void TickActive()
    {
        // Same escape hatch the supply and trailer placements have, so every armed placement in the
        // mod backs out the same way.
        if (AwaitingPlacement && CommanderGameInput.CancelDown)
        {
            CancelBuild();
        }

        // No ghost while the cursor is parked on a Commander window: the click cannot place there,
        // so a preview skidding around behind the BUILD list only reads as a bug.
        BuildingDefinition? pending = PendingDefinition;
        if (pending != null)
        {
            Vector2 cursor = Input.mousePosition;
            if (CommanderOverlayUi.Instance?.ContainsScreenPoint(cursor) == true)
            {
                preview.Suspend();
            }
            else
            {
                preview.Tick(pending, CommanderGameAccess.GetLocalHq(), cursor);
            }
        }

        if (CommanderScheduler.IsDue(ref nextFactoryRefreshAt, FactoryRefreshIntervalSeconds))
        {
            RefreshFriendlyLists();
        }
    }

    public void TickPersistent()
    {
        if (CommanderScheduler.IsDue(ref nextIncomeAt, IncomeIntervalSeconds))
        {
            PayIncome();
        }

        // Same ordering rule as the enemy commander: do not burn a review while no mission is
        // loaded, or the enemy's first mine lands a review after the match already started.
        if (CommanderEnemyCommanderService.EffectiveMode != CommanderEnemyCommanderService.ModeOff
            && CommanderGameAccess.GetLocalHq() != null
            && CommanderScheduler.IsDue(ref nextEnemyReviewAt, EnemyReviewIntervalSeconds))
        {
            ReviewEnemies();
        }
    }

    public void Deactivate()
    {
        pendingBuild = CommanderBuildKind.None;
        pendingStructure = null;
        preview.Hide();
    }

    public void ResetSession()
    {
        mineLevels.Clear();
        factoryLevels.Clear();
        dockLevels.Clear();
        friendlyFactories.Clear();
        friendlyMines.Clear();
        friendlyDocks.Clear();
        staleUnits.Clear();
        productionOptions.Clear();
        builtUnits.Clear();
        preview.Clear();
        catalog.Clear();
        catalogResolved = false;
        pendingBuild = CommanderBuildKind.None;
        pendingStructure = null;
        productionIndex = 0;
        builtNameCounter = 0;
        mineDefinition = null;
        mineDefinitionResolved = false;
        factoryDefinition = null;
        factoryDefinitionResolved = false;
        dockDefinition = null;
        dockDefinitionResolved = false;
        shoreSearchReported.Clear();
        StatusText = string.Empty;
        nextIncomeAt = CommanderScheduler.Stagger("economy.income", IncomeIntervalSeconds);
        nextEnemyReviewAt = 0f;
    }

    internal void BeginBuild(CommanderBuildKind kind)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        string label = BuildLabel(kind);
        if (hq == null)
        {
            StatusText = "No faction HQ.";
            return;
        }

        // Buildings are spawned through the server object manager, so a pure client cannot build.
        if (!hq.IsServer)
        {
            StatusText = "Only the host can construct buildings.";
            return;
        }

        if (ResolveDefinition(kind) == null)
        {
            StatusText = $"No building prefab available for a {label}.";
            return;
        }

        if (kind == CommanderBuildKind.Factory && SelectedProduction == null)
        {
            StatusText = "This faction has no ground unit a factory could produce.";
            return;
        }

        if (hq.factionFunds < GetBuildCost(kind))
        {
            StatusText = $"Insufficient faction funds for a {label}.";
            return;
        }

        pendingBuild = kind;
        pendingStructure = null;
        StatusText = $"Select the {label} site in the 3D world.";
    }

    internal void CancelBuild()
    {
        if (pendingBuild == CommanderBuildKind.None)
        {
            return;
        }

        StatusText = $"{PendingBuildLabel()} placement cancelled.";
        pendingBuild = CommanderBuildKind.None;
        pendingStructure = null;
        preview.Hide();
    }

    /// <summary>Placement click from <see cref="CommanderInputController"/>. True once handled.</summary>
    internal bool TryPlaceBuildingFromWorld(Vector2 screenPosition)
    {
        CommanderBuildKind kind = pendingBuild;
        if (kind == CommanderBuildKind.None)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition position))
        {
            StatusText = "No valid ground under the cursor.";
            return true;
        }

        string label = PendingBuildLabel();
        BuildingDefinition? structure = pendingStructure;
        BuildingDefinition? placed = PendingDefinition;
        float cost = PendingBuildCost;

        // The ghost has already said whether this site is legal; the click obeys it rather than
        // building a refinery through the highway because the player was quick on the mouse.
        if (placed != null
            && !preview.IsSiteAllowed(placed, position, CommanderGameAccess.GetLocalHq(), out string blocked))
        {
            StatusText = blocked;
            return true;
        }

        // The same repeat key the supply window uses keeps the placement armed, so a row of
        // bunkers is one trip to the BUILD window instead of five.
        bool repeat = CommanderSettings.RepeatDeployment.IsPressed();
        if (!repeat)
        {
            pendingBuild = CommanderBuildKind.None;
            pendingStructure = null;
            preview.Hide();
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null || !hq.IsServer || hq.factionFunds < cost)
        {
            pendingBuild = CommanderBuildKind.None;
            pendingStructure = null;
            preview.Hide();
            StatusText = $"{label} could not be built.";
            return true;
        }

        if (kind == CommanderBuildKind.Structure)
        {
            if (structure == null || !PlaceStructure(hq, position, structure, cost))
            {
                pendingBuild = CommanderBuildKind.None;
                pendingStructure = null;
                preview.Hide();
            }

            return true;
        }

        if (kind == CommanderBuildKind.NavalDock)
        {
            if (SpawnNavalDock(hq, position) == null)
            {
                pendingBuild = CommanderBuildKind.None;
                preview.Hide();
                StatusText = "Naval dock could not be built. It has to stand on the shore, within "
                    + $"{CommanderSettings.NavalDockRadiusKm:0.#} km of a base you hold.";
                return true;
            }

            hq.AddFunds(-cost);
            RefreshFriendlyLists();
            StatusText = $"Naval dock built. Level 1 unlocks {CommanderNavalPurchaseService.GetLevelUnlockLabel(1)}.";
            return true;
        }

        if (kind == CommanderBuildKind.Mine)
        {
            if (SpawnMine(hq, position) == null)
            {
                pendingBuild = CommanderBuildKind.None;
                preview.Hide();
                StatusText = "Gold mine could not be built.";
                return true;
            }

            hq.AddFunds(-cost);
            RefreshFriendlyLists();
            StatusText = $"Gold mine built. +{FundsLabel(GetMineIncomePerMinute(1))}/min.";
            return true;
        }

        VehicleDefinition? production = SelectedProduction;
        if (production == null || SpawnFactory(hq, position, production) == null)
        {
            pendingBuild = CommanderBuildKind.None;
            preview.Hide();
            StatusText = "Factory could not be built.";
            return true;
        }

        hq.AddFunds(-cost);
        RefreshFriendlyLists();
        StatusText = $"{CommanderGameAccess.GetVehicleLabel(production)} factory built. "
            + $"One unit every {FactoryProductionSeconds:0}s.";
        return true;
    }

    /// <summary>What a factory built now would produce; null when the faction offers nothing.</summary>
    internal VehicleDefinition? SelectedProduction
    {
        get
        {
            return productionOptions.Count == 0
                ? null
                : productionOptions[Mathf.Clamp(productionIndex, 0, productionOptions.Count - 1)];
        }
    }

    internal void CycleProduction(int delta)
    {
        if (productionOptions.Count == 0)
        {
            return;
        }

        productionIndex = (productionIndex + delta + productionOptions.Count) % productionOptions.Count;
    }

    private static string BuildLabel(CommanderBuildKind kind)
    {
        return kind switch
        {
            CommanderBuildKind.Factory => "factory",
            CommanderBuildKind.NavalDock => "naval dock",
            _ => "gold mine",
        };
    }

    private string PendingBuildLabel()
    {
        return pendingBuild == CommanderBuildKind.Structure && pendingStructure != null
            ? GetStructureLabel(pendingStructure)
            : BuildLabel(pendingBuild);
    }


    internal void UpgradeMine(Unit mine)
    {
        int level = GetMineLevel(mine);
        if (level <= 0 || level >= MaxLevel)
        {
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float cost = GetMineUpgradeCost(level);
        if (hq == null || hq.factionFunds < cost)
        {
            StatusText = "Insufficient faction funds for that upgrade.";
            return;
        }

        hq.AddFunds(-cost);
        mineLevels[mine] = level + 1;
        StatusText = $"Gold mine at level {level + 1}. +{FundsLabel(GetMineIncomePerMinute(level + 1))}/min.";
    }

    internal void UpgradeFactory(Factory factory)
    {
        Unit? attached = factory.attachedUnit;
        int level = GetFactoryLevel(factory);
        if (attached == null || level >= MaxLevel)
        {
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float cost = GetFactoryUpgradeCost(level);
        if (hq == null || hq.factionFunds < cost)
        {
            StatusText = "Insufficient faction funds for that upgrade.";
            return;
        }

        hq.AddFunds(-cost);
        factoryLevels[attached] = level + 1;
        StatusText = $"{CommanderGameAccess.GetUnitLabel(attached)} at level {level + 1}: "
            + $"{level + 1} units per cycle.";
    }

    internal static string FundsLabel(float funds)
    {
        return UnitConverter.ValueReading(funds) ?? funds.ToString("F1");
    }

    /// <summary>
    /// Pays every faction's mines. <c>factionFunds</c> is a server SyncVar, so this is a no-op on a
    /// pure multiplayer client — the host runs the economy for everyone.
    /// </summary>
    private void PayIncome()
    {
        HoldFundsInTreasury();
        if (mineLevels.Count == 0)
        {
            return;
        }

        float share = IncomeIntervalSeconds / 60f;
        staleUnits.Clear();
        foreach (KeyValuePair<Unit, int> entry in mineLevels)
        {
            Unit mine = entry.Key;
            if (mine == null || mine.disabled)
            {
                staleUnits.Add(mine!);
                continue;
            }

            FactionHQ hq = mine.NetworkHQ;
            if (hq != null && hq.IsServer)
            {
                hq.AddFunds(GetMineIncomePerMinute(entry.Value) * share);
            }
        }

        for (int i = 0; i < staleUnits.Count; i++)
        {
            mineLevels.Remove(staleUnits[i]);
        }
        staleUnits.Clear();
    }

    /// <summary>
    /// Stops the Basegame paying the mod's economy out to individual pilots' wallets.
    /// </summary>
    /// <remarks>
    /// <c>FactionHQ.DistributeFunds</c> runs every 30 s and hands each player
    /// <c>(factionFunds - excessFundsThreshold) * excessFundsDistributePercent</c> — a quarter of
    /// everything the faction holds above the balance the mission was authored with — on top of the
    /// mission's own regular income. In a normal match that is the point: the faction bankrolls its
    /// pilots. Under a commander it is a leak straight out of the treasury the player is trying to
    /// build up, which is exactly what "the gold mine says +20/min and the faction balance does not
    /// move" was: the mines paid in, and the very next distribution took a quarter of it back out
    /// to the personal account.
    /// <para>
    /// The threshold is a plain float, so raising it to whatever the faction now holds makes the
    /// treasury never look excessive. The mission's authored <c>regularIncome</c> allowance is left
    /// alone, so a player who also flies still gets their own money the normal way.
    /// </para>
    /// </remarks>
    private static void HoldFundsInTreasury()
    {
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq != null && hq.IsServer && hq.factionFunds > hq.excessFundsThreshold)
            {
                hq.excessFundsThreshold = hq.factionFunds;
            }
        }
    }

    private Unit? SpawnMine(FactionHQ hq, GlobalPosition position, bool randomRotation = false)
    {
        Unit? mine = SpawnBuilding(
            hq, position, ResolveDefinition(CommanderBuildKind.Mine), MineDisplayName, randomRotation);
        if (mine == null)
        {
            return null;
        }

        mineLevels[mine] = 1;
        return mine;
    }

    /// <summary>
    /// The dock that gates naval purchases. It carries no game behaviour of its own — the prefab is
    /// scenery — so everything it does lives in <see cref="GetNavalDockLevel(FactionHQ)"/>, which
    /// both the player's naval window and the enemy commander read before they buy a ship.
    /// </summary>
    private Unit? SpawnNavalDock(FactionHQ hq, GlobalPosition position, bool randomRotation = false)
    {
        Unit? dock = SpawnBuilding(
            hq, position, ResolveDefinition(CommanderBuildKind.NavalDock), NavalDockDisplayName, randomRotation);
        if (dock == null)
        {
            return null;
        }

        dockLevels[dock] = 1;
        return dock;
    }

    internal void UpgradeNavalDock(Unit dock)
    {
        int level = GetNavalDockLevel(dock);
        if (level <= 0 || level >= MaxLevel)
        {
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        float cost = GetNavalDockUpgradeCost(level);
        if (hq == null || hq.factionFunds < cost)
        {
            StatusText = "Insufficient faction funds for that upgrade.";
            return;
        }

        hq.AddFunds(-cost);
        dockLevels[dock] = level + 1;
        StatusText = $"Naval dock at level {level + 1}: "
            + $"{CommanderNavalPurchaseService.GetLevelUnlockLabel(level + 1)}.";
    }

    /// <summary>
    /// Builds a working factory: the prefab carries the <see cref="Factory"/> component, and
    /// <c>SetFactory</c> is what registers the production cycle — the same call the game makes for
    /// a mission-authored factory. Its cadence cannot be changed afterwards, so the interval is
    /// chosen here and the upgrades move batch size instead.
    /// </summary>
    private Unit? SpawnFactory(
        FactionHQ hq,
        GlobalPosition position,
        VehicleDefinition production,
        bool randomRotation = false)
    {
        Unit? unit = SpawnBuilding(
            hq,
            position,
            ResolveDefinition(CommanderBuildKind.Factory),
            $"{production.code} Factory",
            randomRotation);
        if (unit == null)
        {
            return null;
        }

        if (!unit.TryGetComponent(out Factory factory))
        {
            CommanderPlugin.Log.LogWarning(
                "The factory building prefab has no Factory component, so it will never produce.");
            return null;
        }

        factory.SetFactory(production.jsonKey, FactoryProductionSeconds);
        return unit;
    }

    /// <summary>
    /// Spawns the building. The player's placements are unrotated so the ghost preview is exactly
    /// what lands; the enemy commander still scatters its rotations, because nobody is watching a
    /// ghost for those.
    /// </summary>
    /// <summary>
    /// Spawns the building under <paramref name="displayName"/>. The player's placements are
    /// unrotated so the ghost preview is exactly what lands; the enemy commander still scatters
    /// its rotations, because nobody is watching a ghost for those.
    /// </summary>
    /// <remarks>
    /// The name is written twice on purpose. <c>UniqueName</c> is the id the game registers the
    /// unit under and the first thing <c>GetUnitLabel</c> reads, so it gets a numbered copy;
    /// <c>unitName</c> is the SyncVar the Basegame map-icon hover reads
    /// (<c>UnitMapIcon.GetInfoText</c>), so it gets the plain one. Set neither and a gold mine
    /// reports itself as whichever industrial prefab it happens to be wearing.
    /// </remarks>
    private Unit? SpawnBuilding(
        FactionHQ hq,
        GlobalPosition position,
        BuildingDefinition? definition,
        string displayName,
        bool randomRotation = false)
    {
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (definition == null || spawner == null)
        {
            return null;
        }

        // Every build path in the mod ends here, so the site rules are enforced once, in the one
        // place both commanders route through, rather than at each caller.
        if (!preview.IsSiteAllowed(definition, position, hq, out _))
        {
            return null;
        }

        Quaternion rotation = randomRotation
            ? Quaternion.Euler(0f, Random.Range(0f, 360f), 0f)
            : Quaternion.identity;
        GlobalPosition ground = CommanderGameAccess.SnapToTerrain(position);
        Vector3 local = ground.ToLocalPosition() + rotation * definition.spawnOffset;
        Unit? spawned = spawner.SpawnFromUnitDefinitionInEditor(
            definition,
            local.ToGlobalPosition(),
            rotation,
            hq,
            $"{displayName} {++builtNameCounter}");
        if (spawned != null)
        {
            spawned.NetworkunitName = displayName;
            builtUnits.Add(spawned);
        }

        return spawned;
    }

    /// <summary>
    /// Tears a commander-built building down. There is no refund: the money went into the ground,
    /// and a full refund would make a mine a free scouting tool.
    /// </summary>
    internal void Demolish(Unit? unit)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (unit == null || hq == null)
        {
            return;
        }

        // Despawning goes through the server object manager, same as building does.
        if (!hq.IsServer)
        {
            StatusText = "Only the host can demolish buildings.";
            return;
        }

        string label = CommanderGameAccess.GetUnitLabel(unit);
        builtUnits.Remove(unit);
        mineLevels.Remove(unit);
        factoryLevels.Remove(unit);
        dockLevels.Remove(unit);

        NetworkManagerNuclearOption? manager = NetworkManagerNuclearOption.i;
        if (unit.Identity == null || manager?.ServerObjectManager == null)
        {
            StatusText = $"{label} could not be demolished.";
            return;
        }

        manager.ServerObjectManager.Destroy(unit.Identity, !unit.Identity.IsSceneObject);
        RefreshFriendlyLists();
        StatusText = $"{label} demolished.";
    }

    private int CountMines(FactionHQ hq)
    {
        int count = 0;
        foreach (KeyValuePair<Unit, int> entry in mineLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq)
            {
                count++;
            }
        }
        return count;
    }

    private void RefreshFriendlyLists()
    {
        friendlyFactories.Clear();
        friendlyMines.Clear();
        friendlyDocks.Clear();
        builtUnits.RemoveWhere(unit => unit == null);
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return;
        }

        // The factory production list is collected here rather than from the BUILD window, so the
        // roster walk happens on this 5 s tick instead of every OnGUI frame. It is per faction and
        // does not change mid-mission, so once it has entries it is left alone.
        if (productionOptions.Count == 0)
        {
            CommanderGameAccess.CollectFactionVehicleDefinitions(productionOptions);
        }

        Factory[] factories = UnityEngine.Object.FindObjectsOfType<Factory>();
        for (int i = 0; i < factories.Length; i++)
        {
            Factory factory = factories[i];
            Unit? attached = factory == null ? null : factory.attachedUnit;
            if (factory == null
                || attached == null
                || attached.disabled
                || attached.NetworkHQ != hq
                || factory.ProductionUnit == null)
            {
                continue;
            }

            friendlyFactories.Add(factory);
        }

        foreach (KeyValuePair<Unit, int> entry in mineLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq)
            {
                friendlyMines.Add(entry.Key);
            }
        }

        foreach (KeyValuePair<Unit, int> entry in dockLevels)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Key.NetworkHQ == hq)
            {
                friendlyDocks.Add(entry.Key);
            }
        }
    }

    private BuildingDefinition? ResolveDefinition(CommanderBuildKind kind)
    {
        if (kind == CommanderBuildKind.NavalDock)
        {
            if (dockDefinitionResolved)
            {
                return dockDefinition;
            }

            Encyclopedia? dockEncyclopedia = Encyclopedia.i;
            if (dockEncyclopedia?.buildings == null)
            {
                return null;
            }

            dockDefinition = FindBuilding(dockEncyclopedia, NavalDockPrefabKeys)
                ?? FindAnyBuilding(dockEncyclopedia);
            dockDefinitionResolved = true;
            // The game ships no dock prefab, so which structure stands in for one is a guess that
            // a patch can invalidate. Say which one won in the console rather than silently
            // building a shed and calling it a harbour.
            CommanderPlugin.Log.LogInfo(dockDefinition != null
                ? $"Naval dock will be built from the '{dockDefinition.jsonKey}' prefab."
                : "No building prefab is available for a naval dock, so naval purchases stay locked.");
            return dockDefinition;
        }

        bool factory = kind == CommanderBuildKind.Factory;
        if (factory ? factoryDefinitionResolved : mineDefinitionResolved)
        {
            return factory ? factoryDefinition : mineDefinition;
        }

        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia == null || encyclopedia.buildings == null)
        {
            return null;
        }

        // A factory has to be the factory prefab — the Factory component is what produces — so
        // unlike the mine there is no fall back to whatever building happens to be first.
        string[] keys = factory ? FactoryPrefabKeys : MinePrefabKeys;
        BuildingDefinition? resolved = FindBuilding(encyclopedia, keys)
            ?? (factory ? null : FindAnyBuilding(encyclopedia));
        if (factory)
        {
            factoryDefinition = resolved;
            factoryDefinitionResolved = true;
        }
        else
        {
            mineDefinition = resolved;
            mineDefinitionResolved = true;
        }

        return resolved;
    }

    private static BuildingDefinition? FindBuilding(Encyclopedia encyclopedia, string[] keys)
    {
        for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
        {
            for (int i = 0; i < encyclopedia.buildings.Count; i++)
            {
                BuildingDefinition candidate = encyclopedia.buildings[i];
                if (candidate != null
                    && candidate.unitPrefab != null
                    && candidate.jsonKey == keys[keyIndex])
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static BuildingDefinition? FindAnyBuilding(Encyclopedia encyclopedia)
    {
        for (int i = 0; i < encyclopedia.buildings.Count; i++)
        {
            if (encyclopedia.buildings[i] != null && encyclopedia.buildings[i].unitPrefab != null)
            {
                return encyclopedia.buildings[i];
            }
        }

        return null;
    }
}
