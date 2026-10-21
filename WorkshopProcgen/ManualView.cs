using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace WorkshopProcgen
{
    /// <summary>
    /// In-game reader for the player manual copied next to the plugin.
    /// </summary>
    internal sealed class ManualView : MonoBehaviour
    {
        private const int WindowId = 0x575049;

        private static ManualView _instance;

        private bool _open;
        private Rect _window = new Rect(24f, 24f, 680f, 720f);
        private Vector2 _scroll;
        private string _text;
        private bool _loaded;
        private GUIStyle _body;
        private GUIStyle _heading;
        private GUIStyle _title;

        public static void Show()
        {
            if (_instance == null)
                return;
            _instance._open = true;
            _instance.Load();
        }

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void Load()
        {
            if (_loaded)
                return;
            _loaded = true;
            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "manual.md");
            _text = File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private void OnGUI()
        {
            if (!_open)
                return;
            EnsureStyles();
            float height = Mathf.Min(720f, Screen.height - 48f);
            _window.height = height;
            _window = GUILayout.Window(WindowId, _window, DrawWindow, "Workshop Procgen manual");
        }

        private void EnsureStyles()
        {
            if (_body != null)
                return;
            _body = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true, fontSize = 13 };
            _heading = new GUIStyle(_body) { fontStyle = FontStyle.Bold, fontSize = 16 };
            _title = new GUIStyle(_body) { fontStyle = FontStyle.Bold, fontSize = 18 };
        }

        private void DrawWindow(int id)
        {
            _scroll = GUILayout.BeginScrollView(_scroll);
            if (string.IsNullOrEmpty(_text))
            {
                GUILayout.Label("The manual was not found next to the plugin.", _body);
            }
            else
            {
                foreach (string raw in _text.Replace("\r\n", "\n").Split('\n'))
                    DrawLine(raw);
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Close"))
                _open = false;
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private void DrawLine(string raw)
        {
            string line = raw ?? "";
            if (line.Length == 0)
            {
                GUILayout.Space(8f);
                return;
            }
            if (line.StartsWith("### "))
            {
                GUILayout.Label(Rich(line.Substring(4)), _heading);
                return;
            }
            if (line.StartsWith("## "))
            {
                GUILayout.Space(6f);
                GUILayout.Label(Rich(line.Substring(3)), _heading);
                return;
            }
            if (line.StartsWith("# "))
            {
                GUILayout.Label(Rich(line.Substring(2)), _title);
                return;
            }
            if (line.StartsWith("- "))
                line = "• " + line.Substring(2);
            GUILayout.Label(Rich(line), _body);
        }

        private static string Rich(string value)
        {
            value = Regex.Replace(value, "`([^`]+)`", "$1");
            return Regex.Replace(value, @"\*\*([^*]+)\*\*", "<b>$1</b>");
        }
    }
}
