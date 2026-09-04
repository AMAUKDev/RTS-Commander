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

    private static float ScoreLoadout(
        Loadout loadout,
        AirCommandMode mode,
        AircraftDefinition? aircraftDefinition = null)
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
        if (ContainsWeaponToken(identity, "AGM-48", "AGM48", "AGM-68", "AGM68")) delivery *= 1.42f;
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

    private static bool IsCompatibleAirbase(Airbase? airbase, FactionHQ hq, AircraftDefinition definition)
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
