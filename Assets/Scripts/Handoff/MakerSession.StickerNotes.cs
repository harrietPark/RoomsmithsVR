using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F47 (2026-11-12): the crew's opinion stickers on the look pictures carry their few words, and a tap opens
    // the teammate's whole line as a small note. The person asked for "the sticker and the opinion stuck on
    // together, and a small note when I press it" and picked draft A of j_StickerNotes_3options.png.
    //  - One die-cut sticker: the round mark (heart / ! / bulb, MakerSession.Crew.cs) with a strip in the
    //    teammate's colour holding the AI's tag (1-2 words), running inward along the picture's edge from the
    //    teammate's corner (Spark top-left, Maker top-right, Fixer bottom-right), so they never overlap.
    //  - A fingertip on it, a pinch on it (near, or a ray from afar) or an Editor click opens the note in front of
    //    the picture: the name and mood, then the line it said when it stuck it. The same sticker again folds it;
    //    another sticker swaps it (one at a time); picking a look, the step moving on or the sticker going folds
    //    it. Time never closes it. The teammate looks at the note and nods / shakes / pops an idea, but says
    //    nothing again (the crew never repeats itself).
    //  - A tap on a sticker never picks its picture: stickers are tested before pictures (OnPinch, EditorInput).
    // (Names here carry "Sticker"/"LookSticker": Spots.cs has StickerRadius for the idea notes' face stickers.)
    public sealed partial class MakerSession
    {
        public const float LookStickerRadius = .015f, StickerNoteWidth = .118f, StickerNoteFont = 10.5f, StickerWordsFont = 11f;
        public Sticker NoteSticker { get; private set; }
        public Transform StickerNoteView => stickerNote;
        public string StickerNoteLine { get; private set; } = "";
        public int StickerNotesOpened { get; private set; }
        public readonly List<string> StickerLog = new List<string>(); // "opened (how): who mood @look" / "closed (why): ..."

        private Transform stickerNote;
        private Vector2 stickerNoteOffset;   // from the sticker, along the picture's right/up (m)
        private float stickerNoteHeight;
        private Step stickerNoteStep;
        private readonly List<Mesh> stickerNoteMeshes = new List<Mesh>();
        private readonly HashSet<Hand> stickerTouchDown = new HashSet<Hand>();
        private Sticker lastStickerTapped;
        private float lastStickerTapAt = -10f;

        // ------------------------------------------------------------ the words on the sticker

        // The tag as printed: cleaned, whole words, at most 3 of them and about 14 letters (the AI is asked for 12).
        private static string StickerTag(string tag)
        {
            var words = MakerRoles.Clean(tag).Trim().Trim('.', '!', '?', '"', '\'').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(3).ToList();
            while (words.Count > 1 && string.Join(" ", words).Length > 14) words.RemoveAt(words.Count - 1);
            return string.Join(" ", words);
        }

        // The strip with the words, beside the disc (built at its real size; longer words shrink to fit half the edge).
        private void BuildStickerWords(Sticker s, float pictureSize)
        {
            s.radius = LookStickerRadius;
            if (string.IsNullOrEmpty(s.tag)) return;
            float h = LookStickerRadius * 1.4f, gap = .0045f, end = .0035f;
            // F52: back on the corners, two stickers can share an edge: each strip stops 2 mm before its middle.
            float most = Mathf.Max(.02f, pictureSize * .5f - LookStickerRadius * 1.35f - gap - end - .002f);
            var canvas = new GameObject("Words", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvas.transform;
            rect.SetParent(s.root, false);
            rect.localScale = Vector3.one * .001f;
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var label = HatIdeaCard.Label(rect, font, 0f, 0f, most * 1000f, h * 1000f, StickerWordsFont, StickerWordsFont, Color.white, FontStyles.Bold, TextAlignmentOptions.Center);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            float want = label.GetPreferredValues(s.tag, 9999f, h * 1000f).x / 1000f + .0015f;
            float width = Mathf.Min(want, most);
            if (want > most) { label.enableAutoSizing = true; label.fontSizeMin = 6f; label.fontSizeMax = StickerWordsFont; }
            label.text = s.tag;
            rect.sizeDelta = label.rectTransform.sizeDelta = new Vector2(width * 1000f, h * 1000f);
            rect.localPosition = new Vector3(s.side * (LookStickerRadius + gap + width * .5f), 0f, -.0008f);

            float from = s.side * LookStickerRadius * .3f, to = s.side * (LookStickerRadius + gap + width + end);
            s.stripFrom = Mathf.Min(from, to);
            s.stripTo = Mathf.Max(from, to);
            s.stripHalf = h * .5f;
            float length = s.stripTo - s.stripFrom, middle = (s.stripFrom + s.stripTo) * .5f;
            Color color = MakerRoles.Color(s.who);
            var strip = ToyMeshes.RoundedSlab(length, h, h * .5f, .0012f, color, Color.Lerp(color, Color.black, .25f), color, "StickerStrip");
            var cut = ToyMeshes.RoundedSlab(length + .0044f, h + .0044f, (h + .0044f) * .5f, .001f, Color.white, new Color(.86f, .86f, .88f), Color.white, "StickerStripCut");
            owned.Add(strip);
            owned.Add(cut);
            Part(s.root, "Strip", strip, new Vector3(middle, 0f, .0003f), 0f);
            Part(s.root, "StripCut", cut, new Vector3(middle, 0f, .0008f), 0f);
        }

        // ------------------------------------------------------------ finding the sticker under a hand

        // How far `world` is outside the sticker's shape in its plane (m, 0 on it), and how far in front of it.
        // (Its shape is kept in its own units; a picked picture shrinks it, so distances are scaled back to metres.)
        private static bool StickerOffset(Sticker s, Vector3 world, out float off, out float front)
        {
            off = front = float.MaxValue;
            if (s?.root == null || !s.root.gameObject.activeInHierarchy || float.IsInfinity(world.x)) return false;
            float scale = Mathf.Max(1e-4f, s.root.lossyScale.x);
            Vector3 l = s.root.InverseTransformPoint(world);
            float disc = Mathf.Max(0f, new Vector2(l.x, l.y).magnitude - s.radius);
            float strip = s.stripTo > s.stripFrom
                ? new Vector2(Mathf.Max(0f, Mathf.Max(s.stripFrom - l.x, l.x - s.stripTo)), Mathf.Max(0f, Mathf.Abs(l.y) - s.stripHalf)).magnitude
                : float.MaxValue;
            off = Mathf.Min(disc, strip) * scale;
            front = -l.z * scale; // the person's side is the sticker's -z
            return true;
        }

        // The sticker at `point` (within `margin`, from `behind` it to `inFront` of it), the nearest when several.
        private Sticker StickerAt(Vector3 point, float margin, float behind, float inFront)
        {
            Sticker best = null;
            float bestOff = float.MaxValue;
            foreach (var s in Stickers)
            {
                if (!StickerOffset(s, point, out float off, out float front)) continue;
                if (front < -behind || front > inFront || off > margin || off >= bestOff) continue;
                best = s;
                bestOff = off;
            }
            return best;
        }

        private Sticker StickerOnRay(Ray ray)
        {
            Sticker best = null;
            float bestDistance = float.MaxValue;
            foreach (var s in Stickers)
            {
                if (s?.root == null || !s.root.gameObject.activeInHierarchy) continue;
                var plane = new Plane(s.root.forward, s.root.position);
                if (!plane.Raycast(ray, out float d) || d <= 0f || d > 4f || d >= bestDistance) continue;
                // A ray from afar shakes more: about 1 cm of slack per metre (4-12 mm), as for the reply pills.
                if (!StickerOffset(s, ray.GetPoint(d), out float off, out _) || off > Mathf.Clamp(d * .01f, .004f, .012f)) continue;
                best = s;
                bestDistance = d;
            }
            return best;
        }

        // A pinch on a sticker (near, or aimed from afar when it is not on a picture) or an Editor click: its note.
        private bool StickerPinch(Vector3 point, Ray? ray)
        {
            if (Stickers.Count == 0) return false;
            var near = StickerAt(point, .008f, .03f, .03f);
            var s = near;
            if (s == null && ray.HasValue && PictureAt(point) == null) s = StickerOnRay(ray.Value);
            if (s == null) return false;
            TapSticker(s, float.IsInfinity(point.x) ? "click" : near != null ? "pinch" : "ray");
            return true;
        }

        // Tests: a pinch aimed along `ray` from afar (no hand near anything); a sticker as the crew would stick it.
        public bool SimulateStickerRay(Ray ray) => StickerPinch(Vector3.positiveInfinity, ray);
        public void SimulateSticker(Crew crew, int look, string mood, string tag, string line)
        {
            var looks = ShownLooks();
            if (look >= 0 && look < looks.Count) StickOn(crew, looks[look], mood, tag, line);
        }

        // A fingertip pressing a sticker (once per touch: the finger leaves it before it counts again).
        private readonly FingerPress stickerPress = new FingerPress(); // F60
        private void StickerPokeFrame()
        {
            if (Stickers.Count == 0) { stickerTouchDown.Clear(); return; }
            bool busy = NotebookHeld || held != null || Dragging != null || Drawing;
            foreach (var hand in HandSet.Both())
            {
                if (busy || hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand) || !HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip))
                {
                    stickerPress.Forget(hand);
                    continue;
                }
                var s = StickerAt(tip.position, .003f, .012f, .006f);
                if (stickerPress.Step(this, hand, s != null)) { PressLog("sticker", hand); TapSticker(s, "poke"); }
            }
        }

        private void TapSticker(Sticker s, string how)
        {
            if (s?.root == null) return;
            // A finger that touched it on the way to a pinch is one tap, not two.
            if (s == lastStickerTapped && Time.time - lastStickerTapAt < .8f) return;
            lastStickerTapped = s;
            lastStickerTapAt = Time.time;
            Heard();
            if (NoteSticker == s)
            {
                ToySounds.Play(ToySounds.Cue.Paper, s.root.position, .35f, 1.5f);
                CloseStickerNote("tapped again");
                return;
            }
            if (NoteSticker != null) CloseStickerNote("swapped");
            OpenStickerNote(s, how);
        }

        // ------------------------------------------------------------ the note

        private static string StickerMoodWord(string mood) => mood == "like" || mood == "worry" || mood == "idea" ? mood : "";
        private static string StickerWhere(Sticker s) => MakerRoles.Clean(s?.picture?.look?.label ?? "a look");

        private void OpenStickerNote(Sticker s, string how)
        {
            string line = string.IsNullOrWhiteSpace(s.line) ? s.tag : s.line;
            Color role = MakerRoles.Color(s.who), face = Color.Lerp(role, Color.white, .87f), ink = Color.Lerp(role, Color.black, .2f);
            stickerNote = new GameObject("Sticker Note " + s.who).transform;
            stickerNote.SetParent(transform, false);

            // The words first: their height sets the card's.
            const float pad = .0075f;
            float textWidth = StickerNoteWidth - pad * 2f;
            var canvas = new GameObject("Text", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)canvas.transform;
            rect.SetParent(stickerNote, false);
            rect.localScale = Vector3.one * .001f;
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var text = HatIdeaCard.Label(rect, font, 0f, 0f, textWidth * 1000f, 200f, StickerNoteFont, StickerNoteFont, new Color(.15f, .15f, .18f), FontStyles.Normal, TextAlignmentOptions.TopLeft);
            text.richText = true;
            text.overflowMode = TextOverflowModes.Overflow;
            string mood = StickerMoodWord(s.mood);
            text.text = "<size=86%><b><color=#" + ColorUtility.ToHtmlStringRGB(ink) + ">" + MakerRoles.Name(s.who) + "</color></b>" +
                        (mood.Length > 0 ? "<color=#8A8A8A> · " + mood + "</color>" : "") + "</size>\n" + line;
            float textHeight = Mathf.Max(.012f, text.GetPreferredValues(text.text, textWidth * 1000f, 0f).y / 1000f);
            rect.sizeDelta = text.rectTransform.sizeDelta = new Vector2(textWidth * 1000f, textHeight * 1000f);
            rect.localPosition = new Vector3(0f, 0f, -.0016f);
            stickerNoteHeight = textHeight + pad * 2f;

            // The card, a rim of the teammate's colour and a tail toward the sticker.
            var card = ToyMeshes.RoundedSlab(StickerNoteWidth, stickerNoteHeight, .008f, .002f, face, Color.Lerp(face, Color.black, .08f), face, "StickerNote");
            var rim = ToyMeshes.RoundedSlab(StickerNoteWidth + .003f, stickerNoteHeight + .003f, .0095f, .0016f, role, role, role, "StickerNoteRim");
            var tail = ToyMeshes.Tail(.0055f, .009f, .0014f, .002f, .002f, face, face, face);
            var tailRim = ToyMeshes.Tail(.0072f, .0108f, .0022f, .002f, .0016f, role, role, role);
            stickerNoteMeshes.AddRange(new[] { card, rim, tail, tailRim });
            StickerNotePart("Card", card, Vector3.zero, 0f);
            StickerNotePart("Rim", rim, new Vector3(0f, 0f, .0009f), 0f);

            // Its place: on the sticker's side, its outer edge at the picture's edge; below a top sticker, above a
            // bottom one, a little in front of the picture.
            float r = s.radius * StickerPictureScale(s);
            stickerNoteOffset = new Vector2(s.side * (StickerNoteWidth * .5f - .002f), (s.top ? -1f : 1f) * (r + .008f + stickerNoteHeight * .5f));
            float aim = Mathf.Clamp(-stickerNoteOffset.x + s.side * (r + .006f), -StickerNoteWidth * .5f + .012f, StickerNoteWidth * .5f - .012f);
            float edge = (s.top ? 1f : -1f) * stickerNoteHeight * .5f;
            float turn = s.top ? 180f : 0f; // the tail points down; a top sticker's points up
            StickerNotePart("Tail", tail, new Vector3(aim, edge, -.0001f), turn);
            StickerNotePart("TailRim", tailRim, new Vector3(aim, edge, .0009f), turn);

            NoteSticker = s;
            StickerNoteLine = line;
            stickerNoteStep = Current;
            StickerNotesOpened++;
            PlaceStickerNote(1f);
            stickerNote.localScale = Vector3.zero;
            StartCoroutine(PopStickerNote(stickerNote));
            StickerLog.Add("opened (" + how + "): " + s.who + " " + s.mood + " @" + StickerWhere(s));
            Talk.Add("The person opened " + MakerRoles.Name(s.who) + "'s sticker on " + StickerWhere(s) + ": " + line);
            // The teammate looks at it (LateUpdate) and shows the mood again, without a word.
            Gesture(s.who, s.mood);
            ToySounds.Play(ToySounds.Cue.Pop, stickerNote.position, .4f, 1.6f);
            ToyFx.Puff(s.root.position, .004f, 2);
        }

        private void StickerNotePart(string name, Mesh mesh, Vector3 at, float turn)
        {
            var part = Part(name, mesh);
            part.SetParent(stickerNote, false);
            part.localPosition = at;
            part.localRotation = Quaternion.Euler(0f, 0f, turn);
        }

        private static float StickerPictureScale(Sticker s) => s?.picture?.root != null ? s.picture.root.lossyScale.x : 1f;

        private static IEnumerator PopStickerNote(Transform t)
        {
            for (float k = 0f; k < 1f && t != null; k += Time.deltaTime / .2f)
            {
                t.localScale = Vector3.one * Mathf.SmoothStep(0f, 1f, k) * (1f + Mathf.Sin(k * Mathf.PI) * .12f);
                yield return null;
            }
            if (t != null) t.localScale = Vector3.one;
        }

        private void PlaceStickerNote(float blend)
        {
            var s = NoteSticker;
            if (stickerNote == null || s?.root == null || s.picture?.root == null) return;
            var axes = s.picture.root;
            // F52: next to its sticker again (on the corner), over the picture and a little in front of it: nothing
            // is listed under the pictures any more.
            Vector3 local = s.root.parent == axes ? s.root.localPosition : axes.InverseTransformPoint(s.root.position);
            Vector3 at = axes.TransformPoint(new Vector3(local.x + stickerNoteOffset.x, local.y + stickerNoteOffset.y, local.z)) - axes.forward * .016f;
            stickerNote.SetPositionAndRotation(blend >= 1f ? at : Vector3.Lerp(stickerNote.position, at, blend), axes.rotation);
        }

        public void CloseStickerNote(string why)
        {
            if (NoteSticker != null) StickerLog.Add("closed (" + why + "): " + NoteSticker.who + " " + NoteSticker.mood + " @" + StickerWhere(NoteSticker));
            if (StickerLog.Count > 100) StickerLog.RemoveRange(0, StickerLog.Count - 100);
            NoteSticker = null;
            StickerNoteLine = "";
            if (stickerNote != null) StartCoroutine(FoldStickerNote(stickerNote, stickerNoteMeshes.ToArray()));
            else foreach (var m in stickerNoteMeshes) if (m != null) Destroy(m);
            stickerNote = null;
            stickerNoteMeshes.Clear();
        }

        private static IEnumerator FoldStickerNote(Transform t, Mesh[] meshes)
        {
            Vector3 start = t != null ? t.localScale : Vector3.one;
            for (float k = 0f; k < 1f && t != null; k += Time.deltaTime / .14f)
            {
                t.localScale = start * (1f - Mathf.SmoothStep(0f, 1f, k));
                yield return null;
            }
            if (t != null) Destroy(t.gameObject);
            foreach (var m in meshes) if (m != null) Destroy(m);
        }

        // Who looks at the open note (its teammate), for the crew's look targets in LateUpdate.
        private Transform StickerNoteLook(Crew crew) => stickerNote != null && NoteSticker != null && NoteSticker.who == crew ? stickerNote : null;

        // ------------------------------------------------------------ every frame (LateUpdate)

        private void StickerNotesFrame()
        {
            StickerPokeFrame();
            var open = NoteSticker;
            if (open == null) return;
            if (open.root == null || !Stickers.Contains(open)) CloseStickerNote("sticker gone");
            else if (open.picture?.root == null || !open.picture.root.gameObject.activeInHierarchy) CloseStickerNote("picture gone");
            else if (Current != stickerNoteStep) CloseStickerNote("step moved on");
            else if (stickerNoteStep == Step.Looks && Look != null) CloseStickerNote("look picked");
            else PlaceStickerNote(1f - Mathf.Exp(-Time.deltaTime * 14f));
        }
    }
}
