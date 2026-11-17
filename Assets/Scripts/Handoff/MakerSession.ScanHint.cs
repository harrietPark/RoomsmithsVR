using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F88 (Quest 2026-11-15: "when scanning I didn't know I had to press the red button. Put 'touch me to scan' over the
    // button, and it goes when touched. Or make it blink."): a pale yellow paper tag "Touch to scan" with a small arrow
    // down to the red shutter stands just above the button while Spark's frame is out (facing the person, over the
    // button whether the frame lies on the desk or is held up), and blinks: its words fade in and out and it bobs a
    // little, and the red dome bounces with it now and then (silently). The first press of the button takes it away for
    // the rest of the run. Not pressable itself (a paper tag, F77); hidden while a picture is taken, while the frame flies
    // or changes size. In the second after every press (ScanDelay) the same tag says "Hands away!" (steady, no blink):
    // it is right where the pressing finger is (the frame's own label under it can be behind the mic).
    public sealed partial class MakerSession
    {
        public const string ScanHintWords = "Touch to scan", HandsAwayWords = "Hands away!";
        public const float ScanHintW = .1f, ScanHintH = .028f, ScanHintFont = 13f, ScanHintAbove = .038f, ScanHintBlink = 1.3f; // blink period (s)
        public bool ScanHintDone { get; private set; } // pressed once this run: no more tag
        private Transform scanHint;
        private TextMeshProUGUI scanHintText;
        private float scanHintBounceAt;
        public Transform ScanHintView => scanHint;
        public bool ScanHintShown => scanHint != null && scanHint.gameObject.activeSelf;

        private void BuildScanHint()
        {
            if (scanHint != null || showFrame == null) return;
            Color paper = new Color(1f, .95f, .62f);
            scanHint = PaperTag("Scan Hint", ScanHintW, ScanHintH, paper, out scanHintText, ScanHintFont, false);
            scanHintText.text = ScanHintWords;
            scanHintText.fontStyle = FontStyles.Bold;
            scanHintText.color = new Color(.55f, .12f, .1f);
            // A small arrow under the tag, pointing down at the red button.
            var arrow = Part("Arrow", StickerMesh("scanHintArrow", () => DownArrow(.011f, .008f, paper)));
            arrow.SetParent(scanHint, false);
            arrow.localPosition = new Vector3(0f, -ScanHintH * .5f + .0005f, 0f);
            scanHint.SetParent(showFrame, false);
            scanHint.gameObject.SetActive(false);
        }

        // ShowFrameLate: over the button, facing the person, blinking until the first press.
        private void ScanHintLate()
        {
            bool handsAway = ScanWaiting && FrameShown && frameButton != null;
            bool want = handsAway || (!ScanHintDone && FrameShown && !frameFlying && !capturingShow && frameMode != FrameMode.Resize && frameMode != FrameMode.Stretch &&
                        frameButton != null && !(shotPreview != null && shotPreview.gameObject.activeSelf));
            if (want && scanHint == null) BuildScanHint();
            if (scanHint == null) return;
            if (scanHint.gameObject.activeSelf != want) scanHint.gameObject.SetActive(want);
            if (!want) return;
            string words = handsAway ? HandsAwayWords : ScanHintWords;
            if (scanHintText != null && scanHintText.text != words) scanHintText.text = words;
            float phase = Time.time / ScanHintBlink * Mathf.PI * 2f;
            float wave = handsAway ? 1f : .5f + .5f * Mathf.Sin(phase);
            // Above the button: straight up when the frame lies on a surface, along the frame's top edge when it is held up.
            Vector3 up = FrameLying ? Vector3.up : showFrame.up;
            Vector3 at = frameButton.position + up * (ScanHintAbove + .003f * wave);
            scanHint.SetPositionAndRotation(at, SpeechBubbleDock.FacingAt(at));
            float parentScale = Mathf.Max(.2f, showFrame.lossyScale.x);
            scanHint.localScale = Vector3.one * ((1f + .06f * wave) / parentScale);
            if (scanHintText != null)
            {
                var c = scanHintText.color;
                c.a = Mathf.Lerp(.3f, 1f, wave);
                scanHintText.color = c;
            }
            // The red dome bounces with it every few blinks (no sound: PulseFrameButton pops).
            if (!handsAway && Time.time - scanHintBounceAt > ScanHintBlink * 2f) { scanHintBounceAt = Time.time; buttonPulseAt = Time.time; }
        }

        // A flat triangle pointing down (both faces), in the paper's colour with a darker rim colour at the tip.
        private static Mesh DownArrow(float width, float height, Color color)
        {
            var tip = Color.Lerp(color, new Color(.55f, .12f, .1f), .55f);
            var mesh = new Mesh { name = "Scan Hint Arrow" };
            mesh.vertices = new[] { new Vector3(-width * .5f, 0f, -.0004f), new Vector3(width * .5f, 0f, -.0004f), new Vector3(0f, -height, -.0004f) };
            mesh.colors = new[] { color, color, tip };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 1 };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
