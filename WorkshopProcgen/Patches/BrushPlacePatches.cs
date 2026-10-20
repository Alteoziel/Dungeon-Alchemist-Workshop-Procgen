using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using DungeonAlchemist.Config;
using DungeonAlchemist.MapEditing;
using HarmonyLib;
using WorkshopProcgen.MultiPaint;

namespace WorkshopProcgen.Patches
{
    /// <summary>
    /// Place-object brush: when started from a paint set member, every placement (click, shift-click,
    /// and each object of a line/area drag) uses a random paint set member.
    /// </summary>
    internal static class BrushPlacePatches
    {
        private const int ClickNone = 0;
        private const int ClickSingle = 1;
        private const int ClickDrag = 2;

        internal static void Apply(Harmony harmony)
        {
            if (!PlaceBrushSwapper.Resolve())
            {
                MultiPaintModule.Log.LogWarning("[skip] place-brush randomisation disabled: PlaceObjectBrush internals changed");
                return;
            }
            Type self = typeof(BrushPlacePatches);
            Type brush = typeof(PlaceObjectBrush);

            MethodInfo create = AccessTools.GetDeclaredMethods(brush).FirstOrDefault(m =>
                m.Name == nameof(PlaceObjectBrush.Create) && m.IsPublic &&
                m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(BuildingBlockVariant));
            MultiPaintModule.TryPatch(harmony, create, "PlaceObjectBrush.Create(BuildingBlockVariant, ...)",
                postfix: nameof(CreatePostfix), patchType: self);
            MultiPaintModule.TryPatch(harmony, AccessTools.DeclaredMethod(brush, nameof(PlaceObjectBrush.DestroyBrush)),
                "PlaceObjectBrush.DestroyBrush", postfix: nameof(DestroyBrushPostfix), patchType: self);
            MultiPaintModule.TryPatch(harmony, AccessTools.DeclaredMethod(brush, nameof(PlaceObjectBrush.OnClick)),
                "PlaceObjectBrush.OnClick", prefix: nameof(OnClickPrefix), postfix: nameof(OnClickPostfix), patchType: self);
            MultiPaintModule.TryPatch(harmony, AccessTools.DeclaredMethod(brush, nameof(PlaceObjectBrush.SwapVariant)),
                "PlaceObjectBrush.SwapVariant", postfix: nameof(SwapVariantPostfix), patchType: self);

            MultiPaintModule.TryPatch(harmony,
                AccessTools.DeclaredMethod(brush, nameof(PlaceObjectBrush.SpawnDragSurfaces)),
                "PlaceObjectBrush.SpawnDragSurfaces", transpiler: nameof(SpawnDragSurfacesTranspiler), patchType: self);
        }

        private static void CreatePostfix(PlaceObjectBrush __instance, BuildingBlockVariant block)
        {
            if (MultiPaintConfig.IsActive && PaintSet.Count >= 2 && PaintSet.Contains(block?.block?.Id))
            {
                PlaceBrushSwapper.ActiveBrush = __instance;
                MultiPaintModule.Log.LogInfo($"Place brush uses paint set ({PaintSet.Count} assets)");
            }
            else if (ReferenceEquals(PlaceBrushSwapper.ActiveBrush, __instance))
            {
                PlaceBrushSwapper.ActiveBrush = null;
            }
        }

        private static void DestroyBrushPostfix(PlaceObjectBrush __instance)
        {
            if (ReferenceEquals(PlaceBrushSwapper.ActiveBrush, __instance))
            {
                PlaceBrushSwapper.ActiveBrush = null;
            }
        }

        private static void OnClickPrefix(PlaceObjectBrush __instance, out int __state)
        {
            __state = ClickNone;
            if (!PlaceBrushSwapper.IsMultiBrush(__instance))
            {
                return;
            }
            if (PlaceBrushSwapper.DragSurfaceCount(__instance) > 0)
            {
                __state = ClickDrag;
            }
            else if (PlaceBrushSwapper.HasLastValidOp(__instance))
            {
                __state = ClickSingle;
            }
        }

        private static void OnClickPostfix(PlaceObjectBrush __instance, PlaceObjectBrushMode mode, int __state)
        {
            if (__state == ClickNone || !PlaceBrushSwapper.IsMultiBrush(__instance))
            {
                return;
            }
            // Multiple mode already rerolls through SwapVariant; Single mode only needs it when the
            // placement went through (OnClick clears lastValidOp right after the real AddObject).
            bool placed = __state == ClickDrag ||
                          (mode == PlaceObjectBrushMode.Single && !PlaceBrushSwapper.HasLastValidOp(__instance));
            if (placed && MultiPaintConfig.RandomizePlaceBrush.Value)
            {
                PlaceBrushSwapper.SwapToRandom(__instance);
            }
        }

        private static void SwapVariantPostfix(PlaceObjectBrush __instance)
        {
            if (PlaceBrushSwapper.IsMultiBrush(__instance) && MultiPaintConfig.RandomizePlaceBrush.Value)
            {
                PlaceBrushSwapper.SwapToRandom(__instance);
            }
        }

        // SpawnDragSurfaces calls StartSimulation once per surface before both the simulated and the real
        // AddObject, and reads the template field after it, so swapping there gives one random asset per surface.
        // A transpiler is used because Mono may inline the one-line StartSimulation, bypassing a prefix on it.
        private static IEnumerable<CodeInstruction> SpawnDragSurfacesTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo startSimulation = AccessTools.DeclaredMethod(typeof(MapEditor), nameof(MapEditor.StartSimulation));
            MethodInfo hook = AccessTools.Method(typeof(BrushPlacePatches), nameof(BeforeDragSurfaceSpawn));
            int injected = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (startSimulation != null && instruction.Calls(startSimulation))
                {
                    // Stack holds the MapEditor receiver; push the brush, consume it in the hook, leave the receiver.
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                    yield return new CodeInstruction(OpCodes.Call, hook);
                    injected++;
                }
                yield return instruction;
            }
            if (injected == 0)
            {
                MultiPaintModule.Log.LogWarning("[skip] SpawnDragSurfaces: no StartSimulation call found, drag randomisation disabled");
            }
        }

        private static void BeforeDragSurfaceSpawn(PlaceObjectBrush brush)
        {
            if (PlaceBrushSwapper.IsMultiBrush(brush) && MultiPaintConfig.RandomizeDragPlacement.Value)
            {
                PlaceBrushSwapper.SwapToRandom(brush);
            }
        }
    }
}
