using UnityEngine;
using UnityEngine.UI;

namespace Windups.Handoff
{
    // Reveals a legacy UI Text glyph by glyph by fading vertex alpha. The text string itself
    // is never modified, so gameplay code and checks keep reading the full sentence.
    [RequireComponent(typeof(Text))]
    public sealed class TypewriterReveal : BaseMeshEffect
    {
        public float charactersPerSecond = 40f;
        public float fadeCharacters = 2.5f;

        public bool IsRevealing => revealed < glyphs + fadeCharacters;
        public float Revealed => revealed;

        private Text text;
        private string shown;
        private float revealed = float.MaxValue;
        private int glyphs;

        protected override void Awake()
        {
            base.Awake();
            text = GetComponent<Text>();
            shown = text.text;
        }

        public void Restart()
        {
            revealed = 0f;
            if (graphic != null) graphic.SetVerticesDirty();
        }

        public void Complete()
        {
            revealed = float.MaxValue;
            if (graphic != null) graphic.SetVerticesDirty();
        }

        private void Update()
        {
            if (text == null) return;
            if (text.text != shown)
            {
                string previous = shown;
                shown = text.text;
                if (string.IsNullOrEmpty(shown)) Complete();
                else
                {
                    // Only the new part types in (live transcription grows word by word).
                    int kept = 0;
                    if (previous != null)
                        while (kept < previous.Length && kept < shown.Length && previous[kept] == shown[kept]) kept++;
                    int keptGlyphs = 0;
                    for (int i = 0; i < kept; i++) if (!char.IsWhiteSpace(shown[i])) keptGlyphs++;
                    revealed = Mathf.Min(revealed >= float.MaxValue ? keptGlyphs : revealed, keptGlyphs);
                    if (graphic != null) graphic.SetVerticesDirty();
                }
            }
            if (revealed < glyphs + fadeCharacters)
            {
                revealed += Time.deltaTime * charactersPerSecond;
                graphic.SetVerticesDirty();
            }
        }

        public override void ModifyMesh(VertexHelper helper)
        {
            if (!IsActive()) return;
            int count = helper.currentVertCount;
            glyphs = count / 4;
            if (revealed >= glyphs + fadeCharacters) return;
            var vertex = new UIVertex();
            for (int i = 0; i < count; i++)
            {
                helper.PopulateUIVertex(ref vertex, i);
                float visible = Mathf.Clamp01((revealed - i / 4) / fadeCharacters);
                vertex.color.a = (byte)(vertex.color.a * visible);
                // Each new glyph settles up from slightly below its baseline.
                vertex.position.y -= (1f - visible) * 6f;
                helper.SetUIVertex(vertex, i);
            }
        }
    }
}
