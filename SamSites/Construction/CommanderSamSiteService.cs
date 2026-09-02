using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSamSiteService : ICommanderTickPersistent, ICommanderResetSession
{
    private const float CoreSupplyRangeMeters = 600f;
    private const float FallbackCoreCapacity = 10000f;

    private static readonly FieldInfo? RearmerMaxCapacityField =
        AccessTools.Field(typeof(Rearmer), "maxCapacity");
    private static readonly FieldInfo? RearmerSingleUseField =
        AccessTools.Field(typeof(Rearmer), "singleUse");
    private static CommanderSamSiteService? instance;

    private readonly CommanderSamSiteAnalyzerService analyzer;
    private readonly List<CommanderSamSiteAnalyzerService.SiteLayoutMarker> layout = new();
    private readonly List<Unit> spawnedUnits = new();
    private readonly Dictionary<CommanderSamSiteAnalyzerService.SiteUnitRole, UnitDefinition> definitions = new();
    private string statusText = "Select and jump to a SAM site before spawning.";

    internal CommanderSamSiteService(
        CommanderSamSiteAnalyzerService analyzer,
        CommanderSupplyHeliService supplyHeliService)
    {
        this.analyzer = analyzer;
        this.supplyHeliService = supplyHeliService;
        instance = this;
    }

    internal bool HasSpawnedUnits
    {
        get
        {
            PruneConstructionSites();
            return constructionSites.Count > 0;
        }
    }

    internal string StatusText => statusText;

    internal bool TryGetPlatformTarget(out GlobalPosition target)
    {
        return TryGetConstructionPlatformTarget(out target);
    }

    internal void Toggle()
    {
        ToggleConstructionSite();
    }

    internal void SpawnCompleteDebugSite()
    {
        TryCreateActiveConstructionSite(automaticBuild: false, instant: true);
    }

    internal void StartAutomaticSiteConstruction(bool useLocalCandidatePass)
    {
        if (!analyzer.BeginAutomaticSiteSelection(
            useLocalCandidatePass,
            success =>
            {
                if (!success)
                {
                    SetStatus("Automatic SAM-site terrain refinement failed.", warning: true);
                    return;
                }
                TryCreateActiveConstructionSite(automaticBuild: true, instant: false);
            }))
        {
            SetStatus(analyzer.StatusText, warning: true);
            return;
        }
        SetStatus("Friendly AI is selecting and refining a SAM-site location.");
    }

    public void ResetSession()
    {
        ResetConstructionSession();
    }

    internal static bool TryDepositAmmunition(
        int siteId,
        Unit cargo,
        out float transferred)
    {
        transferred = 0f;
        return instance != null
            && instance.DepositConstructionAmmunition(siteId, cargo, out transferred);
    }

    internal static void NotifyFoundationCargoActivated(int siteId, Unit cargo)
    {
        instance?.HandleFoundationCargoActivated(siteId, cargo);
    }

    internal static void ReserveDeliveredJacknife(int siteId, GroundVehicle jacknife)
    {
        instance?.ReserveConstructionJacknife(siteId, jacknife);
    }

    internal static void NotifySiteJacknifeActivated(int siteId, Unit cargo)
    {
        instance?.HandleSiteJacknifeActivated(siteId, cargo);
    }

    internal static void NotifyFoundationAmmunitionDelivered(int siteId, float supply)
    {
        instance?.HandleFoundationAmmunitionDelivered(siteId, supply);
    }

    internal static bool TryDepositAmmunitionAmount(int siteId, float supply, out float transferred)
    {
        transferred = 0f;
        return instance != null
            && instance.DepositConstructionAmmunitionAmount(siteId, supply, out transferred);
    }

    internal static void NotifySupplyMissionFailed(
        int foundationSiteId,
        int supplySiteId,
        int jacknifeSiteId)
    {
        instance?.HandleSupplyMissionFailed(foundationSiteId, supplySiteId, jacknifeSiteId);
    }

    internal static bool IsReservedConstructionJacknife(Unit? unit)
    {
        return instance != null && instance.IsConstructionJacknifeReserved(unit);
    }

    internal static bool ShouldBlockConstructionDestination(UnitCommand command)
    {
        return instance != null
            && !instance.issuingConstructionMove
            && instance.IsConstructionJacknifeReserved(command.GetComponent<Unit>());
    }

    private static void DisableDecorativeRearmer(Unit unit, FactionHQ hq)
    {
        Rearmer[] rearmers = unit.GetComponentsInChildren<Rearmer>(true);
        for (int i = 0; i < rearmers.Length; i++)
        {
            Rearmer rearmer = rearmers[i];
            hq.RearmMissionController.DeregisterRearmer(rearmer);
            rearmer.AvailableForMission = false;
            rearmer.Range = 0f;
            rearmer.SetCapacity(0f);
            RearmerMaxCapacityField?.SetValue(rearmer, 0f);
            RearmerSingleUseField?.SetValue(rearmer, false);
            rearmer.enabled = false;
        }
    }

    private static void HideCoreVisuals(Unit core)
    {
        Renderer[] renderers = core.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            renderers[i].enabled = false;
        }

        Collider[] colliders = core.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            colliders[i].enabled = false;
        }
    }

    private static float FindFactionMunitionsTruckCapacity(FactionHQ hq)
    {
        string faction = $"{hq.faction?.factionTag} {hq.faction?.factionName} {hq.faction?.factionExtendedName}";
        string expectedName = faction.IndexOf("PALA", StringComparison.OrdinalIgnoreCase) >= 0
            ? "MSV Munitions"
            : faction.IndexOf("BDF", StringComparison.OrdinalIgnoreCase) >= 0
                ? "HLT Munitions Truck"
                : string.Empty;

        VehicleDefinition? definition = Resources.FindObjectsOfTypeAll<VehicleDefinition>()
            .FirstOrDefault(candidate =>
                candidate != null
                && candidate.unitPrefab != null
                && string.Equals(candidate.unitName, expectedName, StringComparison.OrdinalIgnoreCase));
        Rearmer? rearmer = definition?.unitPrefab.GetComponentInChildren<Rearmer>(true);
        return rearmer != null ? rearmer.Capacity : 0f;
    }

    private static bool ShouldExposeSiteMarker(
        CommanderSamSiteAnalyzerService.SiteUnitRole role)
    {
        return role == CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm
            || role == CommanderSamSiteAnalyzerService.SiteUnitRole.Irm
            || role == CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo;
    }

    private static GlobalPosition ResolvePlatformSurface(GlobalPosition fallback)
    {
        Vector3 local = fallback.ToLocalPosition();
        Vector3 origin = new(local.x, local.y + 500f, local.z);
        return Physics.Raycast(
            origin,
            Vector3.down,
            out RaycastHit hit,
            1000f,
            PhysicsLayers.StaticsMask,
            QueryTriggerInteraction.Ignore)
            ? hit.point.ToGlobalPosition()
            : fallback;
    }

    private static GlobalPosition ResolvePlatformDeckSurface(Unit platform, GlobalPosition fallback)
    {
        Collider[] colliders = platform.GetComponentsInChildren<Collider>(true);
        float highestColliderPoint = float.MinValue;
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null)
            {
                highestColliderPoint = Mathf.Max(highestColliderPoint, colliders[i].bounds.max.y);
            }
        }

        Vector3 fallbackLocal = fallback.ToLocalPosition();
        if (highestColliderPoint > float.MinValue)
        {
            Vector3 origin = new(fallbackLocal.x, highestColliderPoint + 10f, fallbackLocal.z);
            RaycastHit[] hits = Physics.RaycastAll(
                origin,
                Vector3.down,
                Mathf.Max(50f, highestColliderPoint - fallbackLocal.y + 30f),
                ~0,
                QueryTriggerInteraction.Ignore);
            float highestHit = float.MinValue;
            Vector3 deckPoint = fallbackLocal;
            for (int i = 0; i < hits.Length; i++)
            {
                Transform hitTransform = hits[i].collider.transform;
                if (!hitTransform.IsChildOf(platform.transform) || hits[i].point.y <= highestHit)
                {
                    continue;
                }

                highestHit = hits[i].point.y;
                deckPoint = hits[i].point;
            }

            if (highestHit > float.MinValue)
            {
                return (deckPoint + Vector3.up * 0.5f).ToGlobalPosition();
            }
        }

        Renderer[] renderers = platform.GetComponentsInChildren<Renderer>(true);
        float highestRendererPoint = float.MinValue;
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
            {
                highestRendererPoint = Mathf.Max(highestRendererPoint, renderers[i].bounds.max.y);
            }
        }

        return highestRendererPoint > float.MinValue
            ? new Vector3(fallbackLocal.x, highestRendererPoint + 0.5f, fallbackLocal.z).ToGlobalPosition()
            : fallback;
    }

    private bool ResolveDefinitions(
        IReadOnlyList<CommanderSamSiteAnalyzerService.SiteLayoutMarker> requestedLayout,
        FactionHQ hq,
        out string missingRoles)
    {
        definitions.Clear();
        UnitDefinition[] available = Resources.FindObjectsOfTypeAll<UnitDefinition>()
            .Where(definition => definition != null && definition.unitPrefab != null)
            .Distinct()
            .ToArray();

        HashSet<CommanderSamSiteAnalyzerService.SiteUnitRole> roles = new();
        for (int i = 0; i < requestedLayout.Count; i++)
        {
            roles.Add(requestedLayout[i].Role);
        }

        List<string> missing = new();
        foreach (CommanderSamSiteAnalyzerService.SiteUnitRole role in roles)
        {
            UnitDefinition? definition = FindBestDefinition(available, role, hq);
            if (definition == null)
            {
                missing.Add(role.ToString());
                LogCandidates(available, role, hq);
                continue;
            }

            definitions[role] = definition;
        }

        missingRoles = string.Join(", ", missing);
        return missing.Count == 0;
    }

    private static UnitDefinition? FindBestDefinition(
        IEnumerable<UnitDefinition> available,
        CommanderSamSiteAnalyzerService.SiteUnitRole role,
        FactionHQ hq)
    {
        string? expectedName = GetFactionVehicleName(role, hq);
        if (expectedName != null)
        {
            return available
                .OfType<VehicleDefinition>()
                .FirstOrDefault(definition =>
                    string.Equals(definition.unitName, expectedName, StringComparison.OrdinalIgnoreCase));
        }

        UnitDefinition? best = null;
        int bestScore = int.MinValue;
        foreach (UnitDefinition definition in available)
        {
            int score = ScoreDefinition(definition, role);
            if (score > bestScore)
            {
                bestScore = score;
                best = definition;
            }
        }

        return bestScore >= 100 ? best : null;
    }

    private static string? GetFactionVehicleName(
        CommanderSamSiteAnalyzerService.SiteUnitRole role,
        FactionHQ hq)
    {
        string faction = $"{hq.faction?.factionTag} {hq.faction?.factionName} {hq.faction?.factionExtendedName}";
        bool pala = faction.IndexOf("PALA", StringComparison.OrdinalIgnoreCase) >= 0;
        bool bdf = faction.IndexOf("BDF", StringComparison.OrdinalIgnoreCase) >= 0;

        if (pala)
        {
            return role switch
            {
                CommanderSamSiteAnalyzerService.SiteUnitRole.Radar => "MSV Radar",
                CommanderSamSiteAnalyzerService.SiteUnitRole.FireControl => "MSV Fire Control",
                CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher => "MSV R9 Stratolance Launcher",
                CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo => null,
                _ => null
            };
        }

        if (bdf)
        {
            return role switch
            {
                CommanderSamSiteAnalyzerService.SiteUnitRole.Radar => "HLT Radar Truck",
                CommanderSamSiteAnalyzerService.SiteUnitRole.FireControl => "HLT Fire Control",
                CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher => "StratoLance R9 Launcher",
                CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo => null,
                _ => null
            };
        }

        return null;
    }

    private static int ScoreDefinition(
        UnitDefinition definition,
        CommanderSamSiteAnalyzerService.SiteUnitRole role)
    {
        bool requiresVehicle = role == CommanderSamSiteAnalyzerService.SiteUnitRole.Radar
            || role == CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher
            || role == CommanderSamSiteAnalyzerService.SiteUnitRole.FireControl;
        if (requiresVehicle && definition is not VehicleDefinition)
        {
            return -1;
        }

        string text = $"{definition.unitName} {definition.jsonKey} {definition.name} {definition.code}"
            .ToLowerInvariant();
        GameObject prefab = definition.unitPrefab;
        bool hasRadar = prefab.GetComponentInChildren<Radar>(true) != null;
        bool hasFireControl = prefab.GetComponentInChildren<FireControl>(true) != null;
        bool hasRearmer = prefab.GetComponentInChildren<Rearmer>(true) != null;

        return role switch
        {
            CommanderSamSiteAnalyzerService.SiteUnitRole.Platform =>
                string.Equals(definition.unitName, "Small Platform", StringComparison.OrdinalIgnoreCase)
                    ? 1000
                    : ContainsAll(text, "small", "platform") ? 300 : -1,
            CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower =>
                string.Equals(definition.jsonKey, "controlTower1", StringComparison.OrdinalIgnoreCase)
                    ? 1000
                    : ContainsAll(text, "control", "tower") ? 300 : -1,
            CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm =>
                (text.Contains("23mm") || text.Contains("23 mm"))
                    ? 300 + (text.Contains("emplacement") ? 200 : 0)
                    : -1,
            CommanderSamSiteAnalyzerService.SiteUnitRole.Irm =>
                string.Equals(definition.unitName, "IRM-S1 Emplacment", StringComparison.OrdinalIgnoreCase)
                    ? 1000
                    : (ContainsToken(text, "irm-s1") || ContainsToken(text, "irm"))
                    ? 300 + ((text.Contains("emplacement") || text.Contains("emplacment")) ? 200 : 0)
                    : -1,
            CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher =>
                string.Equals(definition.unitName, "StratoLance R9", StringComparison.OrdinalIgnoreCase)
                    ? 1000
                    : text.Contains("stratolance")
                        ? 500
                        : (ContainsToken(text, "r9") && text.Contains("launcher")) ? 300 : -1,
            CommanderSamSiteAnalyzerService.SiteUnitRole.Radar =>
                hasRadar
                    ? 150
                        + (text.Contains("radar") ? 250 : 0)
                        + (text.Contains("hlt-r") ? 200 : 0)
                        - (hasFireControl ? 50 : 0)
                    : -1,
            CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo =>
                hasRearmer && definition is BuildingDefinition
                    ? 200
                        + (string.Equals(definition.unitName, "Ammo Dump", StringComparison.OrdinalIgnoreCase) ? 1000 : 0)
                        + (string.Equals(definition.unitName, "Munitions Dump", StringComparison.OrdinalIgnoreCase) ? 1000 : 0)
                        + (text.Contains("dump") ? 500 : 0)
                        + (text.Contains("ammo") ? 250 : 0)
                        + (text.Contains("munition") ? 200 : 0)
                        + (text.Contains("bunker") ? 100 : 0)
                    : -1,
            CommanderSamSiteAnalyzerService.SiteUnitRole.FireControl =>
                hasFireControl
                    ? 200
                        + (text.Contains("fire control") ? 300 : 0)
                        + (text.Contains("command") ? 100 : 0)
                    : -1,
            _ => -1
        };
    }

    private static void LogCandidates(
        IEnumerable<UnitDefinition> available,
        CommanderSamSiteAnalyzerService.SiteUnitRole role,
        FactionHQ hq)
    {
        string? expectedName = GetFactionVehicleName(role, hq);
        IEnumerable<string> candidates = available
            .Where(definition => ScoreDefinition(definition, role) >= 0)
            .Take(12)
            .Select(definition => $"{definition.unitName} [{definition.jsonKey}]");
        CommanderPlugin.Log.LogWarning(
            $"No safe definition match for SAM role {role}. "
            + $"Expected: {expectedName ?? "generic match"}. Candidates: {string.Join("; ", candidates)}");
    }

    private static bool ContainsAll(string text, params string[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (!text.Contains(values[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsToken(string text, string token)
    {
        string[] parts = text.Split(new[] { ' ', '_', '-', '/', '[', ']', '(', ')' },
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => string.Equals(part, token, StringComparison.OrdinalIgnoreCase));
    }

    private void SetStatus(string value, bool warning = false)
    {
        statusText = value;
        if (warning)
        {
            CommanderPlugin.Log.LogWarning(value);
        }
        else
        {
            CommanderPlugin.Log.LogInfo(value);
        }
    }
}
