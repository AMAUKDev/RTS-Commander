using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderCursorController
{
    private bool active;
    private bool previousMapFlag;
    private bool previousForceHidden;

    internal void Activate()
    {
        previousMapFlag = CursorManager.GetFlag(CursorFlags.Map);
        previousForceHidden = CursorManager.GetFlags() != CursorFlags.None && !CursorManager.Visible;
        active = true;
        CursorManager.ForceHidden(false);
        CursorManager.SetFlag(CursorFlags.Map, true);
    }

    internal void Deactivate()
    {
        if (!active)
        {
            return;
        }

        CursorManager.SetFlag(CursorFlags.Map, previousMapFlag);
        CursorManager.ForceHidden(previousForceHidden);
        active = false;
    }

    internal void TickActive()
    {
        if (!CursorManager.GetFlag(CursorFlags.Map))
        {
            CursorManager.SetFlag(CursorFlags.Map, true);
        }

        if (!CursorManager.Visible)
        {
            CursorManager.ForceHidden(false);
            CursorManager.Refresh();
        }
    }
}
