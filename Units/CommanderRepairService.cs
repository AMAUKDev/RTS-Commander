using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderRepairService : ICommanderResetSession
{
    private readonly HashSet<Unit> nearestTargetUnits = new();
    private float statusUntil;
    private string statusText = string.Empty;

    internal static CommanderRepairService? Instance { get; private set; }

    internal CommanderRepairService()
    {
        Instance = this;
    }

    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;

    internal bool IsRepairUnit(Unit? unit)
    {
        return GetRepairer(unit) != null;
    }

    internal bool UsesNearestTarget(Unit? unit)
    {
        return unit != null && nearestTargetUnits.Contains(unit);
    }

    internal void ToggleNearestTarget(Unit unit)
    {
        Repairer? repairer = GetRepairer(unit);
        if (repairer == null)
        {
            return;
        }

        bool enabled = !nearestTargetUnits.Remove(unit);
        if (enabled)
        {
            nearestTargetUnits.Add(unit);
        }
        CommanderRepairPatches.RequestImmediateSearch(repairer);
        SetStatus(enabled
            ? "Repair targeting set to nearest damaged structure."
            : "Repair targeting restored to Basegame priority.");
    }

    internal bool ShouldUseNearestTarget(Unit? unit)
    {
        return unit != null && !unit.disabled && nearestTargetUnits.Contains(unit);
    }

    public void ResetSession()
    {
        nearestTargetUnits.Clear();
        statusText = string.Empty;
    }

    private static Repairer? GetRepairer(Unit? unit)
    {
        return unit?.GetComponentInChildren<Repairer>(true);
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + 6f;
    }
}
