using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Keeping commanded ground vehicles off runways and taxiways (user instruction, 2026-09-16:
/// "ground vehicles shouldnt be driving on runways or taxi-ways").
/// </summary>
/// <remarks>
/// <para>
/// The mod does not control pathfinding, so a vehicle crossing a strip on its way somewhere is not
/// something it can prevent. What it does own is where vehicles are told to STAND: ring posts,
/// defence arcs, rally and form-up points, truck parks and depot staging blocks. Every one of those
/// now goes through <see cref="OffAirfieldStandingPoint"/>, which forwards to the build preview's
/// own airfield test (<c>CommanderBuildPreview.IsOnAirfieldSurface</c>) so there is still exactly
/// one such test in the mod.
/// </para>
/// <para>
/// The second half is the vehicle that is already standing on the tarmac — it rolled out of a depot
/// built on the apron, or it drove there before this rule existed. The movement tick sweeps those
/// off once each, because the hold-post drives will not: they skip any member already within
/// <c>HoldArrivedMeters</c> of its post, which is exactly the vehicle parked on the strip beside a
/// clean post.
/// </para>
/// </remarks>
internal sealed partial class CommanderOperationsService
{
    /// <summary>Vehicles already sent off an airfield surface, so the sweep issues one order and
    /// writes one line per episode rather than one every movement tick. A vehicle drops out of the
    /// set as soon as it is clear, so a second trip onto the tarmac is swept again.</summary>
    private readonly HashSet<Unit> airfieldClearedUnits = new();

    /// <summary>Scratch list for the sweep, so it allocates nothing per tick.</summary>
    private readonly List<Unit> airfieldSweep = new();

    /// <summary>Rings whose off-airfield move has been logged — the ring's name and its centre — so
    /// the line is written once per ring rather than every review. Moved here out of
    /// <c>CommanderOperationsFront.BuildHoldRing</c> when the point ring and the defence arc needed
    /// the same rule (Reuse rule 5).</summary>
    private static readonly HashSet<string> ringMovedLogged = new();

    /// <summary>
    /// The point a standing position is pushed directly away from when it lands on a runway or a
    /// taxiway, pure.
    /// </summary>
    /// <remarks>
    /// Normally that is the airfield's own centre, which sends the position outward off the field.
    /// The case this exists for is a position that IS the centre — a withdrawing platoon's rally
    /// point is literally <c>airbase.center</c> — where there is no outward direction to read at
    /// all and <c>CommanderBuildPreview.OffAirfieldPost</c> gives up and returns the post unmoved.
    /// Then the push runs toward whoever is coming to stand there, one step back along that line, so
    /// the vehicles stop short of the field instead of driving across it; with nowhere to come from
    /// either, it runs due north so the answer is at least the same every time it is asked.
    /// </remarks>
    internal static GlobalPosition AirfieldPushOrigin(
        GlobalPosition post, GlobalPosition airfieldCentre, GlobalPosition approachFrom, float stepMeters)
    {
        if (HorizontalSpan(airfieldCentre, post) >= 1f)
        {
            return airfieldCentre;
        }

        float step = Mathf.Max(1f, stepMeters);
        float dx = approachFrom.x - post.x;
        float dz = approachFrom.z - post.z;
        float span = Mathf.Sqrt(dx * dx + dz * dz);
        if (span < 1f)
        {
            dx = 0f;
            dz = 1f;
            span = 1f;
        }

        return new GlobalPosition(post.x - dx / span * step, post.y, post.z - dz / span * step);
    }

    /// <summary>Horizontal metres between two positions, pure. Height is irrelevant to every
    /// airfield-surface question.</summary>
    private static float HorizontalSpan(GlobalPosition a, GlobalPosition b)
    {
        float dx = b.x - a.x;
        float dz = b.z - a.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// One place a ground vehicle may be told to stand, kept off every runway and taxiway on the
    /// map: <paramref name="post"/> itself when it is clear, otherwise the same post pushed away
    /// from the airfield it is sitting on until it is clear (at most
    /// <c>CommanderBuildPreview.AirfieldPushMaxSteps</c> steps).
    /// <paramref name="approachFrom"/> is where the vehicles are coming from, used only when the
    /// post sits exactly on the airfield centre; pass the post itself when there is no such side.
    /// <paramref name="moved"/> says whether the post had to move.
    /// </summary>
    internal static GlobalPosition OffAirfieldStandingPoint(
        GlobalPosition post, GlobalPosition approachFrom, out bool moved)
    {
        moved = false;
        if (!CommanderBuildPreview.IsOnAirfieldSurface(
                post, CommanderBuildPreview.VehicleAirfieldClearanceMeters))
        {
            return post;
        }

        GlobalPosition centre = TryNearestAirfieldCentre(post, out GlobalPosition found) ? found : post;
        GlobalPosition origin = AirfieldPushOrigin(
            post, centre, approachFrom, CommanderBuildPreview.AirfieldPushStepMeters);
        return CommanderBuildPreview.OffAirfieldPost(origin, post, out moved);
    }

    /// <summary>The centre of the airbase nearest <paramref name="position"/>, any faction's — the
    /// same "every airbase on the map counts" rule the surface test itself applies, because a base
    /// about to be captured is one whose strip is about to be wanted.</summary>
    private static bool TryNearestAirfieldCentre(GlobalPosition position, out GlobalPosition centre)
    {
        centre = default;
        if (FactionRegistry.airbaseLookup == null)
        {
            return false;
        }

        bool any = false;
        float best = float.MaxValue;
        foreach (KeyValuePair<string, Airbase> entry in FactionRegistry.airbaseLookup)
        {
            Airbase airbase = entry.Value;
            if (airbase == null || airbase.disabled || airbase.center == null)
            {
                continue;
            }

            GlobalPosition candidate = airbase.center.GlobalPosition();
            float span = HorizontalSpan(candidate, position);
            if (span < best)
            {
                best = span;
                centre = candidate;
                any = true;
            }
        }

        return any;
    }

    /// <summary>Writes the "n of m posts moved off a runway or taxiway" line once per ring centre,
    /// rather than once per review. One definition for the reserve ring, a point's own ring and the
    /// defence arc (Reuse rule 5, generalised out of <c>BuildHoldRing</c> when the second and third
    /// ring needed the same line).</summary>
    private static void LogAirfieldPostsMoved(string what, GlobalPosition center, int moved, int total)
    {
        if (moved <= 0 || !ringMovedLogged.Add($"{what} ({center.x:0},{center.z:0})"))
        {
            return;
        }

        CommanderPlugin.Log.LogInfo(
            $"{what} at ({center.x:0},{center.z:0}): {moved} of {total} posts moved off a runway or taxiway.");
    }

    /// <summary>
    /// Sends every commanded ground vehicle that is standing on a runway or a taxiway off it, once.
    /// Runs at the end of the movement tick so its order is the last one issued that tick and is not
    /// immediately overwritten by a hold-post drive. A vehicle under a player order is left alone —
    /// if the player parked it there, that is the player's call.
    /// </summary>
    private void ClearVehiclesOffAirfield(FactionHQ hq, OperationsState state)
    {
        airfieldSweep.Clear();
        for (int i = 0; i < state.Platoons.Count; i++)
        {
            airfieldSweep.AddRange(state.Platoons[i].Members);
        }

        airfieldSweep.AddRange(state.Pool);

        for (int i = 0; i < airfieldSweep.Count; i++)
        {
            Unit unit = airfieldSweep[i];
            if (unit == null || unit.disabled || unit is not GroundVehicle)
            {
                continue;
            }

            GlobalPosition here = unit.transform.GlobalPosition();
            if (!CommanderBuildPreview.IsOnAirfieldSurface(
                    here, CommanderBuildPreview.VehicleAirfieldClearanceMeters))
            {
                airfieldClearedUnits.Remove(unit);
                continue;
            }

            if (airfieldClearedUnits.Contains(unit)
                || CommanderMoveService.Instance?.HasPlayerOrder(unit) == true)
            {
                continue;
            }

            GlobalPosition off = OffAirfieldStandingPoint(here, here, out bool moved);
            if (!moved || !CommanderGameAccess.TrySetDestination(unit, off))
            {
                continue;
            }

            airfieldClearedUnits.Add(unit);
            CommanderAiLog.Note(
                hq,
                $"moves {CommanderGameAccess.GetUnitLabel(unit)} off the runway or taxiway it was "
                    + $"standing on ({HorizontalSpan(here, off):0} m).");
        }

        airfieldSweep.Clear();
    }

    /// <summary>The airfield-clearance rules at their named boundaries (user instruction,
    /// 2026-09-16). Called from the operations self-check with the rest of the ground rules.
    /// </summary>
    private static void CheckAirfieldClearance(List<string> failures)
    {
        // The ordinary case: a post out on the ring is pushed away from the airfield centre.
        GlobalPosition centre = new(1000f, 0f, 1000f);
        GlobalPosition onTheStrip = new(1500f, 0f, 1000f);
        GlobalPosition origin = AirfieldPushOrigin(onTheStrip, centre, onTheStrip, 150f);
        Expect(failures, "a ring post is pushed away from the airfield centre (x)", origin.x, centre.x);
        Expect(failures, "a ring post is pushed away from the airfield centre (z)", origin.z, centre.z);

        // The case the ring push cannot answer on its own: a post sitting exactly on the centre.
        // The push must then run toward whoever is coming, so the origin is one step the other way.
        GlobalPosition approach = new(1000f, 0f, 5000f);
        GlobalPosition centred = AirfieldPushOrigin(centre, centre, approach, 150f);
        Expect(failures, "a post on the airfield centre is pushed toward the approach", centred.z, 850f);
        Expect(failures, "a post on the airfield centre is not pushed sideways", centred.x, 1000f);

        // With no approach either, the answer still has to be an answer rather than the post itself,
        // or the post would never move and the rule would silently do nothing.
        GlobalPosition nowhere = AirfieldPushOrigin(centre, centre, centre, 150f);
        Expect(failures, "a post with nowhere to come from is still given a push direction", nowhere.z, 850f);
        Expect(
            failures,
            "a post with nowhere to come from is not left on the centre",
            HorizontalSpan(nowhere, centre) >= 1f,
            true);

        // The push itself has to clear a real strip: eight steps of 150 m is 1.2 km, which crosses
        // the widest runway on these maps and the taxiway beside it.
        Expect(
            failures,
            "the airfield push reaches across a runway; check CommanderBuildPreview.AirfieldPushStepMeters",
            CommanderBuildPreview.AirfieldPushStepMeters * CommanderBuildPreview.AirfieldPushMaxSteps >= 1000f,
            true);
        Expect(
            failures,
            "a parked vehicle is kept clear of the tarmac by more than a vehicle length; check VehicleAirfieldClearanceMeters",
            CommanderBuildPreview.VehicleAirfieldClearanceMeters >= 20f,
            true);
    }
}
