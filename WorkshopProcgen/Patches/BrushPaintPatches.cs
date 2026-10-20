using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DungeonAlchemist;
using DungeonAlchemist.Config;
using DungeonAlchemist.Database;
using DungeonAlchemist.MapEditing;
using HarmonyLib;
using UnityEngine;
using WorkshopProcgen.MultiPaint;

namespace WorkshopProcgen.Patches
{
    /// <summary>
    /// Paint Objects tab: the built-in "Create Brush From Selected Object" toggle builds a runtime
    /// ObjectBrushConfig with one TerrainSpawnObjectFamily. While the paint set is not empty we let it
    /// run and rewrite that family's block list to the set, so DrawObjectsSpawner's weighted random pick
    /// scatters all selected assets.
    /// </summary>
    internal static class BrushPaintPatches
    {
        private static readonly List<PaintObjectsTab> tabs = new List<PaintObjectsTab>();

        private static FieldInfo lastSelectedBlockField;
        private static FieldInfo customBrushObjectField;

        // Stand-in for "selected object" so the vanilla toggle is clickable without a map selection.
        private static BuildingBlockInstanceData syntheticSelection;

        internal static void Apply(Harmony harmony)
        {
            Type tab = typeof(PaintObjectsTab);
            lastSelectedBlockField = AccessTools.Field(tab, "LastSelectedBlock");
            customBrushObjectField = AccessTools.Field(tab, "customBrushObject");
            if (lastSelectedBlockField == null || customBrushObjectField == null || !GameAccess.HasDatabaseGetter)
            {
                MultiPaintModule.Log.LogWarning("[skip] paint-tab multi brush disabled: PaintObjectsTab internals changed");
                return;
            }
            Type self = typeof(BrushPaintPatches);

            bool ok = MultiPaintModule.TryPatch(harmony,
                AccessTools.DeclaredMethod(typeof(DrawObjectsBrush), nameof(DrawObjectsBrush.Create)),
                "DrawObjectsBrush.Create", prefix: nameof(DrawObjectsBrushCreatePrefix), patchType: self);
            if (!ok)
            {
                return;
            }
            MultiPaintModule.TryPatch(harmony, AccessTools.DeclaredMethod(tab, nameof(PaintObjectsTab.Create)),
                "PaintObjectsTab.Create", postfix: nameof(TabCreatePostfix), patchType: self);
            MultiPaintModule.TryPatch(harmony, AccessTools.DeclaredMethod(tab, nameof(PaintObjectsTab.SetLastSelectedBlock)),
                "PaintObjectsTab.SetLastSelectedBlock", postfix: nameof(SetLastSelectedBlockPostfix), patchType: self);
            MultiPaintModule.TryPatch(harmony, AccessTools.DeclaredMethod(tab, nameof(PaintObjectsTab.ToggleCreateBrush)),
                "PaintObjectsTab.ToggleCreateBrush", prefix: nameof(ToggleCreateBrushPrefix), patchType: self);

            PaintSet.Changed += RefreshAllTabs;
        }

        private static bool SetActive =>
            MultiPaintConfig.IsActive && MultiPaintConfig.OverridePaintCustomBrush.Value && PaintSet.Count > 0;

        private static void TabCreatePostfix(PaintObjectsTab __instance)
        {
            tabs.RemoveAll(t => t == null);
            if (!tabs.Contains(__instance))
            {
                tabs.Add(__instance);
            }
            Refresh(__instance);
        }

        private static void SetLastSelectedBlockPostfix(PaintObjectsTab __instance)
        {
            Refresh(__instance);
        }

        private static void ToggleCreateBrushPrefix(PaintObjectsTab __instance, bool isOn)
        {
            if (!isOn || !SetActive || lastSelectedBlockField.GetValue(__instance) != null)
            {
                return;
            }
            BuildingBlockConfig first = PaintSet.Entries[0].Variant.block;
            syntheticSelection = BuildingBlockInstanceData.CreateObject(first, Vector2.zero);
            lastSelectedBlockField.SetValue(__instance, syntheticSelection);
        }

        private static void DrawObjectsBrushCreatePrefix(DrawObjectsBrush __instance, ObjectBrushConfig config)
        {
            if (!SetActive || config == null || !IsCustomBrushConfig(config))
            {
                return;
            }
            if (config.families == null || config.families.Count == 0 || config.families[0]?.family == null)
            {
                return;
            }
            DatabaseConnection database = GameAccess.Database(__instance);
            if (database == null)
            {
                return;
            }

            List<string> blockIds = PaintSet.DistinctBlockIds().Where(database.BuildingBlockConfigs.Has).ToList();
            if (blockIds.Count == 0)
            {
                return;
            }

            ObjectBrushFamily brushFamily = config.families[0];
            brushFamily.family.blocks = blockIds
                .Select(id => new TerrainSpawnBuildingBlockReference { value = id, spawnRate = 1f })
                .ToList();

            // Same spacing heuristic the vanilla custom brush uses, averaged over the set.
            float averageSize = blockIds.Average(id =>
            {
                Vector3 size = database.BuildingBlockConfigs.Get(id).GetModelBounds(database).size;
                return Mathf.Max(Mathf.Min(size.x, size.z), 0.05f);
            });
            float densityScale = 1f - Mathf.InverseLerp(0.2f, 1.5f, averageSize) * 0.9f;
            brushFamily.minDistanceBetweenObjects = averageSize * 0.5f;
            brushFamily.spawnsPerSquareMeter = densityScale * (1f / averageSize);

            MultiPaintModule.Log.LogInfo($"Paint brush uses paint set: {blockIds.Count} assets, " +
                                         $"spacing {brushFamily.minDistanceBetweenObjects:0.##}m");
        }

        private static bool IsCustomBrushConfig(ObjectBrushConfig config)
        {
            GameObject owner = config.gameObject;
            foreach (PaintObjectsTab tab in tabs)
            {
                if (tab != null && customBrushObjectField.GetValue(tab) is GameObject custom && custom == owner)
                {
                    return true;
                }
            }
            return false;
        }

        private static void RefreshAllTabs()
        {
            tabs.RemoveAll(t => t == null);
            foreach (PaintObjectsTab tab in tabs)
            {
                Refresh(tab);
            }
        }

        private static void Refresh(PaintObjectsTab tab)
        {
            if (tab == null || tab.createBrushToggle == null)
            {
                return;
            }
            object lastSelected = lastSelectedBlockField.GetValue(tab);
            if (!SetActive && lastSelected != null && ReferenceEquals(lastSelected, syntheticSelection))
            {
                lastSelectedBlockField.SetValue(tab, null);
                lastSelected = null;
            }
            tab.createBrushToggle.disabled = lastSelected == null && !SetActive;
        }
    }
}
