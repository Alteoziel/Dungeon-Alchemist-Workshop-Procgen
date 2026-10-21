using System;
using System.Reflection;
using DungeonAlchemist.Config;
using DungeonAlchemist.Data;
using DungeonAlchemist.MapEditing;
using DungeonAlchemist.RoomGeneration;
using HarmonyLib;
using UnityEngine;

namespace WorkshopProcgen.Patches
{
    /// <summary>Set only while a room-generation commit is placing walls or openings, so manual painting is left alone.</summary>
    internal static class StructuralGeneration
    {
        [ThreadStatic] private static int _depth;
        [ThreadStatic] private static bool _retrying;

        public static bool IsActive => _depth > 0;
        public static bool Retrying => _retrying;

        public static void Enter() => _depth++;
        public static void Exit()
        {
            if (_depth > 0)
                _depth--;
        }

        public static void SetRetrying(bool value) => _retrying = value;
    }

    [HarmonyPatch]
    internal static class SpawnFloorsPatch
    {
        private static MethodBase _target;
        private static AccessTools.FieldRef<MapManipulatorRoomGenratorTarget, MapManipulator> _mapRef;
        private static FieldInfo _floorConfigField;

        private static bool Prepare()
        {
            if (_target != null)
                return true;
            try
            {
                _mapRef = AccessTools.FieldRefAccess<MapManipulatorRoomGenratorTarget, MapManipulator>("mapManipulator");
                _floorConfigField = AccessTools.Field(typeof(AddFloorConfig), "config");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Floor swap fields not found ({e.Message}); floor replacement disabled.");
                return false;
            }
            if (_floorConfigField == null)
            {
                Plugin.Log.LogWarning("AddFloorConfig.config not found; floor replacement disabled.");
                return false;
            }
            _target = PatchTargets.Resolve(typeof(MapManipulatorRoomGenratorTarget), nameof(MapManipulatorRoomGenratorTarget.SpawnFloors));
            return _target != null;
        }

        private static MethodBase TargetMethod() => _target;

        private static bool Prefix(MapManipulatorRoomGenratorTarget __instance, GeneratedFloorData floorData)
        {
            if (!Plugin.Enabled.Value || floorData?.floors == null)
                return true;
            var snap = WorkshopIndex.Current;
            var floors = _mapRef(__instance)?.floorManipulator;
            if (snap == null || floors == null)
                return true;

            float chance = Plugin.ReplacementChance.Value;
            foreach (var floor in floorData.floors)
            {
                if (floor?.config == null)
                    continue;
                var native = floor.config;
                var tile = floor.tile;
                bool swapped = snap.TryPickStructural(native, new Vector2(tile.x, tile.y), 0f, chance, out var replacement);
                if (swapped)
                {
                    GenerationStats.Considered++;
                    _floorConfigField.SetValue(floor, replacement);
                }
                try
                {
                    floors.AddFloorBuildingBlockInstance(floor);
                    if (swapped)
                        GenerationStats.Swapped++;
                }
                catch (Exception e)
                {
                    if (!swapped)
                    {
                        Plugin.Log.LogWarning($"[swap] Native floor '{native.name}' failed to place. {e.Message}");
                        continue;
                    }
                    GenerationStats.SwapRejected++;
                    Plugin.Log.LogWarning($"[swap] Floor '{replacement.name}' did not fit ({e.Message}); placing native '{native.name}'.");
                    _floorConfigField.SetValue(floor, native);
                    try { floors.AddFloorBuildingBlockInstance(floor); }
                    catch (Exception again)
                    {
                        Plugin.Log.LogWarning($"[swap] Native floor '{native.name}' also failed. {again.Message}");
                    }
                }
            }
            return false;
        }
    }

    [HarmonyPatch]
    internal static class SpawnWallsFlagPatch
    {
        private static MethodBase _target;

        private static bool Prepare() =>
            (_target ??= PatchTargets.Resolve(typeof(MapManipulatorRoomGenratorTarget), nameof(MapManipulatorRoomGenratorTarget.SpawnWalls))) != null;

        private static MethodBase TargetMethod() => _target;

        private static void Prefix(ref bool __state)
        {
            __state = Plugin.Enabled.Value && WorkshopIndex.Current != null;
            if (__state)
                StructuralGeneration.Enter();
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
                StructuralGeneration.Exit();
        }
    }

    [HarmonyPatch]
    internal static class SpawnWallOpeningsFlagPatch
    {
        private static MethodBase _target;

        private static bool Prepare() =>
            (_target ??= PatchTargets.Resolve(typeof(MapManipulatorRoomGenratorTarget), nameof(MapManipulatorRoomGenratorTarget.SpawnWallOpenings))) != null;

        private static MethodBase TargetMethod() => _target;

        private static void Prefix(ref bool __state)
        {
            __state = Plugin.Enabled.Value && WorkshopIndex.Current != null;
            if (__state)
                StructuralGeneration.Enter();
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
                StructuralGeneration.Exit();
        }
    }

    [HarmonyPatch]
    internal static class SpawnBasicWallPatch
    {
        private static MethodBase _target;

        private static bool Prepare()
        {
            if (_target != null)
                return true;
            _target = PatchTargets.Resolve(typeof(FoundationWallManipulator), nameof(FoundationWallManipulator.SpawnBasicWall),
                new[] { typeof(WallSide), typeof(BuildingBlockConfig), typeof(int), typeof(string), typeof(bool), typeof(bool) });
            return _target != null;
        }

        private static MethodBase TargetMethod() => _target;

        private static void Prefix(WallSide wallSide, ref BuildingBlockConfig config, ref BuildingBlockConfig __state)
        {
            __state = null;
            if (!StructuralGeneration.IsActive || StructuralGeneration.Retrying || config == null)
                return;
            var snap = WorkshopIndex.Current;
            if (snap == null || !snap.TryPickStructural(config, wallSide.GetPosition(), wallSide.left ? 0f : 1f, Plugin.ReplacementChance.Value, out var replacement))
                return;
            GenerationStats.Considered++;
            __state = config;
            config = replacement;
        }

        private static void Postfix(ref PlaceWallOperation __result, BuildingBlockConfig __state, FoundationWallManipulator __instance,
            WallSide wallSide, int randSeed, string parentInstanceId, bool doValidityChecks, bool updatePillars)
        {
            if (__state == null || StructuralGeneration.Retrying)
                return;
            if (__result != null && __result.wallPlaced)
            {
                GenerationStats.Swapped++;
                return;
            }
            RetryWall(__instance, wallSide, __state, randSeed, parentInstanceId, doValidityChecks, updatePillars, ref __result);
        }

        private static Exception Finalizer(Exception __exception, BuildingBlockConfig __state, FoundationWallManipulator __instance,
            WallSide wallSide, int randSeed, string parentInstanceId, bool doValidityChecks, bool updatePillars, ref PlaceWallOperation __result)
        {
            if (__exception == null || __state == null || StructuralGeneration.Retrying)
                return __exception;
            Plugin.Log.LogWarning($"[swap] Wall '{__state.name}' replacement threw; placing native. {__exception.Message}");
            RetryWall(__instance, wallSide, __state, randSeed, parentInstanceId, doValidityChecks, updatePillars, ref __result);
            return null;
        }

        private static void RetryWall(FoundationWallManipulator manipulator, WallSide wallSide, BuildingBlockConfig native,
            int randSeed, string parentInstanceId, bool doValidityChecks, bool updatePillars, ref PlaceWallOperation result)
        {
            GenerationStats.SwapRejected++;
            StructuralGeneration.SetRetrying(true);
            try
            {
                result = manipulator.SpawnBasicWall(wallSide, native, randSeed, parentInstanceId, doValidityChecks, updatePillars);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[swap] Native wall '{native?.name}' also failed. {e.Message}");
            }
            finally
            {
                StructuralGeneration.SetRetrying(false);
            }
        }
    }

    [HarmonyPatch]
    internal static class OrientWallOpeningPatch
    {
        private static MethodBase _target;

        private static bool Prepare() =>
            (_target ??= PatchTargets.Resolve(typeof(WallOpeningManipulator), nameof(WallOpeningManipulator.AddWallOpeningAndOrientTowardsInterior),
                new[] { typeof(string), typeof(WallPieceSection), typeof(BuildingBlockConfig) })) != null;

        private static MethodBase TargetMethod() => _target;

        private static void Prefix(WallPieceSection wallPieceSection, ref BuildingBlockConfig config, ref BuildingBlockConfig __state)
            => OpeningSwap.Prefix(wallPieceSection, ref config, ref __state);

        private static void Postfix(ref PlaceWallOpeningOperation __result, BuildingBlockConfig __state, WallOpeningManipulator __instance,
            string foundationId, WallPieceSection wallPieceSection)
            => OpeningSwap.Postfix(ref __result, __state, native => __instance.AddWallOpeningAndOrientTowardsInterior(foundationId, wallPieceSection, native));

        private static Exception Finalizer(Exception __exception, BuildingBlockConfig __state, WallOpeningManipulator __instance,
            string foundationId, WallPieceSection wallPieceSection, ref PlaceWallOpeningOperation __result)
            => OpeningSwap.Finalizer(__exception, __state, ref __result,
                native => __instance.AddWallOpeningAndOrientTowardsInterior(foundationId, wallPieceSection, native));
    }

    [HarmonyPatch]
    internal static class PlainWallOpeningPatch
    {
        private static MethodBase _target;

        private static bool Prepare() =>
            (_target ??= PatchTargets.Resolve(typeof(WallOpeningManipulator), nameof(WallOpeningManipulator.AddWallOpeningWithoutChangingOrientation),
                new[] { typeof(string), typeof(WallPieceSection), typeof(BuildingBlockConfig) })) != null;

        private static MethodBase TargetMethod() => _target;

        private static void Prefix(WallPieceSection wallPieceSection, ref BuildingBlockConfig config, ref BuildingBlockConfig __state)
            => OpeningSwap.Prefix(wallPieceSection, ref config, ref __state);

        private static void Postfix(ref PlaceWallOpeningOperation __result, BuildingBlockConfig __state, WallOpeningManipulator __instance,
            string foundationId, WallPieceSection wallPieceSection)
            => OpeningSwap.Postfix(ref __result, __state, native => __instance.AddWallOpeningWithoutChangingOrientation(foundationId, wallPieceSection, native));

        private static Exception Finalizer(Exception __exception, BuildingBlockConfig __state, WallOpeningManipulator __instance,
            string foundationId, WallPieceSection wallPieceSection, ref PlaceWallOpeningOperation __result)
            => OpeningSwap.Finalizer(__exception, __state, ref __result,
                native => __instance.AddWallOpeningWithoutChangingOrientation(foundationId, wallPieceSection, native));
    }

    internal static class OpeningSwap
    {
        public static void Prefix(WallPieceSection section, ref BuildingBlockConfig config, ref BuildingBlockConfig state)
        {
            state = null;
            if (!StructuralGeneration.IsActive || StructuralGeneration.Retrying || config == null)
                return;
            var snap = WorkshopIndex.Current;
            if (snap == null || !snap.TryPickStructural(config, section.p1, section.length, Plugin.ReplacementChance.Value, out var replacement))
                return;
            GenerationStats.Considered++;
            state = config;
            config = replacement;
        }

        public static void Postfix(ref PlaceWallOpeningOperation result, BuildingBlockConfig native, Func<BuildingBlockConfig, PlaceWallOpeningOperation> placeNative)
        {
            if (native == null || StructuralGeneration.Retrying)
                return;
            if (result != null && result.placed)
            {
                GenerationStats.Swapped++;
                return;
            }
            Retry(native, placeNative, ref result);
        }

        public static Exception Finalizer(Exception exception, BuildingBlockConfig native, ref PlaceWallOpeningOperation result,
            Func<BuildingBlockConfig, PlaceWallOpeningOperation> placeNative)
        {
            if (exception == null || native == null || StructuralGeneration.Retrying)
                return exception;
            Plugin.Log.LogWarning($"[swap] Opening '{native.name}' replacement threw; placing native. {exception.Message}");
            Retry(native, placeNative, ref result);
            return null;
        }

        private static void Retry(BuildingBlockConfig native, Func<BuildingBlockConfig, PlaceWallOpeningOperation> placeNative, ref PlaceWallOpeningOperation result)
        {
            GenerationStats.SwapRejected++;
            StructuralGeneration.SetRetrying(true);
            try
            {
                result = placeNative(native);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[swap] Native opening '{native?.name}' also failed. {e.Message}");
            }
            finally
            {
                StructuralGeneration.SetRetrying(false);
            }
        }
    }
}
