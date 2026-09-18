using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Repair: which friendly buildings are damaged, and the paid crews a commander sends to fix them.
/// <para>
/// A crew is the faction's own repair truck, bought out of faction funds and dropped beside the
/// building it was hired for. From there the Basegame <see cref="Repairer"/> loop does the work —
/// this service only pins which building the crew should drive to, through
/// <see cref="CommanderRepairPatches"/>. Both commanders hire crews at the same price.
/// </para>
/// </summary>
/// <remarks>
/// ponytail: the crew is a real vehicle rather than a timer that heals the building, so it can be
/// shelled on the way in and it shows up on the map like everything else. It also means repair
/// needs no new networking — spawning a vehicle is something this mod already does.
/// </remarks>
internal sealed class CommanderRepairService : ICommanderTickActive, ICommanderResetSession
{
    private const float DamageRefreshIntervalSeconds = 3f;

    /// <summary>How far out of the building the crew is dropped, on top of the building radius.</summary>
    private const float CrewDropMargin = 80f;

    private readonly HashSet<Unit> nearestTargetUnits = new();
    private readonly Dictionary<Unit, Unit> crewAssignments = new();
    private readonly List<Unit> damagedBuildings = new();
    private readonly List<Unit> staleCrews = new();
    private float nextDamageRefreshAt;
    private float statusUntil;
    private string statusText = string.Empty;

    internal static CommanderRepairService? Instance { get; private set; }

    internal CommanderRepairService()
    {
        Instance = this;
        nextDamageRefreshAt = CommanderScheduler.Stagger("repair.damage", DamageRefreshIntervalSeconds);
    }

    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;

    /// <summary>Friendly buildings currently in need of repair, refreshed while RTS mode is up.</summary>
    internal IReadOnlyList<Unit> DamagedBuildings => damagedBuildings;

    internal static float CrewCost => Mathf.Max(0f, CommanderSettings.RepairCrewCost);

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

    /// <summary>The building a hired crew was paid to fix, while that job is still outstanding.</summary>
    internal bool TryGetAssignedTarget(Unit? crew, out Unit target)
    {
        target = null!;
        if (crew == null || !crewAssignments.TryGetValue(crew, out Unit assigned))
        {
            return false;
        }

        if (assigned == null || assigned is not IRepairable repairable || !repairable.NeedsRepair())
        {
            crewAssignments.Remove(crew);
            return false;
        }

        target = assigned;
        return true;
    }

    /// <summary>Is a paid crew already on its way to <paramref name="building"/>?</summary>
    internal bool HasCrewAssigned(Unit building)
    {
        foreach (KeyValuePair<Unit, Unit> entry in crewAssignments)
        {
            if (entry.Key != null && !entry.Key.disabled && ReferenceEquals(entry.Value, building))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Hires a crew for the local faction. The BUILD window REPAIR rows call this.</summary>
    internal bool SendRepairCrew(Unit building)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            SetStatus("No faction HQ.");
            return false;
        }

        // Crews are spawned through the server object manager, so a pure client cannot hire one.
        if (!hq.IsServer)
        {
            SetStatus("Only the host can send a repair crew.");
            return false;
        }

        if (hq.factionFunds < CrewCost)
        {
            SetStatus("Insufficient faction funds for a repair crew.");
            return false;
        }

        if (!TryHireCrew(hq, building))
        {
            SetStatus("This faction fields no repair vehicle.");
            return false;
        }

        SetStatus($"Repair crew dispatched to {CommanderGameAccess.GetUnitLabel(building)} for {CrewCost:0}.");
        return true;
    }

    /// <summary>
    /// The enemy commander half. Same price, same crew, out of its own funds — the duel stays
    /// symmetric, so a bombed enemy refinery gets patched up just like yours does.
    /// </summary>
    internal bool TrySendEnemyRepairCrew(FactionHQ hq)
    {
        if (!hq.IsServer || hq.factionUnits == null)
        {
            return false;
        }

        // A repair crew is a ground vehicle like any other, so it is bought under the same ceiling
        // (design.md, unit-economy_20260918 §2.3; one predicate, every commander ground buy). The
        // player's own REPAIR button is deliberately NOT gated — the ceiling is a rule about what the
        // computer commander grows to, not a limit on what the player may do with their own money.
        if (!CommanderOperationsService.GroundBuyAllowed(hq))
        {
            return false;
        }

        if (!TryFindWorstDamagedBuilding(hq, out Unit? worst) || !TryHireCrew(hq, worst!))
        {
            return false;
        }

        CommanderAiLog.Note(hq, $"sent a repair crew to {CommanderGameAccess.GetUnitLabel(worst)}.");
        return true;
    }

    /// <summary>
    /// The damaged building a repair crew would go to: the dearest one with no crew already on the
    /// way, or none. Internal (one definition, two callers — Reuse rule 4): the send itself and the
    /// priority ladder's building-rung demand read (<c>WantsEnemyRepairCrew</c>) must agree on
    /// whether there is work at all, or the ladder would reserve rung money for a commander whose
    /// repairs are already covered.
    /// </summary>
    internal bool TryFindWorstDamagedBuilding(FactionHQ hq, out Unit? worst)
    {
        worst = null;
        if (hq.factionUnits == null)
        {
            return false;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (!id.TryGetUnit(out Unit candidate)
                || candidate == null
                || candidate is not Building
                || candidate is not IRepairable repairable
                || !repairable.NeedsRepair()
                || HasCrewAssigned(candidate))
            {
                continue;
            }

            if (worst == null || candidate.definition.value > worst.definition.value)
            {
                worst = candidate;
            }
        }

        return worst != null;
    }

    /// <summary>
    /// Whether this commander has a damaged building with no crew on the way — the repair half of
    /// rung 4's demand (design.md, commander-priorities_20260914): repair crews stay ahead of the
    /// structure spend, so a commander with a bombed refinery but a finished build list still gets
    /// its turn in the draw.
    /// </summary>
    internal bool WantsEnemyRepairCrew(FactionHQ hq)
    {
        // The ceiling is read here as well as at the send, because this method's whole contract is
        // that the two must agree: a commander whose crew the ceiling will refuse must not have rung
        // money reserved for one (unit-economy_20260918 §2.3).
        return hq.IsServer
            && CommanderOperationsService.GroundBuyAllowed(hq)
            && TryFindWorstDamagedBuilding(hq, out _);
    }

    public void TickActive()
    {
        if (CommanderScheduler.IsDue(ref nextDamageRefreshAt, DamageRefreshIntervalSeconds))
        {
            RefreshDamagedBuildings();
            PruneCrews();
        }
    }

    public void ResetSession()
    {
        nearestTargetUnits.Clear();
        crewAssignments.Clear();
        damagedBuildings.Clear();
        staleCrews.Clear();
        statusText = string.Empty;
    }

    private bool TryHireCrew(FactionHQ hq, Unit building)
    {
        VehicleDefinition? definition = CommanderGameAccess.FindRepairVehicleDefinition(hq);
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (definition == null || spawner == null)
        {
            return false;
        }

        // Dropped clear of the building footprint, or the crew spawns inside the wall it came to
        // fix and the Basegame pathing has to shove it out first.
        float angle = Random.Range(0f, Mathf.PI * 2f);
        float distance = building.maxRadius * 2f + CrewDropMargin;
        GlobalPosition site = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
            building.startPosition.x + Mathf.Cos(angle) * distance,
            building.startPosition.y,
            building.startPosition.z + Mathf.Sin(angle) * distance));

        Quaternion rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        Unit? crew = spawner.SpawnFromUnitDefinitionInEditor(
            definition,
            (site.ToLocalPosition() + rotation * definition.spawnOffset).ToGlobalPosition(),
            rotation,
            hq,
            $"NOC_CREW_{hq.GetInstanceID()}_{Time.frameCount}");
        if (crew == null)
        {
            return false;
        }

        hq.AddFunds(-CrewCost);
        crewAssignments[crew] = building;

        Repairer? repairer = GetRepairer(crew);
        if (repairer != null)
        {
            CommanderRepairPatches.RequestImmediateSearch(repairer);
        }

        return true;
    }

    private void RefreshDamagedBuildings()
    {
        damagedBuildings.Clear();
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq?.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit != null
                && unit is Building
                && unit is IRepairable repairable
                && repairable.NeedsRepair())
            {
                damagedBuildings.Add(unit);
            }
        }
    }

    private void PruneCrews()
    {
        staleCrews.Clear();
        foreach (KeyValuePair<Unit, Unit> entry in crewAssignments)
        {
            if (entry.Key == null
                || entry.Key.disabled
                || entry.Value == null
                || entry.Value is not IRepairable repairable
                || !repairable.NeedsRepair())
            {
                staleCrews.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleCrews.Count; i++)
        {
            crewAssignments.Remove(staleCrews[i]);
        }
        staleCrews.Clear();
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
