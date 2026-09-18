namespace GroundControlRts;

// Lifecycle hooks a commander service can opt into. CommanderServiceRegistry fans each phase
// out to whoever implements the matching interface, so adding a service means one Register
// call instead of threading it through six parallel lists in CommanderModeController.
//
// A service implements only the hooks it needs; there are no empty overrides.

/// <summary>Called when RTS mode is entered (advanced services only on gated missions).</summary>
internal interface ICommanderActivate
{
    void Activate();
}

/// <summary>Called when RTS mode is left, on scene change, and on shutdown. Always runs,
/// regardless of the feature gate, so nothing is left bound when the gate flips.</summary>
internal interface ICommanderDeactivate
{
    void Deactivate();
}

/// <summary>Per frame while RTS mode is active.</summary>
internal interface ICommanderTickActive
{
    void TickActive();
}

/// <summary>Per frame regardless of mode, for work that must survive the player flying:
/// in-flight missions, unit routes, alerts, production queues.</summary>
internal interface ICommanderTickPersistent
{
    void TickPersistent();
}

/// <summary>Called on scene change. Always runs, for every tier.</summary>
internal interface ICommanderResetSession
{
    void ResetSession();
}

/// <summary>
/// Opt-in hook for a service that has state worth carrying across a BepInEx ScriptEngine hot
/// reload (developer quality-of-life only; see <see cref="CommanderStateStore"/>). Fanned out the
/// same way as the phases above, but on its own schedule: <see cref="Snapshot"/> runs while a
/// mission is live and on shutdown, <see cref="Restore"/> runs at most once, only on the run that
/// loaded from a hot reload and only after the session guard in <see cref="CommanderStateStore"/>
/// has already accepted the file. A service that has nothing worth saving does not implement this.
/// </summary>
internal interface ICommanderPersistState
{
    /// <summary>Write this service's state into the shared snapshot.</summary>
    void Snapshot(CommanderStateWriter w);

    /// <summary>Restore this service's state from an already-guard-checked snapshot.</summary>
    void Restore(CommanderStateReader r);
}

/// <summary>
/// Opt-in hook for a service that owns part of the STRATEGIC picture — who holds what, which
/// forward bases exist, how much money each faction has — which the developer can save and carry
/// across a mission restart (see <see cref="CommanderStrategicSaveStore"/>).
/// </summary>
/// <remarks>
/// Deliberately a second interface beside <see cref="ICommanderPersistState"/> rather than two more
/// methods on it. The two are read on different runs and by different gates, and the hot-reload
/// records are actively harmful across a mission restart: they are keyed by <c>PersistentID</c>,
/// which is a counter <c>UnitRegistry.Clear()</c> resets, so a saved id would resolve to a
/// DIFFERENT unit after a restart. Keeping the two sets apart is what stops that, and it leaves the
/// hot-reload path exactly as it was.
/// <para>
/// <see cref="RestoreStrategic"/> is LOAD-ONLY: it reads its records into memory and writes nothing
/// to the world. The world is rebuilt afterwards, by
/// <see cref="CommanderStrategicSaveStore"/>, in an order the registration order does not give —
/// money has to be set before a garrison can be paid for.
/// </para>
/// </remarks>
internal interface ICommanderPersistStrategic
{
    /// <summary>Write this service's strategic state into the shared save.</summary>
    void SnapshotStrategic(CommanderStrategicWriter w);

    /// <summary>Read this service's strategic state out of an already-gate-checked save. No world
    /// writes here: stash the records and let the store drive the rebuild.</summary>
    void RestoreStrategic(CommanderStrategicReader r);
}
