using System;
using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The strategic save's world side (track <c>strategic-save_20260917</c>): which forward bases
/// existed and where, and — on load — putting those bases back and standing a paid-for garrison on
/// every control point a faction held.
/// </summary>
/// <remarks>
/// <para>
/// Three rules the developer decided on 2026-09-17, and each one has a named check below.
/// </para>
/// <para>
/// <b>A forward base is rebuilt where it stood, unless it was still being delivered.</b> A base is
/// the strategic picture, not a unit, so it comes back in kind. An order still delivering is
/// CANCELLED and not refunded (<see cref="StrategicDeliveringRefund"/>): the transport it was
/// waiting on no longer exists after a mission restart, and there is no refund anywhere else on the
/// delivery path either.
/// </para>
/// <para>
/// <b>A held control point gets a garrison placed directly, or it is lost in five seconds.</b>
/// <c>CommanderStrategicPointService.Step</c> drops an owner on the first tick that finds nothing
/// qualifying in the ring. The garrison is placed on the point's own hold posts — the same ring the
/// picket insertion lands on — and placed there, not flown or driven in, because there is nothing
/// to fly it with and no time to drive it. It is also CLAIMED into that point's picket as it lands
/// (<see cref="ClaimStrategicGarrison"/>): an unclaimed vehicle is a free vehicle, and the review
/// after the load drove every one of them to the reserve ring until this was added on 2026-09-18.
/// </para>
/// <para>
/// <b>Conservation of value: the garrison is paid for.</b> The save already banked the cash value of
/// everything that was standing on those points, so handing the garrisons over free would print
/// money on every save. They are bought at the commander's own price, from the commander's own
/// catalogue, through the commander's own "cheapest combat vehicle that can move a capture bar"
/// rule (<c>CommanderEnemyCommanderService.CheapestCaptureVehicle</c>; until 2026-09-18 that rule
/// omitted the combat half and every garrison was an unarmed ammo truck). When the chest cannot cover a
/// whole point's garrison that point is released to neutral and logged, so the mod never believes
/// it holds ground nothing is standing on.
/// </para>
/// </remarks>
internal sealed partial class CommanderOperationsService : ICommanderPersistStrategic
{
    /// <summary>
    /// The crew skill a restored garrison vehicle is spawned with when the faction has no live
    /// ground vehicle to copy one from: 0.5, the middle of the game's 0-1 range. It is a fallback
    /// only — <see cref="FactionGroundCrewSkill"/> reads a live vehicle first, so a restored
    /// garrison is normally crewed exactly like the rest of the faction's armour rather than to a
    /// number this mod invented.
    /// </summary>
    private const float DefaultGarrisonCrewSkill = 0.5f;

    /// <summary>Forward bases read out of the strategic save, waiting for
    /// <see cref="ApplyStrategicRebuild"/>. <c>RestoreStrategic</c> is load-only by contract.</summary>
    private readonly List<CommanderStrategicForwardBaseRecord> strategicForwardBases = new();

    /// <summary>What the last strategic rebuild spent on garrisons, for the conservation line in
    /// the log — the figure that, with the economy's, says a load left each faction no richer and
    /// no poorer than the save did.</summary>
    internal float StrategicGarrisonSpend { get; private set; }

    private static readonly List<VehicleDefinition> strategicCatalogScratch = new();
    private static readonly List<CommanderStrategicPoint> strategicHeldScratch = new();

    /// <summary>The vehicles one point's garrison placement just put on the ground, handed straight
    /// to <see cref="ClaimStrategicGarrison"/>. One reused list rather than one per point.</summary>
    private static readonly List<Unit> strategicGarrisonScratch = new();

    /// <summary>
    /// Whether a forward-base order is worth saving: only one that is ONLINE. Delivering, building
    /// and abandoning orders are all mid-flight, and the study's rule is that nothing in flight
    /// crosses a mission restart. Pure, for the self-check.
    /// </summary>
    internal static bool StrategicForwardBaseSaved(CommanderFobPhase phase)
    {
        return phase == CommanderFobPhase.Online;
    }

    /// <summary>
    /// What a faction gets back for the forward-base orders that were NOT saved: NOTHING. A
    /// delivering order is cancelled, not refunded (study, 2026-09-17). That is the rule the mod
    /// already applies everywhere else on the delivery path — "No refund — the structures were
    /// charged at order time, which is the stake" (<c>CancelFobOrder</c>) — and the same stance at
    /// abandonment and at teardown. Restoring a half-built order instead would strand it: the
    /// transport it was waiting on no longer exists, so it would sit until its own fifteen-minute
    /// stall timeout cancelled it anyway, ten minutes later and with no explanation. Pure, so the
    /// rule is a check rather than a comment.
    /// </summary>
    internal static float StrategicDeliveringRefund(int deliveringOrders, float structureCost)
    {
        // Deliberately ignores both arguments. They are in the signature so the rule reads as the
        // question it answers — "how much does a faction get back for N orders worth this much that
        // never arrived?" — and so a later edit that starts refunding has to change this line and
        // fail the check that pins it.
        _ = deliveringOrders;
        _ = structureCost;
        return 0f;
    }

    /// <summary>
    /// The order record synthesised for a restored forward base. Every clock is stamped from
    /// <paramref name="now"/> rather than restored, and two of them matter enough to be the reason
    /// this is a function rather than an object literal.
    /// </summary>
    /// <remarks>
    /// <b>The named rule the study asked for.</b> <c>LastContactAt</c> and <c>LastUseAt</c> default
    /// to -1, which the abandonment rule reads as "nothing hostile has ever been near it" and
    /// "nothing of ours has ever used it" — quiet and idle, which is precisely the state that makes
    /// a base eligible for demolition to pay for one nearer the front. A base restored with them at
    /// their defaults would be torn down almost immediately. Seeded to now, it gets the same grace
    /// a freshly built base gets. Pure, so a later edit that drops the seeding fails a check rather
    /// than quietly demolishing the developer's bases on every load.
    /// </remarks>
    internal static CommanderFobOrder NewRestoredForwardBaseOrder(CommanderStrategicPoint point, float now)
    {
        return new CommanderFobOrder
        {
            Point = point,
            ByAir = false,
            Purpose = CommanderLiftPurpose.FobConstruction,
            Phase = CommanderFobPhase.Online,
            Delivered = FobDeliveries,
            OrderedAt = now,
            LastProgressAt = now,
            LastContactAt = now,
            LastUseAt = now,
        };
    }

    /// <summary>
    /// What a whole point's garrison costs. Whole or nothing: a point held by one vehicle when the
    /// rule wants two is a point that changes hands on the next contact, so a garrison that cannot
    /// be afforded in full is not placed at all. Pure, for the self-check.
    /// </summary>
    internal static float StrategicGarrisonCost(float unitPrice, int wanted, bool paid)
    {
        return paid ? Mathf.Max(0f, unitPrice) * Mathf.Max(0, wanted) : 0f;
    }

    /// <summary>
    /// Whether the war chest can stand this point's garrison. Exactly enough is enough — the
    /// commander's own buy gate reads <c>definition.value &lt;= budget</c>, and disagreeing with it
    /// here would mean a garrison refused at a price the commander would have paid. Pure, for the
    /// self-check.
    /// </summary>
    internal static bool StrategicGarrisonAffordable(float funds, float unitPrice, int wanted, bool paid)
    {
        return funds >= StrategicGarrisonCost(unitPrice, wanted, paid);
    }

    /// <summary>
    /// True when this unit is one of a forward base's structures. Read by the war-chest valuation:
    /// a forward base is rebuilt in kind on load, so cashing its buildings in as well would pay for
    /// them twice.
    /// </summary>
    internal static bool IsForwardBaseBuilding(Unit? unit)
    {
        CommanderOperationsService? service = Instance;
        if (unit == null || service == null)
        {
            return false;
        }

        foreach (KeyValuePair<FactionHQ, OperationsState> entry in service.states)
        {
            List<CommanderFobOrder> orders = entry.Value.FobOrders;
            for (int i = 0; i < orders.Count; i++)
            {
                if (orders[i].Buildings.Contains(unit))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Names in the log every forward-base order that will NOT survive the save, so the developer
    /// sees what a save costs them at the moment they take it rather than wondering later where an
    /// order went. Nothing is cancelled here: pressing SAVE must not change a running match, so the
    /// order goes on delivering and is simply absent from the file (a deliberate narrowing of the
    /// study's "cancel at save time", which had the same outcome but mutated a live match).
    /// </summary>
    internal static void ReportForwardBasesLostToSave(FactionHQ hq)
    {
        CommanderOperationsService? service = Instance;
        if (hq == null || service == null || !service.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        float stake = CommanderEconomyService.FobStructuresCost();
        for (int i = 0; i < state.FobOrders.Count; i++)
        {
            CommanderFobOrder order = state.FobOrders[i];
            if (StrategicForwardBaseSaved(order.Phase) || order.Point == null)
            {
                continue;
            }

            CommanderPlugin.Log.LogInfo(
                $"Strategic save: {hq.faction?.factionName}'s forward base at {order.Point.Label} is still "
                    + $"{order.Phase} and will not survive the save. Its "
                    + $"{StrategicDeliveringRefund(1, stake):0} stake is not refunded — the same rule the "
                    + "delivery path already applies everywhere else.");
        }
    }

    public void SnapshotStrategic(CommanderStrategicWriter w)
    {
        foreach (KeyValuePair<FactionHQ, OperationsState> entry in states)
        {
            FactionHQ hq = entry.Key;
            if (hq?.faction == null || string.IsNullOrEmpty(hq.faction.factionName))
            {
                continue;
            }

            ReportForwardBasesLostToSave(hq);
            List<CommanderFobOrder> orders = entry.Value.FobOrders;
            for (int i = 0; i < orders.Count; i++)
            {
                CommanderFobOrder order = orders[i];
                if (!StrategicForwardBaseSaved(order.Phase) || order.Point == null)
                {
                    continue;
                }

                w.Snapshot.ForwardBases.Add(new CommanderStrategicForwardBaseRecord
                {
                    Faction = hq.faction.factionName,
                    PointLabel = order.Point.Label,
                    X = order.Point.Position.x,
                    Y = order.Point.Position.y,
                    Z = order.Point.Position.z,
                });
            }
        }
    }

    public void RestoreStrategic(CommanderStrategicReader r)
    {
        strategicForwardBases.Clear();
        strategicForwardBases.AddRange(r.Snapshot.ForwardBases);
    }

    /// <summary>
    /// The world half of the strategic load, driven by <see cref="CommanderStrategicSaveStore"/>
    /// AFTER the war chests are on the table, because the garrisons are bought out of them. Forward
    /// bases first, garrisons second: a rebuilt base is a depot the commander can spawn from, and a
    /// point it stands on should be garrisoned like any other.
    /// </summary>
    internal void ApplyStrategicRebuild()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return;
        }

        EnsureStates(localHq);
        int bases = RebuildStrategicForwardBases();
        PlaceStrategicGarrisons(out int pointsHeld, out int pointsGarrisoned, out float spent);
        StrategicGarrisonSpend = spent;
        strategicForwardBases.Clear();

        CommanderPlugin.Log.LogInfo(
            $"Strategic rebuild complete: {bases} forward base{(bases == 1 ? string.Empty : "s")} back up, "
                + $"{pointsGarrisoned} of {pointsHeld} held points garrisoned for {spent:0}.");
    }

    /// <summary>
    /// Puts each saved forward base back through the same <c>TryBuildFob</c> path that built it the
    /// first time, and re-attaches an ONLINE order to it so the teardown watch, the abandonment rule
    /// and the usage clock all see it. A record whose base could not be built is DROPPED and logged
    /// rather than kept: a record with nothing standing under it is the "commander believes in bases
    /// that are not there" failure the feasibility study named.
    /// </summary>
    private int RebuildStrategicForwardBases()
    {
        CommanderEconomyService? economy = CommanderEconomyService.Instance;
        CommanderStrategicPointService? pointService = CommanderStrategicPointService.Instance;
        if (economy == null || pointService == null)
        {
            return 0;
        }

        // Before the first rebuild: the name counter restarts at zero on a mission change while the
        // restored bases still carry their old numbers, so without this the first rebuilt base
        // would claim a name one of them is using.
        economy.SeedFobNameCounterFromWorld();

        int rebuilt = 0;
        for (int i = 0; i < strategicForwardBases.Count; i++)
        {
            CommanderStrategicForwardBaseRecord record = strategicForwardBases[i];
            FactionHQ? hq = FindHqByFactionName(record.Faction);
            if (hq == null || !hq.IsServer)
            {
                CommanderPlugin.Log.LogInfo(
                    $"Strategic load: dropped the forward base at {record.PointLabel} "
                        + $"(no server-side faction named '{record.Faction}').");
                continue;
            }

            CommanderStrategicPoint? point = FindPointByLabel(pointService, record.PointLabel);
            if (point == null)
            {
                CommanderPlugin.Log.LogInfo(
                    $"Strategic load: dropped the forward base at {record.PointLabel} "
                        + "(no control point of that name in the restored map).");
                continue;
            }

            // A faction with no operations state is one this mod is not commanding — the player's
            // own, with the player-side commander switched off. Its base is still put back, because
            // the base is the strategic picture; there is simply no order to attach to it, which is
            // exactly the situation a base the mod never ordered is already in.
            states.TryGetValue(hq, out OperationsState? state);

            // Every clock on the rebuilt order is stamped NOW rather than restored. Nothing in the
            // save carries a clock, and this is where that rule is paid off: a saved Time.time
            // replayed against a level clock back at zero would read as far in the future and pause
            // the teardown watch and the abandonment rule for the rest of the match.
            CommanderFobOrder order = NewRestoredForwardBaseOrder(point, Time.time);

            if (!economy.TryBuildFob(hq, point.Position, point.Label, order.Buildings, out Airbase? built)
                || built == null
                || order.Buildings.Count == 0)
            {
                CommanderPlugin.Log.LogWarning(
                    $"Strategic load: dropped the forward base at {record.PointLabel} "
                        + "(nothing could be built on that ground). The commander will not believe in it.");
                continue;
            }

            order.Base = built;
            state?.FobOrders.Add(order);
            rebuilt++;
            CommanderAiLog.Note(
                hq,
                $"strategic load: FOB {point.Label} rebuilt where it stood "
                    + $"({CommanderCaptureService.GetAirbaseLabel(built)}, {order.Buildings.Count} structures).");
        }

        return rebuilt;
    }

    /// <summary>
    /// Stands a garrison on every control point a faction held, in the commander's own priority
    /// order, paying for each one out of the war chest. A point whose garrison cannot be afforded in
    /// full is released to neutral and named in the log, so the mod's picture and the world agree.
    /// </summary>
    private void PlaceStrategicGarrisons(out int pointsHeld, out int pointsGarrisoned, out float spent)
    {
        pointsHeld = 0;
        pointsGarrisoned = 0;
        spent = 0f;

        CommanderStrategicPointService? pointService = CommanderStrategicPointService.Instance;
        if (pointService == null)
        {
            return;
        }

        bool paid = CommanderSettings.StrategicGarrisonPaid;
        int wanted = Mathf.Max(1, CommanderSettings.PointsMinGarrison);

        // Every faction, not just the ones this mod commands. The player's own faction has no
        // operations state when the player-side commander is switched off, and its points are the
        // ones the developer cares about most — garrisoning only commanded factions would lose them
        // five seconds after the load.
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq?.faction == null || !hq.IsServer)
            {
                // Spawning and factionFunds are both server-side; a pure multiplayer client throws.
                continue;
            }

            // The commander's own ranking where there is one, so "restore the most valuable points
            // first" means exactly what it means everywhere else in the mod rather than a second
            // idea of what a point is worth. An uncommanded faction falls back to discovery order.
            if (states.TryGetValue(hq, out OperationsState? state))
            {
                RankPoints(hq, state);
            }

            CollectHeldPointsInPriorityOrder(pointService, hq, state, strategicHeldScratch);
            pointsHeld += strategicHeldScratch.Count;

            strategicCatalogScratch.Clear();
            CommanderGameAccess.CollectFactionVehicleDefinitions(strategicCatalogScratch, hq);
            float skill = FactionGroundCrewSkill(hq);

            for (int i = 0; i < strategicHeldScratch.Count; i++)
            {
                CommanderStrategicPoint point = strategicHeldScratch[i];
                VehicleDefinition? definition = CommanderEnemyCommanderService.CheapestCaptureVehicle(
                    strategicCatalogScratch, paid ? hq.factionFunds : float.MaxValue);
                float price = definition == null ? 0f : Mathf.Max(0f, definition.value);
                if (definition == null
                    || !StrategicGarrisonAffordable(hq.factionFunds, price, wanted, paid))
                {
                    ReleaseStrategicPoint(
                        hq,
                        point,
                        definition == null
                            ? "no capture-capable vehicle this faction can field"
                            : $"the war chest holds {hq.factionFunds:0} and the garrison costs "
                                + $"{StrategicGarrisonCost(price, wanted, paid):0}");
                    continue;
                }

                int placed = SpawnStrategicGarrison(
                    hq, point, definition, wanted, skill, strategicGarrisonScratch);
                if (placed == 0)
                {
                    ReleaseStrategicPoint(hq, point, "no garrison vehicle could be placed on its hold posts");
                    continue;
                }

                // Before the money, because a garrison the commander does not know about is one it
                // drives away on its next review (see ClaimStrategicGarrison).
                ClaimStrategicGarrison(hq, point, strategicGarrisonScratch);

                float cost = StrategicGarrisonCost(price, placed, paid);
                if (cost > 0f)
                {
                    hq.AddFunds(-cost);
                    spent += cost;
                }

                pointsGarrisoned++;
                CommanderAiLog.Note(
                    hq,
                    $"strategic load: {placed} x {CommanderGameAccess.GetVehicleLabel(definition)} stand on "
                        + $"{point.Label} for {cost:0}.");
            }

            strategicHeldScratch.Clear();
        }

        strategicCatalogScratch.Clear();
        strategicGarrisonScratch.Clear();
    }

    /// <summary>
    /// The points this faction holds, ranked first by the commander's own ranking and then by
    /// whatever the ranking did not cover, so a held point off the reach set is garrisoned last
    /// rather than never.
    /// </summary>
    private static void CollectHeldPointsInPriorityOrder(
        CommanderStrategicPointService pointService,
        FactionHQ hq,
        OperationsState? state,
        List<CommanderStrategicPoint> into)
    {
        into.Clear();
        for (int i = 0; state != null && i < state.RankedPoints.Count; i++)
        {
            CommanderStrategicPoint point = state.RankedPoints[i].Point;
            if (IsStrategicGarrisonPoint(point, hq) && !into.Contains(point))
            {
                into.Add(point);
            }
        }

        IReadOnlyList<CommanderStrategicPoint> all = pointService.Points;
        for (int i = 0; i < all.Count; i++)
        {
            if (IsStrategicGarrisonPoint(all[i], hq) && !into.Contains(all[i]))
            {
                into.Add(all[i]);
            }
        }
    }

    /// <summary>
    /// A control point this faction holds BY PRESENCE, which is the only kind a garrison can hold.
    /// Two exemptions, both because their ownership is somebody else's state and a garrison would
    /// change nothing (study, 2026-09-17): a BASE is owned by the game's own capture ring and is
    /// restored with <c>ForceCapture</c>, and a SITE with a mine standing on it is owned by the
    /// mine, which the economy rebuild has already put back by the time this runs. A site with NO
    /// mine is held by presence like a village and does need one.
    /// </summary>
    internal static bool IsStrategicGarrisonPoint(CommanderStrategicPoint? point, FactionHQ hq)
    {
        return point != null
            && StrategicPointKinds.IsControlPoint(point.Kind)
            && StrategicGarrisonHoldsThisKind(point.Kind, point.Mine != null && !point.Mine.disabled)
            && point.Hold.OwnerIndex >= 0
            && ReferenceEquals(point.GetOwner(), hq);
    }

    /// <summary>The exemption rule on its own, pure, so it can be checked at load. Presence is what
    /// a garrison buys; a point whose owner is decided by something else gets none.</summary>
    internal static bool StrategicGarrisonHoldsThisKind(StrategicPointKind kind, bool mineStanding)
    {
        if (kind == StrategicPointKind.Base)
        {
            return false;
        }

        return kind != StrategicPointKind.Site || !mineStanding;
    }

    /// <summary>
    /// Spawns the garrison on the point's own hold posts. Placed, not delivered: the transports the
    /// picket insertion would use do not exist on the frame a mission starts, and a point with
    /// nothing on it is lost within five seconds. The spawn itself is the mod's one existing
    /// put-a-vehicle-here primitive, the one <c>CommanderMobileEmplacementService.RestoreTrailer</c>
    /// uses.
    /// </summary>
    private int SpawnStrategicGarrison(
        FactionHQ hq,
        CommanderStrategicPoint point,
        VehicleDefinition definition,
        int wanted,
        float skill,
        List<Unit> spawnedInto)
    {
        spawnedInto.Clear();
        if (definition.unitPrefab == null || NetworkSceneSingleton<Spawner>.i == null)
        {
            return 0;
        }

        List<GlobalPosition> posts = EnsureHoldPosts(point, wanted);
        if (posts.Count == 0)
        {
            return 0;
        }

        int placed = 0;
        for (int i = 0; i < wanted; i++)
        {
            GlobalPosition post = posts[i % posts.Count];
            GlobalPosition ground = CommanderGameAccess.SnapToTerrain(post);
            Vector3 local = ground.ToLocalPosition() + Vector3.up * definition.spawnOffset.y;
            GroundVehicle? spawned = NetworkSceneSingleton<Spawner>.i.SpawnVehicle(
                definition.unitPrefab,
                local.ToGlobalPosition(),
                Quaternion.identity,
                Vector3.zero,
                hq,
                null,
                skill,
                holdPosition: true,
                null);
            if (spawned != null)
            {
                placed++;
                spawnedInto.Add(spawned);
            }
        }

        return placed;
    }

    /// <summary>
    /// Tells this commander that the vehicles just placed on <paramref name="point"/> are its
    /// garrison, by putting them in the point's own picket.
    /// <para>
    /// Without this the placement was a no-op within seconds, and the failure is worth recording
    /// because it is structural rather than a slip. A vehicle nobody has claimed is, by the one
    /// definition of "already spoken for" (<c>IsClaimedVehicle</c>), a free vehicle: the review's
    /// opening sweep put all thirty-one restored garrisons into the free pool, and <c>StagePool</c>
    /// then did exactly its job and parked the free pool on the reserve ring — driving every
    /// garrison off the point it had just been bought to hold (observed in game, 2026-09-18, log
    /// reading <c>pool=19 staged=19</c> on the first review after a load).
    /// </para>
    /// <para>
    /// The picket is the mod's existing name for "a detachment standing on a point", so this reuses
    /// it rather than inventing a restored-garrison state: <c>PlanPickets</c> prunes its dead,
    /// <c>FillPickets</c> tops it up, <c>DriveToHoldPosts</c> keeps it on the ring and the unit
    /// economy's retirement already counts it. A point that already carries a mission keeps it and
    /// the garrison joins that — a forward base with something standing on it is manned, which is
    /// true — and the promotion walk at the end of <c>PlanPickets</c> turns a picket on a threatened
    /// point back into a forward base on the very next review, so creating one here cannot hold a
    /// point at the wrong purpose.
    /// </para>
    /// <para>
    /// A faction with no operations state is skipped and needs no claim: nothing sweeps its
    /// vehicles into a pool, because the pool is the operations state.
    /// </para>
    /// </summary>
    private void ClaimStrategicGarrison(FactionHQ hq, CommanderStrategicPoint point, List<Unit> garrison)
    {
        if (garrison.Count == 0 || !states.TryGetValue(hq, out OperationsState? state) || state == null)
        {
            return;
        }

        if (!TryFindMissionFor(state, point, out CommanderOperationsMission? mission) || mission == null)
        {
            mission = new CommanderOperationsMission
            {
                Kind = CommanderMissionKind.Picket,
                Point = point,
                WantedPlatoons = 0,
                Label = point.Label,
            };
            state.Missions.Add(mission);
        }

        for (int i = 0; i < garrison.Count; i++)
        {
            AdoptPicketVehicle(mission.PicketMembers, state.Pool, garrison[i]);
        }
    }

    /// <summary>
    /// Hands a point back to neutral because its garrison could not be paid for or could not be
    /// placed, and says so. Without this the mod would go on believing it holds ground nothing is
    /// standing on until the hold tick's grace expired and quietly took it away.
    /// </summary>
    private static void ReleaseStrategicPoint(FactionHQ hq, CommanderStrategicPoint point, string why)
    {
        point.Hold = new HoldState
        {
            OwnerIndex = -1,
            CandidateIndex = -1,
            Progress = 0f,
            Contested = false,
        };
        CommanderAiLog.Note(hq, $"strategic load: {point.Label} LOST on load — {why}.");
        CommanderPlugin.Log.LogWarning(
            $"Strategic load: {hq.faction?.factionName} loses {point.Label} — {why}.");
    }

    /// <summary>
    /// The crew skill the faction's own ground vehicles are running at, so a restored garrison is
    /// crewed like the rest of the army rather than to a number this mod invented. Falls back to
    /// <see cref="DefaultGarrisonCrewSkill"/> only when the faction has no live ground vehicle at
    /// all, which on a freshly restarted mission is the ordinary case for the first faction
    /// restored.
    /// </summary>
    private static float FactionGroundCrewSkill(FactionHQ hq)
    {
        if (hq.factionUnits == null)
        {
            return DefaultGarrisonCrewSkill;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is GroundVehicle vehicle && !vehicle.disabled)
            {
                return vehicle.skill;
            }
        }

        return DefaultGarrisonCrewSkill;
    }

    private static FactionHQ? FindHqByFactionName(string factionName)
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

    private static CommanderStrategicPoint? FindPointByLabel(
        CommanderStrategicPointService pointService, string label)
    {
        IReadOnlyList<CommanderStrategicPoint> points = pointService.Points;
        for (int i = 0; i < points.Count; i++)
        {
            if (string.Equals(points[i].Label, label, StringComparison.Ordinal))
            {
                return points[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The strategic restore's decision table, checked at plugin load: which forward-base orders are
    /// saved, what the unsaved ones are worth back, and the whole-or-nothing garrison rule with the
    /// conservation-of-value toggle in both positions.
    /// </summary>
    private static void CheckStrategicRestore(List<string> failures)
    {
        Expect(failures, "an online forward base is saved",
            StrategicForwardBaseSaved(CommanderFobPhase.Online), true);
        Expect(failures, "a forward base still being delivered is cashed in, not restored",
            StrategicForwardBaseSaved(CommanderFobPhase.Delivering), false);
        Expect(failures, "a forward base mid-construction is cashed in, not restored",
            StrategicForwardBaseSaved(CommanderFobPhase.Building), false);
        Expect(failures, "a forward base being abandoned is not restored",
            StrategicForwardBaseSaved(CommanderFobPhase.Abandoning), false);

        Expect(failures, "a forward base that never arrived is not refunded",
            StrategicDeliveringRefund(deliveringOrders: 1, structureCost: 900f), 0f);
        Expect(failures, "three forward bases that never arrived are not refunded either",
            StrategicDeliveringRefund(deliveringOrders: 3, structureCost: 900f), 0f);
        Expect(failures, "a commander with no orders in flight is refunded nothing",
            StrategicDeliveringRefund(deliveringOrders: 0, structureCost: 900f), 0f);

        // Which kinds a garrison can actually hold. A base and a mined site are owned by somebody
        // else's state, so parking vehicles on them changes nothing and buys nothing.
        Expect(failures, "a village is held by its garrison",
            StrategicGarrisonHoldsThisKind(StrategicPointKind.Village, mineStanding: false), true);
        Expect(failures, "a hilltop is held by its garrison",
            StrategicGarrisonHoldsThisKind(StrategicPointKind.Hilltop, mineStanding: false), true);
        Expect(failures, "an airbase is never garrisoned, because its owner is the capture ring",
            StrategicGarrisonHoldsThisKind(StrategicPointKind.Base, mineStanding: false), false);
        Expect(failures, "a site with a mine on it is never garrisoned, because the mine owns it",
            StrategicGarrisonHoldsThisKind(StrategicPointKind.Site, mineStanding: true), false);
        Expect(failures, "a site with no mine on it is held by its garrison like a village",
            StrategicGarrisonHoldsThisKind(StrategicPointKind.Site, mineStanding: false), true);

        Expect(failures, "a garrison is paid for at the commander's own price",
            StrategicGarrisonCost(unitPrice: 250f, wanted: 2, paid: true), 500f);
        Expect(failures, "a garrison costs nothing when conservation of value is switched off",
            StrategicGarrisonCost(unitPrice: 250f, wanted: 2, paid: false), 0f);
        Expect(failures, "a war chest holding exactly the garrison price affords it",
            StrategicGarrisonAffordable(funds: 500f, unitPrice: 250f, wanted: 2, paid: true), true);
        Expect(failures, "a war chest one short of a whole garrison affords none of it",
            StrategicGarrisonAffordable(funds: 499f, unitPrice: 250f, wanted: 2, paid: true), false);
        Expect(failures, "a point is restored whole or not at all: half a garrison is never bought",
            StrategicGarrisonAffordable(funds: 250f, unitPrice: 250f, wanted: 2, paid: true), false);
        Expect(failures, "an empty war chest still affords a garrison when it is not paid for",
            StrategicGarrisonAffordable(funds: 0f, unitPrice: 250f, wanted: 2, paid: false), true);

        // Points are restored in priority order until the money runs out: the third point in the
        // ranking is the one that falls off a chest holding two garrisons, not the first.
        float chest = 1000f;
        int restored = 0;
        for (int i = 0; i < 3; i++)
        {
            if (!StrategicGarrisonAffordable(chest, unitPrice: 250f, wanted: 2, paid: true))
            {
                continue;
            }

            chest -= StrategicGarrisonCost(unitPrice: 250f, wanted: 2, paid: true);
            restored++;
        }

        Expect(failures, "points are restored in priority order until the money runs out", restored, 2);
        Expect(failures, "the war chest is spent down as points are restored", chest, 0f);
        Expect(failures, "an empty war chest loses the first point rather than granting it free",
            StrategicGarrisonAffordable(funds: 0f, unitPrice: 250f, wanted: 2, paid: true), false);

        // The restored forward base's clocks. Both default to -1, which the abandonment rule reads
        // as quiet and idle — the state that gets a base demolished.
        CommanderFobOrder restoredBase = NewRestoredForwardBaseOrder(null!, 1234.5f);
        Expect(failures, "a restored forward base is not read as never having been contested",
            restoredBase.LastContactAt, 1234.5f);
        Expect(failures, "a restored forward base is not read as never having been used",
            restoredBase.LastUseAt, 1234.5f);
        Expect(failures, "a restored forward base's clocks are stamped now, never left at their default",
            restoredBase.LastContactAt >= 0f && restoredBase.LastUseAt >= 0f, true);
        Expect(failures, "a restored forward base is online, not waiting on a delivery",
            restoredBase.Phase == CommanderFobPhase.Online, true);
        Expect(failures, "a restored forward base counts its delivery as already made",
            restoredBase.Delivered, FobDeliveries);
        Expect(failures, "a restored forward base's stale valve runs from now, not from the save",
            restoredBase.LastProgressAt, 1234.5f);
    }
}
