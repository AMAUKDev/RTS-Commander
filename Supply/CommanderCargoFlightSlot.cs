namespace GroundControlRts;

/// <summary>
/// Which ONE cargo run a per-point call is talking about (lift-wave_20260916). The contract between
/// the operations side, which hands out the identities, and the supply side, which files the runs —
/// a shared rule rather than either service's private business, which is why it is here and not in
/// <see cref="CommanderSupplyHeliService"/> beside the queue it filters.
/// <para>
/// Until 2026-09-16 the supply side's per-point calls — <c>CancelInsertion</c>,
/// <c>TryConvertInsertionToAirdrop</c>, the registration bind and the delivery credit — were written
/// to the invariant that a <see cref="CommanderStrategicPoint"/> carries at most ONE cargo mission,
/// and a forward-base or platoon lift kept that invariant by sending its loads one after another.
/// The user's instruction that an air-mobile platoon go in one wave lifts the invariant, so every
/// one of those calls now has to be able to mean one load of three rather than all of them.
/// </para>
/// <para>
/// This class has NO Unity, Harmony or game-assembly dependency on purpose: it is the one piece of
/// the wave that can be self-checked outside a running game, and the supply service's own type
/// initializer cannot be run in a harness.
/// </para>
/// </summary>
internal static class CommanderCargoFlightSlot
{
    /// <summary>
    /// The slot a cargo run carries when its caller keeps no per-flight identity: ZERO. It is the
    /// value every caller written before the platoon lift wave passes — the player's supply window,
    /// the SAM foundation drop, the naval run and every picket insertion — and it means "this run is
    /// the only one its point has", which is exactly what those callers guarantee. A cancel, an
    /// airdrop conversion or a delivery quoted with zero therefore still addresses EVERY run
    /// standing on the point, which is the behaviour they have always had; only a caller that hands
    /// out real ids narrows to one run.
    /// </summary>
    internal const int Unslotted = 0;

    /// <summary>
    /// Whether a cargo run whose own slot is <paramref name="runSlot"/> is the one a call for
    /// <paramref name="wantedSlot"/> means, pure. A wanted slot of <see cref="Unslotted"/> means
    /// every run on the point, so it matches anything; otherwise the two must be the same run. An
    /// unslotted RUN is never caught by a call meant for one load of a wave — a lift that has handed
    /// out identities must not recall a picket transport that happens to share its landing zone.
    /// <para>One definition, because the cancel, the airdrop conversion, the registration bind and
    /// the delivery credit must never disagree about which transport of a three-ship wave they are
    /// talking about — disagreeing is how a wave loses a load it never lost.</para>
    /// </summary>
    internal static bool Matches(int runSlot, int wantedSlot)
    {
        return wantedSlot == Unslotted || runSlot == wantedSlot;
    }
}
