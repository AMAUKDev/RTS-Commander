using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Expansion: taking airbases the faction does not hold. Owns the capture-target list both
/// commanders read, the player's capture order, and the enemy commander's decision to go and take
/// a base.
/// </summary>
/// <remarks>
/// The game's capture rule is simply "have a unit with <c>CaptureStrength</c> inside the airbase's
/// <c>CaptureRange</c>" (<c>Capture.GetInRangeUnits</c>), so a capture order is a move order into
/// the middle of the ring and nothing more — no new networking and no new state on the units. What
/// was missing was that nobody ever issued it: the player could not see which bases were takeable,
/// and the enemy commander bought units but never told them to go anywhere.
/// <para>
/// This is the one place in the mod that issues an order for a faction that is not the player's,
/// and it passes <c>playerCommand: false</c> so the Basegame treats it as the AI moving itself.
/// </para>
/// <para>
/// ponytail: capture is carried by ground vehicles, which cross the map slowly. The fast version is
/// an air assault — landing a troop-carrying helicopter inside the ring — and the machinery for it
/// already exists in <see cref="CommanderSupplyHeliService"/>, which flies a helicopter to a chosen
/// point and lands it there. It is deliberately not wired up here because it stacks three engine
/// behaviours that cannot be verified without running the game: whether a troop mount reads as a
/// cargo station, whether <c>AIHeloTransportState</c> can be forced on an aircraft that did not
/// pick it, and whether troops landed inside a ring register as capture strength. Upgrade once
/// ground capture is confirmed in play.
/// </para>
/// </remarks>
internal sealed class CommanderCaptureService : ICommanderTickPersistent, ICommanderResetSession
{
    /// <summary>How often the takeable-base list is rebuilt. Bases change hands slowly.</summary>
    private const float TargetRefreshSeconds = 5f;

    /// <summary>How often each enemy commander reconsiders its expansion.</summary>
    private const float EnemyReviewSeconds = 20f;

    /// <summary>How far from its own territory the enemy will look for a base worth taking.</summary>
    private const float EnemyReachMeters = 40000f;

    /// <summary>Units the enemy commits to one capture. Enough to survive a picket, not an army.</summary>
    private const int EnemySquadSize = 3;

    /// <summary>Slack around a capture ring within which an order counts as a capture order.</summary>
    private const float OrderSlackMeters = 500f;

    /// <summary>
    /// Clear space a capture squad's hold point needs. Roughly a couple of vehicle lengths, which
    /// is enough to keep the point off a terminal wall without demanding a parade ground.
    /// </summary>
    private const float HoldClearanceMeters = 25f;

    /// <summary>Compass points probed for a hold point clear of the airbase's own buildings.</summary>
    private static readonly Vector2[] HoldProbeDirections =
    {
        new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
        new(0.7071f, 0.7071f), new(-0.7071f, 0.7071f), new(0.7071f, -0.7071f), new(-0.7071f, -0.7071f),
    };

    /// <summary>Hold points already worked out, one per airbase. An airfield does not move.</summary>
    private static readonly Dictionary<Airbase, GlobalPosition> holdPoints = new();

    /// <summary>Generous on purpose: a full buffer reads as clear ground, and an airbase centre
    /// is exactly where colliders are dense.</summary>
    private static readonly Collider[] holdProbeHits = new Collider[64];

    /// <summary>
    /// How close one of your units has to get before a base counts as found. An airfield is a big
    /// thing seen from the air and a small thing seen from a road, hence two ranges rather than one.
    /// </summary>
    private const float AirDiscoveryMeters = 12000f;
    private const float GroundDiscoveryMeters = 4000f;

    private readonly CommanderSelectionService selectionService;

    private readonly List<CaptureTarget> targets = new();
    private readonly Dictionary<FactionHQ, EnemyDrive> drives = new();
    private readonly List<FactionHQ> staleDrives = new();
    private readonly List<Unit> squad = new();

    private readonly List<VehicleDefinition> rosterProbe = new();

    /// <summary>
    /// Which base each unit was last sent to take. This is what makes a capture order
    /// redirectable: the capture snap reaches a whole ring plus slack, so once a squad is standing
    /// on a base every order the player gives it nearby snapped straight back to the same hold
    /// point and the units looked stuck. A unit already sent to that base is not re-snapped, so
    /// the second order is taken literally and drives it off.
    /// </summary>
    private readonly Dictionary<Unit, Airbase> assignments = new();

    private readonly List<Unit> staleAssignments = new();

    /// <summary>
    /// Bases the player has found, and the reason capture targets are not simply "every capturable
    /// airbase on the map". Sticky for the whole mission: an airfield does not move, so once you
    /// have seen one it stays on your map, the same way a spotted neutral contact does.
    /// </summary>
    private readonly HashSet<Airbase> discovered = new();

    private float nextTargetRefreshAt;
    private float nextEnemyReviewAt;
    private bool loggedRoster;

    internal static CommanderCaptureService? Instance { get; private set; }

    internal CommanderCaptureService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
    }

    /// <summary>Bases the local faction could take, empty ones first then nearest. </summary>
    internal IReadOnlyList<CaptureTarget> Targets => targets;

    internal string StatusText { get; private set; } = string.Empty;

    public void ResetSession()
    {
        targets.Clear();
        drives.Clear();
        staleDrives.Clear();
        squad.Clear();
        discovered.Clear();
        assignments.Clear();
        staleAssignments.Clear();
        holdPoints.Clear();
        loggedRoster = false;
        StatusText = string.Empty;
        nextTargetRefreshAt = 0f;
        nextEnemyReviewAt = 0f;
    }

    public void TickPersistent()
    {
        if (CommanderScheduler.IsDue(ref nextTargetRefreshAt, TargetRefreshSeconds))
        {
            RefreshTargets();
        }

        if (CommanderPlayerCommanderService.AnyCommanderOn
            && CommanderScheduler.IsDue(ref nextEnemyReviewAt, EnemyReviewSeconds))
        {
            ReviewEnemies();
        }
    }

    /// <summary>
    /// True when an order point lands on a base this faction could take, rewriting the point to the
    /// middle of its capture ring. This is what makes a capture order just an order: right-click a
    /// base in the 3D view or on the map — on its own or as the last of a string of travel points —
    /// and the units go and stand in the ring instead of stopping wherever the cursor happened to
    /// be. The slack is deliberate: a base on a zoomed-out map is a few pixels wide.
    /// </summary>
    internal bool TryResolveCaptureOrder(ref GlobalPosition point, out CaptureTarget target)
    {
        target = default;
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return false;
        }

        float bestDistance = float.MaxValue;
        Airbase? best = null;
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (!IsTakeable(airbase, localHq))
            {
                continue;
            }

            // Horizontal only: a map coordinate carries no useful height.
            GlobalPosition center = airbase.center.GlobalPosition();
            float dx = point.x - center.x;
            float dz = point.z - center.z;
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            if (!discovered.Contains(airbase))
            {
                continue;
            }

            if (distance <= airbase.SavedAirbase.CaptureRange + OrderSlackMeters && distance < bestDistance)
            {
                bestDistance = distance;
                best = airbase;
            }
        }

        if (best == null)
        {
            ForgetSelectionAssignments();
            return false;
        }

        // Already sent there: this click is the player moving the squad off the base, not another
        // capture order. Snapping it back to the ring is what made captured units unorderable.
        if (IsSelectionAlreadySentTo(best))
        {
            ForgetSelectionAssignments();
            CommanderAlertService.Instance?.NotifyCaptureOrder(
                $"LEAVING {GetAirbaseLabel(best).ToUpperInvariant()}", selectionService.SelectedUnits[0]);
            return false;
        }

        target = new CaptureTarget(best, bestDistance);
        point = target.HoldPoint;
        return true;
    }

    /// <summary>
    /// The airbase an order point lands on that this faction already holds. Ground units have no
    /// use for one — they can drive onto their own base without being told — but an aircraft does:
    /// an order dropped on a field you own is a request to land there and rearm.
    /// </summary>
    internal static bool TryResolveOwnedAirfield(GlobalPosition point, FactionHQ? hq, out Airbase airbase)
    {
        airbase = null!;
        if (hq == null)
        {
            return false;
        }

        float bestDistance = float.MaxValue;
        foreach (Airbase candidate in hq.GetAirbases())
        {
            if (candidate == null || candidate.disabled || candidate.center == null || candidate.SavedAirbase == null)
            {
                continue;
            }

            // Horizontal only: a map coordinate carries no useful height.
            GlobalPosition center = candidate.center.GlobalPosition();
            float dx = point.x - center.x;
            float dz = point.z - center.z;
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            if (distance <= candidate.SavedAirbase.CaptureRange + OrderSlackMeters && distance < bestDistance)
            {
                bestDistance = distance;
                airbase = candidate;
            }
        }

        return airbase != null;
    }

    /// <summary>
    /// True when every orderable unit in the selection is already committed to this base. One unit
    /// in the selection that has not been sent there means the order is reinforcement, not a
    /// redirect, so it still snaps.
    /// </summary>
    private bool IsSelectionAlreadySentTo(Airbase airbase)
    {
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        int considered = 0;
        for (int i = 0; i < selection.Count; i++)
        {
            Unit unit = selection[i];
            if (unit == null || unit.disabled || CommanderGameAccess.GetUnitCommand(unit) == null)
            {
                continue;
            }

            considered++;
            if (!assignments.TryGetValue(unit, out Airbase assigned) || !ReferenceEquals(assigned, airbase))
            {
                return false;
            }
        }

        return considered > 0;
    }

    /// <summary>Drops the selection's capture assignments, because it has just been given a
    /// plain order somewhere else.</summary>
    private void ForgetSelectionAssignments()
    {
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        for (int i = 0; i < selection.Count; i++)
        {
            assignments.Remove(selection[i]);
        }
    }

    /// <summary>
    /// Announces a capture order and warns when the selection cannot actually move the bar. It
    /// warns rather than refuses on purpose: which units carry troops is not obvious, and an order
    /// that silently does nothing is exactly how the first version of this looked broken.
    /// </summary>
    internal void AnnounceCaptureOrder(CaptureTarget target, Unit? anchor)
    {
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        for (int i = 0; i < selection.Count; i++)
        {
            if (selection[i] != null && target.Airbase != null)
            {
                assignments[selection[i]] = target.Airbase;
            }
        }

        int capable = CountCapableSelected();
        int selected = selection.Count;
        string label = target.Label.ToUpperInvariant();
        string text = capable > 0
            ? $"CAPTURING {label} ({capable} OF {selected} CAN TAKE GROUND)"
            : $"{label}: NOTHING SELECTED CARRIES TROOPS";
        StatusText = capable > 0
            ? $"{capable} of {selected} selected unit(s) can capture {target.Label}."
            : $"Ordered to {target.Label}, but nothing selected has capture strength — "
                + "send a troop carrier to actually take it.";
        CommanderAlertService.Instance?.NotifyCaptureOrder(text, anchor);
    }

    /// <summary>A unit that contributes to a capture: it has capture strength and can be ordered.</summary>
    internal static bool CanCapture(Unit? unit)
    {
        return unit != null
            && !unit.disabled
            && unit.CaptureStrength > 0f
            && CommanderGameAccess.GetUnitCommand(unit) != null;
    }

    /// <summary>How many of the selected units would actually move the capture bar.</summary>
    internal int CountCapableSelected()
    {
        int count = 0;
        IReadOnlyList<Unit> selection = selectionService.SelectedUnits;
        for (int i = 0; i < selection.Count; i++)
        {
            if (CanCapture(selection[i]))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Every capturable airbase the local faction does not already hold. Empty bases sort first: an
    /// unheld base costs a drive, a held one costs a battle.
    /// </summary>
    private void RefreshTargets()
    {
        targets.Clear();
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return;
        }

        LogCaptureRosterOnce(localHq);
        UpdateDiscovery(localHq);
        PruneAssignments();
        GlobalPosition from = GetTerritoryCenter(localHq);
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (IsTakeable(airbase, localHq) && discovered.Contains(airbase))
            {
                targets.Add(new CaptureTarget(airbase, FastMath.Distance(from, airbase.center.GlobalPosition())));
            }
        }

        targets.Sort(static (left, right) =>
        {
            int held = left.HeldByOther.CompareTo(right.HeldByOther);
            return held != 0 ? held : left.Distance.CompareTo(right.Distance);
        });
    }

    /// <summary>
    /// Names, once per mission, every vehicle in the faction roster that carries capture strength.
    /// <c>UnitDefinition.captureStrength</c> lives in the game's asset files, not in its code, so
    /// which units can take ground cannot be read outside a running game — this line in the BepInEx
    /// console is the answer, and an empty one says capture needs troops flown in instead.
    /// </summary>
    private void LogCaptureRosterOnce(FactionHQ hq)
    {
        if (loggedRoster)
        {
            return;
        }

        loggedRoster = true;
        CommanderGameAccess.CollectFactionVehicleDefinitions(rosterProbe, hq);
        string names = string.Empty;
        for (int i = 0; i < rosterProbe.Count; i++)
        {
            if (rosterProbe[i].captureStrength > 0f)
            {
                names += (names.Length == 0 ? string.Empty : ", ")
                    + $"{rosterProbe[i].unitName} ({rosterProbe[i].captureStrength:0.#})";
            }
        }

        rosterProbe.Clear();
        if (names.Length == 0)
        {
            CommanderPlugin.Log.LogWarning(
                $"No vehicle in the {hq.faction.name} roster carries capture strength, so no ground "
                    + "unit can take an airbase on this mission. Capture needs troops landed from "
                    + "the air here.");
            return;
        }

        CommanderPlugin.Log.LogInfo($"Vehicles that can capture ({hq.faction.name}): {names}.");
    }

    /// <summary>
    /// Adds any capturable base one of the faction's units has come near to the found set. Runs on
    /// the target refresh rather than per frame, and only ever grows: a base drops off the list
    /// when it is taken, never because you flew away from it.
    /// </summary>
    private void UpdateDiscovery(FactionHQ hq)
    {
        if (hq.factionUnits == null)
        {
            return;
        }

        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null
                || airbase.disabled
                || airbase.center == null
                || airbase.SavedAirbase == null
                || !airbase.SavedAirbase.Capturable
                || discovered.Contains(airbase))
            {
                continue;
            }

            // A base the faction already holds is found by definition.
            if (ReferenceEquals(airbase.CurrentHQ, hq))
            {
                discovered.Add(airbase);
                continue;
            }

            GlobalPosition center = airbase.center.GlobalPosition();
            foreach (PersistentID id in hq.factionUnits)
            {
                if (!id.TryGetUnit(out Unit unit) || unit == null || unit.disabled)
                {
                    continue;
                }

                float range = unit is Aircraft ? AirDiscoveryMeters : GroundDiscoveryMeters;
                if (FastMath.InRange(unit.transform.GlobalPosition(), center, range))
                {
                    discovered.Add(airbase);
                    CommanderAlertService.Instance?.NotifyCapture(
                        $"FOUND {GetAirbaseLabel(airbase).ToUpperInvariant()} — CAPTURABLE");
                    break;
                }
            }
        }
    }

    /// <summary>Every found base the local faction could take, for the map and the 3D view.</summary>
    internal void CopyDiscoveredTargets(List<CaptureTarget> buffer)
    {
        buffer.Clear();
        for (int i = 0; i < targets.Count; i++)
        {
            buffer.Add(targets[i]);
        }
    }

    private static bool IsTakeable(Airbase? airbase, FactionHQ hq)
    {
        return airbase != null
            && !airbase.disabled
            && airbase.center != null
            && airbase.SavedAirbase != null
            && airbase.SavedAirbase.Capturable
            && !ReferenceEquals(airbase.CurrentHQ, hq);
    }

    /// <summary>
    /// Where a faction "is": the mean of the airbases it holds. Capture distances are measured from
    /// there, so "nearest" means nearest to its territory rather than to whichever unit happens to
    /// have wandered furthest from home.
    /// </summary>
    internal static GlobalPosition GetTerritoryCenter(FactionHQ hq)
    {
        Vector3 sum = Vector3.zero;
        int count = 0;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase != null && !airbase.disabled && airbase.center != null)
            {
                sum += airbase.center.GlobalPosition().AsVector3();
                count++;
            }
        }

        return count == 0 ? default : new GlobalPosition(sum.x / count, sum.y / count, sum.z / count);
    }

    private void ReviewEnemies()
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return;
        }

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq != null
                && CommanderPlayerCommanderService.IsCommanded(hq, localHq)
                && hq.IsServer
                && hq.faction != null)
            {
                ReviewEnemy(hq);
            }
        }

        PruneDrives(localHq);
    }

    /// <summary>
    /// One expansion decision per enemy commander: pick a base worth taking, commit whatever
    /// capture-capable units it owns, and keep pointing them at the ring until the base changes
    /// hands or the squad is gone. Re-issuing every review is cheap — three destinations every
    /// twenty seconds — and it is what recovers the drive when a unit is killed on the way.
    /// </summary>
    private void ReviewEnemy(FactionHQ hq)
    {
        if (!drives.TryGetValue(hq, out EnemyDrive drive))
        {
            drive = new EnemyDrive();
            drives[hq] = drive;
        }

        if (!IsTakeable(drive.Target, hq))
        {
            if (drive.Target != null && ReferenceEquals(drive.Target.CurrentHQ, hq))
            {
                // The toast is raised by CommanderAlertPatches off Airbase.CaptureFaction, which
                // catches every base that changes hands rather than only the one this drive was
                // aimed at. Console line only here.
                CommanderPlugin.Log.LogInfo(
                    $"{CommanderPlayerCommanderService.CommanderLabel(hq)} captured {GetAirbaseLabel(drive.Target)}.");
            }

            drive.Target = ChooseEnemyTarget(hq);
            drive.Announced = false;
        }

        if (drive.Target == null)
        {
            drive.WantsCaptureUnit = false;
            return;
        }

        CollectCaptureSquad(hq, EnemySquadSize);
        // No unit that can take ground is the one thing that stops an expansion dead, so it is the
        // one thing the unit spender is told about.
        drive.WantsCaptureUnit = squad.Count == 0;
        if (squad.Count == 0)
        {
            return;
        }

        GlobalPosition hold = GetHoldPoint(drive.Target);
        float ring = Mathf.Max(drive.Target.SavedAirbase.CaptureRange, 100f);
        for (int i = 0; i < squad.Count; i++)
        {
            // A unit already standing in the ring is already capturing. Re-ordering it there every
            // review only makes it shuffle, and every issue is a networked RPC.
            if (FastMath.InRange(squad[i].transform.GlobalPosition(), hold, ring * 0.8f))
            {
                continue;
            }

            CommanderGameAccess.GetUnitCommand(squad[i])?.SetDestination(hold, false);
        }

        if (!drive.Announced)
        {
            drive.Announced = true;
            CommanderPlugin.Log.LogInfo(
                $"{CommanderPlayerCommanderService.CommanderLabel(hq)} is moving on {GetAirbaseLabel(drive.Target)} "
                    + $"with {squad.Count} unit(s).");
        }
    }

    /// <summary>
    /// True when this commander wants to expand but owns nothing that can take ground, which is
    /// what tells <see cref="CommanderEnemyCommanderService"/> to buy a capture unit ahead of its
    /// plan. Without this the enemy can sit on a full pot next to an empty airbase forever.
    /// </summary>
    internal bool WantsCaptureUnit(FactionHQ hq)
    {
        return drives.TryGetValue(hq, out EnemyDrive drive) && drive.WantsCaptureUnit;
    }

    private static Airbase? ChooseEnemyTarget(FactionHQ hq)
    {
        GlobalPosition from = GetTerritoryCenter(hq);
        Airbase? best = null;
        float bestScore = float.MaxValue;
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (!IsTakeable(airbase, hq))
            {
                continue;
            }

            float distance = FastMath.Distance(from, airbase.center.GlobalPosition());
            if (distance > EnemyReachMeters)
            {
                continue;
            }

            float score = ScoreTarget(airbase.CurrentHQ != null, distance);
            if (score < bestScore)
            {
                bestScore = score;
                best = airbase;
            }
        }

        return best;
    }

    /// <summary>
    /// Lower is better. A base somebody already holds is still worth taking, but only once the
    /// empty ones are gone — hence a whole reach added to its score rather than a separate pass.
    /// </summary>
    private static float ScoreTarget(bool held, float distance)
    {
        // The penalty has to be strictly larger than the reach, or an empty base at the very edge
        // of reach only ties with a held base underfoot instead of beating it.
        return held ? distance + EnemyReachMeters * 2f : distance;
    }

    /// <summary>
    /// One runnable check for the expansion priority, run from <see cref="CommanderPlugin"/> at
    /// load next to the other self-checks, because a Unity plugin has nowhere else to run one.
    /// The rule that matters is that an empty base anywhere in reach beats a defended one next
    /// door — invert it and the enemy commander throws its capture squad at the player's airbase
    /// instead of walking onto a free one.
    /// </summary>
    internal static void SelfCheck()
    {
        Expect("far empty beats near held", ScoreTarget(false, EnemyReachMeters) < ScoreTarget(true, 0f));
        Expect("nearest empty wins", ScoreTarget(false, 1000f) < ScoreTarget(false, 2000f));
        Expect("nearest held wins", ScoreTarget(true, 1000f) < ScoreTarget(true, 2000f));
    }

    private static void Expect(string name, bool condition)
    {
        if (!condition)
        {
            CommanderPlugin.Log.LogError($"Capture priority self-check FAILED: {name}.");
        }
    }

    /// <summary>Capture-capable units this faction owns, up to <paramref name="limit"/>.</summary>
    private void CollectCaptureSquad(FactionHQ hq, int limit)
    {
        squad.Clear();
        if (hq.factionUnits == null)
        {
            return;
        }

        foreach (PersistentID id in hq.factionUnits)
        {
            if (squad.Count >= limit)
            {
                return;
            }

            // A vehicle standing on the commander's own base ring is spoken for: the home guard
            // pins it with a player command and this would order it away again every review. A
            // vehicle the player has ordered somewhere is spoken for the same way — this re-issues
            // a destination to every capture-capable unit it owns every review, so without the
            // second test the AI would drive a troop carrier off the player's own order.
            if (id.TryGetUnit(out Unit unit)
                && CanCapture(unit)
                && !CommanderEnemyCommanderService.IsDefendingUnit(unit)
                && CommanderMoveService.Instance?.HasPlayerOrder(unit) != true)
            {
                squad.Add(unit);
            }
        }
    }

    /// <summary>
    /// Where a capture squad is sent to stand. Deliberately not the airbase centre transform: on a
    /// real airfield that sits on the terminal or the tower, so a squad ordered there drove into
    /// the wall, reversed off it and drove in again forever. Any point inside <c>CaptureRange</c>
    /// captures — <c>Capture.GetInRangeUnits</c> is a plain radius test — so the squad goes to the
    /// first spot on a ring at half that radius which no building occupies. Cached per airbase:
    /// the airfield does not move, and open ground stays open.
    /// </summary>
    private static GlobalPosition GetHoldPoint(Airbase airbase)
    {
        if (holdPoints.TryGetValue(airbase, out GlobalPosition cached))
        {
            return cached;
        }

        GlobalPosition center = airbase.center.GlobalPosition();
        GlobalPosition point = center;
        if (IsBuiltOver(center))
        {
            float reach = Mathf.Max(airbase.SavedAirbase?.CaptureRange ?? 0f, 200f) * 0.5f;
            for (int i = 0; i < HoldProbeDirections.Length; i++)
            {
                GlobalPosition candidate = CommanderGameAccess.SnapToTerrain(new GlobalPosition(
                    center.x + HoldProbeDirections[i].x * reach,
                    center.y,
                    center.z + HoldProbeDirections[i].y * reach));
                // SnapToTerrain returns the seabed over water, so this also keeps the squad
                // from being sent for a swim at a coastal airfield.
                if (!CommanderGameAccess.IsBelowSeaLevel(candidate) && !IsBuiltOver(candidate))
                {
                    point = candidate;
                    break;
                }
            }
        }

        holdPoints[airbase] = point;
        return point;
    }

    /// <summary>
    /// A building stands here. Only buildings count: vehicles parked on the spot move away on
    /// their own, and treating them as blockers would make the hold point depend on whoever
    /// happened to be standing there the first time it was asked for.
    /// </summary>
    private static bool IsBuiltOver(GlobalPosition point)
    {
        int hits = Physics.OverlapSphereNonAlloc(
            point.ToLocalPosition() + Vector3.up * HoldClearanceMeters,
            HoldClearanceMeters,
            holdProbeHits,
            ~0,
            QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits; i++)
        {
            Collider hit = holdProbeHits[i];
            Unit? occupant = hit == null ? null : hit.GetComponentInParent<Unit>();
            if (occupant is Building && !occupant.disabled)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Drops capture assignments for units that died or whose base changed hands.</summary>
    private void PruneAssignments()
    {
        staleAssignments.Clear();
        foreach (KeyValuePair<Unit, Airbase> entry in assignments)
        {
            if (entry.Key == null || entry.Key.disabled || entry.Value == null || entry.Value.disabled)
            {
                staleAssignments.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleAssignments.Count; i++)
        {
            assignments.Remove(staleAssignments[i]);
        }
    }

    /// <summary>Drops drives for HQs that went away, and for the local HQ while nothing is
    /// commanding it — the player's own expansion drive only exists while the switch is on.</summary>
    private void PruneDrives(FactionHQ localHq)
    {
        bool dropLocal = !CommanderPlayerCommanderService.IsCommanded(localHq, localHq);
        staleDrives.Clear();
        foreach (KeyValuePair<FactionHQ, EnemyDrive> entry in drives)
        {
            if (entry.Key == null || (dropLocal && ReferenceEquals(entry.Key, localHq)))
            {
                staleDrives.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleDrives.Count; i++)
        {
            drives.Remove(staleDrives[i]);
        }
    }

    internal static string GetAirbaseLabel(Airbase? airbase)
    {
        string? name = airbase?.SavedAirbase?.DisplayName;
        return string.IsNullOrEmpty(name) ? airbase?.name ?? "airbase" : name!;
    }

    internal readonly struct CaptureTarget
    {
        internal CaptureTarget(Airbase airbase, float distance)
        {
            Airbase = airbase;
            Distance = distance;
        }

        internal Airbase Airbase { get; }

        internal float Distance { get; }

        /// <summary>Somebody else already holds it, so taking it means fighting for it.</summary>
        internal bool HeldByOther => Airbase.CurrentHQ != null;

        /// <summary>
        /// How far the base has moved toward changing hands, 0 to 1. <c>Capture.controlBalance</c>
        /// means the owner's grip on a held base and the challenger's progress on an empty one, so
        /// a held base is inverted to keep this "how close is it to falling" either way.
        /// </summary>
        internal float CaptureProgress
        {
            get
            {
                Airbase? airbase = Airbase;
                Capture? capture = airbase == null ? null : airbase.capture;
                if (airbase == null || capture == null)
                {
                    return 0f;
                }

                return airbase.CurrentHQ != null ? 1f - capture.controlBalance : capture.controlBalance;
            }
        }

        /// <summary>Who is taking an unheld base, or null when nobody is or it is already held.</summary>
        internal FactionHQ? CapturingHq
        {
            get
            {
                Airbase? airbase = Airbase;
                return airbase == null ? null : airbase.capture?.capturingHQ;
            }
        }

        internal string Label => GetAirbaseLabel(Airbase);

        internal GlobalPosition HoldPoint => GetHoldPoint(Airbase);
    }

    private sealed class EnemyDrive
    {
        internal Airbase? Target;
        internal bool Announced;
        internal bool WantsCaptureUnit;
    }
}
