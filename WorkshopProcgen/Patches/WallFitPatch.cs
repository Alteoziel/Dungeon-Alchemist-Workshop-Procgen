using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using DungeonAlchemist;
using DungeonAlchemist.Config;
using DungeonAlchemist.Database;
using DungeonAlchemist.MapViewing;
using DungeonAlchemist.MeshCutter;
using HarmonyLib;
using UnityEngine;

namespace WorkshopProcgen.Patches
{
    /// <summary>
    /// The wall renderer loads the mesh with the block's yaw and uniform scale, then cuts door holes in that
    /// space. A converted panel needs a non-uniform fit, including standing a flat mesh up, applied to the
    /// same transform the cutters read. The back-face cutter resets that transform, so the fit is put back.
    /// </summary>
    [HarmonyPatch(typeof(BuildingBlockConfig), nameof(BuildingBlockConfig.ApplyToModel))]
    internal static class WallFitApplyPatch
    {
        private static void Postfix(BuildingBlockConfig __instance, Transform transform)
        {
            WallPropConverter.Apply(transform, __instance?.Id);
        }
    }

    [HarmonyPatch(typeof(BackSideModelCutter), nameof(BackSideModelCutter.CutoutBackside))]
    internal static class WallFitRestorePatch
    {
        private static void Postfix(ModelSourceModel wallModel)
        {
            if (wallModel == null)
                return;
            WallPropConverter.Apply(wallModel.transform, wallModel.config?.Id);
        }
    }

    /// <summary>
    /// Colliders and coverage read the config bounds, which cannot express an axis swap. Report the fitted segment.
    /// </summary>
    [HarmonyPatch]
    internal static class WallFitBoundsPatch
    {
        private static MethodBase _target;

        private static bool Prepare() =>
            (_target ??= AccessTools.Method(typeof(BuildingBlockConfig), nameof(BuildingBlockConfig.GetModelBounds), new[] { typeof(DatabaseConnection) })) != null;

        private static MethodBase TargetMethod() => _target;

        private static void Postfix(BuildingBlockConfig __instance, ref Cube3D __result)
        {
            if (__instance?.Id == null || !WallPropConverter.TryGetFit(__instance.Id, out var fit))
                return;
            __result = Cube3D.FromAxisAligned(fit.Center, fit.Size.x, fit.Size.y, fit.Size.z);
        }
    }

    /// <summary>
    /// A normal floor tile swaps the loaded mesh for the game's tile sheet. Assigned floors keep the fitted model.
    /// The load is caught here: Scifi floor 110 threw from ModelSourceModel.Create, Forget published that as an
    /// unobserved exception, and the bug reporter treated the flood of floor tiles as a fatal error.
    /// </summary>
    [HarmonyPatch(typeof(FloorAsyncModelLoader), nameof(FloorAsyncModelLoader.LoadNewModel))]
    internal static class FittedFloorMeshPatch
    {
        private static AccessTools.FieldRef<FloorAsyncModelLoader, BuildingBlockConfig> _config;
        private static MethodInfo _addToCache;
        private static FieldInfo _databaseManager;
        private static readonly HashSet<string> Logged = new HashSet<string>();

        private static void Bind()
        {
            _config = AccessTools.FieldRefAccess<FloorAsyncModelLoader, BuildingBlockConfig>("config");
            _addToCache = AccessTools.Method(typeof(AsyncModelLoader), "AddToCache");
            _databaseManager = AccessTools.Field(typeof(DatabaseAutoInit), "databaseManager");
        }

        private static bool Prefix(FloorAsyncModelLoader __instance, ref IModelSourceModel __result)
        {
            if (_config == null)
                Bind();
            BuildingBlockConfig config = _config(__instance);
            if (config == null || !WallPropConverter.TryGetFit(config.Id, out _))
                return true;

            ModelSourceModel model = UnityEngine.Object.Instantiate(__instance.normalModelPrefab);
            Action release = () =>
            {
                if (_addToCache != null)
                    _addToCache.Invoke(__instance, new object[] { model });
            };
            LoadFitted(__instance, model, config, release).Forget();
            __result = model;
            return false;
        }

        private static async UniTask LoadFitted(FloorAsyncModelLoader loader, ModelSourceModel model, BuildingBlockConfig config, Action release)
        {
            try
            {
                if (model == null)
                    throw new InvalidOperationException("the floor model prefab is missing");
                if (_databaseManager != null && _databaseManager.GetValue(model) == null)
                {
                    object manager = _databaseManager.GetValue(loader);
                    if (manager != null)
                        _databaseManager.SetValue(model, manager);
                }
                await model.Create(config, false, release);
                WallPropConverter.Apply(model.transform, config.Id);
            }
            catch (Exception e)
            {
                if (Logged.Add(config.Id ?? ""))
                {
                    string note = $"Floor '{config.name}' could not be shown ({e.GetType().Name}: {e.Message}).";
                    Plugin.Log.LogWarning("[wall] " + note + " The room is left intact.");
                    FeedbackForm.Offer(note);
                }
            }
        }
    }

    /// <summary>
    /// Doors and windows load through the same model path. A failed opening must not escape the async load.
    /// </summary>
    [HarmonyPatch(typeof(WallOpeningModelSourceModel), nameof(WallOpeningModelSourceModel.Create))]
    internal static class FittedOpeningPatch
    {
        private static readonly HashSet<string> Logged = new HashSet<string>();

        private static bool Prefix(WallOpeningModelSourceModel __instance, BuildingBlockConfig config, ModelCurver modelCurver, Action release, ref UniTask __result)
        {
            if (config?.Id == null || !WallPropConverter.IsConvertedId(config.Id))
                return true;
            __result = Load(__instance, config, modelCurver, release);
            return false;
        }

        private static async UniTask Load(WallOpeningModelSourceModel host, BuildingBlockConfig config, ModelCurver modelCurver, Action release)
        {
            try
            {
                AccessTools.Field(typeof(WallOpeningModelSourceModel), "release")?.SetValue(host, release);
                if (host.normalModelSourceModelPrefab == null)
                    throw new InvalidOperationException("the opening model prefab is missing");
                ModelSourceModel model = UnityEngine.Object.Instantiate(host.normalModelSourceModelPrefab);
                await model.Create(config, false);
                if (host.container != null)
                    model.transform.SetParent(host.container, false);
                modelCurver?.CurveTransform(config, host.container);
                AccessTools.Property(typeof(WallOpeningModelSourceModel), "Loaded")?.SetValue(host, true);
            }
            catch (Exception e)
            {
                if (Logged.Add(config.Id ?? ""))
                {
                    string note = $"'{config.name}' could not be shown as a door or window ({e.GetType().Name}: {e.Message}).";
                    Plugin.Log.LogWarning("[wall] " + note + " The room is left intact.");
                    FeedbackForm.Offer(note);
                }
            }
        }
    }

    /// <summary>
    /// The wall boolean needs one tangent per vertex, and it dies outright past the 16-bit mesh limit.
    /// Egyptian Wall 1 is a 65532-vertex glb with no tangents, so the cut threw out of an async wall
    /// load and the game treated that as a fatal error. Smaller meshes get the missing channels filled in.
    /// Larger ones keep their mesh and skip the back-face, door, and curve cuts.
    /// </summary>
    internal static class MeshCutSafety
    {
        public const int MaxVertices = 32768;

        private static readonly HashSet<int> Logged = new HashSet<int>();

        public static bool CanCut(Mesh mesh)
        {
            if (mesh == null)
                return true;
            if (mesh.vertexCount > MaxVertices)
            {
                if (Logged.Add(mesh.GetInstanceID()))
                    Plugin.Log.LogWarning($"[wall] '{mesh.name}' has {mesh.vertexCount} vertices, so back-face and door cuts are skipped. The wall still renders.");
                return false;
            }
            EnsureAttributes(mesh);
            return true;
        }

        public static Mesh Copy(Mesh mesh)
        {
            return mesh == null ? null : UnityEngine.Object.Instantiate(mesh);
        }

        public static void EnsureAttributes(Mesh mesh)
        {
            if (mesh == null)
                return;
            int count;
            try
            {
                count = mesh.vertexCount;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[wall] Could not read '{mesh.name}': {e.Message}");
                return;
            }
            if (count == 0)
                return;
            try
            {
                if (mesh.uv == null || mesh.uv.Length != count)
                    mesh.uv = new Vector2[count];
                if (mesh.normals == null || mesh.normals.Length != count)
                    mesh.RecalculateNormals();
                if (mesh.tangents != null && mesh.tangents.Length == count)
                    return;
                try
                {
                    mesh.RecalculateTangents();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[wall] Could not calculate tangents for '{mesh.name}': {e.Message}");
                }
                if (mesh.tangents == null || mesh.tangents.Length != count)
                {
                    var tangents = new Vector4[count];
                    var fill = new Vector4(1f, 0f, 0f, 1f);
                    for (int i = 0; i < count; i++)
                        tangents[i] = fill;
                    mesh.tangents = tangents;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[wall] Could not prepare '{mesh.name}' for cutting: {e.Message}");
            }
        }

        public static Exception Swallow(Exception error, string step, string name)
        {
            if (error == null)
                return null;
            Plugin.Log.LogError($"[wall] {step} failed for '{name}': {error.Message}");
            return null;
        }
    }

    [HarmonyPatch(typeof(DynamicMesh), MethodType.Constructor, new[] { typeof(Mesh) })]
    internal static class DynamicMeshAttributesPatch
    {
        private static void Prefix(Mesh mesh)
        {
            if (mesh != null && mesh.vertexCount <= MeshCutSafety.MaxVertices)
                MeshCutSafety.EnsureAttributes(mesh);
        }
    }

    [HarmonyPatch(typeof(BackSideModelCutter), "CalculateCutout")]
    internal static class BackSideCutSafetyPatch
    {
        private static bool Prefix(Mesh sourceMesh, ref Mesh __result)
        {
            if (MeshCutSafety.CanCut(sourceMesh))
                return true;
            __result = MeshCutSafety.Copy(sourceMesh);
            return false;
        }
    }

    [HarmonyPatch(typeof(BackSideModelCutter), nameof(BackSideModelCutter.CutoutBackside))]
    internal static class BackSideCutGuardPatch
    {
        private static Exception Finalizer(Exception __exception, ModelSourceModel wallModel)
        {
            return MeshCutSafety.Swallow(__exception, "Back-face cut", wallModel?.config?.name);
        }
    }

    [HarmonyPatch(typeof(CutoutModelCutter), "CalculateCutout")]
    internal static class DoorCutSafetyPatch
    {
        private static bool Prefix(Mesh sourceMesh, ref Mesh __result)
        {
            if (MeshCutSafety.CanCut(sourceMesh))
                return true;
            __result = MeshCutSafety.Copy(sourceMesh);
            return false;
        }
    }

    [HarmonyPatch(typeof(CutoutModelCutter), nameof(CutoutModelCutter.CutoutWallSide))]
    internal static class DoorCutGuardPatch
    {
        private static Exception Finalizer(Exception __exception, ModelSourceModel wallModel)
        {
            return MeshCutSafety.Swallow(__exception, "Door cut", wallModel?.config?.name);
        }
    }

    [HarmonyPatch(typeof(ModelFlipper), "FlipMesh")]
    internal static class FlipMeshSafetyPatch
    {
        private static bool Prefix(Mesh mesh, ref Mesh __result)
        {
            if (MeshCutSafety.CanCut(mesh))
                return true;
            __result = MeshCutSafety.Copy(mesh);
            return false;
        }
    }

    [HarmonyPatch(typeof(ModelFlipper), nameof(ModelFlipper.FlipModel))]
    internal static class FlipModelGuardPatch
    {
        private static Exception Finalizer(Exception __exception, ModelSourceModel model)
        {
            return MeshCutSafety.Swallow(__exception, "Wall flip", model?.config?.name);
        }
    }

    [HarmonyPatch(typeof(ModelCurver), "CutMyMeshIntoPieces")]
    internal static class CurveCutSafetyPatch
    {
        private static bool Prefix(MeshFilter meshFilter, ref Mesh __result)
        {
            Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (MeshCutSafety.CanCut(mesh))
                return true;
            __result = MeshCutSafety.Copy(mesh);
            return false;
        }
    }

    [HarmonyPatch(typeof(ModelCurver), nameof(ModelCurver.CurveModel))]
    internal static class CurveModelGuardPatch
    {
        private static Exception Finalizer(Exception __exception, ModelSourceModel wallModel)
        {
            return MeshCutSafety.Swallow(__exception, "Wall curve", wallModel?.config?.name);
        }
    }
}
