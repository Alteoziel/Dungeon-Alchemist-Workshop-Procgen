using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WorkshopProcgen
{
    /// <summary>
    /// Opt-in note to the mod author. It never opens the game's bug report, and it sends nothing until
    /// the player presses Send. The delivery address is not shown.
    /// </summary>
    internal sealed class FeedbackForm : MonoBehaviour
    {
        private const int WindowId = 0x575048;

        private static FeedbackForm _instance;
        private static string _pendingContext;

        private bool _open;
        private Rect _window = new Rect(0f, 80f, 420f, 520f);
        private string _what = "";
        private string _doing = "";
        private string _contact = "";
        private string _status = "";
        private bool _sending;
        private Vector2 _scroll;
        private GUIStyle _hint;
        private GUIStyle _wrap;

        public static void Show()
        {
            if (_instance == null)
                return;
            _instance._open = true;
            _instance.ApplyPending();
        }

        public static void Offer(string context)
        {
            if (string.IsNullOrWhiteSpace(context))
                return;
            _pendingContext = context.Trim();
            if (_instance == null)
                return;
            _instance._open = true;
            _instance.ApplyPending();
        }

        private void Awake()
        {
            _instance = this;
            _window.x = Mathf.Max(16f, Screen.width - _window.width - 24f);
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void ApplyPending()
        {
            if (string.IsNullOrEmpty(_pendingContext))
                return;
            if (string.IsNullOrWhiteSpace(_what))
                _what = _pendingContext;
            _pendingContext = null;
        }

        private void OnGUI()
        {
            if (!_open)
                return;
            EnsureStyles();
            _window = GUILayout.Window(WindowId, _window, DrawWindow, "Mod feedback");
        }

        private void EnsureStyles()
        {
            if (_hint != null)
                return;
            _hint = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 12 };
            _wrap = new GUIStyle(GUI.skin.textArea) { wordWrap = true };
        }

        private void DrawWindow(int id)
        {
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label("This note goes to the mod author. Opening this window does not send it. Press Send when it is ready. It includes only what you type below and the mod version. It does not send your map, and it does not replace Dungeon Alchemist's own bug report.", _hint);
            GUILayout.Space(6f);
            GUILayout.Label("What happened?");
            _what = GUILayout.TextArea(_what ?? "", _wrap, GUILayout.MinHeight(90f));
            GUILayout.Label("What were you doing?");
            _doing = GUILayout.TextArea(_doing ?? "", _wrap, GUILayout.MinHeight(70f));
            GUILayout.Label("How can the author reply? (optional)");
            _contact = GUILayout.TextField(_contact ?? "");
            GUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_status))
                GUILayout.Label(_status, _hint);

            GUILayout.BeginHorizontal();
            GUI.enabled = !_sending;
            if (GUILayout.Button("Send"))
                Submit();
            if (GUILayout.Button("Don't send"))
                Close();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private void Close()
        {
            _open = false;
            _status = "";
            _sending = false;
        }

        private void Submit()
        {
            string what = (_what ?? "").Trim();
            string doing = (_doing ?? "").Trim();
            if (what.Length == 0 && doing.Length == 0)
            {
                _status = "Write what happened, or what you were doing, before sending.";
                return;
            }
            string url = DeliveryUrl();
            if (url == null)
            {
                _status = "This copy of the mod has no delivery address, so the note stayed on this computer.";
                return;
            }
            _sending = true;
            _status = "Sending…";
            string json = JsonConvert.SerializeObject(new Payload
            {
                message = Note(what, doing, (_contact ?? "").Trim()),
                _subject = "Workshop Procgen " + Plugin.PluginVersion,
                _captcha = "false",
                _template = "table",
            });
            ThreadPool.QueueUserWorkItem(_ => Deliver(url, json));
        }

        private static string Note(string what, string doing, string contact)
        {
            var body = new StringBuilder();
            body.Append("Mod ").Append(Plugin.PluginVersion).Append("\n\n");
            if (what.Length > 0)
                body.Append("What happened:\n").Append(Trim(what)).Append("\n\n");
            if (doing.Length > 0)
                body.Append("What they were doing:\n").Append(Trim(doing)).Append("\n\n");
            if (contact.Length > 0)
                body.Append("Reply contact:\n").Append(Trim(contact));
            return body.ToString().Trim();
        }

        private void Deliver(string url, string json)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Accept = "application/json";
                request.Referer = "https://formsubmit.co/";
                request.UserAgent = "WorkshopProcgen/" + Plugin.PluginVersion;
                request.Timeout = 20000;
                byte[] raw = Encoding.UTF8.GetBytes(json);
                request.ContentLength = raw.Length;
                using (Stream stream = request.GetRequestStream())
                    stream.Write(raw, 0, raw.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream() ?? Stream.Null))
                    ApplyResult(reader.ReadToEnd());
            }
            catch (Exception e)
            {
                _sending = false;
                _status = "The note was not sent. Check that the game can reach the internet, then try again.";
                Plugin.Log.LogWarning("[feedback] send failed: " + e.GetType().Name);
            }
        }

        private void ApplyResult(string body)
        {
            _sending = false;
            bool ok = false;
            string message = "";
            try
            {
                JObject json = JObject.Parse(body ?? "");
                JToken success = json["success"];
                ok = success != null && (
                    success.Type == JTokenType.Boolean
                        ? success.Value<bool>()
                        : string.Equals(success.ToString(), "true", StringComparison.OrdinalIgnoreCase));
                message = json["message"]?.ToString() ?? "";
            }
            catch (Exception)
            {
                ok = false;
            }

            if (ok)
            {
                _status = "Sent. The mod author has this note.";
                _what = "";
                _doing = "";
                _contact = "";
                return;
            }

            if (message.IndexOf("activ", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _status = "The note is waiting. The delivery service emailed the mod author a confirmation link. Open that message, including the spam folder, and the note is delivered.";
                return;
            }

            _status = "The note was not sent. Try again in a moment.";
            Plugin.Log.LogWarning("[feedback] the delivery service declined the note.");
        }

        private static string Trim(string value)
        {
            if (value.Length <= 4000)
                return value;
            return value.Substring(0, 4000);
        }

        private static string DeliveryUrl()
        {
            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "delivery.local");
            if (!File.Exists(path))
                return null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                if (line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return line;
                return "https://formsubmit.co/ajax/" + line;
            }
            return null;
        }

        private sealed class Payload
        {
            public string message;
            public string _subject;
            public string _captcha;
            public string _template;
        }
    }
}
