using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// ARAD: anti-radiation sorties and the ARAD-first gate. Split out of <c>CommanderOperationsAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past six thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderOperationsService
{
    // ---- ARAD (design.md, smarter-air-wing_20260914 Section 5) ----

    /// <summary>Sorties whose demand closed this review but whose minimum hold has not run out
    /// (<see cref="SortieMinHoldSeconds"/>). Rebuilt by every reconcile.</summary>
    private readonly List<CommanderAirSortie> heldSorties = new();

    /// <summary>Sorties this review's demand did not match, in their old priority order, waiting for
    /// the match pass to finish before they are held or let go. Rebuilt by every reconcile.</summary>
    private readonly List<CommanderAirSortie> dissolvingSorties = new();

    private readonly List<GlobalPosition> aradEmitters = new();
    private readonly List<int> aradClusterOf = new();
    private readonly List<CommanderAirSortie> aradDemand = new();

    /// <summary>
    /// Cluster this commander's tracked hostile air-defence vehicles and open an anti-radiation
    /// sortie over any belt of <c>AradClusterMinimum</c> or more that sits near an objective the
    /// wing is already flying against. Inserted directly after the AWACS so the demand queue, the
    /// fill and the buy all agree that suppression comes before the CAS it is clearing the way for.
    /// <para>The objective's own package is marked to wait at its form-up point until the ARAD
    /// sortie has gone in — bounded, like every other reason a package holds.</para>
    /// </summary>
    /// <summary>
    /// Whether a belt of <paramref name="clusteredLaunchers"/> tracked air-defence vehicles, all in
    /// ONE cluster, is worth suppressing: <paramref name="clusterMinimum"/> or more
    /// (<c>CommanderSettings.AradClusterMinimum</c>, 3). Pure.
    /// <para>
    /// The wing's ONE idea of "a belt", and the reason it is a named rule rather than an inline
    /// comparison (user decision 2026-09-16: "only hold for a belt worth sweeping"). Two callers ask
    /// it: <see cref="AradWanted"/>, which sizes the suppression sortie, and the sortie posture's belt
    /// hold (<c>CommanderOperationsAirPosture.cs</c>), which decides whether a formation waits for
    /// that sortie. They disagreed for one day and it cost airframes: the hold triggered on a single
    /// tracked launcher while a sweep was only ever opened for a cluster of three, so a one- or
    /// two-launcher belt held a sortie for a sweep that could never be bought and the hold always ran
    /// to its five-minute stand-down. The running log showed twelve such holds — five over one site,
    /// seven over two — and two sorties stood down before it was found.
    /// </para>
    /// <para>
    /// Below the threshold the sortie does NOT hold: it flies on, and what keeps it alive is the
    /// aircraft's own equipment — the game's threat-vector avoidance, its radar-warning descent and
    /// flares, and the bravery refusal restored in
    /// <c>CommanderAirCommandService.TargetIsTooDangerous</c>. A single launcher is a risk the pilot
    /// handles; a belt is a problem the commander has to solve, and the only solution it has is a
    /// sweep.
    /// </para>
    /// </summary>
    internal static bool BeltWorthSuppressing(int clusteredLaunchers, int clusterMinimum)
    {
        return clusteredLaunchers >= Mathf.Max(1, clusterMinimum);
    }

    private void InsertAradDemand(FactionHQ hq, List<CommanderAirSortie> demand)
    {
        aradDemand.Clear();

        // Nothing on the roster can carry a real anti-radiation missile → no suppression sortie is
        // opened at all (fix, 2026-09-14, the AWACS gate's own rule one line up). The tightened
        // weapon test can now answer "no" where the old one always found something, and an
        // unfillable sortie sitting in the demand queue is exactly what starved the wing before.
        if (!CommanderEnemyCommanderService.HasRoleCandidate(hq, CommanderEnemyCommanderService.AirRole.Arad))
        {
            return;
        }

        // A cheap pre-filter on the raw total before the clustering pass: too few tracked launchers
        // ANYWHERE to make one belt means there is no belt to find (BeltWorthSuppressing, the wing's
        // one idea of a belt).
        CollectTrackedAirDefence(hq, aradEmitters);
        if (!BeltWorthSuppressing(aradEmitters.Count, CommanderSettings.AradClusterMinimum))
        {
            return;
        }

        int clusterCount = BuildAirDefenceClusters(aradEmitters, aradClusterOf);
        for (int cluster = 0; cluster < clusterCount; cluster++)
        {
            int size = 0;
            float sumX = 0f;
            float sumY = 0f;
            float sumZ = 0f;
            for (int i = 0; i < aradEmitters.Count; i++)
            {
                if (aradClusterOf[i] != cluster)
                {
                    continue;
                }

                size++;
                sumX += aradEmitters[i].x;
                sumY += aradEmitters[i].y;
                sumZ += aradEmitters[i].z;
            }

            int wanted = AradWanted(size, CommanderSettings.AradClusterMinimum);
            if (wanted <= 0)
            {
                continue;
            }

            GlobalPosition centroid = new(sumX / size, sumY / size, sumZ / size);
            CommanderAirSortie? served = null;
            for (int d = 0; d < demand.Count; d++)
            {
                CommanderAirSortie objective = demand[d];
                // A strike package is served by the ARAD sortie the same way a CAS objective is: the
                // belt is what kills both, and the package's own AradWanted is only whether it
                // expects suppression (design.md, strike-packages_20260915 Section 2). A lift cover
                // is the third, on the same terms (air-mobile-platoons_20260915 Section 3): the belt
                // that kills a strike element kills a transport faster.
                if ((objective.Kind == CommanderSortieKind.Objective
                        || ((objective.Kind == CommanderSortieKind.Strike
                                || objective.Kind == CommanderSortieKind.Cap)
                            && objective.AradWanted > 0))
                    && CommanderGameAccess.HorizontalDistance(objective.Center.AsVector3(), centroid.AsVector3())
                        <= ObservedRadiusMeters)
                {
                    served = objective;
                    break;
                }
            }

            if (served == null)
            {
                continue; // a belt nobody is attacking is not this wing's problem
            }

            served.AradPending = true;
            aradDemand.Add(new CommanderAirSortie
            {
                Kind = CommanderSortieKind.Arad,
                Mission = served.Mission,
                Center = centroid,
                Wanted = wanted,
                CapsWanted = 0,
                LastObserved = size,
                NoCapWait = true,
                // A belt that has been seen is contact by any reading, so a suppression sortie is
                // never a source for a retask.
                InContact = true,
                // The objective's own label: the "ARAD" word belongs to the line and to the review
                // segment, not to the name, or both read "ARAD ARAD Hilltop 12".
                Label = served.Label,
            });
        }

        if (aradDemand.Count == 0)
        {
            return;
        }

        // Straight after the AWACS entry, which is always first when it exists. The "opens ARAD"
        // line is printed by the reconcile, which is what can tell a new belt from an old one.
        int insertAt = demand.Count > 0 && demand[0].Kind == CommanderSortieKind.Awacs ? 1 : 0;
        demand.InsertRange(insertAt, aradDemand);
    }

    /// <summary>Last known positions of every tracked hostile air-defence vehicle — the
    /// <c>CountObserved</c> walk with the buyer's air-defence role test, gathering positions instead
    /// of counting inside one ring.</summary>
    private static void CollectTrackedAirDefence(FactionHQ hq, List<GlobalPosition> into)
    {
        into.Clear();
        float now = Time.timeSinceLevelLoad;
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo info = entry.Value;
            if (now - info.lastSpottedTime > CommanderEnemyCommanderService.ThreatMemorySeconds
                || !info.TryGetUnit(out Unit unit)
                || unit == null
                || unit.disabled
                || unit is not GroundVehicle
                || unit is Building
                || unit.NetworkHQ == null
                || ReferenceEquals(unit.NetworkHQ, hq)
                || unit.definition is not VehicleDefinition definition
                || !CommanderEnemyCommanderService.IsAirDefence(definition))
            {
                continue;
            }

            into.Add(info.lastKnownPosition);
        }
    }

    /// <summary>
    /// Single-link clustering at <see cref="AradClusterLinkMeters"/>: two emitters within the link
    /// distance are in the same belt, and a belt is whatever that relation connects. Writes each
    /// emitter's cluster index into <paramref name="clusterOf"/> and returns how many clusters there
    /// are. A flood fill rather than anything cleverer — a commander tracks tens of vehicles, not
    /// thousands.
    /// </summary>
    private static int BuildAirDefenceClusters(List<GlobalPosition> emitters, List<int> clusterOf)
    {
        clusterOf.Clear();
        for (int i = 0; i < emitters.Count; i++)
        {
            clusterOf.Add(-1);
        }

        int clusters = 0;
        for (int seed = 0; seed < emitters.Count; seed++)
        {
            if (clusterOf[seed] >= 0)
            {
                continue;
            }

            clusterOf[seed] = clusters;
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int i = 0; i < emitters.Count; i++)
                {
                    if (clusterOf[i] != clusters)
                    {
                        continue;
                    }

                    for (int j = 0; j < emitters.Count; j++)
                    {
                        if (clusterOf[j] >= 0)
                        {
                            continue;
                        }

                        if (CommanderGameAccess.HorizontalDistance(
                                emitters[i].AsVector3(), emitters[j].AsVector3()) <= AradClusterLinkMeters)
                        {
                            clusterOf[j] = clusters;
                            grew = true;
                        }
                    }
                }
            }

            clusters++;
        }

        return clusters;
    }

    /// <summary>Whether an ARAD sortie is still on its way in — what holds the CAS package for the
    /// same objective (design SS5). Gone in means the package released AND an airframe is over the
    /// belt; until then the CAS waits, bounded by the package clock.</summary>
    private static bool AradStillInbound(CommanderAirSortie sortie)
    {
        if (!sortie.GoneIn)
        {
            return true;
        }

        for (int i = 0; i < sortie.Cas.Count; i++)
        {
            Aircraft aircraft = sortie.Cas[i];
            if (aircraft != null
                && !aircraft.disabled
                && CommanderGameAccess.HorizontalDistance(
                    aircraft.transform.GlobalPosition().AsVector3(), sortie.Center.AsVector3()) <= CasSortieRadiusMeters)
            {
                return false;
            }
        }

        return true;
    }
}
