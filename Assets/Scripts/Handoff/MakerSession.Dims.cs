using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Windups.Handoff
{
    // F39 (2026-11-12, chosen design B "blueprint dimension line"): the hand tape and every size on a made
    // thing look like a drawing's dimension line: a thin blue line with arrowheads at both ends, a dot every
    // centimetre and a small white pill with the length ("W  85 mm").
    //  - The tape snaps to an axis as seen from the person (W across, D toward them, H up) when it is within
    //    10 degrees of it, and its ends snap like magnets (2.5 cm) to the edges of the things made, of the
    //    object the person means, and to the desk top; a ring marks a snapped end and a dashed line goes
    //    from it to the edge it took. Sounds: a tick per centimetre, a click on a snap, a chime when kept.
    //  - Kept, the line stays where it was measured for a while (it was a moment ago: everything went away).
    //  - The same line with a round knob at one end is how sizes are changed on v2 (MakerSession.Draft.cs).
    //  - The measuring step asks for the one size that matters most for the thing: its height for tall things
    //    (pen holders, vases, lamps...), its width otherwise, a desk edge's thickness for a clip.
    public sealed partial class MakerSession
    {
        public static readonly Color Blueprint = new Color(.18f, .38f, .84f);
        public const float SnapAngle = 10f, SnapReach = .025f, TapePinSeconds = 15f;
        public string TapeAxis { get; private set; }          // W, D, H or null (not snapped)
        public bool TapeSnappedA { get; private set; }
        public bool TapeSnappedB { get; private set; }
        public int TapeSnaps { get; private set; }

        private sealed class DimView
        {
            public Transform root, bar, arrowA, arrowB, label, knob, ringA, ringB;
            public TextMeshProUGUI text;
            public readonly List<Transform> ticks = new List<Transform>();
            public readonly List<Transform> dashes = new List<Transform>();
        }

        private DimView tapeDim;
        private float tapePinnedUntil;
        private Vector3 tapeExtA, tapeExtB;
        private int tickCm = -1;
        private float tickSoundAt;
        private Mesh dimBar, dimArrow, dimTick, dimDash, dimKnob, dimKnobDot;

        // ------------------------------------------------------------ building a dimension line

        private Mesh DimArrowMesh()
        {
            // A flat triangle (tip at the origin, pointing +x), a little thick, in the blueprint blue.
            const float len = .0075f, half = .0034f, th = .0008f;
            var v = new List<Vector3>();
            var t = new List<int>();
            Vector3[] tri = { Vector3.zero, new Vector3(-len, half, 0f), new Vector3(-len, -half, 0f) };
            foreach (float z in new[] { -th, th }) foreach (var p in tri) v.Add(p + Vector3.forward * z);
            t.AddRange(new[] { 0, 2, 1, 3, 4, 5 });
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                t.AddRange(new[] { i, j, j + 3, i, j + 3, i + 3 });
            }
            var mesh = new Mesh { name = "DimArrow" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.colors = Enumerable.Repeat(Blueprint, v.Count).ToArray();
            mesh.RecalculateNormals();
            owned.Add(mesh);
            return mesh;
        }

        private DimView BuildDim(string name, bool withKnob)
        {
            Color edge = Color.Lerp(Blueprint, Color.black, .2f);
            dimBar ??= Own(ToyMeshes.RoundedSlab(1f, .0016f, .0008f, .0016f, Blueprint, edge, Blueprint, "DimBar"));
            dimArrow ??= DimArrowMesh();
            dimTick ??= Own(ToyMeshes.Disc(.0012f, .0012f, Blueprint, edge, "DimTick"));
            dimDash ??= Own(ToyMeshes.RoundedSlab(.004f, .0011f, .0005f, .0011f, Blueprint, edge, Blueprint, "DimDash"));
            dimKnob ??= Own(ToyMeshes.Disc(.0062f, .0035f, Color.white, Blueprint, "DimKnob"));
            dimKnobDot ??= Own(ToyMeshes.Disc(.0024f, .001f, Blueprint, Blueprint, "DimKnobDot"));
            var v = new DimView { root = new GameObject("Dim " + name).transform };
            v.root.SetParent(transform, false);
            Transform Child(string n, Mesh m) { var p = Part(n, m); p.SetParent(v.root, false); return p; }
            v.bar = Child("Line", dimBar);
            v.arrowA = Child("Arrow A", dimArrow);
            v.arrowB = Child("Arrow B", dimArrow);
            v.label = PaperTag("Dim Label " + name, .086f, .024f, Color.white, out v.text, 12.5f, false); // F77: a paper label, not a pill
            v.text.color = Blueprint;
            v.label.SetParent(v.root, true);
            if (withKnob)
            {
                v.knob = Child("Knob", dimKnob);
                var dot = Part("Knob Dot", dimKnobDot);
                dot.SetParent(v.knob, false);
                dot.localPosition = new Vector3(0f, 0f, -.0022f);
            }
            var ringMesh = RingMesh();
            v.ringA = Child("Snap A", ringMesh);
            v.ringB = Child("Snap B", ringMesh);
            v.ringA.gameObject.SetActive(false);
            v.ringB.gameObject.SetActive(false);
            return v;
        }

        private Mesh ringMeshCache;
        private Mesh RingMesh()
        {
            if (ringMeshCache != null) return ringMeshCache;
            ringMeshCache = ToyMeshes.Torus(.18f, "DimSnapRing");
            Paint(ringMeshCache, Blueprint);
            owned.Add(ringMeshCache);
            return ringMeshCache;
        }

        private Mesh Own(Mesh mesh) { owned.Add(mesh); return mesh; }

        // Lays a dimension line from a to b, facing the person. `knobAt`: 0 none, 1 at a, 2 at b.
        private void DrawDim(DimView v, Vector3 a, Vector3 b, string label, int knobAt = 0, bool ringA = false, bool ringB = false,
                             Vector3? extA = null, Vector3? extB = null, Vector3? labelLift = null)
        {
            if (v == null) return;
            Vector3 d = b - a;
            float length = Mathf.Max(.001f, d.magnitude);
            Vector3 along = d / length;
            Vector3 mid = (a + b) * .5f;
            Vector3 toEye = Head != null ? (Head.position - mid).normalized : Vector3.back;
            Vector3 normal = Vector3.ProjectOnPlane(toEye, along);
            if (normal.sqrMagnitude < 1e-4f) normal = Vector3.ProjectOnPlane(Vector3.up, along);
            if (normal.sqrMagnitude < 1e-4f) normal = Vector3.forward;
            normal.Normalize();
            Quaternion rot = Quaternion.LookRotation(-normal, Vector3.Cross(-normal, along));
            v.root.SetPositionAndRotation(mid, rot);
            v.root.localScale = Vector3.one;
            const float arrowLen = .0075f;
            v.bar.localPosition = Vector3.zero;
            v.bar.localRotation = Quaternion.identity;
            v.bar.localScale = new Vector3(Mathf.Max(.0005f, length - arrowLen * 1.6f), 1f, 1f);
            v.arrowB.localPosition = new Vector3(length * .5f, 0f, 0f);
            v.arrowB.localRotation = Quaternion.identity;
            v.arrowA.localPosition = new Vector3(-length * .5f, 0f, 0f);
            v.arrowA.localRotation = Quaternion.Euler(0f, 0f, 180f);
            // A dot every centimetre (none at the ends).
            int count = Mathf.Clamp(Mathf.FloorToInt((length - .004f) / .01f), 0, 150);
            while (v.ticks.Count < count) { var tick = Part("Tick", dimTick); tick.SetParent(v.root, false); v.ticks.Add(tick); }
            for (int i = 0; i < v.ticks.Count; i++)
            {
                bool on = i < count;
                if (v.ticks[i].gameObject.activeSelf != on) v.ticks[i].gameObject.SetActive(on);
                if (on) v.ticks[i].localPosition = new Vector3(-length * .5f + (i + 1) * .01f, 0f, -.0012f);
            }
            // The pill above the middle, facing the eye.
            Vector3 lift = labelLift ?? Vector3.Cross(along, -normal) * .016f;
            if (Vector3.Dot(lift, Vector3.up) < 0f && labelLift == null) lift = -lift;
            v.label.position = mid + lift;
            v.label.rotation = SpeechBubbleDock.FacingAt(v.label.position);
            v.text.text = label;
            if (v.knob != null)
            {
                v.knob.gameObject.SetActive(knobAt != 0);
                v.knob.localPosition = new Vector3((knobAt == 1 ? -1f : 1f) * length * .5f, 0f, -.002f);
                v.knob.localRotation = Quaternion.identity;
            }
            Ring(v.ringA, ringA, a, rot);
            Ring(v.ringB, ringB, b, rot);
            // Dashed lines from snapped ends to the edges they took.
            int used = 0;
            void Dashes(Vector3 from, Vector3? to)
            {
                if (to == null) return;
                Vector3 span = to.Value - from;
                float dist = span.magnitude;
                if (dist < .008f) return;
                Vector3 dir = span / dist;
                for (float s = .004f; s < dist && used < 40; s += .0075f)
                {
                    if (used >= v.dashes.Count) { var dash = Part("Dash", dimDash); dash.SetParent(v.root, false); v.dashes.Add(dash); }
                    var p = v.dashes[used++];
                    p.gameObject.SetActive(true);
                    p.SetPositionAndRotation(from + dir * s, Quaternion.LookRotation(-toEye, Vector3.Cross(-toEye, dir).sqrMagnitude > 1e-6f ? Vector3.Cross(-toEye, dir) : Vector3.up) * Quaternion.Euler(0f, 0f, 90f));
                }
            }
            Dashes(a, extA);
            Dashes(b, extB);
            for (int i = used; i < v.dashes.Count; i++) if (v.dashes[i].gameObject.activeSelf) v.dashes[i].gameObject.SetActive(false);
        }

        private static void Ring(Transform ring, bool on, Vector3 at, Quaternion rot)
        {
            if (ring == null) return;
            if (ring.gameObject.activeSelf != on) ring.gameObject.SetActive(on);
            if (!on) return;
            ring.SetPositionAndRotation(at, rot * Quaternion.Euler(90f, 0f, 0f));
            ring.localScale = Vector3.one * (.0075f * (1f + .08f * Mathf.Sin(Time.time * 9f)));
        }

        private static void ShowDim(DimView v, bool on)
        {
            if (v != null && v.root != null && v.root.gameObject.activeSelf != on) v.root.gameObject.SetActive(on);
        }

        // ------------------------------------------------------------ the hand tape

        private void BuildTapeDim()
        {
            tapeDim = BuildDim("Tape", false);
            tapeBar = tapeDim.root;
            endA = tapeDim.arrowA;
            endB = tapeDim.arrowB;
            tapeLabelCard = tapeDim.label;
            tapeLabel = tapeDim.text;
            tapeDim.root.gameObject.SetActive(false);
        }

        // The axes as seen from the person: across (W), toward them (D), up (H).
        private void PersonAxes(Vector3 at, out Vector3 w, out Vector3 dAxis)
        {
            Vector3 toward = Head != null ? Vector3.ProjectOnPlane(Head.position - at, Vector3.up) : Vector3.back;
            dAxis = toward.sqrMagnitude > 1e-4f ? toward.normalized : Vector3.back;
            w = Vector3.Cross(Vector3.up, dAxis);
        }

        // Snaps the tape's ends: to an axis, then to edges nearby. Changes the tape's A and B.
        private void SnapTapeEnds()
        {
            Vector3 a = Tape.A, b = Tape.B;
            TapeAxis = null;
            bool wasA = TapeSnappedA, wasB = TapeSnappedB;
            TapeSnappedA = TapeSnappedB = false;
            tapeExtA = tapeExtB = Vector3.zero;
            Vector3 d = b - a;
            float length = d.magnitude;
            if (length < .01f) return;
            PersonAxes((a + b) * .5f, out Vector3 w, out Vector3 dAxis);
            var axes = new[] { ("W", w), ("D", dAxis), ("H", Vector3.up) };
            Vector3 axis = Vector3.zero;
            float best = Mathf.Cos(SnapAngle * Mathf.Deg2Rad);
            foreach (var (name, ax) in axes)
            {
                float c = Mathf.Abs(Vector3.Dot(d / length, ax));
                if (c < best) continue;
                best = c;
                axis = ax * Mathf.Sign(Vector3.Dot(d, ax));
                TapeAxis = name;
            }
            if (TapeAxis != null)
            {
                Vector3 mid = (a + b) * .5f;
                float half = Vector3.Dot(d, axis) * .5f;
                a = mid - axis * half;
                b = mid + axis * half;
                foreach (var box in SnapBoxes())
                {
                    if (!TapeSnappedA && SnapEnd(ref a, axis, box, out var ea)) { TapeSnappedA = true; tapeExtA = ea; }
                    if (!TapeSnappedB && SnapEnd(ref b, axis, box, out var eb)) { TapeSnappedB = true; tapeExtB = eb; }
                }
                // The desk top for heights (a desk edge's thickness, a height from the desk).
                if (TapeAxis == "H")
                {
                    if (!TapeSnappedA && SnapToDeskTop(ref a)) TapeSnappedA = true;
                    if (!TapeSnappedB && SnapToDeskTop(ref b)) TapeSnappedB = true;
                }
            }
            if ((TapeSnappedA && !wasA) || (TapeSnappedB && !wasB))
            {
                TapeSnaps++;
                ToySounds.Play(ToySounds.Cue.Pop, TapeSnappedB ? b : a, .3f, 1.9f);
            }
            Tape.Snap(a, b);
        }

        // Boxes the ends snap to: everything made, v2, and the object the person means.
        private IEnumerable<Bounds> SnapBoxes()
        {
            foreach (var made in MadeThings())
            {
                if (made == sizeBox) continue; // F75: the tape measures a side of the box anywhere, not its edges
                var r = made.GetComponentInChildren<MeshRenderer>();
                if (r != null) yield return r.bounds;
            }
            if (Draft != null && draftMesh != null)
            {
                var r = draftMesh.GetComponent<MeshRenderer>();
                if (r != null) yield return r.bounds;
            }
            if (Target != null && Target.onSurface && Target.width > .01f)
            {
                float h = Mathf.Max(.01f, Target.top.y - Target.basePoint.y);
                yield return new Bounds(Target.basePoint + Vector3.up * h * .5f, new Vector3(Target.width, h, Target.width));
            }
        }

        private static bool SnapEnd(ref Vector3 end, Vector3 axis, Bounds box, out Vector3 ext)
        {
            ext = Vector3.zero;
            Bounds near = box;
            near.Expand(.24f); // (12 cm around it: a hand held a little in front of the thing still snaps)
            if (!near.Contains(end)) return false;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) == 0 ? box.min.x : box.max.x, (i & 2) == 0 ? box.min.y : box.max.y, (i & 4) == 0 ? box.min.z : box.max.z);
                float s = Vector3.Dot(c, axis);
                lo = Mathf.Min(lo, s);
                hi = Mathf.Max(hi, s);
            }
            float at = Vector3.Dot(end, axis);
            float target = Mathf.Abs(at - lo) < Mathf.Abs(at - hi) ? lo : hi;
            if (Mathf.Abs(at - target) > SnapReach) return false;
            end += axis * (target - at);
            ext = box.ClosestPoint(end);
            return true;
        }

        private bool SnapToDeskTop(ref Vector3 end)
        {
            var surface = SurfaceAt(end + Vector3.up * .03f);
            if (surface == null) return false;
            float y = surface.Point(new Vector2(end.x, end.z)).y;
            if (Mathf.Abs(end.y - y) > SnapReach) return false;
            end.y = y;
            return true;
        }

        private string TapeText(float mm) => (TapeAxis != null ? TapeAxis + "  " : "") + mm.ToString("0.#") + " mm";

        // The tape keeps showing where it was measured for a while after it was kept.
        private void PinTape()
        {
            tapePinnedUntil = Time.time + TapePinSeconds;
            ShowTape(true);
            UpdateTapeVisual();
        }

        private void TapePinFrame()
        {
            if (tapePinnedUntil <= 0f || Time.time < tapePinnedUntil) return;
            tapePinnedUntil = 0f;
            if (Current != Step.Measuring && !freeTape && !tapeDemo) ShowTape(false);
        }

        // A tick for every centimetre the tape grows or shrinks (at most about 25 a second).
        private void TapeTicks(float length)
        {
            int cm = Mathf.FloorToInt(length * 100f);
            if (cm == tickCm) return;
            bool first = tickCm < 0;
            tickCm = cm;
            if (first || Time.time - tickSoundAt < .04f) return;
            tickSoundAt = Time.time;
            ToySounds.Play(ToySounds.Cue.Press, (Tape.A + Tape.B) * .5f, .07f, 2.5f);
        }

        // ------------------------------------------------------------ the one size that matters most

        private static readonly string[] TallThings =
        {
            "vase", "pen holder", "pencil holder", "pen cup", "pencil cup", "pen pot", "planter", "plant pot", "flower pot", "lamp", "shade",
            "tower", "bottle", "candle", "tumbler", "cup holder", "brush holder", "toothbrush", "utensil", "organizer", "organiser",
            "꽃병", "펜꽂이", "연필꽂이", "화분", "조명", "램프", "타워", "꽂이"
        };

        // "H" when the height matters most (tall things), "W" otherwise; a clip measures its desk edge.
        public string KeyAxis => Kind == "clip" ? "T" : PlannedKeyAxis ?? (TallThings.Any(w => (Need ?? "").ToLowerInvariant().Contains(w)) ? "H" : "W"); // F61: the size plan's first size

        // Tests: both hands pinch at a and b (the tape snaps as it would), then optionally let go (kept).
        public void SimulateTapeHands(Vector3 a, Vector3 b, bool letGo)
        {
            freeTape = true;
            freeShownUntil = Time.time + 6f;
            Tape.Step(Time.time, true, a, a, true, b, b);
            SnapTapeEnds();
            ShowTape(true);
            UpdateTapeVisual();
            if (!letGo) return;
            Tape.Step(Time.time + 1f, false, a, a, false, b, b);
            if (Tape.JustMeasured) TookFreeMeasure(Tape.Length * 1000f);
        }

        // The made thing's width (mm) from its key size: the height for tall things (the mesh's proportions).
        private float WidthFromKey(Mesh mesh, float keyMm)
        {
            if (mesh == null || KeyAxis != "H") return keyMm;
            var b = mesh.bounds;
            return keyMm * Mathf.Max(b.size.x, b.size.z) / Mathf.Max(1e-5f, b.size.y);
        }
    }
}
