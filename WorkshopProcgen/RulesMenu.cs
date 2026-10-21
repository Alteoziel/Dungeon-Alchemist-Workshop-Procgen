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
        private Rect _window = new Rect(24f, 24f, 760f, 760f);
        private Vector2 _scroll;
        private string _search = "";
        private int _statusFilter;
        private bool _newestFirst;
        private string _categoryFilter = "";
        private bool _categoryOpen;
        private string _expandedId;
        private int _seenVersion = -1;
        private bool _allFolders;
        private string _folder = "";
        private string _newFolder = "";
        private string _renameText = "";
        private string _renameFor;
        private bool _bulkMoveOpen;
        private string _moveId;
        private string _folderNotice;
        private bool _foldersReady;
        private readonly List<string> _folders = new List<string>();
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
            public string BuildAs;
            public string Folder;

            public static Edit From(CatalogAsset asset)
            {
                return new Edit
                {
                    Enabled = asset.Enabled,
                    Chance = asset.Chance,
                    Custom = asset.UsingCustomCategories,
                    Categories = new List<string>(asset.Categories ?? Array.Empty<string>()),
                    BuildAs = asset.BuildAs,
                    Folder = asset.Folder,
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
                _foldersReady = false;
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
            GUILayout.Label("Floors, full walls, partial walls, doors, and windows are replaced when a subscribed asset is that kind of block. Build as assigns any object to one of those, and a single wall panel is still detected on its own. Workshop models cannot replace roofs.", _hint);
            GUILayout.Space(4f);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(52f));
            _search = GUILayout.TextField(_search ?? "");
            if (GUILayout.Button("Close", GUILayout.Width(70f)))
                _open = false;
            GUILayout.EndHorizontal();
            DrawListControls(snap);
            EnsureFolders(snap);

            if (snap?.Catalog == null)
            {
                GUILayout.Label("Workshop assets are still loading. Open a map and try again in a few seconds.", _hint);
                GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
                return;
            }

            DrawFolders(snap);
            var listed = Ordered(snap);
            DrawBulk(snap, listed);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (listed.Count == 0)
                GUILayout.Label(snap.Catalog.Count == 0
                    ? "No subscribed or imported assets are loaded yet."
                    : "No assets match this folder, category, or search.", _hint);
            else if (_allFolders)
                DrawFolderSections(snap, listed);
            else
            {
                foreach (var asset in listed)
                {
                    DrawAsset(snap, asset);
                    GUILayout.Space(6f);
                }
            }
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private void DrawListControls(IndexSnapshot snap)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Show", GUILayout.Width(40f));
            if (GUILayout.Button(_statusFilter == 0 ? "[All]" : "All", GUILayout.Width(52f)))
                _statusFilter = 0;
            if (GUILayout.Button(_statusFilter == 1 ? "[Selected]" : "Selected", GUILayout.Width(84f)))
                _statusFilter = 1;
            if (GUILayout.Button(_statusFilter == 2 ? "[Unselected]" : "Unselected", GUILayout.Width(96f)))
                _statusFilter = 2;
            GUILayout.Space(8f);
            GUILayout.Label("Sort", GUILayout.Width(32f));
            if (GUILayout.Button(!_newestFirst ? "[A–Z]" : "A–Z", GUILayout.Width(52f)))
                _newestFirst = false;
            if (GUILayout.Button(_newestFirst ? "[Newest]" : "Newest", GUILayout.Width(72f)))
                _newestFirst = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Category", GUILayout.Width(64f));
            string categoryLabel = string.IsNullOrEmpty(_categoryFilter) ? "All categories" : _categoryFilter;
            if (GUILayout.Button(categoryLabel, GUILayout.Width(220f)))
                _categoryOpen = !_categoryOpen;
            if (!string.IsNullOrEmpty(_categoryFilter) && GUILayout.Button("Clear", GUILayout.Width(56f)))
            {
                _categoryFilter = "";
                _categoryOpen = false;
            }
            GUILayout.EndHorizontal();
            if (!_categoryOpen || snap?.Catalog == null)
                return;
            var names = CategoryChoices(snap);
            int columns = 0;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("All categories", GUILayout.Width(220f)))
            {
                _categoryFilter = "";
                _categoryOpen = false;
            }
            columns++;
            foreach (var name in names)
            {
                if (columns == 3)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    columns = 0;
                }
                if (GUILayout.Button(name, GUILayout.Width(220f)))
                {
                    _categoryFilter = name;
                    _categoryOpen = false;
                }
                columns++;
            }
            GUILayout.EndHorizontal();
        }

        private static List<string> CategoryChoices(IndexSnapshot snap)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in snap.Catalog)
            {
                set.Add(WorkshopIndex.RoleLabel(asset.Role));
                if (asset.Categories == null)
                    continue;
                foreach (var name in asset.Categories)
                {
                    if (!string.IsNullOrEmpty(name))
                        set.Add(name);
                }
            }
            var list = new List<string>(set);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private List<CatalogAsset> Ordered(IndexSnapshot snap)
        {
            var list = new List<CatalogAsset>();
            foreach (var asset in snap.Catalog)
            {
                if (Passes(asset))
                    list.Add(asset);
            }
            if (_newestFirst)
            {
                list.Sort((a, b) =>
                {
                    int time = b.AddedUtcTicks.CompareTo(a.AddedUtcTicks);
                    if (time != 0)
                        return time;
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
            }
            return list;
        }

        private bool Passes(CatalogAsset asset)
        {
            string filter = (_search ?? "").Trim();
            if (filter.Length > 0
                && (asset.Name == null || asset.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                && WorkshopIndex.RoleLabel(asset.Role).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            bool selected = View(asset).Enabled;
            if (_statusFilter == 1 && !selected)
                return false;
            if (_statusFilter == 2 && selected)
                return false;
            if (!_allFolders)
            {
                string folder = View(asset).Folder ?? "";
                if (!string.Equals(folder, _folder ?? "", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            if (string.IsNullOrEmpty(_categoryFilter))
                return true;
            if (string.Equals(WorkshopIndex.RoleLabel(asset.Role), _categoryFilter, StringComparison.OrdinalIgnoreCase))
                return true;
            if (asset.Categories == null)
                return false;
            for (int i = 0; i < asset.Categories.Length; i++)
            {
                if (string.Equals(asset.Categories[i], _categoryFilter, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void DrawFolders(IndexSnapshot snap)
        {
            GUILayout.Space(4f);
            GUILayout.Label("New assets stay in Unassigned until you move them.", _subtle);
            int columns = 0;
            GUILayout.BeginHorizontal();
            columns += FolderTab("Unassigned", !_allFolders && string.IsNullOrEmpty(_folder), () =>
            {
                _allFolders = false;
                _folder = "";
                _bulkMoveOpen = false;
            });
            columns += FolderTab("All", _allFolders, () =>
            {
                _allFolders = true;
                _bulkMoveOpen = false;
            });
            foreach (var name in FolderNames(snap))
            {
                if (columns >= 4)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    columns = 0;
                }
                string folderName = name;
                bool on = !_allFolders && string.Equals(_folder, folderName, StringComparison.OrdinalIgnoreCase);
                columns += FolderTab(folderName, on, () =>
                {
                    _allFolders = false;
                    _folder = folderName;
                    _bulkMoveOpen = false;
                });
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            _newFolder = GUILayout.TextField(_newFolder ?? "", GUILayout.Width(220f));
            if (GUILayout.Button("Add folder", GUILayout.Width(100f)))
                CreateFolder(_newFolder);
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_folderNotice))
                GUILayout.Label(_folderNotice, _subtle);

            if (!_allFolders && !string.IsNullOrEmpty(_folder))
            {
                if (!string.Equals(_renameFor, _folder, StringComparison.Ordinal))
                {
                    _renameFor = _folder;
                    _renameText = _folder;
                }
                GUILayout.BeginHorizontal();
                _renameText = GUILayout.TextField(_renameText ?? "", GUILayout.Width(220f));
                if (GUILayout.Button("Rename", GUILayout.Width(80f)))
                    RenameFolder(_folder, _renameText);
                if (GUILayout.Button("Delete folder", GUILayout.Width(110f)))
                    DeleteFolder(snap, _folder);
                GUILayout.EndHorizontal();
            }
        }

        private int FolderTab(string label, bool on, Action select)
        {
            if (GUILayout.Button(on ? "[" + label + "]" : label, GUILayout.Width(170f)))
                select();
            return 1;
        }

        private void DrawBulk(IndexSnapshot snap, List<CatalogAsset> listed)
        {
            GUILayout.BeginHorizontal();
            bool previous = GUI.enabled;
            GUI.enabled = listed.Count > 0;
            if (GUILayout.Button("Select all", GUILayout.Width(110f)))
                SetListedEnabled(listed, true);
            if (GUILayout.Button("Unselect all", GUILayout.Width(120f)))
                SetListedEnabled(listed, false);
            GUI.enabled = previous && listed.Count > 0;
            bool showMove = _bulkMoveOpen;
            if (GUILayout.Button(showMove ? "[Move shown]" : "Move shown", GUILayout.Width(110f)))
                _bulkMoveOpen = !showMove;
            GUI.enabled = previous;
            GUILayout.Label("Select all and Unselect all change the assets listed below.", _subtle);
            GUILayout.EndHorizontal();
            if (showMove)
                DrawFolderChoices(snap, name => MoveListed(listed, name));
        }

        private void DrawFolderSections(IndexSnapshot snap, List<CatalogAsset> listed)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DrawSection(snap, listed, "");
            foreach (var name in FolderNames(snap))
            {
                if (!seen.Add(name))
                    continue;
                DrawSection(snap, listed, name);
            }
            foreach (var asset in listed)
            {
                string folder = View(asset).Folder;
                if (string.IsNullOrEmpty(folder) || !seen.Add(folder))
                    continue;
                DrawSection(snap, listed, folder);
            }
        }

        private void DrawSection(IndexSnapshot snap, List<CatalogAsset> listed, string folder)
        {
            bool header = false;
            foreach (var asset in listed)
            {
                string name = View(asset).Folder ?? "";
                if (!string.Equals(name, folder ?? "", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!header)
                {
                    GUILayout.Label(string.IsNullOrEmpty(folder) ? "Unassigned" : folder, _hint);
                    header = true;
                }
                DrawAsset(snap, asset);
                GUILayout.Space(6f);
            }
        }

        private void DrawFolder(CatalogAsset asset, Edit edit)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Folder", _subtle, GUILayout.Width(52f));
            string current = string.IsNullOrEmpty(edit.Folder) ? "Unassigned" : edit.Folder;
            bool open = _moveId == asset.Id;
            if (GUILayout.Button(open ? "[" + current + "]" : current, GUILayout.Width(180f)))
                _moveId = open ? null : asset.Id;
            GUILayout.EndHorizontal();
            if (!open)
                return;
            DrawFolderChoices(null, name =>
            {
                edit.Folder = name;
                _moveId = null;
                Commit(asset, edit);
            });
        }

        private void DrawFolderChoices(IndexSnapshot snap, Action<string> choose)
        {
            int columns = 0;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Unassigned", GUILayout.Width(170f)))
                choose(null);
            columns++;
            foreach (var name in FolderNames(snap))
            {
                if (columns >= 4)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    columns = 0;
                }
                string folderName = name;
                if (GUILayout.Button(folderName, GUILayout.Width(170f)))
                    choose(folderName);
                columns++;
            }
            GUILayout.EndHorizontal();
        }

        private void SetListedEnabled(List<CatalogAsset> listed, bool enabled)
        {
            bool changed = false;
            for (int i = 0; i < listed.Count; i++)
            {
                var edit = View(listed[i]);
                if (edit.Enabled == enabled)
                    continue;
                edit.Enabled = enabled;
                changed = true;
            }
            if (changed)
                CommitAll(listed);
        }

        private void MoveListed(List<CatalogAsset> listed, string folder)
        {
            for (int i = 0; i < listed.Count; i++)
                View(listed[i]).Folder = folder;
            _bulkMoveOpen = false;
            CommitAll(listed);
        }

        private void CreateFolder(string name)
        {
            if (IsReservedFolder(name))
            {
                _folderNotice = "Choose a folder name other than All or Unassigned.";
                return;
            }
            name = CleanFolderName(name);
            if (name == null)
                return;
            _folderNotice = null;
            RememberFolder(name);
            _newFolder = "";
            _allFolders = false;
            _folder = name;
            _bulkMoveOpen = false;
            SaveFolders();
        }

        private void RenameFolder(string current, string nextName)
        {
            if (IsReservedFolder(nextName))
            {
                _folderNotice = "Choose a folder name other than All or Unassigned.";
                return;
            }
            nextName = CleanFolderName(nextName);
            if (nextName == null || string.Equals(current, nextName, StringComparison.Ordinal))
                return;
            _folderNotice = null;
            for (int i = 0; i < _folders.Count; i++)
            {
                if (!string.Equals(_folders[i], current, StringComparison.OrdinalIgnoreCase))
                    continue;
                _folders.RemoveAt(i);
                break;
            }
            string canonical = RememberFolder(nextName);
            var snap = WorkshopIndex.Current;
            var moved = new List<CatalogAsset>();
            if (snap?.Catalog != null)
            {
                foreach (var asset in snap.Catalog)
                {
                    var edit = View(asset);
                    if (!string.Equals(edit.Folder, current, StringComparison.OrdinalIgnoreCase))
                        continue;
                    edit.Folder = canonical;
                    moved.Add(asset);
                }
            }
            _folder = canonical;
            _renameFor = canonical;
            _renameText = canonical;
            if (moved.Count > 0)
                CommitAll(moved);
            else
                SaveFolders();
        }

        private void DeleteFolder(IndexSnapshot snap, string name)
        {
            _folders.RemoveAll(folder => string.Equals(folder, name, StringComparison.OrdinalIgnoreCase));
            var moved = new List<CatalogAsset>();
            if (snap?.Catalog != null)
            {
                foreach (var asset in snap.Catalog)
                {
                    var edit = View(asset);
                    if (!string.Equals(edit.Folder, name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    edit.Folder = null;
                    moved.Add(asset);
                }
            }
            _allFolders = false;
            _folder = "";
            _renameFor = null;
            if (moved.Count > 0)
                CommitAll(moved);
            else
                SaveFolders();
        }

        private void SaveFolders()
        {
            var cfg = OverrideConfig.LoadOrCreate();
            cfg.Folders = new List<string>(_folders);
            cfg.Save();
            WorkshopIndex.RequestRebuild();
        }

        private string RememberFolder(string name)
        {
            foreach (var existing in _folders)
            {
                if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                    return existing;
            }
            _folders.Add(name);
            return name;
        }

        private void EnsureFolders(IndexSnapshot snap)
        {
            if (_foldersReady)
                return;
            _foldersReady = true;
            _folders.Clear();
            var cfg = OverrideConfig.LoadOrCreate();
            if (cfg.Folders != null)
            {
                foreach (var name in cfg.Folders)
                {
                    string clean = CleanFolderName(name);
                    if (clean != null)
                        RememberFolder(clean);
                }
            }
            if (snap?.Catalog == null)
                return;
            foreach (var asset in snap.Catalog)
            {
                string clean = CleanFolderName(asset.Folder);
                if (clean != null)
                    RememberFolder(clean);
            }
        }

        private List<string> FolderNames(IndexSnapshot snap)
        {
            EnsureFolders(snap);
            return _folders;
        }

        private static bool IsReservedFolder(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            name = name.Trim();
            return string.Equals(name, "Unassigned", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "All", StringComparison.OrdinalIgnoreCase);
        }

        private static string CleanFolderName(string name)
        {
            if (IsReservedFolder(name) || string.IsNullOrWhiteSpace(name))
                return null;
            name = name.Trim().Replace("\r", "").Replace("\n", "");
            return name.Length == 0 ? null : name;
        }

        private void DrawBuildAs(CatalogAsset asset, Edit edit)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Build as", _subtle, GUILayout.Width(58f));
            BuildChoice(asset, edit, null, "Auto");
            BuildChoice(asset, edit, "object", "Object");
            BuildChoice(asset, edit, "wall", "Wall");
            BuildChoice(asset, edit, "partial wall", "Partial");
            BuildChoice(asset, edit, "floor", "Floor");
            BuildChoice(asset, edit, "door", "Door");
            BuildChoice(asset, edit, "window", "Window");
            GUILayout.EndHorizontal();
        }

        private void BuildChoice(CatalogAsset asset, Edit edit, string value, string label)
        {
            bool on = string.IsNullOrEmpty(value)
                ? string.IsNullOrEmpty(edit.BuildAs)
                : string.Equals(edit.BuildAs, value, StringComparison.OrdinalIgnoreCase);
            if (!GUILayout.Button(on ? "[" + label + "]" : label))
                return;
            edit.BuildAs = value;
            edit.Custom = false;
            edit.Categories = new List<string>(asset.AutoCategories ?? Array.Empty<string>());
            Commit(asset, edit);
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
            var pointer = Event.current.type;
            bool click = pointer == EventType.MouseDown || pointer == EventType.MouseUp;
            bool enabled = GUILayout.Toggle(edit.Enabled, asset.Name ?? asset.Id);
            if (click && enabled != edit.Enabled)
            {
                edit.Enabled = enabled;
                Commit(asset, edit);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_expandedId == asset.Id ? "Done" : "Categories", GUILayout.Width(90f)))
                _expandedId = _expandedId == asset.Id ? null : asset.Id;
            GUILayout.EndHorizontal();

            string source = asset.PublishedId != 0 ? "Workshop " + asset.PublishedId : (asset.Local ? "Local import" : "Imported");
            string size = asset.Footprint.x > 0.01f || asset.Footprint.y > 0.01f
                ? $" · {asset.Footprint.x:0.##}×{asset.Footprint.y:0.##} m"
                : "";
            GUILayout.Label($"{WorkshopIndex.RoleLabel(asset.Role)} · {source}{size}", _subtle);
            DrawFolder(asset, edit);
            DrawBuildAs(asset, edit);
            DrawRate(asset, edit);
            string replaces = asset.Role != StructuralRole.None && !edit.Custom
                ? "Replaces every " + WorkshopIndex.RoleLabel(asset.Role) + " (automatic)"
                : "Replaces: " + (edit.Categories.Count == 0 ? "nothing" : string.Join(", ", edit.Categories))
                  + (edit.Custom ? "" : " (automatic)");
            GUILayout.Label(replaces, _subtle);

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

            if (nextTyped.Trim().Length > 0)
            {
                int matches = 0;
                foreach (var name in snap.CategoryNamesFor(asset.Role))
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

            if (edit.Custom && GUILayout.Button(asset.Role == StructuralRole.None
                    ? "Use automatic categories"
                    : "Replace every " + WorkshopIndex.RoleLabel(asset.Role), GUILayout.Width(260f)))
            {
                edit.Custom = false;
                edit.Categories = new List<string>(asset.AutoCategories ?? Array.Empty<string>());
                Commit(asset, edit);
            }
        }

        private void TryAddCategory(IndexSnapshot snap, CatalogAsset asset, Edit edit, string typed)
        {
            string match = null;
            foreach (var name in snap.CategoryNamesFor(asset.Role))
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
            cfg.Folders = new List<string>(_folders);
            ApplyRule(cfg, asset, edit);
            cfg.Save();
            WorkshopIndex.RequestRebuild();
        }

        private void CommitAll(IReadOnlyList<CatalogAsset> assets)
        {
            if (assets == null || assets.Count == 0)
                return;
            var cfg = OverrideConfig.LoadOrCreate();
            cfg.Folders = new List<string>(_folders);
            for (int i = 0; i < assets.Count; i++)
                ApplyRule(cfg, assets[i], View(assets[i]));
            cfg.Save();
            WorkshopIndex.RequestRebuild();
        }

        private static void ApplyRule(OverrideConfig cfg, CatalogAsset asset, Edit edit)
        {
            cfg.Assets ??= new Dictionary<string, AssetRule>(StringComparer.OrdinalIgnoreCase);
            bool sameAsAuto = !edit.Custom || Same(edit.Categories, asset.AutoCategories);
            bool explicitBuild = !string.IsNullOrEmpty(edit.BuildAs);
            bool hasFolder = !string.IsNullOrEmpty(edit.Folder);
            bool pristine = edit.Enabled && !edit.Chance.HasValue && sameAsAuto && !explicitBuild && !hasFolder;
            if (pristine)
                cfg.Assets.Remove(asset.Id);
            else
            {
                cfg.Assets[asset.Id] = new AssetRule
                {
                    Enabled = edit.Enabled,
                    Chance = edit.Chance,
                    Categories = sameAsAuto ? null : new List<string>(edit.Categories),
                    BuildAs = explicitBuild ? edit.BuildAs : null,
                    Folder = hasFolder ? edit.Folder : null,
                };
            }
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
