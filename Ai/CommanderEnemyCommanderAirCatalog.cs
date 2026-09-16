using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The air catalogue, its prices and the launch base choice. Split out of <c>CommanderEnemyCommanderAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past three thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
    private void RefreshAirCatalog()
    {
        if (airCatalogResolved)
        {
            return;
        }

        CommanderAirCommandService.CollectAircraftDefinitions(airCatalog);
        airCatalogResolved = true;
    }

    /// <summary>
    /// The live ceiling for this commander's air fund: the dearest airframe any open demand wants,
    /// floored at the dearest fighter its strips accept (see <see cref="AirFundCeiling(float, float)"/>).
    /// Walks the same catalog, the same role gate and the same accepting-strip pair the buy itself
    /// walks, so the fund can never save for an airframe the buy would refuse (Reuse rule 4).
    /// </summary>
    private float AirFundCeiling(FactionHQ hq, CommanderState state)
    {
        ReadAirDemandPrices(hq, state, out float cheapestWanted, out float dearestWanted, out float dearestFighter);
        CommanderOperationsService.ReadAirDemandCounts(hq, out int capBound, out int capWanted, out int casBound, out int casWanted);
        int shortfall = Mathf.Max(0, capWanted - capBound) + Mathf.Max(0, casWanted - casBound);
        int room = CommanderOperationsService.EffectiveAirborneCeiling(hq) - CountAirborne(hq);
        return AirFundCeiling(dearestWanted, dearestFighter, shortfall, room, cheapestWanted);
    }

    /// <summary>
    /// The price range of the airframes the wing's OPEN demands actually want, in one walk:
    /// <paramref name="cheapestWanted"/> is the least any open demand would have to pay (the
    /// buy loop's continuation test), <paramref name="dearestWanted"/> the most (the fund's
    /// ceiling), and <paramref name="dearestFighter"/> the ceiling's floor — the wing must always
    /// be able to replace the home patrol. Reuse rule 5: the ceiling's own role walk was the first
    /// instance and was generalised rather than forked, so the saving rule and the spending rule can
    /// never disagree about which airframes count.
    /// <para><paramref name="cheapestWanted"/> is <c>float.MaxValue</c> when nothing open can be
    /// launched from a strip this commander holds, which is exactly "there is nothing to buy".</para>
    /// </summary>
    private void ReadAirDemandPrices(
        FactionHQ hq, CommanderState state, out float cheapestWanted, out float dearestWanted, out float dearestFighter)
    {
        RefreshAirCatalog();
        CommanderOperationsService.ReadOpenAirDemandRoles(
            hq, out bool wantsCap, out bool wantsCas, out bool wantsRotaryCas, out bool wantsAwacs, out bool wantsArad);

        // The fighter price is both the CAP demand's answer and the floor, so it is read once.
        dearestFighter = DearestLaunchableValue(hq, state, AirRole.Fighter);
        dearestWanted = 0f;
        cheapestWanted = float.MaxValue;
        if (wantsCap)
        {
            dearestWanted = Mathf.Max(dearestWanted, dearestFighter);
            cheapestWanted = Mathf.Min(cheapestWanted, CheapestLaunchableValue(hq, state, AirRole.Fighter));
        }

        if (wantsCas)
        {
            dearestWanted = Mathf.Max(dearestWanted, DearestLaunchableValue(hq, state, AirRole.Strike));
            cheapestWanted = Mathf.Min(cheapestWanted, CheapestLaunchableValue(hq, state, AirRole.Strike));
        }

        if (wantsRotaryCas)
        {
            dearestWanted = Mathf.Max(dearestWanted, DearestLaunchableValue(hq, state, AirRole.RotaryCas));
            cheapestWanted = Mathf.Min(cheapestWanted, CheapestLaunchableValue(hq, state, AirRole.RotaryCas));
        }

        if (wantsAwacs)
        {
            dearestWanted = Mathf.Max(dearestWanted, DearestLaunchableValue(hq, state, AirRole.Awacs));
            cheapestWanted = Mathf.Min(cheapestWanted, CheapestLaunchableValue(hq, state, AirRole.Awacs));
        }

        if (wantsArad)
        {
            dearestWanted = Mathf.Max(dearestWanted, DearestLaunchableValue(hq, state, AirRole.Arad));
            cheapestWanted = Mathf.Min(cheapestWanted, CheapestLaunchableValue(hq, state, AirRole.Arad));
        }
    }

    /// <summary>
    /// Whether the wing may shop again this review, live: its whole money — the fund plus the radar
    /// airframe's own savings, since a radar demand spends both — against the cheapest airframe any
    /// open demand wants, under the airborne ceiling. The live wrapper around the pure rule
    /// (<see cref="AirBuyContinues(int, float, float, int, int)"/>).
    /// </summary>
    private bool AirBuyContinues(FactionHQ hq, CommanderState state, int boughtThisReview)
    {
        ReadAirDemandPrices(hq, state, out float cheapestWanted, out _, out _);
        return AirBuyContinues(
            boughtThisReview,
            state.AirFund + state.AwacsSavings,
            cheapestWanted,
            CountAirborne(hq),
            CommanderOperationsService.EffectiveAirborneCeiling(hq));
    }

    /// <summary>The dearest airframe that can fill <paramref name="role"/> from a strip this
    /// commander holds, or zero when none can. The last-resort airframe is excluded for the same
    /// reason the buy hides it: the commander never saves toward a Cricket.</summary>
    private float DearestLaunchableValue(FactionHQ hq, CommanderState state, AirRole role)
    {
        return LaunchableValue(hq, state, role, dearest: true);
    }

    /// <summary>
    /// The CHEAPEST airframe that can fill <paramref name="role"/> from a strip this commander
    /// holds, or <c>float.MaxValue</c> when none can — the price the turn order asks about when it
    /// wants to know whether a demand is affordable at all this review. Same catalog, same role
    /// gate, same accepting-strip pair as the buy and as the fund ceiling (Reuse rule 5: the
    /// dearest read was the first instance and was generalised rather than forked, so the two can
    /// never disagree about which airframes count).
    /// </summary>
    private float CheapestLaunchableValue(FactionHQ hq, CommanderState state, AirRole role)
    {
        return LaunchableValue(hq, state, role, dearest: false);
    }

    /// <summary>
    /// A <see cref="CheapestLaunchableValue"/> answer turned into a savings target: the price when
    /// one exists, zero when nothing in the role can launch. A commander whose strips accept no
    /// radar airframe must bank nothing toward one, and <c>float.MaxValue</c> as a ceiling would
    /// bank everything forever.
    /// </summary>
    internal static float AffordablePrice(float cheapest)
    {
        return cheapest > 0f && cheapest < float.MaxValue ? cheapest : 0f;
    }

    /// <param name="dearest">Which end of the price range is wanted. The "nothing qualifies" answer
    /// differs with it on purpose: zero for the dearest, because a commander saves toward nothing,
    /// and <c>float.MaxValue</c> for the cheapest, because an unlaunchable role is never
    /// affordable.</param>
    private float LaunchableValue(FactionHQ hq, CommanderState state, AirRole role, bool dearest)
    {
        float best = dearest ? 0f : float.MaxValue;
        foreach (AircraftDefinition definition in airCatalog)
        {
            if (definition == null
                // Cheap early-out before the capability gate and the strip walk, as it always was.
                || (dearest ? definition.value <= best : definition.value >= best)
                || IsLastResortAirframe(definition)
                || !PassesRoleCapability(hq, state, definition, role)
                || FindAcceptingAirbase(hq, definition) == null)
            {
                continue;
            }

            best = definition.value;
        }

        return best;
    }

    /// <summary>
    /// How far the nearest pad or strip that would launch an attack helicopter sits from
    /// <paramref name="objective"/>, IGNORING the range gate — <c>float.MaxValue</c> when the
    /// roster has no rotary CAS airframe any held base accepts. The number the fallback line quotes
    /// (fix, 2026-09-14): "no attack helicopter can launch within 90 km" is unreadable without it,
    /// because a reader cannot tell a pad 5 km outside the gate from a roster with no attack
    /// helicopter at all. Measured over the same capability and acceptance pair the buy itself uses
    /// (Reuse rule 4, one definition), so the two can never disagree about which bases count.
    /// </summary>
    private float NearestRotaryLaunchMeters(FactionHQ hq, CommanderState state, GlobalPosition objective)
    {
        RefreshAirCatalog();
        float nearest = float.MaxValue;
        foreach (AircraftDefinition definition in airCatalog)
        {
            if (definition == null || !PassesRoleCapability(hq, state, definition, AirRole.RotaryCas))
            {
                continue;
            }

            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (airbase == null
                    || airbase.disabled
                    || airbase.center == null
                    || !CommanderAirCommandService.IsCompatibleAirbase(airbase, hq, definition)
                    || !airbase.CanSpawnAircraft(definition))
                {
                    continue;
                }

                float distance = CommanderGameAccess.HorizontalDistance(
                    airbase.center.GlobalPosition().AsVector3(), objective.AsVector3());
                if (distance < nearest)
                {
                    nearest = distance;
                }
            }
        }

        return nearest;
    }

    /// <summary>
    /// Whether one of the commander's held airbases will launch <paramref name="definition"/> — the
    /// AIR window's own pair (<see cref="CommanderAirCommandService.IsCompatibleAirbase"/> plus a
    /// free compatible hangar), so the commander's candidate gate and the window's launch gate can
    /// never disagree about what a strip accepts. Returns the first accepting base, or null.
    /// </summary>
    /// <param name="near">When given with <paramref name="withinMeters"/>, only bases inside that
    /// range of this position are considered, and the nearest one wins — the rotary CAS gate
    /// (design.md, smarter-air-wing_20260914 Section 2: an attack helicopter launches from a pad or
    /// strip within <c>HeliCasRangeMeters</c> of the objective).</param>
    private static Airbase? FindAcceptingAirbase(
        FactionHQ hq, AircraftDefinition definition, GlobalPosition? near = null, float withinMeters = 0f)
    {
        Airbase? best = null;
        float bestScore = float.MaxValue;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (airbase == null
                || airbase.disabled
                || airbase.center == null
                || !CommanderAirCommandService.IsCompatibleAirbase(airbase, hq, definition)
                || !airbase.CanSpawnAircraft(definition))
            {
                continue;
            }

            // The nearest accepting base to the objective wins (user decision 2026-09-14: a package
            // is ordered "from the same location", and the location worth pinning is the one closest
            // to the objective) — but every aircraft already sitting on that base's deck counts as
            // LaunchQueuePenaltyMeters of extra distance (user report 2026-09-14: "nearly all air
            // missions are being spawned from the closest airbase - this results in a huge taxi-ing
            // queue"). A buy with no objective scores on the queue alone. The rotary pass's range
            // limit is still a limit on the real distance, never on the score.
            float distance = near == null
                ? 0f
                : CommanderGameAccess.HorizontalDistance(
                    airbase.center.GlobalPosition().AsVector3(), near.Value.AsVector3());
            if (near != null && withinMeters > 0f && distance > withinMeters)
            {
                continue;
            }

            float score = LaunchBaseScore(distance, CountOnDeckNear(hq, airbase), LaunchQueuePenaltyMeters);
            if (score < bestScore)
            {
                best = airbase;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    /// How far a base is pushed down the launch order for each aircraft already on its deck: 15 km,
    /// so one queued aircraft only loses a base to another that is closer by that much, and a
    /// queue of three sends the launch a whole map away rather than into the taxi line. Retune
    /// down if launches spread too eagerly, up if queues persist.
    /// </summary>
    internal const float LaunchQueuePenaltyMeters = 15_000f;

    /// <summary>Aircraft closer than this to a base's centre and on the deck count as its launch
    /// queue — the base ring plus the taxiways.</summary>
    private const float LaunchQueueRadiusMeters = 2_500f;

    /// <summary>The launch-base ranking, pure: distance to the objective plus one penalty per
    /// aircraft already waiting on the deck. Lower launches first.</summary>
    internal static float LaunchBaseScore(float distanceMeters, int onDeck, float penaltyMeters)
    {
        return Mathf.Max(0f, distanceMeters) + Mathf.Max(0, onDeck) * Mathf.Max(0f, penaltyMeters);
    }

    /// <summary>This faction's aircraft on the deck within <see cref="LaunchQueueRadiusMeters"/> of a
    /// base: the taxi queue the next launch would join. The on-deck test is the recovery's own
    /// (<see cref="CommanderAirCommandService.IsOnDeck"/>), so "waiting" means one thing everywhere.</summary>
    internal static int CountOnDeckNear(FactionHQ hq, Airbase airbase)
    {
        if (hq.factionUnits == null || airbase.center == null)
        {
            return 0;
        }

        // Both sides of the distance in the same space (fix, 2026-09-15). The base centre was read
        // as a GLOBAL position while the aircraft was read as its Unity transform, which sits in the
        // game's shifting floating-origin space; the two differ by the origin offset, so the count
        // was zero on every one of the 1,349 launches in the overnight log while the same base
        // refunded 167 aircraft stuck on its deck. The map-edge rule and the launch spread both read
        // this number and neither ever fired.
        Vector3 center = airbase.center.position;
        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit)
                && unit is Aircraft aircraft
                && !aircraft.disabled
                && CommanderAirCommandService.IsOnDeck(aircraft)
                && CommanderGameAccess.HorizontalDistance(aircraft.transform.position, center) <= LaunchQueueRadiusMeters)
            {
                count++;
            }
        }

        return count;
    }
}
