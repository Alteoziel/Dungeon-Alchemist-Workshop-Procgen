using System.Reflection;
using System.Threading;
using DungeonAlchemist.Config;
using DungeonAlchemist.MapEditing.SteamWorkshop;
using DungeonAlchemist.RoomGeneration;
using HarmonyLib;

namespace WorkshopProcgen.Patches
{
    internal static class GenerationStats
    {
        public static int SimulatedSpawns;
        public static int Considered;
        public static int Swapped;
        public static int SwapRejected;
        public static int SkippedOnTop;

        public static void Reset()
        {
            SimulatedSpawns = 0;
            Considered = 0;
            Swapped = 0;
            SwapRejected = 0;
            SkippedOnTop = 0;
        }

        public static string Summary() =>
            $"considered={Considered}, swapped={Swapped}, left native because items were on top={SkippedOnTop}, replacement did not fit={SwapRejected}";
    }

    [HarmonyPatch]
    internal static class GenerateRoomReconPatch
    {
        private static MethodBase _target;

        private static bool Prepare() =>
            (_target ??= PatchTargets.Resolve(typeof(ThreadedMapEditor), nameof(ThreadedMapEditor.GenerateRoom))) != null;

        private static MethodBase TargetMethod() => _target;

        private static void Prefix(RoomConfig roomConfig)
        {
            TraceLog.Write($"hook: ThreadedMapEditor.GenerateRoom '{roomConfig?.name}'");
            if (Plugin.VerboseLogging.Value)
                Plugin.Log.LogInfo($"[recon] GenerateRoom requested on thread {Thread.CurrentThread.ManagedThreadId}: room config '{roomConfig?.name}' ({roomConfig?.Id})");
        }
    }

    [HarmonyPatch]
    internal static class RoomGeneratorGeneratePatch
    {
        private static MethodBase _target;

        private static bool Prepare() =>
            (_target ??= PatchTargets.Resolve(typeof(RoomGenerator), nameof(RoomGenerator.Generate))) != null;

        private static MethodBase TargetMethod() => _target;

        private static void Prefix() => GenerationStats.Reset();

        private static void Postfix()
        {
            TraceLog.Write($"hook: RoomGenerator.Generate done: {GenerationStats.Summary()}");
            if (GenerationStats.Considered > 0)
                Plugin.Log.LogInfo($"[swap] room generation finished: {GenerationStats.Summary()}");
        }
    }

    [HarmonyPatch]
    internal static class RoomGeneratorExpandPatch
    {
        private static MethodBase _target;

        private static bool Prepare() =>
            (_target ??= PatchTargets.Resolve(typeof(RoomGenerator), nameof(RoomGenerator.Expand))) != null;

        private static MethodBase TargetMethod() => _target;

        private static void Prefix() => GenerationStats.Reset();

        private static void Postfix()
        {
            if (GenerationStats.Considered > 0)
                Plugin.Log.LogInfo($"[swap] room expand finished: {GenerationStats.Summary()}");
        }
    }

    [HarmonyPatch]
    internal static class SpawnCandidateReconPatch
    {
        private static MethodBase _target;

        private static bool Prepare() =>
            (_target ??= PatchTargets.Resolve(typeof(ObjectGenerator), nameof(ObjectGenerator.SpawnCandidate))) != null;

        private static MethodBase TargetMethod() => _target;

        private static void Postfix(GeneratedObjectCandidate candidate, bool __result)
        {
            if (!Plugin.VerboseLogging.Value)
                return;
            Interlocked.Increment(ref GenerationStats.SimulatedSpawns);
            Plugin.Log.LogDebug($"[recon] simulated SpawnCandidate block={candidate?.blockId} at {candidate?.surface?.mapPosition} ok={__result}");
        }
    }

    [HarmonyPatch]
    internal static class WorkshopUpdateModelsReconPatch
    {
        private static MethodBase _target;

        private static bool Prepare() =>
            (_target ??= PatchTargets.Resolve(typeof(WorkshopModelLoader), nameof(WorkshopModelLoader.UpdateModels))) != null;

        private static MethodBase TargetMethod() => _target;

        private static void Prefix()
        {
            if (Plugin.VerboseLogging.Value)
                Plugin.Log.LogInfo("[recon] WorkshopModelLoader.UpdateModels started (async; index rebuilds after configs stop arriving)");
        }
    }
}
