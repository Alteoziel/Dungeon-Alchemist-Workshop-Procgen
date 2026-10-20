using System;
using System.Reflection;
using HarmonyLib;

namespace WorkshopProcgen.Patches
{
    internal static class PatchTargets
    {
        /// <summary>Resolves a patch target for a Harmony Prepare(); logs and returns null instead of throwing.</summary>
        public static MethodBase Resolve(Type type, string method, Type[] args = null)
        {
            MethodBase target = null;
            try
            {
                target = type == null ? null : AccessTools.Method(type, method, args);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Error resolving {type?.Name}.{method}: {e.Message}");
            }

            if (target == null)
                Plugin.Log.LogWarning($"Patch target {type?.FullName ?? "<null type>"}.{method} not found; skipping that patch.");
            else
                Plugin.Log.LogInfo($"Patching {target.DeclaringType?.Name}.{target.Name}");
            return target;
        }
    }
}
