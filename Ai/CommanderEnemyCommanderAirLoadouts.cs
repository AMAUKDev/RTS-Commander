using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Loadout scoring and role capability. Split out of <c>CommanderEnemyCommanderAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past three thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>
    /// The AIR window's AIR SUPERIORITY score of the best air-to-air loadout this airframe's own
    /// picker can build for it — 0 when it has no A/A-capable option at all. Memoized per mission.
    /// This is THE capability test (user decision 2026-09-14): a CAP or escort candidate is an
    /// airframe whose loadout can fight air, not one whose role data says "Fighter" — the identity
    /// test excluded a Compass carrying Scythes, which is exactly the CAP a highway-strip commander
    /// can actually field.
    /// </summary>
    private static float CapLoadoutScore(FactionHQ hq, CommanderState state, AircraftDefinition definition)
    {
        if (state.CapScores.TryGetValue(definition, out float cached))
        {
            return cached;
        }

        float score = CommanderAirCommandService.TryBuildRoleLoadout(
            definition, hq, CommanderAirCommandService.AirCommandMode.AirGuard,
            preferArhMissiles: true, out _, out float built)
            ? built
            : 0f;
        state.CapScores[definition] = score;
        return score;
    }

    /// <summary>The same read on the CAS scorer — 0 when the airframe has no ground-attack option.
    /// Memoized per mission.</summary>
    private static float CasLoadoutScore(FactionHQ hq, CommanderState state, AircraftDefinition definition)
    {
        if (state.CasScores.TryGetValue(definition, out float cached))
        {
            return cached;
        }

        float score = CommanderAirCommandService.TryBuildRoleLoadout(
            definition, hq, CommanderAirCommandService.AirCommandMode.Cas,
            preferArhMissiles: false, out _, out float built)
            ? built
            : 0f;
        state.CasScores[definition] = score;
        return score;
    }

    /// <summary>The same read on the ARAD scorer — 0 when the airframe can mount no anti-radiation
    /// weapon at all. Memoized per mission (design.md, smarter-air-wing_20260914 Section 5).</summary>
    private static float AradLoadoutScore(FactionHQ hq, CommanderState state, AircraftDefinition definition)
    {
        if (state.AradScores.TryGetValue(definition, out float cached))
        {
            return cached;
        }

        float score = CommanderAirCommandService.TryBuildRoleLoadout(
            definition, hq, CommanderAirCommandService.AirCommandMode.Arad,
            preferArhMissiles: false, out _, out float built)
            ? built
            : 0f;
        state.AradScores[definition] = score;
        return score;
    }

    /// <summary>Whether the commander's own builder can give this airframe the game's radar pod —
    /// the AWACS candidate test (Section 4). Memoized per mission.</summary>
    private static bool HasRadarLoadout(FactionHQ hq, CommanderState state, AircraftDefinition definition)
    {
        if (state.AwacsCapable.TryGetValue(definition, out bool cached))
        {
            return cached;
        }

        bool capable = CommanderAirCommandService.TryBuildRoleLoadout(
                definition, hq, CommanderAirCommandService.AirCommandMode.AwacsJammer,
                preferArhMissiles: false, out Loadout loadout, out _)
            && CommanderAirCommandService.LoadoutHasRadarSystem(loadout);
        state.AwacsCapable[definition] = capable;
        return capable;
    }

    /// <summary>Whether this airframe can fight air at all — the best A/A loadout its picker can
    /// build scores above zero. Internal (one-word widening, Reuse rule 4): the operations claim and
    /// the sortie fill bind by the SAME capability the buy bought on, or a CAP-bought Compass would
    /// bind to the first CAS sortie instead of the CAP it was bought for.</summary>
    internal static bool IsAntiAirCapable(FactionHQ hq, AircraftDefinition definition)
    {
        CommanderEnemyCommanderService? service = Instance;
        if (service != null && service.states.TryGetValue(hq, out CommanderState state))
        {
            return CapLoadoutScore(hq, state, definition) > 0f;
        }

        return CommanderAirCommandService.TryBuildRoleLoadout(
            definition, hq, CommanderAirCommandService.AirCommandMode.AirGuard,
            preferArhMissiles: true, out _, out float score) && score > 0f;
    }

    /// <summary>
    /// Whether an airframe can fill <paramref name="role"/> at all — capability, not identity.
    /// Internal (one-word widening, Reuse rule 4): the wing's own count reads it, and so do the
    /// operations claim and the sortie fill, so an airframe is only ever bound to a sortie it can
    /// actually fly — a radar airframe to the AWACS, an anti-radiation one to an ARAD sortie.
    /// </summary>
    internal static bool FillsAirRole(FactionHQ hq, AircraftDefinition definition, AirRole role)
    {
        // The structural exclusion, ahead of every capability test: a transport, an airframe with no
        // plane pilot, or a helicopter asked to fly air superiority is not a candidate whatever its
        // loadout can carry. This is the gate the owned-airframe binding, the registration claim,
        // the sortie fill, the retask and the role counts all read (user report, 2026-09-14: a
        // UH-90 Ibis bound to a platoon's CAP).
        if (!MayFillRole(definition, role))
        {
            return false;
        }

        CommanderEnemyCommanderService? service = Instance;
        // The radar aeroplane binds to the AWACS station and nothing else — the same reservation
        // the purchase gate applies (RadarAirframeMayFill), so a released or stray Medusa is never
        // put on a patrol, a strike or a suppression slot.
        if (!RadarAirframeMayFill(role)
            && service != null
            && service.states.TryGetValue(hq, out CommanderState radarState)
            && HasRadarLoadout(hq, radarState, definition))
        {
            return false;
        }

        return role switch
        {
            AirRole.Transport => GetAirRole(definition) == AirRole.Transport,
            // The same air-superiority refusal as the buy, so the claim, the sortie fill, the
            // retask and the role counts all agree with it (see MayFlyAirSuperiority).
            AirRole.Fighter => IsAntiAirCapable(hq, definition) && MayFlyAirSuperiority(definition),
            AirRole.Awacs => service != null
                && service.states.TryGetValue(hq, out CommanderState awacsState)
                && HasRadarLoadout(hq, awacsState, definition),
            AirRole.Arad => service != null
                && service.states.TryGetValue(hq, out CommanderState aradState)
                && AradLoadoutScore(hq, aradState, definition) > 0f,
            AirRole.RotaryCas => IsAntiSurfaceCapable(hq, definition)
                && CommanderAirCommandService.IsRotaryAirframe(definition),
            _ => IsAntiSurfaceCapable(hq, definition),
        };
    }

    /// <summary>
    /// Which roles a radar/EW airframe may fill, pure: the AWACS station and the transport role
    /// (which it never has anyway) — never a patrol, a strike, rotary CAS or a suppression sortie
    /// (design.md, airframe-selection_20260914 Section 1: "radar/EW special-system airframes are
    /// excluded from both"). One rule read by the purchase gate, the binding gate and the roster
    /// line, so they cannot disagree about it again.
    /// </summary>
    internal static bool RadarAirframeMayFill(AirRole role)
    {
        return role is AirRole.Awacs or AirRole.Transport;
    }

    /// <summary>
    /// The binding rule, pure (user decision 2026-09-14: "ARADs are not using the correct
    /// ordinance"): an airframe may be put on a sortie's main slot when its TYPE can fill the role
    /// and — for the suppression role alone — it is carrying anti-radiation stores right now.
    /// <para>The type test is the right question for a purchase and the wrong one for a binding.
    /// An FS-12 Revoker could be given anti-radiation missiles, so it passes the type test; the one
    /// bought for the home patrol in the 2026-09-14 match was carrying air-to-air missiles, and was
    /// bound to an anti-radiation strike over a belt of twenty-three launchers regardless. Every
    /// other role's loadout is either irrelevant to the job (a fighter bound as a fighter) or
    /// already forced at the buy.</para>
    /// Pure, for the self-check.
    /// </summary>
    internal static bool MayBindToRole(AirRole role, bool typeFillsRole, bool carriesAntiRadiation)
    {
        return typeFillsRole && (role != AirRole.Arad || carriesAntiRadiation);
    }

    /// <summary>
    /// Whether this AIRCRAFT can fill <paramref name="role"/> right now — <see cref="FillsAirRole"/>
    /// on its type, plus the live-stores rule above. Every path that binds a real aeroplane to a
    /// sortie reads this rather than the type test: the claim, the fill, the retask to contact, the
    /// released-airframe retask and the unbound-airframe search (Reuse rule 4, one definition).
    /// </summary>
    internal static bool FillsAirRoleNow(FactionHQ hq, Aircraft? aircraft, AirRole role)
    {
        if (aircraft == null || aircraft.disabled || aircraft.definition is not AircraftDefinition definition)
        {
            return false;
        }

        return MayBindToRole(
            role,
            FillsAirRole(hq, definition, role),
            CommanderAirCommandService.CarriesAntiRadiation(aircraft));
    }

    /// <summary>
    /// Whether any airframe on the roster can fill <paramref name="role"/> from a strip this
    /// commander holds — the "is this sortie kind possible at all" test the AWACS demand reads
    /// before it opens a sortie nothing can ever fly (design.md, smarter-air-wing_20260914
    /// Section 4). The deadlock valve's shape (<see cref="HasAnyCapCandidate"/>) widened to any
    /// role: one definition, two callers.
    /// </summary>
    internal static bool HasRoleCandidate(FactionHQ hq, AirRole role)
    {
        CommanderEnemyCommanderService? service = Instance;
        if (service == null || !service.states.TryGetValue(hq, out CommanderState state))
        {
            return false;
        }

        service.RefreshAirCatalog();
        foreach (AircraftDefinition definition in service.airCatalog)
        {
            if (definition != null
                && PassesRoleCapability(hq, state, definition, role)
                && FindAcceptingAirbase(hq, definition) != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether this airframe can attack ground at all — the CAS-scored twin of
    /// <see cref="IsAntiAirCapable"/>, for the CAS side of the claim and the fill.</summary>
    internal static bool IsAntiSurfaceCapable(FactionHQ hq, AircraftDefinition definition)
    {
        CommanderEnemyCommanderService? service = Instance;
        if (service != null && service.states.TryGetValue(hq, out CommanderState state))
        {
            return CasLoadoutScore(hq, state, definition) > 0f;
        }

        return CommanderAirCommandService.TryBuildRoleLoadout(
            definition, hq, CommanderAirCommandService.AirCommandMode.Cas,
            preferArhMissiles: false, out _, out float score) && score > 0f;
    }

    /// <summary>
    /// Whether <paramref name="definition"/> is a candidate for <paramref name="role"/> — capability,
    /// not identity (user decision 2026-09-14). Combat roles additionally require a plane pilot:
    /// every combat buy is tasked through Air Command, whose <c>TryTaskAiAircraft</c> already
    /// refuses anything without one, so a rotary or VTOL combat buy would be money spent on an
    /// airframe nothing can fly for us. Transports keep the identity test (carrying capacity IS
    /// their capability) and the looser <see cref="CommanderAirCommandService.CanAiFly"/> gate,
    /// because the Basegame's own helo state flies them.
    /// </summary>
    private static bool PassesRoleCapability(FactionHQ hq, CommanderState state, AircraftDefinition definition, AirRole role)
    {
        // The faction gate, first (2026-09-16). The game has no per-faction aircraft roster of its
        // own, so this is the mod's table and nothing else enforces it — and it has to be enforced
        // HERE rather than left to the engine, because the wing's usual route into the sky spawns
        // an aircraft already airborne over the base and never asks the engine anything. One line
        // covers every computer air decision: the buy, the fund ceiling, the role-candidate test,
        // the rotary launch distance and the loadout choice all pass through this gate.
        if (!CommanderFactionRoster.MayFlyAircraft(hq, definition))
        {
            return false;
        }

        // The shared candidate gate — the structural exclusion, the plane-pilot test and the
        // no-helicopters-on-patrol rule — so the buy and the binding can never disagree about what
        // is a candidate. The transport role ends here: carrying capacity IS its capability.
        if (!MayFillRole(definition, role))
        {
            return false;
        }

        if (role == AirRole.Transport)
        {
            return true;
        }

        // The radar aeroplane is the AWACS and nothing else (design.md,
        // airframe-selection_20260914 Section 1: "radar/EW special-system airframes are excluded
        // from both"). The EW-25 Medusa reads as a Fighter on its role identity and can hang an
        // air-to-air missile, so without this it would be a CAP candidate at 145 — spending the
        // patrol's whole slice on the one airframe the wing needs standing off behind the front.
        // ... and not the anti-radiation role either (fix, 2026-09-14): the Medusa can hang
        // ARAD-116s, so once suppression sorties bought through AirRole.Arad it was chosen for them
        // — 39 launched in one hour of the 2026-09-14 match, 5,655 spent on radar aeroplanes flown
        // into launcher belts, four of them as fallback fighters.
        if (!RadarAirframeMayFill(role) && HasRadarLoadout(hq, state, definition))
        {
            return false;
        }

        return role switch
        {
            // The air-superiority refusal, applied here so the buy can never pick a
            // ground-attack specialist for a patrol or an escort (see MayFlyAirSuperiority).
            AirRole.Fighter => CapLoadoutScore(hq, state, definition) > 0f && MayFlyAirSuperiority(definition),
            AirRole.Awacs => HasRadarLoadout(hq, state, definition),
            AirRole.Arad => AradLoadoutScore(hq, state, definition) > 0f,
            // The rotary CAS pass is the Strike pass with the flight model added (design SS2): the
            // airframe has to attack ground AND fly like a helicopter.
            AirRole.RotaryCas => CasLoadoutScore(hq, state, definition) > 0f
                && CommanderAirCommandService.IsRotaryAirframe(definition),
            _ => CasLoadoutScore(hq, state, definition) > 0f,
        };
    }

    /// <summary>
    /// The loadout a combat buy launches with — the AIR window's own scorer, never the airframe's
    /// default (user decision 2026-09-14): max air-to-air missiles with an active-radar missile
    /// preferred for a CAP/escort buy, max ground-attack for a strike buy. The score memo already
    /// proved the build succeeds, so this cannot come back empty for a candidate that passed
    /// <see cref="PassesRoleCapability"/>.
    /// </summary>
    private static Loadout? BuildRoleLoadout(FactionHQ hq, AircraftDefinition definition, AirRole role)
    {
        bool isAirToAir = role == AirRole.Fighter;
        CommanderAirCommandService.AirCommandMode mode = role switch
        {
            AirRole.Fighter => CommanderAirCommandService.AirCommandMode.AirGuard,
            AirRole.Awacs => CommanderAirCommandService.AirCommandMode.AwacsJammer,
            AirRole.Arad => CommanderAirCommandService.AirCommandMode.Arad,
            _ => CommanderAirCommandService.AirCommandMode.Cas,
        };
        return CommanderAirCommandService.TryBuildRoleLoadout(
            definition,
            hq,
            mode,
            preferArhMissiles: isAirToAir,
            out Loadout loadout,
            out _)
            ? loadout
            : null;
    }

    /// <summary>
    /// Whether ANY air-to-air-capable airframe can launch from a base this commander holds — the
    /// ladder's deadlock valve (user follow-up, 2026-09-14), read after the capability test and the
    /// window's own strip acceptance. Last resort included: a Cricket CAP beats a deadlocked
    /// ladder. Memoized loadout scores keep the walk cheap after the first review.
    /// </summary>
    private static bool HasAnyCapCandidate(FactionHQ hq)
    {
        return HasRoleCandidate(hq, AirRole.Fighter);
    }

    /// <summary>The names of the bases this commander holds, for the holds line's
    /// CAP-impossible reason — the reader has to be able to see WHICH strips were found
    /// incapable.</summary>
    private static string HeldBaseNames(FactionHQ hq)
    {
        string names = string.Empty;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.disabled)
            {
                continue;
            }

            names = names.Length == 0 ? airbase.name : names + ", " + airbase.name;
        }

        return names.Length > 0 ? names : "no base it holds";
    }

    /// <summary>Whether a built loadout carries an active-radar-homing air-to-air missile — the
    /// launch line's "active-radar loadout" tag, read off the same test the loadout preference used.</summary>
    private static bool LoadoutHasArhMissile(Loadout loadout)
    {
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount?.info != null && CommanderAirCommandService.IsArhAirToAirMissile(mount.info))
            {
                return true;
            }
        }

        return false;
    }
}
