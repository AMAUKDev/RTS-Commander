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
