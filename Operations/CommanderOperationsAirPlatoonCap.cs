using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Platoon-requested CAP and transport escorts. Split out of <c>CommanderOperationsAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past six thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderOperationsService
{
    // ---- Platoon-requested CAP (design.md addendum 2026-09-14, Section 7) ----

    /// <summary>
    /// The counterpart to the home CAP's ceiling: a platoon under way or holding its posts, and a
    /// forward base or picket point, that has tracked hostile AIRCRAFT over it gets fighters of its
    /// own — even when nothing else makes it an active objective. The home CAP stops at four by
    /// decision; this is where the rest of the answer to enemy air comes from.
    /// <para>Anything that already has a sortie over it is skipped: its own CAP wing grows with the
    /// tracked hostiles by the ordinary ladder, so a second sortie would double-count the raid.</para>
    /// </summary>
    private void AddPlatoonCapDemand(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            CommanderPlatoon platoon = state.Platoons[i];
            if (platoon.State != CommanderPlatoonState.Moving
                && platoon.State != CommanderPlatoonState.Attacking
                && platoon.State != CommanderPlatoonState.Holding)
            {
                continue;
            }

            if (AlreadyServed(demand, platoon.Mission, platoon))
            {
                continue;
            }

            AddCapDemand(hq, demand, PlatoonAirCenter(platoon), platoon.Name, null, platoon);
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if ((mission.Kind != CommanderMissionKind.ForwardBase && mission.Kind != CommanderMissionKind.Picket)
                || mission.Point == null
                || AlreadyServed(demand, mission, null)
                // A lift cover already has fighters over this very ground (fix, 2026-09-15). It
                // carries no mission — a transport escort never has, so that two CAP sorties on one
                // mission are never matched onto each other — which is exactly why AlreadyServed
                // cannot see it, and the commander was buying a second patrol over a point it was
                // already covering.
                || CoveredByLift(demand, mission.Point.Label))
            {
                continue;
            }

            AddCapDemand(hq, demand, mission.Point.Position, mission.Label, mission, null);
        }

        AddTransportEscortDemand(hq, state, demand);
    }

    /// <summary>Whether this review's demand already has any sortie of any kind over this mission or
    /// this platoon — the platoon CAP only opens where nothing is flying yet.</summary>
    private static bool AlreadyServed(
        List<CommanderAirSortie> demand, CommanderOperationsMission? mission, CommanderPlatoon? platoon)
    {
        for (int i = 0; i < demand.Count; i++)
        {
            CommanderAirSortie sortie = demand[i];
            if ((mission != null && ReferenceEquals(sortie.Mission, mission))
                || (platoon != null && ReferenceEquals(sortie.ContactPlatoon, platoon)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether an aircraft on a deck is stuck, pure: it has a mission (so it should have
    /// left), it has never been airborne (a parked aircraft that flew is recovered, not stuck), and
    /// it has sat there at least <paramref name="minutes"/>. Zero minutes disables the rule.</summary>
    internal static bool StuckOnDeck(bool hasMission, bool everAirborne, float secondsOnDeck, float minutes)
    {
        return minutes > 0f && hasMission && !everAirborne && secondsOnDeck >= minutes * 60f;
    }

    /// <summary>Scratch for the stuck sweep.</summary>
    private readonly List<Aircraft> stuckScratch = new();

    /// <summary>
    /// Despawns and refunds an owned aircraft stuck on a deck (user, 2026-09-14: "check for
    /// aircraft stuck at an airbase and refund/despawn them"): a taxi that never becomes a take-off
    /// is the game's taxi state failing, and the airframe's price comes back in full — the commander
    /// paid for a sortie it never got. Server-only, like every spawn and refund.
    /// </summary>
    private void RefundStuckOnDeck(FactionHQ hq, OperationsState state, CommanderAirCommandService airCommand)
    {
        if (!hq.IsServer)
        {
            return;
        }

        stuckScratch.Clear();
        foreach (Aircraft owned in state.CommanderAirframes)
        {
            if (owned == null || owned.disabled)
            {
                continue;
            }

            if (!CommanderAirCommandService.IsOnDeck(owned))
            {
                state.OnDeckSince.Remove(owned);
                state.EverAirborne.Add(owned);
                continue;
            }

            if (!state.OnDeckSince.TryGetValue(owned, out float since))
            {
                since = Time.time;
                state.OnDeckSince[owned] = since;
            }

            if (StuckOnDeck(airCommand.IsOnAnyMission(owned), state.EverAirborne.Contains(owned), Time.time - since, CommanderSettings.StuckOnDeckMinutes))
            {
                stuckScratch.Add(owned);
            }
        }

        // The ever-airborne set is pruned of dead and destroyed airframes here, once a review, so it
        // does not hold every aircraft the match ever launched (1,775 in one 2026-09-15 match).
        state.EverAirborne.RemoveWhere(static airframe => airframe == null || airframe.disabled);

        for (int i = 0; i < stuckScratch.Count; i++)
        {
            Aircraft stuck = stuckScratch[i];
            float price = stuck.definition is AircraftDefinition definition ? Mathf.Max(0f, definition.value) : 0f;
            string label = CommanderGameAccess.GetUnitLabel(stuck);
            string baseLabel = NearestAirbaseLabel(hq, stuck.transform.GlobalPosition());
            float minutes = (Time.time - state.OnDeckSince[stuck]) / 60f;
            state.OnDeckSince.Remove(stuck);
            state.CommanderAirframes.Remove(stuck);
            state.HomeCapAirframes.Remove(stuck);
            // A write-off on the deck is not a combat loss: told to the attrition ledger before the
            // despawn, or the next sweep would count it as shot down.
            CommanderEnemyCommanderService.NoteAirframeNotLost(stuck);
            if (CommanderEconomyService.DespawnUnit(stuck))
            {
                hq.AddFunds(price);
                CommanderAiLog.Note(hq, $"refunds {label} ({price:0}): stuck on the deck at {baseLabel} for {minutes:0} min without taking off.");
            }
        }

        stuckScratch.Clear();
    }

    /// <summary>
    /// Whether the next AI launch from a base spawns airborne at the map edge instead of taxiing,
    /// pure: only in hangar mode (an air-spawn commander already launches airborne over the base),
    /// and only once more than <paramref name="queueMax"/> aircraft are already waiting on the deck.
    /// </summary>
    internal static bool LaunchesFromMapEdge(bool hangarMode, int onDeck, int queueMax)
    {
        return hangarMode && queueMax >= 0 && onDeck > queueMax;
    }

    /// <summary>
    /// The point on the map's boundary nearest <paramref name="baseLocal"/>, pure: the map is
    /// centred on the origin with half-extents of half <paramref name="mapSize"/>; the nearest of
    /// the four edges is chosen and the other coordinate kept, so the aircraft enters on the
    /// straightest line to the base.
    /// </summary>
    internal static Vector3 NearestMapEdgePoint(Vector3 baseLocal, Vector2 mapSize)
    {
        float halfX = Mathf.Max(1f, mapSize.x * 0.5f);
        float halfZ = Mathf.Max(1f, mapSize.y * 0.5f);
        float toEast = halfX - baseLocal.x;
        float toWest = baseLocal.x + halfX;
        float toNorth = halfZ - baseLocal.z;
        float toSouth = baseLocal.z + halfZ;
        float nearest = Mathf.Min(Mathf.Min(toEast, toWest), Mathf.Min(toNorth, toSouth));
        if (nearest == toEast)
        {
            return new Vector3(halfX, baseLocal.y, Mathf.Clamp(baseLocal.z, -halfZ, halfZ));
        }

        if (nearest == toWest)
        {
            return new Vector3(-halfX, baseLocal.y, Mathf.Clamp(baseLocal.z, -halfZ, halfZ));
        }

        if (nearest == toNorth)
        {
            return new Vector3(Mathf.Clamp(baseLocal.x, -halfX, halfX), baseLocal.y, halfZ);
        }

        return new Vector3(Mathf.Clamp(baseLocal.x, -halfX, halfX), baseLocal.y, -halfZ);
    }

    /// <summary>
    /// Fighters over every transport flight's landing zone while the flight is queued or in the
    /// air (user, 2026-09-14: "aerial picket insertions are not generated for points near enemy
    /// unless heavily escorted by CAP & CAS"; 15 of 74 picket flights and 7 of 15 construction
    /// flights of that match were lost to fighters). Unlike a platoon's CAP this one opens with at
    /// least ONE fighter even when no hostile aircraft is tracked yet — a transport meets the raid
    /// before anyone tracks it — and grows with the tracked hostiles by the ordinary formula.
    /// </summary>
    private static void AddTransportEscortDemand(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        for (int i = 0; i < state.Insertions.Count; i++)
        {
            CommanderInsertion insertion = state.Insertions[i];
            if (insertion.Delivered >= insertion.ExpectedLoads || (insertion.Aircraft != null && insertion.Aircraft.disabled))
            {
                continue;
            }

            // No mission on the escort: the picket mission already owns its own CAP sortie, and two
            // Cap sorties on one mission would be matched onto each other by SameSortie.
            AddEscortDemand(hq, demand, insertion.Lz, $"escort to {insertion.Point.Label}", null);
        }

        // The FOB flights' own escorts used to be posted here, one per flight in the air. They are
        // the lift cover's escort half now (design.md, air-mobile-platoons_20260915 Section 3): an
        // escort that only exists once the transport is airborne is an escort the transport cannot
        // wait for, and the whole point of the cover is that it is up BEFORE the load launches.
    }

    /// <summary>
    /// The lift cover's label, pure for the self-check: one sortie per lift order, named after the
    /// ground it is covering rather than after the load, so two lifts never collide on one label and
    /// the reconcile (which matches a mission-less CAP sortie by its label alone) keeps the same
    /// sortie alive across the whole order.
    /// </summary>
    internal static string LiftCoverLabel(string pointLabel)
    {
        return $"LIFT ESCORT {pointLabel}";
    }

    /// <summary>Whether one sortie label IS the lift cover over a given point, pure — the match the
    /// forward-base patrol walk uses to leave ground a lift is already covering alone.</summary>
    internal static bool IsLiftCoverFor(string sortieLabel, string pointLabel)
    {
        return sortieLabel == LiftCoverLabel(pointLabel);
    }

    /// <summary>Whether a sortie label is ANY lift cover, pure — the label prefix every lift cover
    /// carries. The retask rule reads it: a cover is never stripped for a contact elsewhere.</summary>
    internal static bool IsLiftCoverLabel(string sortieLabel)
    {
        return sortieLabel != null && sortieLabel.StartsWith(LiftCoverLabel(string.Empty), System.StringComparison.Ordinal);
    }

    /// <summary>True when this review's demand already holds a lift cover over
    /// <paramref name="pointLabel"/>.</summary>
    private static bool CoveredByLift(List<CommanderAirSortie> demand, string pointLabel)
    {
        for (int i = 0; i < demand.Count; i++)
        {
            if (IsLiftCoverFor(demand[i].Label, pointLabel))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fighters over every open lift — a platoon lift and a FOB construction order alike — from the
    /// order opening to its last landing (design.md, air-mobile-platoons_20260915 Section 3; user
    /// decision 3, 2026-09-15: "escort plus sweep first"). The escort is sized by the strike
    /// package's own live rule so there is ONE escort formula in the mod: a floor of
    /// <c>CommanderSettings.LiftEscortMinimum</c>, never fewer than the hostile aircraft tracked over
    /// the landing zone or along the route, never more than the airborne ceiling has room for. The
    /// suppression element is only whether the cover EXPECTS a sweep; the sortie itself is opened by
    /// the ordinary belt clustering, exactly as a strike package's is.
    /// <para>Posted before <c>InsertAradDemand</c> in the demand walk, because that is what can bind
    /// a belt to it; a cover posted after it would never be swept for.</para>
    /// </summary>
    private void AddLiftCoverDemand(FactionHQ hq, OperationsState state, List<CommanderAirSortie> demand)
    {
        int headroom = Mathf.Max(
            0, EffectiveAirborneCeiling(hq) - CommanderEnemyCommanderService.CountAirborne(hq));
        for (int i = 0; i < state.FobOrders.Count; i++)
        {
            CommanderFobOrder order = state.FobOrders[i];
            if (order.Phase != CommanderFobPhase.Delivering || !order.ByAir)
            {
                continue;
            }

            GlobalPosition zone = order.Point.Position;
            string label = LiftCoverLabel(order.Point.Label);
            bool already = false;
            for (int d = 0; d < demand.Count && !already; d++)
            {
                already = demand[d].Label == label;
            }

            if (already)
            {
                continue;
            }

            bool haveOrigin = TryFindInsertionLaunchBase(hq, zone, out GlobalPosition origin);
            int hostileAir = CountHostileAirNearLift(hq, origin, zone, haveOrigin);
            // Never below the floor, however full the sky (fix, 2026-09-18, found in the track's own
            // review). StrikeEscortWanted clamps to the room left under the ceiling, so at a full sky
            // it returned ZERO — and a cover wanting zero escorts reads as a cover already satisfied,
            // so LiftMayLaunch(0, 0, 0, ...) let the transport go at once, alone, with no hold at
            // all. That is the loss mode the escort rule was written for (2026-09-14: 15 of 74 picket
            // flights and 7 of 15 construction flights lost to fighters). The headroom clamp is meant
            // to bound the escort's GROWTH, not to delete it. With the floor restored, a lift at a
            // full sky holds at its form-up point and is released by the existing bounded wait
            // (PackageFormUpSeconds) rather than launching naked on the first review — the same
            // compromise the wait already makes everywhere else.
            int escorts = Mathf.Max(
                CommanderSettings.LiftEscortMinimum,
                StrikeEscortWanted(CommanderSettings.LiftEscortMinimum, hostileAir, headroom));
            // Spent as it is given out (fix, 2026-09-15): the headroom was read once outside this
            // loop, so two open lifts each sized their escort against the whole of the room left
            // under the airborne ceiling and together asked for twice what there was.
            headroom = Mathf.Max(0, headroom - escorts);
            // The same suppression element a hard strike target is owed (HardArad): the question is
            // only whether the package waits for a sweep at all, and the belt's own size is what buys
            // the second airframe.
            bool routeThreatened = haveOrigin && TryFindInsertionRouteThreat(hq, origin, zone, out _);
            demand.Add(new CommanderAirSortie
            {
                Kind = CommanderSortieKind.Cap,
                IsTransportEscort = true,
                Center = zone,
                Wanted = 0,
                CapsWanted = escorts,
                AradWanted = routeThreatened ? HardArad : 0,
                LastHostileAir = hostileAir,
                LastAirDefence = CountObservedAirDefence(hq, zone),
                NoCapWait = true,
                // The transport is what waits for this cover, not the other way round: the fighters
                // fly to the zone as soon as they are bought, and the lift holds at its form-up point
                // until they are up.
                InContact = hostileAir > 0,
                Label = label,
            });
        }
    }

    /// <summary>
    /// Hostile aircraft this commander has tracked over a lift's landing zone or along the leg to it
    /// — the number the cover's escort is never sized below. One walk of the tracking database with
    /// the insertion threat test's own freshness and own-faction skip, widened from "is anything
    /// there" to "how many", because the escort rule counts rather than asks.
    /// </summary>
    private static int CountHostileAirNearLift(
        FactionHQ hq, GlobalPosition origin, GlobalPosition zone, bool haveOrigin)
    {
        if (haveOrigin)
        {
            BuildRouteSamples(
                origin.AsVector3(), zone.AsVector3(), InsertionRouteSampleMeters, liftRouteSamples);
        }
        else
        {
            liftRouteSamples.Clear();
        }

        int count = 0;
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > CommanderEnemyCommanderService.ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not Aircraft
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq))
            {
                continue;
            }

            GlobalPosition at = info.lastKnownPosition;
            if (CommanderGameAccess.HorizontalDistance(at.AsVector3(), zone.AsVector3()) <= ObservedRadiusMeters
                || RouteThreatened(liftRouteSamples, at.AsVector3(), InsertionThreatRadiusMeters, out _))
            {
                count++;
            }
        }

        liftRouteSamples.Clear();
        return count;
    }

    /// <summary>Scratch for the lift cover's route walk — one commander's covers are sized at a
    /// time, the insertion scratch convention.</summary>
    private static readonly List<Vector3> liftRouteSamples = new();

    /// <summary>
    /// Whether ONE escort is AHEAD of the load it covers, pure (user instruction, 2026-09-17: "air
    /// insertions should wait for their escorts to be ahead of them (they have a habit of flying
    /// straight into danger)"). Two conditions, both necessary: the escort is off the deck, and it
    /// has less ground left to the LANDING ZONE than the load has, by at least
    /// <paramref name="marginMeters"/>.
    /// <para>Ground left to cover, never a fraction of the route: a load and its cover routinely
    /// launch from different airbases, and the same fraction of two different routes is not the same
    /// piece of sky. The distances are to the landing zone the load is actually going to — a lift's
    /// landing zone is often an airhead short of its objective — so the comparison is against the
    /// ground the load will really be over.</para>
    /// <para>Airborne is asked separately because <see cref="CountFightersUp"/> counts a fighter
    /// that is alive and bound, parked or not: a cover still on the runway of a base nearer the
    /// landing zone than the load's own base would otherwise read as "ahead" while its wheels were
    /// still down, which is the very failure this rule exists to stop.</para>
    /// <para>An escort that has turned for home reads as behind and goes on reading as behind until
    /// it re-engages; that case is what the bounded wait in <see cref="LiftMayLaunch"/> is for.
    /// Inclusive at the boundary, the convention the rest of the mod uses.</para>
    /// </summary>
    internal static bool EscortIsAhead(
        bool escortAirborne, float escortToLandingZoneMeters, float loadToLandingZoneMeters, float marginMeters)
    {
        return escortAirborne
            && escortToLandingZoneMeters + Mathf.Max(0f, marginMeters) <= loadToLandingZoneMeters;
    }

    /// <summary>
    /// Whether a load may leave the deck, pure (design.md, air-mobile-platoons_20260915 Section 3;
    /// user decision 3, 2026-09-15): its cover's fighters are up, enough of them are AHEAD of it
    /// (user instruction, 2026-09-17 — see <see cref="EscortIsAhead"/>) AND the sweep has gone in, or
    /// the bounded wait has run out. The wait is <c>CommanderSettings.PackageFormUpSeconds</c>, the
    /// same clock every other package holds on, and it is inclusive at the boundary exactly as
    /// <see cref="PackageGoesIn"/> is — one bounded clock in the mod, not two, and the same timeout
    /// releases the escort wait and the ahead wait together. A negative
    /// <paramref name="secondsWaiting"/> means nothing has been held yet.
    /// <para>An escort that is ahead is an escort that is up, so <paramref name="escortsAhead"/> is
    /// never greater than <paramref name="escortsUp"/>; both are kept so the hold line can say which
    /// of the two the load is waiting on.</para>
    /// </summary>
    internal static bool LiftMayLaunch(
        int escortsUp,
        int escortsAhead,
        int escortsWanted,
        bool aradPending,
        float secondsWaiting,
        float formUpSeconds)
    {
        if (secondsWaiting >= 0f && formUpSeconds > 0f && secondsWaiting >= formUpSeconds)
        {
            return true;
        }

        return escortsUp >= escortsWanted && escortsAhead >= escortsWanted && !aradPending;
    }

    /// <summary>
    /// Fewest fighters an escort ever has: two (user, 2026-09-14: "heavily escorted"). The
    /// 2026-09-14 match flew single-fighter escorts and lost six of them over one FOB site to
    /// fighters nobody had tracked — one aeroplane alone is the first thing a pair of raiders kills.
    /// </summary>
    internal const int TransportEscortMinimum = 2;

    /// <summary>How many fighters escort a transport, pure: the platoon CAP formula for the tracked
    /// hostile aircraft, never fewer than <see cref="TransportEscortMinimum"/>.</summary>
    internal static int TransportEscortWanted(int trackedHostileAir)
    {
        return Mathf.Max(TransportEscortMinimum, PlatoonCapWanted(trackedHostileAir));
    }

    private static void AddEscortDemand(
        FactionHQ hq, List<CommanderAirSortie> demand, GlobalPosition center, string label, CommanderOperationsMission? mission)
    {
        for (int i = 0; i < demand.Count; i++)
        {
            if (demand[i].Label == label)
            {
                return;
            }
        }

        int hostileAir = CountHostileAirInRing(hq, center, ObservedRadiusMeters);
        demand.Add(new CommanderAirSortie
        {
            Kind = CommanderSortieKind.Cap,
            IsTransportEscort = true,
            Mission = mission,
            Center = center,
            Wanted = 0,
            CapsWanted = TransportEscortWanted(hostileAir),
            LastHostileAir = hostileAir,
            NoCapWait = true,
            InContact = hostileAir > 0,
            Label = label,
        });
    }

    private static void AddCapDemand(
        FactionHQ hq,
        List<CommanderAirSortie> demand,
        GlobalPosition center,
        string label,
        CommanderOperationsMission? mission,
        CommanderPlatoon? platoon)
    {
        int hostileAir = CountHostileAirInRing(hq, center, ObservedRadiusMeters);
        if (hostileAir <= 0)
        {
            return;
        }

        demand.Add(new CommanderAirSortie
        {
            Kind = CommanderSortieKind.Cap,
            // The one site that posts a STANDING patrol, and so the one site that marks itself as
            // rationed (air-ceiling_20260918 §4 decision F). AddEscortDemand and AddLiftCoverDemand
            // post the same sortie kind and deliberately leave this false.
            IsStandingPatrol = true,
            Mission = mission,
            ContactPlatoon = platoon,
            Center = center,
            Wanted = 0,
            CapsWanted = PlatoonCapWanted(hostileAir),
            LastHostileAir = hostileAir,
            NoCapWait = true,
            // It only opened because hostile aircraft are overhead, so it is in contact by
            // construction: a platoon CAP is a target for a retask and never a source.
            InContact = true,
            Label = label,
        });
    }
}
