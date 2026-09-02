using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// An opposing commander that spends its faction's funds. The Basegame only ever deploys the
/// vehicle reserve a mission was authored with and never buys anything, so an enemy faction
/// that converts income into reinforcements is the difference between a sandbox and a game.
/// </summary>
/// <remarks>
/// ponytail: this buys and shapes the composition, then hands the units to the Basegame depot
/// deployment and its objective-seeking ground AI rather than issuing routes of its own.
/// Upgrade to real orders only once buying alone stops being enough of a fight.
/// </remarks>
internal sealed class CommanderEnemyCommanderService : ICommanderTickPersistent, ICommanderResetSession
{
    private const float ReviewIntervalSeconds = 30f;
    private const float FundsFloor = 500f;

    private readonly List<VehicleDefinition> catalog = new();
    private readonly List<VehicleDefinition> candidates = new();
    private float nextReviewAt;

    internal static CommanderEnemyCommanderService? Instance { get; private set; }

    internal CommanderEnemyCommanderService()
    {
        Instance = this;
        nextReviewAt = CommanderScheduler.Stagger("ai.enemyCommander", ReviewIntervalSeconds);
    }

    /// <summary>Units this session's enemy commanders have bought, for the settings readout.</summary>
    internal int TotalPurchases { get; private set; }

    internal static string GetLevelLabel(int level)
    {
        return level switch
        {
            1 => "CAUTIOUS",
            2 => "STANDARD",
            3 => "AGGRESSIVE",
            _ => "OFF",
        };
    }

    public void TickPersistent()
    {
        int level = Mathf.Clamp(CommanderSettings.EnemyCommanderLevel, 0, 3);
        if (level == 0 || !CommanderScheduler.IsDue(ref nextReviewAt, ReviewIntervalSeconds))
        {
            return;
        }

        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return;
        }

        int friendlyAircraft = CountFriendlyAircraft(localHq);
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || ReferenceEquals(hq, localHq) || !hq.IsServer || hq.faction == null)
            {
                continue;
            }

            Review(hq, level, friendlyAircraft);
        }
    }

    public void ResetSession()
    {
        catalog.Clear();
        candidates.Clear();
        TotalPurchases = 0;
        nextReviewAt = CommanderScheduler.Stagger("ai.enemyCommander", ReviewIntervalSeconds);
    }

    private void Review(FactionHQ hq, int level, int friendlyAircraft)
    {
        if (level >= 3)
        {
            // Aggressive is the deliberate cheat setting: a small stipend so the enemy keeps
            // buying on missions that give its faction almost no income.
            hq.AddFunds(ReviewIntervalSeconds * 10f);
        }

        int budgetUnits = level switch
        {
            1 => 1,
            2 => 2,
            _ => 4,
        };
        float spendable = (hq.factionFunds - FundsFloor) * (level >= 3 ? 0.5f : 0.25f);
        if (spendable <= 0f)
        {
            return;
        }

        CollectCatalog(hq);
        if (catalog.Count == 0)
        {
            return;
        }

        bool needsAirDefence = friendlyAircraft > 0 && CountAirDefence(hq) < friendlyAircraft;
        for (int purchase = 0; purchase < budgetUnits && spendable > 0f; purchase++)
        {
            VehicleDefinition? choice = Choose(spendable, needsAirDefence);
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
                $"Enemy commander ({hq.faction.name}) bought {CommanderGameAccess.GetVehicleLabel(choice)} for {cost:0}.");
        }
    }

    private VehicleDefinition? Choose(float budget, bool needsAirDefence)
    {
        candidates.Clear();
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.value > budget || !IsCombatVehicle(definition))
            {
                continue;
            }

            if (needsAirDefence == IsAirDefence(definition))
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

    private static int CountFriendlyAircraft(FactionHQ hq)
    {
        int count = 0;
        if (hq.factionUnits == null)
        {
            return 0;
        }

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
