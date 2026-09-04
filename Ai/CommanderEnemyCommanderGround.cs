using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The enemy commander's two standing postures that are not "buy another tank": a radar screen
/// pushed out to ground it can see from, and a squadron bought through the naval dock.
/// </summary>
/// <remarks>
/// The radar screen is the answer to a real asymmetry. <c>RevealPlayerBase</c> hands the enemy every
/// building the player owns, because a commander that cannot see a base has nothing to attack — but
/// it deliberately reveals nothing that moves. So the enemy knows where the base is and nothing at
/// all about the army in front of it, and its own sensors are whatever happens to be parked at home.
/// Driving a radar vehicle out to high ground on the approach is how a commander earns that
/// information instead of being given it, and it is the same trade the player makes when they push a
/// radar forward: everything it sees, it sees because it is exposed.
/// </remarks>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>Radar vehicles the commander wants standing watch before it stops buying them.</summary>
    private const int ReconPostTarget = 2;

    /// <summary>
    /// How far from the player's territory the radar screen sits. Far enough to be outside short
    /// range air defence, close enough that a ground radar can still see the approaches.
    /// </summary>
    private const float ReconStandoffMeters = 12000f;

    /// <summary>Fan the posts sit on, either side of the axis between the two territories.</summary>
    private const float ReconFanDegrees = 55f;

    /// <summary>A truck this close to its post is on station; re-ordering it only makes it shuffle.</summary>
    private const float ReconArrivedMeters = 400f;

    /// <summary>Local samples taken around a post looking for the highest ground near it.</summary>
    private const int ReconHeightSamples = 5;
    private const float ReconHeightSpreadMeters = 900f;

    /// <summary>Hulls the commander keeps at sea. Ships are expensive and slow to replace.</summary>
    private const int NavalHullTarget = 2;

    /// <summary>Share of a review's pot set aside for ships, accrued the same way airframes are.</summary>
    private const float NavalBudgetShare = 0.2f;

    private readonly List<Unit> reconUnits = new();
    private readonly List<ShipDefinition> shipCatalog = new();

    /// <summary>
    /// Drives the faction's radar vehicles out to standing overwatch posts on the approach from the
    /// player's territory, and reports whether it is short of one so the buy loop can order it.
    /// </summary>
    private void ReviewRecon(FactionHQ hq, FactionHQ localHq, CommanderState state)
    {
        reconUnits.Clear();
        if (hq.factionUnits != null)
        {
            foreach (PersistentID id in hq.factionUnits)
            {
                if (id.TryGetUnit(out Unit unit)
                    && unit != null
                    && !unit.disabled
                    && unit.definition is VehicleDefinition definition
                    && definition.vehicleType == VehicleType.RDR)
                {
                    reconUnits.Add(unit);
                }
            }
        }

        state.WantsReconUnit = reconUnits.Count < ReconPostTarget;
        if (reconUnits.Count == 0)
        {
            return;
        }

        EnsureReconPosts(state, hq, localHq);
        if (state.ReconPosts.Count == 0)
        {
            return;
        }

        for (int i = 0; i < reconUnits.Count; i++)
        {
            GlobalPosition post = state.ReconPosts[i % state.ReconPosts.Count];
            Unit truck = reconUnits[i];
            // Already on station. Every issue is a networked RPC, so a truck that has arrived is
            // left alone rather than re-ordered to the spot it is standing on.
            if (FastMath.InRange(truck.transform.GlobalPosition(), post, ReconArrivedMeters))
            {
                continue;
            }

            CommanderGameAccess.GetUnitCommand(truck)?.SetDestination(post, false);
        }
    }

    /// <summary>
    /// Picks the posts once per commander: a fan of points at standoff from the player's territory,
    /// spread either side of the axis between the two sides, each pulled to the highest ground in
    /// its neighbourhood. Height is the whole point — a radar in a valley is a radar that sees the
    /// valley. Posts are fixed for the mission, because a post that moves is a truck that never
    /// arrives.
    /// </summary>
    private static void EnsureReconPosts(CommanderState state, FactionHQ hq, FactionHQ localHq)
    {
        if (state.ReconPosts.Count > 0)
        {
            return;
        }

        GlobalPosition playerCenter = CommanderCaptureService.GetTerritoryCenter(localHq);
        GlobalPosition ownCenter = CommanderCaptureService.GetTerritoryCenter(hq);
        Vector3 axis = ownCenter.AsVector3() - playerCenter.AsVector3();
        axis.y = 0f;
        if (axis.sqrMagnitude < 1f)
        {
            return;
        }

        axis.Normalize();
        for (int i = 0; i < ReconPostTarget; i++)
        {
            // Evenly spread across the fan: two posts land on its edges, more fill in between.
            float t = ReconPostTarget == 1 ? 0.5f : i / (float)(ReconPostTarget - 1);
            float degrees = Mathf.Lerp(-ReconFanDegrees, ReconFanDegrees, t);
            Vector3 direction = Quaternion.Euler(0f, degrees, 0f) * axis;
            Vector3 nominal = playerCenter.AsVector3() + direction * ReconStandoffMeters;
            state.ReconPosts.Add(FindHighGround(new GlobalPosition(nominal.x, nominal.y, nominal.z)));
        }
    }

    /// <summary>The highest terrain within a short walk of a point, sampled rather than searched.</summary>
    private static GlobalPosition FindHighGround(GlobalPosition around)
    {
        GlobalPosition best = CommanderGameAccess.SnapToTerrain(around);
        for (int i = 1; i < ReconHeightSamples; i++)
        {
            float angle = i * (Mathf.PI * 2f / ReconHeightSamples);
            GlobalPosition candidate = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
                around.x + Mathf.Cos(angle) * ReconHeightSpreadMeters,
                around.y,
                around.z + Mathf.Sin(angle) * ReconHeightSpreadMeters));
            if (candidate.y > best.y)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>The dearest radar vehicle the budget allows: a better set sees further, which is
    /// the only thing this purchase is for.</summary>
    private VehicleDefinition? ChooseReconUnit(float budget)
    {
        VehicleDefinition? best = null;
        for (int i = 0; i < catalog.Count; i++)
        {
            VehicleDefinition definition = catalog[i];
            if (definition.vehicleType == VehicleType.RDR
                && definition.value <= budget
                && (best == null || definition.value > best.value))
            {
                best = definition;
            }
        }

        return best;
    }

    /// <summary>
    /// Buys a hull through the faction's own naval dock, under the same level gate the player is on:
    /// no dock, no ships, and each dock level opens the next class. The dock itself is built by the
    /// economy spender, the same split as mines and factories — one buys units, the other buys the
    /// ability to buy them.
    /// <para>
    /// Returns what it took out of this review's pot, which is the saved share and not the purchase:
    /// a commander with no dock takes nothing, so a faction that will never go to sea does not
    /// quietly withhold a fifth of every review from its convoys.
    /// </para>
    /// </summary>
    private float ReviewNaval(FactionHQ hq, FactionHQ localHq, CommanderState state, float share)
    {
        CommanderNavalPurchaseService? naval = CommanderNavalPurchaseService.Instance;
        int dockLevel = CommanderEconomyService.GetNavalDockLevel(hq);
        if (naval == null || dockLevel <= 0 || CountShips(hq) >= NavalHullTarget)
        {
            return 0f;
        }

        float taken = AccrueFund(ref state.NavalFund, share);
        RefreshShipCatalog();
        ShipDefinition? choice = null;
        for (int i = 0; i < shipCatalog.Count; i++)
        {
            ShipDefinition definition = shipCatalog[i];
            if (definition.value <= state.NavalFund
                && CommanderNavalPurchaseService.IsUnlocked(definition, dockLevel)
                && (choice == null || definition.value > choice.value))
            {
                choice = definition;
            }
        }

        if (choice == null)
        {
            return taken;
        }

        float spent = naval.TryPurchaseForHq(hq, choice, CommanderCaptureService.GetTerritoryCenter(localHq));
        if (spent > 0f)
        {
            state.NavalFund -= spent;
            TotalPurchases++;
            CommanderPlugin.Log.LogInfo(
                $"Enemy commander ({hq.faction.name}) put a {choice.unitName} to sea for {spent:0}.");
        }

        return taken;
    }

    private static int CountShips(FactionHQ hq)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is Ship && !unit.disabled)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Every hull the game ships, collected here rather than read off the naval window: that list is
    /// only built once the player opens RTS mode, and this commander is spending from minute zero.
    /// </summary>
    private void RefreshShipCatalog()
    {
        if (shipCatalog.Count > 0)
        {
            return;
        }

        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia?.ships == null)
        {
            return;
        }

        for (int i = 0; i < encyclopedia.ships.Count; i++)
        {
            ShipDefinition definition = encyclopedia.ships[i];
            if (definition != null
                && definition.unitPrefab != null
                && definition.IsAllowed(includeEventContent: false)
                && definition.unitPrefab.GetComponent<Ship>() != null)
            {
                shipCatalog.Add(definition);
            }
        }
    }
}
