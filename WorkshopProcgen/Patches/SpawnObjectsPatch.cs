using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DungeonAlchemist;
using DungeonAlchemist.Config;
using DungeonAlchemist.Data;
using DungeonAlchemist.Database;
using DungeonAlchemist.MapEditing;
using DungeonAlchemist.RoomGeneration;
using HarmonyLib;
using UnityEngine;

namespace WorkshopProcgen.Patches
{
    /// <summary>
    /// Replaces the commit loop of MapManipulatorRoomGenratorTarget.SpawnObjects. The generator only ever sees
    /// native ids; swaps happen here on the real (post-simulation) placement, with a collision-checked attempt
    /// and a fallback to the original native object.
    /// </summary>
    [HarmonyPatch]
    internal static class SpawnObjectsPatch
    {
        private static MethodBase _target;
        private static AccessTools.FieldRef<MapManipulatorRoomGenratorTarget, DatabaseConnection> _databaseRef;
        private static AccessTools.FieldRef<MapManipulatorRoomGenratorTarget, MapManipulator> _mapManipulatorRef;
        private static bool _loggedChooseError;

        private static bool Prepare()
        {
            if (_target != null)
                return true;
            try
            {
                _databaseRef = AccessTools.FieldRefAccess<MapManipulatorRoomGenratorTarget, DatabaseConnection>("database");
                _mapManipulatorRef = AccessTools.FieldRefAccess<MapManipulatorRoomGenratorTarget, MapManipulator>("mapManipulator");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"MapManipulatorRoomGenratorTarget fields not found ({e.Message}); generator swap disabled.");
                return false;
            }
            _target = PatchTargets.Resolve(typeof(MapManipulatorRoomGenratorTarget), nameof(MapManipulatorRoomGenratorTarget.SpawnObjects));
            return _target != null;
        }

        private static MethodBase TargetMethod() => _target;

        private static bool Prefix(MapManipulatorRoomGenratorTarget __instance, GeneratedObjectData generatedObjects)
        {
            TraceLog.Write($"hook: SpawnObjects with {generatedObjects?.objects?.Count ?? -1} objects; index={(WorkshopIndex.Current == null ? "null" : WorkshopIndex.Current.Entries.Count.ToString())}");
            if (generatedObjects?.objects == null)
                return true;

            bool verbose = Plugin.VerboseLogging.Value;
            bool force = Plugin.DebugForceScifiDesk.Value;
            if (!Plugin.Enabled.Value && !force)
                return true;

            var snap = WorkshopIndex.Current;
            if (snap == null)
            {
                if (verbose)
                    Plugin.Log.LogInfo("[swap] Workshop index not built yet; placing native objects only.");
                return true;
            }
            if (!force && !verbose && snap.Entries.Count == 0 && snap.Forced.Count == 0)
                return true;

            var db = _databaseRef(__instance);
            var objectManipulator = _mapManipulatorRef(__instance)?.objectManipulator;
            if (db == null || objectManipulator == null)
                return true;

            float tolerance = Plugin.FootprintTolerance.Value;
            float chance = Plugin.ReplacementChance.Value;
            var objects = generatedObjects.objects;

            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                if (obj?.template?.source == null || obj.surface == null)
                {
                    PlaceNative(db, objectManipulator, obj);
                    continue;
                }

                BuildingBlockInstanceDataTemplate replacement = null;
                try
                {
                    replacement = ChooseReplacement(db, snap, objects, i, force, tolerance, chance, verbose);
                }
                catch (Exception e)
                {
                    if (!_loggedChooseError || verbose)
                        Plugin.Log.LogError($"[swap] Error choosing replacement for {obj.blockId}: {e}");
                    _loggedChooseError = true;
                }

                if (replacement != null && TryPlace(db, objectManipulator, obj, replacement, verbose))
                {
                    GenerationStats.Swapped++;
                    continue;
                }
                PlaceNative(db, objectManipulator, obj);
            }
            return false;
        }

        private static BuildingBlockInstanceDataTemplate ChooseReplacement(DatabaseConnection db, IndexSnapshot snap,
            List<GeneratedObjectCandidate> objects, int index, bool force, float tolerance, float defaultChance, bool verbose)
        {
            var obj = objects[index];
            var template = obj.template;
            var src = template.source;
            GenerationStats.Considered++;

            var native = db.BuildingBlockConfigs.GetOrNull(src.blockId);
            if (native == null || snap.ImportedBlockIds.Contains(native.Id))
                return null;
            if (native.type != BuildingBlockType.Object || native.automaticType != AutomaticBuildingBlockType.None
                || native.IsToken() || native.IsDecal() || native.IsOverlay())
                return null;

            string desc = verbose ? Describe(db, native, obj) : null;

            WorkshopEntry[] candidates;
            float chance;
            if (force)
            {
                if (snap.ScifiDeskId == null || !snap.IsTable(native))
                {
                    if (verbose) Plugin.Log.LogInfo($"[swap] {desc} -> native (debug ScifiDesk mode: not a table or desk missing)");
                    return null;
                }
                candidates = new[] { new WorkshopEntry { Id = snap.ScifiDeskId, Name = "ScifiDesk (debug)" } };
                chance = 1f;
            }
            else
            {
                bool forced = snap.IsForced(native.Id);
                if (!forced && native.IsInLightCategory(db))
                {
                    if (verbose) Plugin.Log.LogInfo($"[swap] {desc} -> native (light)");
                    return null;
                }
                candidates = snap.GetCandidates(native, tolerance);
                if (candidates.Length == 0)
                {
                    if (verbose) Plugin.Log.LogInfo($"[swap] {desc} -> native (no matching imported asset)");
                    return null;
                }
                chance = 0f;
                for (int c = 0; c < candidates.Length; c++)
                {
                    float rate = candidates[c].Chance ?? snap.GetChance(native, defaultChance);
                    if (rate > chance)
                        chance = rate;
                }
            }

            if (!snap.SwapObjectsWithItemsOnTop && HasItemsOnTop(db, native, objects, index))
            {
                GenerationStats.SkippedOnTop++;
                if (verbose) Plugin.Log.LogInfo($"[swap] {desc} -> native (has generated items on top)");
                return null;
            }

            var rng = ReplacementRng.ForObject(native.Id, obj.surface.mapPosition, src.rotation);
            double roll = rng.NextDouble();
            var pick = PickByRate(candidates, rng, snap, native, defaultChance);
            if (roll >= chance)
            {
                if (verbose) Plugin.Log.LogInfo($"[swap] {desc} -> native (roll {roll:F3} >= chance {chance:F2}; {candidates.Length} candidates)");
                return null;
            }
            if (db.BuildingBlockConfigs.GetOrNull(pick.Id) == null)
                return null;

            if (verbose)
                Plugin.Log.LogInfo($"[swap] {desc} -> {pick} (roll {roll:F3} < chance {chance:F2}; {candidates.Length} candidates)");

            var swappedSource = src.UpdateBlockId(pick.Id).UpdateModifiers(new Dictionary<string, string>());
            float fit = FitScale(db, native, pick);
            if (fit < 1f)
            {
                swappedSource = swappedSource.UpdateScale(db, src.scale * fit, overrideScaleLimits: true);
                if (verbose)
                    Plugin.Log.LogInfo($"[swap]   scaled {pick.Name} by {fit:F2} to fit the native footprint");
            }
            return new BuildingBlockInstanceDataTemplate(swappedSource, null, -1)
            {
                randSeed = template.randSeed,
                allowFloatOnWater = template.allowFloatOnWater,
                ignoreObjectsOnObjectsInVariant = template.ignoreObjectsOnObjectsInVariant,
            };
        }

        private static WorkshopEntry PickByRate(WorkshopEntry[] candidates, System.Random rng, IndexSnapshot snap,
            BuildingBlockConfig native, float defaultChance)
        {
            double total = 0;
            for (int i = 0; i < candidates.Length; i++)
                total += Mathf.Clamp01(candidates[i].Chance ?? snap.GetChance(native, defaultChance));
            if (total <= 0)
                return candidates[candidates.Length - 1];
            double cursor = rng.NextDouble() * total;
            for (int i = 0; i < candidates.Length; i++)
            {
                cursor -= Mathf.Clamp01(candidates[i].Chance ?? snap.GetChance(native, defaultChance));
                if (cursor <= 0)
                    return candidates[i];
            }
            return candidates[candidates.Length - 1];
        }

        // Native props are often placed flush against walls, so a replacement must not exceed the native footprint.
        private static float FitScale(DatabaseConnection db, BuildingBlockConfig native, WorkshopEntry pick)
        {
            if (pick.Footprint.x <= 0f || pick.Footprint.y <= 0f || !WorkshopIndex.TryGetFootprint(native, db, out var fp))
                return 1f;
            return Mathf.Min(1f, fp.x / pick.Footprint.x, fp.y / pick.Footprint.y);
        }

        private static bool HasItemsOnTop(DatabaseConnection db, BuildingBlockConfig native, List<GeneratedObjectCandidate> objects, int index)
        {
            var self = objects[index].surface;
            if (!WorkshopIndex.TryGetFootprint(native, db, out var fp))
                return false;
            float radius = Mathf.Max(fp.x, fp.y) * 0.5f * Mathf.Max(objects[index].template.source.scale, 0.01f) + 0.05f;
            for (int j = 0; j < objects.Count; j++)
            {
                if (j == index)
                    continue;
                var other = objects[j]?.surface;
                if (other == null || !other.onObject || other.height <= self.height + 0.01f)
                    continue;
                if (Vector2.Distance(other.mapPosition, self.mapPosition) <= radius)
                    return true;
            }
            return false;
        }

        private static bool TryPlace(DatabaseConnection db, ObjectManipulator om, GeneratedObjectCandidate obj,
            BuildingBlockInstanceDataTemplate replacement, bool verbose)
        {
            AddObjectOperation op = null;
            try
            {
                op = om.AddObjectWithoutManipulationWithCollision(obj.surface, replacement, obj.clipToGrid);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[swap] Placing {replacement.source.blockId} threw; falling back to native. {e.Message}");
            }

            if (op != null && op.result == PlaceObjectOperationResult.Success)
                return true;

            GenerationStats.SwapRejected++;
            if (op?.addedInstance != null)
            {
                try
                {
                    om.DeleteObject(op.addedInstance);
                }
                catch
                {
                    // Already removed by the failed placement.
                }
            }
            if (verbose)
                Plugin.Log.LogInfo($"[swap]   replacement {db.BuildingBlockConfigs.GetOrNull(replacement.source.blockId)?.name} did not fit ({op?.result.ToString() ?? "exception"}); placing native.");
            return false;
        }

        private static void PlaceNative(DatabaseConnection db, ObjectManipulator om, GeneratedObjectCandidate obj)
        {
            if (obj?.template == null)
                return;
            var op = om.AddObjectWithoutManipulationWithCollision(obj.surface, obj.template, obj.clipToGrid);
            if (op.result != PlaceObjectOperationResult.Success)
                Debug.LogError("Tried to place invalid object " + db.BuildingBlockConfigs.Get(obj.template.source.blockId)?.ToString()
                               + " at " + obj.surface?.ToString() + ": " + op.result);
        }

        private static string Describe(DatabaseConnection db, BuildingBlockConfig native, GeneratedObjectCandidate obj)
        {
            var cats = string.Join(", ", WorkshopIndex.SafeNormalCategories(native, db).Select(c => db.DisplayCategories.GetOrNull(c)?.name ?? c));
            string fp = WorkshopIndex.TryGetFootprint(native, db, out var f) ? $"{f.x:F2}x{f.y:F2}" : "?";
            return $"'{native.name}' ({native.Id}) wall={native.wallMountType} footprint={fp} categories=[{cats}] " +
                   $"at {obj.surface.mapPosition} rot={obj.template.source.rotation:F0} onObject={obj.surface.onObject} variantSet={obj.template.variantSetId ?? "-"}";
        }
    }
}
