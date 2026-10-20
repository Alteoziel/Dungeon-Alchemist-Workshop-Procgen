using System.Collections;
using System.Reflection;
using DungeonAlchemist;
using DungeonAlchemist.Config;
using DungeonAlchemist.Database;
using DungeonAlchemist.MapEditing;
using HarmonyLib;
using UnityEngine;

namespace WorkshopProcgen.MultiPaint
{
    /// <summary>
    /// Replaces the private <c>template</c> of a <see cref="PlaceObjectBrush"/> with a random paint set member,
    /// mirroring what <c>PlaceObjectBrush.Create(BuildingBlockInstanceData, ...)</c> does for a fresh block.
    /// </summary>
    internal static class PlaceBrushSwapper
    {
        private static FieldInfo templateField;
        private static FieldInfo mapEditorField;
        private static FieldInfo allowRandomScaleField;
        private static FieldInfo randomRotationOffsetField;
        private static FieldInfo variantQueueField;
        private static FieldInfo dragSurfacesField;
        private static FieldInfo lastValidOpField;

        /// <summary>The brush that was started from a paint set member (only one place brush exists at a time).</summary>
        internal static PlaceObjectBrush ActiveBrush;

        internal static bool Resolve()
        {
            templateField = Field("template");
            mapEditorField = Field("mapEditor");
            allowRandomScaleField = Field("allowRandomScale");
            randomRotationOffsetField = Field("randomRotationOffset");
            variantQueueField = Field("variantQueue");
            dragSurfacesField = Field("dragSurfaces");
            lastValidOpField = Field("lastValidOp");
            if (!GameAccess.HasDatabaseGetter)
            {
                MultiPaintModule.Log.LogWarning("[skip] DatabaseAutoInit.database getter not found");
            }
            return templateField != null && mapEditorField != null && GameAccess.HasDatabaseGetter;
        }

        private static FieldInfo Field(string name)
        {
            FieldInfo field = AccessTools.Field(typeof(PlaceObjectBrush), name);
            if (field == null)
            {
                MultiPaintModule.Log.LogWarning($"[skip] PlaceObjectBrush.{name} not found");
            }
            return field;
        }

        internal static bool IsMultiBrush(PlaceObjectBrush brush)
        {
            return MultiPaintConfig.IsActive
                   && brush != null
                   && ReferenceEquals(ActiveBrush, brush)
                   && PaintSet.Count >= 2;
        }

        internal static int DragSurfaceCount(PlaceObjectBrush brush)
        {
            return dragSurfacesField?.GetValue(brush) is ICollection list ? list.Count : 0;
        }

        internal static bool HasLastValidOp(PlaceObjectBrush brush)
        {
            return lastValidOpField?.GetValue(brush) != null;
        }

        internal static void SwapToRandom(PlaceObjectBrush brush)
        {
            PaintSetEntry entry = PaintSet.PickRandom();
            if (entry == null || templateField == null)
            {
                return;
            }
            DatabaseConnection database = GameAccess.Database(brush);
            var mapEditor = mapEditorField?.GetValue(brush) as MapEditor;
            if (database == null || mapEditor == null || !database.BuildingBlockConfigs.Has(entry.BlockId))
            {
                return;
            }

            BuildingBlockConfig config = database.BuildingBlockConfigs.Get(entry.BlockId);
            var previous = templateField.GetValue(brush) as BuildingBlockInstanceDataTemplate;
            string variantSetId = entry.Variant.variantId;
            if (variantSetId != null && !database.BuildingBlockVariants.Has(variantSetId))
            {
                variantSetId = null;
            }

            BuildingBlockInstanceData data = BuildingBlockInstanceData.CreateObject(config, Vector2.zero);
            var template = new BuildingBlockInstanceDataTemplate(data, variantSetId, RandomUtility.GetInt(0, 5000))
            {
                randSeed = RandomUtility.GetInt(),
                copyModifiersFromVariant = true,
                allowFloatOnWater = previous?.allowFloatOnWater ?? true
            };
            bool allowRandomScale = Mathf.Approximately(template.source.scale, 1f);
            template.source = template.source.ApplyRandomization(database, RandomUtility.GetStaticRand(),
                mapEditor.GetClipToGridMode(), allowRandomScale);

            templateField.SetValue(brush, template);
            allowRandomScaleField?.SetValue(brush, allowRandomScale);
            randomRotationOffsetField?.SetValue(brush, template.source.rotation);
            variantQueueField?.SetValue(brush, null);
        }
    }
}
