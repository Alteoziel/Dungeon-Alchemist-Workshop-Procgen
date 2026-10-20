using Bombardel;
using DungeonAlchemist;
using DungeonAlchemist.Config;
using DungeonAlchemist.MapEditing;
using HarmonyLib;
using UnityEngine.EventSystems;
using WorkshopProcgen.MultiPaint;

namespace WorkshopProcgen.Patches
{
    /// <summary>
    /// Modifier+click on an object button in the place-object picker toggles it in the paint set
    /// instead of selecting it. BuildingBlockButton inherits its click handling from BetterToggle.
    /// </summary>
    internal static class BrushSelectionPatches
    {
        internal static void Apply(Harmony harmony)
        {
            MultiPaintModule.TryPatch(harmony,
                AccessTools.Method(typeof(BetterToggle), nameof(BetterToggle.OnPointerClick),
                    new[] { typeof(PointerEventData), typeof(bool) }),
                "BetterToggle.OnPointerClick(PointerEventData, bool)",
                prefix: nameof(OnPointerClickPrefix), patchType: typeof(BrushSelectionPatches));
        }

        private static bool OnPointerClickPrefix(BetterToggle __instance, PointerEventData eventData)
        {
            if (eventData == null || eventData.button != PointerEventData.InputButton.Left)
            {
                return true;
            }
            if (!MultiPaintConfig.IsActive || !(__instance is BuildingBlockButton button) || button.disabled)
            {
                return true;
            }
            if (!MultiPaintConfig.IsSelectModifierHeld())
            {
                return true;
            }
            if (button.GetComponentInParent<BuildingBlockSelector>() == null)
            {
                return true;
            }
            BuildingBlockVariant variant = button.entry?.GetVariant();
            if (variant?.block == null || variant.block.type != BuildingBlockType.Object)
            {
                return true;
            }

            bool added = PaintSet.Toggle(variant, button.entry.GetName());
            MultiPaintModule.Log.LogInfo($"{(added ? "Added" : "Removed")} '{button.entry.GetName()}' ({variant.block.Id}) " +
                                         $"{(added ? "to" : "from")} paint set ({PaintSet.Count} total)");
            return false;
        }
    }
}
