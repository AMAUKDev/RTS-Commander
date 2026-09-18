using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>
/// The mod's one answer to "may this base put this airframe into the air, and take it back".
/// </summary>
/// <remarks>
/// <para>
/// The game's own <c>Airbase.CanSpawnAircraft</c> / <c>Hangar.CanSpawnAircraft</c> answer a
/// narrower question than the mod needs. Both are pure roster lookups — <c>Hangar</c> holds an
/// authored <c>availableAircraft</c> array and the base is the union of its hangars' arrays — so a
/// helipad says yes to every airframe its prefab roster names, including ones that cannot use a
/// pad. The VT-7 Vagrant (<c>VTOLTrainer1</c>) is exactly that case: a vertical-lift airframe
/// carrying a <c>Pilot.PilotType.Plane</c>, so <c>Pilot.SetStartingAiState</c> hands it
/// <c>AIPilotCombatModes</c>, the aeroplane AI that expects a strip, and then it is started on a
/// pad with no strip anywhere on the base. It despawns or falls over (user report, 2026-09-16).
/// Note that this is NOT the <c>PilotType.VTOL</c> case <c>CommanderAirCommandService.CanAiFly</c>
/// already bans; that ban does not catch it, which is why it gets through.
/// </para>
/// <para>
/// So the rule is: <b>an aircraft may only be produced at a facility that suits how it flies.</b> A
/// base with no runway is a pad-only base, and a pad-only base may only launch and recover
/// airframes that fly like a helicopter. "Flies like a helicopter" is not re-decided here — it is
/// <c>CommanderAirCommandService.IsRotaryAirframe</c>, the mod's single definition-level test
/// (Reuse rule 4), which is true for <c>PilotType.Helo</c> and <c>PilotType.Tiltwing</c>. The
/// tiltwing VL-49 Tarantula is therefore allowed on a pad on purpose: the game gives it
/// <c>AIHeloCombatState</c>, it homes on <c>airbase.verticalLandingPoints</c> to recover, and every
/// other rotary decision in the mod already counts it as rotary. Splitting it out here would be a
/// second, disagreeing idea of what a helicopter is.
/// </para>
/// <para>
/// Every place in the mod that asks whether a base or a hangar will produce a type goes through
/// <see cref="CanBaseLaunch"/> or <see cref="CanHangarLaunch"/>. The raw game calls are made here
/// and nowhere else; a pointer comment sits at each converted site so a future reader does not
/// reintroduce a direct call.
/// </para>
/// </remarks>
internal static class CommanderAirLaunchFacility
{
    /// <summary>
    /// How many ways the game says an airframe can be flown: 4, read off <c>Pilot.PilotType</c> in
    /// the shipped <c>Assembly-CSharp</c> on 2026-09-16 — Plane 0, Helo 1, Tiltwing 2, VTOL 3. The
    /// facility rule divides exactly these four into "needs a strip" and "does not", so a game
    /// update that adds a fifth introduces an airframe class nobody has decided about, and the
    /// named check below says so at load rather than letting it default to needing a strip.
    /// </summary>
    private const int KnownPilotTypeCount = 4;

    /// <summary>
    /// The rule itself, with no game types in it so the self-check can drive it: a facility suits
    /// an airframe when the base has a strip, or when the airframe does not need one.
    /// </summary>
    internal static bool FacilitySuitsAirframe(bool baseHasRunway, bool airframeIsRotary)
    {
        return baseHasRunway || airframeIsRotary;
    }

    /// <summary>
    /// Whether the base has anywhere an aeroplane could roll. Derived from what the facility HAS
    /// rather than from its name or its hangars' rosters, so it holds on a map that names things
    /// differently, and so it is not circular: a helipad's authored roster is exactly the thing
    /// that is wrong, so "does a hangar here list a non-rotary type" cannot be the test.
    /// A forward base is created with an empty runway list on purpose
    /// (<c>CommanderFobBuilder.TryCreateFobAirbase</c>: "a FOB has no strip"), a carrier keeps the
    /// runways authored on its prefab through <c>Airbase.SetupAttachedAirbase</c>, and a ship with
    /// only a deck pad has none — all three answer correctly here. Takeoff-only and landing-only
    /// runway flags are deliberately not read: this one test has to give launch and recovery the
    /// same answer, or an airframe is produced somewhere it can never come home to.
    /// </summary>
    internal static bool BaseHasRunway(Airbase? airbase)
    {
        Airbase.Runway[]? runways = airbase?.runways;
        for (int i = 0; runways != null && i < runways.Length; i++)
        {
            Airbase.Runway runway = runways[i];
            if (runway?.Start != null && runway.End != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// May this base produce this airframe. The game's roster answer
    /// (<c>Airbase.CanSpawnAircraft</c>, the RAW call — read it here and nowhere else) narrowed by
    /// <see cref="FacilitySuitsAirframe"/>.
    /// </summary>
    internal static bool CanBaseLaunch(Airbase? airbase, AircraftDefinition? definition)
    {
        return airbase != null
            && definition != null
            && airbase.CanSpawnAircraft(definition)
            && FacilitySuitsAirframe(
                BaseHasRunway(airbase), CommanderAirCommandService.IsRotaryAirframe(definition));
    }

    /// <summary>
    /// May this one hangar produce this airframe: the game's roster answer for the hangar
    /// (<c>Hangar.CanSpawnAircraft</c>, the RAW call — read it here and nowhere else), its own
    /// readiness, and the same facility rule read off the base the hangar belongs to.
    /// </summary>
    internal static bool CanHangarLaunch(Airbase? airbase, Hangar? hangar, AircraftDefinition? definition)
    {
        return hangar != null
            && definition != null
            && !hangar.Disabled
            && hangar.Available
            && hangar.CanSpawnAircraft(definition)
            && FacilitySuitsAirframe(
                BaseHasRunway(airbase), CommanderAirCommandService.IsRotaryAirframe(definition));
    }

    /// <summary>
    /// The facility rule at its named boundaries, run once from <see cref="CommanderPlugin"/> at
    /// load. The whole rule is two booleans and one <c>||</c>, so a single inverted character
    /// silently turns it into either "pads launch nothing" — which stops forward bases and
    /// air-mobile platoons working — or "pads launch anything", which is the bug it was written
    /// for, and neither shows up until a match is well under way.
    /// </summary>
    internal static void SelfCheck()
    {
        List<string> failures = new();
        CheckLaunchFacility(failures);

        if (failures.Count == 0)
        {
            CommanderPlugin.Log.LogDebug("Air launch facility self-check passed.");
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Air launch facility self-check FAILED: {failures[i]}");
        }
    }

    /// <summary>The rule at its named boundaries, collected rather than logged so the same body can
    /// be driven outside the game — the shape <c>CommanderAirCommandGroundLoss.CheckGroundLoss</c>
    /// established.</summary>
    internal static void CheckLaunchFacility(List<string> failures)
    {
        Expect(
            failures,
            "a helipad-only base does not launch a plane-piloted airframe such as the VT-7 Vagrant",
            FacilitySuitsAirframe(baseHasRunway: false, airframeIsRotary: false),
            false);
        // The forward-base supply checks. These deliberately do NOT hard-code "rotary" as a literal
        // true: each asks the mod's own pilot-type rule what the airframe is and then asks the
        // facility rule whether a pad will take it, so BOTH halves are pinned. Narrowing the rotary
        // rule to helicopters, or refusing rotary airframes at a pad, fails these — which is the
        // point, because either one strands the forward bases. Where a forward base is supplied
        // from, if these ever go red: nowhere. A forward base's only aircraft facility is its
        // helipads (CommanderFobBuilder's recipe is a depot, a radar and two of them), so the pads
        // are the whole answer; a runway base could still fly cargo TO it, but the mod's own
        // insertion and air-mobile steps launch from the accepting base nearest the objective, and
        // a forward base exists precisely to be that base.
        Expect(
            failures,
            "a forward base's pads still launch the UH-90 Ibis, the helicopter transport",
            FacilitySuitsAirframe(
                baseHasRunway: false,
                airframeIsRotary: CommanderAirCommandService.IsRotaryPilotType(Pilot.PilotType.Helo)),
            true);
        Expect(
            failures,
            "a forward base's pads still launch the VL-49 Tarantula, the only transport that can "
                + "air-land a vehicle able to take ground",
            FacilitySuitsAirframe(
                baseHasRunway: false,
                airframeIsRotary: CommanderAirCommandService.IsRotaryPilotType(Pilot.PilotType.Tiltwing)),
            true);
        Expect(
            failures,
            "the mod still counts a tiltwing as flying like a helicopter; if it does not, a forward "
                + "base can no longer be supplied with anything that takes ground",
            CommanderAirCommandService.IsRotaryPilotType(Pilot.PilotType.Tiltwing),
            true);
        Expect(
            failures,
            "an aeroplane pilot is not counted as flying like a helicopter, whatever the airframe lifts on",
            CommanderAirCommandService.IsRotaryPilotType(Pilot.PilotType.Plane),
            false);
        Expect(
            failures,
            "the rule never refuses an airframe that flies like a helicopter, at any base",
            FacilitySuitsAirframe(baseHasRunway: false, airframeIsRotary: true)
                && FacilitySuitsAirframe(baseHasRunway: true, airframeIsRotary: true),
            true);
        Expect(
            failures,
            "the game still defines the four pilot types this rule was reasoned about; a new one "
                + "has never been classified as flying like a helicopter or not",
            System.Enum.GetValues(typeof(Pilot.PilotType)).Length,
            KnownPilotTypeCount);
        Expect(
            failures,
            "an airfield with a strip launches an aeroplane",
            FacilitySuitsAirframe(baseHasRunway: true, airframeIsRotary: false),
            true);
        Expect(
            failures,
            "an airfield with a strip still launches a helicopter",
            FacilitySuitsAirframe(baseHasRunway: true, airframeIsRotary: true),
            true);
        Expect(
            failures,
            "the strip is what separates the two aeroplane answers; the rule must not ignore the base",
            FacilitySuitsAirframe(baseHasRunway: true, airframeIsRotary: false)
                != FacilitySuitsAirframe(baseHasRunway: false, airframeIsRotary: false),
            true);
        Expect(
            failures,
            "a base with no runway array at all is treated as pad-only",
            BaseHasRunway(null),
            false);
    }

    private static void Expect(List<string> failures, string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, int actual, int expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }
}
