using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Windups
{
    // Draws a legacy UI Text through a TextMeshPro SDF child so world-space text stays crisp
    // in the headset. The legacy Text keeps owning the string, size and colour, so existing
    // gameplay code and checks are untouched. If a string needs glyphs that neither the
    // primary font nor its fallbacks have (for example Korean on a device without a CJK
    // system font), the legacy Text renders that string instead of showing empty boxes.
    [RequireComponent(typeof(Text))]
    public sealed class ToyTextMirror : MonoBehaviour
    {
        public float charactersPerSecond = 40f;
        public bool typewriter = true;
        // Hard "\n" breaks in authored lines fight the card's balanced wrapping (leaving a
        // lonely word on its own line), so cards draw them as spaces.
        public bool collapseLineBreaks = true;

        public string DisplayText(string text)
        {
            if (!collapseLineBreaks || string.IsNullOrEmpty(text)) return text ?? string.Empty;
            var collapsed = text.Replace("\r", "").Replace('\n', ' ');
            while (collapsed.Contains("  ")) collapsed = collapsed.Replace("  ", " ");
            return collapsed.Trim();
        }

        // Optional: how the TMP copy shows the legacy string (rich text, e.g. a smaller second line). The legacy
        // Text keeps the plain string; the glyph check is made on the plain text.
        public System.Func<string, string> richFormat;
        public string Shown(string text) => richFormat != null ? richFormat(text ?? string.Empty) : DisplayText(text);
        // Whether `text` will be drawn by the crisp TMP copy (false: the legacy Text draws it).
        public bool CanShow(string text) => target != null && CanDraw(target.font, DisplayText(text));

        public TextMeshProUGUI Target => target;
        public bool UsingLegacyFallback => usingLegacy;
        public bool IsRevealing => target != null && revealed < shownLength;
        public bool Hidden { get; set; }

        private static bool systemFallbacksTried;
        private Text source;
        private TextMeshProUGUI target;
        private string shown;
        private string previousDisplay;
        private int shownLength;
        private float revealed = float.MaxValue;
        private bool usingLegacy;

        public void Initialize(TMP_FontAsset font, bool reveal)
        {
            if (font == null) return;
            source = GetComponent<Text>();
            typewriter = reveal;
            // A copied card (Instantiate) already carries its "TMP" child: reuse it instead of
            // stacking a second, stale text on top.
            if (target == null && transform.Find("TMP") is Transform existing) target = existing.GetComponent<TextMeshProUGUI>();
            if (target == null)
            {
                var child = new GameObject("TMP", typeof(RectTransform));
                child.layer = gameObject.layer;
                var rect = (RectTransform)child.transform;
                rect.SetParent(transform, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                rect.pivot = source.rectTransform.pivot;
                target = child.AddComponent<TextMeshProUGUI>();
                target.raycastTarget = false;
                target.richText = false;
                target.textWrappingMode = TextWrappingModes.Normal;
                target.overflowMode = TextOverflowModes.Overflow;
                target.margin = Vector4.zero;
                target.lineSpacing = -12f;
            }
            target.font = font;
            TrySystemFallbacks(font);
            shown = null;
            Sync(true);
        }

        // On device only: add an OS CJK font behind Patrick Hand. Editing the asset's
        // fallback list in the Editor would be saved into the project, so skip it there.
        private static void TrySystemFallbacks(TMP_FontAsset font)
        {
#if !UNITY_EDITOR
            if (systemFallbacksTried) return;
            systemFallbacksTried = true;
            string[] families = { "Noto Sans CJK KR", "Noto Sans KR", "Noto Sans CJK", "Droid Sans Fallback", "Malgun Gothic", "Apple SD Gothic Neo" };
            foreach (var family in families)
            {
                TMP_FontAsset system = null;
                try { system = TMP_FontAsset.CreateFontAsset(family, "Regular"); } catch { }
                if (system == null) continue;
                if (font.fallbackFontAssetTable == null) font.fallbackFontAssetTable = new List<TMP_FontAsset>();
                font.fallbackFontAssetTable.Add(system);
                Debug.Log("[Windups Text] Added system fallback font " + family);
                break;
            }
#else
            systemFallbacksTried = true;
#endif
        }

        private void LateUpdate()
        {
            if (source == null || target == null) return;
            Sync(false);
            if (revealed < shownLength)
            {
                revealed += Time.deltaTime * charactersPerSecond;
                target.maxVisibleCharacters = Mathf.Min(shownLength, Mathf.FloorToInt(revealed));
            }
            else if (target.maxVisibleCharacters != int.MaxValue) target.maxVisibleCharacters = int.MaxValue;
        }

        private void Sync(bool force)
        {
            string text = source.text ?? string.Empty;
            if (force || text != shown)
            {
                shown = text;
                string display = DisplayText(text);
                shownLength = display.Length;
                usingLegacy = !CanDraw(target.font, display);
                if (target.richText != (richFormat != null)) target.richText = richFormat != null;
                target.text = usingLegacy ? string.Empty : richFormat != null ? richFormat(text) : display;
                // Typewriter: only the new part types in. Live transcription grows the text one
                // word at a time; what is already on screen stays instead of being retyped.
                int kept = 0;
                if (previousDisplay != null)
                    while (kept < previousDisplay.Length && kept < display.Length && previousDisplay[kept] == display[kept]) kept++;
                float already = revealed >= float.MaxValue ? (previousDisplay?.Length ?? 0) : revealed;
                revealed = typewriter && !force && text.Length > 0 ? Mathf.Min(already, kept) : float.MaxValue;
                previousDisplay = display;
                target.maxVisibleCharacters = revealed < shownLength ? Mathf.FloorToInt(revealed) : int.MaxValue;
            }
            bool showTarget = !usingLegacy && !Hidden;
            if (source.enabled != (usingLegacy && !Hidden)) source.enabled = usingLegacy && !Hidden;
            if (target.enabled != showTarget) target.enabled = showTarget;
            if (target.color != source.color) target.color = source.color;
            var alignment = Map(source.alignment);
            if (target.alignment != alignment) target.alignment = alignment;
            var style = source.fontStyle == FontStyle.Bold || source.fontStyle == FontStyle.BoldAndItalic ? FontStyles.Bold : FontStyles.Normal;
            if (target.fontStyle != style) target.fontStyle = style;
            bool autoSize = source.resizeTextForBestFit;
            if (target.enableAutoSizing != autoSize) target.enableAutoSizing = autoSize;
            float max = autoSize ? source.resizeTextMaxSize : source.fontSize;
            float min = Mathf.Min(source.resizeTextMinSize, max);
            if (!Mathf.Approximately(target.fontSizeMax, max)) target.fontSizeMax = max;
            if (!Mathf.Approximately(target.fontSizeMin, min)) target.fontSizeMin = min;
            if (!autoSize && !Mathf.Approximately(target.fontSize, max)) target.fontSize = max;
        }

        private static bool CanDraw(TMP_FontAsset font, string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            var visible = new System.Text.StringBuilder(text.Length);
            foreach (char c in text) if (!char.IsWhiteSpace(c) && !char.IsControl(c)) visible.Append(c);
            if (visible.Length == 0) return true;
            return font.HasCharacters(visible.ToString(), out uint[] _, true, true);
        }

        private static TextAlignmentOptions Map(TextAnchor anchor)
        {
            switch (anchor)
            {
                case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
                case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
                case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
                case TextAnchor.MiddleLeft: return TextAlignmentOptions.Left;
                case TextAnchor.MiddleRight: return TextAlignmentOptions.Right;
                case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
                case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
                case TextAnchor.LowerRight: return TextAlignmentOptions.BottomRight;
                default: return TextAlignmentOptions.Center;
            }
        }
    }
}
