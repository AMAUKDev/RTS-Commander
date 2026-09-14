using System;
using System.Collections.Generic;
using System.Reflection;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    private static bool TryFindBestLoadout(
        AircraftDefinition definition,
        FactionHQ hq,
        AirCommandMode mode,
        out StandardLoadout loadout,
        out float score)
    {
        loadout = null!;
        score = 0f;
        StandardLoadout[] standardLoadouts = definition.aircraftParameters.StandardLoadouts;
        Aircraft? aircraftPrefab = definition.unitPrefab.GetComponent<Aircraft>();
        if (standardLoadouts == null || aircraftPrefab?.weaponManager == null)
        {
            return false;
        }

        for (int i = 0; i < standardLoadouts.Length; i++)
        {
            StandardLoadout candidate = standardLoadouts[i];
            if (candidate == null || candidate.disabled || candidate.loadout == null)
            {
                continue;
            }

            float candidateScore = ScoreLoadout(candidate.loadout, mode, definition);
            if (candidateScore > score)
            {
                score = candidateScore;
                loadout = candidate;
            }
        }

        return loadout != null && score > 0f;
    }

    /// <summary>
    /// The ordnance a commander-built close-air-support loadout prefers above every other
    /// air-to-ground store (design.md, smarter-air-wing_20260914 Section 1; user decision
    /// 2026-09-14: "a strong preference for AGM-68 and AGM-48 ordnance in CAS missions"). Matched on
    /// the designation prefix the game's own asset data carries — the missile definitions read
    /// <c>AGM-48 </c> (with a trailing space) and <c>AGM-68</c>, and the mounts read
    /// <c>AGM-48 x4</c>, <c>AGM-68 x2</c> and so on — so a prefix match catches every rack size and
    /// the unspaced spellings catch a future rename. Verified against the strings in
    /// <c>NuclearOption_Data/resources.assets</c>, 2026-09-14.
    /// </summary>
    private static readonly string[][] PreferredCasOrdnance =
    {
        new[] { "AGM-68", "AGM68" },
        new[] { "AGM-48", "AGM48" },
    };

    /// <summary>
    /// What a preferred CAS store adds to its mount's score. Additive and far above the whole range
    /// an ordinary air-to-ground mount scores (effectiveness x sqrt(stores) x delivery x range
    /// reaches about 10 for the heaviest rack on the roster), so a hardpoint group that can carry an
    /// AGM-68 or an AGM-48 always picks it. It is applied inside the scorer, which only ever ranks
    /// mounts the hardpoint set already offers: it can never make an incompatible store legal, and
    /// the strip's own acceptance test (<see cref="IsCompatibleAirbase"/> and the mount checks in
    /// <c>ValidateSelectedLoadout</c>) runs afterwards either way.
    /// </summary>
    private const float PreferredCasOrdnanceBonus = 100f;

    /// <summary>How much better these missiles deliver their warhead than an ordinary store of the
    /// same effectiveness — the standing multiplier the scorer has carried since the AIR window was
    /// written (lock-on after launch, erratic terminal manoeuvring, launch from behind terrain).
    /// Unchanged in value; it only moved here so the designation table has one home.</summary>
    private const float PreferredCasOrdnanceDelivery = 1.42f;

    /// <summary>Where this mount sits in the <see cref="PreferredCasOrdnance"/> table — 0 is the
    /// most preferred tier (AGM-68), 1 the next (AGM-48), -1 not preferred at all. A store that is
    /// nuclear, a jammer, or scores nothing against ground is never preferred however it is named:
    /// the recon variant of the AGM-48 carries a sensor instead of a warhead. Internal (one-word
    /// widening, Reuse rule 4): the loadout scorer ranks with it and the enemy commander's roster
    /// line reports with it — one definition of "preferred CAS ordnance", two callers.</summary>
    internal static int PreferredCasOrdnanceRank(WeaponMount? mount)
    {
        WeaponInfo? info = mount?.info;
        if (info == null || info.nuclear || info.jammer || info.effectiveness.antiSurface <= 0.05f)
        {
            return -1;
        }

        string identity = GetWeaponIdentity(mount, info);
        for (int tier = 0; tier < PreferredCasOrdnance.Length; tier++)
        {
            if (ContainsWeaponToken(identity, PreferredCasOrdnance[tier]))
            {
                return tier;
            }
        }

        return -1;
    }

    /// <summary>Whether this mount carries any preferred CAS ordnance at all.</summary>
    internal static bool IsPreferredCasOrdnance(WeaponMount? mount)
    {
        return PreferredCasOrdnanceRank(mount) >= 0;
    }

    /// <summary>The commander's own CAS ranking for one mount — the number
    /// <see cref="AutoConfigureRoleLoadout"/> picks each hardpoint group's store by. Internal
    /// (one-word widening): the enemy commander's self-check asserts the ordnance order with it.</summary>
    internal static float ScoreCasMountForCommander(WeaponMount mount)
    {
        return ScoreMount(mount, AirCommandMode.Cas, preferCasOrdnance: true);
    }

    /// <summary>Whether a built loadout carries any preferred CAS ordnance at all — what the launch
    /// note and the roster line report.</summary>
    internal static bool LoadoutHasPreferredCasOrdnance(Loadout? loadout)
    {
        if (loadout?.weapons == null)
        {
            return false;
        }

        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            if (IsPreferredCasOrdnance(loadout.weapons[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What the commander's own CAS loadout for <paramref name="definition"/> ends up carrying:
    /// whether it got one of the <see cref="PreferredCasOrdnance"/> designations, and the name of
    /// the heaviest-scoring air-to-ground store it carries either way. False when the airframe has
    /// no ground-attack loadout at all. What the <c>Air roster</c> line reports (design SS1:
    /// "AGM-68/AGM-48 available" or "none — falls back to &lt;best A/G&gt;").
    /// </summary>
    internal static bool TryDescribeCasOrdnance(
        AircraftDefinition definition, FactionHQ hq, out bool preferred, out string bestStore)
    {
        preferred = false;
        bestStore = string.Empty;
        if (!TryBuildRoleLoadout(definition, hq, AirCommandMode.Cas, preferArhMissiles: false, out Loadout loadout, out _))
        {
            return false;
        }

        preferred = LoadoutHasPreferredCasOrdnance(loadout);
        float best = 0f;
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount == null)
            {
                continue;
            }

            float score = ScoreMount(mount, AirCommandMode.Cas);
            if (score > best)
            {
                best = score;
                bestStore = GetWeaponTypeName(mount);
            }
        }

        return true;
    }

    /// <param name="preferCasOrdnance">Rank <see cref="PreferredCasOrdnance"/> above every other
    /// air-to-ground store (design SS1). Only the commander's own loadout builder passes true: the
    /// player's AIR window keeps its unweighted weapon list so the preference never reorders what a
    /// human is choosing from.</param>
    private static float ScoreLoadout(
        Loadout loadout,
        AirCommandMode mode,
        AircraftDefinition? aircraftDefinition = null,
        bool preferCasOrdnance = false)
    {
        float score = 0f;
        bool hasRadar = false;
        bool hasJammer = false;
        float radarRange = 0f;
        float jammerRange = 0f;
        int remainingLaserTargets = GetLaserTargetCapacity(loadout, aircraftDefinition);
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount == null)
            {
                continue;
            }

            WeaponInfo? info = mount.info;
            switch (mode)
            {
                case AirCommandMode.AwacsJammer:
                    SpecialAirSystem specialSystem = GetSpecialAirSystem(mount);
                    if (specialSystem == SpecialAirSystem.Radar)
                    {
                        hasRadar = true;
                        radarRange = Mathf.Max(radarRange, GetSpecialSystemRange(mount));
                    }
                    if (specialSystem == SpecialAirSystem.RadarJammer)
                    {
                        hasJammer = true;
                        jammerRange = Mathf.Max(jammerRange, GetSpecialSystemRange(mount));
                    }
                    break;

                case AirCommandMode.Cas:
                    if (info != null
                        && !info.nuclear
                        && !info.jammer
                        && !IsStrategicStrikeWeapon(info, mount)
                        && info.effectiveness.antiSurface > 0.05f)
                    {
                        score += ScoreConventionalWeapon(
                            info.effectiveness.antiSurface, mount, info, ref remainingLaserTargets);
                        int preferredTier = preferCasOrdnance ? PreferredCasOrdnanceRank(mount) : -1;
                        if (preferredTier >= 0)
                        {
                            // One tier above the next, both far above every ordinary store: the
                            // AGM-68 wins a hardpoint the AGM-48 could also fill, and either wins
                            // over anything else the group offers.
                            score += PreferredCasOrdnanceBonus * (PreferredCasOrdnance.Length - preferredTier);
                        }
                    }
                    break;

                case AirCommandMode.AirGuard:
                    if (info != null
                        && !info.jammer
                        && info.effectiveness.antiAir > 0.05f
                        && info.effectiveness.antiSurface <= 0.05f)
                    {
                        score += ScoreConventionalWeapon(
                            info.effectiveness.antiAir, mount, info, ref remainingLaserTargets);
                    }
                    break;

                case AirCommandMode.Arad:
                    if (info != null && IsAradWeapon(info))
                    {
                        score += ScoreConventionalWeapon(
                            Mathf.Max(info.effectiveness.antiRadar, 0.5f), mount, info, ref remainingLaserTargets);
                    }
                    break;

                case AirCommandMode.StrategicStrike:
                    if (info != null && IsStrategicStrikeWeapon(info, mount))
                    {
                        score += 20f + ScoreConventionalWeapon(
                            Mathf.Max(info.effectiveness.antiSurface, info.effectiveness.antiRadar, 0.5f),
                            mount,
                            info,
                            ref remainingLaserTargets);
                    }
                    break;
            }
        }

        if (mode == AirCommandMode.AwacsJammer)
        {
            if (hasRadar) score += 16f + radarRange / 10000f;
            if (hasJammer) score += 8f + jammerRange / 10000f;
        }

        return score;
    }

    private static float ScoreConventionalWeapon(
        float effectiveness,
        WeaponMount mount,
        WeaponInfo info,
        ref int remainingLaserTargets)
    {
        int usefulStores = Mathf.Max(mount.ammo, 1);
        if (info.laserGuided)
        {
            usefulStores = Mathf.Min(usefulStores, Mathf.Max(remainingLaserTargets, 0));
            remainingLaserTargets = Mathf.Max(remainingLaserTargets - usefulStores, 0);
            if (usefulStores == 0)
            {
                return 0f;
            }
        }

        // Guns use round counts several orders of magnitude above discrete stores.
        float quantity = info.gun ? 1f : Mathf.Sqrt(usefulStores);
        float delivery = 1f;
        if (info.gun) delivery *= 0.5f;
        if (info.missile) delivery *= 1.12f;
        if (info.bomb) delivery *= 0.88f;
        if (info.glideBomb) delivery *= 1.28f;
        if (info.overHorizon) delivery *= 1.2f;
        if (info.laserGuided) delivery *= 0.78f;

        if (info.missile)
        {
            float speed = info.GetMaxSpeed();
            if (speed > 0f)
            {
                delivery *= speed switch
                {
                    < 250f => Mathf.Lerp(0.4f, 0.58f, speed / 250f),
                    < 400f => Mathf.Lerp(0.58f, 0.82f, (speed - 250f) / 150f),
                    < 700f => Mathf.Lerp(0.82f, 1.08f, (speed - 400f) / 300f),
                    _ => Mathf.Clamp(1.08f + (speed - 700f) / 3000f, 1.08f, 1.25f),
                };
            }
        }

        float maxRange = Mathf.Max(info.targetRequirements.maxRange, 1000f);
        float rangeFactor = Mathf.Clamp(Mathf.Sqrt(maxRange / 10000f), 0.72f, 1.45f);
        string identity = GetWeaponIdentity(mount, info);
        // One definition of the preferred designations (Reuse rule 4): this delivery nudge and the
        // commander's dominant CAS bonus read the same table, so a rename lands in both at once.
        for (int tier = 0; tier < PreferredCasOrdnance.Length; tier++)
        {
            if (ContainsWeaponToken(identity, PreferredCasOrdnance[tier]))
            {
                delivery *= PreferredCasOrdnanceDelivery;
                break;
            }
        }
        if (ContainsWeaponToken(identity, "AGM-99", "AGM99")) delivery *= 0.4f;
        if (ContainsWeaponToken(identity, "KINGPIN")) delivery *= 1.35f;
        if (info.glideBomb && ContainsWeaponToken(identity, "CLUSTER")) delivery *= 1.22f;

        return effectiveness * quantity * delivery * rangeFactor;
    }

    private static int GetLaserTargetCapacity(Loadout loadout, AircraftDefinition? definition)
    {
        int capacity = 0;
        LaserDesignator? builtIn = definition?.unitPrefab?.GetComponentInChildren<LaserDesignator>(true);
        if (builtIn != null)
        {
            capacity = Mathf.Max(capacity, builtIn.GetMaxTargets());
        }
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            LaserDesignator? mounted = loadout.weapons[i]?.prefab?.GetComponentInChildren<LaserDesignator>(true);
            if (mounted != null)
            {
                capacity = Mathf.Max(capacity, mounted.GetMaxTargets());
            }
        }
        return Mathf.Max(capacity, 1);
    }

    private static bool IsStrategicStrikeWeapon(WeaponInfo info, WeaponMount? mount = null)
    {
        if (info.strategic)
        {
            return true;
        }
        GameObject? prefab = info.weaponPrefab;
        if (prefab != null
            && (prefab.GetComponent<OpticalSeekerCruiseMissile>() != null
                || prefab.GetComponent<BallisticMissileGuidance>() != null))
        {
            return true;
        }
        return ContainsWeaponToken(
            GetWeaponIdentity(mount, info),
            "CRUISE",
            "TBM",
            "BALLISTIC",
            "TUSKO-B",
            "TUSKO B",
            "TUSKOB");
    }

    private static string GetWeaponIdentity(WeaponMount? mount, WeaponInfo info)
    {
        return string.Join("|", new[]
        {
            info.weaponName,
            info.shortName,
            info.name,
            mount?.mountName,
            mount?.jsonKey,
            mount?.name,
        });
    }

    private static bool ContainsWeaponToken(string identity, params string[] tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (identity.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsStationEligible(WeaponStation station, AirCommandMode mode)
    {
        WeaponInfo? info = station.WeaponInfo;
        if (info == null)
        {
            return false;
        }

        return mode switch
        {
            AirCommandMode.AwacsJammer => info.jammer,
            AirCommandMode.Cas => !info.nuclear
                && !info.jammer
                && !IsStrategicStrikeWeapon(info)
                && info.effectiveness.antiSurface > 0.05f,
            // Dual-role weapons remain manually usable, but ScoreLoadout keeps
            // them out of the recommended Air Superiority section.
            AirCommandMode.AirGuard => !info.jammer && info.effectiveness.antiAir > 0.05f,
            AirCommandMode.Arad => IsAradWeapon(info),
            AirCommandMode.StrategicStrike => IsStrategicStrikeWeapon(info),
            _ => false,
        };
    }

    private static bool IsTargetEligible(Unit target, AirCommandMode mode, bool targetOrdnance)
    {
        return mode switch
        {
            AirCommandMode.AwacsJammer => target.HasRadarEmission(),
            AirCommandMode.Cas => target is GroundVehicle || target is Ship || target is Building,
            AirCommandMode.AirGuard => target is Aircraft || (targetOrdnance && target is Missile),
            AirCommandMode.Arad => target is not Aircraft && target.HasRadarEmission(),
            AirCommandMode.StrategicStrike => target is GroundVehicle || target is Ship || target is Building,
            _ => false,
        };
    }

    private static bool IsAradWeapon(WeaponInfo info)
    {
        return info.targetRequirements.minRadar > 0f
            || info.effectiveness.antiRadar > Mathf.Max(info.effectiveness.antiAir, 0.05f)
            || info.weaponPrefab?.GetComponentInChildren<ARMSeeker>(true) != null
            || info.weaponName?.IndexOf("ARAD", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Whether <paramref name="info"/> is an active-radar-homing air-to-air missile — the game's own
    /// <c>ARHSeeker</c> component on the missile prefab (<c>Missile.Awake</c> reads its seeker off its
    /// own GameObject, verified by decompile), on a weapon the game types as a missile and scores
    /// against air. The air test is the same 0.05 the AIR window's loadout label uses for its <c>A/A</c>
    /// tag — one threshold, two callers. Internal (one-word widening, Reuse rule 4): the enemy
    /// commander's home-CAP buy classifies a fighter's loadout with the same test — the CAP must be
    /// flown by an airframe that can shoot at something it has not been handed (user decision
    /// 2026-09-14: home-CAP fighters carry an ARH missile).
    /// </summary>
    internal static bool IsArhAirToAirMissile(WeaponInfo info)
    {
        return info != null
            && info.missile
            && info.effectiveness.antiAir > 0.05f
            && info.weaponPrefab?.GetComponentInChildren<ARHSeeker>(true) != null;
    }

    private static void NormalizeLoadoutLength(Loadout loadout, AircraftDefinition definition)
    {
        Aircraft? aircraft = definition.unitPrefab != null
            ? definition.unitPrefab.GetComponent<Aircraft>()
            : null;
        int hardpointCount = aircraft?.weaponManager?.hardpointSets?.Length ?? loadout.weapons.Count;
        while (loadout.weapons.Count < hardpointCount)
        {
            loadout.weapons.Add(null!);
        }
        if (loadout.weapons.Count > hardpointCount)
        {
            loadout.weapons.RemoveRange(hardpointCount, loadout.weapons.Count - hardpointCount);
        }
    }

    /// <summary>
    /// True when this airframe is flown by a fixed-wing AI pilot, which is the only kind an Air
    /// Command mission can steer. Everything the mod does to a commanded aircraft — the idle-timer
    /// prefix, the route destination, the altitude hold — is a patch on
    /// <c>AIPilotCombatModes</c>, and only <c>PilotType.Plane</c> uses that state machine:
    /// <c>Pilot.SetStartingAiState</c> puts helicopters and tiltwings on <c>AIHeloCombatState</c>
    /// instead. Handing one of those a mission applies the target half of it and none of the flying
    /// half, which is how the enemy's VTOLs ended up nosing into the ground shortly after takeoff.
    /// </summary>
    internal static bool HasPlanePilot(AircraftDefinition definition)
    {
        return definition.unitPrefab != null
            && HasPlanePilot(definition.unitPrefab.GetComponent<Aircraft>());
    }

    internal static bool HasPlanePilot(Aircraft? aircraft)
    {
        if (aircraft?.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            if (aircraft.pilots[i] != null && aircraft.pilots[i].pilotType == Pilot.PilotType.Plane)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when this airframe flies like a helicopter — its prefab carries a rotary pilot. The
    /// definition-level twin of <see cref="IsRotaryPilot"/> (one definition of "flies like a
    /// helicopter", read off the prefab instead of a live airframe), for the rotary CAS buy
    /// (design.md, smarter-air-wing_20260914 Section 2).
    /// </summary>
    internal static bool IsRotaryAirframe(AircraftDefinition definition)
    {
        Aircraft? prefab = definition.unitPrefab != null ? definition.unitPrefab.GetComponent<Aircraft>() : null;
        if (prefab?.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < prefab.pilots.Length; i++)
        {
            if (prefab.pilots[i] != null && IsRotaryPilot(prefab.pilots[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the Basegame AI has a flight model for this airframe at all.
    /// <c>Pilot.SetStartingAiState</c> hands <c>PilotType.Plane</c> to <c>AIPilotCombatModes</c> and
    /// <c>Helo</c>/<c>Tiltwing</c> to <c>AIHeloCombatState</c>, and gives <c>PilotType.VTOL</c>
    /// **nothing at all** — a VTOL with an AI pilot has no state, no autopilot input and falls out
    /// of the sky. Nothing the mod can do fixes that, so nothing may buy one.
    /// </summary>
    internal static bool CanAiFly(AircraftDefinition definition)
    {
        Aircraft? prefab = definition.unitPrefab != null ? definition.unitPrefab.GetComponent<Aircraft>() : null;
        if (prefab?.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < prefab.pilots.Length; i++)
        {
            Pilot? pilot = prefab.pilots[i];
            if (pilot != null
                && (pilot.pilotType == Pilot.PilotType.Plane
                    || pilot.pilotType == Pilot.PilotType.Helo
                    || pilot.pilotType == Pilot.PilotType.Tiltwing))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="airbase"/> is one this faction holds whose hangars carry
    /// <paramref name="definition"/> in their authored list. Internal (one-word widening, Reuse rule
    /// 4): this is THE airbase acceptance test — the AIR window's launch gate and the AI buyers'
    /// candidate gate read the same answer, so a commander can never be refused an aircraft the
    /// player's own window launches from the same strip.</summary>
    internal static bool IsCompatibleAirbase(Airbase? airbase, FactionHQ hq, AircraftDefinition definition)
    {
        if (airbase == null || airbase.disabled || !airbase.GetAvailableAircraft().Contains(definition))
        {
            return false;
        }

        foreach (Airbase ownedAirbase in hq.GetAirbases())
        {
            if (ReferenceEquals(ownedAirbase, airbase))
            {
                return true;
            }
        }

        return false;
    }

    private static float GetMissionRadius(AirCommandMode mode)
    {
        return mode switch
        {
            AirCommandMode.Cas => CommanderSettings.CasRadiusKm * 1000f,
            AirCommandMode.AirGuard => CommanderSettings.AirGuardRadiusKm * 1000f,
            AirCommandMode.Arad => CommanderSettings.AradRadiusKm * 1000f,
            AirCommandMode.StrategicStrike => CommanderSettings.StrikeRadiusKm * 1000f,
            _ => CommanderSettings.AwacsRadiusKm * 1000f,
        };
    }

    private static void SetMissionRadius(AirCommandMode mode, float radiusKm)
    {
        switch (mode)
        {
            case AirCommandMode.Cas: CommanderSettings.CasRadiusKm = radiusKm; break;
            case AirCommandMode.AirGuard: CommanderSettings.AirGuardRadiusKm = radiusKm; break;
            case AirCommandMode.Arad: CommanderSettings.AradRadiusKm = radiusKm; break;
            case AirCommandMode.StrategicStrike: CommanderSettings.StrikeRadiusKm = radiusKm; break;
            default: CommanderSettings.AwacsRadiusKm = radiusKm; break;
        }
    }
}
