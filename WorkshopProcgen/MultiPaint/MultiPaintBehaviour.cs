using System.Text;
using UnityEngine;

namespace WorkshopProcgen.MultiPaint
{
    /// <summary>Hotkeys and the IMGUI overlay that lists the current paint set.</summary>
    internal sealed class MultiPaintBehaviour : MonoBehaviour
    {
        private const int MaxListedEntries = 15;
        private const float Width = 260f;

        private string cachedText;
        private int cachedLines;
        private GUIStyle boxStyle;

        private void OnEnable()
        {
            PaintSet.Changed += Invalidate;
        }

        private void OnDisable()
        {
            PaintSet.Changed -= Invalidate;
        }

        private void Invalidate()
        {
            cachedText = null;
        }

        private void Update()
        {
            if (!MultiPaintConfig.IsActive)
            {
                return;
            }
            if (MultiPaintConfig.ClearSetHotkey.Value.IsDown())
            {
                PaintSet.Clear();
                MultiPaintModule.Log.LogInfo("Paint set cleared");
            }
            if (MultiPaintConfig.ToggleOverlayHotkey.Value.IsDown())
            {
                MultiPaintConfig.ShowOverlay.Value = !MultiPaintConfig.ShowOverlay.Value;
            }
        }

        private void OnGUI()
        {
            if (!MultiPaintConfig.IsActive || !MultiPaintConfig.ShowOverlay.Value || PaintSet.Count == 0)
            {
                return;
            }
            if (cachedText == null)
            {
                BuildText();
            }
            if (boxStyle == null)
            {
                boxStyle = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    wordWrap = false,
                    fontSize = 12,
                    padding = new RectOffset(8, 8, 6, 6)
                };
                boxStyle.normal.textColor = Color.white;
            }
            float height = 12f + cachedLines * 17f;
            GUI.Box(new Rect(Screen.width - Width - 12f, 60f, Width, height), cachedText, boxStyle);
        }

        private void BuildText()
        {
            var sb = new StringBuilder();
            sb.Append("Paint set (").Append(PaintSet.Count).Append(")");
            int lines = 1;
            var entries = PaintSet.Entries;
            for (int i = 0; i < entries.Count && i < MaxListedEntries; i++)
            {
                sb.Append("\n• ").Append(entries[i].Name);
                lines++;
            }
            if (entries.Count > MaxListedEntries)
            {
                sb.Append("\n  … +").Append(entries.Count - MaxListedEntries).Append(" more");
                lines++;
            }
            sb.Append("\n").Append(MultiPaintConfig.SelectModifier.Value).Append("+click: add/remove   ")
              .Append(MultiPaintConfig.ClearSetHotkey.Value).Append(": clear");
            lines++;
            cachedText = sb.ToString();
            cachedLines = lines;
        }
    }
}
