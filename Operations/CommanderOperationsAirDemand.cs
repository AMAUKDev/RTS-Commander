using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Runtime demand, binding, the claim, and what the buyer reads. Split out of <c>CommanderOperationsAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past six thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderOperationsService
{
    // ---- Runtime: demand, binding, the claim, and what the buyer reads ----

    /// <summary>Registered faction units forwarded from the shared <c>RegisterFactionUnit</c>
    /// postfix; claims an airframe the buy loop just launched into the hungry sortie.</summary>
    internal static void NotifyAircraftRegistered(FactionHQ hq, Unit unit)
    {
        // Stamp it as "arrived while a commander was running" (design SS15) before the claim, so an
        // airframe the claim declines is still distinguishable from a stock mission's authored free
        // aircraft, which existed before the commander did.
        if (unit is Aircraft aircraft
            && CommanderPlayerCommanderService.AnyCommanderOn
            && Instance != null
            && Instance.states.TryGetValue(hq, out OperationsState state))
        {
            state.RegisteredWhileCommanded.Add(aircraft);
        }

        Instance?.TryClaimAircraft(hq, unit);
    }

    /// <summary>The buy loop calls this just before EVERY launch, so the registration that fires
    /// inside the launch can be matched back and the airframe owned — the same shape the player's
    /// own <c>PendingAircraftSpawn</c> uses, for the same reason. A null objective is a standing
    /// buy: the claim owns the airframe and parks it on the home CAP; a sortie claim may retask it
    /// later. <paramref name="forHomeCap"/> marks a rung-1 buy (design.md,
    /// commander-priorities_20260914: one definition, two callers — the sortie buyer and the
    /// ladder's CAP buyer), which the claim never lends to a sortie.</summary>
    internal static void RecordCommanderLaunch(
        FactionHQ hq,
        AircraftDefinition definition,
        Airbase origin,
        GlobalPosition? objective,
        CommanderEnemyCommanderService.AirRole role,
        bool forHomeCap = false)
    {
        if (Instance == null || !Instance.states.TryGetValue(hq, out OperationsState state))
        {
            return;
        }

        state.PendingAirLaunch = new CommanderPendingAirLaunch(
            definition, origin, objective, Time.time + CommanderAirCommandService.PendingSpawnTimeoutSeconds, role, forHomeCap);
    }

    /// <summary>The launch failed, so nothing will register; the expectation would sit for its
    /// whole window and could wrongly claim a same-type airframe registered by something else (a
    /// stock mission's free-aircraft tap on stock maps).</summary>
    internal static void ClearCommanderLaunch(FactionHQ hq)
    {
        if (Instance != null && Instance.states.TryGetValue(hq, out OperationsState state))
        {
            state.PendingAirLaunch = null;
        }
    }

    private void TryClaimAircraft(FactionHQ hq, Unit unit)
    {
        // The pool claim's gates, for the same reasons: host-only, and only for an HQ this service
        // manages. A pure multiplayer client must never touch spawns.
        if (!states.TryGetValue(hq, out OperationsState state)
            || !hq.IsServer
            || unit is not Aircraft aircraft
            || aircraft.disabled
            || aircraft.Player != null
            || state.PendingAirLaunch == null
            || Time.time > state.PendingAirLaunch.ExpiresAt
            || !ReferenceEquals(aircraft.definition, state.PendingAirLaunch.Definition))
        {
            return;
        }

        // Anything already carrying an Air Command mission is somebody else's airframe — the
        // player's own AIR-window launch assigns its mission at registration, and its notify runs
        // ahead of this one. Decision 5's hands-off rule, enforced here rather than by trust.
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand == null || airCommand.IsOnAnyMission(aircraft))
        {
            return;
        }

        // Section 17: never claim a transport the cargo service is about to match to its own run.
        if (IsUnderOtherService(state, aircraft))
        {
            return;
        }

        Airbase origin = state.PendingAirLaunch.Origin;
        bool forHomeCap = state.PendingAirLaunch.ForHomeCap;
        CommanderEnemyCommanderService.AirRole boughtRole = state.PendingAirLaunch.Role;
        state.PendingAirLaunch = null;
        state.CommanderAirframes.Add(aircraft);
        // Counted as a launch here, where the buy's airframe and the world's aircraft meet, so the
        // attrition ledger's launches and losses are the same population (user decision 2026-09-15).
        CommanderEnemyCommanderService.Instance?.RecordAttritionLaunch(hq, aircraft, boughtRole);

        // A rung-1 fighter is owned and parked on the standing home CAP here, full stop (design.md,
        // commander-priorities_20260914 Section 2): home-CAP fighters are "never lent to sorties; a
        // sortie's escort is bought separately in rung 2", so however hungry a sortie is, this
        // airframe never enters one.
        // A radar airframe registering is bound to the radar watch or to nothing at all (user
        // decision 2026-09-14). It never joins the standing home patrol, however it was bought:
        // once it is in HomeCapAirframes the lending pass and the CAP counts both own it, and the
        // one airframe the whole wing's picture comes off is on a patrol instead of on station.
        bool radarAirframe = IsAwacsAirframe(hq, aircraft);
        if (forHomeCap && !radarAirframe)
        {
            state.HomeCapAirframes.Add(aircraft);
            if (IssueAirTask(
                    hq,
                    state,
                    aircraft,
                    CommanderAirCommandService.AirCommandMode.AirGuard,
                    HomeCAPCentre(hq),
                    CommanderEnemyCommanderService.HomeGuardRadiusMeters,
                    HomeCapAltitude(state, aircraft)))
            {
                CommanderAiLog.Note(
                    hq,
                    $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} on the home CAP; it is never lent to a sortie.");
            }

            return;
        }

        // Bind to the hungry sortie that matches what the airframe can DO (user decision
        // 2026-09-14: the same capability the buy bought on — a CAP-bought Compass with Scythes
        // binds to the sortie's CAP, not to the first CAS slot its role identity suggests); a
        // sortie standing down after a loss takes no new airframes either. CAP before CAS at
        // equal capability, the demand queue's own order. With no hungry sortie the airframe is
        // owned and parked on the home CAP right here (user decision 2026-09-13) — before the fix
        // it was bought unowned, tasked by nobody and idled to the ceiling.
        // Sortie by sortie in priority order, each one's own escort → CAS → wingmen order (see
        // NextSortieSlot), and bound by what the airframe can DO for that slot: an AWACS slot takes
        // only a radar airframe, an ARAD slot only one that can carry anti-radiation weapons. The
        // walk this replaced tried EVERY sortie's CAP before any sortie's CAS, so every claimed
        // airframe that could carry an air-to-air missile — which on this roster is nearly all of
        // them — went to a CAP slot and no strike was ever bound (2026-09-14 match).
        // Two passes, because the buy that produced this airframe took a side (fix, 2026-09-14):
        // the first pass reads every sortie's slot the way the buy read it — CAS ahead of the
        // escort on a CAS turn — so a strike airframe bought for a sortie that is still waiting on
        // its escort actually lands in that sortie's CAS slot. The second pass reads them the other
        // way, so a buy that could not be filled as asked (the fighter the roster could not afford,
        // then a home-CAP fighter launched instead) still finds any slot it can fill rather than
        // idling on the home patrol.
        AircraftDefinition definition = (aircraft.definition as AircraftDefinition)!;
        for (int pass = 0; pass < 2; pass++)
        {
            bool preferCas = pass == 0 ? state.LastAirBuyWasCas : !state.LastAirBuyWasCas;
            for (int i = 0; i < state.AirSorties.Count; i++)
            {
                CommanderAirSortie sortie = state.AirSorties[i];
                if (Time.time < sortie.CooldownUntil)
                {
                    continue;
                }

                CommanderAirSlot slot = NextSortieSlot(
                    sortie.Caps.Count, sortie.CapsWanted, sortie.Cas.Count, sortie.Wanted, preferCas);
                if (slot == CommanderAirSlot.None)
                {
                    continue;
                }

                bool asCap = slot != CommanderAirSlot.Cas;
                if (!CommanderEnemyCommanderService.FillsAirRoleNow(
                        hq,
                        aircraft,
                        asCap ? CommanderEnemyCommanderService.AirRole.Fighter : SortieAirRole(sortie))
                    // The radar rule, at the claim: a radar airframe reaches the radar watch's own
                    // main slot and no other slot on any sortie, and no other airframe reaches that
                    // one (user decision 2026-09-14).
                    || !MayBindToSortie(radarAirframe, !asCap && IsAwacsSortie(sortie)))
                {
                    continue;
                }

                if (asCap)
                {
                    BindCap(hq, state, sortie, aircraft);
                }
                else
                {
                    BindCas(hq, state, sortie, aircraft, origin);
                }

                return;
            }
        }

        // Nothing that may not hold the patrol is parked on it while it waits (user reports,
        // 2026-09-14: A-19 Brawlers on AIR SUPERIORITY, then "it's spawned a UH-90 transport for CAP
        // for a platoon"). A ground-attack airframe, a helicopter, a troop carrier and anything the
        // Air Command mission cannot steer all stay owned and idle here; the idle sweep sends them
        // home on the next review and the first sortie that wants them takes them. The AirGuard task
        // below is an AIR SUPERIORITY task, and this was one of the paths handing it out.
        if (aircraft.definition is AircraftDefinition owned
            && !CommanderEnemyCommanderService.MayHoldPatrol(owned))
        {
            return;
        }

        // And the radar aeroplane never holds the patrol either, whatever its ratings say it could
        // fly (user decision 2026-09-14). It stays owned and idle until the radar watch takes it.
        if (radarAirframe)
        {
            return;
        }

        // Owned, no sortie wants it: the home CAP now, so the wing's standing air-defence posture
        // exists from the first minute of a match with no live objectives yet. A rotary transport
        // cannot take an Air Command task (TryTaskAiAircraft refuses anything without a plane
        // pilot) — the Basegame flies it, and it stays owned until a sortie or the player takes it.
        if (IssueAirTask(
                hq,
                state,
                aircraft,
                CommanderAirCommandService.AirCommandMode.AirGuard,
                HomeCAPCentre(hq),
                CommanderEnemyCommanderService.HomeGuardRadiusMeters,
                HomeCapAltitude(state, aircraft)))
        {
            CommanderAiLog.Note(
                hq, $"tasks {CommanderGameAccess.GetUnitLabel(aircraft)} on the home CAP until a sortie wants it.");
        }
    }

    /// <summary>The air step of every operations review (design SS1/SS2): prune the air book,
    /// derive this review's sortie demand in priority order, reconcile the live sorties against
    /// it, fill them from unbound owned airframes, and sync every bound airframe's task.</summary>
    private void PlanAirSupport(FactionHQ hq, OperationsState state)
    {
        PruneAirBook(hq, state);
        // Before the demand is built (design SS9): a recalled fighter's CAP slot has to read as open
        // this review, not next, or the sortie flies a review short of its escort for nothing.
        RecallLentHomeCap(hq, state);
        BuildAirDemand(hq, state, airDemand);
        ReconcileSorties(hq, state, airDemand);
        FillSorties(hq, state);
        UpdatePackages(hq, state);
        // After the package machinery has decided whether the strike goes in this review, so the
        // go-in, the delivery and the escorts' loiter are all acted on in the same review.
        UpdateStrikePackage(hq, state);
        // After the packages are resolved (so a moved airframe is told the right thing at once) and
        // before the sync, which then agrees with the move rather than undoing it.
        RetaskToContact(hq, state);
        SyncAirTasks(hq, state);
        SweepIdleAirframes(hq, state);
    }
}
