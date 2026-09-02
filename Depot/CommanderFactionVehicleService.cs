using System;
using System.Collections.Generic;

namespace GroundControlRts;

internal sealed class CommanderFactionVehicleService : ICommanderResetSession
{
    private readonly HashSet<string> heldCategories = new(StringComparer.Ordinal);
    private readonly HashSet<VehicleDefinition> heldDefinitions = new();

    internal static CommanderFactionVehicleService? Instance { get; private set; }
    internal static FactionHQ? AutomaticDeploymentHq { get; set; }

    internal CommanderFactionVehicleService()
    {
        Instance = this;
    }

    internal float FactionFunds => CommanderGameAccess.GetLocalHq()?.factionFunds ?? 0f;

    internal bool IsCategoryHeld(string category)
    {
        return heldCategories.Contains(category);
    }

    internal void ToggleCategory(string category)
    {
        if (string.Equals(category, "All", StringComparison.Ordinal))
        {
            return;
        }

        if (!heldCategories.Add(category))
        {
            heldCategories.Remove(category);
        }
    }

    internal bool IsDefinitionHeld(VehicleDefinition definition)
    {
        return heldDefinitions.Contains(definition);
    }

    internal void ToggleDefinition(VehicleDefinition definition)
    {
        if (!heldDefinitions.Add(definition))
        {
            heldDefinitions.Remove(definition);
        }
    }

    internal int GetReserveCount(VehicleDefinition definition)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        return hq != null ? hq.GetUnitSupply(definition) : 0;
    }

    internal float GetPurchaseCost(VehicleDefinition definition)
    {
        return Math.Max(0f, definition.value);
    }

    internal bool CanAcquire(VehicleDefinition definition, out string reason)
    {
        if (GetReserveCount(definition) > 0)
        {
            reason = string.Empty;
            return true;
        }

        float cost = GetPurchaseCost(definition);
        if (FactionFunds >= cost)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"Insufficient faction funds for {CommanderGameAccess.GetVehicleLabel(definition)}.";
        return false;
    }

    internal void CommitAcquisition(VehicleDefinition definition)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            return;
        }

        if (hq.GetUnitSupply(definition) > 0)
        {
            hq.ModifyUnitSupply(definition, -1);
            return;
        }

        hq.AddFunds(-GetPurchaseCost(definition));
    }

    internal bool ShouldBlockAutomaticDeployment(VehicleDepot depot, VehicleDefinition definition)
    {
        FactionHQ? deploymentHq = AutomaticDeploymentHq;
        if (deploymentHq == null || depot.NetworkHQ != deploymentHq)
        {
            return false;
        }

        return heldDefinitions.Contains(definition)
            || heldCategories.Contains(CommanderGameAccess.GetVehicleCategoryLabel(definition));
    }

    public void ResetSession()
    {
        heldCategories.Clear();
        heldDefinitions.Clear();
        AutomaticDeploymentHq = null;
    }
}
