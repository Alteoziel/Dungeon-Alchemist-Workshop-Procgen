using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WorkshopProcgen
{
    internal sealed class OverrideConfig
    {
        public static string FilePath => Path.Combine(Paths.ConfigPath, "WorkshopProcgen.json");

        [JsonProperty("disableAutoMatch")]
        public bool DisableAutoMatch { get; set; }

        [JsonProperty("includeLocalImports")]
        public bool IncludeLocalImports = true;

        [JsonProperty("swapObjectsWithItemsOnTop")]
        public bool SwapObjectsWithItemsOnTop { get; set; }

        [JsonProperty("blacklist")]
        public List<string> Blacklist = new List<string>();

        [JsonProperty("forcedMappings")]
        public Dictionary<string, List<string>> ForcedMappings = new Dictionary<string, List<string>>();

        [JsonProperty("categoryChance")]
        public Dictionary<string, float> CategoryChance = new Dictionary<string, float>();

        [JsonProperty("forcedMappingChance")]
        public float? ForcedMappingChance { get; set; }

        /// <summary>Folder names created in the F6 menu. Assets with no folder stay unassigned.</summary>
        [JsonProperty("folders")]
        public List<string> Folders = new List<string>();

        /// <summary>
        /// Per Workshop asset, keyed by block id (also accepts published file id or exact config name).
        /// Missing entries are automatic: enabled, global chance, categories from <see cref="CategoryRoles"/>.
        /// </summary>
        [JsonProperty("assets")]
        public Dictionary<string, AssetRule> Assets = new Dictionary<string, AssetRule>(StringComparer.OrdinalIgnoreCase);

        public AssetRule FindAsset(string blockId, string name, ulong publishedId)
        {
            if (Assets == null || Assets.Count == 0)
                return null;
            if (!string.IsNullOrEmpty(blockId) && Assets.TryGetValue(blockId, out var rule))
                return rule;
            if (publishedId != 0 && Assets.TryGetValue(publishedId.ToString(), out rule))
                return rule;
            if (!string.IsNullOrEmpty(name) && Assets.TryGetValue(name, out rule))
                return rule;
            return null;
        }

        public void Save()
        {
            var path = FilePath;
            var tmp = path + ".tmp";
            try
            {
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(tmp, "// Workshop Procgen. The in-game menu (F6) rewrites this file.\n" + json);
                if (!File.Exists(path))
                {
                    File.Move(tmp, path);
                    return;
                }
                var backup = path + ".bak";
                File.Replace(tmp, path, backup);
                if (File.Exists(backup))
                    File.Delete(backup);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Failed to save {path}: {e.Message}");
            }
        }

        public static OverrideConfig LoadOrCreate()
        {
            var path = FilePath;
            try
            {
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, ExampleFile);
                    Plugin.Log.LogInfo($"Created example override file at {path}");
                }

                var text = File.ReadAllText(path);
                var token = JToken.Parse(text, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
                var cfg = token.ToObject<OverrideConfig>() ?? new OverrideConfig();
                cfg.Blacklist ??= new List<string>();
                cfg.ForcedMappings ??= new Dictionary<string, List<string>>();
                cfg.CategoryChance ??= new Dictionary<string, float>();
                cfg.Assets = cfg.Assets == null
                    ? new Dictionary<string, AssetRule>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, AssetRule>(cfg.Assets, StringComparer.OrdinalIgnoreCase);
                cfg.Folders ??= new List<string>();
                return cfg;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Failed to read {path}; using defaults. {e.Message}");
                return new OverrideConfig();
            }
        }

        public static DateTime GetLastWriteTimeUtc()
        {
            try
            {
                return File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private const string ExampleFile = @"// WorkshopProcgen overrides. // and /* */ comments are allowed.
// Changes are picked up automatically while the game runs (within a few seconds).
{
  // true = only forcedMappings are used; no automatic category/wall-mount/footprint matching.
  ""disableAutoMatch"": false,

  // Also use locally imported (non-Workshop) models as replacements.
  ""includeLocalImports"": true,

  // Allow swapping objects that have other generated objects standing on them (e.g. a table with plates).
  // Off by default because the items on top may end up floating or clipping.
  ""swapObjectsWithItemsOnTop"": false,

  // Assets that are never used as replacements. Accepts block config ids, model/folder ids,
  // Steam Workshop published file ids, or exact config names (case-insensitive).
  ""blacklist"": [
    // ""3024965781"",
    // ""Scifi Desk""
  ],

  // Native block (config id or exact name) -> Workshop assets (config id, model id, published id or name).
  // Forced mappings skip the category and footprint checks. Collisions are still checked, and the
  // native block is placed if the replacement does not fit.
  ""forcedMappings"": {
    // ""Wooden Table"": [ ""Scifi Desk"" ]
  },

  // Display category (id or name) -> replacement chance 0..1. Overrides General.ReplacementChance for
  // native objects in that category. If an object is in several listed categories, the highest wins.
  ""categoryChance"": {
    // ""Tables"": 0.8
  },

  // Chance used for forced mappings. null = same rules as categoryChance / ReplacementChance.
  ""forcedMappingChance"": null,

  // Folder names from the F6 menu. New assets stay unassigned until moved into one.
  ""folders"": [],

  // Per Workshop asset. Omit an asset and it is included automatically.
  // enabled: false keeps it out of generation. chance: 0..1, omit to use the global rate.
  // categories: native display-category names this asset may replace. Omit to use automatic matching
  // (its own categories, plus close roles such as Desks standing in for Tables).
  // folder: omit to leave the asset in Unassigned.
  ""assets"": {
    // ""Scifi Desk"": { ""enabled"": true, ""chance"": 0.8, ""categories"": [ ""Tables"", ""Desks"" ] }
  }
}
";
    }

    internal sealed class AssetRule
    {
        [JsonProperty("enabled")]
        public bool Enabled = true;

        [JsonProperty("chance", NullValueHandling = NullValueHandling.Ignore)]
        public float? Chance { get; set; }

        /// <summary>Null means automatic categories. An empty list replaces nothing.</summary>
        [JsonProperty("categories", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Categories { get; set; }

        /// <summary>
        /// Null or "auto" keeps automatic matching. "object" stays an object.
        /// "wall", "partial wall", "floor", "door", or "window" builds a block of that kind.
        /// </summary>
        [JsonProperty("buildAs", NullValueHandling = NullValueHandling.Ignore)]
        public string BuildAs { get; set; }

        /// <summary>Null leaves the asset in the unassigned list.</summary>
        [JsonProperty("folder", NullValueHandling = NullValueHandling.Ignore)]
        public string Folder { get; set; }
    }
}
