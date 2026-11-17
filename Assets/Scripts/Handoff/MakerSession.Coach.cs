using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F29 follow-ups from the first Quest try:
    //  - Palette coach: the palette only exists on the left palm (it hides when the palm goes down). The
    //    first time the vibe is asked, Maker says where it lives ("turn your left palm up"); nothing floats
    //    in the room for it: the palette appears only when the palm turns up (asked once more later).
    //    Then Spark teaches pointing and pinching a colour, and after the first colour, the two-hand frame
    //    for textures. Remembered in PlayerPrefs (thinkmade.paletteTaught / thinkmade.textureTaught).
    //  - Texture frame: see MakerSession.Swatches (viewfinder on the frame the hands make; a seamless
    //    material tile goes into the palette).
    //  - What Spark sees: every look puts a name tag (with a little tail pointing at it) on each recognised
    //    object it could place, for 10 s (the object the person means keeps its tag). When the session
    //    starts, Spark glances at the desk once so the person sees that it knows their things.
    public sealed partial class MakerSession
    {
        public const string PaletteTaughtKey = "thinkmade.paletteTaught", TextureTaughtKey = "thinkmade.textureTaught";
        public bool CoachingPalette { get; private set; }
        public int SeenMarks => seenMarks.Count;
        public static float CoachAfter = 5f; // seconds into the vibe step before the palette tip (tests shorten it)

        private Transform framePreview;
        private readonly List<(RoomEyes.Found found, Transform label)> seenMarks = new List<(RoomEyes.Found, Transform)>();
        private float seenUntil, coachUntil;
        private int coachTries;
        private bool glanced;
        private Mesh ringMesh;

        private static bool Taught(string key) => PlayerPrefs.GetInt(key, 0) == 1;
        private static void MarkTaught(string key) { PlayerPrefs.SetInt(key, 1); PlayerPrefs.Save(); }
        public static void ForgetCoaching() { PlayerPrefs.DeleteKey(PaletteTaughtKey); PlayerPrefs.DeleteKey(TextureTaughtKey); PlayerPrefs.DeleteKey(TapeTaughtKey); }

        // ------------------------------------------------------------ palette coach

        private void CoachFrame()
        {
            if (!Begun || FormFirst) return; // F49: the palette is taught in the finish lesson
            if (Taught(PaletteTaughtKey) && !CoachingPalette) return;
            // Start: the first time the vibe step waits (and once more at the looks, if still not tried).
            if (!CoachingPalette && !Taught(PaletteTaughtKey) && coachTries < 2 && !PaletteOnPalm && !crewBusy && lines.Count == 0 &&
                !Voice.IsSpeaking && !Voice.Loading && ((Current == Step.Style && coachTries == 0) || (Current == Step.Looks && LooksShown && coachTries == 1)) &&
                Time.time - lastStepAt > CoachAfter)
                StartPaletteCoach();
            if (!CoachingPalette) return;
            PlaceCoachHint();
            if (PaletteOnPalm)
            {
                CoachingPalette = false;
                HideCoachHint();
                MarkTaught(PaletteTaughtKey);
                Say(Crew.Spark, "That's your palette! Point at anything, pinch and let go to grab its colour.");
                return;
            }
            if (Time.time > coachUntil) { CoachingPalette = false; HideCoachHint(); }
        }

        private void StartPaletteCoach()
        {
            coachTries++;
            CoachingPalette = true;
            coachUntil = Time.time + 15f;
            Heard();
            EnsurePalette();
            Body(Crew.Maker)?.Play("Decision_Present");
            Say(Crew.Maker, "Tip: turn your left palm up. Your palette lives there.");
        }

        // (Nothing floats in the room for the coach: the palette shows only on the palm.)
        private void PlaceCoachHint() { }

        private void HideCoachHint()
        {


        }

        // After the first colour: teach the texture frame once.
        private void CoachTexture()
        {
            if (Taught(TextureTaughtKey)) return;
            MarkTaught(TextureTaughtKey);
            StartCoroutine(After(2.2f, () => Say(Crew.Spark, "Want a texture too? Make an L with both hands around a surface and hold still.")));
        }

        // ------------------------------------------------------------ what Spark sees

        // A name tag on each thing Spark recognised and could place (no ring on the desk any more, 2026-11-11:
        // the tag's little tail points down at the thing). What could not be placed gets no tag.
        private void ShowSeen(List<RoomEyes.Found> found)
        {
            ClearSeen();
            if (found == null) return;
            seenUntil = Time.time + 10f;
            foreach (var f in found.Where(f => f != null && f.placed).Take(8))
            {
                Transform label = null;
                // The thing the person means already carries its own tag (also when a later look sees it again).
                bool isTarget = targetTag != null && Target != null &&
                                (Target == f || Vector3.ProjectOnPlane(Target.basePoint - f.basePoint, Vector3.up).magnitude < Mathf.Max(.05f, Target.width * .6f));
                if (!isTarget)
                {
                    var colour = new Color(.92f, 1f, .94f);
                    label = PaperTag("Seen " + f.seen.name, .075f, .02f, colour, out var text, 9f); // F77: a paper tag, not a pill
                    text.text = MakerRoles.Clean(f.seen.name);
                    AddTail(label, .02f, colour);
                    Vector3 at = f.top + Vector3.up * TagGap;
                    // Two things close together: the later tag sits a little higher, so both can be read.
                    foreach (var other in seenMarks)
                        if (other.label != null && Vector3.ProjectOnPlane(other.label.position - at, Vector3.up).magnitude < .06f && Mathf.Abs(other.label.position.y - at.y) < .03f)
                            at += Vector3.up * .034f;
                    label.position = at;
                    label.localScale = Vector3.one * .3f;   // pops out of its tip
                    StartCoroutine(Grow(label, 1f, .25f));
                }
                seenMarks.Add((f, label));
            }
        }

        public const float TagTail = .012f, TagGap = .012f;
        private readonly Dictionary<Color, Mesh> tailMeshes = new Dictionary<Color, Mesh>();

        // Gives a tag a tail: the tag's origin becomes the tail's tip (so the tag is put by its tip, grows
        // out of it and turns around it), the card and everything on it move up above the tail.
        // `under`: room between the tail and the card (the colour chips of a target tag).
        private void AddTail(Transform tag, float cardHeight, Color colour, float under = 0f)
        {
            float lift = TagTail + under + cardHeight * .5f;
            foreach (Transform child in tag) child.localPosition += Vector3.up * lift;
            if (!tailMeshes.TryGetValue(colour, out var mesh))
            {
                const float half = .006f;
                mesh = new Mesh { name = "TagTail" };
                // Its own corners for the front and the back, so each side has a normal.
                mesh.SetVertices(new List<Vector3> { new Vector3(-half, TagTail, 0f), new Vector3(half, TagTail, 0f), Vector3.zero, new Vector3(-half, TagTail, 0f), new Vector3(half, TagTail, 0f), Vector3.zero });
                mesh.SetNormals(new List<Vector3> { Vector3.back, Vector3.back, Vector3.back, Vector3.forward, Vector3.forward, Vector3.forward });
                mesh.SetColors(Enumerable.Repeat(colour, 6).ToList());
                mesh.SetTriangles(new[] { 0, 1, 2, 4, 3, 5 }, 0);
                mesh.RecalculateBounds();
                owned.Add(mesh);
                tailMeshes[colour] = mesh;
            }
            var tail = new GameObject("Tail", typeof(MeshFilter), typeof(MeshRenderer));
            tail.transform.SetParent(tag, false);
            // (The tail keeps its tip at the tag's origin; `under` only lifts what sits above it.)
            tail.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = tail.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private Mesh SeenRingMesh()
        {
            if (ringMesh != null) return ringMesh;
            const int n = 48;
            var v = new List<Vector3>();
            var c = new List<Color>();
            var t = new List<int>();
            Color green = new Color(.35f, .9f, .55f);
            for (int i = 0; i <= n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2f;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                v.Add(dir * .9f); v.Add(dir * 1f);
                c.Add(green); c.Add(green);
            }
            for (int i = 0; i < n; i++)
            {
                int k = i * 2;
                t.AddRange(new[] { k, k + 2, k + 1, k + 1, k + 2, k + 3, k, k + 1, k + 2, k + 1, k + 3, k + 2 });
            }
            ringMesh = new Mesh { name = "SeenRing" };
            ringMesh.SetVertices(v);
            ringMesh.SetColors(c);
            ringMesh.SetTriangles(t, 0);
            ringMesh.RecalculateNormals();
            owned.Add(ringMesh);
            return ringMesh;
        }

        private void HideSeenLabel(RoomEyes.Found f)
        {
            for (int i = 0; i < seenMarks.Count; i++)
                if (seenMarks[i].found == f && seenMarks[i].label != null) { Destroy(seenMarks[i].label.gameObject); seenMarks[i] = (f, null); }
        }

        private void ClearSeen()
        {
            foreach (var m in seenMarks)
                if (m.label != null) Destroy(m.label.gameObject);
            seenMarks.Clear();
        }

        private void SeenFrame()
        {
            if (seenMarks.Count == 0) return;
            if (Time.time > seenUntil) { ClearSeen(); return; }
            foreach (var m in seenMarks)
                if (m.label != null) m.label.rotation = SpeechBubbleDock.FacingAt(m.label.position);
        }

        // Once per session start: Spark glances at the desk so the person sees what it recognises.
        private IEnumerator Glance()
        {
            if (glanced || Eyes == null) yield break;
            glanced = true;
            yield return new WaitForSeconds(Scripted ? .5f : 2.5f);
            if (Eyes.Looking || Spotting || SparkWalking || looking || Current == Step.Suggesting) yield break;
            List<RoomEyes.Found> found = null;
            yield return SparkLooks("everyday objects on the desk and around the person", list => found = list);
            if (found == null || found.Count == 0) { if (Eyes.LastError != null) Caption(Crew.Spark, Eyes.LastError, 4f); yield break; }
            Talk.Add("Spark sees: " + string.Join(", ", found.Select(f => f.seen.name)));
            Caption(Crew.Spark, "I see your " + Join(found.Take(3).Select(f => MakerRoles.Clean(f.seen.name)).ToList()) + "!", 3f);
        }
    }
}
