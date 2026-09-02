using BepInEx.Configuration;
using UnityEngine;

namespace GroundControlRts;

internal static class CommanderShortcutInput
{
    internal static bool IsDown(KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None
            && Input.GetKeyDown(shortcut.MainKey)
            && AreModifiersPressed(shortcut);
    }

    internal static bool IsUp(KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None && Input.GetKeyUp(shortcut.MainKey);
    }

    internal static bool IsPressed(KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None
            && Input.GetKey(shortcut.MainKey)
            && AreModifiersPressed(shortcut);
    }

    private static bool AreModifiersPressed(KeyboardShortcut shortcut)
    {
        foreach (KeyCode modifier in shortcut.Modifiers)
        {
            if (!Input.GetKey(modifier))
            {
                return false;
            }
        }
        return true;
    }
}
