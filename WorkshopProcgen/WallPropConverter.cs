using System;
using System.Collections.Generic;
using DungeonAlchemist;
using DungeonAlchemist.Config;
using DungeonAlchemist.Database;
using UnityEngine;

namespace WorkshopProcgen
{
    internal enum WallPropStatus
    {
        NotAWall,
        Waiting,
        Rejected,
        Converted,
    }

    internal sealed class WallPropConversion
    {
        public WallPropStatus Status;
        public string Reason;
        public BuildingBlockConfig Config;
        public Vector2 Footprint;
        public float Length;
        public float Height;
        public float Thickness;
    }

    /// <summary>
    /// A fitted wall prop's visual transform. Published as an immutable dictionary the renderer may read.
    /// </summary>
    internal sealed class WallFit
    {
        public Vector3 Scale;
        public Quaternion Rotation;
        public Vector3 Position;
        public Vector3 Center;
        public Vector3 Size;
    }

    /// <summary>
    /// Turns a subscribed object that is already a single wall panel into a temporary Wall block.
    /// The original object config is left in place. The mesh is moved onto the game's 1 m wall segment
    /// when that wall block is drawn, which is also the space door and window holes are cut in.
    /// </summary>
    internal static class WallPropConverter
    {
        public const string IdSuffix = "#wall";

        private const float SegmentLength = 1f;
        private const float MinLength = 0.45f;
        private const float MaxLength = 1.65f;
        private const float MinHeight = 0.35f;
        private const float MaxHeight = 3.2f;
        private const float PartialBelow = 1.2f;
        private const float MaxThicknessRatio = 0.75f;
        private const float MinThickness = 0.002f;
        private const int MaxBoundRetries = 8;

        private static readonly HashSet<string> Registered = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Session = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> BoundRetries = new Dictionary<string, int>(StringComparer.Ordinal);

        private static Dictionary<string, WallFit> _fits = new Dictionary<string, WallFit>(StringComparer.Ordinal);
        private static Dictionary<string, WallFit> _pending = new Dictionary<string, WallFit>(StringComparer.Ordinal);

        public static bool WantsRetry { get; private set; }

        public static string WallId(string sourceId) => sourceId + IdSuffix;

        public static string StructuralId(string sourceId, StructuralRole role)
        {
            switch (role)
            {
                case StructuralRole.Floor: return sourceId + "#floor";
                case StructuralRole.Door: return sourceId + "#door";
                case StructuralRole.Window: return sourceId + "#window";
                default: return sourceId + IdSuffix;
            }
        }

        public static bool IsConvertedId(string id)
        {
            return id != null && (id.EndsWith(IdSuffix, StringComparison.Ordinal)
                || id.EndsWith("#floor", StringComparison.Ordinal)
                || id.EndsWith("#door", StringComparison.Ordinal)
                || id.EndsWith("#window", StringComparison.Ordinal));
        }

        public static bool TryGetFit(string blockId, out WallFit fit)
        {
            fit = null;
            return blockId != null && _fits.TryGetValue(blockId, out fit);
        }

        public static void Apply(Transform transform, string blockId)
        {
            if (transform == null || !TryGetFit(blockId, out var fit))
                return;
            transform.localScale = fit.Scale;
            transform.localRotation = fit.Rotation;
            transform.localPosition = fit.Position;
        }

        public static void Begin()
        {
            WantsRetry = false;
            Session.Clear();
            _pending = new Dictionary<string, WallFit>(StringComparer.Ordinal);
        }

        public static void Finish(DatabaseConnection db)
        {
            _fits = _pending ?? new Dictionary<string, WallFit>(StringComparer.Ordinal);
            _pending = new Dictionary<string, WallFit>(StringComparer.Ordinal);
            if (db?.BuildingBlockConfigs == null)
                return;
            var stale = new List<string>();
            foreach (var id in Registered)
            {
                if (!Session.Contains(id))
                    stale.Add(id);
            }
            foreach (var id in stale)
            {
                Registered.Remove(id);
                BoundRetries.Remove(id);
                try
                {
                    if (db.BuildingBlockConfigs.Has(id))
                        db.BuildingBlockConfigs.RemoveTemporaryConfig(id);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[wall] Could not drop converted wall {id}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// forced None means automatic: only a wall-named single panel. Any other role is an explicit
        /// assignment and is fitted even when the mesh would not have qualified on its own.
        /// </summary>
        public static WallPropConversion TryConvert(DatabaseConnection db, BuildingBlockConfig source, StructuralRole forced)
        {
            var result = new WallPropConversion { Status = WallPropStatus.NotAWall };
            if (source == null || source.type != BuildingBlockType.Object || string.IsNullOrEmpty(source.Id))
                return result;
            if (IsConvertedId(source.Id))
                return result;
            bool auto = forced == StructuralRole.None;
            if (auto && !NamedLikeWall(source.name, source.keywords))
                return result;

            Bounds bounds;
            try
            {
                bounds = db.ModelStaticDataTable.Get(source.modelId).GetStateIndependentMeshBounds();
            }
            catch (Exception e)
            {
                return Waiting(source, "mesh is not loaded yet (" + e.Message + ")");
            }

            Vector3 size = bounds.size;
            if (size.x < 0.0001f || size.y < 0.0001f || size.z < 0.0001f
                || float.IsNaN(size.x) || float.IsNaN(size.y) || float.IsNaN(size.z))
            {
                return Waiting(source, "mesh bounds are not ready");
            }

            int thin = SmallestAxis(size);
            int heightAxis;
            int lengthAxis;
            if (thin == 1)
            {
                // Flat on the ground: stand the longer face axis up.
                if (size.x >= size.z)
                {
                    heightAxis = 0;
                    lengthAxis = 2;
                }
                else
                {
                    heightAxis = 2;
                    lengthAxis = 0;
                }
            }
            else
            {
                heightAxis = 1;
                lengthAxis = thin == 0 ? 2 : 0;
            }

            float length = size[lengthAxis];
            float height = size[heightAxis];
            float thickness = size[thin];
            result.Length = length;
            result.Height = height;
            result.Thickness = thickness;

            float face = Mathf.Min(length, height);
            if (auto && (thickness < MinThickness || face < 0.01f || thickness / face > MaxThicknessRatio))
            {
                result.Status = WallPropStatus.Rejected;
                result.Reason = $"it is {length:0.##}×{height:0.##}×{thickness:0.##} m, which is not a flat wall panel";
                return result;
            }
            if (auto && (length < MinLength || length > MaxLength))
            {
                result.Status = WallPropStatus.Rejected;
                result.Reason = $"it runs {length:0.##} m along the wall; a real segment is about {SegmentLength:0.#} m";
                return result;
            }
            if (auto && (height < MinHeight || height > MaxHeight))
            {
                result.Status = WallPropStatus.Rejected;
                result.Reason = $"it is {height:0.##} m tall, outside a single story";
                return result;
            }

            StructuralRole role;
            int xAxis = lengthAxis;
            int yAxis = heightAxis;
            int zAxis = thin;
            float targetX = SegmentLength;
            float targetY;
            float targetZ = StandardMeasurements.wallThickness;
            if (forced == StructuralRole.Floor)
            {
                role = StructuralRole.Floor;
                int faceA;
                int faceB;
                ChooseOtherAxes(thin, out faceA, out faceB);
                xAxis = faceA;
                yAxis = thin;
                zAxis = faceB;
                targetX = SegmentLength;
                targetY = 0.05f;
                targetZ = SegmentLength;
            }
            else if (forced == StructuralRole.PartialWall)
            {
                role = StructuralRole.PartialWall;
                targetY = Mathf.Min(height, PartialBelow);
            }
            else if (forced == StructuralRole.Door || forced == StructuralRole.Window)
            {
                role = forced;
                targetY = StandardMeasurements.wallHeight;
            }
            else if (forced == StructuralRole.Wall)
            {
                role = StructuralRole.Wall;
                targetY = StandardMeasurements.wallHeight;
            }
            else
            {
                bool partial = height < PartialBelow;
                role = partial ? StructuralRole.PartialWall : StructuralRole.Wall;
                targetY = partial ? height : StandardMeasurements.wallHeight;
            }

            var fit = BuildFit(bounds.center, size, xAxis, yAxis, zAxis, targetX, targetY, targetZ);
            var wall = CreateConfig(source, role);

            try
            {
                if (db.BuildingBlockConfigs.Has(wall.Id))
                    db.BuildingBlockConfigs.UpdateWithoutSave(wall);
                else
                    db.BuildingBlockConfigs.AddTemporaryConfig(wall);
            }
            catch (Exception e)
            {
                result.Status = WallPropStatus.Rejected;
                result.Reason = "the wall block could not be registered (" + e.Message + ")";
                return result;
            }

            _pending[wall.Id] = fit;
            Registered.Add(wall.Id);
            Session.Add(wall.Id);
            BoundRetries.Remove(source.Id);

            result.Status = WallPropStatus.Converted;
            result.Config = wall;
            result.Footprint = role == StructuralRole.Floor
                ? new Vector2(SegmentLength, SegmentLength)
                : new Vector2(SegmentLength, targetY);
            result.Height = targetY;
            Plugin.Log.LogInfo($"[wall] '{source.name}' registered as a {WorkshopIndex.RoleLabel(role)} " +
                               $"({length:0.##}×{height:0.##}×{thickness:0.##} m fitted to {targetX:0.##}×{targetY:0.##}×{targetZ:0.##} m).");
            return result;
        }

        private static WallPropConversion Waiting(BuildingBlockConfig source, string reason)
        {
            int n = 0;
            BoundRetries.TryGetValue(source.Id, out n);
            n++;
            BoundRetries[source.Id] = n;
            if (n <= MaxBoundRetries)
            {
                WantsRetry = true;
                return new WallPropConversion { Status = WallPropStatus.Waiting, Reason = reason };
            }
            return new WallPropConversion
            {
                Status = WallPropStatus.Rejected,
                Reason = reason,
            };
        }

        private static BuildingBlockConfig CreateConfig(BuildingBlockConfig source, StructuralRole role)
        {
            var wall = source.Clone();
            wall.Id = StructuralId(source.Id, role);
            wall.pillarId = null;
            wall.categoryId = null;
            wall.objectCategory = ObjectCategory.Normal;
            wall.wallMountType = WallMountType.No;
            wall.scale = 1f;
            wall.rotation = 0f;
            wall.offset = Vector3.zero;
            wall.automaticType = AutomaticBuildingBlockType.None;
            wall.deprecated = false;
            wall.displayCategoryIds = new List<string>();
            if (role == StructuralRole.Floor)
            {
                wall.type = BuildingBlockType.Floor;
            }
            else if (role == StructuralRole.Door || role == StructuralRole.Window)
            {
                wall.type = BuildingBlockType.WallOpening;
                wall.wallOpeningCategory = role == StructuralRole.Window
                    ? WallOpeningCategory.Window
                    : WallOpeningCategory.Door;
                wall.wallOpeningLength = 1;
            }
            else
            {
                wall.type = BuildingBlockType.Wall;
                wall.wallCategory = role == StructuralRole.PartialWall
                    ? WallCategory.PartialWall
                    : WallCategory.Wall;
            }
            return wall;
        }

        private static void ChooseOtherAxes(int taken, out int a, out int b)
        {
            if (taken == 0)
            {
                a = 1;
                b = 2;
            }
            else if (taken == 1)
            {
                a = 0;
                b = 2;
            }
            else
            {
                a = 0;
                b = 1;
            }
        }

        private static WallFit BuildFit(Vector3 center, Vector3 size, int xAxis, int yAxis, int zAxis,
            float targetX, float targetY, float targetZ)
        {
            var scale = Vector3.one;
            scale[xAxis] = targetX / Mathf.Max(size[xAxis], 0.0001f);
            scale[yAxis] = targetY / Mathf.Max(size[yAxis], 0.0001f);
            scale[zAxis] = targetZ / Mathf.Max(size[zAxis], 0.0001f);

            Vector3 xDir = Axis(xAxis);
            Vector3 yDir = Axis(yAxis);
            Vector3 zDir = Axis(zAxis);
            float sign = Vector3.Dot(Vector3.Cross(xDir, yDir), zDir) >= 0f ? 1f : -1f;

            var rot = Matrix4x4.identity;
            rot.SetColumn(xAxis, new Vector4(1f, 0f, 0f, 0f));
            rot.SetColumn(yAxis, new Vector4(0f, 1f, 0f, 0f));
            rot.SetColumn(zAxis, new Vector4(0f, 0f, sign, 0f));
            Quaternion rotation = rot.rotation;

            var pivot = center;
            pivot[yAxis] = center[yAxis] - size[yAxis] * 0.5f;
            Vector3 position = rotation * Vector3.Scale(scale, -pivot);
            var fittedSize = new Vector3(targetX, targetY, targetZ);

            return new WallFit
            {
                Scale = scale,
                Rotation = rotation,
                Position = position,
                Center = new Vector3(0f, targetY * 0.5f, 0f),
                Size = fittedSize,
            };
        }

        private static int SmallestAxis(Vector3 size)
        {
            if (size.x <= size.y && size.x <= size.z)
                return 0;
            if (size.y <= size.x && size.y <= size.z)
                return 1;
            return 2;
        }

        private static Vector3 Axis(int axis)
        {
            if (axis == 0)
                return Vector3.right;
            if (axis == 1)
                return Vector3.up;
            return Vector3.forward;
        }

        private static bool NamedLikeWall(string name, List<string> keywords)
        {
            if (HasWallToken(name))
                return true;
            if (keywords == null)
                return false;
            for (int i = 0; i < keywords.Count; i++)
            {
                if (HasWallToken(keywords[i]))
                    return true;
            }
            return false;
        }

        private static bool HasWallToken(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && !char.IsLetter(text[i]))
                    i++;
                int start = i;
                while (i < text.Length && char.IsLetter(text[i]))
                    i++;
                int length = i - start;
                if (length == 4 && string.Compare(text, start, "wall", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
                if (length == 5 && string.Compare(text, start, "walls", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
                // LogWall, StoneWall. A lowercase ending such as "swallow" is not a wall name.
                if (length > 4 && char.IsUpper(text[i - 4])
                    && string.Compare(text, i - 4, "wall", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
            }
            return false;
        }
    }
}
