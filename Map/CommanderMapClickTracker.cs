using UnityEngine;

namespace GroundControlRts;

internal sealed class CommanderMapClickTracker
{
    private const float MaxClickMovement = 8f;

    private bool pressed;
    private Vector2 pressPosition;

    internal bool Tick(DynamicMap dynamicMap, out GlobalPosition position)
    {
        position = default;

        if (CommanderShortcutInput.IsDown(CommanderSettings.PrimaryAction) && dynamicMap.IsCursorInMapRectangle())
        {
            pressed = true;
            pressPosition = Input.mousePosition;
        }

        if (!pressed || !CommanderShortcutInput.IsUp(CommanderSettings.PrimaryAction))
        {
            return false;
        }

        pressed = false;
        if (Vector2.Distance(pressPosition, Input.mousePosition) > MaxClickMovement)
        {
            return false;
        }

        return dynamicMap.TryGetCursorCoordinates(out position);
    }

    internal void Reset()
    {
        pressed = false;
    }
}
