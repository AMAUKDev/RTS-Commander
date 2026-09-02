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
