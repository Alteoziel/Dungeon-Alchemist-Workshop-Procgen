using System.Linq;
using System.Reflection;
using DungeonAlchemist.Backend;
using DungeonAlchemist.Config;
using DungeonAlchemist.Database;
using HarmonyLib;

namespace WorkshopProcgen.Patches
{
    /// <summary>
    /// Captures the database even if AddImportedModelDescription gets inlined by the Mono JIT; the index updater
    /// then polls the imported-config table for changes.
    /// </summary>
    [HarmonyPatch]
    internal static class DatabaseConnectionCtorPatch
    {
        private static MethodBase _target;

        private static bool Prepare()
        {
            if (_target != null)
                return true;
            _target = AccessTools.GetDeclaredConstructors(typeof(DatabaseConnection))
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (_target == null)
                Plugin.Log.LogWarning("DatabaseConnection constructor not found; relying on AddImportedModelDescription only.");
            else
                Plugin.Log.LogInfo("Patching DatabaseConnection..ctor");
            return _target != null;
        }

        private static MethodBase TargetMethod() => _target;

        private static void Postfix(DatabaseConnection __instance) => WorkshopIndex.RegisterDatabase(__instance);
    }

    [HarmonyPatch]
    internal static class AddImportedModelDescriptionPatch
    {
        private static MethodBase _target;

        private static bool Prepare() =>
            (_target ??= PatchTargets.Resolve(typeof(DatabaseConnection), nameof(DatabaseConnection.AddImportedModelDescription))) != null;

        private static MethodBase TargetMethod() => _target;

        private static void Postfix(DatabaseConnection __instance, string importedModelId, string blockId, ImportedModelDescription description)
        {
            WorkshopIndex.MarkDirty(__instance);
            if (Plugin.VerboseLogging.Value)
                Plugin.Log.LogInfo($"[recon] Imported config registered: block={blockId} model={importedModelId} desc={description}");
        }
    }

    [HarmonyPatch]
    internal static class HideImportedModelPatch
    {
        private static MethodBase _target;

        private static bool Prepare() =>
            (_target ??= PatchTargets.Resolve(typeof(DatabaseConnection), nameof(DatabaseConnection.HideImportedModel))) != null;

        private static MethodBase TargetMethod() => _target;

        private static void Postfix(DatabaseConnection __instance, string importedModelId)
        {
            WorkshopIndex.MarkDirty(__instance);
            if (Plugin.VerboseLogging.Value)
                Plugin.Log.LogInfo($"[recon] Imported model hidden: {importedModelId}");
        }
    }

    [HarmonyPatch]
    internal static class HideCustomModelPatch
    {
        private static MethodBase _target;

        private static bool Prepare() =>
            (_target ??= PatchTargets.Resolve(typeof(DatabaseConnection), nameof(DatabaseConnection.HideCustomModel))) != null;

        private static MethodBase TargetMethod() => _target;

        private static void Postfix(DatabaseConnection __instance, BuildingBlockConfig config)
        {
            WorkshopIndex.MarkDirty(__instance);
            if (Plugin.VerboseLogging.Value)
                Plugin.Log.LogInfo($"[recon] Custom model hidden: {config}");
        }
    }
}
