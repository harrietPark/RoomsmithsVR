using System.Collections.Generic;
using UnityEngine;

namespace Windups.Handoff
{
    // How the place for the confirmed note in the middle of the crew looks (WindupsArrival.ringLook).
    // 2026-11-12 (the user picked draft A of the second round; mat/gear/stage and light pool/pushpin were
    // turned down):
    //  Ring   - the slim white ring (mint when the note is in), as before;
    //  Doodle - a hand-drawn circle on the desk: a light pencil loop that draws itself and "boils" like a
    //           hand-drawn animation while it waits; when a note settles in it, a mint marker inks over the
    //           pencil and a few emphasis strokes flick out round it (again for every new note).
    // The ring's transform keeps its old frame: the mesh lies in local XY with unit radius and local -Z up
    // (rotation Euler(90,0,0)); WindupsArrival scales it by the radius and hides it with TopicRing.enabled.
    // Sizes are real (mm) and rebuilt when the ring grows (without drawing the loop in again).
    public enum RingLook { Ring, Doodle }

    public sealed class TopicRingLook : MonoBehaviour
    {
        public RingLook Look { get; private set; }
        public bool Confirmed { get; private set; }
        // The marker has gone all round and the strokes are out.
        public bool Inked => Look == RingLook.Doodle && Confirmed && Time.time - changedAt > InkTime + TickTime + .05f;
        // Height above the ring's centre (6 mm above the surface) where a note's bottom rests.
        public float Rest => Look == RingLook.Ring ? .012f : -.004f;

        private const float Surface = -.006f, DrawTime = .45f, InkTime = .4f;
        private MeshRenderer view;
        private MeshFilter filter;
        private Material toy;
        private Mesh mesh;
        private Color[] colours;
        private Vector3[] shown;
        private float radius, changedAt = -10f, shownAt, toyVertexColour;
        private bool wasShown, dirtyColours, dirtyVertices, built;

        private static readonly Color White = new Color(.97f, .98f, 1f), Mint = new Color(.62f, 1f, .82f);

        public void Init(MeshRenderer renderer, Material ringMaterial)
        {
            view = renderer;
            filter = renderer.GetComponent<MeshFilter>();
            toy = ringMaterial;
            if (toy != null && toy.HasProperty("_VertexColor")) toyVertexColour = toy.GetFloat("_VertexColor");
        }

        public void Build(RingLook look, float ringRadius)
        {
            // A rebuild for a grown ring keeps the drawing as it is; a new look draws itself in.
            bool redraw = !built || look != Look;
            built = true;
            Look = look;
            radius = Mathf.Max(.05f, ringRadius);
            var old = mesh;
            mesh = look == RingLook.Doodle ? BuildDoodle() : Windups.ToyMeshes.Torus(Mathf.Clamp(.004f / radius, .012f, .05f), "TopicRing");
            mesh.MarkDynamic();
            colours = mesh.colors;
            shown = mesh.vertices;
            filter.sharedMesh = mesh;
            if (old != null) Destroy(old);
            view.sharedMaterial = toy;
            if (redraw) shownAt = Time.time;
            Paint();
            // Compose the new mesh now so a rebuilt doodle never shows a frame of its raw vertices.
            if (look == RingLook.Doodle) Refresh();
        }

        public void SetConfirmed(bool confirmed)
        {
            if (Confirmed != confirmed)
            {
                Confirmed = confirmed;
                changedAt = Time.time;
            }
            Paint();
        }

        // A new note settled in the ring: the doodle inks it again (confirms it if it was waiting).
        public void NoteSettled()
        {
            Confirmed = true;
            changedAt = Time.time;
            Paint();
        }

        private void Paint()
        {
            if (toy == null) return;
            bool ring = Look == RingLook.Ring;
            if (toy.HasProperty("_BaseColor")) toy.SetColor("_BaseColor", ring ? (Confirmed ? Mint : White) : Color.white);
            if (toy.HasProperty("_ShadeColor")) toy.SetColor("_ShadeColor", ring ? new Color(.86f, .88f, .92f) : new Color(.86f, .86f, .92f));
            if (toy.HasProperty("_VertexColor")) toy.SetFloat("_VertexColor", ring ? toyVertexColour : 1f);
            // 2026-11-13: on the crew's shader (Toy Character) the vertex colours here are already linear (Lin).
            if (toy.HasProperty("_VertexSRGB")) toy.SetFloat("_VertexSRGB", 0f);
        }

        private void LateUpdate()
        {
            if (view == null || mesh == null) return;
            bool showing = view.enabled && view.gameObject.activeInHierarchy;
            if (!showing) { wasShown = false; return; }
            if (!wasShown) { wasShown = true; shownAt = Time.time; }
            if (Look == RingLook.Doodle) Refresh();
        }

        private void Refresh()
        {
            AnimateDoodle();
            if (dirtyVertices) { mesh.SetVertices(shown); mesh.RecalculateBounds(); dirtyVertices = false; }
            if (dirtyColours) { mesh.SetColors(colours); dirtyColours = false; }
        }

        private void OnDestroy()
        {
            if (mesh != null) Destroy(mesh);
        }

        // Vertex colours reach the shader as they are (no sRGB conversion like material colours get), so the
        // colours here (picked as sRGB) are converted in a linear project.
        private static Color Lin(Color c) => QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;

        // Real size (y up) -> the ring's frame (unit radius in local XY, local -Z up).
        private Vector3 Local(Vector3 p) => new Vector3(p.x, p.z, -p.y) / radius;

        // ------------------------------------------------------------ A. the doodle

        private const int LoopSegments = 150, Ticks = 0, TickSegments = 6;
        private const float TickTime = .18f + Ticks * .04f;
        private static readonly Color Pencil = new Color(.80f, .84f, .93f), Ink = new Color(.33f, .84f, .60f);
        private Vector3[][] pencil;
        private Vector3[] inked;
        private int boilFrame = -1;
        private float composedAt = -1f;

        private Mesh BuildDoodle()
        {
            pencil = new Vector3[3][];
            for (int v = 0; v < 3; v++) pencil[v] = DoodleVertices(.0035f, Surface + .0008f, v, false);
            inked = (Vector3[])pencil[0].Clone(); // F50: just the circle, no marker halo or rays
            var m = new Mesh { name = "TopicRing Doodle" };
            m.vertices = (Vector3[])pencil[0].Clone();
            var normals = new Vector3[inked.Length];
            for (int i = 0; i < normals.Length; i++) normals[i] = new Vector3(0f, 0f, -1f);
            m.normals = normals;
            var c = new Color[inked.Length];
            for (int i = 0; i < c.Length; i++) c[i] = Lin(Pencil);
            m.colors = c;
            var tris = new List<int>();
            void Strip(int start, int segments)
            {
                for (int i = 0; i < segments; i++)
                {
                    int a = start + 2 * i;
                    tris.AddRange(new[] { a, a + 1, a + 2, a + 1, a + 3, a + 2 });
                }
            }
            Strip(0, LoopSegments);
            for (int t = 0; t < Ticks; t++) Strip(2 * (LoopSegments + 1) + t * 2 * (TickSegments + 1), TickSegments);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            boilFrame = -1;
            composedAt = -1f;
            return m;
        }

        // A quick hand-drawn loop (a little more than one turn, wobbly, its ends tapered) and the emphasis
        // strokes round it. `variant` changes the wobble (the pencil's three boil frames, or the ink).
        private Vector3[] DoodleVertices(float width, float y, int variant, bool ink)
        {
            var rng = new System.Random(11 + variant * 31);
            float R() => (float)rng.NextDouble();
            // The pencil's boil frames differ a little in their slow wobble too (about 1-2 mm at a 10 cm ring).
            float jitter = ink ? 0f : .3f;
            float p1 = 1.3f + (ink ? .4f : (R() - .5f) * jitter), p2 = 4.1f + (R() - .5f) * jitter, p3 = .7f;
            float q = R() * 6.283f, wobble = ink ? .006f : .004f + R() * .002f;
            var v = new Vector3[2 * (LoopSegments + 1) + Ticks * 2 * (TickSegments + 1)];
            const float start = 1.9f;
            for (int i = 0; i <= LoopSegments; i++)
            {
                float u = i / (float)LoopSegments, a = start + u * Mathf.PI * 2f * (ink ? 1.08f : 1.12f);
                float rr = radius * (1f + .03f * Mathf.Sin(2f * a + p1) + .018f * Mathf.Sin(3f * a + p2) + .008f * Mathf.Sin(5f * a + p3)
                                     + wobble * Mathf.Sin(7f * a + q)) * (1f - .045f * u);
                var out1 = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                var c = out1 * rr + Vector3.up * y;
                float w = width * (.35f + .65f * Mathf.SmoothStep(0f, 1f, u / .08f) * Mathf.SmoothStep(0f, 1f, (1f - u) / .14f));
                v[2 * i] = Local(c - out1 * w * .5f);
                v[2 * i + 1] = Local(c + out1 * w * .5f);
            }
            // Emphasis strokes: short flicks outward, unevenly spaced, a little off radial.
            var tr = new System.Random(5);
            for (int t = 0; t < Ticks; t++)
            {
                float a = start + .5f + t * Mathf.PI * 2f / Ticks + ((float)tr.NextDouble() - .5f) * .35f;
                float skew = ((float)tr.NextDouble() - .5f) * .4f, length = .016f + (float)tr.NextDouble() * .007f;
                var dir = new Vector3(Mathf.Cos(a + skew), 0f, Mathf.Sin(a + skew));
                var side = new Vector3(-dir.z, 0f, dir.x);
                var from = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (radius + .009f) + Vector3.up * (y + .0002f);
                int baseIndex = 2 * (LoopSegments + 1) + t * 2 * (TickSegments + 1);
                for (int s = 0; s <= TickSegments; s++)
                {
                    float u = s / (float)TickSegments;
                    float w = .0062f * Mathf.Lerp(1f, .35f, u);
                    var c = from + dir * length * u;
                    v[baseIndex + 2 * s] = Local(c - side * w * .5f);
                    v[baseIndex + 2 * s + 1] = Local(c + side * w * .5f);
                }
            }
            return v;
        }

        private void AnimateDoodle()
        {
            float now = Time.time;
            float appear = Mathf.Clamp01((now - shownAt) / DrawTime);
            float ink = Confirmed ? Mathf.Clamp01((now - changedAt) / InkTime) : 0f;
            int frame = Confirmed && ink >= 1f ? -2 : Mathf.FloorToInt(now * 6f) % 3;
            float ticks = Confirmed ? Mathf.Clamp01((now - changedAt - InkTime) / TickTime) : 0f;
            bool moving = appear < 1f || (Confirmed && (ink < 1f || ticks < 1f));
            if (!moving && frame == boilFrame && composedAt >= 0f) return;
            boilFrame = frame;
            composedAt = now;
            var pen = pencil[Mathf.Max(0, Mathf.FloorToInt(now * 6f) % 3)];
            int drawn = Mathf.FloorToInt(appear * LoopSegments);
            Color pencilColour = Lin(Pencil), inkColour = Lin(Pencil);
            for (int i = 0; i <= LoopSegments; i++)
            {
                float u = i / (float)LoopSegments;
                bool inkHere = Confirmed && u <= ink;
                var src = inkHere ? inked : (Confirmed ? pencil[0] : pen);
                int k = Mathf.Min(i, drawn);
                shown[2 * i] = src[2 * k];
                shown[2 * i + 1] = i > drawn ? src[2 * k] : src[2 * i + 1];
                colours[2 * i] = colours[2 * i + 1] = inkHere ? inkColour : pencilColour;
            }
            for (int t = 0; t < Ticks; t++)
            {
                int baseIndex = 2 * (LoopSegments + 1) + t * 2 * (TickSegments + 1);
                float p = Mathf.Clamp01(ticks * (1f + Ticks * .25f) - t * .25f);
                int reach = Mathf.FloorToInt(p * TickSegments);
                for (int s = 0; s <= TickSegments; s++)
                {
                    int k = Mathf.Min(s, reach);
                    shown[baseIndex + 2 * s] = inked[baseIndex + 2 * k];
                    shown[baseIndex + 2 * s + 1] = p <= 0f || s > reach ? inked[baseIndex + 2 * k] : inked[baseIndex + 2 * s + 1];
                    colours[baseIndex + 2 * s] = colours[baseIndex + 2 * s + 1] = inkColour;
                }
            }
            dirtyVertices = dirtyColours = true;
        }
    }
}
