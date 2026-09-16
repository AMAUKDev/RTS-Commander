using System;
using System.Collections.Generic;
using UnityEngine;

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

    /// <summary>
    /// Whether a depot belongs to someone other than the faction whose deployment loop is running,
    /// pure. Generic over the owner type so <c>SelfCheck</c> can drive it with plain objects rather
    /// than live <see cref="FactionHQ"/> instances, the same shape
    /// <c>CommanderOperationsService.AdoptPicketVehicle</c> uses.
    /// <para>A depot with no owner at all is nobody's and is left to the game; a loop running
    /// outside <c>DeployVehicles</c> (no deploying faction recorded) blocks nothing.</para>
    /// </summary>
    internal static bool IsForeignDepot<T>(T? deployingHq, T? depotOwner)
        where T : class
    {
        return deployingHq != null && depotOwner != null && !ReferenceEquals(depotOwner, deployingHq);
    }

    /// <summary>Depots already reported as foreign, so the line below is written once per depot
    /// rather than once per deployment tick.</summary>
    private readonly HashSet<VehicleDepot> foreignDepotsReported = new();

    internal bool ShouldBlockAutomaticDeployment(VehicleDepot depot, VehicleDefinition definition)
    {
        FactionHQ? deploymentHq = AutomaticDeploymentHq;
        if (deploymentHq == null)
        {
            return false;
        }

        // The captured-depot leak (design.md, fob-construction_20260914 Decision 6; feasibility §6a):
        // FactionHQ.AddDepot is called ONCE, when the depot spawns, and the assembly has no
        // RemoveDepot at all — so after a base changes hands the LOSING faction's deployment loop
        // still walks that depot, spends its own vehicle supply at it, and VehicleDepot.TrySpawnVehicle
        // hands the vehicle to base.NetworkHQ, which is now the faction that took the base. The loser
        // was buying vehicles for the winner. A depot the deploying faction does not own is refused
        // here, which is the smallest place to say it: TrySpawnVehicle takes no HQ argument, so the
        // caller's identity has to come from the DeployVehicles prefix that already records it, and
        // the list the game walks (depotSorted) is private with no removal call to reach it.
        if (IsForeignDepot(deploymentHq, depot.NetworkHQ))
        {
            if (CommanderSettings.OperationsDebugLog && foreignDepotsReported.Add(depot))
            {
                CommanderPlugin.Log.LogInfo(
                    $"Refused {CommanderGameAccess.GetUnitLabel(depot)} to "
                        + $"{deploymentHq.faction?.factionName ?? "an unnamed faction"}: the depot now belongs to "
                        + $"{depot.NetworkHQ?.faction?.factionName ?? "nobody"}.");
            }

            return true;
        }

        if (depot.NetworkHQ != deploymentHq)
        {
            return false;
        }

        // The idle-pool cap (user report 2026-09-14, frame rate): factory supply became a vehicle
        // the moment the depot loop reached it, whatever the commander needed, and the operations
        // service claimed every one into a pool that reached 238 idle vehicles on the enemy side.
        // Held here, the supply stays banked at the depot and deploys the review the pool has room.
        int idle = CommanderOperationsService.PoolCount(deploymentHq);
        bool poolFull = DeploymentHeldForFullPool(
            CommanderOperationsService.OwnsGroundForce(deploymentHq), idle, CommanderSettings.PoolIdleCap);
        if (poolFull != poolFullReported.Contains(deploymentHq))
        {
            if (poolFull)
            {
                poolFullReported.Add(deploymentHq);
                CommanderAiLog.Note(
                    deploymentHq,
                    $"holds automatic deployment: {idle} vehicles idle in the pool (cap {CommanderSettings.PoolIdleCap}); supply banks at the depot.");
            }
            else
            {
                poolFullReported.Remove(deploymentHq);
                CommanderAiLog.Note(deploymentHq, $"resumes automatic deployment: {idle} vehicles idle in the pool.");
            }
        }

        if (poolFull)
        {
            return true;
        }

        if (ReservationRefuses(deploymentHq, depot, definition))
        {
            return true;
        }

        return heldDefinitions.Contains(definition)
            || heldCategories.Contains(CommanderGameAccess.GetVehicleCategoryLabel(definition));
    }

    /// <summary>Commanders whose deployment is currently held by the idle-pool cap, so the hold and
    /// the resume are each logged once.</summary>
    private readonly HashSet<FactionHQ> poolFullReported = new();

    /// <summary>
    /// Minutes a bought vehicle waits for the depot nearest its objective before the game's loop may
    /// place it anywhere: 5. A depot pad frees in seconds once the vehicle on it drives off, so five
    /// minutes is a depot that has died or is boxed in — after that the vehicle is better anywhere
    /// than nowhere. Planner-chosen.
    /// </summary>
    internal const float DeploymentReservationMinutes = 5f;

    /// <summary>One bought vehicle waiting for one depot (2026-09-15, user: "need to ensure that if
    /// multiple depots available, units sourced from depot closest to platoon/picket objective").</summary>
    private sealed class DepotReservation
    {
        internal VehicleDefinition Definition = null!;
        internal VehicleDepot? Depot;
        internal float ExpiresAt;
    }

    private readonly Dictionary<FactionHQ, List<DepotReservation>> reservations = new();

    /// <summary>
    /// Records that one banked <paramref name="definition"/> belongs at <paramref name="depot"/>.
    /// Called by the commander's buy when the nearest depot refused the direct spawn (its pad was
    /// busy) and the vehicle went into supply instead. In the 2026-09-15 match 1,046 of 1,534 buys
    /// took that path and then appeared at whichever depot the game's loop reached first.
    /// </summary>
    internal void ReserveDeployment(FactionHQ hq, VehicleDefinition definition, VehicleDepot depot)
    {
        if (hq == null || definition == null || depot == null)
        {
            return;
        }

        if (!reservations.TryGetValue(hq, out List<DepotReservation> list))
        {
            list = new List<DepotReservation>();
            reservations[hq] = list;
        }

        list.Add(new DepotReservation
        {
            Definition = definition,
            Depot = depot,
            ExpiresAt = Time.time + DeploymentReservationMinutes * 60f,
        });
    }

    /// <summary>
    /// The reservation rule, pure: the loop is refused at this depot when reservations for the
    /// definition exist, none of them names this depot, and the faction has no MORE of the vehicle
    /// banked than it has reserved (an unreserved surplus may go anywhere). A reserved depot is never
    /// refused.
    /// </summary>
    internal static bool ReservationBlocks(bool depotReserved, int reserved, int banked)
    {
        return reserved > 0 && !depotReserved && banked <= reserved;
    }

    /// <summary>Applies <see cref="ReservationBlocks"/> to the live list for one deployment: drops
    /// expired and dead-depot entries, consumes one reservation when this depot is the one named.</summary>
    private bool ReservationRefuses(FactionHQ hq, VehicleDepot depot, VehicleDefinition definition)
    {
        if (!reservations.TryGetValue(hq, out List<DepotReservation> list) || list.Count == 0)
        {
            return false;
        }

        int reserved = 0;
        int reservedHere = -1;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            DepotReservation entry = list[i];
            if (entry.Depot == null || entry.Depot.disabled || Time.time >= entry.ExpiresAt)
            {
                list.RemoveAt(i);
                continue;
            }

            if (!ReferenceEquals(entry.Definition, definition))
            {
                continue;
            }

            reserved++;
            if (ReferenceEquals(entry.Depot, depot))
            {
                reservedHere = i;
            }
        }

        if (reservedHere >= 0)
        {
            list.RemoveAt(reservedHere);
            return false;
        }

        return ReservationBlocks(false, reserved, hq.GetUnitSupply(definition));
    }

    /// <summary>
    /// Whether the depot loop is held for a full pool, pure: only for a faction whose ground force
    /// the operations service owns (a faction it does not manage keeps the game's own behaviour),
    /// and only while the idle count has reached the cap. A cap of zero or less disables the hold.
    /// </summary>
    internal static bool DeploymentHeldForFullPool(bool ownsGroundForce, int idle, int cap)
    {
        return ownsGroundForce && cap > 0 && idle >= cap;
    }

    /// <summary>The owner test at its named boundaries, run at plugin load beside the other
    /// services' self-checks.</summary>
    internal static void SelfCheck()
    {
        object alpha = new();
        object bravo = new();
        if (IsForeignDepot(alpha, bravo) != true
            || IsForeignDepot(alpha, alpha) != false
            || IsForeignDepot<object>(alpha, null) != false
            || IsForeignDepot<object>(null, bravo) != false
            || IsForeignDepot<object>(null, null) != false)
        {
            CommanderPlugin.Log.LogError(
                "Faction vehicle self-check FAILED: a depot's owner test no longer refuses a depot "
                    + "the deploying faction does not own.");
        }

        if (DeploymentHeldForFullPool(true, 12, 12) != true
            || DeploymentHeldForFullPool(true, 11, 12) != false
            || DeploymentHeldForFullPool(false, 500, 12) != false
            || DeploymentHeldForFullPool(true, 500, 0) != false)
        {
            CommanderPlugin.Log.LogError(
                "Faction vehicle self-check FAILED: the idle-pool cap no longer holds deployment at the cap, "
                    + "for managed factions only, with zero disabling it.");
        }

        if (ReservationBlocks(false, 1, 1) != true
            || ReservationBlocks(true, 1, 1) != false
            || ReservationBlocks(false, 0, 3) != false
            || ReservationBlocks(false, 1, 2) != false)
        {
            CommanderPlugin.Log.LogError(
                "Faction vehicle self-check FAILED: the depot reservation no longer refuses other depots "
                    + "while a reserved vehicle is banked, or refuses the reserved depot or an unreserved surplus.");
        }
    }

    public void ResetSession()
    {
        heldCategories.Clear();
        heldDefinitions.Clear();
        AutomaticDeploymentHq = null;
        poolFullReported.Clear();
        reservations.Clear();
    }
}
