using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace GroundControlRts;

internal sealed partial class CommanderSamSiteAnalyzerService : ICommanderActivate, ICommanderDeactivate, ICommanderTickActive, ICommanderTickPersistent, ICommanderResetSession
{
    internal static CommanderSamSiteAnalyzerService? Instance { get; private set; }
    private const float RegionalSampleSpacing = 200f;
    private const float CandidateRegionSize = 4000f;
    private const int CandidateLimit = 2000;
    private const float LowAltitudeClearance = 100f;
    private const int CoverageDirectionCount = 64;
    private const float CoverageSampleSpacing = 1000f;
    private const float CoverageRange = 50000f;
    private const float ForwardCoverageRange = 5000f;
    private const float ForwardCoverageHalfAngle = 22.5f;
    private const float RequiredRoadDistance = 500f;
    private const float RadarHeight = 5f;
    private const int CoverageOverlayResolution = 192;
    private const int CoverageHorizonDirectionCount = 720;
    private const float CoverageHorizonSampleSpacing = 100f;
    private const int SuggestedSiteCount = 12;
    private const float CandidateSeparation = 500f;
    private const float StartupDelaySeconds = 8f;
    private const float SiteControlRadius = 300f;
    private const float LocalSnapRadius = 250f;
    private readonly List<SiteCandidate> candidates = new();
    private readonly List<SiteCandidate> suggestedSites = new();
    private readonly List<SiteLayoutMarker> siteLayout = new();
    private readonly List<GlobalPosition> friendlyAirbases = new();
    private readonly List<GlobalPosition> enemyAirbases = new();
    private const int StrategicWeightResolution = 256;
    private float[]? strategicWeights;
    private float[]? strategicRisks;
    private readonly CommanderStrategicHeightMap strategicHeightMap = new();
    private readonly CommanderLocalHeightMapBaker localHeightMapBaker = new();
    private readonly List<CommanderLocalHeightMapBaker.LocalHeightMap> localSiteMaps = new();

    private AnalyzerState state;
    private MapSettings? mapSettings;
    private string mapKey = string.Empty;
    private int regionColumns;
    private int regionRows;
    private int regionIndex;
    private int coverageCandidateIndex;
    private int coverageDirectionIndex;
    private float coverageDistance;
    private float coverageHighestTerrainSlope;
    private float coverageVisibleAreaWeight;
    private float coverageVisibleFrontWeight;
    private float coverageTotalFrontWeight;
    private float coverageTotalAreaWeight;
    private float coverageForwardVisibleWeight;
    private float coverageForwardTotalWeight;
    private Vector2 coverageEnemyDirection;
    private bool coverageEnemyDirectionReady;
    private Color32[]? coverageOverlayPixels;
    private float[]? coverageRequiredAltitudes;
    private byte[]? coverageOverlayAlpha;
    private float[]? coverageHorizonSlopes;
    private float[]? coverageHorizonProfile;
    private SiteCandidate coverageOverlayCandidate;
    private int coverageOverlayPixelIndex;
    private int coverageHorizonSampleIndex;
    private bool coverageOverlayBuilding;
    private Unit? coverageOverlaySource;
    private float coverageEmitterHeight;
    private float coverageTargetAltitude = LowAltitudeClearance;
    private bool uiVisible;
    private bool localRefinementActive;
    private int activeCandidateId = -1;
    private int activeRefinementGeneration;
    private SiteCandidate activeSite;
    private float nextNearbyRefreshAt;
    private float nextInfluenceRefreshAt;
    private bool showProposalMarkers;
    private Action<bool>? automaticSelectionCompleted;
    private bool limitRoadDistance;
    private float maximumCandidateRange;
    private float minimumAreaCoverage;
    private float minimumFrontShare;
    private float maximumRisk = 1f;
    private float minimumForwardCoverage;
    private FilterComparison rangeComparison = FilterComparison.Maximum;
    private FilterComparison areaComparison = FilterComparison.Minimum;
    private FilterComparison frontComparison = FilterComparison.Minimum;
    private FilterComparison riskComparison = FilterComparison.Maximum;
    private FilterComparison forwardComparison = FilterComparison.Minimum;
    private CandidateListMode candidateListMode;
    private CandidateSortMode candidateSortMode = CandidateSortMode.Rating;
    private string statusText = "Waiting for mission terrain.";
    private float analysisStartedAt;
    private float samplingStartedAt;
    private float coverageStartedAt;
    private float refinementStartedAt;

    internal IReadOnlyList<SiteCandidate> SuggestedSites => suggestedSites;
    internal AnalyzerState State => state;
    internal string StatusText => statusText;
    internal bool IsReady => state == AnalyzerState.Ready;
    internal IReadOnlyList<SiteLayoutMarker> SiteLayout => siteLayout;
    internal int ActiveSiteIndex => FindSuggestedSiteIndex(activeCandidateId);
    internal bool HasActiveSite => activeCandidateId >= 0;
    internal bool ActiveSiteReady => HasActiveSite && !localRefinementActive && siteLayout.Count > 0;
    internal bool ShowProposalMarkers => showProposalMarkers;
    internal bool LimitRoadDistance => limitRoadDistance;
    internal float MaximumCandidateRange => maximumCandidateRange;
    internal float MinimumAreaCoverage => minimumAreaCoverage;
    internal float MinimumFrontShare => minimumFrontShare;
    internal float MaximumRisk => maximumRisk;
    internal float MinimumForwardCoverage => minimumForwardCoverage;
    internal FilterComparison RangeComparison => rangeComparison;
    internal FilterComparison AreaComparison => areaComparison;
    internal FilterComparison FrontComparison => frontComparison;
    internal FilterComparison RiskComparison => riskComparison;
    internal FilterComparison ForwardComparison => forwardComparison;
    internal CandidateListMode ListMode => candidateListMode;
    internal CandidateSortMode SortMode => candidateSortMode;
    internal float MaxRoadDistance => RequiredRoadDistance;
    internal Texture2D? CoverageOverlayTexture { get; private set; }
    internal bool CoverageOverlayEnabled { get; private set; }
    internal bool CoverageOverlayBuilding => coverageOverlayBuilding;
    internal bool CoverageOverlayReady => CoverageOverlayEnabled
        && CoverageOverlayTexture != null
        && !coverageOverlayBuilding;
    internal float CoverageOverlayProgress => CoverageOverlayTexture == null
        ? 0f
        : coverageOverlayBuilding
            ? (float)(coverageHorizonSampleIndex + coverageOverlayPixelIndex)
                / (CoverageHorizonSampleCount + CoverageOverlayTexture.width * CoverageOverlayTexture.height)
            : 1f;
    internal GlobalPosition CoverageOverlayOrigin => coverageOverlayCandidate.Position;
    internal float CoverageTargetAltitude => coverageTargetAltitude;
    internal int ScanQueriesPerFrame => Mathf.Clamp(CommanderSettings.SamScanQueriesPerFrame, 16, 1024);

    internal static bool TryGetStrategicTerrainHeight(float globalX, float globalZ, out float height)
    {
        height = 0f;
        return Instance != null
            && Instance.strategicHeightMap.TryGetHeight(globalX, globalZ, out height);
    }

    internal static bool TryGetStrategicHeightMapSize(out Vector2 size)
    {
        size = Instance?.strategicHeightMap.MapSize ?? Vector2.zero;
        return Instance?.strategicHeightMap.IsReady == true && size.x > 0f && size.y > 0f;
    }

    internal static float EstimateStrategicTerrainNormalY(float globalX, float globalZ, float spacing = 25f)
    {
        return Instance?.strategicHeightMap.EstimateNormalY(globalX, globalZ, spacing) ?? 0f;
    }

    internal static bool TryEvaluateLogisticsRisk(
        GlobalPosition start,
        GlobalPosition destination,
        IReadOnlyList<GlobalPosition>? route,
        out float risk,
        out float routeLength)
    {
        risk = 0.5f;
        routeLength = 0f;
        if (Instance == null || !Instance.EnsureStrategicInfluenceReady())
        {
            return false;
        }

        float weightedRisk = 0f;
        float totalWeight = 0f;
        float maximumRisk = 0f;
        GlobalPosition previous = start;
        int pointCount = (route?.Count ?? 0) + 1;
        for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
        {
            GlobalPosition next = route != null && pointIndex < route.Count
                ? route[pointIndex]
                : destination;
            float segmentLength = Mathf.Sqrt(HorizontalSquareDistance(previous, next));
            routeLength += segmentLength;
            int samples = Mathf.Max(1, Mathf.CeilToInt(segmentLength / 2000f));
            for (int sample = 0; sample < samples; sample++)
            {
                float t = (sample + 0.5f) / samples;
                GlobalPosition position = new(
                    Mathf.Lerp((float)previous.x, (float)next.x, t),
                    Mathf.Lerp((float)previous.y, (float)next.y, t),
                    Mathf.Lerp((float)previous.z, (float)next.z, t));
                float sampleRisk = Instance.CalculateRisk(position);
                float weight = Mathf.Lerp(1.15f, 0.85f, t);
                weightedRisk += sampleRisk * weight;
                totalWeight += weight;
                maximumRisk = Mathf.Max(maximumRisk, sampleRisk);
            }
            previous = next;
        }

        float sourceRisk = Instance.CalculateRisk(start);
        float averageRisk = totalWeight > 0f ? weightedRisk / totalWeight : sourceRisk;
        risk = Mathf.Clamp01(sourceRisk * 0.4f + averageRisk * 0.4f + maximumRisk * 0.2f);
        return true;
    }

    internal CommanderSamSiteAnalyzerService()
    {
        Instance = this;
    }

    internal float Progress
    {
        get
        {
            return state switch
            {
                AnalyzerState.Sampling => regionColumns * regionRows == 0
                    ? 0f
                    : (float)regionIndex / (regionColumns * regionRows),
                AnalyzerState.Coverage => candidates.Count == 0
                    ? 0f
                    : (float)coverageCandidateIndex / candidates.Count,
                AnalyzerState.Refining => localRefinementActive ? 0.5f : 0f,
                AnalyzerState.Ready => 1f,
                _ => 0f
            };
        }
    }

    public void Activate()
    {
    }

    public void Deactivate()
    {
        uiVisible = false;
        ClearActiveSite();
        CoverageOverlayEnabled = false;
        ClearCoverageOverlay();
    }

    public void ResetSession()
    {
        state = AnalyzerState.Waiting;
        mapSettings = null;
        mapKey = string.Empty;
        regionColumns = 0;
        regionRows = 0;
        regionIndex = 0;
        coverageCandidateIndex = 0;
        ResetCoverageAccumulator();
        coverageOverlayPixels = null;
        coverageOverlayPixelIndex = 0;
        coverageOverlayBuilding = false;
        activeCandidateId = -1;
        activeRefinementGeneration = 0;
        nextNearbyRefreshAt = 0f;
        nextInfluenceRefreshAt = 0f;
        showProposalMarkers = false;
        candidates.Clear();
        suggestedSites.Clear();
        siteLayout.Clear();
        strategicWeights = null;
        strategicRisks = null;
        strategicHeightMap.Reset();
        localHeightMapBaker.Reset();
        localSiteMaps.Clear();
        localRefinementActive = false;
        CoverageOverlayEnabled = false;
        ClearCoverageOverlay();
        statusText = "Waiting for mission terrain.";
    }

    public void TickPersistent()
    {
        UpdateCoverageOverlayBatch();

        if (state == AnalyzerState.Waiting)
        {
            TryStart();
            return;
        }

        if (state == AnalyzerState.Sampling)
        {
            SampleTerrainBatch();
        }
        else if (state == AnalyzerState.Coverage)
        {
            EvaluateCoverageBatch();
        }

        if ((state == AnalyzerState.Ready || state == AnalyzerState.Refining)
            && (uiVisible || showProposalMarkers))
        {
            RefreshNearbySites();
        }
    }

    public void TickActive()
    {
    }

    internal void SetUiVisible(bool visible)
    {
        if (uiVisible == visible)
        {
            return;
        }

        uiVisible = visible;
        if (uiVisible)
        {
            RefreshNearbySites(force: true);
            return;
        }
        if (!uiVisible)
        {
            showProposalMarkers = false;
            if (automaticSelectionCompleted == null)
            {
                ClearActiveSite();
            }
            ClearCoverageOverlay();
        }
    }

    internal void RebuildAnalysis()
    {
        if (automaticSelectionCompleted != null)
        {
            statusText = "AI site refinement is in progress; rebuild is temporarily unavailable.";
            return;
        }
        ResetAnalysisData();
        if (mapSettings != null)
        {
            BeginSampling(mapSettings);
        }
    }

    internal void SetLimitRoadDistance(bool enabled)
    {
        if (limitRoadDistance == enabled)
        {
            return;
        }

        limitRoadDistance = enabled;
        RefreshFilteredSuggestions();
    }

    internal void SetCandidateFilters(
        float maximumRange,
        float minimumAreaLos,
        float minimumFront,
        float riskLimit,
        float minimumForward)
    {
        maximumRange = Mathf.Max(0f, maximumRange);
        minimumAreaLos = Mathf.Clamp01(minimumAreaLos);
        minimumFront = Mathf.Clamp01(minimumFront);
        riskLimit = Mathf.Clamp01(riskLimit);
        minimumForward = Mathf.Clamp01(minimumForward);
        if (Mathf.Approximately(maximumCandidateRange, maximumRange)
            && Mathf.Approximately(minimumAreaCoverage, minimumAreaLos)
            && Mathf.Approximately(minimumFrontShare, minimumFront)
            && Mathf.Approximately(maximumRisk, riskLimit)
            && Mathf.Approximately(minimumForwardCoverage, minimumForward))
        {
            return;
        }

        maximumCandidateRange = maximumRange;
        minimumAreaCoverage = minimumAreaLos;
        minimumFrontShare = minimumFront;
        maximumRisk = riskLimit;
        minimumForwardCoverage = minimumForward;
        RefreshFilteredSuggestions();
    }

    internal void SetFilterComparison(int filter, FilterComparison comparison)
    {
        switch (filter)
        {
            case 0:
                rangeComparison = comparison;
                break;
            case 1:
                areaComparison = comparison;
                break;
            case 2:
                frontComparison = comparison;
                break;
            case 3:
                riskComparison = comparison;
                break;
            case 4:
                forwardComparison = comparison;
                break;
            default:
                return;
        }
        RefreshFilteredSuggestions();
    }

    internal void ResetCandidateFilters()
    {
        rangeComparison = FilterComparison.Maximum;
        areaComparison = FilterComparison.Minimum;
        frontComparison = FilterComparison.Minimum;
        riskComparison = FilterComparison.Maximum;
        forwardComparison = FilterComparison.Minimum;
        SetCandidateFilters(0f, 0f, 0f, 1f, 0f);
    }

    internal void SetCandidateListMode(CandidateListMode mode)
    {
        if (candidateListMode == mode)
        {
            return;
        }
        candidateListMode = mode;
        RefreshNearbySites(force: true);
    }

    internal void SetCandidateSortMode(CandidateSortMode mode)
    {
        if (candidateSortMode == mode)
        {
            return;
        }
        candidateSortMode = mode;
        candidateListMode = CandidateListMode.Ranked;
        RefreshNearbySites(force: true);
    }

    internal void JumpToSite(int index)
    {
        if (automaticSelectionCompleted != null)
        {
            statusText = "AI site refinement is in progress; wait for its construction request to finish.";
            return;
        }
        if (index < 0 || index >= suggestedSites.Count)
        {
            return;
        }

        SiteCandidate candidate = suggestedSites[index];
        if (activeCandidateId == candidate.CandidateId)
        {
            ClearActiveSite();
            statusText = $"Hidden SAM-site proposal {index + 1}.";
            return;
        }

        CommanderTacticalMapService? mapService = CommanderTacticalMapService.Instance;
        if (mapService?.JumpCameraToPosition(candidate.Position) != true)
        {
            statusText = "Could not move the camera to the selected proposal.";
            return;
        }

        BeginActiveSiteRefinement(candidate);
        statusText = $"Camera moved to SAM-site proposal {index + 1}; baking detailed terrain.";
        mapService.Close();
    }

    internal bool BeginAutomaticSiteSelection(
        bool useLocalCandidatePass,
        Action<bool> completed)
    {
        if (state != AnalyzerState.Ready || candidates.Count == 0 || localRefinementActive)
        {
            statusText = "Automatic site selection is waiting for completed terrain analysis.";
            return false;
        }

        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        GlobalPosition cameraPosition = cameraManager != null
            ? cameraManager.transform.position.ToGlobalPosition()
            : default;
        List<SiteCandidate> eligible = new(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            float distance = cameraManager == null
                ? 0f
                : Mathf.Sqrt(HorizontalSquareDistance(cameraPosition, candidates[i].Position));
            if (PassesActiveFilters(candidates[i]) && PassesRangeFilter(distance))
            {
                eligible.Add(candidates[i]);
            }
        }

        if (eligible.Count == 0)
        {
            statusText = "No SAM-site candidate satisfies the active AI thresholds.";
            return false;
        }

        SiteCandidate selected = eligible[UnityEngine.Random.Range(0, eligible.Count)];
        if (useLocalCandidatePass)
        {
            const float localRadiusSquared = 1000f * 1000f;
            List<SiteCandidate> nearby = new();
            for (int i = 0; i < eligible.Count; i++)
            {
                if (HorizontalSquareDistance(selected.Position, eligible[i].Position) <= localRadiusSquared)
                {
                    nearby.Add(eligible[i]);
                }
            }
            nearby.Sort(static (left, right) => right.Score.CompareTo(left.Score));
            int topCount = Mathf.Max(1, Mathf.CeilToInt(nearby.Count * 0.3f));
            selected = nearby[UnityEngine.Random.Range(0, topCount)];
        }

        automaticSelectionCompleted = completed;
        BeginActiveSiteRefinement(selected);
        if (!localRefinementActive)
        {
            CompleteAutomaticSelection(false);
            return false;
        }
        statusText = useLocalCandidatePass
            ? "AI selected a filtered candidate and is refining a random local top-30% site."
            : "AI selected a random filtered candidate and is refining its layout.";
        return true;
    }

    internal bool GenerateCoverageOverlay(Unit source, GlobalPosition position)
    {
        if (!strategicHeightMap.IsReady)
        {
            statusText = "Radar coverage is waiting for the strategic heightmap.";
            return false;
        }

        CoverageOverlayEnabled = true;
        GlobalPosition emitterPosition = ResolveRadarEmitterPosition(source, position);
        SiteCandidate candidate = new(
            emitterPosition,
            emitterPosition.y,
            0f,
            0f,
            0f,
            0f,
            0f);
        BeginCoverageOverlay(candidate, emitterPosition.y);
        coverageOverlaySource = source;
        statusText = "Generating radar coverage.";
        return true;
    }

    internal void SetCoverageTargetAltitude(float altitude)
    {
        float snapped = Mathf.Clamp(Mathf.Round(altitude / 25f) * 25f, 0f, 2000f);
        if (Mathf.Approximately(coverageTargetAltitude, snapped))
        {
            return;
        }

        coverageTargetAltitude = snapped;
        if (CoverageOverlayReady)
        {
            RecolorCoverageOverlay();
        }
    }

    private static GlobalPosition ResolveRadarEmitterPosition(Unit source, GlobalPosition fallback)
    {
        Radar[] radars = source.GetComponentsInChildren<Radar>(includeInactive: true);
        Transform? highestScanner = null;
        for (int i = 0; i < radars.Length; i++)
        {
            Transform scanner = radars[i].GetScanPoint();
            if (scanner != null && (highestScanner == null || scanner.position.y > highestScanner.position.y))
            {
                highestScanner = scanner;
            }
        }

        return highestScanner != null
            ? highestScanner.GlobalPosition()
            : new GlobalPosition(fallback.x, fallback.y + RadarHeight, fallback.z);
    }

    internal bool CoverageMatches(Unit source)
    {
        return CoverageOverlayEnabled
            && CoverageOverlayTexture != null
            && ReferenceEquals(coverageOverlaySource, source);
    }

    internal void RetainCoverageForSelection(IReadOnlyList<Unit> selectedUnits)
    {
        if (!CoverageOverlayEnabled || coverageOverlaySource == null)
        {
            return;
        }

        for (int i = 0; i < selectedUnits.Count; i++)
        {
            if (ReferenceEquals(selectedUnits[i], coverageOverlaySource))
            {
                return;
            }
        }

        CoverageOverlayEnabled = false;
        ClearCoverageOverlay();
    }

    internal void SetProposalMarkersVisible(bool visible)
    {
        showProposalMarkers = visible;
    }

    internal void CopyProposalSites(List<SiteCandidate> destination)
    {
        destination.Clear();
        if (!showProposalMarkers)
        {
            return;
        }
        destination.AddRange(suggestedSites);
    }

    internal void CopyActiveLayout(List<SiteLayoutMarker> destination)
    {
        destination.Clear();
        if (!HasActiveSite)
        {
            return;
        }

        for (int i = 0; i < siteLayout.Count; i++)
        {
            if (siteLayout[i].SiteIndex == activeCandidateId)
            {
                destination.Add(siteLayout[i]);
            }
        }
    }

    internal void CopyVisibleActiveLayout(List<SiteLayoutMarker> destination)
    {
        if (!uiVisible)
        {
            destination.Clear();
            return;
        }
        CopyActiveLayout(destination);
    }

    internal bool TryGetActiveEnemyDirection(out Vector2 direction)
    {
        if (HasActiveSite)
        {
            direction = FindEnemyFrontDirection(activeSite.Position);
            return true;
        }

        direction = Vector2.up;
        return false;
    }

}
