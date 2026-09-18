using System;
using System.Collections;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSupplyHeliService
{
    private void SpawnCargoRun(
        CargoAircraftOption aircraftOption,
        Loadout cargoLoadout,
        string cargoLabel,
        Airbase airbase,
        bool useHighTerrainClearance,
        float terrainClearanceMeters,
        bool useAirdrop,
        string supportSummary,
        bool useOtherAirfields,
        IReadOnlyList<GlobalPosition> targets)
    {
        if (!CanHostSpawn(out FactionHQ? hq, out string error))
        {
            SetStatus(error);
            return;
        }

        if (!IsCompatibleAirbase(airbase, hq!, aircraftOption.Definition))
        {
            SetStatus("The selected airbase no longer supports this aircraft.");
            return;
        }

        QueuedCargoSpawn request = new(
            aircraftOption,
            CloneLoadout(cargoLoadout),
            cargoLabel,
            airbase,
            useHighTerrainClearance,
            terrainClearanceMeters,
            useAirdrop,
            supportSummary,
            useOtherAirfields,
            targets);
        Airbase? spawnAirbase = ResolveSpawnAirbase(request, hq!);
        if (spawnAirbase == null
            || pendingAircraftSpawn != null
            || IsSamHelipadBusy(request))
        {
            queuedCargoSpawns.Enqueue(request);
            SetStatus(useOtherAirfields
                ? $"Supply run queued. Waiting for a compatible friendly airfield ({queuedCargoSpawns.Count} queued)."
                : $"Supply run queued for {GetAirbaseLabel(airbase)} ({queuedCargoSpawns.Count} queued).");
            return;
        }

        TrySpawnCargoRunAtAirbase(request, spawnAirbase, hq!);
    }

    private void SpawnCargoRun(
        CargoAircraftOption aircraftOption,
        Loadout cargoLoadout,
        string cargoLabel,
        Airbase airbase,
        bool useHighTerrainClearance,
        float terrainClearanceMeters,
        bool useAirdrop,
        string supportSummary,
        bool useOtherAirfields,
        GlobalPosition target)
    {
        SpawnCargoRun(
            aircraftOption,
            cargoLoadout,
            cargoLabel,
            airbase,
            useHighTerrainClearance,
            terrainClearanceMeters,
            useAirdrop,
            supportSummary,
            useOtherAirfields,
            new[] { target });
    }

    private void TryProcessQueuedCargoSpawns()
    {
        if (pendingAircraftSpawn != null || queuedCargoSpawns.Count == 0)
        {
            return;
        }

        if (!CanHostSpawn(out FactionHQ? hq, out _))
        {
            return;
        }

        int attempts = queuedCargoSpawns.Count;
        while (attempts-- > 0)
        {
            QueuedCargoSpawn request = queuedCargoSpawns.Dequeue();
            // The spawn is bought for the request's own faction — null means the local HQ, which
            // is every request the UI or the SAM site service makes. The insertion entry is the
            // first to carry another commanded HQ.
            FactionHQ spawnHq = request.Hq ?? hq!;
            if (!IsCompatibleAirbase(request.RequestedAirbase, spawnHq, request.Aircraft.Definition)
                && !request.UseOtherAirfields)
            {
                SetStatus("A queued supply run was cancelled because its airbase is no longer friendly or compatible.");
                continue;
            }

            if (IsSamHelipadBusy(request))
            {
                queuedCargoSpawns.Enqueue(request);
                continue;
            }

            Airbase? spawnAirbase = ResolveSpawnAirbase(request, spawnHq);
            if (spawnAirbase == null)
            {
                queuedCargoSpawns.Enqueue(request);
                continue;
            }

            TrySpawnCargoRunAtAirbase(request, spawnAirbase, spawnHq);
            return;
        }
    }

    private bool IsSamHelipadBusy(QueuedCargoSpawn request)
    {
        int siteId = GetSamSiteId(request.SupportSummary);
        if (siteId < 0)
        {
            return false;
        }

        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            if (entry.Key != null
                && !entry.Key.disabled
                && entry.Value.TargetOverrideActive
                && GetSamSiteId(entry.Value) == siteId)
            {
                return true;
            }
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq?.factionUnits == null)
        {
            return false;
        }

        Vector3 target = request.Target.ToLocalPosition();
        foreach (PersistentID unitId in hq.factionUnits)
        {
            if (!unitId.TryGetUnit(out Unit unit)
                || unit is not Aircraft aircraft
                || aircraft.disabled)
            {
                continue;
            }

            Vector3 position = aircraft.transform.position;
            if (CommanderGameAccess.HorizontalDistance(position, target) <= 60f
                && Mathf.Abs(position.y - target.y) <= 80f)
            {
                return true;
            }
        }

        return false;
    }

    private static int GetSamSiteId(string supportSummary)
    {
        int siteId = ParseFoundationSiteId(supportSummary);
        if (siteId < 0)
        {
            siteId = ParseCargoSiteId(supportSummary);
        }
        if (siteId < 0)
        {
            siteId = ParseJacknifeSiteId(supportSummary);
        }
        return siteId;
    }

    private static int GetSamSiteId(CargoMission mission)
    {
        if (mission.FoundationSiteId >= 0)
        {
            return mission.FoundationSiteId;
        }
        if (mission.DepositSiteId >= 0)
        {
            return mission.DepositSiteId;
        }
        return mission.JacknifeSiteId;
    }

    private static Airbase? ResolveSpawnAirbase(QueuedCargoSpawn request, FactionHQ hq)
    {
        bool protectedSamRun = RequiresProtectedSamAirbase(request.SupportSummary);
        if (IsAvailableAirbase(request.RequestedAirbase, hq, request.Aircraft.Definition)
            && (!protectedSamRun
                || !request.UseOtherAirfields
                || IsSamSupplyAirbaseSafe(request.RequestedAirbase, request.Target)))
        {
            return request.RequestedAirbase;
        }

        if (!request.UseOtherAirfields)
        {
            return null;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;
        Airbase? nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (Airbase candidate in hq.GetAirbases())
        {
            if (!IsAvailableAirbase(candidate, hq, request.Aircraft.Definition))
            {
                continue;
            }
            if (protectedSamRun && !IsSamSupplyAirbaseSafe(candidate, request.Target))
            {
                continue;
            }

            Transform positionTransform = candidate.center != null ? candidate.center : candidate.transform;
            float distance = Vector3.SqrMagnitude(positionTransform.position - cameraPosition);
            if (distance < nearestDistance)
            {
                nearest = candidate;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private static void IssueSupplyReturnToBase(Aircraft? aircraft, CargoMission mission)
    {
        if (aircraft == null || aircraft.disabled || aircraft.pilots == null)
        {
            return;
        }

        CloseCargoDoors(mission);
        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot == null)
            {
                continue;
            }

            pilot.AILandingState ??= new AIPilotLandingState();
            pilot.SwitchState(pilot.AILandingState);
            return;
        }
    }

    /// <summary>
    /// A delivered insertion's flight home: the rotary landing state — every cargo aircraft the
    /// catalog offers has a helo or tiltwing pilot (<see cref="HasHeloPilot"/>) — with the existing
    /// return-airbase override pinning the origin airbase. <see cref="IssueSupplyReturnToBase"/>'s
    /// fixed-wing state is its own callers' business; for a helicopter's routine recovery it would
    /// fly an aeroplane approach, so the insertion gets the rotary-correct sibling (one caller;
    /// the same reasoning <c>CommanderAirCommandService.IssueReturnToBase</c> records for its own
    /// split).
    /// </summary>
    private static void IssueInsertionReturnToBase(Aircraft? aircraft, CargoMission mission)
    {
        if (aircraft == null || aircraft.disabled || aircraft.pilots == null)
        {
            return;
        }

        CloseCargoDoors(mission);
        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot == null)
            {
                continue;
            }

            pilot.AIHeloLandingState ??= new AIHeloLandingState();
            pilot.SwitchState(pilot.AIHeloLandingState);
            return;
        }
    }

    /// <summary>
    /// Buys and spawns one cargo run at <paramref name="airbase"/> for <paramref name="hq"/> — the
    /// single place supply money meets a spawn, so the insertion's vehicle charge rides here rather
    /// than in its own entry (a charge and a spawn must succeed or roll back together). Returns
    /// false when nothing left the ground and nothing was kept.
    /// </summary>
    private bool TrySpawnCargoRunAtAirbase(QueuedCargoSpawn request, Airbase airbase, FactionHQ hq)
    {
        CargoAircraftOption aircraftOption = request.Aircraft;

        bool purchased = false;
        if (hq.GetUnitSupply(aircraftOption.Definition) <= 0)
        {
            float cost = aircraftOption.Definition.value;
            if (hq.factionFunds < cost)
            {
                SetStatus("The faction cannot afford this supply aircraft.");
                return false;
            }

            hq.AddFunds(-cost);
            hq.ModifyUnitSupply(aircraftOption.Definition, 1);
            purchased = true;
        }

        // An insertion's cargo vehicles are new units entering the world through the loadout;
        // their price is charged once here and never refunded — the vehicles stay (design
        // Decision 2/3). The hull above may still come free out of stock; the vehicles never do.
        if (request.InsertionCargoValue > 0f)
        {
            if (hq.factionFunds < request.InsertionCargoValue)
            {
                if (purchased)
                {
                    hq.ModifyUnitSupply(aircraftOption.Definition, -1);
                    hq.AddFunds(aircraftOption.Definition.value);
                }

                SetStatus("The faction cannot afford the insertion's cargo vehicles.");
                return false;
            }

            hq.AddFunds(-request.InsertionCargoValue);
        }

        pendingAircraftSpawn = new PendingAircraftSpawn(
            hq,
            aircraftOption.Definition,
            airbase,
            request.CargoLabel,
            request.Target,
            request.HighTerrainClearance,
            request.TerrainClearanceMeters,
            request.Airdrop,
            request.SupportSummary,
            purchased,
            purchased ? aircraftOption.Definition.value : 0f,
            request.Targets,
            Time.unscaledTime + PendingSpawnTimeoutSeconds,
            insertionPoint: request.InsertionPoint,
            insertionCargoValue: request.InsertionCargoValue)
        {
            InsertionFlightId = request.InsertionFlightId,
        };

        AircraftDefinition definition = aircraftOption.Definition;
        int liveryIndex = definition.aircraftParameters.GetRandomLiveryForFaction(hq.faction);
        // Every cargo loadout this service builds starts empty and only ever has cargo mounts
        // placed in it, so there is nothing for the cannon rule to remove — but it is applied here
        // all the same (Reuse rule 4, one rule for every commander spawn): the supply spawns do not
        // go through TryLaunchAiAircraft, and a future cargo recipe that kept a gun pod would
        // otherwise slip past the only place the rule is enforced.
        Loadout loadout = CommanderAirCommandService.WithoutInternalCannons(CloneLoadout(request.Loadout));
        // Airborne over the base rather than out of a hangar (fix, 2026-09-15) whenever the wing's
        // own launches are airborne (the Gameplay hangar toggle off) or this base's deck has wrecked
        // a transport within the block window: the overnight log shows every enemy transport torn
        // apart on the deck 24–36 s after spawning. The stock deduction the hangar would have made
        // is made here instead, so the two paths leave the inventory the same.
        // The clear-hangar rule (user decision 2026-09-15): a transport uses a hangar only when one
        // is clear; otherwise it starts airborne over the base, with the blocker in the log.
        string blockedBy = string.Empty;
        Hangar? clearHangar = CommanderSettings.AiAircraftLaunchFromHangar
                && !TransportLaunchBlocked(airbase)
                && !CommanderAirCommandService.DeckRecentlyUsed(airbase)
            ? CommanderAirCommandService.TryFindClearHangar(airbase, definition, out blockedBy)
            : null;
        bool airborne = clearHangar == null;
        if (airborne && CommanderSettings.AiAircraftLaunchFromHangar)
        {
            CommanderAiLog.Note(
                hq,
                $"transport {aircraftOption.Label} starts airborne over {GetAirbaseLabel(airbase)}: "
                    + (blockedBy.Length > 0 ? $"no hangar is clear ({blockedBy})." : "no clear hangar or the deck was just used."));
        }

        if (airborne)
        {
            pendingAircraftSpawn.AirborneSpawn = true;
            Aircraft? spawned = CommanderAirCommandService.SpawnAirborneOverBase(
                hq,
                airbase,
                definition,
                loadout,
                new LiveryKey(liveryIndex),
                definition.aircraftParameters.DefaultFuelLevel,
                request.Target,
                fromMapEdge: false,
                onDeck: 0);
            if (spawned == null)
            {
                NotifySamMissionFailed(request.SupportSummary);
                pendingAircraftSpawn = null;
                if (purchased)
                {
                    hq.ModifyUnitSupply(definition, -1);
                    hq.AddFunds(definition.value);
                }

                if (request.InsertionCargoValue > 0f)
                {
                    hq.AddFunds(request.InsertionCargoValue);
                }

                SetStatus("The spawner refused the airborne supply aircraft.");
                return false;
            }

            hq.ModifyUnitSupply(definition, -1);
            SetStatus($"Spawned {aircraftOption.Label} airborne over {GetAirbaseLabel(airbase)} with {request.CargoLabel}."
                + (purchased ? " Purchased from faction funds." : string.Empty));
            return true;
        }

        Airbase.TrySpawnResult result = clearHangar!.TrySpawnAircraft(
            null,
            definition,
            new LiveryKey(liveryIndex),
            loadout,
            definition.aircraftParameters.DefaultFuelLevel);
        if (result.Allowed)
        {
            CommanderAirCommandService.StampDeckSpawn(airbase);
        }

        if (!result.Allowed)
        {
            NotifySamMissionFailed(request.SupportSummary);
            pendingAircraftSpawn = null;
            if (purchased)
            {
                hq.ModifyUnitSupply(definition, -1);
                hq.AddFunds(definition.value);
            }

            if (request.InsertionCargoValue > 0f)
            {
                hq.AddFunds(request.InsertionCargoValue);
            }

            SetStatus("The airbase rejected the supply aircraft spawn.");
            return false;
        }

        string purchaseLabel = purchased ? " Purchased from faction funds." : string.Empty;
        SetStatus($"Spawned {aircraftOption.Label} with {request.CargoLabel}.{purchaseLabel}");
        return true;
    }

    /// <summary>
    /// One vehicle an insertion can buy as cargo: its platoon role, the price that will actually be
    /// charged, its display name, and where the price came from. Cargo variants in the game's
    /// assets often carry a placeholder price or none at all (the first play test showed prices of
    /// 0, 1, 2 and 20), so the charge resolves through
    /// <see cref="CommanderOperationsService.ResolveInsertionVehiclePrice"/> against the faction's
    /// own ground catalog — the depot price when that catalog lists the same vehicle by name, the
    /// cargo's own value otherwise.
    /// </summary>
    private readonly struct InsertionVehicle
    {
        internal InsertionVehicle(CommanderPlatoonRole role, float value, string name, bool fromDepot)
        {
            Role = role;
            Value = value;
            Name = name;
            FromDepot = fromDepot;
        }

        internal CommanderPlatoonRole Role { get; }
        internal float Value { get; }
        internal string Name { get; }
        internal bool FromDepot { get; }
    }

    /// <summary>One placeable vehicle-carrying mount: its hardpoint slot, and the ground vehicles
    /// it carries.</summary>
    private readonly struct VehicleMountCandidate
    {
        internal VehicleMountCandidate(int slot, WeaponMount mount, List<InsertionVehicle> vehicles)
        {
            Slot = slot;
            Mount = mount;
            Vehicles = vehicles;
        }

        internal int Slot { get; }
        internal WeaponMount Mount { get; }
        internal List<InsertionVehicle> Vehicles { get; }
    }

    /// <summary>
    /// A registering aircraft must still be this low to match an insertion pending: the insertion's
    /// own transports are hangar spawns, which start on the deck, while the air wing launches its
    /// buys into the air at <c>CommanderAirCommandMissions.LaunchAltitudeMeters</c> (1200 m) — so a
    /// rotary airframe still that high up at registration is the wing's, not this flight's, and no
    /// same-type wing buy can be mistaken for an insertion transport (or the other way round).
    /// Only weakens if the hangar-launch Gameplay toggle puts the wing on the deck too; that toggle
    /// exists to re-test the ejection problem, not for regular play.
    /// </summary>
    private const float InsertionDeckSpawnMaxMeters = 100f;

    /// <summary>
    /// The decline this service returns when an airdrop was demanded — the landing zone is woodland
    /// or too steep — and no transport on the roster fields a cargo mount whose vehicles carry
    /// parachutes. Named rather than inlined because the operations side quotes it back to the
    /// player in the denial line and asks the same question through
    /// <see cref="HasLaunchableVehicleTransport"/> before it ever launches.
    /// </summary>
    internal const string NoParachuteCargoDecline = "no transport fields parachute-capable ground vehicles";

    /// <summary>
    /// The decline this service returns when the priority ladder's picket bank, rather than the
    /// faction balance, is what the flight cannot be paid out of. Named rather than inlined because
    /// the operations side matches on it to say "saving for the flight" instead of refusing the
    /// point (departure 2026-09-14) — a rung that is accumulating is not a rung that has said no.
    /// </summary>
    internal const string PicketShareDecline = "the ladder's picket share cannot cover the flight";

    /// <summary>Scratch for the insertion selection, reused across every combination tried in one
    /// <see cref="TryLaunchInsertionAircraft"/> call rather than allocated per pair.</summary>
    private readonly List<VehicleMountCandidate> insertionCandidates = new();
    private readonly List<CommanderPlatoonRole> insertionRoles = new();
    private readonly List<float> insertionValues = new();
    private readonly List<string> insertionNames = new();
    private readonly List<WeaponMount> insertionMounts = new();
    private readonly List<int> insertionSlots = new();
    private readonly List<int> insertionPicks = new();
    private readonly List<WeaponMount> insertionChosenMounts = new();

    /// <summary>Scratch for the depot-price lookup, cleared by the collector itself
    /// (<c>CollectFactionVehicleDefinitions</c> clears before filling).</summary>
    private static readonly List<VehicleDefinition> insertionDepotScratch = new();

    /// <summary>
    /// The AI picket-insertion entry (design.md, heli-picket-insertion_20260913) — the second
    /// programmatic caller after <c>RequestSamSiteFoundationDrop</c> (Reuse rule 5: parameterised
    /// by HQ rather than forked), gated server-side instead of on <c>CanHostSpawn</c>'s local-HQ
    /// test. Picks the best complete combination of transport, airbase and the two cargo vehicles
    /// the operations side's chooser wants (one air-defence plus the cheapest other — a load with
    /// an air-defence vehicle beats a cheaper load without one, design Decision 8), then hands the
    /// flight to the same queue-and-spawn path the SAM runs ride. The hull is charged at spawn and
    /// refunded on recovery by the existing chain; the vehicles are charged at spawn and never
    /// refunded.
    /// <para>
    /// <paramref name="allowance"/> is the priority ladder's rung-3 share for this HQ (design.md,
    /// commander-priorities_20260914 Section 3): the flight's hull and vehicles are charged against
    /// it, so the cargo budget is the smaller of what the faction holds and what the rung was
    /// granted. <paramref name="charged"/> returns the worst-case total the rung's share must cover
    /// (a hull found free in stock only makes the real flight cheaper).
    /// </para>
    /// </summary>
    /// <param name="wantedVehicles">How many vehicles the point is short of (user decision
    /// 2026-09-14): a picket that has lost one of its pair asks for one, and the chooser loads
    /// exactly that rather than a standing pair the establishment has no room for.</param>
    /// <param name="requireAirdrop">True when the operations side scouted the landing zone and found
    /// it unlandable (user report 2026-09-14). Only cargo mounts whose vehicles carry parachutes are
    /// then considered, and the flight is dispatched as an airdrop — the game flies the run at its
    /// own 200 m and releases over the point, so nothing here sets an altitude.</param>
    internal bool TryLaunchInsertionAircraft(
        FactionHQ hq,
        CommanderStrategicPoint point,
        GlobalPosition target,
        float allowance,
        int wantedVehicles,
        bool requireAirdrop,
        out string decline,
        out float charged,
        bool preferHeavyHull = false,
        IReadOnlyList<CommanderPlatoonRole>? wantedRoles = null,
        int insertionFlightId = CommanderCargoFlightSlot.Unslotted)
    {
        decline = string.Empty;
        charged = 0f;
        if (NetworkManagerNuclearOption.i == null
            || !NetworkManagerNuclearOption.i.Server.Active
            || hq == null
            || !hq.IsServer)
        {
            decline = "no host to launch from";
            return false;
        }

        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }

        // Vehicle prices resolve against the faction's own ground catalog (see
        // InsertionVehicle), so the affordability gate and the charge mean real money.
        Dictionary<string, float> depotPrices = CollectInsertionDepotPrices(hq);

        CargoAircraftOption? bestAircraft = null;
        Airbase? bestAirbase = null;
        Loadout? bestLoadout = null;
        string bestLabel = string.Empty;
        float bestCargoValue = 0f;
        float bestTotal = float.MaxValue;
        float bestHull = 0f;
        bool bestHasAirDefence = false;
        bool sawVehicleMount = false;
        bool sawThreatenedRoute = false;

        for (int aircraftIndex = 0; aircraftIndex < aircraftOptions.Count; aircraftIndex++)
        {
            CargoAircraftOption aircraft = aircraftOptions[aircraftIndex];
            float hull = Mathf.Max(0f, aircraft.Definition.value);
            // Worst case the hull comes out of funds as well — TrySpawnCargoRunAtAirbase may still
            // find one free in stock, which only makes the real flight cheaper. The rung's share
            // caps it before the balance does, whichever is smaller.
            float budget = Mathf.Min(hq.factionFunds, Mathf.Max(0f, allowance)) - hull;
            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (!IsAvailableAirbase(airbase, hq, aircraft.Definition))
                {
                    continue;
                }

                // The operations side's launch gate measured the route from the airbase nearest the
                // point; the cheapest complete load may sit at a different one, so every candidate
                // base is put through the same test here. One definition, two callers — a flight is
                // never dispatched down a leg the gate would have refused.
                if (airbase.center != null
                    && CommanderOperationsService.IsInsertionRouteThreatened(
                        hq, airbase.center.GlobalPosition(), target))
                {
                    sawThreatenedRoute = true;
                    continue;
                }

                CollectVehicleMountCandidates(
                    hq, airbase, aircraft, depotPrices, requireAirdrop, insertionCandidates);
                if (insertionCandidates.Count == 0)
                {
                    continue;
                }

                sawVehicleMount = true;
                for (int a = 0; a < insertionCandidates.Count; a++)
                {
                    TryInsertionCombination(
                        aircraft, airbase, insertionCandidates[a], null,
                        budget, wantedVehicles, preferHeavyHull, wantedRoles, ref bestAircraft, ref bestAirbase, ref bestLoadout, ref bestLabel, ref bestCargoValue, ref bestTotal, ref bestHull, ref bestHasAirDefence);
                    for (int b = a + 1; b < insertionCandidates.Count; b++)
                    {
                        // Two mounts load together only from different slots that do not preclude
                        // each other — a combined cargo bay precludes everything, so a bay heli
                        // carries one mount, which is fine when that mount carries both vehicles.
                        if (insertionCandidates[a].Slot == insertionCandidates[b].Slot
                            || SetsConflict(aircraft.HardpointSets, insertionCandidates[a].Slot, insertionCandidates[b].Slot))
                        {
                            continue;
                        }

                        TryInsertionCombination(
                            aircraft, airbase, insertionCandidates[a], insertionCandidates[b],
                            budget, wantedVehicles, preferHeavyHull, wantedRoles, ref bestAircraft, ref bestAirbase, ref bestLoadout, ref bestLabel, ref bestCargoValue, ref bestTotal, ref bestHull, ref bestHasAirDefence);
                    }
                }
            }
        }

        if (bestAircraft == null || bestAirbase == null || bestLoadout == null)
        {
            if (sawThreatenedRoute && !sawVehicleMount)
            {
                decline = "hostile air defence tracked along every route to the point";
                return false;
            }

            // Money was NOT the limit when the bank and the balance both cover the cheapest complete
            // flight priced over every held base (fix, 2026-09-14): the base that could fly the
            // cheap load sat behind a threatened route, the bases left could only launch a dearer
            // hull, and the refusal read as "saving for the flight (31/31)" with the bank full —
            // for a whole match. Say which it was instead, so the bank is not told to grow.
            float cheapestClear = CheapestInsertionFlightValue(hq, wantedVehicles, depotPrices, requireAirdrop, target);
            if (cheapestClear >= float.MaxValue && sawThreatenedRoute)
            {
                decline = "hostile air defence tracked along the route from every base that could launch a transport";
                return false;
            }

            if (sawVehicleMount
                && cheapestClear < float.MaxValue
                && allowance >= cheapestClear
                && hq.factionFunds >= cheapestClear)
            {
                decline = "no held base can launch the affordable transport right now"
                    + DescribeInsertionAttempt(hq, target, wantedVehicles, requireAirdrop, allowance, depotPrices);
                return false;
            }

            // The share, not the balance, was the binding limit when the allowance sits below the
            // funds — the rung will grant again next ladder review, so the reason says whose
            // pocket is short.
            if (sawVehicleMount && allowance < hq.factionFunds)
            {
                decline = PicketShareDecline;
                return false;
            }

            decline = sawVehicleMount
                ? "cannot afford the picket's vehicles"
                : requireAirdrop
                    ? NoParachuteCargoDecline
                    : "no transport fields mountable ground vehicles";
            return false;
        }

        charged = bestTotal;

        QueuedCargoSpawn request = new(
            bestAircraft,
            bestLoadout,
            bestLabel,
            bestAirbase,
            true,
            100f,
            requireAirdrop,
            "Picket insertion",
            false,
            new[] { target },
            hq,
            point,
            bestCargoValue)
        {
            InsertionFlightId = insertionFlightId,
        };
        Airbase? spawnAirbase = ResolveSpawnAirbase(request, hq);
        if (spawnAirbase == null || pendingAircraftSpawn != null || IsSamHelipadBusy(request))
        {
            // No free hangar this instant: queue behind the SAM runs and open the record anyway —
            // the operations side's stale-request valve covers a queue that never drains.
            queuedCargoSpawns.Enqueue(request);
            return true;
        }

        return TrySpawnCargoRunAtAirbase(request, spawnAirbase, hq);
    }

    /// <summary>
    /// Whether a picket insertion could be launched at all right now: this commander holds an
    /// airbase that will spawn a transport whose cargo mounts carry ground vehicles. The gate the
    /// picket delivery rule reads (<c>CommanderOperationsService.PicketDeliveryMode</c>, departure
    /// 2026-09-14) before it reserves an off-road point for the flight — reserving a point on a
    /// roster that fields no vehicle-carrying transport would simply leave it empty for the match.
    /// <para>Deliberately the SAME candidate walk <see cref="TryLaunchInsertionAircraft"/> makes
    /// (Reuse rule 4, one definition), stopped at the first hit and with the price and route tests
    /// left out: affordability moves review to review and is the flight's own business, while this
    /// answers the standing question of whether the roster can do it at all. Called once per
    /// commander per review.</para>
    /// </summary>
    /// <param name="requireAirdrop">True asks the narrower question the wooded-point rule needs: can
    /// this roster field a load it could PARACHUTE onto a point it cannot land on (user report
    /// 2026-09-14)? Same walk, one extra mount test (Reuse rule 4, parameterised rather than
    /// forked).</param>
    internal bool HasLaunchableVehicleTransport(FactionHQ? hq, bool requireAirdrop = false)
    {
        if (NetworkManagerNuclearOption.i == null
            || !NetworkManagerNuclearOption.i.Server.Active
            || hq == null
            || !hq.IsServer)
        {
            return false;
        }

        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }

        Dictionary<string, float> depotPrices = CollectInsertionDepotPrices(hq);
        for (int i = 0; i < aircraftOptions.Count; i++)
        {
            CargoAircraftOption aircraft = aircraftOptions[i];
            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (!IsAvailableAirbase(airbase, hq, aircraft.Definition))
                {
                    continue;
                }

                CollectVehicleMountCandidates(
                    hq, airbase, aircraft, depotPrices, requireAirdrop, insertionCandidates);
                if (insertionCandidates.Count > 0)
                {
                    insertionCandidates.Clear();
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// What one complete picket insertion would cost this commander right now: the cheapest
    /// combination of transport hull and the vehicles the shared chooser
    /// (<c>CommanderOperationsService.PickInsertionCargo</c>) loads for
    /// <paramref name="wantedVehicles"/>. <c>float.MaxValue</c> when no held airbase will launch a
    /// transport whose cargo mounts carry ground vehicles at all.
    /// <para>The priority ladder's rung 3 saves toward this number (design.md,
    /// pickets-first_20260914, departure 2026-09-14): a flight costs a hull plus two vehicles and a
    /// per-review share of ten to thirty could never reach it, so the rung banks its allocation up
    /// to exactly one flight's price and no further.</para>
    /// <para>The SAME walk <see cref="HasLaunchableVehicleTransport"/> makes (Reuse rule 4, one
    /// definition), with the price kept instead of stopping at the first hit and with the budget
    /// and route tests left out — this answers "what does a flight cost", not "can this one fly
    /// today". Called once per commander per ladder review, the same cadence as that walk.</para>
    /// </summary>
    internal float CheapestInsertionFlightValue(FactionHQ? hq, int wantedVehicles)
    {
        if (NetworkManagerNuclearOption.i == null
            || !NetworkManagerNuclearOption.i.Server.Active
            || hq == null
            || !hq.IsServer)
        {
            return float.MaxValue;
        }

        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }

        Dictionary<string, float> depotPrices = CollectInsertionDepotPrices(hq);
        // Priced twice, for a landing flight and for an airdrop flight, and the DEARER of the two
        // cheapest is the target (fix, 2026-09-14). The estimate used to price the landing load
        // only; on a wooded map nearly every request demands an airdrop, whose parachute-capable
        // cargo costs more, so the bank stopped at the landing price and every launch was refused
        // with the bank "full": `saving for the flight (31/31)` on 371 requests in a row and not
        // one transport in the air. An airdrop price that cannot be launched at all is ignored.
        float landing = CheapestInsertionFlightValue(hq, wantedVehicles, depotPrices, requireAirdrop: false);
        float airdrop = CheapestInsertionFlightValue(hq, wantedVehicles, depotPrices, requireAirdrop: true);
        return InsertionSavingsTargetPrice(landing, airdrop);
    }

    /// <summary>What one complete flight of the given kind would cost right now, for the request's
    /// own report and the bank's feedback; <c>float.MaxValue</c> when none can launch.</summary>
    internal float CheapestInsertionFlightValue(
        FactionHQ hq, int wantedVehicles, bool requireAirdrop, GlobalPosition? clearRouteTo = null)
    {
        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active || hq == null || !hq.IsServer)
        {
            return float.MaxValue;
        }

        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }

        return CheapestInsertionFlightValue(hq, wantedVehicles, CollectInsertionDepotPrices(hq), requireAirdrop, clearRouteTo);
    }

    /// <summary>
    /// The numbers behind a refusal that money and routes cannot explain (diagnostic, 2026-09-14):
    /// for the cheapest transport hull that a clear, available base will launch — the same pair the
    /// price estimate found — the real cargo budget, every vehicle the mounts offer with its price,
    /// and what the shared chooser picked out of them under that budget. Empty when no such pair
    /// exists. Read once per refusal, so the cost is one candidate walk.
    /// </summary>
    private string DescribeInsertionAttempt(
        FactionHQ hq, GlobalPosition target, int wantedVehicles, bool requireAirdrop, float allowance, Dictionary<string, float> depotPrices)
    {
        CargoAircraftOption? cheapest = null;
        Airbase? at = null;
        for (int i = 0; i < aircraftOptions.Count; i++)
        {
            CargoAircraftOption aircraft = aircraftOptions[i];
            if (cheapest != null && aircraft.Definition.value >= cheapest.Definition.value)
            {
                continue;
            }

            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (!IsAvailableAirbase(airbase, hq, aircraft.Definition)
                    || (airbase.center != null
                        && CommanderOperationsService.IsInsertionRouteThreatened(hq, airbase.center.GlobalPosition(), target)))
                {
                    continue;
                }

                CollectVehicleMountCandidates(hq, airbase, aircraft, depotPrices, requireAirdrop, insertionCandidates);
                if (insertionCandidates.Count > 0)
                {
                    cheapest = aircraft;
                    at = airbase;
                    break;
                }
            }
        }

        if (cheapest == null || at == null)
        {
            return string.Empty;
        }

        CollectVehicleMountCandidates(hq, at, cheapest, depotPrices, requireAirdrop, insertionCandidates);
        insertionRoles.Clear();
        insertionValues.Clear();
        insertionNames.Clear();
        insertionMounts.Clear();
        insertionSlots.Clear();
        for (int a = 0; a < insertionCandidates.Count; a++)
        {
            AppendVehicleCandidate(insertionCandidates[a]);
        }

        float hull = Mathf.Max(0f, cheapest.Definition.value);
        float budget = Mathf.Min(hq.factionFunds, Mathf.Max(0f, allowance)) - hull;
        CommanderOperationsService.PickInsertionCargo(insertionRoles, insertionValues, budget, wantedVehicles, insertionPicks);
        System.Text.StringBuilder text = new();
        text.Append(" [").Append(cheapest.Definition.unitName).Append(" at ").Append(GetAirbaseLabel(at))
            .Append($": hull {hull:0.##}, allowance {allowance:0.##}, cargo budget {budget:0.##}, wanted {wantedVehicles}, ")
            .Append(insertionCandidates.Count).Append(" mounts offering");
        for (int i = 0; i < insertionNames.Count; i++)
        {
            text.Append(i == 0 ? " " : ", ").Append(insertionNames[i]).Append(' ').Append($"{insertionValues[i]:0.##}")
                .Append('/').Append(insertionRoles[i]).Append("@slot").Append(insertionSlots[i]);
        }

        text.Append("; chooser picked ").Append(insertionPicks.Count).Append(']');
        insertionCandidates.Clear();
        return text.ToString();
    }

    /// <summary>The price the picket bank aims at, pure: the dearer of the landing and airdrop
    /// flights, ignoring an airdrop that cannot be launched (<c>float.MaxValue</c>). Both
    /// unlaunchable reads as unlaunchable.</summary>
    internal static float InsertionSavingsTargetPrice(float landingPrice, float airdropPrice)
    {
        if (airdropPrice >= float.MaxValue)
        {
            return landingPrice;
        }

        return landingPrice >= float.MaxValue ? airdropPrice : Mathf.Max(landingPrice, airdropPrice);
    }

    /// <param name="clearRouteTo">When given, a base whose route to this point the threat gate
    /// refuses is left out — the price of the flight that could ACTUALLY fly to that point right
    /// now, which is what a request's bank has to reach.</param>
    private float CheapestInsertionFlightValue(
        FactionHQ hq, int wantedVehicles, Dictionary<string, float> depotPrices, bool requireAirdrop, GlobalPosition? clearRouteTo = null)
    {
        float best = float.MaxValue;
        for (int i = 0; i < aircraftOptions.Count; i++)
        {
            CargoAircraftOption aircraft = aircraftOptions[i];
            float hull = Mathf.Max(0f, aircraft.Definition.value);
            if (hull >= best)
            {
                // A hull already dearer than a complete flight found elsewhere can never win, and
                // the cargo walk below is the expensive half.
                continue;
            }

            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (!IsAvailableAirbase(airbase, hq, aircraft.Definition))
                {
                    continue;
                }

                if (clearRouteTo != null
                    && airbase.center != null
                    && CommanderOperationsService.IsInsertionRouteThreatened(hq, airbase.center.GlobalPosition(), clearRouteTo.Value))
                {
                    continue;
                }

                CollectVehicleMountCandidates(
                    hq, airbase, aircraft, depotPrices, requireAirdrop, insertionCandidates);
                for (int a = 0; a < insertionCandidates.Count; a++)
                {
                    KeepCheapestInsertionPrice(insertionCandidates[a], null, wantedVehicles, hull, ref best);
                    for (int b = a + 1; b < insertionCandidates.Count; b++)
                    {
                        // The pair rule is TryLaunchInsertionAircraft's: two mounts load together
                        // only from different slots that do not preclude each other.
                        if (insertionCandidates[a].Slot == insertionCandidates[b].Slot
                            || SetsConflict(aircraft.HardpointSets, insertionCandidates[a].Slot, insertionCandidates[b].Slot))
                        {
                            continue;
                        }

                        KeepCheapestInsertionPrice(
                            insertionCandidates[a], insertionCandidates[b], wantedVehicles, hull, ref best);
                    }
                }
            }
        }

        insertionCandidates.Clear();
        return best;
    }

    /// <summary>Prices one candidate load through the shared chooser at no budget limit and keeps it
    /// when it is the cheapest complete flight seen. The unpriced sibling of
    /// <see cref="TryInsertionCombination"/> — no loadout is built, because nothing is being
    /// launched.</summary>
    private void KeepCheapestInsertionPrice(
        VehicleMountCandidate first, VehicleMountCandidate? second, int wantedVehicles, float hull, ref float best)
    {
        insertionRoles.Clear();
        insertionValues.Clear();
        insertionNames.Clear();
        insertionMounts.Clear();
        insertionSlots.Clear();
        AppendVehicleCandidate(first);
        if (second.HasValue)
        {
            AppendVehicleCandidate(second.Value);
        }

        CommanderOperationsService.PickInsertionCargo(
            insertionRoles, insertionValues, float.MaxValue, wantedVehicles, insertionPicks);
        if (insertionPicks.Count == 0)
        {
            return;
        }

        float total = hull;
        for (int i = 0; i < insertionPicks.Count; i++)
        {
            total += insertionValues[insertionPicks[i]];
        }

        if (total < best)
        {
            best = total;
        }
    }

    /// <summary>
    /// Every vehicle cargo mount this heli, airbase and HQ combination accepts. Called per
    /// (aircraft, airbase) pair in one selection, so the pair enumeration below never re-reads the
    /// catalog inside its loops.
    /// </summary>
    private static void CollectVehicleMountCandidates(
        FactionHQ hq,
        Airbase airbase,
        CargoAircraftOption aircraft,
        IReadOnlyDictionary<string, float> depotPrices,
        bool requireAirdrop,
        List<VehicleMountCandidate> candidates)
    {
        candidates.Clear();
        for (int s = 0; s < aircraft.CargoSlots.Count; s++)
        {
            CargoSlotOption slot = aircraft.CargoSlots[s];
            for (int m = 0; m < slot.Mounts.Count; m++)
            {
                WeaponMount mount = slot.Mounts[m];
                if (!WeaponChecker.MountAllowedHQ(mount, hq)
                    || !WeaponChecker.MountAllowedAirbase(mount, airbase)
                    || (requireAirdrop && !CargoMountSupportsAirdrop(mount))
                    || !TryGetVehicleCargo(hq, mount, depotPrices, out List<InsertionVehicle> vehicles))
                {
                    continue;
                }

                candidates.Add(new VehicleMountCandidate(slot.HardpointIndex, mount, vehicles));
            }
        }
    }

    /// <summary>The faction's own ground-catalog prices by vehicle name — the depot price ladder the
    /// insertion charges read, so a cargo variant carrying a placeholder price still costs what the
    /// same vehicle costs at the depot.</summary>
    private static Dictionary<string, float> CollectInsertionDepotPrices(FactionHQ hq)
    {
        Dictionary<string, float> prices = new();
        CommanderGameAccess.CollectFactionVehicleDefinitions(insertionDepotScratch, hq);
        for (int i = 0; i < insertionDepotScratch.Count; i++)
        {
            VehicleDefinition definition = insertionDepotScratch[i];
            if (definition != null && !string.IsNullOrEmpty(definition.unitName))
            {
                prices[definition.unitName] = definition.value;
            }
        }

        insertionDepotScratch.Clear();
        return prices;
    }

    /// <summary>
    /// The ground vehicles a cargo mount carries, false when it carries none. Tightened after the
    /// first play test (user, 2026-09-14): a vehicle is insertion cargo only when its
    /// <c>MountedCargo.cargo</c> is a real <c>VehicleDefinition</c> whose prefab is a
    /// <c>GroundVehicle</c> — supply crates and containers are not pickets, and a transport whose
    /// cargo is not vehicles never becomes an insertion candidate at all — and munitions trucks
    /// (<see cref="CommanderPlatoonRole.Truck"/>) are excluded, because a truck cannot hold a
    /// point and the game's rearm brain would claim it anyway. Which vehicles are mountable is
    /// asset data a decompile cannot answer, so this runtime read is the one both the insertion
    /// chooser and the roster log go through.
    /// </summary>
    private static bool TryGetVehicleCargo(
        FactionHQ hq,
        WeaponMount mount,
        IReadOnlyDictionary<string, float> depotPrices,
        out List<InsertionVehicle> vehicles)
    {
        vehicles = new List<InsertionVehicle>();
        if (!IsRuntimeCargoMount(mount) || mount.prefab == null)
        {
            return false;
        }

        MountedCargo[] cargoItems = mount.prefab.GetComponentsInChildren<MountedCargo>(true);
        for (int i = 0; i < cargoItems.Length; i++)
        {
            if (cargoItems[i]?.cargo is not VehicleDefinition definition
                || definition.unitPrefab == null
                || definition.unitPrefab.GetComponent<Unit>() is not GroundVehicle)
            {
                continue;
            }

            // The faction gate (2026-09-16). A cargo mount's vehicles belong to the aeroplane, not
            // to the faction, so before this both sides air-landed a character-for-character
            // identical load. The air-landed route is the one carrying the per-side extension
            // lists, because NOT ONE of these families appears in either faction's convoy groups.
            if (!CommanderFactionRoster.MayFieldVehicle(hq, definition, CommanderFieldingRoute.AirLanded))
            {
                continue;
            }

            CommanderPlatoonRole role = CommanderPlatoonRoles.Of(definition);
            if (role == CommanderPlatoonRole.Truck)
            {
                continue;
            }

            bool fromDepot = depotPrices.TryGetValue(definition.unitName, out float depotValue);
            float price = CommanderOperationsService.ResolveInsertionVehiclePrice(fromDepot, depotValue, definition.value);
            vehicles.Add(new InsertionVehicle(role, price, definition.unitName, fromDepot));
        }

        return vehicles.Count > 0;
    }

    /// <summary>Runs the operations side's pure chooser over one mount (or one loadable pair), and
    /// keeps the best complete combination it returns: a load with an air-defence vehicle beats one
    /// without at any price, then the cheaper total wins
    /// (<see cref="CommanderOperationsService.InsertionCombinationBeats"/>).</summary>
    private void TryInsertionCombination(
        CargoAircraftOption aircraft,
        Airbase airbase,
        VehicleMountCandidate first,
        VehicleMountCandidate? second,
        float budget,
        int wantedVehicles,
        bool preferHeavyHull,
        IReadOnlyList<CommanderPlatoonRole>? wantedRoles,
        ref CargoAircraftOption? bestAircraft,
        ref Airbase? bestAirbase,
        ref Loadout? bestLoadout,
        ref string bestLabel,
        ref float bestCargoValue,
        ref float bestTotal,
        ref float bestHull,
        ref bool bestHasAirDefence)
    {
        insertionRoles.Clear();
        insertionValues.Clear();
        insertionNames.Clear();
        insertionMounts.Clear();
        insertionSlots.Clear();
        AppendVehicleCandidate(first);
        if (second.HasValue)
        {
            AppendVehicleCandidate(second.Value);
        }

        CommanderOperationsService.PickInsertionCargo(
            insertionRoles, insertionValues, budget, wantedVehicles, insertionPicks, wantedRoles);
        if (insertionPicks.Count == 0)
        {
            return;
        }

        // Whatever the chooser loaded — one vehicle for a reinforcement, two for a fresh picket.
        float cargoValue = 0f;
        bool hasAirDefence = false;
        for (int i = 0; i < insertionPicks.Count; i++)
        {
            cargoValue += insertionValues[insertionPicks[i]];
            hasAirDefence |= insertionRoles[insertionPicks[i]] == CommanderPlatoonRole.AirDefence;
        }

        float hull = Mathf.Max(0f, aircraft.Definition.value);
        float total = hull + cargoValue;
        if (!CommanderOperationsService.InsertionCombinationBeats(
                hasAirDefence, hull, total, bestHasAirDefence, bestHull, bestTotal, preferHeavyHull))
        {
            return;
        }

        insertionChosenMounts.Clear();
        for (int i = 0; i < insertionPicks.Count; i++)
        {
            WeaponMount mount = insertionMounts[insertionPicks[i]];
            if (!insertionChosenMounts.Contains(mount))
            {
                insertionChosenMounts.Add(mount);
            }
        }

        Loadout loadout = CreateEmptyLoadout(aircraft.HardpointSets.Length);
        for (int i = 0; i < insertionPicks.Count; i++)
        {
            PlaceCargoAndClearNonCargo(
                loadout, aircraft.HardpointSets, insertionSlots[insertionPicks[i]], insertionMounts[insertionPicks[i]]);
        }

        string label = insertionNames[insertionPicks[0]];
        for (int i = 1; i < insertionPicks.Count; i++)
        {
            label += " + " + insertionNames[insertionPicks[i]];
        }

        bestAircraft = aircraft;
        bestAirbase = airbase;
        bestLoadout = loadout;
        bestLabel = label;
        bestCargoValue = cargoValue;
        bestHull = hull;
        bestTotal = total;
        bestHasAirDefence = hasAirDefence;
    }

    private void AppendVehicleCandidate(VehicleMountCandidate candidate)
    {
        for (int i = 0; i < candidate.Vehicles.Count; i++)
        {
            InsertionVehicle vehicle = candidate.Vehicles[i];
            insertionRoles.Add(vehicle.Role);
            insertionValues.Add(vehicle.Value);
            insertionNames.Add(vehicle.Name);
            insertionMounts.Add(candidate.Mount);
            insertionSlots.Add(candidate.Slot);
        }
    }

    /// <summary>
    /// Recalls an insertion flight whose point is no longer worth reinforcing — the
    /// <c>CancelSamSiteMissions</c> shape: stop overriding the target, mark cancelled (a vehicle
    /// that activates after this is destroyed), and fly home for the hull's refund. A
    /// queued-but-unspawned request for the same point dies with the flight, so a hangar that
    /// frees later cannot launch a transport nobody is waiting for.
    /// </summary>
    /// <param name="flightId">Which ONE flight of a lift to recall, or
    /// <see cref="CommanderCargoFlightSlot.Unslotted"/> to recall EVERY flight standing on the point
    /// (lift-wave_20260916). Zero is what every caller written before the lift wave passes, and it
    /// is the behaviour they have always had: a picket insertion carries one flight per point, and a
    /// lift order being closed outright wants all of its loads back. A non-zero id is the wave's own
    /// door — one load abandoned on a deck must not recall the two flying beside it.</param>
    internal void CancelInsertion(
        FactionHQ hq, CommanderStrategicPoint point, int flightId = CommanderCargoFlightSlot.Unslotted)
    {
        int queued = queuedCargoSpawns.Count;
        for (int i = 0; i < queued; i++)
        {
            QueuedCargoSpawn request = queuedCargoSpawns.Dequeue();
            if (request.InsertionPoint == null
                || !ReferenceEquals(request.Hq, hq)
                || !ReferenceEquals(request.InsertionPoint, point)
                || !CommanderCargoFlightSlot.Matches(request.InsertionFlightId, flightId))
            {
                queuedCargoSpawns.Enqueue(request);
            }
        }

        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            CargoMission mission = entry.Value;
            if (mission.InsertionPoint == null
                || !ReferenceEquals(mission.Hq, hq)
                || !ReferenceEquals(mission.InsertionPoint, point)
                || !CommanderCargoFlightSlot.Matches(mission.InsertionFlightId, flightId))
            {
                continue;
            }

            mission.TargetOverrideActive = false;
            mission.Cancelled = true;
            mission.CargoClearancePending = false;
            IssueInsertionReturnToBase(entry.Key, mission);
            if (entry.Key?.autopilot != null)
            {
                terrainClearanceAutopilots.Remove(entry.Key.autopilot);
                assignedAutopilotAircraft.Remove(entry.Key.autopilot);
            }

            if (entry.Key != null)
            {
                pendingTerrainAutopilotBindings.Remove(entry.Key);
            }
        }
    }

    /// <summary>
    /// Re-points every insertion for <paramref name="from"/> — queued, spawning or in the air — at
    /// <paramref name="to"/>, landing on <paramref name="target"/>, so a load already flying goes on
    /// to the new ground instead of home (user decision 2026-09-16: "re-routed rather than RTB").
    /// The mirror of <see cref="CancelInsertion"/>: the same three places are walked with the same
    /// per-HQ, per-point match. A live mission has its destination seeding and route cleared the way
    /// <c>TryConvertInsertionToAirdrop</c> does, so the next override cycle builds the approach to
    /// the new site from scratch; a picket insertion carries no planned route, so nothing is lost.
    /// True when anything answered to the old point.
    /// </summary>
    /// <param name="flightId">Which ONE flight to re-point, or
    /// <see cref="CommanderCargoFlightSlot.Unslotted"/> for every flight standing on
    /// <paramref name="from"/> (lift-wave_20260916). A lift wave re-points its loads ONE AT A TIME,
    /// each with its own landing spot: they all fly to the new zone, but three transports told to
    /// touch down on the same square metre is the defect the per-vehicle hold posts exist to
    /// prevent.</param>
    internal bool TryRedirectInsertion(
        FactionHQ hq,
        CommanderStrategicPoint from,
        CommanderStrategicPoint to,
        GlobalPosition target,
        int flightId = CommanderCargoFlightSlot.Unslotted)
    {
        bool redirected = false;
        foreach (QueuedCargoSpawn request in queuedCargoSpawns)
        {
            if (request.InsertionPoint == null
                || !ReferenceEquals(request.Hq, hq)
                || !ReferenceEquals(request.InsertionPoint, from)
                || !CommanderCargoFlightSlot.Matches(request.InsertionFlightId, flightId))
            {
                continue;
            }

            request.InsertionPoint = to;
            request.Targets.Clear();
            request.Targets.Add(target);
            redirected = true;
        }

        PendingAircraftSpawn? pending = pendingAircraftSpawn;
        if (pending?.InsertionPoint != null
            && ReferenceEquals(pending.Hq, hq)
            && ReferenceEquals(pending.InsertionPoint, from)
            && CommanderCargoFlightSlot.Matches(pending.InsertionFlightId, flightId))
        {
            pending.InsertionPoint = to;
            pending.Target = target;
            pending.Targets.Clear();
            pending.Targets.Add(target);
            redirected = true;
        }

        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            CargoMission mission = entry.Value;
            if (mission.InsertionPoint == null
                || mission.Cancelled
                || !ReferenceEquals(mission.Hq, hq)
                || !ReferenceEquals(mission.InsertionPoint, from)
                || !CommanderCargoFlightSlot.Matches(mission.InsertionFlightId, flightId)
                || entry.Key == null
                || entry.Key.disabled)
            {
                continue;
            }

            mission.InsertionPoint = to;
            mission.DeliveryTargets.Clear();
            mission.DeliveryTargets.Add(target);
            mission.Initialized = false;
            mission.LandingUnloadAt = 0f;
            mission.CargoClearancePending = false;
            mission.FoundationLandingSearchInitialized = false;
            mission.PlatformArrivalInitialized = false;
            mission.RoutePlanned = false;
            mission.ApproachRoute.Clear();
            mission.ApproachRouteIndex = 0;
            mission.RouteTransitActive = false;
            // The bypass's own arrival state goes with the rest of it (delivery-bypass_20260916).
            // A redirect is the mod moving this flight to different ground, so a load that had begun
            // unloading at the old site must not carry that site's latch — or its anchor — onto the
            // new one and put its remaining vehicles down where it is no longer going.
            mission.UnloadInPlace = false;
            mission.UnloadAnchor = null;
            mission.UnloadStartedAt = 0f;
            mission.UnloadPlacedCount = 0;
            mission.UnloadGroundFallbackLogged = false;
            redirected = true;
        }

        return redirected;
    }

    private void TryAssignPendingAircraft(FactionHQ hq, Unit unit)
    {
        PendingAircraftSpawn? pending = pendingAircraftSpawn;
        if (pending == null
            || !ReferenceEquals(hq, pending.Hq)
            || unit is not Aircraft aircraft
            || aircraft.Player != null
            || !ReferenceEquals(aircraft.definition, pending.Definition))
        {
            return;
        }

        // An insertion pending matches only the transport this request spawned: a hangar spawn
        // still on the deck. The air wing launches its own buys into the air at
        // LaunchAltitudeMeters, so a rotary airframe still that high up at registration is the
        // wing's (it takes the wing slot and the next registration takes the insertion), and an
        // airframe the wing has already claimed is never also an insertion transport — no
        // double-binding, no orphan, whichever of the two registration notifies runs first.
        if (pending.InsertionPoint != null
            && ((!pending.AirborneSpawn && aircraft.radarAlt >= InsertionDeckSpawnMaxMeters)
                || CommanderOperationsService.IsWingAirframe(hq, aircraft)))
        {
            return;
        }

        CargoMission mission = new(
            pending.Hq,
            pending.Target,
            pending.CargoLabel,
            pending.HighTerrainClearance,
            pending.TerrainClearanceMeters,
            pending.Airdrop,
            pending.PurchasedWithFunds,
            pending.PurchaseCost,
            CountDeployableCargo(aircraft),
            pending.Targets,
            pending.SupportSummary == SamSiteCargoSupportSummary
                || ParseCargoSiteId(pending.SupportSummary) >= 0,
            ParseCargoSiteId(pending.SupportSummary),
            ParseFoundationSiteId(pending.SupportSummary),
            ParseJacknifeSiteId(pending.SupportSummary),
            pending.OriginAirbase,
            pending.NavalTarget,
            pending.InsertionPoint);
        mission.InsertionFlightId = pending.InsertionFlightId;
        assignedMissions[aircraft] = mission;
        mission.AssignedAt = Time.time;
        if (pending.AirborneSpawn)
        {
            // Deferred to the next tick (fix, 2026-09-15): switching the pilot's state inside the
            // registration callback threw a null reference — the aircraft is not finished setting up
            // when it registers — and the exception aborted the assignment, so no airborne transport
            // ever bound (`Supply cargo run assignment timed out`, zero `insertion flight` lines).
            pendingAirborneHandoffs.Add(aircraft);
        }
        int cachedRouteSiteId = mission.DepositSiteId >= 0
            ? mission.DepositSiteId
            : mission.JacknifeSiteId;
        if (cachedRouteSiteId >= 0
            && CommanderSamSiteService.TryCopyCachedSupplyRoute(
                cachedRouteSiteId,
                pending.OriginAirbase,
                mission.ApproachRoute,
                out bool cachedSteepLanding))
        {
            mission.RoutePlanned = true;
            mission.SteepLanding = cachedSteepLanding;
        }
        else if (mission.FoundationSiteId >= 0)
        {
            mission.RoutePlanned = CommanderTerrainFlightPlanner.TryBuildRoute(
                aircraft.GlobalPosition(),
                mission.Target,
                Mathf.Max(60f, mission.TerrainClearanceMeters),
                mission.ApproachRoute,
                out bool steepLanding);
            mission.SteepLanding = steepLanding;
        }
        string missionLabel = pending.NavalTarget != null
            ? "Naval Supply"
            : pending.Airdrop
                ? $"Airdrop: {pending.CargoLabel}"
                : pending.InsertionPoint != null
                    ? $"Picket insertion: {pending.CargoLabel}"
                    : $"Cargo Delivery: {pending.CargoLabel}";
        // The pinned-units list is the local player's; an enemy commander's insertion transport is
        // intel, not a mission to show.
        if (ReferenceEquals(pending.Hq, CommanderGameAccess.GetLocalHq()))
        {
            CommanderSelectionService.PinMissionUnit(aircraft, "SUPPLY", missionLabel);
        }

        if (pending.InsertionPoint != null)
        {
            CommanderOperationsService.NotifyInsertionAircraft(
                pending.Hq, pending.InsertionPoint, pending.InsertionFlightId, aircraft);
            CommanderAiLog.Note(
                pending.Hq,
                $"insertion flight {CommanderGameAccess.GetUnitLabel(aircraft)} bound for {pending.InsertionPoint.Label}, "
                    + $"carrying {pending.CargoLabel}, from {GetAirbaseLabel(pending.OriginAirbase)}.");
        }
        if (pending.HighTerrainClearance && !TryBindTerrainAutopilot(aircraft, mission))
        {
            pendingTerrainAutopilotBindings.Add(aircraft);
        }
        pendingAircraftSpawn = null;
    }

    /// <summary>
    /// Puts an airborne-spawned transport into the game's transport state at once, with its cargo
    /// station selected — the hand-off the game's own combat state performs in
    /// <c>AssessHQTargets</c> once it notices cargo aboard (fix, 2026-09-15). A spawn over the base
    /// starts in the combat state, and that state lands an aircraft with no target within 3 km of
    /// home after 15 s, so 45 transports in one match came straight back down (`recovered VL-49
    /// Tarantula after 30 s in the air`, `lost a construction flight 32.5 km short of the point …
    /// at 0 m`). The transport state's target is ours from its first tick through
    /// <see cref="OverrideTransportTarget"/>.
    /// </summary>
    private static void HandAirborneTransportToCargoFlight(Aircraft aircraft)
    {
        if (aircraft == null || aircraft.disabled || aircraft.pilots == null || aircraft.NetworkHQ == null)
        {
            return;
        }

        TrySelectCargoStation(aircraft);
        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            // A pilot with no state yet has not run its own start-up; it is left to the game, which
            // hands a cargo-carrying helicopter to the transport state itself once it is flying.
            if (pilot == null || pilot.currentState == null || !CommanderAirCommandService.IsRotaryPilot(pilot))
            {
                continue;
            }

            try
            {
                pilot.AIHeloTransportState ??= new AIHeloTransportState(aircraft);
                if (pilot.currentState != pilot.AIHeloTransportState)
                {
                    pilot.SwitchState(pilot.AIHeloTransportState);
                }
            }
            catch (System.Exception error)
            {
                CommanderPlugin.Log.LogWarning(
                    $"Supply: could not hand {CommanderGameAccess.GetUnitLabel(aircraft)} to its cargo flight at once "
                        + $"({error.GetType().Name}); the game's own hand-off will do it.");
            }
        }
    }

    /// <summary>Airborne-spawned transports waiting one tick for their hand-off.</summary>
    private readonly List<Aircraft> pendingAirborneHandoffs = new();

    private void RunPendingAirborneHandoffs()
    {
        if (pendingAirborneHandoffs.Count == 0)
        {
            return;
        }

        for (int i = 0; i < pendingAirborneHandoffs.Count; i++)
        {
            HandAirborneTransportToCargoFlight(pendingAirborneHandoffs[i]);
        }

        pendingAirborneHandoffs.Clear();
    }

    private bool OverrideTransportTarget(AIHeloTransportState state)
    {
        Aircraft? aircraft = AircraftField?.GetValue(state) as Aircraft;
        if (aircraft == null
            || !assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || !mission.TargetOverrideActive)
        {
            return false;
        }

        // An airdropped insertion never touches down, so the landing branch's ramp-clear handshake
        // never runs and nothing else would ever send it home. The moment the last vehicle is out
        // from under the wing, the transport goes back for its hull refund — the same end the
        // landing branch reaches through DeployNextAssignedCargo (user report 2026-09-14).
        if (mission.Airdrop
            && mission.InsertionPoint != null
            && mission.DeliveryCompleted
            && !mission.ReturnIssued)
        {
            mission.ReturnIssued = true;
            mission.TargetOverrideActive = false;
            IssueInsertionReturnToBase(aircraft, mission);
            return true;
        }

        if (Mathf.Approximately(mission.LastTransportOverrideFixedTime, Time.fixedTime))
        {
            return true;
        }
        mission.LastTransportOverrideFixedTime = Time.fixedTime;

        // The delivery bypass (Supply/CommanderSupplyHeliUnload.cs): a transport that has arrived low
        // and roughly still unloads where it is instead of chasing the game's own touchdown gate.
        // It rides here, above the three-second landing-spot throttle below, because the release
        // cadence between vehicles needs a look every fixed frame.
        TickUnloadInPlace(state, aircraft, mission);

        float lastCheck = LastLandingSpotCheckField?.GetValue(state) is float value ? value : 0f;
        bool routeNeedsUpdate = mission.RouteTransitActive
            || mission.ApproachRouteIndex < mission.ApproachRoute.Count;
        if (Time.timeSinceLevelLoad - lastCheck < 3f && !routeNeedsUpdate)
        {
            return true;
        }

        if (mission.NavalTarget != null)
        {
            return OverrideNavalSupplyTarget(state, aircraft, mission);
        }

        if (!TrySelectCargoStation(aircraft))
        {
            if (PilotField?.GetValue(state) is Pilot unloadingPilot
                && unloadingPilot.flightInfo.LastCargoDelivery > 0f)
            {
                LastLandingSpotCheckField?.SetValue(state, Time.timeSinceLevelLoad);
                TimeWithoutMissionField?.SetValue(state, 0f);
                state.stateDisplayName = "Unloading cargo";
                return true;
            }

            CommanderPlugin.Log.LogWarning($"Assigned supply aircraft has no active cargo station: {CommanderGameAccess.GetUnitLabel(aircraft)}");
            return false;
        }

        LastLandingSpotCheckField?.SetValue(state, Time.timeSinceLevelLoad);
        TimeWithoutMissionField?.SetValue(state, 0f);
        TransportModeField?.SetValue(state, AIHeloTransportState.TransportMode.LandSuppy);
        state.stateDisplayName = $"Delivering {mission.CargoLabel}";

        if (PilotField?.GetValue(state) is Pilot pilot)
        {
            pilot.flightInfo.EnemyContact = true;
        }

        object? destination = TransportDestinationField?.GetValue(state);
        if (destination == null)
        {
            return false;
        }

        bool intermediateRouteTarget = TryGetApproachRouteTarget(aircraft, mission, out GlobalPosition assignedTarget);
        mission.RouteTransitActive = intermediateRouteTarget;
        AirdropField?.SetValue(state, mission.Airdrop);
        if (intermediateRouteTarget)
        {
            if (aircraft.autopilot is not AutopilotTiltwing)
            {
                assignedTarget = GetTurnAnticipationTarget(aircraft, mission, assignedTarget);
                GlobalPosition aircraftPosition = aircraft.GlobalPosition();
                Vector3 routeDirection = assignedTarget - aircraftPosition;
                routeDirection.y = 0f;
                if (routeDirection.sqrMagnitude > 1f)
                {
                    routeDirection.Normalize();
                    assignedTarget = new GlobalPosition(
                        aircraftPosition.x + routeDirection.x * 10000f,
                        assignedTarget.y,
                        aircraftPosition.z + routeDirection.z * 10000f);
                }
            }
        }
        else
        {
            assignedTarget = mission.Target;
        }
        bool foundationLandingSearch = !intermediateRouteTarget && mission.FoundationSiteId >= 0;
        if (foundationLandingSearch)
        {
            if (!mission.FoundationLandingSearchInitialized)
            {
                Vector3 approach = aircraft.GlobalPosition() - mission.Target;
                approach.y = 0f;
                if (approach.sqrMagnitude < 1f)
                {
                    approach = -aircraft.transform.forward;
                    approach.y = 0f;
                }
                approach.Normalize();
                assignedTarget = mission.Target + approach * 120f;
                DestinationLzField?.SetValue(destination, assignedTarget);
                DestinationTouchdownField?.SetValue(destination, assignedTarget);
                DestinationSlopeField?.SetValue(destination, 90f);
                DestinationAttemptsField?.SetValue(destination, 0);
                mission.FoundationLandingSearchInitialized = true;
            }
            UpdateTouchdownPointMethod?.Invoke(destination, new object[] { 300f, aircraft });
            if (DestinationTouchdownField?.GetValue(destination) is GlobalPosition foundationTouchdown)
            {
                assignedTarget = foundationTouchdown;
            }
        }
        bool elevatedPlatformApproach = !intermediateRouteTarget
            && mission.FoundationSiteId < 0
            && (mission.DepositSiteId >= 0 || mission.JacknifeSiteId >= 0);
        if (elevatedPlatformApproach && !mission.PlatformArrivalInitialized)
        {
            Vector3 awayFromPlatform = aircraft.GlobalPosition() - mission.Target;
            awayFromPlatform.y = 0f;
            if (awayFromPlatform.sqrMagnitude < 1f)
            {
                awayFromPlatform = -aircraft.transform.forward;
                awayFromPlatform.y = 0f;
            }
            awayFromPlatform.Normalize();
            float arrivalDistance = Mathf.Max(150f, aircraft.maxRadius * 5f);
            const float arrivalClearance = 20f;
            mission.PlatformArrivalPoint = new GlobalPosition(
                mission.Target.x + awayFromPlatform.x * arrivalDistance,
                mission.Target.y + arrivalClearance,
                mission.Target.z + awayFromPlatform.z * arrivalDistance);
            mission.PlatformArrivalInitialized = true;
        }
        if (elevatedPlatformApproach && !mission.PlatformArrivalReached)
        {
            float arrivalDistance = CommanderGameAccess.HorizontalDistance(
                aircraft.transform.position,
                mission.PlatformArrivalPoint.ToLocalPosition());
            float heightAboveDeck = aircraft.GlobalPosition().y - mission.Target.y;
            if (arrivalDistance <= 50f && heightAboveDeck >= 10f && aircraft.speed <= 25f)
            {
                mission.PlatformArrivalReached = true;
            }
            else
            {
                assignedTarget = mission.PlatformArrivalPoint;
            }
        }
        if (elevatedPlatformApproach
            && mission.PlatformArrivalReached
            && !mission.PlatformApproachComplete)
        {
            const float deckClearance = 20f;
            float horizontalDistance = CommanderGameAccess.HorizontalDistance(
                aircraft.transform.position,
                mission.Target.ToLocalPosition());
            float heightAboveDeck = aircraft.GlobalPosition().y - mission.Target.y;
            if (horizontalDistance <= 30f
                && heightAboveDeck >= deckClearance * 0.6f
                && aircraft.speed <= 20f)
            {
                mission.PlatformApproachComplete = true;
            }
            else
            {
                assignedTarget = new GlobalPosition(
                    mission.Target.x,
                    mission.Target.y + deckClearance,
                    mission.Target.z);
            }
        }

        DestinationValidMissionField?.SetValue(destination, true);
        DestinationDropConditionsField?.SetValue(destination, false);
        DestinationEnemyPositionField?.SetValue(destination, mission.Target);
        if (!foundationLandingSearch)
        {
            DestinationLzField?.SetValue(destination, assignedTarget);
        }

        if (!mission.Initialized)
        {
            DestinationTouchdownField?.SetValue(destination, assignedTarget);
            DestinationSlopeField?.SetValue(destination, 90f);
            DestinationAttemptsField?.SetValue(destination, 0);
            mission.Initialized = true;
        }

        if (intermediateRouteTarget || elevatedPlatformApproach)
        {
            DestinationTouchdownField?.SetValue(destination, assignedTarget);
            DestinationSlopeField?.SetValue(destination, 0f);
        }
        else if (mission.Airdrop && mission.InsertionPoint != null)
        {
            // A drop run wants the point itself under the wing, not the flattest ground within
            // 150 m of it: the game's touchdown search exists to find somewhere to LAND, and running
            // it here would walk the release point off the posts the vehicles are meant to hold.
            // Left alone for the player's own airdrops, whose target is a spot the player clicked.
            DestinationTouchdownField?.SetValue(destination, assignedTarget);
            DestinationSlopeField?.SetValue(destination, 0f);
        }
        else if (!foundationLandingSearch)
        {
            UpdateTouchdownPointMethod?.Invoke(destination, new object[] { 150f, aircraft });
        }
        TransportDestinationField?.SetValue(state, destination);
        StateDestinationField?.SetValue(state, assignedTarget);
        return true;
    }

    private void EndTargetOverrideForState(AIHeloTransportState state)
    {
        if (AircraftField?.GetValue(state) is Aircraft aircraft
            && assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            && mission.TargetOverrideActive)
        {
            mission.TargetOverrideActive = false;
        }
    }

    private bool ShouldDelayAssignedCargoTakeoff(Pilot pilot, PilotBaseState requestedState)
    {
        if (pilot.aircraft != null
            && assignedMissions.TryGetValue(pilot.aircraft, out CargoMission assignedMission)
            && assignedMission.Cancelled)
        {
            return false;
        }

        if (pilot.aircraft != null
            && assignedMissions.ContainsKey(pilot.aircraft)
            && pilot.currentState == pilot.AIHeloTakeoffState
            && requestedState != pilot.currentState
            && pilot.aircraft.radarAlt < 30f)
        {
            return true;
        }

        if (requestedState == pilot.currentState
            || pilot.currentState != pilot.AIHeloTransportState
            || pilot.aircraft == null
            || !assignedMissions.TryGetValue(pilot.aircraft, out CargoMission mission)
            || !mission.TargetOverrideActive)
        {
            return false;
        }

        // Do not block the initial departure from the airfield. After the first
        // release, keep an airdrop in transport state until every cargo station
        // has fired; otherwise Basegame leaves the state after only one load.
        if (mission.Airdrop)
        {
            // An insertion's drop run is also held past the last release until the return flight has
            // been issued: the game hands an emptied transport straight to its combat state, which
            // would take the airframe before the override above could send it home.
            return mission.ReleasedCargoCount > 0
                && (HasDeployableCargo(pilot.aircraft)
                    || (mission.InsertionPoint != null && !mission.ReturnIssued));
        }
        // A bypassed insertion is held past its last release until the return flight has been
        // issued, exactly as the drop run above is: the game hands an emptied transport straight to
        // its combat state, which would take the airframe before the override could send it home.
        return mission.CargoClearancePending
            || (mission.UnloadInPlace && mission.InsertionPoint != null && !mission.ReturnIssued)
            || (mission.LastCargoReleasedAt > 0f
                && Time.timeSinceLevelLoad - mission.LastCargoReleasedAt < 8f);
    }

    private void HoldDeployedCargo(Aircraft aircraft, Unit cargoUnit)
    {
        if (!assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            return;
        }

        if (mission.Cancelled)
        {
            DestroyCargoUnit(cargoUnit);
            return;
        }

        // Under the insertion shield from this moment until it has settled (Supply/CommanderSupplyHeliShield.cs).
        // A bypassed load is also told WHERE to go: the shield sweep sets it down on clear ground
        // near the transport on its next tick (delivery-bypass_20260916, design section 4.3).
        GlobalPosition? placeAt = null;
        if (mission.UnloadInPlace
            && cargoUnit is GroundVehicle
            && TryChooseUnloadGround(aircraft, mission, mission.UnloadPlacedCount, out GlobalPosition unloadGround))
        {
            placeAt = unloadGround;
            mission.UnloadPlacedCount++;
        }

        ShieldDeliveredCargo(cargoUnit, mission, placeAt);

        mission.ActivatedCargoCount++;
        if (IsSamLogisticsMission(mission)
            && !IsJacknifeUnit(cargoUnit))
        {
            float supply = GetCargoSupply(cargoUnit);
            if (mission.FoundationSiteId >= 0)
            {
                CommanderSamSiteService.NotifyFoundationAmmunitionDelivered(
                    mission.FoundationSiteId,
                    supply);
            }
            else if (mission.DepositSiteId >= 0)
            {
                CommanderSamSiteService.TryDepositAmmunitionAmount(
                    mission.DepositSiteId,
                    supply,
                    out _);
            }

            DestroyCargoUnit(cargoUnit);
            UpdateDeliveryCompleted(aircraft, mission);
            return;
        }

        if (mission.DepositAtSamCore && pendingSamCargoDeposits.Add(cargoUnit))
        {
            CommanderPlugin.Instance?.StartCoroutine(
                DepositSamCargoWhenStationary(cargoUnit, mission.DepositSiteId));
        }
        bool cargoStillOnAircraft = HasDeployableCargo(aircraft);
        if (cargoUnit is GroundVehicle groundVehicle)
        {
            if ((mission.FoundationSiteId >= 0 || mission.JacknifeSiteId >= 0)
                && IsJacknifeUnit(groundVehicle)
                && !mission.Airdrop)
            {
                int siteId = mission.FoundationSiteId >= 0
                    ? mission.FoundationSiteId
                    : mission.JacknifeSiteId;
                groundVehicle.SetHoldPosition(true);
                groundVehicle.UnitCommand?.SetDestination(
                    groundVehicle.GlobalPosition(),
                    playerCommand: false);
                CommanderSamSiteService.ReserveDeliveredJacknife(siteId, groundVehicle);
                mission.CargoClearancePending = true;
                CommanderPlugin.Instance?.StartCoroutine(
                    ReleaseSamJacknifeAfterUnloadDelay(
                        aircraft,
                        groundVehicle,
                        mission));
                return;
            }

            // The ramp-clear handshake is the one part of a landing delivery the bypass genuinely
            // breaks: it exists to drive the first vehicle clear before the second is released, and
            // there is no ramp on the ground to clear when the transport never touched down. A
            // bypassed load therefore takes the same branch an airdrop takes, and the spacing is the
            // release cadence's instead (design section 4.5).
            if (cargoStillOnAircraft && !mission.Airdrop && !mission.UnloadInPlace)
            {
                mission.CargoClearancePending = true;
                CommanderPlugin.Instance?.StartCoroutine(ClearGroundVehicleFromRamp(aircraft, groundVehicle, mission));
            }
            else
            {
                groundVehicle.SetHoldPosition(true);
                groundVehicle.UnitCommand?.SetDestination(groundVehicle.GlobalPosition(), playerCommand: false);
                NotifyFoundationCargoActivated(mission, cargoUnit);
                if (mission.InsertionPoint != null)
                {
                    CommanderOperationsService.NotifyPicketVehicleDelivered(
                        mission.Hq, mission.InsertionPoint, mission.InsertionFlightId, cargoUnit);
                }
            }
        }
        else if (cargoStillOnAircraft
            && !mission.Airdrop
            && !mission.UnloadInPlace
            && !mission.CargoClearancePending)
        {
            mission.CargoClearancePending = true;
            CommanderPlugin.Instance?.StartCoroutine(ClearStaticCargoFromRamp(aircraft, cargoUnit, mission));
        }
        else
        {
            NotifyFoundationCargoActivated(mission, cargoUnit);
        }
        UpdateDeliveryCompleted(aircraft, mission);
    }

    private static IEnumerator ReleaseSamJacknifeAfterUnloadDelay(
        Aircraft aircraft,
        GroundVehicle vehicle,
        CargoMission mission)
    {
        float releaseAt = Time.timeSinceLevelLoad + 10f;
        while (vehicle != null
            && !vehicle.disabled
            && Time.timeSinceLevelLoad < releaseAt)
        {
            vehicle.SetHoldPosition(true);
            yield return new WaitForSeconds(0.25f);
        }

        if (vehicle == null || vehicle.disabled)
        {
            mission.CargoClearancePending = false;
            yield break;
        }

        NotifyFoundationCargoActivated(mission, vehicle);

        float clearance = aircraft != null
            ? Mathf.Max(12f, aircraft.maxRadius + vehicle.maxRadius + 2f)
            : 12f;
        float timeout = Time.timeSinceLevelLoad + 10f;
        while (aircraft != null
            && vehicle != null
            && !vehicle.disabled
            && Time.timeSinceLevelLoad < timeout
            && CommanderGameAccess.HorizontalDistance(
                aircraft.transform.position,
                vehicle.transform.position) < clearance)
        {
            yield return new WaitForSeconds(0.25f);
        }

        mission.CargoClearancePending = false;
    }

    private static void NotifyFoundationCargoActivated(CargoMission mission, Unit? cargo)
    {
        if (cargo == null || cargo.disabled)
        {
            return;
        }

        if (mission.FoundationSiteId >= 0)
        {
            CommanderSamSiteService.NotifyFoundationCargoActivated(
                mission.FoundationSiteId,
                cargo);
        }
        else if (mission.JacknifeSiteId >= 0)
        {
            CommanderSamSiteService.NotifySiteJacknifeActivated(
                mission.JacknifeSiteId,
                cargo);
        }
    }

    private IEnumerator DepositSamCargoWhenStationary(Unit cargo, int siteId)
    {
        float removeAt = Time.timeSinceLevelLoad + 45f;
        yield return new WaitForSeconds(3f);
        float stableSince = -1f;
        float timeout = Time.timeSinceLevelLoad + 60f;
        while (cargo != null && !cargo.disabled && Time.timeSinceLevelLoad < timeout)
        {
            Rigidbody? body = cargo.rb;
            bool stationary = body == null
                || (body.velocity.sqrMagnitude < 0.25f && body.angularVelocity.sqrMagnitude < 0.25f);
            if (stationary)
            {
                if (stableSince < 0f)
                {
                    stableSince = Time.timeSinceLevelLoad;
                }
                else if (Time.timeSinceLevelLoad - stableSince >= 2f)
                {
                    break;
                }
            }
            else
            {
                stableSince = -1f;
            }

            yield return new WaitForSeconds(0.25f);
        }

        pendingSamCargoDeposits.Remove(cargo!);
        if (cargo == null || cargo.disabled)
        {
            yield break;
        }
        if (!CommanderSamSiteService.TryDepositAmmunition(
                siteId,
                cargo,
                out float transferred))
        {
            DestroyCargoUnit(cargo);
            yield break;
        }

        CommanderPlugin.Log.LogInfo(
            $"SAM cargo deposited: cargo={CommanderGameAccess.GetUnitLabel(cargo)}, ammunition={transferred:0.0}.");
        while (cargo != null && !cargo.disabled && Time.timeSinceLevelLoad < removeAt)
        {
            yield return new WaitForSeconds(0.5f);
        }
        if (cargo != null
            && NetworkManagerNuclearOption.i?.ServerObjectManager != null
            && cargo.Identity != null)
        {
            NetworkManagerNuclearOption.i.ServerObjectManager.Destroy(
                cargo.Identity,
                !cargo.Identity.IsSceneObject);
        }
    }

    private bool DeployNextAssignedCargo(AIHeloTransportState state)
    {
        Aircraft? aircraft = AircraftField?.GetValue(state) as Aircraft;
        if (aircraft == null || !assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            return false;
        }

        if (mission.Cancelled)
        {
            return true;
        }

        if (mission.RouteTransitActive)
        {
            return true;
        }

        HoldLandingTimerWhileCargoProcesses(state, aircraft, mission);

        if (!mission.Airdrop && IsSamLogisticsMission(mission))
        {
            bool unloading = HasDeployableCargo(aircraft)
                || mission.ReleasedCargoCount < mission.ExpectedCargoLoads
                || mission.ReleasedCargoCount > mission.ActivatedCargoCount
                || mission.CargoClearancePending;
            if (unloading)
            {
                OpenCargoDoors(aircraft, mission);
                if (mission.LandingUnloadAt <= 0f)
                {
                    mission.LandingUnloadAt = Time.timeSinceLevelLoad + 30f;
                }
                if (Time.timeSinceLevelLoad < mission.LandingUnloadAt)
                {
                    TouchedDownTimeField?.SetValue(state, 0f);
                    return true;
                }
            }
            else if (!CloseCargoDoors(mission))
            {
                TouchedDownTimeField?.SetValue(state, 0f);
                return true;
            }
        }

        if (Time.timeSinceLevelLoad < mission.NextCargoReleaseAt)
        {
            return true;
        }

        if (!mission.Airdrop
            && (mission.ReleasedCargoCount > mission.ActivatedCargoCount || mission.CargoClearancePending))
        {
            return true;
        }

        if (!TrySelectNextCargoWeapon(aircraft, out WeaponStation station, out Weapon cargoWeapon))
        {
            if (!mission.Airdrop && IsSamLogisticsMission(mission))
            {
                mission.DeliveryCompleted = mission.ActivatedCargoCount >= mission.ExpectedCargoLoads;
                mission.VerticalDepartureActive = true;
            }
            else if (!mission.Airdrop
                && mission.InsertionPoint != null
                && mission.DeliveryCompleted
                && !mission.ReturnIssued)
            {
                // Both vehicles are out and the ramp is clear: the flight is over, and the
                // transport goes home for recovery (and its hull refund) rather than orbiting the
                // point in the combat state until the Basegame's own no-target logic remembers it
                // has a base. DeliveryCompleted is what proves the ramp is clear — the hold
                // below keeps the landing timer at zero until then.
                mission.ReturnIssued = true;
                IssueInsertionReturnToBase(aircraft, mission);
            }

            return true;
        }

        Pilot? pilot = PilotField?.GetValue(state) as Pilot;
        if (pilot == null)
        {
            return false;
        }

        if (!mission.Airdrop
            && IsSamLogisticsMission(mission)
            && cargoWeapon is MountedCargo mountedCargo
            && !IsJacknifeCargoDefinition(mountedCargo.cargo)
            && TryConsumeVirtualSamCargo(aircraft, station, mountedCargo, mission, pilot))
        {
            return true;
        }

        aircraft.weaponManager.currentWeaponStation = station;
        cargoWeapon.Fire(aircraft, null!, aircraft.rb.velocity, station, default);
        station.UpdateLastFired(1);
        mission.ReleasedCargoCount++;
        mission.LastCargoReleasedAt = Time.timeSinceLevelLoad;
        pilot.flightInfo.LastCargoDelivery = Time.timeSinceLevelLoad;
        pilot.flightInfo.EnemyContact = true;
        mission.NextCargoReleaseAt = Time.timeSinceLevelLoad + (mission.Airdrop ? 1.5f : 2.5f);
        return true;
    }

    private static void HoldLandingTimerWhileCargoProcesses(
        AIHeloTransportState state,
        Aircraft aircraft,
        CargoMission mission)
    {
        if (mission.Airdrop)
        {
            return;
        }

        bool waitingForReleasedCargo = mission.ActivatedCargoCount < mission.ReleasedCargoCount;
        bool moreCargoAtThisLz = HasDeployableCargo(aircraft)
            || mission.ReleasedCargoCount < mission.ExpectedCargoLoads;
        if (waitingForReleasedCargo || moreCargoAtThisLz || mission.CargoClearancePending)
        {
            TouchedDownTimeField?.SetValue(state, 0f);
        }
    }

    private static IEnumerator ClearGroundVehicleFromRamp(
        Aircraft aircraft,
        GroundVehicle vehicle,
        CargoMission mission)
    {
        float earliestMoveAt = mission.LastCargoReleasedAt + 3f;
        while (vehicle != null && aircraft != null && Time.timeSinceLevelLoad < earliestMoveAt)
        {
            yield return new WaitForSeconds(0.1f);
        }
        if (vehicle == null || aircraft == null
            || !TryFindCargoClearanceDirection(aircraft, vehicle, 10f, out Vector3 direction))
        {
            if (vehicle != null)
            {
                vehicle.SetHoldPosition(true);
                NotifyFoundationCargoActivated(mission, vehicle);
            }
            mission.CargoClearancePending = false;
            yield break;
        }

        GlobalPosition clearPoint = (vehicle.transform.position + direction * 6f).ToGlobalPosition();
        vehicle.SetHoldPosition(false);
        vehicle.UnitCommand?.SetDestination(clearPoint, playerCommand: true);
        float timeout = Time.timeSinceLevelLoad + 10f;
        while (vehicle != null
            && !vehicle.disabled
            && Time.timeSinceLevelLoad < timeout
            && CommanderGameAccess.HorizontalDistance(vehicle.transform.position, clearPoint.ToLocalPosition()) > 1.25f)
        {
            yield return new WaitForSeconds(0.25f);
        }

        if (vehicle != null && !vehicle.disabled)
        {
            vehicle.SetHoldPosition(true);
            vehicle.UnitCommand?.SetDestination(vehicle.GlobalPosition(), playerCommand: false);
            NotifyFoundationCargoActivated(mission, vehicle);
            if (mission.InsertionPoint != null)
            {
                // Same as the no-clearance branch in HoldDeployedCargo: once the vehicle has
                // settled off the ramp, it belongs to the picket it was bought for.
                CommanderOperationsService.NotifyPicketVehicleDelivered(
                    mission.Hq, mission.InsertionPoint, mission.InsertionFlightId, vehicle);
            }
        }
        mission.CargoClearancePending = false;
    }

    private static IEnumerator ClearStaticCargoFromRamp(Aircraft aircraft, Unit cargo, CargoMission mission)
    {
        float earliestMoveAt = mission.LastCargoReleasedAt + 3f;
        while (cargo != null && aircraft != null && Time.timeSinceLevelLoad < earliestMoveAt)
        {
            yield return new WaitForSeconds(0.1f);
        }

        if (cargo == null || aircraft == null)
        {
            mission.CargoClearancePending = false;
            yield break;
        }

        if (!TryFindCargoClearanceDirection(aircraft, cargo, 10f, out Vector3 direction))
        {
            CommanderPlugin.Log.LogWarning(
                $"Supply cargo could not find 10m ramp clearance: {CommanderGameAccess.GetUnitLabel(cargo)}");
            NotifyFoundationCargoActivated(mission, cargo);
            mission.CargoClearancePending = false;
            yield break;
        }

        Vector3 start = cargo.transform.position;
        Vector3 target = start + direction.normalized * 6f;
        const float moveDuration = 5f;
        float startedAt = Time.timeSinceLevelLoad;
        while (cargo != null && Time.timeSinceLevelLoad - startedAt < moveDuration)
        {
            float t = Mathf.SmoothStep(0f, 1f, (Time.timeSinceLevelLoad - startedAt) / moveDuration);
            Vector3 position = Vector3.Lerp(start, target, t);
            if (cargo.rb != null)
            {
                cargo.rb.MovePosition(position);
                cargo.rb.velocity = Vector3.zero;
                cargo.rb.angularVelocity = Vector3.zero;
            }
            else
            {
                cargo.transform.position = position;
            }
            yield return new WaitForFixedUpdate();
        }
        NotifyFoundationCargoActivated(mission, cargo);
        mission.CargoClearancePending = false;
    }

    private static bool TryFindCargoClearanceDirection(
        Aircraft aircraft,
        Unit cargo,
        float scanDistance,
        out Vector3 direction)
    {
        Vector3 rear = -aircraft.transform.forward;
        rear.y = 0f;
        rear = rear.sqrMagnitude > 0.01f ? rear.normalized : Vector3.back;
        Vector3[] candidates =
        {
            Quaternion.AngleAxis(-70f, Vector3.up) * rear,
            Quaternion.AngleAxis(70f, Vector3.up) * rear,
            rear,
        };
        Vector3 origin = cargo.transform.position + Vector3.up * Mathf.Max(cargo.maxRadius * 0.25f, 0.5f);
        for (int i = 0; i < candidates.Length; i++)
        {
            Vector3 candidate = candidates[i].normalized;
            if (!Physics.Raycast(origin, candidate, scanDistance, 2112, QueryTriggerInteraction.Ignore))
            {
                direction = candidate;
                return true;
            }
        }

        direction = Vector3.zero;
        return false;
    }

    private void HandleAircraftReturned(Aircraft aircraft)
    {
        if (!assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || mission.PurchaseRefunded
            || mission.Hq == null)
        {
            return;
        }

        if (mission.InsertionPoint != null && TransportAbandonedBeforeTakeoff(aircraft))
        {
            // The overnight log of 2026-09-15: every one of the enemy's 99 construction and picket
            // transports came back to inventory 30-40 s after spawning and not one delivered. The
            // game's own take-off state ejects a helicopter crew that has not reached 20 m within
            // 30 s, or that takes any damage before lifting, and an ejected crew at an airbase
            // returns the hull to inventory. To the sweeps that looked like "never registered" (FOB)
            // and "lost" (picket) — a cooldown on the point and a 15 min pause on every flight for a
            // failure that was the DECK's. It is named for what it is, the base is closed to
            // transports for a while so the next launch tries another one, and the operations side
            // is told it was a launch failure rather than a loss.
            mission.AbandonedOnDeck = true;
            float seconds = Time.time - mission.AssignedAt;
            string baseLabel = mission.OriginAirbase != null ? GetAirbaseLabel(mission.OriginAirbase) : "an unknown base";
            string state = aircraft.pilots != null && aircraft.pilots.Length > 0 && aircraft.pilots[0]?.currentState != null
                ? aircraft.pilots[0].currentState.stateDisplayName
                : "no pilot state";
            float damage = aircraft.partDamageTracker != null ? aircraft.partDamageTracker.GetDetachedRatio() : 0f;
            BlockTransportLaunches(mission.OriginAirbase);
            CommanderAiLog.Note(
                mission.Hq,
                $"transport {CommanderGameAccess.GetUnitLabel(aircraft)} abandoned on the deck at {baseLabel} {seconds:0} s after spawn: "
                    + $"the crew ejected before take-off (parts detached {damage:P0}, radar altitude {aircraft.radarAlt:0} m, state \"{state}\"). "
                    + $"Transports from there spawn airborne for the next {TransportLaunchBlockMinutes:0} min"
                    + (mission.PurchasedWithFunds ? "; hull refunded." : "."));
            CommanderOperationsService.NoteInsertionLaunchFailed(
                mission.Hq, mission.InsertionPoint, mission.InsertionFlightId, baseLabel);
        }
        else if (mission.InsertionPoint != null)
        {
            // The insertion's own recovery line; the hull refund half only when a hull was
            // actually charged (a transport taken out of stock costs nothing and refunds nothing).
            CommanderAiLog.Note(
                mission.Hq,
                mission.PurchasedWithFunds
                    ? "transport recovered, hull refunded."
                    : "transport recovered.");
        }

        if (!mission.PurchasedWithFunds)
        {
            return;
        }

        mission.Hq.ModifyUnitSupply(aircraft.definition, -1);
        mission.Hq.AddFunds(mission.PurchaseCost);
        mission.PurchaseRefunded = true;
    }

    /// <summary>
    /// Whether a transport back in inventory never flew: no pilot's <c>HasTakenOff</c> is set. The
    /// game sets that flag when the take-off state is left for the combat state, so a crew that
    /// ejected on the deck — the take-off state's 30 s rule, or damage before lift-off — leaves it
    /// clear. An aircraft with no pilots is not judged.
    /// </summary>
    private static bool TransportAbandonedBeforeTakeoff(Aircraft aircraft)
    {
        if (aircraft.pilots == null || aircraft.pilots.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot != null && pilot.flightInfo != null && pilot.flightInfo.HasTakenOff)
            {
                return false;
            }
        }

        return true;
    }

    private bool SuppressEjectionAtAssignedSamSite(AIHeloTransportState state)
    {
        return AircraftField?.GetValue(state) is Aircraft aircraft
            && assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            // An insertion's landing zone is an ordinary control point 20+ km from any airbase, and
            // the game's ejection check abandons a stationary transport beyond 200 m of its
            // touchdown point — without this the crew would eject while the vehicles roll out.
            && (IsSamLogisticsMission(mission) || mission.InsertionPoint != null)
            && !mission.Airdrop
            && !mission.RouteTransitActive
            && aircraft.radarAlt < 15f
            && FastMath.InRange(aircraft.GlobalPosition(), mission.Target, 500f);
    }

    private bool OverrideAssignedReturnAirbase(AIHeloLandingState state)
    {
        Aircraft? aircraft = AircraftField?.GetValue(state) as Aircraft;
        if (aircraft == null
            || !assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || mission.OriginAirbase == null
            || mission.OriginAirbase.disabled
            || mission.OriginAirbase.CurrentHQ != mission.Hq)
        {
            return false;
        }

        RunwayQuery query = new()
        {
            RunwayType = RunwayQueryType.Vertical,
            MinSize = aircraft.maxRadius
        };
        if (!mission.OriginAirbase.TryRequestVerticalLanding(
            aircraft,
            query,
            out Airbase.VerticalLandingPoint landingPoint))
        {
            return false;
        }

        StateNearestAirbaseField?.SetValue(state, mission.OriginAirbase);
        LandingStatePointField?.SetValue(state, landingPoint);
        LandingStateReachedApproachField?.SetValue(state, false);
        StateDestinationField?.SetValue(state, landingPoint.GetApproachPoint(aircraft));
        return true;
    }

    private static bool TryGetApproachRouteTarget(
        Aircraft aircraft,
        CargoMission mission,
        out GlobalPosition target)
    {
        const float waypointReachDistance = 400f;
        target = default;
        while (mission.ApproachRouteIndex < mission.ApproachRoute.Count)
        {
            GlobalPosition waypoint = mission.ApproachRoute[mission.ApproachRouteIndex];
            Vector3 previous = GetPreviousRoutePoint(mission).ToLocalPosition();
            Vector3 waypointPosition = waypoint.ToLocalPosition();
            if (CommanderGameAccess.HorizontalDistance(aircraft.transform.position, waypointPosition) > waypointReachDistance
                && !HasPassedRoutePoint(aircraft.transform.position, previous, waypointPosition))
            {
                break;
            }
            mission.ApproachRouteIndex++;
        }
        if (mission.ApproachRouteIndex < mission.ApproachRoute.Count)
        {
            target = mission.ApproachRoute[mission.ApproachRouteIndex];
            return true;
        }
        return false;
    }

    private static GlobalPosition GetTurnAnticipationTarget(
        Aircraft aircraft,
        CargoMission mission,
        GlobalPosition currentWaypoint)
    {
        int nextIndex = mission.ApproachRouteIndex + 1;
        if (nextIndex >= mission.ApproachRoute.Count)
        {
            return currentWaypoint;
        }

        float distance = CommanderGameAccess.HorizontalDistance(
            aircraft.transform.position,
            currentWaypoint.ToLocalPosition());
        float turnLead = Mathf.Clamp(aircraft.speed * 8f, 500f, 1600f);
        if (distance >= turnLead)
        {
            return currentWaypoint;
        }

        float blend = Mathf.InverseLerp(turnLead, 0f, distance) * 0.75f;
        GlobalPosition nextWaypoint = mission.ApproachRoute[nextIndex];
        return new GlobalPosition(
            Mathf.Lerp((float)currentWaypoint.x, (float)nextWaypoint.x, blend),
            Mathf.Lerp((float)currentWaypoint.y, (float)nextWaypoint.y, blend),
            Mathf.Lerp((float)currentWaypoint.z, (float)nextWaypoint.z, blend));
    }

    private static GlobalPosition GetPreviousRoutePoint(CargoMission mission)
    {
        if (mission.ApproachRouteIndex > 0)
        {
            return mission.ApproachRoute[mission.ApproachRouteIndex - 1];
        }

        Transform origin = mission.OriginAirbase.center != null
            ? mission.OriginAirbase.center
            : mission.OriginAirbase.transform;
        return origin.GlobalPosition();
    }

    private static bool HasPassedRoutePoint(Vector3 aircraft, Vector3 previous, Vector3 waypoint)
    {
        Vector3 segment = waypoint - previous;
        Vector3 beyondWaypoint = aircraft - waypoint;
        segment.y = 0f;
        beyondWaypoint.y = 0f;
        return segment.sqrMagnitude > 1f && Vector3.Dot(segment, beyondWaypoint) >= 0f;
    }

    private static bool TryConsumeVirtualSamCargo(
        Aircraft aircraft,
        WeaponStation station,
        MountedCargo cargo,
        CargoMission mission,
        Pilot pilot)
    {
        if (MountedCargoRemoveMethod == null)
        {
            return false;
        }

        float supply = GetCargoSupply(cargo.cargo);
        if (supply <= 0f)
        {
            return false;
        }

        if (mission.FoundationSiteId >= 0)
        {
            CommanderSamSiteService.NotifyFoundationAmmunitionDelivered(
                mission.FoundationSiteId,
                supply);
        }
        else if (mission.DepositSiteId >= 0)
        {
            CommanderSamSiteService.TryDepositAmmunitionAmount(
                mission.DepositSiteId,
                supply,
                out _);
        }

        aircraft.weaponManager.currentWeaponStation = station;
        station.LaunchMount(aircraft, null!, default);
        MountedCargoRemoveMethod.Invoke(cargo, null);
        UnityEngine.Object.Destroy(cargo.gameObject);
        mission.ReleasedCargoCount++;
        mission.ActivatedCargoCount++;
        mission.LastCargoReleasedAt = Time.timeSinceLevelLoad;
        mission.NextCargoReleaseAt = Time.timeSinceLevelLoad + 1f;
        pilot.flightInfo.LastCargoDelivery = Time.timeSinceLevelLoad;
        pilot.flightInfo.EnemyContact = true;
        UpdateDeliveryCompleted(aircraft, mission);
        return true;
    }

    private static bool IsJacknifeCargoDefinition(UnitDefinition? definition)
    {
        if (definition == null)
        {
            return false;
        }

        string identity = $"{definition.unitName} {definition.code} {definition.jsonKey}";
        return identity.IndexOf("jacknife", StringComparison.OrdinalIgnoreCase) >= 0
            || identity.IndexOf("jackknife", StringComparison.OrdinalIgnoreCase) >= 0
            || definition.unitPrefab?.GetComponentInChildren<Repairer>(true) != null;
    }

    private static bool IsSamLogisticsMission(CargoMission mission)
    {
        return mission.FoundationSiteId >= 0
            || mission.DepositSiteId >= 0
            || mission.JacknifeSiteId >= 0;
    }

    private static void OpenCargoDoors(Aircraft aircraft, CargoMission mission)
    {
        for (int stationIndex = 0; stationIndex < aircraft.weaponStations.Count; stationIndex++)
        {
            WeaponStation station = aircraft.weaponStations[stationIndex];
            if (station == null || !station.Cargo)
            {
                continue;
            }

            for (int weaponIndex = 0; weaponIndex < station.Weapons.Count; weaponIndex++)
            {
                Weapon weapon = station.Weapons[weaponIndex];
                if (weapon != null && WeaponHardpointField?.GetValue(weapon) is Hardpoint hardpoint)
                {
                    hardpoint.SpringOpenBayDoors();
                    for (int doorIndex = 0; doorIndex < hardpoint.bayDoors.Length; doorIndex++)
                    {
                        BayDoor door = hardpoint.bayDoors[doorIndex];
                        if (door != null && !mission.CargoDoors.Contains(door))
                        {
                            mission.CargoDoors.Add(door);
                        }
                    }
                }
            }
        }
    }

    private static bool CloseCargoDoors(CargoMission mission)
    {
        bool closed = true;
        for (int i = mission.CargoDoors.Count - 1; i >= 0; i--)
        {
            BayDoor door = mission.CargoDoors[i];
            if (door == null)
            {
                mission.CargoDoors.RemoveAt(i);
                continue;
            }

            BayDoorOpenTimerField?.SetValue(door, 0f);
            door.enabled = true;
            float openAmount = BayDoorOpenAmountField?.GetValue(door) is float value ? value : 0f;
            closed &= openAmount <= 0.01f;
        }
        return closed;
    }

    private static void UpdateDeliveryCompleted(Aircraft aircraft, CargoMission mission)
    {
        // The rule itself is DeliveryComplete in Supply/CommanderCargoUnloadRule.cs (Reuse rule 4):
        // the delivery bypass has to be able to check at load that a bypassed load, which runs no
        // ramp clearance at all, still closes its order.
        if (CommanderCargoUnloadRule.DeliveryComplete(
                mission.ActivatedCargoCount,
                mission.ExpectedCargoLoads,
                HasDeployableCargo(aircraft),
                mission.CargoClearancePending))
        {
            mission.DeliveryCompleted = true;
        }
    }

    private static float GetCargoSupply(Unit cargo)
    {
        float supply = 0f;
        Rearmer[] rearmers = cargo.GetComponentsInChildren<Rearmer>(true);
        for (int i = 0; i < rearmers.Length; i++)
        {
            supply += Mathf.Max(0f, rearmers[i].Capacity);
        }
        return supply;
    }

    private static float GetCargoSupply(UnitDefinition? definition)
    {
        float supply = 0f;
        Rearmer[] rearmers = definition?.unitPrefab != null
            ? definition.unitPrefab.GetComponentsInChildren<Rearmer>(true)
            : Array.Empty<Rearmer>();
        for (int i = 0; i < rearmers.Length; i++)
        {
            supply += Mathf.Max(0f, rearmers[i].Capacity);
        }
        return supply;
    }

    private static void DestroyCargoUnit(Unit cargo)
    {
        if (cargo.Identity != null && NetworkManagerNuclearOption.i?.ServerObjectManager != null)
        {
            NetworkManagerNuclearOption.i.ServerObjectManager.Destroy(
                cargo.Identity,
                !cargo.Identity.IsSceneObject);
        }
    }

    private bool OverrideAssignedNearestAirbase(PilotBaseState state)
    {
        Aircraft? aircraft = AircraftField?.GetValue(state) as Aircraft;
        if (aircraft == null
            || !assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || mission.OriginAirbase == null
            || mission.OriginAirbase.disabled
            || mission.OriginAirbase.CurrentHQ != mission.Hq)
        {
            return false;
        }

        StateNearestAirbaseField?.SetValue(state, mission.OriginAirbase);
        return true;
    }

    private void PruneFinishedMissions()
    {
        if (assignedMissions.Count == 0)
        {
            return;
        }

        List<Aircraft>? stale = null;
        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            if (entry.Key != null && !entry.Key.disabled)
            {
                continue;
            }

            stale ??= new List<Aircraft>();
            stale.Add(entry.Key!);
        }

        if (stale == null)
        {
            return;
        }

        for (int i = 0; i < stale.Count; i++)
        {
            Aircraft aircraft = stale[i];
            if (ReferenceEquals(aircraft, null))
            {
                continue;
            }

            if (aircraft.autopilot != null)
            {
                terrainClearanceAutopilots.Remove(aircraft.autopilot);
                assignedAutopilotAircraft.Remove(aircraft.autopilot);
            }
            pendingTerrainAutopilotBindings.Remove(aircraft);

            if (assignedMissions.TryGetValue(aircraft, out CargoMission failedMission)
                && !failedMission.Cancelled
                && !failedMission.DeliveryCompleted)
            {
                CommanderSamSiteService.NotifySupplyMissionFailed(
                    failedMission.FoundationSiteId,
                    failedMission.DepositSiteId,
                    failedMission.JacknifeSiteId);
                if (failedMission.InsertionPoint != null && !failedMission.AbandonedOnDeck)
                {
                    // The vehicles died with the hull (engine behaviour: cargo spawns disabled
                    // when the carrying part detaches), so the point takes the loss cooldown and
                    // falls back to driving.
                    CommanderOperationsService.NoteInsertionLost(failedMission.Hq, failedMission.InsertionPoint);
                }
            }
            assignedMissions.Remove(aircraft);
        }
    }

    private static bool TrySelectCargoStation(Aircraft aircraft)
    {
        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            WeaponStation station = aircraft.weaponStations[i];
            if (station != null && station.Cargo && station.WeaponInfo != null && station.WeaponInfo.cargo && HasDeployableCargo(station))
            {
                aircraft.weaponManager.currentWeaponStation = station;
                return true;
            }
        }

        return false;
    }

    private static bool TrySelectNextCargoWeapon(
        Aircraft aircraft,
        out WeaponStation station,
        out Weapon cargoWeapon)
    {
        for (int stationIndex = 0; stationIndex < aircraft.weaponStations.Count; stationIndex++)
        {
            WeaponStation candidate = aircraft.weaponStations[stationIndex];
            if (candidate == null || !candidate.Cargo)
            {
                continue;
            }

            for (int weaponIndex = 0; weaponIndex < candidate.Weapons.Count; weaponIndex++)
            {
                Weapon weapon = candidate.Weapons[weaponIndex];
                if (weapon != null && weapon.GetAmmoLoaded() > 0)
                {
                    station = candidate;
                    cargoWeapon = weapon;
                    return true;
                }
            }
        }

        station = null!;
        cargoWeapon = null!;
        return false;
    }

    private static bool HasDeployableCargo(Aircraft aircraft)
    {
        return CountDeployableCargo(aircraft) > 0;
    }

    private static int CountDeployableCargo(Aircraft aircraft)
    {
        int count = 0;
        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            WeaponStation station = aircraft.weaponStations[i];
            if (station == null || !station.Cargo)
            {
                continue;
            }

            for (int weaponIndex = 0; weaponIndex < station.Weapons.Count; weaponIndex++)
            {
                Weapon? weapon = station.Weapons[weaponIndex];
                if (weapon != null && weapon.GetAmmoLoaded() > 0)
                {
                    count += weapon.GetAmmoLoaded();
                }
            }
        }

        return count;
    }

    private static bool HasDeployableCargo(WeaponStation station)
    {
        for (int i = 0; i < station.Weapons.Count; i++)
        {
            if (station.Weapons[i] != null && station.Weapons[i].GetAmmoLoaded() > 0)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class PendingTargetSelection
    {
        internal PendingTargetSelection(
            CargoAircraftOption aircraft,
            Loadout loadout,
            string cargoLabel,
            Airbase airbase,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            string supportSummary,
            bool useOtherAirfields)
        {
            Aircraft = aircraft;
            Loadout = loadout;
            CargoLabel = cargoLabel;
            Airbase = airbase;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            SupportSummary = supportSummary;
            UseOtherAirfields = useOtherAirfields;
        }

        internal CargoAircraftOption Aircraft { get; }
        internal Loadout Loadout { get; }
        internal string CargoLabel { get; }
        internal Airbase Airbase { get; }
        internal bool HighTerrainClearance { get; }
        internal float TerrainClearanceMeters { get; }
        internal bool Airdrop { get; }
        internal string SupportSummary { get; }
        internal bool UseOtherAirfields { get; }
        internal List<GlobalPosition> Targets { get; } = new();

        internal string GetTargetPrompt()
        {
            string targetType = Airdrop ? "airdrop point" : "landing point";
            return $"Click a {targetType} in the 3D world. The game's Cancel binding cancels.";
        }
    }

    private sealed class QueuedCargoSpawn
    {
        internal QueuedCargoSpawn(
            CargoAircraftOption aircraft,
            Loadout loadout,
            string cargoLabel,
            Airbase requestedAirbase,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            string supportSummary,
            bool useOtherAirfields,
            IReadOnlyList<GlobalPosition> targets,
            FactionHQ? hq = null,
            CommanderStrategicPoint? insertionPoint = null,
            float insertionCargoValue = 0f)
        {
            Aircraft = aircraft;
            Loadout = loadout;
            CargoLabel = cargoLabel;
            RequestedAirbase = requestedAirbase;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            SupportSummary = supportSummary;
            UseOtherAirfields = useOtherAirfields;
            Targets = new List<GlobalPosition>(targets);
            Hq = hq;
            InsertionPoint = insertionPoint;
            InsertionCargoValue = insertionCargoValue;
        }

        internal CargoAircraftOption Aircraft { get; }
        internal Loadout Loadout { get; }
        internal string CargoLabel { get; }
        internal Airbase RequestedAirbase { get; }
        internal bool HighTerrainClearance { get; }
        internal float TerrainClearanceMeters { get; }
        internal bool Airdrop { get; }
        internal string SupportSummary { get; }
        internal bool UseOtherAirfields { get; }
        internal List<GlobalPosition> Targets { get; }
        internal GlobalPosition Target => Targets[0];

        /// <summary>
        /// The faction this spawn is bought for. Null means the local HQ — every existing caller —
        /// so the queue stays backwards compatible; the picket-insertion entry is the first caller
        /// that carries another commanded HQ (Reuse rule 5).
        /// </summary>
        internal FactionHQ? Hq { get; }

        /// <summary>The control point an insertion delivers to, or null for every other cargo run.
        /// Settable since 2026-09-16 for <see cref="TryRedirectInsertion"/>.</summary>
        internal CommanderStrategicPoint? InsertionPoint { get; set; }

        /// <summary>The total price of the cargo vehicles an insertion is buying; charged at spawn
        /// and never refunded — the vehicles stay in the world (design Decision 2/3).</summary>
        internal float InsertionCargoValue { get; }

        /// <summary>Which ONE flight of a lift this request is, or zero when the caller keeps no
        /// per-flight identity (lift-wave_20260916). See
        /// <see cref="CommanderCargoFlightSlot"/>.</summary>
        internal int InsertionFlightId { get; set; }
    }

    private sealed class PendingAircraftSpawn
    {
        internal PendingAircraftSpawn(
            FactionHQ hq,
            AircraftDefinition definition,
            Airbase originAirbase,
            string cargoLabel,
            GlobalPosition target,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            string supportSummary,
            bool purchasedWithFunds,
            float purchaseCost,
            IReadOnlyList<GlobalPosition> targets,
            float expiresAt,
            Ship? navalTarget = null,
            CommanderStrategicPoint? insertionPoint = null,
            float insertionCargoValue = 0f)
        {
            Hq = hq;
            Definition = definition;
            OriginAirbase = originAirbase;
            CargoLabel = cargoLabel;
            Target = target;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            SupportSummary = supportSummary;
            PurchasedWithFunds = purchasedWithFunds;
            PurchaseCost = purchaseCost;
            Targets = new List<GlobalPosition>(targets);
            ExpiresAt = expiresAt;
            NavalTarget = navalTarget;
            InsertionPoint = insertionPoint;
            InsertionCargoValue = insertionCargoValue;
        }

        internal FactionHQ Hq { get; }
        internal AircraftDefinition Definition { get; }
        internal Airbase OriginAirbase { get; }
        internal string CargoLabel { get; }
        /// <summary>Settable since 2026-09-16 for <see cref="TryRedirectInsertion"/>.</summary>
        internal GlobalPosition Target { get; set; }
        internal bool HighTerrainClearance { get; }
        internal float TerrainClearanceMeters { get; }
        internal bool Airdrop { get; }
        internal string SupportSummary { get; }
        internal bool PurchasedWithFunds { get; }
        internal float PurchaseCost { get; }
        internal List<GlobalPosition> Targets { get; }
        internal float ExpiresAt { get; }

        /// <summary>True when this spawn was put into the air over the base rather than out of a
        /// hangar, so the registration match does not mistake it for a wing buy by its altitude.</summary>
        internal bool AirborneSpawn { get; set; }
        internal Ship? NavalTarget { get; }

        /// <summary>The control point an insertion delivers to, or null for every other cargo run.
        /// Settable since 2026-09-16 for <see cref="TryRedirectInsertion"/>.</summary>
        internal CommanderStrategicPoint? InsertionPoint { get; set; }

        /// <summary>The vehicle charge already taken for this insertion, carried so the timeout
        /// path can hand it back if the transport never matches at registration.</summary>
        internal float InsertionCargoValue { get; }

        /// <summary>Which ONE flight of a lift this spawn is, or zero when the caller keeps no
        /// per-flight identity (lift-wave_20260916). Carried from the request it came from so the
        /// operations side can bind the registered transport to the right record.</summary>
        internal int InsertionFlightId { get; set; }
    }

    private sealed class CargoMission
    {
        internal CargoMission(
            FactionHQ hq,
            GlobalPosition target,
            string cargoLabel,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            bool purchasedWithFunds,
            float purchaseCost,
            int expectedCargoLoads,
            IReadOnlyList<GlobalPosition> deliveryTargets,
            bool depositAtSamCore,
            int depositSiteId,
            int foundationSiteId,
            int jacknifeSiteId,
            Airbase originAirbase,
            Ship? navalTarget = null,
            CommanderStrategicPoint? insertionPoint = null)
        {
            Hq = hq;
            DeliveryTargets = new List<GlobalPosition>(deliveryTargets);
            CargoLabel = cargoLabel;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            PurchasedWithFunds = purchasedWithFunds;
            PurchaseCost = purchaseCost;
            ExpectedCargoLoads = expectedCargoLoads;
            DepositAtSamCore = depositAtSamCore;
            DepositSiteId = depositSiteId;
            FoundationSiteId = foundationSiteId;
            JacknifeSiteId = jacknifeSiteId;
            OriginAirbase = originAirbase;
            NavalTarget = navalTarget;
            InsertionPoint = insertionPoint;
        }

        internal FactionHQ Hq { get; }
        internal GlobalPosition Target => DeliveryTargets[0];
        internal List<GlobalPosition> DeliveryTargets { get; }
        internal string CargoLabel { get; }
        internal bool HighTerrainClearance { get; }
        internal float TerrainClearanceMeters { get; }
        /// <summary>Settable since 2026-09-14: the stall path turns a live landing flight into an
        /// airdrop when it cannot get down (<c>TryConvertInsertionToAirdrop</c>). Every other caller
        /// sets it once at construction.</summary>
        internal bool Airdrop { get; set; }
        internal bool PurchasedWithFunds { get; }
        internal float PurchaseCost { get; }
        internal int ExpectedCargoLoads { get; }
        internal bool DepositAtSamCore { get; }
        internal int DepositSiteId { get; }
        internal int FoundationSiteId { get; }
        internal int JacknifeSiteId { get; }
        internal Airbase OriginAirbase { get; }
        internal Ship? NavalTarget { get; }

        /// <summary>
        /// The control point this flight reinforces (picket insertion), or null for every other
        /// cargo mission. Non-null is what the insertion branches key on: the LZ override needs no
        /// SAM ids, the ejection suppression extends to it, and each activated cargo vehicle is
        /// adopted into the point's picket. Settable since 2026-09-16: a flight whose site went bad
        /// is re-pointed at another (<see cref="TryRedirectInsertion"/>) rather than flown home.
        /// </summary>
        internal CommanderStrategicPoint? InsertionPoint { get; set; }

        /// <summary>Which ONE flight of a lift this mission is, or zero when the caller keeps no
        /// per-flight identity (lift-wave_20260916). Non-zero is what lets a lift recall, re-drop or
        /// credit ONE load of a wave rather than every mission standing on the same point.</summary>
        internal int InsertionFlightId { get; set; }
        internal bool PurchaseRefunded { get; set; }
        internal bool Initialized { get; set; }
        internal bool TargetOverrideActive { get; set; } = true;

        /// <summary>Scaled <c>Time.time</c> the spawned transport was matched to this mission, so the
        /// abandoned-on-deck line can say how long the crew sat before ejecting.</summary>
        internal float AssignedAt { get; set; }

        /// <summary>True once <see cref="HandleAircraftReturned"/> has read this transport as abandoned
        /// before take-off (overnight log 2026-09-15). The stale sweep then leaves the point's loss
        /// bookkeeping alone: the operations side was told it was a launch failure instead.</summary>
        internal bool AbandonedOnDeck { get; set; }
        internal int ActivatedCargoCount { get; set; }
        internal int ReleasedCargoCount { get; set; }
        internal float NextCargoReleaseAt { get; set; }
        internal float LastCargoReleasedAt { get; set; }
        internal bool CargoClearancePending { get; set; }

        /// <summary>True once this flight has begun unloading its vehicles in place instead of
        /// landing (delivery-bypass_20260916). LATCHED: a transport that has started putting vehicles
        /// on the ground must not change its mind because it drifted a few metres or gained a little
        /// height between releases. The ONE thing that clears it is
        /// <see cref="TryRedirectInsertion"/>, which resets every other arrival field of the flight
        /// for the same reason: the flight is going somewhere else now.</summary>
        internal bool UnloadInPlace { get; set; }

        /// <summary>Scaled <c>Time.timeSinceLevelLoad</c> this flight began unloading in place, so a
        /// flight that has latched but not yet fired a mount still reads as making progress. Zero
        /// until it latches.</summary>
        internal float UnloadStartedAt { get; set; }

        /// <summary>Where the transport was when it latched, which is what the per-vehicle spacing is
        /// measured from. Taken ONCE rather than read live: the aircraft is near-stationary but not
        /// still, and five seconds of drift between two releases at up to the unload speed limit is
        /// further than the spacing itself — so offsets measured from the live position could put the
        /// second vehicle back on the first. Null until the flight latches.</summary>
        internal GlobalPosition? UnloadAnchor { get; set; }

        /// <summary>True once this flight has already said that no clear ground could be found near
        /// the transport, so the line is written once per flight rather than once per vehicle.</summary>
        internal bool UnloadGroundFallbackLogged { get; set; }

        /// <summary>How many vehicles this flight has already set down in place, which is what gives
        /// each one its own patch of ground (delivery-bypass_20260916). Separate from
        /// <see cref="ReleasedCargoCount"/> because a release is not a placement: only a vehicle the
        /// bypass actually placed advances this.</summary>
        internal int UnloadPlacedCount { get; set; }
        internal bool Cancelled { get; set; }
        internal float LandingUnloadAt { get; set; }
        internal bool VerticalDepartureActive { get; set; }
        internal bool FoundationLandingSearchInitialized { get; set; }
        internal bool PlatformArrivalInitialized { get; set; }
        internal bool PlatformArrivalReached { get; set; }
        internal GlobalPosition PlatformArrivalPoint { get; set; }
        internal bool PlatformApproachComplete { get; set; }
        internal bool RoutePlanned { get; set; }
        internal bool SteepLanding { get; set; }
        internal bool RouteTransitActive { get; set; }
        internal float LastTransportOverrideFixedTime { get; set; } = float.NegativeInfinity;
        internal int ApproachRouteIndex { get; set; }
        internal readonly List<GlobalPosition> ApproachRoute = new();
        internal bool DeliveryCompleted { get; set; }
        internal readonly List<BayDoor> CargoDoors = new();

        /// <summary>True once a delivered insertion's return flight has been issued, so the cargo
        /// tick issues it exactly once.</summary>
        internal bool ReturnIssued { get; set; }
    }}
