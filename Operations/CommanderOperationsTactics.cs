using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Ground tactics: how a platoon stands on a point, how it meets an attack on it, how it crosses
/// the last few kilometres to an objective, and what a reinforcement does when the point it was
/// sent to is already held (design.md, ground-tactics_20260914; DECISION-012).
/// <para>
/// This is posture inside the platoon state machine the operations service already runs, not a
/// service of its own: the design's own Reuse section rules out a separate tactics service because
/// it would duplicate the move seam and the state machine. Every rule that carries a number is a
/// pure static over floats and ints so <see cref="CheckGroundTactics"/> can drive it at plugin
/// load, and every move goes through the one seam
/// (<see cref="CommanderMoveService.IssuePlatoonMove"/> for formations,
/// <c>DriveMembersToPosts</c> for individual posts).
/// </para>
/// </summary>
internal sealed partial class CommanderOperationsService
{
    /// <summary>
    /// Closest two neighbouring hold posts may sit on a point's ring. 250 m: the complaint this
    /// track answers is a platoon "sat in a tight grouping on the point", and at the 25 m formation
    /// spacing the mod uses for everything else a six-vehicle ring spans barely 150 m — one salvo.
    /// 250 m is far enough apart that one artillery or bomb fall reaches one vehicle, and close
    /// enough that neighbours still cover each other with tank guns (2–4 km reach).
    /// </summary>
    private const float RingMinSpacingMeters = 250f;

    /// <summary>
    /// Where the remainder stands when a ring is oversubscribed — more members than the minimum
    /// spacing allows posts. Half the hold radius (design §1's "an inner ring at half radius"):
    /// still inside the point so it keeps paying, still clear of the outer ring by half a radius.
    /// </summary>
    private const float InnerRingFraction = 0.5f;

    /// <summary>
    /// Where the air-defence pair and the carrier/truck pair stand, as a fraction of the hold
    /// radius. 0.3 (design §1): close enough to the centre that the umbrella covers the whole ring
    /// and the truck is not the first thing an attack meets, far enough out that the two pairs on
    /// opposite sides are still 0.6 of a radius apart.
    /// </summary>
    private const float AirDefenceRingFraction = 0.3f;

    /// <summary>
    /// Arc length between neighbouring vehicles on a defence arc. 150 m (design §2): tighter than
    /// the 250 m ring because an arc is a firing line that has to hold one bearing between its
    /// flanks, and wider than the 25 m march spacing that made the platoon one target.
    /// </summary>
    private const float DefenceArcSpacingMeters = 150f;

    /// <summary>
    /// How far the threat bearing must move before a formed arc is re-aimed. 15° (design §2): at
    /// the arc's own distance from the point centre 15° is roughly one vehicle's slot, so anything
    /// smaller would re-issue the whole arc every movement tick for no change in cover — and every
    /// issue is a networked RPC.
    /// </summary>
    private const float DefenceArcReaimDegrees = 15f;

    /// <summary>
    /// How long a platoon keeps its defence arc after the last contact lapses before returning to
    /// the ring. Three contact holds (design §2's "60 s"), expressed off
    /// <see cref="ContactHoldSeconds"/> rather than as a bare 60 so a retune of the contact clock
    /// carries: long enough that a hostile blinking out of tracking mid-fight does not send the
    /// tanks back to the ring, short enough that a point is spread wide again within two reviews of
    /// the fight ending.
    /// </summary>
    private const float DefenceArcHoldSeconds = ContactHoldSeconds * 3f;

    /// <summary>
    /// How long one bound may take before the platoon moves on without its stragglers. 90 s
    /// (design §3): at the ground speeds in this game an 800 m cross-country bound is roughly a
    /// minute, so 90 s is "it should be there by now", and three movement ticks longer than the
    /// half-closed rule normally needs.
    /// </summary>
    private const float BoundTimeoutSeconds = 90f;

    /// <summary>
    /// How far off the threat bearing a counter-attacking reinforcement swings before it goes in.
    /// 1 km (design §4): the user's own number, and far enough off the axis the attack is driving
    /// that it arrives on a flank rather than into the same head-on fight the garrison is already
    /// losing.
    /// </summary>
    private const float CounterAttackFlankMeters = 1000f;

    /// <summary>
    /// How far a counter-attacking platoon will reach for a tracked hostile to go in on. Twice the
    /// contact range: the fight that opened the reinforcement request is inside
    /// <see cref="ContactRangeMeters"/> of the POINT, and the flank position it attacks from is a
    /// kilometre away from that, so a reach of one contact range would lose the target on arrival.
    /// </summary>
    private const float CounterAttackReachMeters = ContactRangeMeters * 2f;

    /// <summary>
    /// How far out a screening reinforcement sits from the point it covers. 800 m (design §4):
    /// outside the ring the garrison holds, so the screen meets an approach before it reaches the
    /// point, and inside the 2.5 km at which the garrison itself notices contact, so the two are
    /// one position and not two separate fights.
    /// </summary>
    private const float ScreenRangeMeters = 800f;

    /// <summary>
    /// How far a screen position may be moved to sit on an actual road. 600 m: a road inside this
    /// of the raw screen position is the approach the screen is there to watch; past it the nearest
    /// road is a different approach entirely and the raw bearing is the honest answer.
    /// </summary>
    private const float ScreenRoadSnapMeters = 600f;

    /// <summary>One hold post or arc slot in polar form around the point centre: how far out it
    /// sits, which way it bears from the centre — and, because a post is reached by driving outward
    /// from inside the ring, which way the vehicle on it ends up looking (design §1's "facing
    /// outward"; departure 2 records that the engine exposes no facing order).</summary>
    internal readonly struct CommanderPostSlot
    {
        internal CommanderPostSlot(float radiusMeters, float bearingDegrees)
        {
            RadiusMeters = radiusMeters;
            BearingDegrees = bearingDegrees;
        }

        internal float RadiusMeters { get; }

        internal float BearingDegrees { get; }

        /// <summary>Outward facing is the slot's own bearing from the centre.</summary>
        internal float FacingDegrees => BearingDegrees;
    }

    /// <summary>
    /// Angular gap between the two posts of an inner pair — the air-defence pair on the threat
    /// side and the carrier/truck pair on the far side. 60°: the pair straddles the bearing it
    /// covers (±30°) instead of sitting one behind the other, and at 0.3 of a typical hold radius
    /// that is a comfortable 300 m between them.
    /// </summary>
    private const float InnerPairSpreadDegrees = 60f;

    /// <summary>
    /// The most evenly spaced posts a ring of <paramref name="radiusMeters"/> carries without any
    /// two neighbours closing inside <paramref name="minSpacingMeters"/>, never more than
    /// <paramref name="wanted"/> and never fewer than one. The neighbour gap on a ring of n posts
    /// is the chord 2·r·sin(π/n), so n is capped at π / asin(spacing / 2r). Pure, for the
    /// self-check (design §1).
    /// </summary>
    internal static int RingSlotsFor(float radiusMeters, int wanted, float minSpacingMeters)
    {
        if (wanted <= 1)
        {
            return 1;
        }

        if (radiusMeters <= 0f || minSpacingMeters <= 0f)
        {
            return wanted;
        }

        float ratio = minSpacingMeters / (2f * radiusMeters);
        if (ratio >= 1f)
        {
            // The ring is smaller across than one spacing: it carries a single post.
            return 1;
        }

        int capacity = Mathf.FloorToInt(Mathf.PI / Mathf.Asin(ratio));
        return Mathf.Clamp(wanted, 1, Mathf.Max(1, capacity));
    }

    /// <summary>Bearing folded into [0, 360).</summary>
    internal static float NormalizeBearing(float degrees)
    {
        float wrapped = degrees % 360f;
        return wrapped < 0f ? wrapped + 360f : wrapped;
    }

    /// <summary>
    /// The whole post plan for a point's ring (design §1), in polar form around the point centre so
    /// it is pure and the self-check can drive it.
    /// <list type="bullet">
    /// <item><paramref name="outer"/> — the fighting ring at the point's FULL hold radius, its
    /// first post on the threat bearing so the threatened side is always manned, evenly spaced at
    /// no less than <paramref name="minSpacingMeters"/>. Where more members want the ring than it
    /// carries at that spacing, the remainder are appended on an inner ring at
    /// <see cref="InnerRingFraction"/> of the radius, offset half a step so they interleave rather
    /// than queue up behind the outer posts.</item>
    /// <item><paramref name="airDefence"/> — the umbrella, at <see cref="AirDefenceRingFraction"/>
    /// of the radius, straddling the threat bearing.</item>
    /// <item><paramref name="inner"/> — the carrier and the truck, at the same fraction on the far
    /// side, which is the half of the point an attack reaches last.</item>
    /// </list>
    /// Every slot's facing is its own bearing from the centre (outward).
    /// </summary>
    internal static void PlanHoldPosts(
        float radiusMeters,
        float threatBearingDegrees,
        int outerCount,
        int airDefenceCount,
        int innerCount,
        float minSpacingMeters,
        List<CommanderPostSlot> outer,
        List<CommanderPostSlot> airDefence,
        List<CommanderPostSlot> inner)
    {
        outer.Clear();
        airDefence.Clear();
        inner.Clear();

        if (outerCount > 0)
        {
            int ringSlots = RingSlotsFor(radiusMeters, outerCount, minSpacingMeters);
            float step = 360f / ringSlots;
            for (int i = 0; i < ringSlots; i++)
            {
                outer.Add(new CommanderPostSlot(radiusMeters, NormalizeBearing(threatBearingDegrees + i * step)));
            }

            int remainder = outerCount - ringSlots;
            if (remainder > 0)
            {
                float innerRadius = radiusMeters * InnerRingFraction;
                int innerSlots = RingSlotsFor(innerRadius, remainder, minSpacingMeters);
                float innerStep = 360f / innerSlots;
                for (int i = 0; i < innerSlots; i++)
                {
                    outer.Add(new CommanderPostSlot(
                        innerRadius, NormalizeBearing(threatBearingDegrees + innerStep * 0.5f + i * innerStep)));
                }
            }
        }

        AddPairPosts(radiusMeters * AirDefenceRingFraction, threatBearingDegrees, airDefenceCount, airDefence);
        AddPairPosts(radiusMeters * AirDefenceRingFraction, threatBearingDegrees + 180f, innerCount, inner);
    }

    /// <summary>One inner pair (or a single post, or three) spread symmetrically about
    /// <paramref name="centreBearingDegrees"/> at <see cref="InnerPairSpreadDegrees"/> between
    /// neighbours. One definition, two callers: the air-defence pair and the carrier/truck pair are
    /// the same shape on opposite bearings.</summary>
    private static void AddPairPosts(
        float radiusMeters, float centreBearingDegrees, int count, List<CommanderPostSlot> into)
    {
        for (int i = 0; i < count; i++)
        {
            float offset = (i - (count - 1) * 0.5f) * InnerPairSpreadDegrees;
            into.Add(new CommanderPostSlot(radiusMeters, NormalizeBearing(centreBearingDegrees + offset)));
        }
    }

    /// <summary>
    /// A point's ring as actual ground positions: the fighting ring (with any oversubscribed
    /// remainder appended on the inner ring), the air-defence pair on the threat side and the
    /// carrier/truck pair opposite. Cached per point and rebuilt only when the wanted counts or the
    /// threat bearing bucket change, because building it snaps every post to the terrain.
    /// </summary>
    internal sealed class CommanderHoldPostSet
    {
        internal int OuterWanted = -1;

        internal int AirDefenceWanted = -1;

        internal int InnerWanted = -1;

        /// <summary>The threat bearing this set was aimed on, in <see cref="DefenceArcReaimDegrees"/>
        /// buckets — the same tolerance the defence arc re-aims on, so the ring and the arc agree on
        /// what counts as "the threat has moved".</summary>
        internal int BearingBucket = int.MinValue;

        internal readonly List<GlobalPosition> Outer = new();

        internal readonly List<GlobalPosition> AirDefence = new();

        internal readonly List<GlobalPosition> Inner = new();
    }

    /// <summary>
    /// A position <paramref name="radiusMeters"/> from <paramref name="center"/> on
    /// <paramref name="bearingDegrees"/>, in the game's own heading frame (0° is +Z, 90° is +X) —
    /// the frame <see cref="CommanderMoveService.HeadingDegrees"/> reports and every facing in the
    /// mod is expressed in, so a bearing taken from a tracked hostile places a post on the side the
    /// hostile is actually on.
    /// </summary>
    internal static GlobalPosition PositionAt(GlobalPosition center, float bearingDegrees, float radiusMeters)
    {
        float radians = bearingDegrees * Mathf.Deg2Rad;
        return new GlobalPosition(
            center.x + Mathf.Sin(radians) * radiusMeters,
            center.y,
            center.z + Mathf.Cos(radians) * radiusMeters);
    }

    /// <summary>Bearing from <paramref name="from"/> to <paramref name="to"/> in the same frame as
    /// <see cref="PositionAt"/>. One line, but it is the third caller of the move service's heading
    /// maths and naming it here keeps the polar conversions in one place.</summary>
    internal static float BearingTo(GlobalPosition from, GlobalPosition to)
    {
        return CommanderMoveService.HeadingDegrees(from.ToLocalPosition(), to.ToLocalPosition());
    }

    /// <summary>Slot scratch for <see cref="EnsureHoldPostSet"/>: one point is planned at a time.</summary>
    private readonly List<CommanderPostSlot> holdSlotsOuter = new();
    private readonly List<CommanderPostSlot> holdSlotsAirDefence = new();
    private readonly List<CommanderPostSlot> holdSlotsInner = new();

    /// <summary>
    /// The point's post set for this composition and this threat bearing, rebuilt only when one of
    /// them has actually changed (design §1). Terrain-snapped, sea-level posts dropped — the same
    /// two filters the ring builder this replaces applied.
    /// </summary>
    private CommanderHoldPostSet EnsureHoldPostSet(
        CommanderStrategicPoint point, float threatBearingDegrees, int outerCount, int airDefenceCount, int innerCount)
    {
        int bucket = Mathf.RoundToInt(NormalizeBearing(threatBearingDegrees) / DefenceArcReaimDegrees);
        if (!holdPosts.TryGetValue(point, out CommanderHoldPostSet set))
        {
            set = new CommanderHoldPostSet();
            holdPosts[point] = set;
        }
        else if (set.OuterWanted == outerCount
            && set.AirDefenceWanted == airDefenceCount
            && set.InnerWanted == innerCount
            && set.BearingBucket == bucket)
        {
            return set;
        }

        set.OuterWanted = outerCount;
        set.AirDefenceWanted = airDefenceCount;
        set.InnerWanted = innerCount;
        set.BearingBucket = bucket;
        PlanHoldPosts(
            point.Radius,
            threatBearingDegrees,
            outerCount,
            airDefenceCount,
            innerCount,
            RingMinSpacingMeters,
            holdSlotsOuter,
            holdSlotsAirDefence,
            holdSlotsInner);
        FillPostPositions(point.Position, holdSlotsOuter, set.Outer);
        FillPostPositions(point.Position, holdSlotsAirDefence, set.AirDefence);
        FillPostPositions(point.Position, holdSlotsInner, set.Inner);
        return set;
    }

    /// <summary>Turns polar slots into ground positions, dropping anything the terrain puts under
    /// the sea — the filter pair the point ring has always applied.</summary>
    private static void FillPostPositions(
        GlobalPosition center, List<CommanderPostSlot> slots, List<GlobalPosition> into)
    {
        into.Clear();
        for (int i = 0; i < slots.Count; i++)
        {
            GlobalPosition candidate = CommanderGameAccess.SnapToTerrain(
                PositionAt(center, slots[i].BearingDegrees, slots[i].RadiusMeters));
            if (!CommanderGameAccess.IsBelowSeaLevel(candidate))
            {
                into.Add(candidate);
            }
        }
    }

    /// <summary>
    /// Which way an attack on <paramref name="point"/> is expected from: the nearest tracked
    /// hostile inside the point's own threat ring, failing that the bearing to the enemy's nearest
    /// asset (the direction the front is in), failing that north. The ring, the defence arc and the
    /// screen all aim on this one answer.
    /// </summary>
    private static float ThreatBearingFor(FactionHQ hq, CommanderStrategicPoint point)
    {
        return ThreatBearingFor(hq, point, point.Radius * ThreatMarkRingMultiplier);
    }

    /// <summary>The same answer over a circle the caller chooses: the ring and the arc ask about
    /// the point's own threat ring, the screen about the whole front range.</summary>
    private static float ThreatBearingFor(FactionHQ hq, CommanderStrategicPoint point, float searchMeters)
    {
        if (TryNearestTrackedHostile(hq, point.Position, searchMeters, out GlobalPosition hostile, out _))
        {
            return BearingTo(point.Position, hostile);
        }

        if (TryNearestEnemyAsset(hq, point.Position, out GlobalPosition asset, out _))
        {
            return BearingTo(point.Position, asset);
        }

        return 0f;
    }

    /// <summary>Role buckets for <see cref="DriveToHoldPosts"/>: one platoon or picket at a time.</summary>
    private readonly List<Unit> holdRoleOuter = new();
    private readonly List<Unit> holdRoleAirDefence = new();
    private readonly List<Unit> holdRoleInner = new();

    /// <summary>
    /// Design §1's role-to-post rule: the umbrella takes the inner pair on the threat side, the
    /// carrier and the truck the inner pair on the far side, and the tanks — with anything the
    /// recipe could not classify — the fighting ring itself.
    /// </summary>
    private static void SplitByHoldRole(
        IReadOnlyList<Unit> members, List<Unit> outer, List<Unit> airDefence, List<Unit> inner)
    {
        outer.Clear();
        airDefence.Clear();
        inner.Clear();
        for (int i = 0; i < members.Count; i++)
        {
            Unit unit = members[i];
            if (unit == null || unit.disabled)
            {
                continue;
            }

            switch (CommanderPlatoonRoles.Of(unit.definition as VehicleDefinition))
            {
                case CommanderPlatoonRole.AirDefence:
                    airDefence.Add(unit);
                    break;
                case CommanderPlatoonRole.Carrier:
                case CommanderPlatoonRole.Truck:
                    inner.Add(unit);
                    break;
                default:
                    outer.Add(unit);
                    break;
            }
        }
    }

    /// <summary>A post list, or the fighting ring when the terrain filter left that list empty —
    /// a vehicle with nowhere of its own to stand still stands on the point.</summary>
    private static List<GlobalPosition> PostsOrFallback(List<GlobalPosition> posts, List<GlobalPosition> fallback)
    {
        return posts.Count > 0 ? posts : fallback;
    }

    /// <summary>
    /// How far from the point's centre the defence arc stands (design §2): the hold radius plus the
    /// standoff, with the standoff cut to the radius on a point too small to carry it — the
    /// design's "clamped … when the radius is small". Pure, for the self-check.
    /// <para>
    /// Departure 1 (plan.md): the design also says "no vehicle leaves the hold radius", which
    /// cannot hold once §1 puts the ring itself on the FULL radius — clamping the arc inside the
    /// radius would put the tanks back on the ring and make the setting dead. The arc is therefore
    /// forward of the ring, which is the decision's own words ("tanks and IFVs forward"), and the
    /// point still pays because the air-defence pair, the truck and any carrier with no armour
    /// rating stay on their ring posts inside the radius — three vehicles against a
    /// <c>PointsMinGarrison</c> of two.
    /// </para>
    /// </summary>
    internal static float DefenceArcDistanceMeters(float radiusMeters, float standoffMeters)
    {
        if (radiusMeters <= 0f)
        {
            return 0f;
        }

        return radiusMeters + Mathf.Clamp(standoffMeters, 0f, radiusMeters);
    }

    /// <summary>
    /// Where slot <paramref name="slotIndex"/> of a defence arc bears from the point centre: slot 0
    /// on the threat bearing itself — the platoon's first tank, since
    /// <see cref="OrderForMarch"/> has already put the armour at the head of the list — and every
    /// slot after it alternating left and right at <paramref name="spacingMeters"/> of arc length,
    /// the same centre-out alternation <c>CommanderDestinationFormation</c> builds a line abreast
    /// with. Pure, for the self-check (design §2).
    /// </summary>
    internal static float DefenceArcBearing(
        float threatBearingDegrees, int slotIndex, float spacingMeters, float arcDistanceMeters)
    {
        if (slotIndex <= 0 || spacingMeters <= 0f || arcDistanceMeters <= 0f)
        {
            return NormalizeBearing(threatBearingDegrees);
        }

        int rank = (slotIndex + 1) / 2;
        float side = (slotIndex & 1) == 1 ? -1f : 1f;
        float stepDegrees = spacingMeters / arcDistanceMeters * Mathf.Rad2Deg;
        return NormalizeBearing(threatBearingDegrees + side * rank * stepDegrees);
    }

    /// <summary>
    /// True when the threat has moved far enough off the bearing the arc was aimed on to be worth
    /// re-issuing every vehicle in it (design §2's 15°). Pure, for the self-check; the short way
    /// round the compass comes from <see cref="CircularBearingDifference"/>, the mod's one answer
    /// to that question.
    /// </summary>
    internal static bool ArcNeedsReaim(float aimedDegrees, float wantedDegrees, float reaimDegrees)
    {
        return CircularBearingDifference(NormalizeBearing(aimedDegrees), NormalizeBearing(wantedDegrees)) > reaimDegrees;
    }

    /// <summary>
    /// True while a garrison keeps its defence arc: in contact now, or inside
    /// <paramref name="arcHoldSeconds"/> of the last contact (design §2's "returns to the ring 60 s
    /// after contact lapses"). <paramref name="inContactUntil"/> is the platoon's own contact clock,
    /// so the last contact was <paramref name="contactHoldSeconds"/> before it runs out — which
    /// makes this the same freshness question <see cref="LossIsRecent"/> already answers for a lost
    /// member, and it is answered there rather than a second time here. Pure, for the self-check.
    /// </summary>
    internal static bool HoldsDefenceArc(
        float inContactUntil, float now, float contactHoldSeconds, float arcHoldSeconds)
    {
        return LossIsRecent(inContactUntil - contactHoldSeconds, now, arcHoldSeconds);
    }

    /// <summary>
    /// Where the next cross-country bound ends (design §3): <paramref name="boundMeters"/> along the
    /// straight line from <paramref name="from"/> to <paramref name="objective"/>, or the objective
    /// itself once it is inside one bound. Pure, for the self-check.
    /// </summary>
    internal static GlobalPosition NextBound(GlobalPosition from, GlobalPosition objective, float boundMeters)
    {
        float distance = CommanderGameAccess.HorizontalDistance(from.AsVector3(), objective.AsVector3());
        if (boundMeters <= 0f || distance <= boundMeters || distance <= 0f)
        {
            return objective;
        }

        float fraction = boundMeters / distance;
        return new GlobalPosition(
            from.x + (objective.x - from.x) * fraction,
            from.y + (objective.y - from.y) * fraction,
            from.z + (objective.z - from.z) * fraction);
    }

    /// <summary>
    /// True when the platoon may leave the bound it is on for the next one (design §3): half of it
    /// is closed up on this bound's formation slots — the same half-the-platoon measure
    /// <see cref="HasArrived"/> uses for an objective — or the bound has run
    /// <paramref name="timeoutSeconds"/> and the stragglers are not coming. Pure, for the
    /// self-check.
    /// </summary>
    internal static bool BoundComplete(int inCohesion, int memberCount, float secondsAtBound, float timeoutSeconds)
    {
        return inCohesion * 2 >= memberCount || secondsAtBound >= timeoutSeconds;
    }

    /// <summary>
    /// True when a platoon's destination must be issued as a bound rather than as one long leg the
    /// game's own routing may run down a road (design §3): every destination from an attack's
    /// release point onward, and every destination within <paramref name="offRoadRangeMeters"/> of
    /// a tracked enemy whatever the platoon is doing. Pure, for the self-check.
    /// </summary>
    internal static bool BoundsCrossCountry(
        bool attackLaunched, float nearestTrackedHostileMeters, float offRoadRangeMeters)
    {
        return attackLaunched || nearestTrackedHostileMeters <= offRoadRangeMeters;
    }

    /// <summary>
    /// Design §3, once per movement tick for a platoon under way: from its release point onward —
    /// and any time its destination is within <c>OffRoadRangeMeters</c> of a tracked enemy — the
    /// platoon crosses the ground in <c>BoundMeters</c> bounds, line abreast with the tanks leading,
    /// instead of being handed one long destination the game's own routing drives down a road. The
    /// next bound is issued when half the platoon has closed up on this one, or after
    /// <see cref="BoundTimeoutSeconds"/>. Returns true when it issued a bound this tick.
    /// <para>
    /// The contact drill keeps priority: the caller tests it first, exactly as it did before, so a
    /// platoon that can see a hostile deploys into its firing line rather than bounding past it.
    /// </para>
    /// </summary>
    private bool DriveBounds(FactionHQ hq, CommanderPlatoon platoon)
    {
        Unit? leader = platoon.Leader;
        if (leader == null
            || leader.disabled
            || (platoon.State != CommanderPlatoonState.Moving && platoon.State != CommanderPlatoonState.Attacking))
        {
            return false;
        }

        bool launched = platoon.State == CommanderPlatoonState.Attacking && platoon.Mission?.Launched == true;
        float offRoadRange = CommanderSettings.OffRoadRangeMeters;
        TryNearestTrackedHostile(hq, platoon.Objective, offRoadRange, out _, out float hostileRange);
        if (!BoundsCrossCountry(launched, hostileRange, offRoadRange))
        {
            if (platoon.Posture == CommanderGroundPosture.Bounding)
            {
                platoon.ClearGroundPosture();
            }

            return false;
        }

        GlobalPosition here = leader.transform.GlobalPosition();
        if (platoon.Posture != CommanderGroundPosture.Bounding)
        {
            platoon.Posture = CommanderGroundPosture.Bounding;
            if (!platoon.BoundLogged)
            {
                platoon.BoundLogged = true;
                LogBounding(hq, platoon, SituationPlaceLabel(hq, platoon));
            }
        }

        IssueBound(platoon, here);
        return true;
    }

    /// <summary>
    /// Cuts and issues one bound toward <c>platoon.Objective</c>: a new one when the last is
    /// finished, when the objective has moved under it, or when there is none yet; otherwise the
    /// one already standing, re-issued (which <see cref="CommanderMoveService.IssuePlatoonMove"/>
    /// turns into nothing for any member already on its slot). Line abreast on the heading to the
    /// objective, tanks in the centre. The move half of <see cref="DriveBounds"/>, split out
    /// because the counter-attack (design §4) crosses its own two legs by the same rules without
    /// being an attack.
    /// </summary>
    private void IssueBound(CommanderPlatoon platoon, GlobalPosition here)
    {
        bool reCut = platoon.BoundTarget == null
            || !CommanderGameAccess.ApproximatelyEqual(
                platoon.BoundFor, platoon.Objective, CommanderMoveService.ReissueToleranceMeters)
            || BoundComplete(
                CountInCohesion(platoon),
                platoon.Members.Count,
                Time.time - platoon.BoundIssuedAt,
                BoundTimeoutSeconds);
        if (reCut)
        {
            // Slot 0 of a line abreast is its centre, so march order is what puts the tanks in the
            // middle of it and the air defence out on the flanks.
            OrderForMarch(platoon.Members);
            platoon.BoundTarget = NextBound(here, platoon.Objective, CommanderSettings.BoundMeters);
            platoon.BoundFor = platoon.Objective;
            platoon.BoundIssuedAt = Time.time;
        }

        CommanderMoveService.IssuePlatoonMove(
            platoon.Members,
            platoon.BoundTarget!.Value,
            CommanderFormationShape.Line,
            platoon.Issued,
            BearingTo(here, platoon.Objective));
    }

    /// <summary>
    /// What a platoon answering a reinforcement request actually does when it gets there
    /// (design §4). A point that is still held by a garrison of at least
    /// <paramref name="minGarrison"/> does not need another six vehicles standing in its ring: the
    /// reinforcement goes round the attack and hits it from a flank, or — with nothing tracked to
    /// hit — sits out on the approach as a screen. It folds into the ring only once the garrison
    /// has dropped below the minimum, which is the moment the point stops paying. Pure, for the
    /// self-check.
    /// </summary>
    internal static CommanderGroundPosture ReinforcementPosture(
        bool isReinforcing, int garrisonPresent, int minGarrison, bool enemyTracked)
    {
        if (!isReinforcing || garrisonPresent < minGarrison)
        {
            return CommanderGroundPosture.Ring;
        }

        return enemyTracked ? CommanderGroundPosture.CounterAttack : CommanderGroundPosture.Screen;
    }

    /// <summary>The bearing a counter-attack swings out on: square off the threat bearing, to
    /// whichever side the caller found emptier (design §4's "±90°, whichever side is farther from
    /// tracked hostiles"). Pure, for the self-check.</summary>
    internal static float FlankBearing(float threatBearingDegrees, bool takeLeft)
    {
        return NormalizeBearing(threatBearingDegrees + (takeLeft ? -90f : 90f));
    }

    /// <summary>
    /// How many vehicles are actually standing on <paramref name="mission"/>'s point right now,
    /// not counting <paramref name="excluding"/> — the arriving reinforcement itself, which is the
    /// whole question. Counts the members of the mission's other platoons and its picket detachment
    /// inside the point's own radius, which is exactly the set the point's take-and-hold rule
    /// counts when it decides whether the point still pays.
    /// </summary>
    private static int CountGarrisonPresent(CommanderOperationsMission mission, CommanderPlatoon excluding)
    {
        if (mission.Point == null)
        {
            return 0;
        }

        int present = 0;
        for (int i = 0; i < mission.Assigned.Count; i++)
        {
            CommanderPlatoon other = mission.Assigned[i];
            if (ReferenceEquals(other, excluding))
            {
                continue;
            }

            present += CountInsidePoint(mission.Point, other.Members);
        }

        return present + CountInsidePoint(mission.Point, mission.PicketMembers);
    }

    /// <summary>Live vehicles of <paramref name="members"/> inside <paramref name="point"/>'s
    /// radius.</summary>
    private static int CountInsidePoint(CommanderStrategicPoint point, IReadOnlyList<Unit> members)
    {
        int count = 0;
        for (int i = 0; i < members.Count; i++)
        {
            Unit unit = members[i];
            if (unit != null
                && !unit.disabled
                && FastMath.InRange(unit.transform.GlobalPosition(), point.Position, point.Radius))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Which way round a counter-attack swings: the flank whose own kilometre of ground has the
    /// fewest tracked hostiles near it (design §4's "whichever side is farther from tracked
    /// hostiles"). With nothing tracked on either side the left is taken, so the choice is at least
    /// stable from tick to tick instead of flapping.
    /// </summary>
    private static bool FlankSideIsLeft(FactionHQ hq, GlobalPosition center, float threatBearingDegrees)
    {
        GlobalPosition left = PositionAt(center, FlankBearing(threatBearingDegrees, true), CounterAttackFlankMeters);
        GlobalPosition right = PositionAt(center, FlankBearing(threatBearingDegrees, false), CounterAttackFlankMeters);
        TryNearestTrackedHostile(hq, left, CounterAttackReachMeters, out _, out float leftRange);
        TryNearestTrackedHostile(hq, right, CounterAttackReachMeters, out _, out float rightRange);
        return leftRange >= rightRange;
    }

    /// <summary>
    /// The approach a screen watches (design §4): the way the nearest hostile the tracking database
    /// still remembers lies — its history, out to the front range, rather than the point's own
    /// contact ring — and failing that the way the enemy's nearest asset lies. The same answer
    /// <see cref="ThreatBearingFor"/> gives, asked over a wider circle.
    /// </summary>
    private static float ScreenBearingFor(FactionHQ hq, CommanderStrategicPoint point)
    {
        return ThreatBearingFor(hq, point, CommanderSettings.OperationsFrontRangeMeters);
    }

    /// <summary>
    /// Design §4, once per movement tick, for a platoon answering a reinforcement request at a
    /// point that is still held: it counter-attacks from a flank, or screens the most threatened
    /// approach, and folds into the ring only once the garrison has dropped below
    /// <c>PointsMinGarrison</c>. Returns true when it moved the platoon, so the caller leaves the
    /// ordinary march and ring branches alone.
    /// </summary>
    private bool DriveReinforcement(FactionHQ hq, CommanderPlatoon platoon)
    {
        CommanderOperationsMission? mission = platoon.Mission;
        Unit? leader = platoon.Leader;
        if (mission == null
            || mission.Kind != CommanderMissionKind.ForwardBase
            || mission.Point == null
            || platoon.ReinforcesLabel != mission.Label
            || platoon.ReinforcesLabel.Length == 0
            || leader == null
            || leader.disabled
            || platoon.State == CommanderPlatoonState.Forming
            || platoon.State == CommanderPlatoonState.Withdrawing)
        {
            return false;
        }

        CommanderStrategicPoint point = mission.Point;
        bool enemyTracked = TryNearestTrackedHostile(
            hq, point.Position, CounterAttackReachMeters, out GlobalPosition hostile, out _);
        CommanderGroundPosture posture = ReinforcementPosture(
            isReinforcing: true,
            CountGarrisonPresent(mission, platoon),
            CommanderSettings.PointsMinGarrison,
            enemyTracked);
        if (posture == CommanderGroundPosture.Ring)
        {
            if (platoon.Posture == CommanderGroundPosture.CounterAttack
                || platoon.Posture == CommanderGroundPosture.Screen)
            {
                platoon.ClearGroundPosture();
                platoon.Objective = point.Position;
                LogFoldsIntoRing(hq, platoon, point.Label);
            }

            return false;
        }

        GlobalPosition here = leader.transform.GlobalPosition();
        if (posture == CommanderGroundPosture.CounterAttack)
        {
            float threatBearing = BearingTo(point.Position, hostile);
            if (platoon.Posture != CommanderGroundPosture.CounterAttack || platoon.FlankPosition == null)
            {
                platoon.Posture = CommanderGroundPosture.CounterAttack;
                platoon.FlankReached = false;
                platoon.PostureBearing = threatBearing;
                platoon.FlankPosition = CommanderGameAccess.SnapToTerrain(PositionAt(
                    point.Position,
                    FlankBearing(threatBearing, FlankSideIsLeft(hq, point.Position, threatBearing)),
                    CounterAttackFlankMeters));
                LogCounterAttack(hq, platoon, point.Label);
            }

            // The contact drill keeps priority over the manoeuvre, exactly as it does over an
            // attack's bounds: a platoon that can see a hostile fights it where it stands rather
            // than driving past it, and the contact mark is what calls the wing in over it.
            if (ContactDrill(hq, platoon))
            {
                return true;
            }

            platoon.FlankReached |= FastMath.InRange(here, platoon.FlankPosition!.Value, AssaultArrivedMeters);
            // Swing out to the flank first, then go in on the enemy from there — both legs by the
            // same cross-country bounds an attack uses (design §4: "bounding cross-country,
            // Section 3 rules").
            platoon.Objective = platoon.FlankReached ? hostile : platoon.FlankPosition.Value;
            IssueBound(platoon, here);
            return true;
        }

        // A screen is a standing position like a hold post, so it is marked in contact the same way
        // a garrison on its posts is (addendum 2026-09-14 §2) — otherwise a screen being overrun
        // would never call for air support.
        DetectHoldingContact(hq, platoon);

        float approach = ScreenBearingFor(hq, point);
        if (platoon.Posture != CommanderGroundPosture.Screen
            || ArcNeedsReaim(platoon.PostureBearing, approach, DefenceArcReaimDegrees))
        {
            GlobalPosition raw = PositionAt(point.Position, approach, ScreenRangeMeters);
            // A screen belongs on the approach it watches, and an approach is a road.
            GlobalPosition screen =
                CommanderStrategicPointService.Instance?.TryNearestRoadPoint(raw, ScreenRoadSnapMeters, out GlobalPosition onRoad) == true
                    ? onRoad
                    : raw;
            platoon.Objective = CommanderGameAccess.SnapToTerrain(screen);
            platoon.PostureBearing = approach;
            if (platoon.Posture != CommanderGroundPosture.Screen)
            {
                platoon.Posture = CommanderGroundPosture.Screen;
                LogScreen(hq, platoon, point.Label);
            }
        }

        // Facing outward: the line is laid across the approach bearing, and the platoon reaches it
        // driving out from the point, so it ends up looking the way the enemy would come.
        CommanderMoveService.IssuePlatoonMove(
            platoon.Members,
            platoon.Objective,
            CommanderFormationShape.Line,
            platoon.Issued,
            platoon.PostureBearing);
        return true;
    }

    /// <summary>Arc members, ordered tanks-first, and the ground positions of their arc slots.</summary>
    private readonly List<Unit> arcMembers = new();
    private readonly List<Unit> arcStayBehind = new();
    private readonly List<GlobalPosition> arcPosts = new();

    /// <summary>
    /// True for a vehicle that goes forward onto the defence arc (design §2's "tanks and IFVs"):
    /// the armour, and a carrier that is actually an infantry fighting vehicle. A carrier with no
    /// gun of its own (<c>VehicleType.LCV</c>) is transport, and stays on the far side of the ring
    /// with the truck.
    /// </summary>
    private static bool GoesForwardOnArc(Unit unit)
    {
        VehicleDefinition? definition = unit.definition as VehicleDefinition;
        return CommanderPlatoonRoles.Of(definition) switch
        {
            CommanderPlatoonRole.Armour => true,
            CommanderPlatoonRole.Carrier => definition != null && definition.vehicleType == VehicleType.AFV,
            _ => false,
        };
    }

    /// <summary>
    /// Design §2, once per movement tick for a garrison standing on a point: while the platoon or
    /// its point is in contact, the tanks and IFVs move forward onto an arc centred on the threat
    /// bearing, the air defence keeps its ring posts, and the truck and any transport carrier take
    /// the ring's far side. The arc is re-aimed only when the bearing has moved more than
    /// <see cref="DefenceArcReaimDegrees"/>, and it is given up
    /// <see cref="DefenceArcHoldSeconds"/> after the last contact. Returns true when it issued the
    /// arc this tick, so the caller leaves the ordinary ring drive alone.
    /// </summary>
    private bool DriveDefenceArc(FactionHQ hq, CommanderPlatoon platoon, CommanderStrategicPoint point)
    {
        float contactUntil = Mathf.Max(platoon.InContactUntil, platoon.Mission?.ContactUntil ?? -1f);
        if (!HoldsDefenceArc(contactUntil, Time.time, ContactHoldSeconds, DefenceArcHoldSeconds))
        {
            if (platoon.Posture == CommanderGroundPosture.DefenceArc)
            {
                platoon.ClearGroundPosture();
                LogReturnsToRing(hq, platoon);
            }

            return false;
        }

        // The platoon's own contact anchor is where the fight actually is, and it survives the
        // tracking lapsing. It is meaningless when the evidence was a lost member with nothing
        // tracked (the anchor is then the platoon itself), so the ring's own threat bearing —
        // nearest tracked hostile, else the way the enemy lies — answers for it.
        float anchorRange = CommanderGameAccess.HorizontalDistance(
            point.Position.AsVector3(), platoon.ContactBearingAnchor.AsVector3());
        float wanted = platoon.InContactUntil >= Time.time && anchorRange > HoldArrivedMeters
            ? BearingTo(point.Position, platoon.ContactBearingAnchor)
            : ThreatBearingFor(hq, point);

        bool forming = platoon.Posture != CommanderGroundPosture.DefenceArc;
        if (forming || ArcNeedsReaim(platoon.PostureBearing, wanted, DefenceArcReaimDegrees))
        {
            platoon.PostureBearing = wanted;
        }

        if (forming)
        {
            platoon.Posture = CommanderGroundPosture.DefenceArc;
            LogDefenceArc(hq, platoon, platoon.PostureBearing, point.Label);
        }

        arcMembers.Clear();
        arcStayBehind.Clear();
        for (int i = 0; i < platoon.Members.Count; i++)
        {
            Unit unit = platoon.Members[i];
            if (unit == null || unit.disabled)
            {
                continue;
            }

            if (GoesForwardOnArc(unit))
            {
                arcMembers.Add(unit);
            }
            else
            {
                arcStayBehind.Add(unit);
            }
        }

        // Slot 0 is the centre of the arc and has to be a tank, so the arc takes march order rather
        // than the ring's stable-by-instance-id order.
        arcMembers.Sort(static (a, b) =>
        {
            int rank = MarchRank(CommanderPlatoonRoles.Of(a.definition as VehicleDefinition))
                .CompareTo(MarchRank(CommanderPlatoonRoles.Of(b.definition as VehicleDefinition)));
            return rank != 0 ? rank : a.GetInstanceID().CompareTo(b.GetInstanceID());
        });

        float arcDistance = DefenceArcDistanceMeters(point.Radius, CommanderSettings.DefenceArcStandoffMeters);
        arcPosts.Clear();
        for (int i = 0; i < arcMembers.Count; i++)
        {
            GlobalPosition candidate = CommanderGameAccess.SnapToTerrain(PositionAt(
                point.Position,
                DefenceArcBearing(platoon.PostureBearing, i, DefenceArcSpacingMeters, arcDistance),
                arcDistance));
            if (!CommanderGameAccess.IsBelowSeaLevel(candidate))
            {
                arcPosts.Add(candidate);
            }
        }

        // The vehicles that stay behind keep taking ring posts, by the same role split as ever: the
        // air defence on its own pair, the truck and any transport carrier on the far side.
        SplitByHoldRole(arcStayBehind, holdRoleOuter, holdRoleAirDefence, holdRoleInner);
        CommanderHoldPostSet posts = EnsureHoldPostSet(
            point,
            platoon.PostureBearing,
            Mathf.Max(1, holdRoleOuter.Count),
            holdRoleAirDefence.Count,
            holdRoleInner.Count);
        DriveMembersToPosts(holdRoleOuter, posts.Outer, platoon.Issued);
        DriveMembersToPosts(holdRoleAirDefence, PostsOrFallback(posts.AirDefence, posts.Outer), platoon.Issued);
        DriveMembersToPosts(holdRoleInner, PostsOrFallback(posts.Inner, posts.Outer), platoon.Issued);
        DriveMembersToPosts(arcMembers, PostsOrFallback(arcPosts, posts.Outer), platoon.Issued, stableByInstanceId: false);
        return true;
    }

    /// <summary>The ground-tactics rules, driven with synthetic numbers at plugin load: ring
    /// spacing and oversubscription, role-to-post assignment, arc geometry, bound sizing and the
    /// reinforcement's counter-attack / screen / fold decision (design §5).</summary>
    private static void CheckGroundTactics(List<string> failures)
    {
        CheckHoldRing(failures);
        CheckDefenceArc(failures);
        CheckBounds(failures);
        CheckReinforcementPosture(failures);
        CheckScreenRoad(failures);
    }

    /// <summary>Design §4: a screen sits on the approach it watches, which means the nearest point
    /// on an actual road — and refuses to be dragged onto one that is not near enough to be the
    /// approach at all. Drives <c>CommanderStrategicPointService.TryNearestRoadPoint</c>, whose
    /// only runtime caller is the screen.</summary>
    private static void CheckScreenRoad(List<string> failures)
    {
        List<List<GlobalPosition>> roads = new()
        {
            new List<GlobalPosition> { new(0f, 0f, 0f), new(2000f, 0f, 0f) },
        };

        bool found = CommanderStrategicPointService.TryNearestRoadPoint(
            roads, new GlobalPosition(500f, 0f, 300f), ScreenRoadSnapMeters, out GlobalPosition closest, out float distance);
        Expect(failures, "a screen snaps onto the road beside it", found, true);
        Expect(failures, "the snap takes the nearest point on the road", closest.x, 500f);
        Expect(failures, "the snap lands on the road itself", closest.z, 0f);
        Expect(failures, "the snap reports how far it moved", distance, 300f);
        Expect(
            failures,
            "a road past the cap is a different approach",
            CommanderStrategicPointService.TryNearestRoadPoint(
                roads, new GlobalPosition(500f, 0f, 700f), ScreenRoadSnapMeters, out _, out _),
            false);
        Expect(
            failures,
            "a map that kept no roads snaps nothing",
            CommanderStrategicPointService.TryNearestRoadPoint(
                new List<List<GlobalPosition>>(), new GlobalPosition(0f, 0f, 0f), ScreenRoadSnapMeters, out _, out _),
            false);
    }

    /// <summary>Design §4: at the garrison boundary a reinforcement counter-attacks, screens or
    /// folds into the ring, and a platoon that is not answering a request always rings.</summary>
    private static void CheckReinforcementPosture(List<string> failures)
    {
        const int minGarrison = 2;
        Expect(
            failures,
            "a reinforcement at a held point under attack counter-attacks",
            ReinforcementPosture(true, minGarrison, minGarrison, true) == CommanderGroundPosture.CounterAttack,
            true);
        Expect(
            failures,
            "a reinforcement at a held quiet point screens",
            ReinforcementPosture(true, minGarrison, minGarrison, false) == CommanderGroundPosture.Screen,
            true);
        Expect(
            failures,
            "a reinforcement folds into the ring one vehicle below the minimum",
            ReinforcementPosture(true, minGarrison - 1, minGarrison, true) == CommanderGroundPosture.Ring,
            true);
        Expect(
            failures,
            "a platoon that is not reinforcing always rings",
            ReinforcementPosture(false, minGarrison + 4, minGarrison, true) == CommanderGroundPosture.Ring,
            true);

        Expect(failures, "the left flank is square off the threat", FlankBearing(90f, true), 0f);
        Expect(failures, "the right flank is square off the other way", FlankBearing(90f, false), 180f);
        Expect(failures, "a flank bearing wraps the compass", FlankBearing(45f, true), 315f);
    }

    /// <summary>Design §3: bounds are one <c>BoundMeters</c> long until the objective is closer
    /// than that, the next one waits for half the platoon or 90 s, and everything within a
    /// kilometre of a tracked enemy is bounded rather than driven.</summary>
    private static void CheckBounds(List<string> failures)
    {
        GlobalPosition start = new(0f, 0f, 0f);
        GlobalPosition twoKilometres = new(0f, 0f, 2000f);
        Expect(failures, "an 800 m bound on a 2 km leg stops at 800 m", NextBound(start, twoKilometres, 800f).z, 800f);
        Expect(failures, "the last bound lands on the objective", NextBound(new GlobalPosition(0f, 0f, 1500f), twoKilometres, 800f).z, 2000f);
        Expect(failures, "a bound never overshoots its own objective", NextBound(start, new GlobalPosition(0f, 0f, 300f), 800f).z, 300f);

        Expect(failures, "half the platoon closed up releases the next bound", BoundComplete(3, 6, 10f, BoundTimeoutSeconds), true);
        Expect(failures, "one short of half holds the bound", BoundComplete(2, 6, 10f, BoundTimeoutSeconds), false);
        Expect(failures, "a bound that has run 90 s goes on without the stragglers", BoundComplete(1, 6, BoundTimeoutSeconds, BoundTimeoutSeconds), true);

        Expect(failures, "a launched attack always bounds", BoundsCrossCountry(true, 20000f, 1000f), true);
        Expect(failures, "a march a kilometre from a tracked enemy bounds", BoundsCrossCountry(false, 1000f, 1000f), true);
        Expect(failures, "a march past a kilometre takes the ordinary route", BoundsCrossCountry(false, 1001f, 1000f), false);
    }

    /// <summary>Design §2: the arc stands one standoff forward of the ring (cut to the radius on a
    /// small point), its centre slot on the threat bearing with neighbours one spacing of arc to
    /// either side, it re-aims only past 15°, and it runs out 60 s after the last contact.</summary>
    private static void CheckDefenceArc(List<string> failures)
    {
        const float radius = 1000f;
        const float standoff = 400f;
        float distance = DefenceArcDistanceMeters(radius, standoff);
        Expect(failures, "the arc stands one standoff forward of the ring", distance, 1400f);
        Expect(failures, "a small point cuts the standoff to its own radius", DefenceArcDistanceMeters(300f, standoff), 600f);

        Expect(failures, "the centre of the arc sits on the threat bearing", DefenceArcBearing(90f, 0, DefenceArcSpacingMeters, distance), 90f);
        float step = DefenceArcSpacingMeters / distance * Mathf.Rad2Deg;
        Expect(failures, "the first neighbour is one spacing left", DefenceArcBearing(90f, 1, DefenceArcSpacingMeters, distance), 90f - step);
        Expect(failures, "the second neighbour is one spacing right", DefenceArcBearing(90f, 2, DefenceArcSpacingMeters, distance), 90f + step);
        Expect(failures, "the arc wraps the compass rather than running past it", DefenceArcBearing(5f, 1, 150f, 1000f) > 180f, true);

        Expect(failures, "15 degrees of drift is not worth re-aiming", ArcNeedsReaim(10f, 25f, DefenceArcReaimDegrees), false);
        Expect(failures, "more than 15 degrees of drift re-aims the arc", ArcNeedsReaim(10f, 26f, DefenceArcReaimDegrees), true);
        Expect(failures, "drift is measured the short way round", ArcNeedsReaim(355f, 5f, DefenceArcReaimDegrees), false);

        // Contact last seen at 80 s, so the contact clock reads 100 s and the arc runs to 140 s.
        Expect(failures, "the arc stands while the platoon is in contact", HoldsDefenceArc(100f, 95f, ContactHoldSeconds, DefenceArcHoldSeconds), true);
        Expect(failures, "the arc still stands 60 s after the last contact", HoldsDefenceArc(100f, 140f, ContactHoldSeconds, DefenceArcHoldSeconds), true);
        Expect(failures, "the arc returns to the ring past 60 s", HoldsDefenceArc(100f, 141f, ContactHoldSeconds, DefenceArcHoldSeconds), false);
        Expect(failures, "a platoon that has never been in contact forms no arc", HoldsDefenceArc(-1f, 0f, ContactHoldSeconds, DefenceArcHoldSeconds), false);
    }

    /// <summary>Scratch lists for <see cref="CheckHoldRing"/>; load-time only.</summary>
    private static readonly List<CommanderPostSlot> checkOuter = new();
    private static readonly List<CommanderPostSlot> checkAirDefence = new();
    private static readonly List<CommanderPostSlot> checkInner = new();

    /// <summary>Design §1: the ring fills the point's full radius, never closes two neighbours
    /// inside the minimum spacing, pushes an oversubscribed remainder onto the inner ring, and puts
    /// the umbrella on the threat side with the carrier and truck opposite.</summary>
    private static void CheckHoldRing(List<string> failures)
    {
        const float radius = 1000f;
        Expect(failures, "a 1 km ring carries 25 posts at 250 m spacing", RingSlotsFor(radius, 40, RingMinSpacingMeters), 25);
        Expect(failures, "a ring never carries more posts than members", RingSlotsFor(radius, 6, RingMinSpacingMeters), 6);
        Expect(failures, "a ring narrower than one spacing carries one post", RingSlotsFor(120f, 6, RingMinSpacingMeters), 1);

        PlanHoldPosts(radius, 0f, 3, 2, 2, RingMinSpacingMeters, checkOuter, checkAirDefence, checkInner);
        Expect(failures, "a three-vehicle ring takes three outer posts", checkOuter.Count, 3);
        Expect(failures, "the first ring post faces the threat", checkOuter[0].BearingDegrees, 0f);
        Expect(failures, "a ring post stands on the full hold radius", checkOuter[0].RadiusMeters, radius);
        Expect(failures, "a post faces the way it bears from the centre", checkOuter[1].FacingDegrees, checkOuter[1].BearingDegrees);
        Expect(failures, "ring neighbours keep the minimum spacing", NeighbourGapMeters(checkOuter[0], checkOuter[1]) >= RingMinSpacingMeters, true);

        Expect(failures, "the air-defence pair straddles the threat bearing", checkAirDefence[0].BearingDegrees, 330f);
        Expect(failures, "the air-defence pair sits at 0.3 of the radius", checkAirDefence[0].RadiusMeters, radius * AirDefenceRingFraction);
        Expect(failures, "the carrier and truck take the far side", checkInner[0].BearingDegrees, 150f);

        PlanHoldPosts(radius, 0f, 30, 0, 0, RingMinSpacingMeters, checkOuter, checkAirDefence, checkInner);
        Expect(failures, "an oversubscribed ring keeps 25 posts outside", checkOuter.Count > 25, true);
        Expect(failures, "the remainder falls back to the inner ring", checkOuter[checkOuter.Count - 1].RadiusMeters, radius * InnerRingFraction);
    }

    /// <summary>Straight-line distance between two polar slots, for the spacing check.</summary>
    private static float NeighbourGapMeters(CommanderPostSlot a, CommanderPostSlot b)
    {
        Vector2 first = new(
            a.RadiusMeters * Mathf.Cos(a.BearingDegrees * Mathf.Deg2Rad),
            a.RadiusMeters * Mathf.Sin(a.BearingDegrees * Mathf.Deg2Rad));
        Vector2 second = new(
            b.RadiusMeters * Mathf.Cos(b.BearingDegrees * Mathf.Deg2Rad),
            b.RadiusMeters * Mathf.Sin(b.BearingDegrees * Mathf.Deg2Rad));
        return (first - second).magnitude;
    }
}
