using BepInEx.Configuration;
using UnityEngine;

namespace WorkshopProcgen.MultiPaint
{
    internal static class MultiPaintConfig
    {
        private const string Section = "MultiPaint";

        internal static ConfigEntry<bool> MultiPaintEnabled { get; private set; }
        internal static ConfigEntry<KeyCode> SelectModifier { get; private set; }
        internal static ConfigEntry<KeyboardShortcut> ClearSetHotkey { get; private set; }
        internal static ConfigEntry<KeyboardShortcut> ToggleOverlayHotkey { get; private set; }
        internal static ConfigEntry<bool> ShowOverlay { get; private set; }
        internal static ConfigEntry<bool> RandomizePlaceBrush { get; private set; }
        internal static ConfigEntry<bool> RandomizeDragPlacement { get; private set; }
        internal static ConfigEntry<bool> OverridePaintCustomBrush { get; private set; }

        internal static void Bind(ConfigFile config)
        {
            MultiPaintEnabled = config.Bind(Section, "MultiPaintEnabled", true,
                "Allow building a multi-asset paint set in the object picker and painting/placing from it.");
            SelectModifier = config.Bind(Section, "SelectModifier", KeyCode.LeftControl,
                "Hold this key while clicking an object in the picker to add/remove it from the paint set. " +
                "LeftControl/LeftShift/LeftAlt also accept their right-hand counterpart.");
            ClearSetHotkey = config.Bind(Section, "ClearSetHotkey", new KeyboardShortcut(KeyCode.F7),
                "Clear the paint set.");
            ToggleOverlayHotkey = config.Bind(Section, "ToggleOverlayHotkey", new KeyboardShortcut(KeyCode.F8),
                "Show/hide the paint set overlay.");
            ShowOverlay = config.Bind(Section, "ShowOverlay", true,
                "Show an on-screen list of the paint set while it is not empty.");
            RandomizePlaceBrush = config.Bind(Section, "RandomizePlaceBrush", true,
                "When the place-object brush was started from a paint set member, switch to a random set member after every placement.");
            RandomizeDragPlacement = config.Bind(Section, "RandomizeDragPlacement", true,
                "When drag-placing a line/area with the place-object brush, pick a random set member for each spawned object.");
            OverridePaintCustomBrush = config.Bind(Section, "OverridePaintCustomBrush", true,
                "In the Paint Objects tab, 'Create Brush From Selected Object' paints the whole paint set when it is not empty.");
        }

        internal static bool IsActive => MultiPaintEnabled != null && MultiPaintEnabled.Value;

        internal static bool IsSelectModifierHeld()
        {
            KeyCode key = SelectModifier.Value;
            if (key == KeyCode.None)
            {
                return false;
            }
            if (Input.GetKey(key))
            {
                return true;
            }
            switch (key)
            {
                case KeyCode.LeftControl: return Input.GetKey(KeyCode.RightControl);
                case KeyCode.LeftShift: return Input.GetKey(KeyCode.RightShift);
                case KeyCode.LeftAlt: return Input.GetKey(KeyCode.RightAlt);
                default: return false;
            }
        }
    }
}
