using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The refund for a commanded airframe that is destroyed while it is still on the ground at one of
/// its own airbases — parked on the apron, taxiing out, or sitting on the deck after an RTB.
/// </summary>
/// <remarks>
/// <para>
/// Until this existed, an airframe the commander had paid for was only ever refunded by landing and
/// being recovered (<c>HandleAircraftReturned</c>, <c>AirCommand/CommanderAirCommandPilotHooks.cs</c>).
/// Anything killed before it left the field was simply money gone, which is how a friendly MBT
/// driving through a taxiing fighter cost the faction a full airframe price (user report,
/// 2026-09-16). A hull that never entered the war is not a war loss, so the money goes back.
/// </para>
/// <para>
/// The rule is deliberately not narrowed to "never took off". An aeroplane that has flown, come
/// home and put itself down is about to be recovered for its full price anyway
/// (<c>RecoverLandedAircraft</c>); losing it in the last three seconds of that wait to a stray
/// shell should not cost more than losing it in the first. So the test is where the airframe was,
/// not what it had done.
/// </para>
/// <para>
/// A refund can only ever happen once. Both refund paths go through
/// <see cref="TryRefundPurchase"/> and both remove the mission from the table immediately
/// afterwards, and the mission record is the only thing that carries the price — so whichever hook
/// fires first, the second finds nothing to refund. The two hooks cannot even collide in practice:
/// <c>Aircraft.ReturnToInventory</c> writes <c>Networkdisabled</c> straight rather than calling
/// <c>Unit.DisableUnit</c>, so a recovery never runs the disable hook this refund hangs off.
/// </para>
/// </remarks>
internal sealed partial class CommanderAirCommandService
{
    /// <summary>
    /// How far from the centre of one of its own airbases a hull on the ground still counts as lost
    /// "at the base": 3 km. The airbases on these maps run about 2 km end to end, so an aeroplane
    /// killed at the far threshold of the strip or out on a dispersal pad is still on the field,
    /// while anything past 3 km is out in the country and was flying the war rather than parked.
    /// </summary>
    internal const float GroundLossBaseRangeMeters = 3000f;

    /// <summary>
    /// Whether a commanded airframe that has just been destroyed was lost on the ground at one of
    /// its own airbases, and so owes its purchase price back. Pure, for the self-check.
    /// </summary>
    /// <param name="onDeck">The airframe was on the ground and either stopped or taxiing —
    /// <see cref="IsOnDeck"/>, the mod's one definition of "on the deck", which is also what the
    /// recovery sweep and the air markers ask.</param>
    /// <param name="metersToNearestFriendlyBase">Horizontal metres from the wreck to the nearest
    /// airbase this faction holds.</param>
    internal static bool IsGroundLossAtFriendlyBase(bool onDeck, float metersToNearestFriendlyBase)
    {
        return onDeck && metersToNearestFriendlyBase <= GroundLossBaseRangeMeters;
    }

    /// <summary>
    /// The one place a commanded airframe's purchase price goes back to the faction that bought it
    /// (Reuse rule 4). Two callers: the recovery, where the Basegame has just put the airframe back
    /// into stock and the temporary stock entry has to be taken out again with it, and the ground
    /// loss, where nothing came back and only the money moves. Server-only, because
    /// <c>AddFunds</c> and <c>ModifyUnitSupply</c> both write faction state a pure client may not.
    /// </summary>
    /// <param name="returnedToStock">The Basegame has already restored one airframe of this type to
    /// the faction's inventory, so the temporary purchased airframe must be removed from it.</param>
    /// <returns>Whether the purchase price was actually paid back.</returns>
    private static bool TryRefundPurchase(Aircraft aircraft, AirMission mission, bool returnedToStock)
    {
        if (!mission.PurchasedWithFunds || mission.Hq == null || !mission.Hq.IsServer)
        {
            return false;
        }

        if (returnedToStock && aircraft != null && aircraft.definition != null)
        {
            mission.Hq.ModifyUnitSupply(aircraft.definition, -1);
        }

        mission.Hq.AddFunds(mission.PurchaseCost);
        return true;
    }

    /// <summary>
    /// Called the moment any unit is disabled. A commanded airframe killed on the ground at one of
    /// its own airbases gets its price back and says so in the loss line; everything else is left
    /// alone and goes on to be removed from the mission table as it always was.
    /// </summary>
    internal void RefundGroundLoss(Aircraft aircraft)
    {
        if (aircraft == null
            || !missions.TryGetValue(aircraft, out AirMission mission)
            || !mission.PurchasedWithFunds
            || mission.Hq == null
            || !mission.Hq.IsServer)
        {
            return;
        }

        if (!CommanderEnemyCommanderService.TryNearestOwnBase(
                mission.Hq, aircraft.transform.GlobalPosition(), out Airbase? field, out float meters)
            || field == null
            || !IsGroundLossAtFriendlyBase(IsOnDeck(aircraft), meters))
        {
            return;
        }

        float refunded = mission.PurchaseCost;
        if (!TryRefundPurchase(aircraft, mission, returnedToStock: false))
        {
            return;
        }

        // The mission leaves the table here rather than in the caller, so a second hook arriving for
        // the same airframe finds no price left to pay back.
        RemoveMission(aircraft);

        string line = $"lost {GetAircraftLabel(aircraft.definition)} on the ground at "
            + $"{CommanderCaptureService.GetAirbaseLabel(field)}; {refunded:0} refunded.";
        // The status line is the player's own window, so it only ever carries the player's news.
        if (ReferenceEquals(mission.Hq, CommanderGameAccess.GetLocalHq()))
        {
            SetStatus(line);
        }

        // A faction whose wing the commander tracks writes this through its own loss ledger, in its
        // own voice, at the next air review; anything else has no ledger and logs it here. Exactly
        // one line either way.
        if (!CommanderEnemyCommanderService.NoteAirframeRefundedOnDeck(aircraft, line))
        {
            CommanderPlugin.Log.LogInfo($"Air Command: {line}");
        }
    }

    /// <summary>
    /// The ground-loss refund's boundaries, run once from <see cref="CommanderPlugin"/> at load.
    /// Both halves of the rule can be inverted by a one-character edit with nothing visibly
    /// breaking: a refund that never fires is money the commander silently never gets back, and one
    /// that fires too widely pays for every aeroplane that crashes in a field.
    /// </summary>
    internal static void SelfCheck()
    {
        List<string> failures = new();
        CheckGroundLoss(failures);

        if (failures.Count == 0)
        {
            CommanderPlugin.Log.LogDebug("Air Command ground-loss self-check passed.");
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Air Command ground-loss self-check FAILED: {failures[i]}");
        }
    }

    /// <summary>The ground-loss rule at its named boundaries, collected rather than logged so the
    /// same body can be driven outside the game.</summary>
    private static void CheckGroundLoss(List<string> failures)
    {
        Expect(
            failures,
            "a hull on the deck beside its own base is refunded",
            IsGroundLossAtFriendlyBase(true, 200f),
            true);
        Expect(
            failures,
            "a hull on the deck at the far end of the strip is refunded",
            IsGroundLossAtFriendlyBase(true, GroundLossBaseRangeMeters),
            true);
        Expect(
            failures,
            "a hull on the ground a metre past the base range is not refunded",
            IsGroundLossAtFriendlyBase(true, GroundLossBaseRangeMeters + 1f),
            false);
        Expect(
            failures,
            "an aircraft shot down over its own base is not refunded",
            IsGroundLossAtFriendlyBase(false, 0f),
            false);
        Expect(
            failures,
            "an aircraft lost 40 km out is not refunded",
            IsGroundLossAtFriendlyBase(false, 40000f),
            false);
        Expect(
            failures,
            "the base range reaches past a runway's own length; check GroundLossBaseRangeMeters",
            GroundLossBaseRangeMeters >= 2000f,
            true);
    }

    private static void Expect(List<string> failures, string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }
}
