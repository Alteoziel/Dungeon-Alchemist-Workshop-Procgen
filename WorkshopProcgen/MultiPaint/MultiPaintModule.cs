using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using WorkshopProcgen.Patches;
using Logger = BepInEx.Logging.Logger;

namespace WorkshopProcgen.MultiPaint
{
    /// <summary>
    /// Entry point for the multi-asset paint feature. Uses its own Harmony instance and patches each
    /// target individually so a renamed game method only disables that piece, never the generator patches.
    /// </summary>
    internal static class MultiPaintModule
    {
        internal const string HarmonyId = Plugin.PluginGuid + ".multipaint";

        internal static ManualLogSource Log { get; private set; }

        private static Harmony harmony;

        internal static void Init(BaseUnityPlugin plugin)
        {
            if (harmony != null)
            {
                return;
            }
            Log = Logger.CreateLogSource("WorkshopProcgen.MultiPaint");
            MultiPaintConfig.Bind(plugin.Config);
            harmony = new Harmony(HarmonyId);

            BrushSelectionPatches.Apply(harmony);
            BrushPlacePatches.Apply(harmony);
            BrushPaintPatches.Apply(harmony);

            var host = new GameObject("WorkshopProcgen.MultiPaint") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<MultiPaintBehaviour>();
            Log.LogInfo($"Multi-paint initialised (enabled={MultiPaintConfig.MultiPaintEnabled.Value}, " +
                        $"select modifier={MultiPaintConfig.SelectModifier.Value}, clear={MultiPaintConfig.ClearSetHotkey.Value})");
        }

        internal static void Shutdown()
        {
            harmony?.UnpatchSelf();
            harmony = null;
        }

        internal static bool TryPatch(Harmony h, MethodBase target, string description,
            string prefix = null, string postfix = null, string finalizer = null, string transpiler = null,
            Type patchType = null)
        {
            if (target == null)
            {
                Log.LogWarning($"[skip] patch target not found: {description}");
                return false;
            }
            try
            {
                h.Patch(target,
                    Hook(patchType, prefix),
                    Hook(patchType, postfix),
                    Hook(patchType, transpiler),
                    Hook(patchType, finalizer),
                    null);
                Log.LogDebug($"Patched {description}");
                return true;
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to patch {description}: {e}");
                return false;
            }
        }

        private static HarmonyMethod Hook(Type patchType, string name)
        {
            if (name == null)
            {
                return null;
            }
            MethodInfo method = AccessTools.Method(patchType, name);
            if (method == null)
            {
                throw new MissingMethodException(patchType.FullName, name);
            }
            return new HarmonyMethod(method);
        }
    }
}
