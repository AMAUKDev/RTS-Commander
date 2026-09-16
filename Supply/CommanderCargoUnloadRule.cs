using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>
/// The delivery bypass's decision table (design, delivery-bypass_20260916; user decision 2026-09-16:
/// bypass EVERY delivery, not only the ones that stall). When a cargo transport stops trying to land
/// and unloads where it is, how far apart its vehicles are set down, and when its order is closed.
/// <para>
/// Why the bypass exists. The game releases cargo only when radar altitude is under 2 m AND speed is
/// under 10 m/s — in plain words, the aircraft must be physically on the ground and stopped — while
/// inside 300 m of its touchdown point it switches flight assist off and commands a hover at 20 m. A
/// transport that holds that hover never satisfies the gate, which is the "it cannot land, units
/// aren't dropped, stuck" the developer reported, and the tiltwing that keeps trying is the airframe
/// that flies into the ground (survey, 2026-09-16: the VL-49 Tarantula ends 29 percent of its losses
/// in the dirt against the UH-90 Ibis's 9 percent).
/// </para>
/// <para>
/// What the bypass does NOT fix, recorded here so nobody re-litigates it: of 80 measured cargo
/// flights, 18 were shot down in transit and 22 were recalled by our own route watch. Both buckets
/// are larger than the 9 that hovered, and both are separate work (design section 6).
/// </para>
/// <para>
/// This class has NO Unity, Harmony or game-assembly dependency on purpose, for the same reason
/// <see cref="CommanderCargoFlightSlot"/> has none: it is the piece of the bypass that can be driven
/// outside a running game, and the supply service's own type initializer cannot be run in a harness.
/// The runtime half — measuring the aircraft, choosing the ground, firing the mounts — is in
/// <c>Supply/CommanderSupplyHeliUnload.cs</c>.
/// </para>
/// </summary>
internal static class CommanderCargoUnloadRule
{
    /// <summary>
    /// Radar altitude at or below which a transport unloads in place: 25 m. It MUST clear the 20 m
    /// auto-hover the game commands once inside 300 m of its touchdown point, because a lower
    /// ceiling would almost never be met and every delivery would fall through to the bounded wait
    /// below — which is the hang this whole track exists to remove. 25 m is that 20 m plus 5 m of
    /// margin for the hover's own overshoot, and low enough that the insertion shield
    /// (Supply/CommanderSupplyHeliShield.cs) covers whatever fall is left.
    /// </summary>
    internal const float UnloadHeightMeters = 25f;

    /// <summary>
    /// Speed below which a transport counts as roughly still for an unload: 10 m/s. This is the
    /// game's OWN release-gate speed, kept deliberately unchanged — it is the only number the engine
    /// itself treats as "stopped", so the bypass alters exactly one of the two gate numbers, the 2 m
    /// height. Strict, like the game's test: a transport exactly on 10 m/s does not unload.
    /// </summary>
    internal const float UnloadSpeedMetersPerSecond = 10f;

    /// <summary>
    /// Radar altitude ceiling once the EXISTING stall clock has expired: 40 m (design section 4.2).
    /// Higher than <see cref="UnloadHeightMeters"/> so a transport that could not get down still
    /// delivers rather than hanging, and still inside what the insertion shield covers. Above it the
    /// flight falls through to the parachute path exactly as it does today, so the bounded wait can
    /// never become a new way to wait for ever.
    /// </summary>
    internal const float ForcedUnloadHeightMeters = 40f;

    /// <summary>
    /// How far apart consecutive vehicles of one load are set down: 30 m. The game's ramp-clear
    /// handshake — the thing that stops one vehicle being dropped onto another — is the one part of
    /// the delivery the bypass genuinely breaks, so the mod owns the spacing instead. 30 m is wider
    /// than the 20 m half-width of the clear ground a transport is given to set down in
    /// (<c>OperationsLzClearRadiusMeters</c>, 40 m across), so two vehicles of one load never want
    /// the same ground and the clear-ground search walks outward from each spot independently.
    /// </summary>
    internal const float UnloadVehicleSpacingMeters = 30f;

    /// <summary>
    /// How long a latched unload may go without putting another vehicle out before the flight stops
    /// counting as "still delivering": 30 s. It exists because the bounded wait hands the stall clock
    /// an answer, and an answer of "yes, it is unloading" resets that clock - so without a limit a
    /// flight that latched and then stopped would reset the clock for ever, which is precisely the
    /// hang this whole track removes, reintroduced one layer up. A healthy unload puts a vehicle out
    /// every 2.5 s plus the second or two it takes to leave the aircraft, so 30 s is roughly ten
    /// times the real gap: long enough that a slow ramp is never mistaken for a stuck one, short
    /// enough that a genuinely stuck flight is rescued by the parachute drop or the recall on the
    /// clock's very next expiry.
    /// </summary>
    internal const float UnloadProgressGraceSeconds = 30f;

    /// <summary>How close two measured distances count as the same, in metres: 1 mm. Only the
    /// self-check uses it, to compare the spacing offsets without depending on Unity's
    /// <c>Mathf.Approximately</c>.</summary>
    private const float SameDistanceMeters = 0.001f;

    /// <summary>
    /// The unload condition (design section 4.1): the transport is inside the landing zone's ring,
    /// at or below the height ceiling and below the speed limit. All three, or nothing happens.
    /// <para>The ring and the ceiling are passed in rather than read here because the bounded wait
    /// asks the same question with a higher ceiling — one rule, two callers, so the normal path and
    /// the forced path can never disagree about what "low and slow at the landing zone" means.</para>
    /// </summary>
    internal static bool UnloadsInPlace(
        float metresFromLandingZone,
        float radarAltMeters,
        float speed,
        float ringMeters,
        float heightCeilingMeters,
        float speedLimit)
    {
        return metresFromLandingZone <= ringMeters
            && radarAltMeters <= heightCeilingMeters
            && speed < speedLimit;
    }

    /// <summary>
    /// Where the <paramref name="releaseIndex"/>th vehicle of one load is set down relative to the
    /// aircraft: the first goes on the ground under the aircraft, and the rest go one spacing out on
    /// the four cardinal bearings, wrapping so a load of any size is answered. East and north are
    /// metres, and the clear-ground search then walks outward from each of them independently.
    /// </summary>
    internal static void UnloadOffsetMeters(
        int releaseIndex,
        float spacingMeters,
        out float east,
        out float north)
    {
        if (releaseIndex <= 0)
        {
            east = 0f;
            north = 0f;
            return;
        }

        switch ((releaseIndex - 1) % 4)
        {
            case 0:
                east = spacingMeters;
                north = 0f;
                return;
            case 1:
                east = 0f;
                north = spacingMeters;
                return;
            case 2:
                east = -spacingMeters;
                north = 0f;
                return;
            default:
                east = 0f;
                north = -spacingMeters;
                return;
        }
    }

    /// <summary>
    /// Whether a flight that has already begun unloading in place is still getting on with it:
    /// something happened within the grace. <paramref name="lastProgressAt"/> is the later of the
    /// moment it latched and the moment it last put a vehicle out, so a flight that has only just
    /// started counts as progressing before its first release.
    /// <para>This is the answer the bounded wait gives the stall clock about a flight it has already
    /// started. Saying yes resets that clock, so saying yes for ever would be a new way to hover for
    /// ever; saying no hands the flight back to the parachute drop and then the recall, which is
    /// what every other stuck flight gets.</para>
    /// </summary>
    internal static bool UnloadIsProgressing(float now, float lastProgressAt, float graceSeconds)
    {
        return now - lastProgressAt <= graceSeconds;
    }

    /// <summary>
    /// Whether a cargo flight has finished delivering: every expected load has activated, the
    /// aircraft is empty and no ramp clearance is still running. The rule
    /// <c>UpdateDeliveryCompleted</c> has always applied, lifted out here so the bypass — which runs
    /// no ramp clearance at all — can be checked against it at load. One definition, two callers.
    /// </summary>
    internal static bool DeliveryComplete(
        int activatedCargoCount,
        int expectedCargoLoads,
        bool cargoStillAboard,
        bool clearancePending)
    {
        return activatedCargoCount >= expectedCargoLoads && !cargoStillAboard && !clearancePending;
    }

    /// <summary>
    /// Every boundary of the rules above, by name, with the failures returned rather than logged so
    /// the same list can be produced inside the game at plugin load and outside it in a harness.
    /// Empty means all of them held.
    /// </summary>
    internal static List<string> CollectFailures()
    {
        List<string> failures = new();
        const float ring = CommanderOperationsService.InsertionStallRadiusMeters;

        Expect(
            failures,
            "a transport in the ring, low and slow unloads",
            UnloadsInPlace(100f, 20f, 1f, ring, UnloadHeightMeters, UnloadSpeedMetersPerSecond),
            true);
        Expect(
            failures,
            "exactly on the height ceiling still unloads",
            UnloadsInPlace(100f, UnloadHeightMeters, 1f, ring, UnloadHeightMeters, UnloadSpeedMetersPerSecond),
            true);
        Expect(
            failures,
            "one metre above the height ceiling does not unload",
            UnloadsInPlace(100f, UnloadHeightMeters + 1f, 1f, ring, UnloadHeightMeters, UnloadSpeedMetersPerSecond),
            false);
        // The game's own gate is a strict "speed < 10", and this rule keeps it strict.
        Expect(
            failures,
            "exactly on the speed limit does not unload",
            UnloadsInPlace(100f, 10f, UnloadSpeedMetersPerSecond, ring, UnloadHeightMeters, UnloadSpeedMetersPerSecond),
            false);
        Expect(
            failures,
            "just inside the speed limit unloads",
            UnloadsInPlace(100f, 10f, UnloadSpeedMetersPerSecond - 0.1f, ring, UnloadHeightMeters, UnloadSpeedMetersPerSecond),
            true);
        Expect(
            failures,
            "exactly on the ring still unloads",
            UnloadsInPlace(ring, 10f, 1f, ring, UnloadHeightMeters, UnloadSpeedMetersPerSecond),
            true);
        Expect(
            failures,
            "one metre outside the ring does not unload",
            UnloadsInPlace(ring + 1f, 10f, 1f, ring, UnloadHeightMeters, UnloadSpeedMetersPerSecond),
            false);
        // The bounded wait: the same rule with the higher ceiling, so a transport that could not get
        // down to 25 m still delivers at 40 rather than hanging over its post.
        Expect(
            failures,
            "the forced ceiling admits a height the normal ceiling refuses",
            UnloadsInPlace(100f, UnloadHeightMeters + 1f, 1f, ring, ForcedUnloadHeightMeters, UnloadSpeedMetersPerSecond),
            true);
        // And the parachute path survives: too high for a direct placement is still refused, which is
        // what makes the fallback reachable.
        Expect(
            failures,
            "the forced ceiling still refuses a transport above it",
            UnloadsInPlace(100f, ForcedUnloadHeightMeters + 1f, 1f, ring, ForcedUnloadHeightMeters, UnloadSpeedMetersPerSecond),
            false);
        Expect(
            failures,
            "the forced ceiling is above the normal ceiling",
            ForcedUnloadHeightMeters > UnloadHeightMeters,
            true);
        // The height ceiling must clear the 20 m hover the game commands, or the normal path never
        // fires and every delivery waits out the stall clock — the hang this track removes.
        Expect(
            failures,
            "the height ceiling clears the game's 20 m hover",
            UnloadHeightMeters > 20f,
            true);

        UnloadOffsetMeters(0, UnloadVehicleSpacingMeters, out float firstEast, out float firstNorth);
        Expect(
            failures,
            "the first vehicle is placed under the aircraft",
            firstEast == 0f && firstNorth == 0f,
            true);
        UnloadOffsetMeters(1, UnloadVehicleSpacingMeters, out float secondEast, out float secondNorth);
        Expect(
            failures,
            "the second vehicle is a full spacing away",
            SameDistance(Distance(0f, 0f, secondEast, secondNorth), UnloadVehicleSpacingMeters),
            true);
        UnloadOffsetMeters(3, UnloadVehicleSpacingMeters, out float fourthEast, out float fourthNorth);
        Expect(
            failures,
            "opposite vehicles are two spacings apart",
            SameDistance(
                Distance(secondEast, secondNorth, fourthEast, fourthNorth),
                UnloadVehicleSpacingMeters * 2f),
            true);
        UnloadOffsetMeters(5, UnloadVehicleSpacingMeters, out float fifthEast, out float fifthNorth);
        Expect(
            failures,
            "a fifth vehicle wraps onto the first bearing",
            SameDistance(Distance(secondEast, secondNorth, fifthEast, fifthNorth), 0f),
            true);

        // The bounded wait's answer about a flight it has already started. It must be able to say no,
        // or the stall clock is reset for ever and the hang comes back one layer up.
        Expect(
            failures,
            "an unload that has just latched is progressing",
            UnloadIsProgressing(100f, 100f, UnloadProgressGraceSeconds),
            true);
        Expect(
            failures,
            "an unload that released a vehicle within the grace is progressing",
            UnloadIsProgressing(100f, 100f - UnloadProgressGraceSeconds + 1f, UnloadProgressGraceSeconds),
            true);
        Expect(
            failures,
            "exactly on the grace is still progressing",
            UnloadIsProgressing(100f, 100f - UnloadProgressGraceSeconds, UnloadProgressGraceSeconds),
            true);
        Expect(
            failures,
            "an unload silent for longer than the grace is not progressing",
            UnloadIsProgressing(100f, 100f - UnloadProgressGraceSeconds - 1f, UnloadProgressGraceSeconds),
            false);
        // The grace has to cover a healthy release cadence, or a normal two-vehicle unload would be
        // called stuck and converted to a parachute drop halfway through.
        Expect(
            failures,
            "the grace covers a healthy release cadence",
            UnloadProgressGraceSeconds > 2.5f * 4f,
            true);

        Expect(failures, "a full load with an empty aircraft is delivered", DeliveryComplete(2, 2, false, false), true);
        Expect(failures, "a load short of expected is not delivered", DeliveryComplete(1, 2, false, false), false);
        Expect(failures, "cargo still aboard is not delivered", DeliveryComplete(2, 2, true, false), false);
        Expect(failures, "a pending ramp clearance is not delivered", DeliveryComplete(2, 2, false, true), false);
        // The bypass runs no ramp clearance at all, so this is the shape a bypassed load closes in.
        Expect(
            failures,
            "a bypassed load with no ramp clearance still completes",
            DeliveryComplete(2, 2, false, false),
            true);

        return failures;
    }

    private static float Distance(float fromEast, float fromNorth, float toEast, float toNorth)
    {
        float east = toEast - fromEast;
        float north = toNorth - fromNorth;
        return (float)System.Math.Sqrt((east * east) + (north * north));
    }

    private static bool SameDistance(float left, float right)
    {
        return System.Math.Abs(left - right) <= SameDistanceMeters;
    }

    private static void Expect(List<string> failures, string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }
}
