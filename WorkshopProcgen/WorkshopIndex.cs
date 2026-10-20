using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DungeonAlchemist;
using DungeonAlchemist.Config;
using DungeonAlchemist.Database;
using HarmonyLib;
using UnityEngine;

namespace WorkshopProcgen
{
    internal sealed class WorkshopEntry
    {
        public string Id;
        public string Name;
        public string ModelId;
        public string ImportedModelId;
        public ulong PublishedId;
        public bool Local;
        public WallMountType WallMount;
        public string[] Categories;
        public Vector2 Footprint;
        public float? Chance;

        public override string ToString() => $"'{Name}' ({Id})";
    }

    internal sealed class CatalogAsset
    {
        public string Id;
        public string Name;
        public ulong PublishedId;
        public bool Local;
        public Vector2 Footprint;
        public string[] AutoCategories;
        public string[] Categories;
        public bool Enabled;
        public float? Chance;
        public bool UsingCustomCategories;
    }

    /// <summary>Immutable once published; the only index data the generation thread may read.</summary>
    internal sealed class IndexSnapshot
    {
        public DatabaseConnection Db;
        public int Version;
        public List<WorkshopEntry> Entries;
        public Dictionary<string, List<WorkshopEntry>> ByCategory;
        public HashSet<string> ImportedBlockIds;
        public Dictionary<string, WorkshopEntry[]> Forced;
        public Dictionary<string, float> CategoryChance;
        public float? ForcedChance;
        public bool AutoMatch;
        public bool SwapObjectsWithItemsOnTop;
        public string ScifiDeskId;
        public HashSet<string> TableCategoryIds;
        public List<CatalogAsset> Catalog;
        public string[] ObjectCategoryNames;

        private readonly ConcurrentDictionary<(string, float), WorkshopEntry[]> _matchCache =
            new ConcurrentDictionary<(string, float), WorkshopEntry[]>();

        public bool IsForced(string nativeId) => Forced.ContainsKey(nativeId);

        public WorkshopEntry[] GetCandidates(BuildingBlockConfig native, float tolerance)
        {
            if (Forced.TryGetValue(native.Id, out var forced))
                return forced;
            if (!AutoMatch || Entries.Count == 0)
                return Array.Empty<WorkshopEntry>();
            return _matchCache.GetOrAdd((native.Id, tolerance), _ => ComputeMatches(native, tolerance));
        }

        public float GetChance(BuildingBlockConfig native, float defaultChance)
        {
            if (ForcedChance.HasValue && Forced.ContainsKey(native.Id))
                return ForcedChance.Value;
            if (CategoryChance.Count == 0)
                return defaultChance;
            float? best = null;
            foreach (var cat in WorkshopIndex.SafeNormalCategories(native, Db))
            {
                if (CategoryChance.TryGetValue(cat, out var c) && (!best.HasValue || c > best.Value))
                    best = c;
            }
            return best ?? defaultChance;
        }

        public bool IsTable(BuildingBlockConfig native)
        {
            if (native.name != null && native.name.IndexOf("table", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return WorkshopIndex.SafeNormalCategories(native, Db).Any(TableCategoryIds.Contains);
        }

        private WorkshopEntry[] ComputeMatches(BuildingBlockConfig native, float tolerance)
        {
            var cats = WorkshopIndex.SafeNormalCategories(native, Db);
            if (cats.Length == 0)
                return Array.Empty<WorkshopEntry>();
            if (!WorkshopIndex.TryGetFootprint(native, Db, out var nativeFp))
                return Array.Empty<WorkshopEntry>();

            var result = new List<WorkshopEntry>();
            foreach (var cat in cats)
            {
                if (!ByCategory.TryGetValue(cat, out var list))
                    continue;
                foreach (var e in list)
                {
                    if (!WallCompatible(e.WallMount, native.wallMountType) || result.Contains(e))
                        continue;
                    if (Fits(e.Footprint, nativeFp, tolerance))
                        result.Add(e);
                }
            }
            return result.ToArray();
        }

        // ForceToWall objects only make sense on walls; free-standing and wall-clipping objects are interchangeable.
        private static bool WallCompatible(WallMountType candidate, WallMountType native)
            => (candidate == WallMountType.ForceToWall) == (native == WallMountType.ForceToWall);

        // Smaller assets keep their own size and sit in the native slot. Larger ones are rejected here;
        // FitScale shrinks anything that is only slightly larger than the slot.
        private static bool Fits(Vector2 candidate, Vector2 native, float tolerance)
        {
            if (candidate.x <= 0.01f || candidate.y <= 0.01f)
                return false;
            return candidate.x <= native.x * (1f + tolerance) && candidate.y <= native.y * (1f + tolerance);
        }
    }

    internal static class WorkshopIndex
    {
        public const string ScifiDeskModelId = "e944764a-79e4-49eb-afee-9072824845e5";

        private static volatile IndexSnapshot _current;
        private static volatile DatabaseConnection _db;
        private static volatile bool _dirty;
        private static int _changeCount;
        private static int _version;

        public static IndexSnapshot Current => _current;

        public static void Initialize()
        {
            var go = new GameObject("WorkshopProcgen.IndexUpdater");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<IndexUpdater>();
            go.AddComponent<RulesMenu>();
        }

        public static void RegisterDatabase(DatabaseConnection db)
        {
            if (db == null)
                return;
            _db = db;
            Plugin.Log.LogInfo("DatabaseConnection captured; Workshop index will build once imported models are loaded.");
        }

        /// <summary>Called from any thread when imported configs change.</summary>
        public static void MarkDirty(DatabaseConnection db)
        {
            if (db != null)
                _db = db;
            System.Threading.Interlocked.Increment(ref _changeCount);
            _dirty = true;
        }

        public static void RequestRebuild() => MarkDirty(null);

        internal static string[] SafeNormalCategories(BuildingBlockConfig config, DatabaseConnection db)
        {
            try
            {
                return config.GetNormalDisplayCategories(db).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        internal static bool TryGetFootprint(BuildingBlockConfig config, DatabaseConnection db, out Vector2 footprint)
        {
            footprint = default;
            try
            {
                footprint = config.GetModelbounds2D(db).size;
            }
            catch
            {
                return false;
            }
            return footprint.x > 0.01f && footprint.y > 0.01f;
        }

        private sealed class IndexUpdater : MonoBehaviour
        {
            private const float Debounce = 1.5f;
            private const float FilePollInterval = 2f;

            private float _nextFilePoll;
            private int _seenChangeCount;
            private float _lastChangeTime;
            private long _importedSignature;

            private static AccessTools.FieldRef<DatabaseLoader, DatabaseConnection> _loaderDatabaseRef;
            private bool _loaderLookupFailed;

            private void TryFindDatabase()
            {
                if (_loaderLookupFailed)
                    return;
                try
                {
                    _loaderDatabaseRef ??= AccessTools.FieldRefAccess<DatabaseLoader, DatabaseConnection>("database");
                    var loader = FindObjectOfType<DatabaseLoader>();
                    var db = loader != null ? _loaderDatabaseRef(loader) : null;
                    if (db != null)
                        RegisterDatabase(db);
                }
                catch (Exception e)
                {
                    _loaderLookupFailed = true;
                    Plugin.Log.LogWarning($"Could not read DatabaseLoader.database: {e.Message}");
                }
            }

            private static long ImportedSignature(DatabaseConnection db)
            {
                var perModel = db?.NonDeprecatedBuildingBlockConfigsPerImportedModel;
                if (perModel == null)
                    return 0;
                long sig = perModel.Count;
                foreach (var list in perModel.Values)
                    sig = sig * 31 + (list?.Count ?? 0);
                return sig;
            }
            private DateTime _overrideStamp = DateTime.MinValue;
            private OverrideConfig _overrides;

            private void Start()
            {
                _overrides = OverrideConfig.LoadOrCreate();
                _overrideStamp = OverrideConfig.GetLastWriteTimeUtc();
                Plugin.Log.LogInfo($"Workshop index updater running; overrides from {OverrideConfig.FilePath}");
            }

            private void Update()
            {
                float now = Time.realtimeSinceStartup;

                if (now >= _nextFilePoll)
                {
                    _nextFilePoll = now + FilePollInterval;
                    if (_db == null)
                        TryFindDatabase();

                    var signature = ImportedSignature(_db);
                    if (signature != _importedSignature)
                    {
                        _importedSignature = signature;
                        MarkDirty(null);
                    }

                    var stamp = OverrideConfig.GetLastWriteTimeUtc();
                    if (stamp != _overrideStamp)
                    {
                        _overrideStamp = stamp;
                        _overrides = OverrideConfig.LoadOrCreate();
                        Plugin.Log.LogInfo("Override file changed; rebuilding Workshop index.");
                        _dirty = true;
                    }
                }

                int changes = System.Threading.Volatile.Read(ref _changeCount);
                if (changes != _seenChangeCount)
                {
                    _seenChangeCount = changes;
                    _lastChangeTime = now;
                }

                if (!_dirty || _db == null || now - _lastChangeTime < Debounce)
                    return;

                _dirty = false;
                try
                {
                    _overrides = OverrideConfig.LoadOrCreate();
                    _overrideStamp = OverrideConfig.GetLastWriteTimeUtc();
                    _current = Build(_db, _overrides ?? new OverrideConfig());
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Workshop index build failed: {e}");
                }
            }
        }

        private static IndexSnapshot Build(DatabaseConnection db, OverrideConfig ov)
        {
            var blacklist = new HashSet<string>(ov.Blacklist.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var importedIds = new HashSet<string>();
            var perModel = db.NonDeprecatedBuildingBlockConfigsPerImportedModel?.ToList()
                           ?? new List<KeyValuePair<string, List<string>>>();

            var displayCategories = db.DisplayCategories.List() ?? new List<DisplayCategoryConfig>();
            var categoryIdByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var knownCategoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var objectCategoryNames = new List<string>();
            foreach (var dc in displayCategories)
            {
                if (dc?.name == null)
                    continue;
                if (!categoryIdByName.TryGetValue(dc.name, out var ids))
                    categoryIdByName[dc.name] = ids = new List<string>();
                ids.Add(dc.Id);
                if (dc.specialCategory != SpecialDisplayCategory.Normal || !dc.visible || dc.type != BuildingBlockType.Object)
                    continue;
                if (knownCategoryNames.ContainsKey(dc.name))
                    continue;
                knownCategoryNames[dc.name] = dc.name;
                objectCategoryNames.Add(dc.name);
            }
            objectCategoryNames.Sort(StringComparer.OrdinalIgnoreCase);

            var entries = new List<WorkshopEntry>();
            var catalog = new List<CatalogAsset>();
            string scifiDeskId = null;
            int skippedHidden = 0, skippedType = 0, skippedBlacklist = 0, skippedLocal = 0, skippedBounds = 0, skippedDisabled = 0;

            foreach (var kv in perModel)
            {
                if (kv.Value == null)
                    continue;
                foreach (var blockId in kv.Value.ToList())
                {
                    importedIds.Add(blockId);
                    var cfg = db.BuildingBlockConfigs.GetOrNull(blockId);
                    if (cfg == null || !db.IsBlockVisible(blockId))
                    {
                        skippedHidden++;
                        continue;
                    }
                    if (cfg.modelId == ScifiDeskModelId && scifiDeskId == null && cfg.type == BuildingBlockType.Object)
                        scifiDeskId = cfg.Id;

                    if (cfg.deprecated || cfg.type != BuildingBlockType.Object || cfg.automaticType != AutomaticBuildingBlockType.None
                        || cfg.IsToken() || cfg.IsDecal() || cfg.IsOverlay())
                    {
                        skippedType++;
                        continue;
                    }

                    var desc = db.GetImportedModelDescription(blockId);
                    var publishedId = desc?.publishedWorkshopId ?? 0UL;
                    bool local = desc?.local ?? false;
                    if (local && !ov.IncludeLocalImports)
                    {
                        skippedLocal++;
                        continue;
                    }
                    if (blacklist.Contains(blockId) || blacklist.Contains(kv.Key) || (cfg.modelId != null && blacklist.Contains(cfg.modelId))
                        || (cfg.name != null && blacklist.Contains(cfg.name))
                        || (publishedId != 0 && blacklist.Contains(publishedId.ToString())))
                    {
                        skippedBlacklist++;
                        continue;
                    }
                    if (!TryGetFootprint(cfg, db, out var fp))
                    {
                        skippedBounds++;
                        continue;
                    }

                    var authored = cfg.IsInLightCategory(db)
                        ? Array.Empty<string>()
                        : NamesOf(SafeNormalCategories(cfg, db), db);
                    var autoNames = CategoryRoles.Suggest(cfg.name, cfg.keywords, authored, knownCategoryNames);
                    var rule = ov.FindAsset(blockId, cfg.name, publishedId);
                    bool custom = rule?.Categories != null;
                    var activeNames = custom ? ResolveRuleCategories(rule.Categories, db, knownCategoryNames) : autoNames;
                    bool enabled = rule?.Enabled ?? true;
                    float? chance = rule?.Chance is float c ? Mathf.Clamp01(c) : (float?)null;

                    var entry = new WorkshopEntry
                    {
                        Id = blockId,
                        Name = cfg.name,
                        ModelId = cfg.modelId,
                        ImportedModelId = kv.Key,
                        PublishedId = publishedId,
                        Local = local,
                        WallMount = cfg.wallMountType,
                        Categories = IdsForNames(activeNames, categoryIdByName),
                        Footprint = fp,
                        Chance = chance,
                    };
                    catalog.Add(new CatalogAsset
                    {
                        Id = blockId,
                        Name = cfg.name,
                        PublishedId = publishedId,
                        Local = local,
                        Footprint = fp,
                        AutoCategories = autoNames,
                        Categories = activeNames,
                        Enabled = enabled,
                        Chance = chance,
                        UsingCustomCategories = custom,
                    });
                    if (!enabled)
                    {
                        skippedDisabled++;
                        continue;
                    }
                    entries.Add(entry);
                }
            }

            catalog.Sort((a, b) =>
            {
                int name = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return name != 0 ? name : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
            });

            var byCategory = new Dictionary<string, List<WorkshopEntry>>();
            foreach (var e in entries)
            foreach (var cat in e.Categories)
            {
                if (!byCategory.TryGetValue(cat, out var list))
                    byCategory[cat] = list = new List<WorkshopEntry>();
                list.Add(e);
            }

            var categoryChance = new Dictionary<string, float>();
            foreach (var kv in ov.CategoryChance)
            {
                float chance = Mathf.Clamp01(kv.Value);
                if (db.DisplayCategories.Has(kv.Key))
                    categoryChance[kv.Key] = chance;
                else if (categoryIdByName.TryGetValue(kv.Key, out var ids))
                    foreach (var id in ids) categoryChance[id] = chance;
                else
                    Plugin.Log.LogWarning($"categoryChance: unknown display category '{kv.Key}'");
            }

            var forced = BuildForcedMappings(db, ov, entries, importedIds);

            var tableCategories = new HashSet<string>(displayCategories
                .Where(dc => dc?.name != null && dc.specialCategory == SpecialDisplayCategory.Normal
                             && dc.name.IndexOf("table", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(dc => dc.Id));

            var snap = new IndexSnapshot
            {
                Db = db,
                Version = ++_version,
                Entries = entries,
                ByCategory = byCategory,
                ImportedBlockIds = importedIds,
                Forced = forced,
                CategoryChance = categoryChance,
                ForcedChance = ov.ForcedMappingChance.HasValue ? Mathf.Clamp01(ov.ForcedMappingChance.Value) : (float?)null,
                AutoMatch = !ov.DisableAutoMatch,
                SwapObjectsWithItemsOnTop = ov.SwapObjectsWithItemsOnTop,
                ScifiDeskId = scifiDeskId,
                TableCategoryIds = tableCategories,
                Catalog = catalog,
                ObjectCategoryNames = objectCategoryNames.ToArray(),
            };

            Plugin.Log.LogInfo($"Workshop index v{snap.Version}: {entries.Count} usable imported configs in {byCategory.Count} categories, " +
                               $"{catalog.Count} in the menu, {forced.Count} forced mappings, {categoryChance.Count} category chances " +
                               $"(skipped: hidden {skippedHidden}, type {skippedType}, local {skippedLocal}, blacklist {skippedBlacklist}, no bounds {skippedBounds}, disabled {skippedDisabled}). " +
                               $"ScifiDesk: {scifiDeskId ?? "not found"}");

            foreach (var asset in catalog)
            {
                var rate = asset.Chance.HasValue ? asset.Chance.Value.ToString("0.##") : "default";
                Plugin.Log.LogInfo($"  [index] '{asset.Name}' {(asset.Enabled ? "on" : "off")} rate={rate} " +
                                   $"replaces=[{string.Join(", ", asset.Categories)}]{(asset.UsingCustomCategories ? " (custom)" : "")}");
            }

            return snap;
        }

        private static string[] NamesOf(IEnumerable<string> categoryIds, DatabaseConnection db)
        {
            var names = new List<string>();
            foreach (var id in categoryIds)
            {
                var name = db.DisplayCategories.GetOrNull(id)?.name;
                if (string.IsNullOrEmpty(name) || names.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                names.Add(name);
            }
            return names.ToArray();
        }

        private static string[] ResolveRuleCategories(IEnumerable<string> keys, DatabaseConnection db, Dictionary<string, string> known)
        {
            var list = new List<string>();
            if (keys == null)
                return Array.Empty<string>();
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;
                var k = key.Trim();
                string canonical = null;
                if (!known.TryGetValue(k, out canonical) && db.DisplayCategories.Has(k))
                {
                    var named = db.DisplayCategories.GetOrNull(k)?.name;
                    if (named != null)
                        known.TryGetValue(named, out canonical);
                }
                if (canonical == null)
                {
                    Plugin.Log.LogWarning($"assets: unknown object category '{k}'");
                    continue;
                }
                if (!list.Exists(n => string.Equals(n, canonical, StringComparison.OrdinalIgnoreCase)))
                    list.Add(canonical);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list.ToArray();
        }

        private static string[] IdsForNames(IEnumerable<string> names, Dictionary<string, List<string>> categoryIdByName)
        {
            var ids = new List<string>();
            foreach (var name in names)
            {
                if (name == null || !categoryIdByName.TryGetValue(name, out var list))
                    continue;
                foreach (var id in list)
                {
                    if (!ids.Contains(id))
                        ids.Add(id);
                }
            }
            return ids.ToArray();
        }

        private static Dictionary<string, WorkshopEntry[]> BuildForcedMappings(DatabaseConnection db, OverrideConfig ov,
            List<WorkshopEntry> entries, HashSet<string> importedIds)
        {
            var result = new Dictionary<string, WorkshopEntry[]>();
            if (ov.ForcedMappings.Count == 0)
                return result;

            var nativeByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in db.BuildingBlockConfigs.ListNonDeprecated())
            {
                if (cfg?.name == null || importedIds.Contains(cfg.Id))
                    continue;
                if (!nativeByName.TryGetValue(cfg.name, out var ids))
                    nativeByName[cfg.name] = ids = new List<string>();
                ids.Add(cfg.Id);
            }

            foreach (var kv in ov.ForcedMappings)
            {
                var targets = new List<WorkshopEntry>();
                foreach (var key in kv.Value ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(key))
                        continue;
                    var k = key.Trim();
                    var matches = entries.Where(e =>
                        string.Equals(e.Id, k, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.ModelId, k, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.ImportedModelId, k, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.Name, k, StringComparison.OrdinalIgnoreCase) ||
                        (e.PublishedId != 0 && e.PublishedId.ToString() == k)).ToList();
                    if (matches.Count == 0)
                        Plugin.Log.LogWarning($"forcedMappings['{kv.Key}']: no usable imported asset matches '{k}' (missing, hidden or blacklisted)");
                    foreach (var m in matches)
                        if (!targets.Contains(m)) targets.Add(m);
                }
                if (targets.Count == 0)
                    continue;

                var nativeIds = new List<string>();
                var direct = db.BuildingBlockConfigs.GetOrNull(kv.Key);
                if (direct != null && !importedIds.Contains(direct.Id))
                    nativeIds.Add(direct.Id);
                else if (nativeByName.TryGetValue(kv.Key, out var byName))
                    nativeIds.AddRange(byName);

                if (nativeIds.Count == 0)
                {
                    Plugin.Log.LogWarning($"forcedMappings: unknown native block '{kv.Key}'");
                    continue;
                }
                foreach (var id in nativeIds)
                    result[id] = targets.ToArray();
            }
            return result;
        }
    }
}
