using System;
using System.Collections.Generic;
using System.Reflection;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    private static readonly FieldInfo? JammingRangeFalloffField = typeof(JammingPod).GetField(
        "rangeFalloff", BindingFlags.Instance | BindingFlags.NonPublic);

    private static AirMissionOption? CreateVariableLoadoutOption(
        AircraftDefinition definition,
        FactionHQ hq,
        AirCommandMode mode)
    {
        Aircraft? aircraft = definition.unitPrefab.GetComponent<Aircraft>();
        HardpointSet[]? sets = aircraft?.weaponManager?.hardpointSets;
        if (sets == null || sets.Length == 0)
        {
            return null;
        }

        List<AirHardpointGroup> groups = new();
        for (int i = 0; i < sets.Length; i++)
        {
            List<int> indices = new() { i };
            string label = GetHardpointSetName(sets[i], i);
            if (i + 1 < sets.Length && sets[i + 1].SymmetryWithPrev)
            {
                indices.Add(i + 1);
                if (!string.IsNullOrWhiteSpace(sets[i + 1].SymmetryName))
                {
                    label = sets[i + 1].SymmetryName;
                }
                i++;
            }

            List<WeaponMount> mounts = GetCommonCombatMounts(sets, indices, hq);
            int physicalMounts = 0;
            for (int index = 0; index < indices.Count; index++)
            {
                physicalMounts += Mathf.Max(sets[indices[index]].hardpoints?.Count ?? 0, 1);
            }
            groups.Add(new AirHardpointGroup(label, indices, mounts, physicalMounts));
        }

        AirMissionOption option = new(definition, mode, sets, groups);
        return option;
    }

    private void BuildWeaponOptions()
    {
        weaponOptions.Clear();
        Dictionary<string, int> catalogIndex = new(StringComparer.OrdinalIgnoreCase);
        for (int optionIndex = 0; optionIndex < options.Count; optionIndex++)
        {
            AirMissionOption option = options[optionIndex];
            for (int groupIndex = 0; groupIndex < option.HardpointGroups.Count; groupIndex++)
            {
                List<WeaponMount> mounts = option.HardpointGroups[groupIndex].Mounts;
                for (int mountIndex = 0; mountIndex < mounts.Count; mountIndex++)
                {
                    WeaponMount mount = mounts[mountIndex];
                    string key = GetWeaponTypeKey(mount);
                    if (!catalogIndex.TryGetValue(key, out int existingIndex))
                    {
                        catalogIndex[key] = weaponOptions.Count;
                        weaponOptions.Add(mount);
                    }
                    else if (GetCatalogPreference(mount) > GetCatalogPreference(weaponOptions[existingIndex]))
                    {
                        weaponOptions[existingIndex] = mount;
                    }
                }
            }
        }

        weaponOptions.Sort((left, right) =>
        {
            float leftScore = ScoreMount(left, selectedMode);
            float rightScore = ScoreMount(right, selectedMode);
            bool leftSuitable = leftScore > 0f;
            bool rightSuitable = rightScore > 0f;
            if (leftSuitable != rightSuitable)
            {
                return leftSuitable ? -1 : 1;
            }
            int scoreCompare = rightScore.CompareTo(leftScore);
            return scoreCompare != 0
                ? scoreCompare
                : string.Compare(GetMountName(left), GetMountName(right), StringComparison.OrdinalIgnoreCase);
        });
    }

    private int FindFirstSuitableWeaponIndex()
    {
        for (int i = 0; i < weaponOptions.Count; i++)
        {
            if (ScoreMount(weaponOptions[i], selectedMode) > 0f)
            {
                return i;
            }
        }
        return -1;
    }

    private void ApplySelectedWeaponsAndSort()
    {
        AircraftDefinition? selectedDefinition = SelectedOption?.Definition;
        for (int i = 0; i < options.Count; i++)
        {
            ConfigureOptionLoadout(options[i], SelectedPrimaryWeapon, SelectedSecondaryWeapon, selectedLoadoutBalance);
        }

        options.Sort((left, right) =>
        {
            if (selectedMode == AirCommandMode.AwacsJammer)
            {
                int capability = right.Score.CompareTo(left.Score);
                if (capability != 0) return capability;
            }
            int primary = GetPrimaryWeaponCount(right).CompareTo(GetPrimaryWeaponCount(left));
            if (primary != 0) return primary;
            int secondary = GetSecondaryWeaponCount(right).CompareTo(GetSecondaryWeaponCount(left));
            return secondary != 0
                ? secondary
                : string.Compare(GetAircraftLabel(left.Definition), GetAircraftLabel(right.Definition), StringComparison.OrdinalIgnoreCase);
        });

        selectedOptionIndex = 0;
        if (selectedDefinition != null)
        {
            int preserved = options.FindIndex(option => ReferenceEquals(option.Definition, selectedDefinition));
            if (preserved >= 0 && GetPrimaryWeaponCount(options[preserved]) > 0)
            {
                selectedOptionIndex = preserved;
            }
        }
        RefreshAirbases();
    }

    private static void ConfigureOptionLoadout(
        AirMissionOption option,
        WeaponMount? primary,
        WeaponMount? secondary,
        LoadoutBalance balance)
    {
        for (int i = 0; i < option.HardpointGroups.Count; i++)
        {
            option.HardpointGroups[i].Clear();
        }
        if (primary == null)
        {
            EnsureFixedEquipment(option);
            return;
        }

        if (secondary == null)
        {
            SelectGroups(option, primary, FindMaximumWeaponGroups(option, primary, Array.Empty<int>()));
            EnsureFixedEquipment(option);
            return;
        }

        if (balance == LoadoutBalance.Primary)
        {
            List<int> primaryGroups = FindMaximumWeaponGroups(option, primary, Array.Empty<int>());
            SelectGroups(option, primary, primaryGroups);
            SelectGroups(option, secondary, FindMaximumWeaponGroups(option, secondary, primaryGroups));
            EnsureFixedEquipment(option);
            return;
        }

        float targetPrimaryShare = balance == LoadoutBalance.Mixed ? 0.75f : 0.25f;
        int[] allocation = FindBalancedWeaponAllocation(option, primary, secondary, targetPrimaryShare);
        bool hasPrimary = Array.IndexOf(allocation, 1) >= 0;
        bool hasSecondary = Array.IndexOf(allocation, 2) >= 0;
        if (!hasPrimary || !hasSecondary)
        {
            List<int> primaryGroups = FindMaximumWeaponGroups(option, primary, Array.Empty<int>());
            SelectGroups(option, primary, primaryGroups);
            SelectGroups(option, secondary, FindMaximumWeaponGroups(option, secondary, primaryGroups));
            EnsureFixedEquipment(option);
            return;
        }
        for (int i = 0; i < allocation.Length; i++)
        {
            if (allocation[i] == 1) SelectEquivalentMount(option.HardpointGroups[i], primary);
            else if (allocation[i] == 2) SelectEquivalentMount(option.HardpointGroups[i], secondary);
        }
        EnsureFixedEquipment(option);
    }

    private static void EnsureFixedEquipment(AirMissionOption option)
    {
        EnsureMedusaLaser(option);
        if (CommanderSettings.AirIncludeInternalCannons)
        {
            EnsureInternalCannons(option);
        }
    }

    /// <summary>
    /// A copy of <paramref name="loadout"/> with every built-in gun left off, for AI-launched
    /// airframes when INTERNAL CANNONS is off. The commander AIs fly the game's own standard
    /// loadouts, which include the cannon, and a pilot with cannon rounds left counts them as
    /// ordnance: it keeps making gun runs instead of going home when the real weapons are spent.
    /// The same rule the AIR window applies to its own loadouts, applied to the AI's. If stripping
    /// would leave nothing at all (a gun-only trainer), the original is returned — an unarmed
    /// aircraft never finds a target and never comes home either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why nulling the entry is enough, verified in <c>Assembly-CSharp</c> 2026-09-14.</b>
    /// <c>Loadout.weapons</c> is one <c>WeaponMount</c> per <c>HardpointSet</c>, positionally.
    /// <c>WeaponManager.LoadHardpointSet(set, null)</c> takes the <c>RemoveMounts</c> branch instead
    /// of <c>SpawnMounts</c>, and <c>Hardpoint.SpawnMount</c> is the ONLY place a weapon is handed
    /// to <c>WeaponManager.RegisterWeapon</c>. So a null entry means the built-in
    /// <c>Hardpoint.BuiltInWeapons</c> gun is never registered, no <c>WeaponStation</c> is created
    /// for it, and every path the AI fires through — <c>WeaponStation.Ready()</c> and
    /// <c>AIPilotCombatModes</c>' own <c>currentWeaponStation.Ammo &lt;= 0</c> gate — can never
    /// reach it. The gun object itself still self-loads one magazine in <c>Gun.Awake</c> when its
    /// <c>startLoaded</c> flag is set; that magazine is unreachable without a station, which is why
    /// <see cref="StripCannonAmmo"/> exists as a second line rather than a first.
    /// </para>
    /// <para>
    /// What must NOT be done: <c>WeaponMount</c> is a <c>ScriptableObject</c> shared by every
    /// aircraft in the game, so its <c>ammo</c> field is asset data. Writing to it would take the
    /// rounds off the player's aircraft, and off every future spawn, for the rest of the session.
    /// </para>
    /// </remarks>
    internal static Loadout WithoutInternalCannons(Loadout loadout)
    {
        if (loadout?.weapons == null || CommanderSettings.AirIncludeInternalCannons)
        {
            return loadout!;
        }

        Loadout stripped = new();
        int kept = 0;
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            bool gun = IsGunMount(mount);
            stripped.weapons.Add(gun ? null! : mount!);
            if (mount != null && !gun)
            {
                kept++;
            }
        }

        return kept == 0 ? loadout : stripped;
    }

    /// <summary>
    /// Whether one mount is a gun — the single reading of the game's own <c>WeaponInfo.gun</c> flag
    /// that the strip, the survivor test and the launch guard all share (Reuse rule 4). A mount with
    /// no <c>info</c> at all (a tail hook, a plain pylon) is not a gun.
    /// </summary>
    internal static bool IsGunMount(WeaponMount? mount)
    {
        return mount != null && mount.info?.gun == true;
    }

    /// <summary>
    /// Whether <paramref name="loadout"/> still carries a gun mount — the guard the commander's
    /// launch runs over whatever loadout it is about to fly, so a path that never reached
    /// <see cref="WithoutInternalCannons"/> says so in the console instead of taking off armed.
    /// </summary>
    internal static bool LoadoutHasGunMount(Loadout? loadout)
    {
        if (loadout?.weapons == null)
        {
            return false;
        }

        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            if (IsGunMount(loadout.weapons[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Empties the internal cannon of an aircraft the commander has just put in the air, and reports
    /// how many rounds it took off. The second line of defence behind
    /// <see cref="WithoutInternalCannons"/>: the loadout rule prevents the gun ever getting a
    /// weapon station, and this removes the rounds from any station that exists anyway — a stock
    /// loadout the game substituted for a null one, a hangar spawn the mod does not build the
    /// loadout for, an airframe adopted from somewhere else.
    /// <para>
    /// <c>Gun.LoadAmmunition(null)</c> is the game's own unload — it is what
    /// <c>Hardpoint.RemoveMount</c> calls — and it zeroes the gun's magazine count and its
    /// <c>Weapon.ammo</c>. <c>WeaponStation.AccountAmmo</c> then re-totals the station from its
    /// weapons, which is the number every AI fire gate reads. Nothing here touches a
    /// <c>WeaponMount</c> asset.
    /// </para>
    /// Returns 0 when INTERNAL CANNONS is on, when the aircraft has no gun station, or when the
    /// station is already empty.
    /// </summary>
    internal static int StripCannonAmmo(Aircraft? aircraft)
    {
        if (aircraft == null || aircraft.disabled || CommanderSettings.AirIncludeInternalCannons)
        {
            return 0;
        }

        List<WeaponStation>? stations = aircraft.weaponStations;
        if (stations == null)
        {
            return 0;
        }

        int removed = 0;
        for (int i = 0; i < stations.Count; i++)
        {
            WeaponStation station = stations[i];
            if (station?.WeaponInfo?.gun != true || station.Ammo <= 0)
            {
                continue;
            }

            removed += station.Ammo;
            for (int w = 0; w < station.Weapons.Count; w++)
            {
                if (station.Weapons[w] is Gun gun)
                {
                    gun.LoadAmmunition(null!);
                }
                else if (station.Weapons[w] != null)
                {
                    station.Weapons[w].ammo = 0;
                }
            }

            station.AccountAmmo();
        }

        return removed;
    }

    private static void EnsureInternalCannons(AirMissionOption option)
    {
        for (int groupIndex = 0; groupIndex < option.HardpointGroups.Count; groupIndex++)
        {
            AirHardpointGroup group = option.HardpointGroups[groupIndex];
            if (group.SelectedMount != null)
            {
                continue;
            }

            bool conflictsWithOtherGroup = false;
            for (int otherIndex = 0; otherIndex < option.HardpointGroups.Count; otherIndex++)
            {
                if (otherIndex != groupIndex
                    && GroupsConflict(option.HardpointSets, group, option.HardpointGroups[otherIndex]))
                {
                    conflictsWithOtherGroup = true;
                    break;
                }
            }
            if (conflictsWithOtherGroup)
            {
                continue;
            }

            for (int mountIndex = 0; mountIndex < group.Mounts.Count; mountIndex++)
            {
                WeaponMount mount = group.Mounts[mountIndex];
                if (mount.info?.gun != true)
                {
                    continue;
                }
                bool dedicatedGunGroup = group.Mounts.Count > 0;
                for (int candidateIndex = 0; candidateIndex < group.Mounts.Count; candidateIndex++)
                {
                    if (group.Mounts[candidateIndex].info?.gun != true)
                    {
                        dedicatedGunGroup = false;
                        break;
                    }
                }
                if (dedicatedGunGroup)
                {
                    group.Select(mountIndex);
                    break;
                }
            }
        }
    }

    private static void EnsureMedusaLaser(AirMissionOption option)
    {
        if (GetAircraftLabel(option.Definition).IndexOf("MEDUSA", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        for (int groupIndex = 0; groupIndex < option.HardpointGroups.Count; groupIndex++)
        {
            AirHardpointGroup group = option.HardpointGroups[groupIndex];
            for (int mountIndex = 0; mountIndex < group.Mounts.Count; mountIndex++)
            {
                WeaponMount mount = group.Mounts[mountIndex];
                string identity = string.Join("|", new[]
                {
                    mount.jsonKey,
                    mount.mountName,
                    mount.name,
                    mount.info?.weaponName,
                    mount.info?.shortName,
                });
                if (ContainsWeaponToken(identity, "LASER_EW1", "LASER DESIGNATOR"))
                {
                    group.Select(mountIndex);
                    return;
                }
            }
        }
    }

    private static void SelectGroups(AirMissionOption option, WeaponMount weapon, List<int> groups)
    {
        for (int i = 0; i < groups.Count; i++)
        {
            SelectEquivalentMount(option.HardpointGroups[groups[i]], weapon);
        }
    }

    private static int[] FindBalancedWeaponAllocation(
        AirMissionOption option,
        WeaponMount primary,
        WeaponMount secondary,
        float targetPrimaryShare)
    {
        int count = option.HardpointGroups.Count;
        int[] current = new int[count];
        int[] best = new int[count];
        float bestScore = float.MinValue;
        FindBalancedWeaponAllocationRecursive(
            option, primary, secondary, targetPrimaryShare, 0, current, 0, 0, ref bestScore, best);
        return best;
    }

    private static void FindBalancedWeaponAllocationRecursive(
        AirMissionOption option,
        WeaponMount primary,
        WeaponMount secondary,
        float targetPrimaryShare,
        int index,
        int[] current,
        int primaryStores,
        int secondaryStores,
        ref float bestScore,
        int[] best)
    {
        if (index >= current.Length)
        {
            if (primaryStores <= 0 || secondaryStores <= 0)
            {
                return;
            }

            int total = primaryStores + secondaryStores;
            float share = (float)primaryStores / Mathf.Max(total, 1);
            float score = total - Mathf.Abs(share - targetPrimaryShare) * total * 2f;
            if (secondaryStores > 0) score += 0.01f;
            if (score > bestScore)
            {
                bestScore = score;
                Array.Copy(current, best, current.Length);
            }
            return;
        }

        current[index] = 0;
        FindBalancedWeaponAllocationRecursive(
            option, primary, secondary, targetPrimaryShare, index + 1, current,
            primaryStores, secondaryStores, ref bestScore, best);

        if (ConflictsWithCurrentSelection(option, index, current))
        {
            return;
        }

        int primaryCapacity = GetGroupWeaponCapacity(option.HardpointGroups[index], primary);
        if (primaryCapacity > 0)
        {
            current[index] = 1;
            FindBalancedWeaponAllocationRecursive(
                option, primary, secondary, targetPrimaryShare, index + 1, current,
                primaryStores + primaryCapacity, secondaryStores, ref bestScore, best);
        }

        int secondaryCapacity = GetGroupWeaponCapacity(option.HardpointGroups[index], secondary);
        if (secondaryCapacity > 0)
        {
            current[index] = 2;
            FindBalancedWeaponAllocationRecursive(
                option, primary, secondary, targetPrimaryShare, index + 1, current,
                primaryStores, secondaryStores + secondaryCapacity, ref bestScore, best);
        }
        current[index] = 0;
    }

    private static bool ConflictsWithCurrentSelection(AirMissionOption option, int groupIndex, int[] current)
    {
        AirHardpointGroup candidate = option.HardpointGroups[groupIndex];
        for (int i = 0; i < groupIndex; i++)
        {
            if (current[i] != 0
                && GroupsConflict(option.HardpointSets, candidate, option.HardpointGroups[i]))
            {
                return true;
            }
        }
        return false;
    }

    private static List<int> FindMaximumWeaponGroups(
        AirMissionOption option,
        WeaponMount weaponType,
        IReadOnlyList<int> lockedGroups)
    {
        List<int> candidates = new();
        for (int i = 0; i < option.HardpointGroups.Count; i++)
        {
            AirHardpointGroup group = option.HardpointGroups[i];
            if (group.SelectedMount != null || FindEquivalentMountIndex(group, weaponType) < 0)
            {
                continue;
            }

            bool conflictsWithLocked = false;
            for (int locked = 0; locked < lockedGroups.Count; locked++)
            {
                if (GroupsConflict(option.HardpointSets, group, option.HardpointGroups[lockedGroups[locked]]))
                {
                    conflictsWithLocked = true;
                    break;
                }
            }
            if (!conflictsWithLocked)
            {
                candidates.Add(i);
            }
        }

        // Aircraft have few logical station groups, so an exact search is cheap
        // and avoids combined bays accidentally reducing the primary store count.
        List<int> best = new();
        int bestCapacity = -1;
        FindMaximumWeaponGroupsRecursive(
            option, weaponType, candidates, 0, new List<int>(), 0, ref bestCapacity, ref best);
        return best;
    }

    private static void FindMaximumWeaponGroupsRecursive(
        AirMissionOption option,
        WeaponMount weaponType,
        List<int> candidates,
        int candidateIndex,
        List<int> selected,
        int capacity,
        ref int bestCapacity,
        ref List<int> best)
    {
        if (candidateIndex >= candidates.Count)
        {
            if (capacity > bestCapacity)
            {
                bestCapacity = capacity;
                best = new List<int>(selected);
            }
            return;
        }

        FindMaximumWeaponGroupsRecursive(
            option, weaponType, candidates, candidateIndex + 1, selected, capacity, ref bestCapacity, ref best);

        int groupIndex = candidates[candidateIndex];
        AirHardpointGroup group = option.HardpointGroups[groupIndex];
        for (int i = 0; i < selected.Count; i++)
        {
            if (GroupsConflict(option.HardpointSets, group, option.HardpointGroups[selected[i]]))
            {
                return;
            }
        }

        selected.Add(groupIndex);
        FindMaximumWeaponGroupsRecursive(
            option,
            weaponType,
            candidates,
            candidateIndex + 1,
            selected,
            capacity + GetGroupWeaponCapacity(group, weaponType),
            ref bestCapacity,
            ref best);
        selected.RemoveAt(selected.Count - 1);
    }

    private int GetPrimaryWeaponCount(AirMissionOption option) => CountMountedType(option, SelectedPrimaryWeapon);
    private int GetSecondaryWeaponCount(AirMissionOption option) => CountMountedType(option, SelectedSecondaryWeapon);

    private static int CountMountedType(AirMissionOption option, WeaponMount? requested)
    {
        if (requested == null) return 0;
        int count = 0;
        for (int i = 0; i < option.HardpointGroups.Count; i++)
        {
            AirHardpointGroup group = option.HardpointGroups[i];
            if (SameMountType(group.SelectedMount, requested))
            {
                int storesPerMount = group.SelectedMount?.info?.gun == true
                    ? 1
                    : Mathf.Max(group.SelectedMount?.ammo ?? 1, 1);
                count += group.PhysicalMountCount * storesPerMount;
            }
        }
        return count;
    }

    private static void SelectEquivalentMount(AirHardpointGroup group, WeaponMount requested)
    {
        group.Select(FindEquivalentMountIndex(group, requested));
    }

    private static int FindEquivalentMountIndex(AirHardpointGroup group, WeaponMount requested)
    {
        string key = GetWeaponTypeKey(requested);
        int bestIndex = -1;
        float bestPreference = -1f;
        for (int i = 0; i < group.Mounts.Count; i++)
        {
            WeaponMount candidate = group.Mounts[i];
            if (string.Equals(GetWeaponTypeKey(candidate), key, StringComparison.OrdinalIgnoreCase)
                && GetCatalogPreference(candidate) > bestPreference)
            {
                bestIndex = i;
                bestPreference = GetCatalogPreference(candidate);
            }
        }
        return bestIndex;
    }

    private static bool SameMountType(WeaponMount? left, WeaponMount? right)
    {
        return left != null && right != null
            && string.Equals(GetWeaponTypeKey(left), GetWeaponTypeKey(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWeaponTypeKey(WeaponMount mount)
    {
        SpecialAirSystem specialSystem = GetSpecialAirSystem(mount);
        if (specialSystem == SpecialAirSystem.RadarJammer) return "RADAR JAMMER";
        if (specialSystem == SpecialAirSystem.Radar) return "RADAR";
        WeaponInfo? info = mount.info;
        if (!string.IsNullOrWhiteSpace(info?.weaponName)) return info!.weaponName;
        if (!string.IsNullOrWhiteSpace(info?.shortName)) return info!.shortName;
        if (!string.IsNullOrWhiteSpace(info?.name)) return info!.name;
        if (!string.IsNullOrWhiteSpace(mount.name)) return mount.name;
        return GetMountName(mount);
    }

    private static int GetRackStoreCount(WeaponMount mount)
    {
        return mount.info?.gun == true ? 1 : Mathf.Max(mount.ammo, 1);
    }

    private static float GetCatalogPreference(WeaponMount mount)
    {
        float range = GetSpecialSystemRange(mount);
        return range > 0f ? range : GetRackStoreCount(mount);
    }

    private static int GetGroupWeaponCapacity(AirHardpointGroup group, WeaponMount weaponType)
    {
        int mountIndex = FindEquivalentMountIndex(group, weaponType);
        return mountIndex < 0
            ? 0
            : group.PhysicalMountCount * GetRackStoreCount(group.Mounts[mountIndex]);
    }

    internal bool IsWeaponSuitable(WeaponMount mount) => ScoreMount(mount, selectedMode) > 0f;

    internal string GetMissionWeaponLabel(WeaponMount mount)
    {
        return $"{GetWeaponTypeName(mount)}  | score {ScoreMount(mount, selectedMode):F2}";
    }

    private static string GetWeaponTypeName(WeaponMount mount)
    {
        SpecialAirSystem specialSystem = GetSpecialAirSystem(mount);
        if (specialSystem == SpecialAirSystem.RadarJammer) return "Radar Jammer";
        if (specialSystem == SpecialAirSystem.Radar) return "Radar";
        WeaponInfo? info = mount.info;
        if (!string.IsNullOrWhiteSpace(info?.weaponName)) return info!.weaponName;
        if (!string.IsNullOrWhiteSpace(info?.shortName)) return info!.shortName;
        return GetMountName(mount);
    }

    private static List<WeaponMount> GetCommonCombatMounts(
        HardpointSet[] sets,
        List<int> indices,
        FactionHQ hq)
    {
        List<WeaponMount> result = new();
        List<WeaponMount>? firstOptions = sets[indices[0]].weaponOptions;
        if (firstOptions == null)
        {
            return result;
        }

        for (int mountIndex = 0; mountIndex < firstOptions.Count; mountIndex++)
        {
            WeaponMount? mount = firstOptions[mountIndex];
            if (mount == null
                || mount.Cargo
                || mount.Troops
                || mount.info?.cargo == true
                || mount.info?.troops == true
                || !WeaponChecker.MountAllowedHQ(mount, hq))
            {
                continue;
            }

            bool availableOnEveryMirroredSet = true;
            for (int index = 1; index < indices.Count; index++)
            {
                if (!sets[indices[index]].weaponOptions.Contains(mount))
                {
                    availableOnEveryMirroredSet = false;
                    break;
                }
            }

            if (availableOnEveryMirroredSet && !result.Contains(mount))
            {
                result.Add(mount);
            }
        }

        result.Sort(static (left, right) =>
            string.Compare(GetMountName(left), GetMountName(right), StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// The AIR window's own auto-configure, exposed for the AI buyers (one definition, two callers):
    /// the best loadout the hardpoint picker can build for this airframe and mission mode — the
    /// per-group best mount by the mode's scorer, conflicts resolved the window's way. With
    /// <paramref name="preferArhMissiles"/> an active-radar-homing air-to-air missile outranks the
    /// raw score inside its group (the home-CAP rule, user decision 2026-09-14), so the CAP gets
    /// the fighter's radar missiles when it has them and its best other A/A when it does not.
    /// </summary>
    internal static bool TryBuildRoleLoadout(
        AircraftDefinition definition,
        FactionHQ hq,
        AirCommandMode mode,
        bool preferArhMissiles,
        out Loadout loadout,
        out float score)
    {
        loadout = null!;
        score = 0f;
        AirMissionOption? option = CreateVariableLoadoutOption(definition, hq, mode);
        if (option == null)
        {
            return false;
        }

        // The commander's own builder always prefers the named CAS ordnance (design SS1); the
        // player's AIR window never does, so its weapon list keeps its unweighted order.
        AutoConfigureRoleLoadout(option, preferArhMissiles, preferCasOrdnance: true);
        score = option.Score;
        if (score <= 0f)
        {
            // No mountable weapon scores for this mode at all — a ground-attack-only aeroplane is
            // not a CAP candidate however its role data reads, and vice versa. Capability is what
            // the loadout can actually do, not what the data sheet says the airframe is for
            // (user decision 2026-09-14: a Compass with Scythes is a CAP candidate).
            return false;
        }

        loadout = option.BuildLoadout();
        NormalizeLoadoutLength(loadout, definition);
        return true;
    }

    private static void AutoConfigureRoleLoadout(
        AirMissionOption option, bool preferArhMissiles = false, bool preferCasOrdnance = false)
    {
        // Suppression picks its pylons by its own two-pass rule (user decision 2026-09-14), because
        // a greedy walk in group order can knock an anti-radiation missile off the aeroplane
        // through the hardpoint exclusions. Everything else keeps the single greedy pass below.
        if (option.Mode == AirCommandMode.Arad)
        {
            AutoConfigureAradLoadout(option);
            return;
        }

        for (int groupIndex = 0; groupIndex < option.HardpointGroups.Count; groupIndex++)
        {
            AirHardpointGroup group = option.HardpointGroups[groupIndex];
            int bestIndex = -1;
            float bestScore = 0f;
            for (int mountIndex = 0; mountIndex < group.Mounts.Count; mountIndex++)
            {
                WeaponMount mount = group.Mounts[mountIndex];
                float score = ScoreMount(mount, option.Mode, preferCasOrdnance) * group.PhysicalMountCount;
                if (preferArhMissiles && mount.info != null && IsArhAirToAirMissile(mount.info))
                {
                    // Big enough to win its group outright, small enough to stay out of the loadout
                    // score the caller reads (score is recomputed from the built loadout, not from
                    // these per-group numbers, so this only decides WHICH mount is picked).
                    score += 1000f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = mountIndex;
                }
            }

            if (bestIndex >= 0)
            {
                SelectHardpointMount(option, groupIndex, bestIndex);
            }
        }
    }

    /// <summary>
    /// The suppression loadout, in two passes (user decision 2026-09-14: "ARADs are not using the
    /// correct ordinance, should be AGM-99 or AGM-68 etc"). Every hardpoint group that can carry a
    /// real anti-radiation missile takes one FIRST; only then do the groups that cannot take the
    /// named standoff stores, and one of those is skipped whenever selecting it would exclude a
    /// group that already has a store on it.
    /// <para>
    /// The order is the whole point. The suppression scorer counts anti-radiation stores and
    /// nothing else, so before this every other pylon on the aeroplane flew EMPTY — which is what
    /// the user saw. Filling them with one greedy pass in group index order would have been worse
    /// than empty: the game's hardpoint exclusions let a fat standoff store on an inboard pylon
    /// clear the missile off the pylon beside it, and the sortie would arrive with nothing that can
    /// hit a radar at all.
    /// </para>
    /// </summary>
    private static void AutoConfigureAradLoadout(AirMissionOption option)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool antiRadiationPass = pass == 0;
            for (int groupIndex = 0; groupIndex < option.HardpointGroups.Count; groupIndex++)
            {
                AirHardpointGroup group = option.HardpointGroups[groupIndex];
                if (group.SelectedMount != null
                    || (!antiRadiationPass && ConflictsWithSelectedGroup(option, groupIndex)))
                {
                    continue;
                }

                int bestIndex = -1;
                float bestScore = 0f;
                for (int mountIndex = 0; mountIndex < group.Mounts.Count; mountIndex++)
                {
                    WeaponMount mount = group.Mounts[mountIndex];
                    bool antiRadiation = mount.info != null && IsAradWeapon(mount.info, mount);
                    if (antiRadiation != antiRadiationPass)
                    {
                        continue;
                    }

                    float score = ScoreAradMountForCommander(mount) * group.PhysicalMountCount;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = mountIndex;
                    }
                }

                if (bestIndex >= 0)
                {
                    SelectHardpointMount(option, groupIndex, bestIndex);
                }
            }
        }
    }

    /// <summary>Whether filling this hardpoint group would exclude a group that already carries a
    /// store — the guard that keeps the suppression pass's second half from undoing its first.
    /// </summary>
    private static bool ConflictsWithSelectedGroup(AirMissionOption option, int groupIndex)
    {
        for (int i = 0; i < option.HardpointGroups.Count; i++)
        {
            if (i != groupIndex
                && option.HardpointGroups[i].SelectedMount != null
                && GroupsConflict(option.HardpointSets, option.HardpointGroups[groupIndex], option.HardpointGroups[i]))
            {
                return true;
            }
        }

        return false;
    }

    internal void SelectHardpointMount(int groupIndex, int mountIndex)
    {
        AirMissionOption? option = SelectedOption;
        if (option != null)
        {
            SelectHardpointMount(option, groupIndex, mountIndex);
        }
    }

    private static void SelectHardpointMount(AirMissionOption option, int groupIndex, int mountIndex)
    {
        if (groupIndex < 0 || groupIndex >= option.HardpointGroups.Count)
        {
            return;
        }

        AirHardpointGroup selected = option.HardpointGroups[groupIndex];
        selected.Select(mountIndex);
        if (selected.SelectedMount == null)
        {
            return;
        }

        for (int otherIndex = 0; otherIndex < option.HardpointGroups.Count; otherIndex++)
        {
            if (otherIndex != groupIndex
                && GroupsConflict(option.HardpointSets, selected, option.HardpointGroups[otherIndex]))
            {
                option.HardpointGroups[otherIndex].Clear();
            }
        }
    }

    internal string GetHardpointButtonLabel(AirMissionOption option, AirHardpointGroup group)
    {
        string mount = group.SelectedMount == null ? "EMPTY" : GetMountName(group.SelectedMount);
        string mirror = group.PhysicalMountCount > 1 ? $"  [MIRRORED x{group.PhysicalMountCount}]" : string.Empty;
        string excludes = GetConflictSummary(option, group);
        return $"{group.Label}{mirror}\n{mount}{excludes}";
    }

    internal string GetHardpointMountLabel(WeaponMount mount)
    {
        WeaponInfo? info = mount.info;
        List<string> tags = new();
        if (mount.radar || mount.prefab?.GetComponentInChildren<Radar>(true) != null) tags.Add("RADAR");
        if (info?.jammer == true) tags.Add("ECM");
        if (info != null && IsAradWeapon(info, mount)) tags.Add("ARAD");
        if (info?.effectiveness.antiAir > 0.05f) tags.Add("A/A");
        if (info?.effectiveness.antiSurface > 0.05f) tags.Add("A/G");
        string suffix = tags.Count > 0 ? $"  [{string.Join(", ", tags)}]" : string.Empty;
        return GetMountName(mount) + suffix;
    }

    private static string GetConflictSummary(AirMissionOption option, AirHardpointGroup group)
    {
        List<string> names = new();
        for (int i = 0; i < option.HardpointGroups.Count; i++)
        {
            AirHardpointGroup other = option.HardpointGroups[i];
            if (!ReferenceEquals(group, other) && GroupsConflict(option.HardpointSets, group, other))
            {
                names.Add(other.Label);
            }
        }
        return names.Count == 0 ? string.Empty : $"  | excludes {string.Join(" / ", names)}";
    }

    private static bool GroupsConflict(HardpointSet[] sets, AirHardpointGroup first, AirHardpointGroup second)
    {
        for (int i = 0; i < first.HardpointIndices.Count; i++)
        {
            for (int j = 0; j < second.HardpointIndices.Count; j++)
            {
                int firstIndex = first.HardpointIndices[i];
                int secondIndex = second.HardpointIndices[j];
                if (ContainsHardpointIndex(sets[firstIndex].precludingHardpointSets, secondIndex)
                    || ContainsHardpointIndex(sets[secondIndex].precludingHardpointSets, firstIndex))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool ContainsHardpointIndex(List<byte>? indices, int value)
    {
        return indices != null && value <= byte.MaxValue && indices.Contains((byte)value);
    }

    /// <param name="fromPicker">True when the loadout was just built from the AIR window's live
    /// hardpoint picker, so the picker's own selection has to be checked too. A relaunch replays a
    /// loadout frozen at first launch; whatever the picker shows now is about some other aircraft,
    /// and checking it is what left a queued relaunch waiting forever on "select a primary weapon".</param>
    private bool ValidateSelectedLoadout(
        AirMissionOption option,
        Loadout loadout,
        Airbase airbase,
        FactionHQ hq,
        out string error,
        bool fromPicker = true)
    {
        if (fromPicker && (SelectedPrimaryWeapon == null || GetPrimaryWeaponCount(option) <= 0))
        {
            error = "Select a primary weapon supported by the selected aircraft.";
            return false;
        }

        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount == null)
            {
                continue;
            }

            HardpointSet set = option.HardpointSets[i];
            if (!WeaponChecker.MountAllowedHardpoint(mount, set)
                || !WeaponChecker.MountAllowedConflict(set, loadout)
                || !WeaponChecker.MountAllowedHQ(mount, hq)
                || !WeaponChecker.MountAllowedAirbase(mount, airbase)
                || !WeaponChecker.MountAllowedNuclear(mount, set, airbase, null!, hq))
            {
                error = $"{GetMountName(mount)} is not available on {GetHardpointSetName(set, i)} at this airbase.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static float ScoreMount(WeaponMount mount, AirCommandMode mode, bool preferCasOrdnance = false)
    {
        Loadout loadout = new();
        loadout.weapons.Add(mount);
        return ScoreLoadout(loadout, mode, null, preferCasOrdnance);
    }

    private static string GetHardpointSetName(HardpointSet set, int index)
    {
        return !string.IsNullOrWhiteSpace(set.name) ? set.name : $"Hardpoint {index + 1}";
    }

    private static string GetMountName(WeaponMount mount)
    {
        if (!string.IsNullOrWhiteSpace(mount.mountName)) return mount.mountName;
        WeaponInfo? info = mount.info;
        if (info != null && !string.IsNullOrWhiteSpace(info.weaponName)) return info.weaponName;
        if (!string.IsNullOrWhiteSpace(mount.jsonKey)) return mount.jsonKey;
        return mount.name;
    }

    /// <summary>
    /// Whether a built loadout carries the game's own airborne radar — a mount whose prefab holds a
    /// <c>Radar</c> component, which is exactly what <see cref="GetSpecialAirSystem"/> calls
    /// <see cref="SpecialAirSystem.Radar"/>. The AWACS candidate test (design.md,
    /// smarter-air-wing_20260914 Section 4): the mode's scorer also rewards a jamming pod, and a
    /// jammer is not an early-warning aircraft.
    /// </summary>
    internal static bool LoadoutHasRadarSystem(Loadout? loadout)
    {
        if (loadout?.weapons == null)
        {
            return false;
        }

        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount != null && GetSpecialAirSystem(mount) == SpecialAirSystem.Radar)
            {
                return true;
            }
        }

        return false;
    }

    private static SpecialAirSystem GetSpecialAirSystem(WeaponMount mount)
    {
        if (mount.info?.jammer == true || mount.prefab?.GetComponentInChildren<JammingPod>(true) != null)
        {
            return SpecialAirSystem.RadarJammer;
        }
        if (mount.radar || mount.prefab?.GetComponentInChildren<Radar>(true) != null)
        {
            return SpecialAirSystem.Radar;
        }
        return SpecialAirSystem.None;
    }

    private static float GetSpecialSystemRange(WeaponMount mount)
    {
        Radar? radar = mount.prefab?.GetComponentInChildren<Radar>(true);
        if (radar != null)
        {
            return radar.GetRadarRange();
        }

        JammingPod? jammer = mount.prefab?.GetComponentInChildren<JammingPod>(true);
        AnimationCurve? falloff = jammer != null ? JammingRangeFalloffField?.GetValue(jammer) as AnimationCurve : null;
        Keyframe[]? keys = falloff?.keys;
        return keys != null && keys.Length > 0 ? keys[keys.Length - 1].time : 0f;
    }

    private enum SpecialAirSystem
    {
        None,
        Radar,
        RadarJammer,
    }
}
