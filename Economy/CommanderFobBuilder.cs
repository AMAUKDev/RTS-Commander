using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using RoadPathfinding;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The construction half of a forward operating base (design.md, fob-construction_20260914;
/// feasibility.md §1, game calls verified against the decompiled assembly): creating a real runtime
/// airbase where the deliveries landed, putting the recipe's buildings inside its build ring,
/// and tearing the identity down again when every building on it is gone.
/// </summary>
/// <remarks>
/// A partial of <see cref="CommanderEconomyService"/> rather than a class of its own, for the same
/// reason <c>CommanderEconomyServiceEnemy.cs</c> is one: <c>SpawnBuilding</c>,
/// <c>LinkSpawnedBuilding</c>, <c>ResolveCategoryDefinition</c> and <c>GetStructureCost</c> are
/// private to that class and are exactly the four things construction has to reuse rather than
/// paraphrase (Reuse rule 3). The demand and delivery side is the operations service's, in
/// <c>Operations/CommanderOperationsFob.cs</c>.
/// <para>
/// The order of calls matters and is the game's own (feasibility §1): the base is created FIRST, so
/// the three buildings then go down inside its own build ring and no existing siting rule has to be
/// loosened for them; and the <see cref="SavedAirbase"/> is appended to
/// <c>MissionManager.CurrentMission.airbases</c> BEFORE the spawn, because a client joining later
/// runs <c>Airbase.OnStartClientOnly</c>, which looks the name up in that list and logs an error
/// when it is missing.
/// </para>
/// Every call in this file is server-only and guarded on <c>hq.IsServer</c>: a pure multiplayer
/// client must see a FOB and never create one.
/// </remarks>
internal sealed partial class CommanderEconomyService
{
    /// <summary>
    /// The recipe, in the order the buildings are sited (design Decision 3, fixed): the vehicle
    /// depot that makes the base worth having, the radar that lets it see, and TWO hangar-type pads
    /// that let helicopters use it (user instruction 2026-09-16, "FOBs should have 2x helipads
    /// rather than 1" — one pad is occupied the moment a single helicopter sits on it, and a
    /// forward base exists to turn rotary sorties around). Resolved off the live encyclopedia
    /// through <see cref="ResolveCategoryDefinition"/>, so a game patch that removes one of these
    /// categories degrades the FOB to "cannot be ordered" instead of to a null prefab.
    /// <para>This array is the ONE definition of what a FOB is: the price
    /// (<see cref="FobStructuresCost"/>), the room check (<see cref="CanSiteFob"/>), the layout
    /// (<see cref="FobStructureSite"/>) and the wording (<see cref="FobRecipeDescription"/>) are all
    /// read off it, so changing the recipe is one edit here and nowhere else.</para>
    /// </summary>
    private static readonly BuildingType[] FobRecipe =
    {
        BuildingType.DEP, BuildingType.RDR, BuildingType.HGR, BuildingType.HGR,
    };

    /// <summary>
    /// How many hangar-type pads the recipe puts on a forward base: read off
    /// <see cref="FobRecipe"/> rather than written down twice, so the price, the log lines and the
    /// self-check can never disagree with what is actually built.
    /// </summary>
    internal static int FobHelipadCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < FobRecipe.Length; i++)
            {
                if (FobRecipe[i] == BuildingType.HGR)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// The radius a ring of <paramref name="buildingCount"/> evenly spaced buildings needs so that
    /// neighbours stand <paramref name="spacingMeters"/> apart, pure: the chord of a regular polygon
    /// is <c>2 r sin(pi / n)</c>, so the radius is the spacing over that. Generalised from the
    /// hard-coded three-building <c>spacing / sqrt(3)</c> when the recipe grew a second helipad
    /// (2026-09-16); at three buildings it returns exactly what that expression did. Zero for fewer
    /// than two buildings, which have no neighbour to stand apart from.
    /// </summary>
    internal static float FobRingRadiusMeters(float spacingMeters, int buildingCount)
    {
        return buildingCount < 2
            ? 0f
            : spacingMeters / (2f * Mathf.Sin(Mathf.PI / buildingCount));
    }

    /// <summary>
    /// How far out from the centre a FOB's buildings are laid out, derived rather than chosen: a
    /// spacing of <c>CommanderOperationsService.FobBuildingSpacingMeters</c> over four buildings
    /// puts them about 85 m from the centre (it was about 69 m over three) — comfortably inside the
    /// 400 m capture ring and inside the base's build radius.
    /// </summary>
    private static float FobBuildingRingMeters =>
        FobRingRadiusMeters(CommanderOperationsService.FobBuildingSpacingMeters, FobRecipe.Length);

    /// <summary>
    /// What the recipe is, in words — the one definition behind the order line, the "online" line
    /// and the "no room" refusal (Reuse rule 4), so all three follow <see cref="FobRecipe"/> instead
    /// of each spelling out a building list that can drift from it.
    /// </summary>
    internal static string FobRecipeDescription()
    {
        int pads = FobHelipadCount;
        return pads == 1
            ? "depot, radar and helipad"
            : $"depot, radar and {pads} helipads";
    }

    /// <summary>Names a FOB counts up so two of them on one map never share a
    /// <c>UniqueName</c>; the game registers an airbase under that name and a duplicate would make
    /// one of them unreachable.</summary>
    private int fobNameCounter;

    /// <summary>
    /// What every forward operating base's name begins with. It is how a base is recognised as a FOB
    /// later — the cap counts the FOBs a commander OWNS, which includes one it took from the enemy
    /// and excludes one the enemy took from it, and ownership is the only record that survives a
    /// capture. Written once at creation and read by <see cref="CountOwnedFobs"/>.
    /// </summary>
    internal const string FobBasePrefix = "FOB ";

    /// <summary>
    /// Forward operating bases this faction holds right now (design.md, fob-construction_20260914
    /// Decision 7): every base whose name marks it as a FOB, whoever built it. Read off the live base
    /// list rather than off the order records so a FOB captured from the enemy counts against the
    /// captor's cap and a FOB lost to the enemy stops counting at once.
    /// </summary>
    internal static int CountOwnedFobs(FactionHQ hq)
    {
        int count = 0;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (IsFobBase(airbase))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>True when this airbase is one the mod built as a forward operating base.</summary>
    /// <summary>
    /// Pushes the forward-base name counter past every forward base already standing, so a rebuilt
    /// base cannot take a name one of them is using. The counter is not saved and is zeroed on
    /// reset (<c>ResetSession</c>), so after a mission restart it starts at one again while the
    /// restored bases carry their old numbers — the first rebuild would collide (study,
    /// 2026-09-17). Called once by the strategic restore, before it rebuilds anything.
    /// </summary>
    internal void SeedFobNameCounterFromWorld()
    {
        if (FactionRegistry.airbaseLookup == null)
        {
            return;
        }

        int highest = fobNameCounter;
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            if (!IsFobBase(entry.Value))
            {
                continue;
            }

            highest = Mathf.Max(highest, TrailingNumber(entry.Value.SavedAirbase?.UniqueName));
        }

        if (highest > fobNameCounter)
        {
            fobNameCounter = highest;
            CommanderPlugin.Log.LogInfo(
                $"Strategic load: forward-base name counter seeded to {fobNameCounter} so a rebuilt base "
                    + "cannot take a name already in use.");
        }
    }

    /// <summary>The number a forward base's unique name ends in, or zero when it ends in anything
    /// else. The names are built as <c>"FOB &lt;point&gt; &lt;n&gt;"</c>, so the trailing run of
    /// digits is the counter value. Pure, for the self-check.</summary>
    internal static int TrailingNumber(string? uniqueName)
    {
        if (string.IsNullOrEmpty(uniqueName))
        {
            return 0;
        }

        int end = uniqueName!.Length;
        int start = end;
        while (start > 0 && char.IsDigit(uniqueName[start - 1]))
        {
            start--;
        }

        if (start == end || !int.TryParse(uniqueName.Substring(start, end - start), out int value))
        {
            return 0;
        }

        return value;
    }

    internal static bool IsFobBase(Airbase? airbase)
    {
        string? name = airbase?.SavedAirbase?.UniqueName;
        return airbase != null
            && !airbase.disabled
            && name != null
            && name.StartsWith(FobBasePrefix, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Shortest horizontal distance from <paramref name="position"/> to any forward operating base on
    /// the map, whoever holds it — the spacing rule's input. <c>float.MaxValue</c> when there is
    /// none, so the first FOB of a match is never refused for being too near itself.
    /// </summary>
    internal static float NearestFobDistance(GlobalPosition position)
    {
        float best = float.MaxValue;
        foreach (FactionHQ candidate in FactionRegistry.GetAllHQs())
        {
            if (candidate == null)
            {
                continue;
            }

            foreach (Airbase airbase in candidate.GetAirbases())
            {
                if (!IsFobBase(airbase) || airbase.center == null)
                {
                    continue;
                }

                float distance = CommanderGameAccess.HorizontalDistance(
                    position.AsVector3(), airbase.center.GlobalPosition().AsVector3());
                if (distance < best)
                {
                    best = distance;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Every working vehicle depot this commander owns (reach-and-points, 2026-09-14). Read off
    /// <see cref="depotOwners"/> — the table the captured-depot watch already keeps up to date every
    /// five seconds — rather than a fresh <c>FindObjectsOfType</c> per call, because the reach pass
    /// asks this question once per commander per 30 s review and the scene walk is the expensive
    /// half of the watch.
    /// <para>An empty list is a real answer and not an error: a commander whose depots have all been
    /// destroyed, or one whose first sweep has not run yet, has none. The reach pass falls back to
    /// the centres of the bases it holds in that case, which is where its vehicles would deploy from
    /// once a depot is rebuilt.</para>
    /// </summary>
    internal static void CollectOwnedDepots(FactionHQ hq, List<VehicleDepot> into)
    {
        into.Clear();
        CommanderEconomyService? service = Instance;
        if (service == null)
        {
            return;
        }

        foreach (KeyValuePair<VehicleDepot, FactionHQ> entry in service.depotOwners)
        {
            VehicleDepot depot = entry.Key;
            if (depot == null || !ReferenceEquals(entry.Value, hq))
            {
                continue;
            }

            into.Add(depot);
        }
    }

    /// <summary>Whether a depot can actually build a vehicle right now: it exists and is not knocked
    /// out. A destroyed depot comes back when the game repairs it, so it stays in the owner table
    /// and is skipped here rather than forgotten.</summary>
    private static bool IsUsableDepot(VehicleDepot? depot)
    {
        return depot != null && !depot.disabled;
    }

    /// <summary>Scratch for <see cref="TryNearestOwnedDepot"/>, so the nearest-depot pick allocates
    /// nothing on the buy path it runs from.</summary>
    private static readonly List<VehicleDepot> nearestDepotScratch = new();
    private static readonly List<float> nearestDepotDistances = new();
    private static readonly List<bool> nearestDepotUsable = new();

    /// <summary>
    /// The working depot this commander owns nearest <paramref name="near"/>, and how far away it is
    /// (reach-and-points, 2026-09-14). This is what decides where a bought ground vehicle appears:
    /// the objective it was bought for picks the depot, so a platoon ordered for a forward base
    /// twenty kilometres out drives from the nearest base rather than from wherever the game's own
    /// deployment loop happened to reach first. False when the commander owns no working depot.
    /// </summary>
    internal static bool TryNearestOwnedDepot(
        FactionHQ hq, GlobalPosition near, out VehicleDepot depot, out float meters)
    {
        depot = null!;
        meters = float.MaxValue;
        CollectOwnedDepots(hq, nearestDepotScratch);
        nearestDepotDistances.Clear();
        nearestDepotUsable.Clear();
        for (int i = 0; i < nearestDepotScratch.Count; i++)
        {
            VehicleDepot candidate = nearestDepotScratch[i];
            nearestDepotDistances.Add(CommanderGameAccess.HorizontalDistance(
                near.AsVector3(), candidate.transform.GlobalPosition().AsVector3()));
            nearestDepotUsable.Add(IsUsableDepot(candidate));
        }

        int index = NearestUsableIndex(nearestDepotDistances, nearestDepotUsable);
        if (index >= 0)
        {
            depot = nearestDepotScratch[index];
            meters = nearestDepotDistances[index];
        }

        nearestDepotScratch.Clear();
        nearestDepotDistances.Clear();
        nearestDepotUsable.Clear();
        return index >= 0;
    }

    /// <summary>
    /// The nearest usable depot in a list of distances, pure, for the self-check: the index of the
    /// nearest entry whose matching <paramref name="usable"/> flag is true, or -1 when none is. The
    /// live pick above walks live objects; this is the same rule with the objects taken out, so a
    /// change to "nearer wins, a disabled one is skipped" is caught at load.
    /// </summary>
    internal static int NearestUsableIndex(IReadOnlyList<float> distances, IReadOnlyList<bool> usable)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < distances.Count && i < usable.Count; i++)
        {
            if (!usable[i] || distances[i] >= bestDistance)
            {
                continue;
            }

            bestDistance = distances[i];
            best = i;
        }

        return best;
    }

    /// <summary>
    /// What the building rung has banked toward its next structure for this commander — read by the
    /// FOB order so it only opens when the savings genuinely cover the whole price, rather than
    /// borrowing from the rungs below (user instruction 2026-09-14). The bank itself is
    /// <c>SpendEnemyStructures</c>'s; this is the one read outside it.
    /// </summary>
    internal static float StructureSavingsFor(FactionHQ hq)
    {
        CommanderEconomyService? service = Instance;
        return service != null && service.structureSavings.TryGetValue(hq, out float saved) ? saved : 0f;
    }

    /// <summary>
    /// What a FOB costs to order: every structure in <see cref="FobRecipe"/> at the same
    /// <see cref="GetStructureCost"/> the player's own BUILD window charges, so the second helipad
    /// is paid for the moment the recipe carries it. Zero when the encyclopedia is not up yet or has
    /// no structure for one of the categories, which is how the building rung's wish list says "no
    /// FOB is possible on this map".
    /// </summary>
    internal static float FobStructuresCost()
    {
        CommanderEconomyService? service = Instance;
        if (service == null)
        {
            return 0f;
        }

        float depot = 0f, radar = 0f, pad = 0f;
        for (int i = 0; i < FobRecipe.Length; i++)
        {
            BuildingDefinition? definition = service.ResolveCategoryDefinition(FobRecipe[i], preferDearest: false);
            float cost = definition == null ? 0f : GetStructureCost(definition);
            switch (FobRecipe[i])
            {
                case BuildingType.DEP: depot = cost; break;
                case BuildingType.RDR: radar = cost; break;
                default: pad = cost; break;
            }
        }

        return FobRecipePrice(depot, radar, pad);
    }

    /// <summary>
    /// What the recipe as it stands costs, pure for the self-check: the depot, the radar and one
    /// pad price charged <see cref="FobHelipadCount"/> times. It exists so the pad count the price
    /// is charged for has exactly ONE source — the recipe array itself — rather than being counted
    /// again at the call site, where it could be miscounted or hard-coded and nothing at load would
    /// notice. <see cref="FobStructuresCost"/> needs the live encyclopedia and so cannot be checked
    /// at load; this is the part of it that can.
    /// </summary>
    internal static float FobRecipePrice(float depotCost, float radarCost, float padCost)
    {
        return FobPrice(depotCost, radarCost, padCost, FobHelipadCount);
    }

    /// <summary>
    /// The FOB's price, pure for the self-check: the depot, the radar and
    /// <paramref name="padCount"/> pads added up, and ZERO when any one of them is missing from the
    /// encyclopedia. A FOB is the fixed recipe (design Decision 3) and part of one is not a cheaper
    /// FOB, it is a base with nothing to launch from or nothing to deploy from — so a map whose
    /// catalogue is short of a category cannot be offered the order at all. The pad count comes from
    /// the recipe (<see cref="FobHelipadCount"/>, two since 2026-09-16), so a recipe with more pads
    /// is charged for them rather than quietly getting them free.
    /// </summary>
    internal static float FobPrice(float depotCost, float radarCost, float padCost, int padCount)
    {
        return depotCost > 0f && radarCost > 0f && padCost > 0f && padCount > 0
            ? depotCost + radarCost + (padCost * padCount)
            : 0f;
    }

    /// <summary>What a held base is missing, if anything (design Section 4).</summary>
    internal enum CommanderBaseFacility
    {
        /// <summary>Every base this commander holds can deploy vehicles and take helicopters.</summary>
        None,

        /// <summary>A base has no working vehicle depot, so it deploys nothing.</summary>
        Depot,

        /// <summary>A base has no vertical landing point, so no helicopter launches or recovers
        /// there.</summary>
        Pad,
    }

    /// <summary>
    /// Which facility the building rung fixes first, pure (design Section 4): the depot before the
    /// pad, because a base that deploys no vehicles cannot hold the ground it stands on, while one
    /// that takes no helicopters is merely less useful. ONE definition, read by both the rung's cost
    /// walk and its spend, so the rung can never bank toward one and then buy the other.
    /// </summary>
    internal static CommanderBaseFacility NextBaseFacility(bool baseWithoutDepot, bool baseWithoutPad)
    {
        if (baseWithoutDepot)
        {
            return CommanderBaseFacility.Depot;
        }

        return baseWithoutPad ? CommanderBaseFacility.Pad : CommanderBaseFacility.None;
    }

    // ---- Captured bases (design Section 4) ------------------------------------------------------

    /// <summary>
    /// How far from a base's centre a building still counts as that base's: 1500 m, comfortably past
    /// the 900 m outer edge of <see cref="TryFindSiteNear"/>'s own siting ring — so a depot this
    /// commander has just built beside a base reads as covering it on the very next review instead
    /// of being built again — and well inside the spacing between two real airfields.
    /// </summary>
    private const float BaseBuildingCoverageMeters = 1500f;

    /// <summary>
    /// Who each live depot was last registered with. The capture watch below only calls
    /// <c>AddDepot</c> when this says the owner has CHANGED, because the game's own
    /// <see cref="FactionHQ.AddDepot"/> appends without checking for a duplicate — registering a
    /// depot the game already registered at spawn would put it in the deployment list twice.
    /// </summary>
    private readonly Dictionary<VehicleDepot, FactionHQ> depotOwners = new();

    /// <summary>
    /// How often the captured-depot watch runs: every 5 s, matching the depot rally's own refresh
    /// (<c>Depot/CommanderSpawnService.cs</c>), which walks the same objects to answer the same
    /// question. Fast enough that a base taken mid-push starts deploying within a few seconds of the
    /// capture, slow enough that the scene walk costs nothing measurable.
    /// </summary>
    private const float CapturedDepotSweepSeconds = 5f;

    private float nextCapturedDepotSweepAt =
        CommanderScheduler.Stagger("economy.capturedDepots", CapturedDepotSweepSeconds);

    /// <summary>Scratch for the depot walk, so the watch does not allocate every tick.</summary>
    private static readonly List<VehicleDepot> depotScratch = new();

    /// <summary>
    /// Re-registers a captured depot with the faction that took it (design.md,
    /// fob-construction_20260914 Decision 6; feasibility §6a). The game registers a depot with its
    /// owner exactly once, when it spawns, and ships no unregister call at all — so a base that
    /// changes hands leaves its depot in the loser's deployment list and out of the winner's, and
    /// the winner never deploys from the base it just took.
    /// </summary>
    /// <remarks>
    /// A poll rather than a hook on <c>Airbase.onTakeControl</c>, deliberately. That event fires
    /// from inside <c>CaptureFaction</c> BEFORE the buildings' <c>NetworkHQ</c> is moved and five
    /// seconds before <c>WaitRepair</c> un-disables a depot that was knocked out in the fight for the
    /// base — and the game's deployment loop drops a disabled depot from its list on the very next
    /// pass. A one-shot re-registration at event time would therefore register a depot that is then
    /// pruned and never re-added; this retries until the depot is alive and owned, which is the
    /// behaviour the fix actually needs. The other half of the fix — stopping the LOSER feeding a
    /// depot it no longer owns — is <c>CommanderFactionVehicleService.ShouldBlockAutomaticDeployment</c>.
    /// </remarks>
    private void WatchCapturedDepots()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null || !localHq.IsServer)
        {
            return;
        }

        depotScratch.Clear();
        depotScratch.AddRange(UnityEngine.Object.FindObjectsOfType<VehicleDepot>());
        for (int i = 0; i < depotScratch.Count; i++)
        {
            VehicleDepot depot = depotScratch[i];
            FactionHQ? owner = depot == null ? null : depot.NetworkHQ;
            if (depot == null || owner == null || depot.disabled)
            {
                // A dead or knocked-out depot is left alone: the game prunes it from the deployment
                // list on its own, and it is re-registered here when it comes back and changes hands.
                continue;
            }

            if (!depotOwners.TryGetValue(depot, out FactionHQ? last))
            {
                // First sight. The game registered this depot with its owner when it spawned, so
                // recording the owner is all that is wanted — adding it again would duplicate it.
                depotOwners[depot] = owner;
                // The mission audit (user report 2026-09-14): a depot the mission author placed on
                // the far side of the runway from the road drives every vehicle across the strip.
                // Said once per depot, with the runway's ends, so the mission file can be corrected.
                if (CommanderBuildPreview.RoadPathCrossesRunway(depot.transform.GlobalPosition(), out string runway))
                {
                    GlobalPosition at = depot.transform.GlobalPosition();
                    CommanderPlugin.Log.LogWarning(
                        $"Mission depot {CommanderGameAccess.GetUnitLabel(depot)} at ({at.x:0},{at.z:0}) sends its vehicles across "
                            + $"{runway}; move it to the road side of the strip.");
                }

                continue;
            }

            if (ReferenceEquals(last, owner))
            {
                continue;
            }

            depotOwners[depot] = owner;
            owner.AddDepot(depot);
            CommanderAiLog.Note(
                owner,
                $"{NearestHeldBaseLabel(owner, depot.transform.GlobalPosition())} captured: "
                    + $"depot re-registered to {owner.faction?.factionName ?? "this faction"}.");
        }

        // Depots that have gone are dropped so the table does not grow across a long match.
        if (depotOwners.Count > depotScratch.Count)
        {
            PruneDepotOwners();
        }
    }

    private void PruneDepotOwners()
    {
        depotPruneScratch.Clear();
        foreach (KeyValuePair<VehicleDepot, FactionHQ> entry in depotOwners)
        {
            // A destroyed depot compares equal to null through Unity's overload while the dictionary
            // still holds the reference, which is exactly the entry to drop.
            if (entry.Key == null)
            {
                depotPruneScratch.Add(entry);
            }
        }

        for (int i = 0; i < depotPruneScratch.Count; i++)
        {
            depotOwners.Remove(depotPruneScratch[i].Key);
        }
    }

    private readonly List<KeyValuePair<VehicleDepot, FactionHQ>> depotPruneScratch = new();

    /// <summary>
    /// The first base this commander holds that has no working vehicle depot (design Section 4,
    /// feasibility §6). Two independent tests, because either one alone has a hole: the base's own
    /// <c>buildings</c> list is exact for a mission-authored depot and survives capture, while
    /// <see cref="HasBuildingNear"/> covers a depot this mod spawned whose link to the base did not
    /// take. <see cref="TryGetUncoveredBase"/> was not reused for this: it reads
    /// <c>hq.factionUnits</c> only, and a depot that was knocked out at the moment of capture stays
    /// in the LOSING faction's unit list (<c>Unit.HQChanged</c> returns early for a disabled unit),
    /// which is exactly the case this feature exists to answer.
    /// </summary>
    internal static bool TryGetBaseWithoutDepot(FactionHQ hq, out GlobalPosition center)
    {
        center = default;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            // A ship's deck is an airbase too (AttachedAirbase), and it wants no depot, pad, radar
            // or pillbox built in the sea beside it (user, 2026-09-14).
            if (airbase == null || airbase.disabled || airbase.center == null || CommanderGameAccess.IsShipAirbase(airbase))
            {
                continue;
            }

            GlobalPosition candidate = airbase.center.GlobalPosition();
            if (HasLiveDepot(airbase) || HasBuildingNear(hq, BuildingType.DEP, candidate, BaseBuildingCoverageMeters))
            {
                continue;
            }

            center = candidate;
            return true;
        }

        return false;
    }

    /// <summary>Destroyed vehicle depots of this faction within <paramref name="radius"/> of a
    /// point, for the wish line: a depot that keeps dying reads differently from one never built.</summary>
    private static int CountDeadDepotsNear(FactionHQ hq, GlobalPosition position, float radius)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is VehicleDepot
                && unit.disabled
                && FastMath.InRange(unit.transform.GlobalPosition(), position, radius))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>True when one of this base's own buildings is a working vehicle depot.</summary>
    private static bool HasLiveDepot(Airbase airbase)
    {
        List<Building> buildings = airbase.buildings;
        for (int i = 0; buildings != null && i < buildings.Count; i++)
        {
            if (buildings[i] is VehicleDepot && !buildings[i].disabled)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The first base this commander holds with nowhere for a helicopter to set down (design
    /// Section 4, feasibility §6(c)): the mod's own rotary recovery calls
    /// <c>Airbase.TryRequestVerticalLanding</c>, which fails outright on a base whose
    /// <c>verticalLandingPoints</c> array is empty, so such a base can neither launch nor recover
    /// helicopters however many hangars it has.
    /// </summary>
    internal static bool TryGetBaseWithoutPad(FactionHQ hq, out GlobalPosition center)
    {
        center = default;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null
                || airbase.disabled
                || airbase.center == null
                || CommanderGameAccess.IsShipAirbase(airbase)
                || (airbase.verticalLandingPoints != null && airbase.verticalLandingPoints.Length > 0))
            {
                continue;
            }

            center = airbase.center.GlobalPosition();
            return true;
        }

        return false;
    }

    /// <summary>
    /// The cheapest hangar-type structure whose authored roster actually holds an airframe the
    /// rotary flight model can fly. A second resolver beside
    /// <see cref="ResolveCategoryDefinition"/> rather than a parameter on it, because it answers a
    /// different question: that one asks "which structure stands for this category", this one asks
    /// "which hangar can take helicopters" — and the cheapest <c>HGR</c> in the encyclopedia is
    /// often a fixed-wing shelter, which <see cref="LinkSpawnedBuilding"/> deliberately gives no
    /// landing point (a helicopter homing on a shelter's spawn spot descends onto the roof).
    /// </summary>
    private BuildingDefinition? ResolveRotaryHangarDefinition()
    {
        if (rotaryHangarResolved)
        {
            return rotaryHangar;
        }

        IReadOnlyList<BuildingDefinition> entries = Catalog;
        if (entries.Count == 0)
        {
            // The encyclopedia is not up yet. Ask again next review rather than caching a null.
            return null;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            BuildingDefinition candidate = entries[i];
            if (candidate.buildingType != BuildingType.HGR || candidate.unitPrefab == null)
            {
                continue;
            }

            Hangar? hangar = candidate.unitPrefab.GetComponent<Hangar>();
            if (hangar == null || !CanHostRotaryAircraft(hangar))
            {
                continue;
            }

            if (rotaryHangar == null || candidate.value < rotaryHangar.value)
            {
                rotaryHangar = candidate;
            }
        }

        rotaryHangarResolved = true;
        CommanderPlugin.Log.LogInfo(rotaryHangar == null
            ? "No helicopter-capable hangar in the encyclopedia, so no commander will build a helipad."
            : $"Commanders will build {GetStructureLabel(rotaryHangar)} for helipads "
                + $"({GetStructureCost(rotaryHangar):0}).");
        return rotaryHangar;
    }

    private BuildingDefinition? rotaryHangar;
    private bool rotaryHangarResolved;

    /// <summary>
    /// What a base with no depot, or no landing pad, costs the building rung to fix — the first two
    /// entries in <see cref="GetEnemyBuildReserve"/>'s wish list (design Section 4). Zero when every
    /// held base already has both. Ahead of radar, mines and factories because a base that cannot
    /// deploy a vehicle or recover a helicopter is not a base the commander can use at all, which is
    /// a bigger hole than any of those fill.
    /// </summary>
    internal static float NextBaseFacilityCost(FactionHQ hq)
    {
        CommanderEconomyService? service = Instance;
        if (service == null || hq == null || hq.faction == null)
        {
            return 0f;
        }

        BuildingDefinition? depot = service.ResolveCategoryDefinition(BuildingType.DEP, preferDearest: false);
        BuildingDefinition? pad = service.ResolveRotaryHangarDefinition();
        switch (NextBaseFacility(
            depot != null && TryGetBaseWithoutDepot(hq, out _),
            pad != null && TryGetBaseWithoutPad(hq, out _)))
        {
            case CommanderBaseFacility.Depot:
                return GetStructureCost(depot!);
            case CommanderBaseFacility.Pad:
                return GetStructureCost(pad!);
            default:
                return 0f;
        }
    }

    /// <summary>
    /// Builds whatever <see cref="NextBaseFacilityCost"/> named, in the same order so the rung can
    /// never bank toward one and buy the other. Sited with <see cref="TryFindSiteNear"/>, the
    /// radar's own siter, so the structure lands inside the base's build ring and under the site
    /// rules every other build obeys.
    /// </summary>
    private bool TryBuildBaseFacility(FactionHQ hq)
    {
        BuildingDefinition? depot = ResolveCategoryDefinition(BuildingType.DEP, preferDearest: false);
        BuildingDefinition? pad = ResolveRotaryHangarDefinition();
        GlobalPosition depotBase = default;
        GlobalPosition padBase = default;
        bool wantsDepot = depot != null && TryGetBaseWithoutDepot(hq, out depotBase);
        bool wantsPad = pad != null && TryGetBaseWithoutPad(hq, out padBase);

        // The same table the cost walk read, so the rung can never bank toward one and buy the other.
        switch (NextBaseFacility(wantsDepot, wantsPad))
        {
            case CommanderBaseFacility.Depot:
                if (!TryFindSiteNear(hq, depot!, depotBase, out GlobalPosition depotSite)
                    || SpawnBuilding(hq, depotSite, depot!, GetStructureLabel(depot!), randomRotation: true) == null)
                {
                    return false;
                }

                hq.AddFunds(-GetStructureCost(depot!));
                // The dead count says whether this is a base that never had one or one whose depot
                // keeps being destroyed (diagnostic, 2026-09-14: one base logged this wish eight
                // times in a match with nothing to say which).
                CommanderAiLog.Note(
                    hq,
                    $"{NearestHeldBaseLabel(hq, depotBase)} has no working depot "
                        + $"({CountDeadDepotsNear(hq, depotBase, BaseBuildingCoverageMeters)} destroyed nearby); building one so it can deploy.");
                return true;

            case CommanderBaseFacility.Pad:
                if (!TryFindSiteNear(hq, pad!, padBase, out GlobalPosition padSite)
                    || SpawnBuilding(hq, padSite, pad!, GetStructureLabel(pad!), randomRotation: true) == null)
                {
                    return false;
                }

                hq.AddFunds(-GetStructureCost(pad!));
                CommanderAiLog.Note(
                    hq,
                    $"{NearestHeldBaseLabel(hq, padBase)} has no landing pad; building one so helicopters can use it.");
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// The base this commander holds nearest <paramref name="position"/>, by name, for the log lines
    /// above — the siting anchor is a base centre, so it matches exactly, and a captured depot is a
    /// few hundred metres off one, so the nearest within the base coverage ring is the right answer.
    /// <para>Widened from private to internal on 2026-09-14: the AI's buy line names the base a
    /// vehicle was spawned at, and a depot is a few hundred metres off its base centre, which is
    /// exactly the question this already answers (Reuse rule 4).</para>
    /// </summary>
    internal static string NearestHeldBaseLabel(FactionHQ hq, GlobalPosition position)
    {
        Airbase? best = null;
        float bestDistance = BaseBuildingCoverageMeters;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null || airbase.center == null)
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(
                airbase.center.GlobalPosition().AsVector3(), position.AsVector3());
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = airbase;
            }
        }

        return best != null ? CommanderCaptureService.GetAirbaseLabel(best) : "A held base";
    }

    /// <summary>
    /// Builds the whole FOB at <paramref name="position"/>: the airbase first, then the three
    /// structures inside its ring. <paramref name="built"/> collects the buildings that actually
    /// went down, so the teardown watch measures the real recipe rather than an assumed three.
    /// Returns false — having torn the base identity down again — when the ground took no buildings
    /// at all, so the order is cancelled rather than leaving an empty registered base standing.
    /// </summary>
    internal bool TryBuildFob(
        FactionHQ hq,
        GlobalPosition position,
        string pointLabel,
        List<Unit> built,
        out Airbase? airbase)
    {
        airbase = null;
        if (hq == null || !hq.IsServer)
        {
            return false;
        }

        if (!TryCreateFobAirbase(hq, position, pointLabel, out airbase) || airbase == null)
        {
            return false;
        }

        BuildFobStructures(hq, airbase, position, built);
        if (built.Count > 0)
        {
            return true;
        }

        CommanderPlugin.Log.LogWarning(
            $"FOB {pointLabel}: the base was created but no structure could be sited inside it; "
                + "the base is removed again.");
        TearDownFob(hq, airbase);
        airbase = null;
        return false;
    }

    /// <summary>
    /// Creates the runtime airbase (feasibility §1). The faction is carried on the
    /// <see cref="SavedAirbase"/> as a name, and <c>Airbase.SetupCustomAirbase</c> resolves it
    /// through <c>FindHQ</c> and calls <c>hq.AddAirbase</c> itself, so ownership needs no second
    /// call here. Runways, vertical landing points and service points are left empty on purpose: the
    /// helipad appends its own landing point when <see cref="LinkSpawnedBuilding"/> links it, and a
    /// FOB has no strip.
    /// </summary>
    private bool TryCreateFobAirbase(
        FactionHQ hq, GlobalPosition position, string pointLabel, out Airbase? airbase)
    {
        airbase = null;
        Mission? mission = MissionManager.CurrentMission;
        NetworkManagerNuclearOption? manager = NetworkManagerNuclearOption.i;
        if (mission == null
            || manager?.ServerObjectManager == null
            || !manager.Server.Active
            || hq.faction == null)
        {
            return false;
        }

        GlobalPosition ground = CommanderGameAccess.SnapToTerrain(position);
        string displayName = $"{FobBasePrefix}{pointLabel}";
        SavedAirbase saved = new()
        {
            // The name is what marks this base as a FOB for the cap and the spacing rule, so the
            // prefix is not decoration — see FobBasePrefix.
            UniqueName = $"{displayName} {++fobNameCounter}",
            DisplayName = displayName,
            faction = hq.faction.factionName,
            Capturable = true,
            CaptureRange = CommanderOperationsService.FobCaptureRangeMeters,
            Center = ground,
            SelectionPosition = ground,
            roads = new RoadNetwork(),
            runways = new List<SavedRunway>(),
            VerticalLandingPoints = new List<GlobalPosition>(),
            ServicePoints = new List<GlobalPosition>(),
        };

        // Registered BEFORE the spawn: a client that joins later resolves a custom airbase by name
        // out of this list, and logs an error when the name is not in it (feasibility §1).
        mission.airbases.Add(saved);
        airbase = mission.SpawnCustomAirbase(saved, manager.ServerObjectManager);
        if (airbase == null)
        {
            mission.airbases.Remove(saved);
            return false;
        }

        CommanderPlugin.Log.LogInfo(
            $"Created {saved.UniqueName} for {hq.faction.factionName} at {ground.x:0}, {ground.z:0} "
                + $"({CommanderOperationsService.FobCaptureRangeMeters:0} m capture ring, capturable).");
        return true;
    }

    /// <summary>
    /// Puts the recipe down inside the new base's ring: one spot per <see cref="FobRecipe"/> entry,
    /// evenly spaced on <see cref="FobBuildingRingMeters"/> so neighbours stand
    /// <c>CommanderOperationsService.FobBuildingSpacingMeters</c> apart, each moved to the nearest
    /// level ground by the points service's own levelness search (Reuse rule 3 — one levelness rule
    /// in the mod, not two). Each spawn goes through <see cref="SpawnBuilding"/>, so the site rules
    /// and the helipad-to-base link are the ones every other build path already uses.
    /// </summary>
    private void BuildFobStructures(FactionHQ hq, Airbase airbase, GlobalPosition center, List<Unit> built)
    {
        for (int i = 0; i < FobRecipe.Length; i++)
        {
            BuildingDefinition? definition = ResolveCategoryDefinition(FobRecipe[i], preferDearest: false);
            if (definition == null)
            {
                continue;
            }

            GlobalPosition site = FobStructureSite(center, i);
            Unit? spawned = SpawnBuilding(hq, site, definition, GetStructureLabel(definition));
            if (spawned == null)
            {
                CommanderPlugin.Log.LogInfo(
                    $"{CommanderCaptureService.GetAirbaseLabel(airbase)}: no room for a "
                        + $"{GetStructureLabel(definition)} on that ground.");
                continue;
            }

            built.Add(spawned);
        }
    }

    /// <summary>The spot the <paramref name="index"/>th recipe building stands on: evenly spaced on
    /// <see cref="FobBuildingRingMeters"/> around <paramref name="center"/>, moved to the nearest
    /// level ground by the points service's own levelness search. One definition for the build and
    /// for the room check below (Reuse rule 4).</summary>
    private static GlobalPosition FobStructureSite(GlobalPosition center, int index)
    {
        float ring = FobBuildingRingMeters;
        float angle = index * (Mathf.PI * 2f / FobRecipe.Length);
        GlobalPosition candidate = new(
            center.x + Mathf.Cos(angle) * ring,
            center.y,
            center.z + Mathf.Sin(angle) * ring);
        return CommanderStrategicPointService.TryFindLevelBuildingSpot(candidate, out GlobalPosition site)
            ? site
            : candidate;
    }

    /// <summary>
    /// Whether the whole recipe would fit around <paramref name="center"/>, judged by the same site
    /// rule the build itself applies, BEFORE an order is paid for (fix, 2026-09-15: the enemy's first
    /// ever delivered load landed at RESOURCE SITE 6, the base went up, and every one of the three
    /// buildings came back "no room on that ground", so 75 funds and a flight bought a base that was
    /// torn down the same second). <paramref name="blocked"/> names the first building that does not
    /// fit and the rule that refused it.
    /// </summary>
    internal bool CanSiteFob(FactionHQ hq, GlobalPosition center, out string blocked)
    {
        _ = hq;
        for (int i = 0; i < FobRecipe.Length; i++)
        {
            BuildingDefinition? definition = ResolveCategoryDefinition(FobRecipe[i], preferDearest: false);
            if (definition == null)
            {
                continue;
            }

            GlobalPosition site = FobStructureSite(center, i);
            // Asked with hq null, the existing "is this patch of ground clear" convention (see
            // CommanderBuildPreview.Evaluate): the base-radius rule is left out on purpose, because
            // the base the buildings will stand inside does not exist until the load has landed.
            // The first live run of this check (2026-09-15) refused every site on that rule alone.
            if (!preview.IsSiteAllowed(definition, site, null, out string reason))
            {
                blocked = $"no room for the {GetStructureLabel(definition)} ({reason})";
                return false;
            }
        }

        blocked = string.Empty;
        return true;
    }

    /// <summary>
    /// Removes a FOB's airbase identity when every building on it has gone (design Section 2,
    /// feasibility §5's teardown risk): the base leaves the faction's list, the
    /// <see cref="SavedAirbase"/> leaves the mission so a joining client does not resolve a base
    /// that is not there, and the object is despawned. Harmless when the base is already gone.
    /// </summary>
    internal void TearDownFob(FactionHQ hq, Airbase? airbase)
    {
        if (hq == null || !hq.IsServer || airbase == null)
        {
            return;
        }

        string label = CommanderCaptureService.GetAirbaseLabel(airbase);
        SavedAirbase? saved = airbase.SavedAirbase;
        if (ReferenceEquals(airbase.CurrentHQ, hq))
        {
            hq.RemoveAirbase(airbase);
        }

        Mission? mission = MissionManager.CurrentMission;
        if (mission != null && saved != null)
        {
            mission.airbases.Remove(saved);
        }

        NetworkManagerNuclearOption? manager = NetworkManagerNuclearOption.i;
        if (airbase.Identity != null && manager?.ServerObjectManager != null)
        {
            manager.ServerObjectManager.Destroy(airbase.Identity, !airbase.Identity.IsSceneObject);
        }

        CommanderPlugin.Log.LogInfo($"Removed {label}: every building on it was destroyed.");
    }

    /// <summary>
    /// Despawns one unit through the server object manager rather than <c>Destroy</c>, so it leaves
    /// every client's world too. THE definition of "remove this unit from the game": the player's
    /// <see cref="Demolish"/> button, the FOB order consuming its deliveries (design Decision 2) and
    /// a FOB being abandoned (Decision 8) all end here. Returns false when there is nothing to
    /// despawn or no server to despawn it on.
    /// </summary>
    internal static bool DespawnUnit(Unit? unit)
    {
        NetworkManagerNuclearOption? manager = NetworkManagerNuclearOption.i;
        if (unit == null || unit.Identity == null || manager?.ServerObjectManager == null)
        {
            return false;
        }

        Instance?.builtUnits.Remove(unit);
        manager.ServerObjectManager.Destroy(unit.Identity, !unit.Identity.IsSceneObject);
        return true;
    }
}
