using System;
using System.Collections.Generic;
using System.Reflection;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderAirCommandService
{
    internal enum AirCommandMode
    {
        AwacsJammer,
        Cas,
        AirGuard,
        Arad,
        StrategicStrike,
    }

    internal enum LoadoutBalance
    {
        Primary,
        Mixed,
    }

    internal sealed class AirMissionOption
    {
        internal AirMissionOption(
            AircraftDefinition definition,
            AirCommandMode mode,
            HardpointSet[] hardpointSets,
            List<AirHardpointGroup> hardpointGroups)
        {
            Definition = definition;
            Mode = mode;
            HardpointSets = hardpointSets;
            HardpointGroups = hardpointGroups;
        }

        internal AircraftDefinition Definition { get; }
        internal HardpointSet[] HardpointSets { get; }
        internal List<AirHardpointGroup> HardpointGroups { get; }
        internal string LoadoutName => "Custom hardpoints";
        internal float Score => ScoreLoadout(BuildLoadout(), Mode, Definition);
        internal AirCommandMode Mode { get; }

        internal Loadout BuildLoadout()
        {
            Loadout loadout = new();
            for (int i = 0; i < HardpointSets.Length; i++)
            {
                loadout.weapons.Add(null!);
            }
            for (int i = 0; i < HardpointGroups.Count; i++)
            {
                AirHardpointGroup group = HardpointGroups[i];
                WeaponMount? mount = group.SelectedMount;
                for (int index = 0; index < group.HardpointIndices.Count; index++)
                {
                    loadout.weapons[group.HardpointIndices[index]] = mount!;
                }
            }
            return loadout;
        }
    }

    internal sealed class AirHardpointGroup
    {
        private int selectedIndex = -1;

        internal AirHardpointGroup(string label, List<int> hardpointIndices, List<WeaponMount> mounts, int physicalMountCount)
        {
            Label = label;
            HardpointIndices = hardpointIndices;
            Mounts = mounts;
            PhysicalMountCount = physicalMountCount;
        }

        internal string Label { get; }
        internal List<int> HardpointIndices { get; }
        internal List<WeaponMount> Mounts { get; }
        internal int PhysicalMountCount { get; }
        internal WeaponMount? SelectedMount => selectedIndex >= 0 && selectedIndex < Mounts.Count ? Mounts[selectedIndex] : null;

        internal void Select(int index) => selectedIndex = index >= 0 && index < Mounts.Count ? index : -1;
        internal void Clear() => selectedIndex = -1;
    }

    internal sealed class AirbaseOption
    {
        internal AirbaseOption(Airbase airbase, string label, float distance, bool ready)
        {
            Airbase = airbase;
            Label = label;
            Distance = distance;
            Ready = ready;
        }

        internal Airbase Airbase { get; }
        internal string Label { get; }
        internal float Distance { get; }
        internal bool Ready { get; }
    }

    private sealed class PendingAreaSelection
    {
        internal PendingAreaSelection(AirMissionOption option, Airbase airbase)
        {
            Option = option;
            Airbase = airbase;
        }

        internal AirMissionOption Option { get; }
        internal Airbase Airbase { get; }
    }

    private sealed class PendingAircraftSpawn
    {
        internal PendingAircraftSpawn(FactionHQ hq, AirMissionOption option, GlobalPosition areaCenter, float radius, float targetAltitude, bool targetOrdnance, bool saturationAttack, bool purchasedWithFunds, float purchaseCost, float expiresAt)
        {
            Hq = hq;
            Option = option;
            AreaCenter = areaCenter;
            Radius = radius;
            TargetAltitude = targetAltitude;
            TargetOrdnance = targetOrdnance;
            SaturationAttack = saturationAttack;
            PurchasedWithFunds = purchasedWithFunds;
            PurchaseCost = purchaseCost;
            ExpiresAt = expiresAt;
        }

        internal FactionHQ Hq { get; }
        internal AirMissionOption Option { get; }
        internal GlobalPosition AreaCenter { get; }
        internal float Radius { get; }
        internal float TargetAltitude { get; }
        internal bool TargetOrdnance { get; }
        internal bool SaturationAttack { get; }
        internal bool PurchasedWithFunds { get; }
        internal float PurchaseCost { get; }
        internal float ExpiresAt { get; }
    }

    private sealed class AirMission
    {
        internal AirMission(FactionHQ hq, AirCommandMode mode, GlobalPosition areaCenter, float radius, float targetAltitude, bool targetOrdnance, bool saturationAttack, bool purchasedWithFunds, float purchaseCost)
        {
            Hq = hq;
            Mode = mode;
            AreaCenter = areaCenter;
            Radius = radius;
            TargetAltitude = targetAltitude;
            TargetOrdnance = targetOrdnance;
            SaturationAttack = saturationAttack;
            PurchasedWithFunds = purchasedWithFunds;
            PurchaseCost = purchaseCost;
        }

        internal FactionHQ Hq { get; }
        internal AirCommandMode Mode { get; set; }
        internal GlobalPosition AreaCenter { get; set; }
        internal float Radius { get; set; }
        internal float TargetAltitude { get; }
        internal bool TargetOrdnance { get; }
        internal bool SaturationAttack { get; }
        internal bool PurchasedWithFunds { get; }
        internal float PurchaseCost { get; }
        internal GameObject? MapVisual { get; set; }
        internal bool Returning { get; set; }
        internal bool RtbIssued { get; set; }

        /// <summary>Travel points the aircraft flies through before settling on the mission area.</summary>
        internal List<GlobalPosition> Route { get; } = new();
        internal int RouteIndex { get; set; }

        /// <summary>Explicitly commanded target; preferred over the automatic mission target search.</summary>
        internal Unit? ForcedTarget { get; set; }
    }
}
