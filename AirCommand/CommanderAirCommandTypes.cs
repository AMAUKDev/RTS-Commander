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

    /// <summary>Why a commanded aircraft is being put on the deck at a named airbase.</summary>
    internal enum LandingIntent
    {
        /// <summary>Not landing anywhere in particular; the mission is flying.</summary>
        None,

        /// <summary>Land at a field the faction holds. The Basegame recovers the airframe.</summary>
        Resupply,

        /// <summary>Land inside a takeable base's ring and hold it until the base falls.</summary>
        Capture,
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

    /// <summary>
    /// Everything needed to launch one mission again exactly as it was first launched: the airframe
    /// and mode, the loadout as built at launch (not the live hardpoint picker, which the player may
    /// have changed since), the departure base and the mission area settings.
    /// </summary>
    internal sealed class AirMissionRecipe
    {
        internal AirMissionRecipe(AirMissionOption option, Airbase origin, Loadout loadout, GlobalPosition areaCenter, float radius, float targetAltitude, bool targetOrdnance, bool saturationAttack)
        {
            Option = option;
            Origin = origin;
            Loadout = loadout;
            Mode = option.Mode;
            AreaCenter = areaCenter;
            Radius = radius;
            TargetAltitude = targetAltitude;
            TargetOrdnance = targetOrdnance;
            SaturationAttack = saturationAttack;
        }

        internal AirMissionOption Option { get; }
        internal Airbase Origin { get; }
        internal Loadout Loadout { get; }
        internal AirCommandMode Mode { get; set; }
        internal GlobalPosition AreaCenter { get; set; }
        internal float Radius { get; set; }
        internal float TargetAltitude { get; }
        internal bool TargetOrdnance { get; }
        internal bool SaturationAttack { get; }
    }

    private sealed class PendingAircraftSpawn
    {
        internal PendingAircraftSpawn(FactionHQ hq, AirMissionRecipe recipe, bool autoRecreate, bool purchasedWithFunds, float purchaseCost, float expiresAt)
        {
            Hq = hq;
            Recipe = recipe;
            AutoRecreate = autoRecreate;
            PurchasedWithFunds = purchasedWithFunds;
            PurchaseCost = purchaseCost;
            ExpiresAt = expiresAt;
        }

        internal FactionHQ Hq { get; }
        internal AirMissionRecipe Recipe { get; }
        internal AirMissionOption Option => Recipe.Option;
        internal bool AutoRecreate { get; }
        internal bool PurchasedWithFunds { get; }
        internal float PurchaseCost { get; }
        internal float ExpiresAt { get; }
    }

    private sealed class AirMission
    {
        internal AirMission(FactionHQ hq, AirCommandMode mode, GlobalPosition areaCenter, float radius, float targetAltitude, bool targetOrdnance, bool saturationAttack, bool purchasedWithFunds, float purchaseCost, AirMissionRecipe? recipe = null, bool autoRecreate = false)
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
            Recipe = recipe;
            AutoRecreate = autoRecreate && recipe != null;
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

        /// <summary>
        /// How to launch this mission again; null for an adopted airframe, which the Commander never
        /// launched and so cannot relaunch. Deliberately not written through from
        /// <see cref="AreaCenter"/>: landing and travel orders park the area on a field or a
        /// waypoint, and a relaunch after an RTB must go back to the mission area, not to the
        /// runway. Only a real area edit calls <see cref="RememberArea"/>.
        /// </summary>
        internal AirMissionRecipe? Recipe { get; }

        internal bool CanAutoRecreate => Recipe != null;

        /// <summary>Relaunch the same mission when this airframe is lost or recovered.</summary>
        internal bool AutoRecreate { get; set; }

        internal void RememberArea()
        {
            if (Recipe == null)
            {
                return;
            }

            Recipe.Mode = Mode;
            Recipe.AreaCenter = AreaCenter;
            Recipe.Radius = Radius;
        }

        /// <summary>Game time the aircraft was first seen sitting on the deck during an RTB, or a
        /// negative number while it is not. See <c>RecoverLandedAircraft</c>.</summary>
        internal float OnDeckSince { get; set; } = -1f;
        internal GameObject? MapVisual { get; set; }
        internal bool Returning { get; set; }
        internal bool RtbIssued { get; set; }

        /// <summary>The airbase this aircraft has been told to put itself down on, if any.</summary>
        internal Airbase? LandingBase { get; set; }
        internal LandingIntent Intent { get; set; }

        /// <summary>The pilot has been switched into the landing state for <see cref="LandingBase"/>.</summary>
        internal bool LandingIssued { get; set; }

        /// <summary>Stopped on the deck and held there, rather than left to the Basegame taxi state.</summary>
        internal bool Parked { get; set; }

        /// <summary>Capture strength this mission added to the airframe and still owes back.</summary>
        internal float GrantedCaptureStrength { get; set; }

        /// <summary>Travel points the aircraft flies through before settling on the mission area.</summary>
        internal List<GlobalPosition> Route { get; } = new();
        internal int RouteIndex { get; set; }

        /// <summary>Explicitly commanded target; preferred over the automatic mission target search.</summary>
        internal Unit? ForcedTarget { get; set; }
    }
}
