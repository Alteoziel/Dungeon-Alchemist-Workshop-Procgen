using System;
using System.Collections.Generic;
using UnityEngine;

namespace WorkshopProcgen
{
    /// <summary>
    /// In-game list of subscribed Workshop assets. Each one is included automatically; this window turns
    /// individual assets off, sets how often they replace a matching prop, and edits which categories they fill.
    /// </summary>
    internal sealed class RulesMenu : MonoBehaviour
    {
        private const int WindowId = 0x575047;

        private bool _open;
        private Rect _window = new Rect(24f, 24f, 640f, 700f);
        private Vector2 _scroll;
        private string _search = "";
        private string _expandedId;
        private int _seenVersion = -1;
        private string _dragId;
        private float _dragValue;
        private float? _dragGlobal;
        private bool? _itemsOnTop;

        private readonly Dictionary<string, Edit> _edits = new Dictionary<string, Edit>();
        private readonly Dictionary<string, string> _addText = new Dictionary<string, string>();

        private GUIStyle _hint;
        private GUIStyle _subtle;

        private sealed class Edit
        {
            public bool Enabled;
            public float? Chance;
            public bool Custom;
            public List<string> Categories;

            public static Edit From(CatalogAsset asset)
            {
                return new Edit
                {
                    Enabled = asset.Enabled,
                    Chance = asset.Chance,
                    Custom = asset.UsingCustomCategories,
                    Categories = new List<string>(asset.Categories ?? Array.Empty<string>()),
                };
            }
        }

        private void Update()
        {
            var hotkey = Plugin.RulesMenuHotkey;
            if (hotkey != null && hotkey.Value.IsDown())
                _open = !_open;
        }

        private void OnGUI()
        {
            if (!_open)
                return;
            EnsureStyles();
            var snap = WorkshopIndex.Current;
            if (snap != null && snap.Version != _seenVersion)
            {
                _seenVersion = snap.Version;
                _edits.Clear();
                _itemsOnTop = null;
            }
            _window = GUILayout.Window(WindowId, _window, id => DrawWindow(snap), "Workshop Procgen");
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                _open = false;
                Event.current.Use();
            }
        }

        private void EnsureStyles()
        {
            if (_hint != null)
                return;
            _hint = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 12 };
            _subtle = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            _subtle.normal.textColor = new Color(0.78f, 0.78f, 0.78f);
        }

        private void DrawWindow(IndexSnapshot snap)
        {
            bool enabled = Plugin.Enabled.Value;
            bool nextEnabled = GUILayout.Toggle(enabled, "Replace props during room generation");
            if (nextEnabled != enabled)
                Plugin.Enabled.Value = nextEnabled;

            DrawGlobalRate();
            DrawItemsOnTop(snap);

            GUILayout.Label("Subscribed assets are included on their own. A new subscription is ready for generation as soon as the game loads it. Desks also fill table slots unless you edit that asset.", _hint);
            GUILayout.Space(4f);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(52f));
            _search = GUILayout.TextField(_search ?? "");
            if (GUILayout.Button("Close", GUILayout.Width(70f)))
                _open = false;
            GUILayout.EndHorizontal();

            if (snap?.Catalog == null)
            {
                GUILayout.Label("Workshop assets are still loading. Open a map and try again in a few seconds.", _hint);
                GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
                return;
            }

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            string filter = (_search ?? "").Trim();
            int shown = 0;
            foreach (var asset in snap.Catalog)
            {
                if (filter.Length > 0 && (asset.Name == null || asset.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;
                shown++;
                DrawAsset(snap, asset);
                GUILayout.Space(6f);
            }
            if (shown == 0)
                GUILayout.Label(snap.Catalog.Count == 0
                    ? "No subscribed or imported object assets are loaded yet."
                    : "No assets match that search.", _hint);
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private void DrawGlobalRate()
        {
            float current = _dragGlobal ?? Plugin.ReplacementChance.Value;
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Default rate {current:P0}", GUILayout.Width(140f));
            float next = GUILayout.HorizontalSlider(current, 0f, 1f);
            GUILayout.EndHorizontal();
            if (Mathf.Abs(next - current) > 0.0001f)
                _dragGlobal = next;
            if (Event.current.type == EventType.MouseUp && _dragGlobal.HasValue)
            {
                Plugin.ReplacementChance.Value = _dragGlobal.Value;
                _dragGlobal = null;
            }
        }

        private void DrawItemsOnTop(IndexSnapshot snap)
        {
            bool current = _itemsOnTop ?? snap?.SwapObjectsWithItemsOnTop ?? false;
            bool next = GUILayout.Toggle(current, "Also replace props that already have items on them");
            if (next == current)
                return;
            _itemsOnTop = next;
            var cfg = OverrideConfig.LoadOrCreate();
            cfg.SwapObjectsWithItemsOnTop = next;
            cfg.Save();
            WorkshopIndex.RequestRebuild();
        }

        private void DrawAsset(IndexSnapshot snap, CatalogAsset asset)
        {
            var edit = View(asset);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            bool enabled = GUILayout.Toggle(edit.Enabled, asset.Name ?? asset.Id);
            if (enabled != edit.Enabled)
            {
                edit.Enabled = enabled;
                Commit(asset, edit);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_expandedId == asset.Id ? "Done" : "Categories", GUILayout.Width(90f)))
                _expandedId = _expandedId == asset.Id ? null : asset.Id;
            GUILayout.EndHorizontal();

            string source = asset.PublishedId != 0 ? "Workshop " + asset.PublishedId : (asset.Local ? "Local import" : "Imported");
            GUILayout.Label($"{source} · {asset.Footprint.x:0.##}×{asset.Footprint.y:0.##} m", _subtle);
            DrawRate(asset, edit);
            GUILayout.Label("Replaces: " + (edit.Categories.Count == 0 ? "nothing" : string.Join(", ", edit.Categories))
                            + (edit.Custom ? "" : " (automatic)"), _subtle);

            if (_expandedId == asset.Id)
                DrawCategories(snap, asset, edit);
            GUILayout.EndVertical();
        }

        private void DrawRate(CatalogAsset asset, Edit edit)
        {
            bool dragging = _dragId == asset.Id;
            bool custom = edit.Chance.HasValue || dragging;
            GUILayout.BeginHorizontal();
            if (!custom)
            {
                GUILayout.Label($"Rate: default ({Plugin.ReplacementChance.Value:P0})", _subtle);
                if (GUILayout.Button("Set rate", GUILayout.Width(80f)))
                {
                    edit.Chance = Plugin.ReplacementChance.Value;
                    Commit(asset, edit);
                }
            }
            else
            {
                float shown = dragging ? _dragValue : edit.Chance ?? Plugin.ReplacementChance.Value;
                GUILayout.Label(shown.ToString("P0"), GUILayout.Width(48f));
                float next = GUILayout.HorizontalSlider(shown, 0f, 1f);
                if (Mathf.Abs(next - shown) > 0.0001f)
                {
                    _dragId = asset.Id;
                    _dragValue = next;
                }
                if (GUILayout.Button("Default", GUILayout.Width(70f)))
                {
                    _dragId = null;
                    edit.Chance = null;
                    Commit(asset, edit);
                }
            }
            GUILayout.EndHorizontal();
            if (Event.current.type == EventType.MouseUp && _dragId == asset.Id)
            {
                edit.Chance = _dragValue;
                _dragId = null;
                Commit(asset, edit);
            }
        }

        private void DrawCategories(IndexSnapshot snap, CatalogAsset asset, Edit edit)
        {
            int columns = 0;
            string remove = null;
            GUILayout.BeginHorizontal();
            var names = edit.Categories.ToArray();
            for (int i = 0; i < names.Length; i++)
            {
                if (columns == 2)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    columns = 0;
                }
                if (!GUILayout.Toggle(true, names[i], GUILayout.Width(280f)))
                    remove = names[i];
                columns++;
            }
            GUILayout.EndHorizontal();
            if (remove != null)
            {
                edit.Categories.RemoveAll(n => string.Equals(n, remove, StringComparison.OrdinalIgnoreCase));
                edit.Custom = true;
                Commit(asset, edit);
            }

            if (!_addText.TryGetValue(asset.Id, out var typed))
                typed = "";
            GUILayout.BeginHorizontal();
            GUILayout.Label("Add", GUILayout.Width(32f));
            string nextTyped = GUILayout.TextField(typed);
            if (nextTyped != typed)
                _addText[asset.Id] = nextTyped;
            bool add = GUILayout.Button("Add", GUILayout.Width(60f));
            GUILayout.EndHorizontal();
            if (add)
                TryAddCategory(snap, asset, edit, nextTyped);

            if (nextTyped.Trim().Length > 0 && snap.ObjectCategoryNames != null)
            {
                int matches = 0;
                foreach (var name in snap.ObjectCategoryNames)
                {
                    if (name.IndexOf(nextTyped.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (Contains(edit.Categories, name))
                        continue;
                    if (GUILayout.Button(name, GUILayout.Width(280f)))
                    {
                        AddCategory(asset, edit, name);
                        _addText[asset.Id] = "";
                        break;
                    }
                    if (++matches >= 6)
                        break;
                }
            }

            if (edit.Custom && GUILayout.Button("Use automatic categories", GUILayout.Width(200f)))
            {
                edit.Custom = false;
                edit.Categories = new List<string>(asset.AutoCategories ?? Array.Empty<string>());
                Commit(asset, edit);
            }
        }

        private void TryAddCategory(IndexSnapshot snap, CatalogAsset asset, Edit edit, string typed)
        {
            if (snap.ObjectCategoryNames == null)
                return;
            string match = null;
            foreach (var name in snap.ObjectCategoryNames)
            {
                if (string.Equals(name, typed.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    match = name;
                    break;
                }
            }
            if (match == null)
                return;
            AddCategory(asset, edit, match);
            _addText[asset.Id] = "";
        }

        private void AddCategory(CatalogAsset asset, Edit edit, string name)
        {
            if (Contains(edit.Categories, name))
                return;
            edit.Categories.Add(name);
            edit.Categories.Sort(StringComparer.OrdinalIgnoreCase);
            edit.Custom = true;
            Commit(asset, edit);
        }

        private Edit View(CatalogAsset asset)
        {
            if (!_edits.TryGetValue(asset.Id, out var edit))
            {
                edit = Edit.From(asset);
                _edits[asset.Id] = edit;
            }
            return edit;
        }

        private void Commit(CatalogAsset asset, Edit edit)
        {
            _edits[asset.Id] = edit;
            var cfg = OverrideConfig.LoadOrCreate();
            cfg.Assets ??= new Dictionary<string, AssetRule>(StringComparer.OrdinalIgnoreCase);
            bool sameAsAuto = !edit.Custom || Same(edit.Categories, asset.AutoCategories);
            bool pristine = edit.Enabled && !edit.Chance.HasValue && sameAsAuto;
            if (pristine)
                cfg.Assets.Remove(asset.Id);
            else
            {
                cfg.Assets[asset.Id] = new AssetRule
                {
                    Enabled = edit.Enabled,
                    Chance = edit.Chance,
                    Categories = sameAsAuto ? null : new List<string>(edit.Categories),
                };
            }
            cfg.Save();
            WorkshopIndex.RequestRebuild();
        }

        private static bool Same(List<string> left, string[] right)
        {
            int n = right?.Length ?? 0;
            if (left.Count != n)
                return false;
            for (int i = 0; i < n; i++)
            {
                if (!Contains(left, right[i]))
                    return false;
            }
            return true;
        }

        private static bool Contains(List<string> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
