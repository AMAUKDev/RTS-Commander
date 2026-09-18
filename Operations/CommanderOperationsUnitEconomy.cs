using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Which reduction a commander over the ground ceiling reaches for, in the order design §3 fixes
/// (<c>conductor/tracks/unit-economy_20260918/design.md</c>).
/// </summary>
internal enum CommanderUnitEconomyStep
{
    /// <summary>Under the ceiling: the commander grows freely and none of the reductions apply.</summary>
    None,

    /// <summary>Vehicles assigned to nothing are sold back first. Nothing is lost — they were not fighting.</summary>
    CashInIdle,

    /// <summary>Then the surplus garrison on ground nobody has contested for a while is cashed in.</summary>
    RetireQuietGround,

    /// <summary>Only when neither of those has anything left to give does the commander stop buying.</summary>
    RefuseGrowth,
}

/// <summary>
/// The unit economy (design.md, <c>unit-economy_20260918</c>): the rules that keep the number of LIVE
/// GROUND VEHICLES a commander fields down, because the game's own per-frame cost was measured
/// growing as the SQUARE of live unit count — 157 units at 8.7 ms, 480 at 120 ms — and ground
/// vehicles were two-thirds of all growth over half an hour
/// (<c>conductor/designs/2026-09-17-frame-rate-investigation.md</c>).
/// </summary>
/// <remarks>
/// A partial of the operations service rather than a service of its own: every one of these rules
/// reads the pool, the platoons and the missions that only this class holds, and a service of its own
/// would have to be handed all three. The four rules are deliberately small and sit beside the code
/// they govern — the idle sale is <c>SellSurplusPool</c> in
/// <c>Operations/CommanderOperationsService.cs</c>, the garrison size is
/// <see cref="CommanderSettings.PointsMinGarrison"/> read by the fills that already existed, the
/// ceiling is one predicate the ground buyers read, and only the quiet-ground retirement below is
/// new behaviour.
/// <para>
/// NOTHING here touches aircraft, air packages, insertions or lifts. Aircraft were measured at 55 of
/// 427 units and a rounding error in the growth; the air side is explicitly out of scope.
/// </para>
/// <para>
/// ponytail: no Unity <c>Vector3</c> or <c>Quaternion</c> call may enter the pure rules below. They
/// are checked by an offline harness that loads the built assembly outside the player, where those
/// are native calls that throw (recorded in DECISION-058).
/// </para>
/// </remarks>
internal sealed partial class CommanderOperationsService
{
    /// <summary>
    /// The fewest vehicles that may be left standing on a point the commander holds: one. Ownership
    /// is re-derived every <c>CommanderStrategicPointService.HoldCheckSeconds</c> (5 s) from what
    /// stands in the ring, so a point emptied by any of these rules is a point silently handed to
    /// whoever walks in next — the failure the strategic save work hit and wrote up in
    /// <c>conductor/designs/2026-09-17-save-load-study.md</c>. One is a floor under the garrison
    /// setting, not a replacement for it: the floor actually applied is the larger of the two.
    /// </summary>
    private const int MinimumHoldersOnAHeldPoint = 1;

    /// <summary>Seconds in a minute: 60. Every timeout in this file is a setting in minutes, because
    /// a commander's review runs every 30 s and minutes are the unit the user tunes in.</summary>
    private const float SecondsPerMinute = 60f;

    /// <summary>
    /// Whether one more ground vehicle may be bought, pure. THE ceiling test: every ground purchase
    /// path the commander owns reads this one predicate and no other. "Replaces losses but does not
    /// grow" needs no separate replacement accounting — the test is against the LIVE count, so a
    /// vehicle lost drops the count below the ceiling and opens exactly one purchase, and a vehicle
    /// bought closes it again.
    /// <para>A ceiling of zero or less is off, the convention every other rule in the mod uses for a
    /// setting that can be switched off without deleting its caller.</para>
    /// </summary>
    internal static bool GroundBuyAllowed(int liveGroundVehicles, int ceiling)
    {
        return ceiling <= 0 || liveGroundVehicles < ceiling;
    }

    /// <summary>
    /// Whether one more aircraft may be bought, pure — the air mirror of
    /// <see cref="GroundBuyAllowed(int, int)"/>, and deliberately the same shape: inclusive at the
    /// ceiling, tested against the LIVE count so a loss opens exactly one replacement, and a
    /// non-positive ceiling is off.
    /// <para>
    /// The one thing it adds is a SECOND line. A standing patrol — fighters circling a point or a
    /// platoon because hostile aircraft were tracked near it, and nothing else — may buy only while
    /// the faction is <paramref name="patrolReserve"/> aircraft below the ceiling. Everything else
    /// buys right up to it: transport escorts, strike packages, the radar aeroplane, anti-radiation
    /// sorties, air support over a ground fight, and home defence. Without the second line the
    /// twenty-seven patrol requests one commander carried on 2026-09-18 would take every slot, and
    /// every lift would hold at its form-up point waiting for an escort nobody could buy — the same
    /// outage as grounding the transports outright, reached by a different road.
    /// </para>
    /// <para>
    /// A reserve at or above the ceiling grounds standing patrols entirely. That is a legitimate
    /// setting rather than an error: the line goes to zero or below and no live count is ever under
    /// it. The reserve itself is clamped at zero because a NEGATIVE reserve would raise the patrol
    /// line ABOVE the ceiling and let patrols buy past the very limit this rule exists to hold —
    /// that clamp is load-bearing and has its own named check. The line is deliberately not clamped
    /// as well: a second clamp there could never change an answer, because an aircraft count is
    /// never negative, and a defect plant on 2026-09-18 proved it dead by failing no check at all.
    /// </para>
    /// </summary>
    /// <summary>
    /// Whether the three air numbers make sense together, pure (Testing rule 1: a constant that can
    /// be retuned into nonsense gets a check that says so at load). The reserve must leave at least
    /// one slot for a standing patrol, and the maximum must not sit below the floor it is a ceiling
    /// on.
    /// <para>
    /// The cases that drive this pass the shipped defaults as LITERALS rather than reading
    /// <see cref="CommanderSettings"/>, because this block runs under an offline harness with no
    /// BepInEx config bound. That is a real weakness and is written down rather than hidden: the
    /// literals in the check and the defaults in <c>CommanderSettings</c> can drift apart, and only
    /// a reader comparing the two will notice. What the check does catch is the relationship being
    /// inverted — which is the failure that would silently ground every patrol in the mod.
    /// </para>
    /// </summary>
    internal static bool AirCeilingDefaultsCoherent(int patrolReserve, int floor, int max)
    {
        return patrolReserve >= 0 && patrolReserve < floor && floor <= max;
    }

    internal static bool AirBuyAllowed(int liveAircraft, int ceiling, int patrolReserve, bool standingPatrol)
    {
        if (ceiling <= 0)
        {
            return true;
        }

        int line = standingPatrol ? ceiling - Mathf.Max(0, patrolReserve) : ceiling;
        return liveAircraft < line;
    }

    /// <summary>
    /// How many vehicles must be left standing on a point, pure — the guarantee that no rule in this
    /// file can hand a point over. A point the commander HOLDS keeps at least the garrison setting,
    /// and never fewer than <see cref="MinimumHoldersOnAHeldPoint"/>; ground it does not hold keeps
    /// nothing, and neither does a point being deliberately given up.
    /// <para>
    /// The garrison setting is the floor because it is ALSO the ownership threshold
    /// (<c>CommanderStrategicPointService.QualifyingFaction</c> reads the same
    /// <see cref="CommanderSettings.PointsMinGarrison"/>). Keeping that many means the commander
    /// still qualifies to hold the point after the retirement, which is what makes "never leave a
    /// held point with nothing standing on it" and "never disband the last holder" the same rule and
    /// provable rather than hoped for.
    /// </para>
    /// <param name="givenUp">True only when the commander is deliberately abandoning the point. The
    /// seam a future withdrawal would use; nothing in this track ever passes true, so today the floor
    /// on a held point is absolute.</param>
    /// </summary>
    internal static int HeldPointKeepFloor(bool pointHeld, bool givenUp, int garrisonPerPoint)
    {
        return pointHeld && !givenUp ? Mathf.Max(MinimumHoldersOnAHeldPoint, garrisonPerPoint) : 0;
    }

    /// <summary>
    /// How many of a point's holders may be cashed in for standing on quiet ground, pure — the whole
    /// of design §2.4 and the first two absolute rules of §3 in one table.
    /// <list type="bullet">
    /// <item>Anything in contact retires NOTHING, whatever the clock says. The first absolute rule,
    /// and it is tested first so no arithmetic below can ever override it.</item>
    /// <item>A timeout of zero or less switches the retirement off.</item>
    /// <item>Quiet for exactly the timeout retires — inclusive at the boundary, the convention the
    /// rest of the mod uses.</item>
    /// <item>What retires is the surplus over the keep floor, never the floor itself.</item>
    /// </list>
    /// </summary>
    internal static int QuietGroundRetirement(
        bool inContact, float quietSeconds, float quietMinutes, int holders, int keepFloor)
    {
        if (inContact || quietMinutes <= 0f || quietSeconds < quietMinutes * SecondsPerMinute)
        {
            return 0;
        }

        return Mathf.Max(0, holders - Mathf.Max(0, keepFloor));
    }

    /// <summary>
    /// Whether one whole platoon may be cashed in off a quiet point, pure. Three conditions, and
    /// each one is a way the retirement could otherwise break something:
    /// <list type="bullet">
    /// <item>It has live members at all — an empty roster is the sweep's business, not a sale.</item>
    /// <item>Every live member is standing in the ring. A platoon with a vehicle still on the road is
    /// a relief arriving, not a garrison on quiet ground, and it was never counted as a holder, so
    /// taking it would remove vehicles the floor was never worked out against.</item>
    /// <item>The whole platoon fits inside what is allowed. Half a platoon cashed in is a platoon
    /// that can no longer fight and still costs frame time — the opposite of both goals.</item>
    /// </list>
    /// </summary>
    internal static bool PlatoonRetirable(int liveMembers, int membersInRing, int alreadyTaken, int allowed)
    {
        return liveMembers > 0 && membersInRing == liveMembers && alreadyTaken + liveMembers <= allowed;
    }

    /// <summary>
    /// Design §3's order of application, pure: a commander over the ceiling cashes in its idle
    /// reserve first, then retires quiet ground, and only refuses to grow when neither has anything
    /// left to give. A commander under the ceiling is doing none of it.
    /// <para>
    /// The running code implements this order structurally — one review runs the idle sale, then the
    /// quiet-ground retirement, and the buyer reads the ceiling on its own review afterwards — and
    /// this table is what the commander's log line reads to say which step it is on, so the order is
    /// stated in exactly one place and that place has a live caller.
    /// </para>
    /// </summary>
    internal static CommanderUnitEconomyStep NextUnitEconomyStep(
        int liveGroundVehicles, int ceiling, int idleCandidates, int quietCandidates)
    {
        if (GroundBuyAllowed(liveGroundVehicles, ceiling))
        {
            return CommanderUnitEconomyStep.None;
        }

        if (idleCandidates > 0)
        {
            return CommanderUnitEconomyStep.CashInIdle;
        }

        return quietCandidates > 0 ? CommanderUnitEconomyStep.RetireQuietGround : CommanderUnitEconomyStep.RefuseGrowth;
    }

    /// <summary>
    /// Live ground vehicles this faction fields, counted off its own unit list the way
    /// <c>CommanderEnemyCommanderService.CountShips</c> counts hulls. Buildings and aircraft are
    /// excluded by the type test alone: <c>Building</c>, <c>Aircraft</c> and <c>Missile</c> are all
    /// siblings of <c>GroundVehicle</c> under <c>Unit</c>, never subclasses of it.
    /// </summary>
    internal static int CountLiveGroundVehicles(FactionHQ hq)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is GroundVehicle && !unit.disabled)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The ceiling as the ground buyers ask it: true when this faction may buy another ground
    /// vehicle. One definition, read by the AI commander's ground buy
    /// (<c>Ai/CommanderEnemyCommanderService.cs</c>) and its repair-crew hire
    /// (<c>Units/CommanderRepairService.cs</c>) — the two paths by which a COMMANDER adds a ground
    /// vehicle to the world.
    /// </summary>
    internal static bool GroundBuyAllowed(FactionHQ hq)
    {
        return GroundBuyAllowed(CountLiveGroundVehicles(hq), CommanderSettings.GroundUnitCeiling);
    }

    /// <summary>
    /// The air ceiling as the buyer asks it: true when this faction may buy another aircraft for the
    /// work <paramref name="standingPatrol"/> describes. One definition, read by the AI commander's
    /// air buy (<c>Ai/CommanderEnemyCommanderAirBuy.cs</c>) — the one path by which a COMMANDER adds
    /// an aircraft to the world. The ceiling it reads is the income-scaled one that already existed
    /// (<see cref="EffectiveAirborneCeiling"/>), not a second number: this track retuned that rule's
    /// defaults rather than adding a rival to it.
    /// </summary>
    internal static bool AirBuyAllowed(FactionHQ hq, bool standingPatrol)
    {
        return AirBuyAllowed(
            CommanderEnemyCommanderService.CountAirborne(hq),
            EffectiveAirborneCeiling(hq),
            CommanderSettings.AirPatrolReserve,
            standingPatrol);
    }

    /// <summary>
    /// Sells one vehicle back: removes it from the world and answers what the treasury is owed for
    /// it. The one definition of a sale, called by the idle-reserve sale and the quiet-ground
    /// retirement (Reuse rule 5 — the second instance is what made this an extraction rather than a
    /// copy). Priced by <c>CommanderEconomyService.StrategicUnitValue</c>, the mod's single answer to
    /// "what is this unit worth", so a vehicle sold here and the same vehicle banked into a strategic
    /// war chest can never disagree. Answers false, and owes nothing, when the despawn fails.
    /// <para>Server-only, as every despawn and every refund is; both callers check
    /// <c>hq.IsServer</c> before the walk that reaches this.</para>
    /// </summary>
    private static bool CashInVehicle(Unit? unit, out float refund)
    {
        refund = 0f;
        if (unit == null || unit.disabled)
        {
            return false;
        }

        float price = CommanderEconomyService.StrategicUnitValue(unit);
        if (!CommanderEconomyService.DespawnUnit(unit))
        {
            return false;
        }

        refund = price * Mathf.Clamp01(CommanderSettings.PoolSellRefundFraction);
        return true;
    }

    /// <summary>Scratch for the quiet-ground retirement, reused per review rather than allocated per
    /// point — this runs inside the 30 s review of a service that must not add garbage to a
    /// frame-rate investigation.</summary>
    private readonly List<Unit> quietRetirementScratch = new();

    /// <summary>
    /// Design §2.4: a point the commander HOLDS that nobody has contested for
    /// <see cref="CommanderSettings.QuietGroundMinutes"/> is thinned back to its standing garrison
    /// and the surplus cashed in. The commander re-raises there if the front moves back — the point
    /// stays on the ranked list, so the picket fill and the forward-base planner see it again on the
    /// very next review.
    /// </summary>
    /// <remarks>
    /// Contact is the mod's EXISTING idea of contact and not a second one: <c>DetectMissionContact</c>
    /// and <c>DetectHoldingContact</c> already stamp <c>ContactUntil</c> and <c>InContactUntil</c>
    /// every five seconds from tracked hostiles and from lost members, and this pass reads those
    /// clocks and the <c>QuietSince</c> stamp they refresh.
    /// <para>
    /// Three things it will never do, each with a named self-check: disband anything in contact,
    /// leave a held point with nothing standing on it, or take the last holder off a point. All three
    /// come out of <see cref="HeldPointKeepFloor"/> and <see cref="QuietGroundRetirement"/>; the live
    /// code's only job is to feed them honest numbers and to re-read the contact clocks at the moment
    /// of the decision rather than trusting a stamp from a review ago.
    /// </para>
    /// <para>
    /// Only points are retired from. A platoon in reserve holds no ground, so it is not quiet ground
    /// and is left alone — the loose vehicles behind the line are the idle reserve's business, and
    /// the reserve platoons are what answers a reinforcement request.
    /// </para>
    /// </remarks>
    private void RetireQuietGround(FactionHQ hq, OperationsState state)
    {
        // The despawn and the refund are both server-side; a pure multiplayer client throws.
        if (!hq.IsServer)
        {
            return;
        }

        float now = Time.time;
        int retired = 0;
        float refunded = 0f;
        int quietCandidates = 0;
        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if ((mission.Kind != CommanderMissionKind.Picket && mission.Kind != CommanderMissionKind.ForwardBase)
                || mission.Point == null)
            {
                continue;
            }

            // Absolute rule 1, read live rather than trusted from a stamp: the mission's own contact
            // clock, and every platoon standing on it. Either one restarts the quiet clock.
            if (MissionIsInContact(mission, now))
            {
                mission.QuietSince = now;
                continue;
            }

            // Ground the commander does not hold is not ground it is holding quietly — it is ground
            // it is trying to take, and thinning a detachment there is giving up an attempt nobody
            // asked it to give up.
            bool held = ReferenceEquals(mission.Point.GetOwner(), hq);
            if (!held)
            {
                mission.QuietSince = now;
                continue;
            }

            if (mission.QuietSince < 0f)
            {
                // First review this point has been seen quiet: start its clock rather than treating
                // "never been in contact" as "quiet for ever", which would retire a picket the review
                // it was formed and thrash the commander's money at half price a time.
                mission.QuietSince = now;
                continue;
            }

            int holders = CountHoldersInRing(mission);
            int allowed = QuietGroundRetirement(
                inContact: false,
                now - mission.QuietSince,
                CommanderSettings.QuietGroundMinutes,
                holders,
                HeldPointKeepFloor(pointHeld: true, givenUp: false, CommanderSettings.PointsMinGarrison));
            if (allowed <= 0)
            {
                continue;
            }

            quietCandidates += allowed;
            int taken = RetireFromPoint(hq, state, mission, allowed, ref refunded);
            retired += taken;
            if (taken > 0)
            {
                // Restart the clock so a point is thinned once per timeout rather than once per
                // review, and so the log line below cannot repeat for the same quiet spell.
                mission.QuietSince = now;
                CommanderAiLog.Note(
                    hq,
                    $"cashes in {taken} vehicle(s) at {mission.Label}: nobody has contested it for "
                        + $"{CommanderSettings.QuietGroundMinutes:0} minute(s) "
                        + $"({CountHoldersInRing(mission)} left standing, floor "
                        + $"{HeldPointKeepFloor(true, false, CommanderSettings.PointsMinGarrison)}).");
            }
        }

        if (retired > 0)
        {
            hq.AddFunds(refunded);
        }

        ReportUnitEconomyStep(hq, state, quietCandidates);
    }

    /// <summary>True while this point is in contact by any of the evidence the mod already
    /// keeps: the mission's own clock (a tracked hostile near the point, or a detachment member
    /// lost) or any platoon standing on it.</summary>
    private static bool MissionIsInContact(CommanderOperationsMission mission, float now)
    {
        if (mission.ContactUntil >= now)
        {
            return true;
        }

        for (int i = 0; i < mission.Assigned.Count; i++)
        {
            if (mission.Assigned[i].InContactUntil >= now)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Everything of the commander's actually STANDING IN THE RING at this point: the picket
    /// detachment plus every member of every platoon assigned to it, each tested against the point's
    /// own capture radius.
    /// </summary>
    /// <remarks>
    /// The ring test is what makes <see cref="HeldPointKeepFloor"/> a real guarantee rather than a
    /// hopeful one. Ownership is re-derived from what the game finds INSIDE the ring
    /// (<c>CommanderStrategicPointService.CountPresent</c>), so counting a platoon that is assigned
    /// to the point but still driving to it would let the retirement cash in the one vehicle
    /// genuinely standing there and hand the point over while its relief was still on the road —
    /// exactly the failure the strategic save work hit. Same test, same radius, same
    /// <c>FastMath.InRange</c> call as the ownership walk, so the two can never disagree.
    /// <para>
    /// A forward base's munitions truck is deliberately not counted. It stands in the ring and does
    /// hold the point for the game's purposes, so leaving it out only ever makes this count LOWER
    /// and the retirement more cautious, which is the safe direction to be wrong in.
    /// </para>
    /// </remarks>
    private static int CountHoldersInRing(CommanderOperationsMission mission)
    {
        int holders = 0;
        for (int i = 0; i < mission.PicketMembers.Count; i++)
        {
            if (StandsInRing(mission, mission.PicketMembers[i]))
            {
                holders++;
            }
        }

        for (int p = 0; p < mission.Assigned.Count; p++)
        {
            holders += CountPlatoonInRing(mission, mission.Assigned[p]);
        }

        return holders;
    }

    /// <summary>Live members of <paramref name="platoon"/> standing inside the point's ring.</summary>
    private static int CountPlatoonInRing(CommanderOperationsMission mission, CommanderPlatoon platoon)
    {
        int inRing = 0;
        for (int i = 0; i < platoon.Members.Count; i++)
        {
            if (StandsInRing(mission, platoon.Members[i]))
            {
                inRing++;
            }
        }

        return inRing;
    }

    /// <summary>
    /// True when this live vehicle is inside the point's capture ring. Both positions are read as
    /// <c>GlobalPosition</c> — never a mix of <c>transform.position</c> and a global one, which on a
    /// floating-origin map is a radius test that silently always passes or always fails.
    /// </summary>
    private static bool StandsInRing(CommanderOperationsMission mission, Unit? member)
    {
        return member != null
            && !member.disabled
            && mission.Point != null
            && FastMath.InRange(member.transform.GlobalPosition(), mission.Point.Position, mission.Point.CaptureRadius);
    }

    /// <summary>
    /// Cashes in at most <paramref name="allowed"/> of the vehicles STANDING IN THE RING, detachment
    /// vehicles first and whole platoons after. Detachment first because a picket vehicle is a loose
    /// holder with no formation to break, and because taking it leaves the platoon — the thing that
    /// can actually fight if the front returns — standing. A platoon goes out through the existing
    /// <c>DissolveToPool</c>, so the mission bookkeeping, any reinforcement answer and the ground
    /// posture all clear at the one choke point they already clear at, and only then are its members
    /// sold out of the pool.
    /// <para>
    /// Only vehicles in the ring are ever taken, which is what keeps the floor honest: the count the
    /// floor was worked out from and the vehicles this removes are the same set, so the ring can
    /// never end up below the floor. A platoon still driving to the point is not standing on it and
    /// is left alone — it is not a holder, and taking it would be retiring a relief rather than a
    /// garrison.
    /// </para>
    /// <para>A platoon is taken only if the WHOLE of it has arrived and fits inside what is allowed.
    /// Half a platoon cashed in is a platoon that cannot fight and still costs frame time.</para>
    /// </summary>
    private int RetireFromPoint(
        FactionHQ hq, OperationsState state, CommanderOperationsMission mission, int allowed, ref float refunded)
    {
        int taken = 0;
        for (int i = mission.PicketMembers.Count - 1; i >= 0 && taken < allowed; i--)
        {
            Unit member = mission.PicketMembers[i];
            if (!StandsInRing(mission, member))
            {
                continue;
            }

            // A vehicle the PLAYER has given an order to is theirs now, the same exemption the idle
            // sale makes.
            if (CommanderMoveService.Instance?.HasPlayerOrder(member) == true)
            {
                continue;
            }

            if (!CashInVehicle(member, out float refund))
            {
                continue;
            }

            refunded += refund;
            mission.PicketMembers.RemoveAt(i);
            taken++;
        }

        for (int p = mission.Assigned.Count - 1; p >= 0 && taken < allowed; p--)
        {
            CommanderPlatoon platoon = mission.Assigned[p];
            int live = LivePlatoonMembers(platoon);
            if (!PlatoonRetirable(live, CountPlatoonInRing(mission, platoon), taken, allowed)
                || PlatoonHasPlayerOrder(platoon))
            {
                continue;
            }

            quietRetirementScratch.Clear();
            for (int m = 0; m < platoon.Members.Count; m++)
            {
                quietRetirementScratch.Add(platoon.Members[m]);
            }

            string name = platoon.Name;
            DissolveToPool(state, platoon);
            for (int m = 0; m < quietRetirementScratch.Count; m++)
            {
                Unit member = quietRetirementScratch[m];
                if (!CashInVehicle(member, out float refund))
                {
                    continue;
                }

                refunded += refund;
                state.Pool.Remove(member);
                state.PoolIssued.Remove(member);
                state.PoolIdleSince.Remove(member);
                taken++;
            }

            quietRetirementScratch.Clear();
            CommanderAiLog.Note(hq, $"{name} stood down on quiet ground at {mission.Label}.");
        }

        return taken;
    }

    /// <summary>Live members of a platoon — what it is actually worth to the frame budget, as
    /// against the roster it carries.</summary>
    private static int LivePlatoonMembers(CommanderPlatoon platoon)
    {
        int live = 0;
        for (int i = 0; i < platoon.Members.Count; i++)
        {
            Unit member = platoon.Members[i];
            if (member != null && !member.disabled)
            {
                live++;
            }
        }

        return live;
    }

    /// <summary>True when any member of the platoon is driving under a player order, which makes the
    /// whole platoon the player's business for as long as that order stands.</summary>
    private static bool PlatoonHasPlayerOrder(CommanderPlatoon platoon)
    {
        CommanderMoveService? moves = CommanderMoveService.Instance;
        if (moves == null)
        {
            return false;
        }

        for (int i = 0; i < platoon.Members.Count; i++)
        {
            Unit member = platoon.Members[i];
            if (member != null && !member.disabled && moves.HasPlayerOrder(member))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Says which of design §3's steps this commander is on, once per change rather than once per
    /// review (the <c>ReportInsertionDenial</c> de-duplication convention: a review runs every 30 s
    /// and the same line every time is noise nobody reads).
    /// </summary>
    private static void ReportUnitEconomyStep(FactionHQ hq, OperationsState state, int quietCandidates)
    {
        int live = CountLiveGroundVehicles(hq);
        int ceiling = CommanderSettings.GroundUnitCeiling;
        CommanderUnitEconomyStep step = NextUnitEconomyStep(live, ceiling, state.Pool.Count, quietCandidates);
        if (step == state.UnitEconomyStepReported)
        {
            return;
        }

        state.UnitEconomyStepReported = step;
        switch (step)
        {
            case CommanderUnitEconomyStep.CashInIdle:
                CommanderAiLog.Note(
                    hq, $"is at the ground ceiling ({live}/{ceiling}): cashing in the idle reserve before it buys again.");
                break;
            case CommanderUnitEconomyStep.RetireQuietGround:
                CommanderAiLog.Note(
                    hq, $"is at the ground ceiling ({live}/{ceiling}): giving up the garrisons on quiet ground.");
                break;
            case CommanderUnitEconomyStep.RefuseGrowth:
                CommanderAiLog.Note(
                    hq,
                    $"is at the ground ceiling ({live}/{ceiling}) with nothing idle and no quiet ground: "
                        + "it replaces losses now, and grows no further.");
                break;
            default:
                CommanderAiLog.Note(hq, $"is under the ground ceiling again ({live}/{ceiling}) and may grow.");
                break;
        }
    }

    /// <summary>
    /// The unit economy's thresholds and its three absolute rules, checked at plugin load beside
    /// every other block (design §5.1). Every case is named for the rule it defends, so a failure
    /// line says which rule broke rather than which number moved.
    /// </summary>
    private static void CheckUnitEconomy(List<string> failures)
    {
        // --- The ceiling (design §2.3): replaces losses, never grows. ---
        Expect(failures, "a commander under the ground ceiling may buy", GroundBuyAllowed(79, 80), true);
        Expect(failures, "a commander one vehicle under the ceiling may still buy", GroundBuyAllowed(79, 80), true);
        Expect(failures, "a commander exactly at the ceiling may not buy", GroundBuyAllowed(80, 80), false);
        Expect(failures, "a commander over the ceiling may not buy", GroundBuyAllowed(120, 80), false);
        Expect(failures, "a loss at the ceiling opens exactly one replacement", GroundBuyAllowed(79, 80), true);
        Expect(failures, "a ceiling of zero never binds", GroundBuyAllowed(500, 0), true);
        Expect(failures, "a negative ceiling never binds", GroundBuyAllowed(500, -1), true);
        Expect(failures, "an empty faction may always buy", GroundBuyAllowed(0, 80), true);

        // --- The air ceiling's TWO lines (air-ceiling_20260918 §4 decision C). Everything protected
        // buys up to the ceiling; a standing patrol stops a reserved block short of it. ---
        Expect(failures, "a commander under the air ceiling may buy a protected airframe", AirBuyAllowed(15, 16, 6, false), true);
        Expect(failures, "a commander exactly at the air ceiling may not buy a protected airframe", AirBuyAllowed(16, 16, 6, false), false);
        Expect(failures, "a commander over the air ceiling may not buy a protected airframe", AirBuyAllowed(20, 16, 6, false), false);
        Expect(failures, "a standing patrol stops at the reserved line", AirBuyAllowed(10, 16, 6, true), false);
        Expect(failures, "a standing patrol one below the reserved line may still buy", AirBuyAllowed(9, 16, 6, true), true);
        Expect(failures, "an escort may still buy where a standing patrol may not", AirBuyAllowed(10, 16, 6, false), true);
        Expect(failures, "an air ceiling of zero never binds", AirBuyAllowed(500, 0, 6, true), true);
        Expect(failures, "a reserve of zero puts patrols on the same line as everything else", AirBuyAllowed(15, 16, 0, true), true);
        Expect(failures, "a reserve at the ceiling grounds every standing patrol", AirBuyAllowed(0, 16, 16, true), false);
        Expect(failures, "a reserve bigger than the ceiling still grounds every standing patrol", AirBuyAllowed(0, 16, 20, true), false);
        Expect(failures, "a negative reserve never lets a patrol buy past the ceiling", AirBuyAllowed(16, 16, -5, true), false);
        Expect(failures, "the shipped air defaults leave room for standing patrols", AirCeilingDefaultsCoherent(6, 16, 24), true);
        Expect(failures, "a reserve at the shipped floor would ground every patrol", AirCeilingDefaultsCoherent(16, 16, 24), false);
        Expect(failures, "a maximum below the floor is incoherent", AirCeilingDefaultsCoherent(6, 24, 16), false);

        // --- Absolute rule 2 and 3: a held point is never emptied, and its last holder stands. ---
        Expect(failures, "a held point keeps its garrison standing", HeldPointKeepFloor(true, false, 1), 1);
        Expect(failures, "a held point with a garrison of three keeps three", HeldPointKeepFloor(true, false, 3), 3);
        Expect(
            failures,
            "a held point keeps one holder even if the garrison setting is zeroed",
            HeldPointKeepFloor(true, false, 0),
            1);
        Expect(
            failures,
            "a held point keeps one holder even if the garrison setting goes negative",
            HeldPointKeepFloor(true, false, -5),
            1);
        Expect(failures, "ground the commander does not hold keeps nothing", HeldPointKeepFloor(false, false, 3), 0);
        Expect(
            failures,
            "a point being deliberately given up may be emptied",
            HeldPointKeepFloor(true, true, 3),
            0);

        // --- Absolute rule 1, and the quiet-ground timeout (design §2.4). ---
        Expect(
            failures,
            "a garrison in contact is never disbanded, however long the ground was quiet before",
            QuietGroundRetirement(inContact: true, quietSeconds: 3600f, quietMinutes: 5f, holders: 9, keepFloor: 1),
            0);
        Expect(
            failures,
            "a garrison in contact is never disbanded even with no floor under it",
            QuietGroundRetirement(inContact: true, quietSeconds: 3600f, quietMinutes: 5f, holders: 9, keepFloor: 0),
            0);
        Expect(
            failures,
            "ground quiet for exactly the timeout retires its surplus",
            QuietGroundRetirement(false, quietSeconds: 300f, quietMinutes: 5f, holders: 4, keepFloor: 1),
            3);
        Expect(
            failures,
            "ground one second short of the timeout is left alone",
            QuietGroundRetirement(false, quietSeconds: 299f, quietMinutes: 5f, holders: 4, keepFloor: 1),
            0);
        Expect(
            failures,
            "a quiet-ground timeout of zero never retires anything",
            QuietGroundRetirement(false, quietSeconds: 3600f, quietMinutes: 0f, holders: 9, keepFloor: 1),
            0);
        Expect(
            failures,
            "quiet ground holding exactly its floor retires nothing",
            QuietGroundRetirement(false, quietSeconds: 3600f, quietMinutes: 5f, holders: 1, keepFloor: 1),
            0);
        Expect(
            failures,
            "the last holder of a quiet held point is never cashed in",
            QuietGroundRetirement(false, quietSeconds: 86400f, quietMinutes: 5f, holders: 1, keepFloor: 1),
            0);
        Expect(
            failures,
            "a quiet held point is never emptied, however long it stays quiet",
            QuietGroundRetirement(false, quietSeconds: 86400f, quietMinutes: 5f, holders: 12, keepFloor: 1) < 12,
            true);
        Expect(
            failures,
            "an abandoned point may be emptied down to nothing",
            QuietGroundRetirement(false, quietSeconds: 600f, quietMinutes: 5f, holders: 3, keepFloor: 0),
            3);

        // --- Which whole platoons may come off a quiet point. ---
        Expect(
            failures,
            "a platoon wholly standing on quiet ground may be cashed in",
            PlatoonRetirable(liveMembers: 6, membersInRing: 6, alreadyTaken: 0, allowed: 6),
            true);
        Expect(
            failures,
            "a platoon still driving to the point is never cashed in",
            PlatoonRetirable(liveMembers: 6, membersInRing: 5, alreadyTaken: 0, allowed: 6),
            false);
        Expect(
            failures,
            "a platoon with none of it arrived is never cashed in",
            PlatoonRetirable(liveMembers: 6, membersInRing: 0, alreadyTaken: 0, allowed: 6),
            false);
        Expect(
            failures,
            "a platoon larger than what the floor allows is left whole",
            PlatoonRetirable(liveMembers: 6, membersInRing: 6, alreadyTaken: 0, allowed: 5),
            false);
        Expect(
            failures,
            "a platoon that no longer fits after the detachment went is left whole",
            PlatoonRetirable(liveMembers: 6, membersInRing: 6, alreadyTaken: 3, allowed: 8),
            false);
        Expect(
            failures,
            "an empty platoon is the sweep's business, not the sale's",
            PlatoonRetirable(liveMembers: 0, membersInRing: 0, alreadyTaken: 0, allowed: 6),
            false);

        // --- Design §3's order of application when a commander is over the ceiling. ---
        Expect(
            failures,
            "a commander under the ceiling is applying none of the reductions",
            NextUnitEconomyStep(liveGroundVehicles: 40, ceiling: 80, idleCandidates: 9, quietCandidates: 9),
            CommanderUnitEconomyStep.None);
        Expect(
            failures,
            "over the ceiling the idle reserve is cashed in first",
            NextUnitEconomyStep(80, 80, idleCandidates: 9, quietCandidates: 9),
            CommanderUnitEconomyStep.CashInIdle);
        Expect(
            failures,
            "over the ceiling with nothing idle, quiet ground is next",
            NextUnitEconomyStep(80, 80, idleCandidates: 0, quietCandidates: 9),
            CommanderUnitEconomyStep.RetireQuietGround);
        Expect(
            failures,
            "over the ceiling with nothing idle and no quiet ground, the commander refuses to grow",
            NextUnitEconomyStep(80, 80, idleCandidates: 0, quietCandidates: 0),
            CommanderUnitEconomyStep.RefuseGrowth);
        Expect(
            failures,
            "refusing to grow is the last resort, never the first",
            NextUnitEconomyStep(200, 80, idleCandidates: 1, quietCandidates: 0),
            CommanderUnitEconomyStep.CashInIdle);

        // --- The idle reserve (design §2.1), on the existing sale's own predicate. ---
        Expect(failures, "a vehicle idle for exactly the timeout is cashed in", PoolUnitSellable(180f, 3f), true);
        Expect(failures, "a vehicle idle for less than the timeout is kept", PoolUnitSellable(179f, 3f), false);
        Expect(failures, "an idle-reserve timeout of zero never sells", PoolUnitSellable(3600f, 0f), false);
        Expect(failures, "a vehicle that has just arrived is never sold", PoolUnitSellable(0f, 3f), false);

        // --- The garrison setting is one number doing two jobs (design §2.2). A garrison below the
        // ownership threshold would station too few vehicles to hold the ground it was sent to, so
        // the fill rule and the ownership rule must read the same setting. ---
        Expect(
            failures,
            "the garrison standing on a point is enough to qualify to own it",
            CommanderStrategicPointService.QualifyingFaction(
                new[] { HeldPointKeepFloor(true, false, CommanderSettings.PointsMinGarrison), 0, 0 },
                CommanderSettings.PointsMinGarrison,
                out _),
            0);
        Expect(
            failures,
            "a point left with nothing standing on it qualifies for nobody",
            CommanderStrategicPointService.QualifyingFaction(new[] { 0, 0, 0 }, 1, out _),
            -1);
    }

    /// <summary>The step table's own <c>Expect</c> overload, beside the four the operations
    /// self-check already carries: a check that had to stringify the step would read worse than the
    /// rule it is checking (the <c>CommanderPicketDelivery</c> overload's reason).</summary>
    private static void Expect(
        List<string> failures, string name, CommanderUnitEconomyStep actual, CommanderUnitEconomyStep expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }
}
