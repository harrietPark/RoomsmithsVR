using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Windups.Handoff
{
    // F41 (2026-11-12): a desk clip goes onto the real desk edge.
    //  - Words like clip, hook, clamp, cable, 걸이 make a clip again (the F30 shape the app builds to the measured
    //    desk thickness; everything else is still made by Meshy): Spark's colours, the desk edge's thickness
    //    (a ruler's number is best), then hook, cable clip or little shelf.
    //  - On an edge it slides along it only. Pulled more than EdgePullOff away it comes off and is carried like
    //    anything else (F38). Brought within EdgeSnapReach of any desk edge (any MRUK table, storage or couch
    //    top), a see-through clip shows where it would go; let go, it slides on with a click.
    //  - An invisible slab the measured thickness hides the part of the clip behind the real desk (its lower
    //    bar seen from above), so it looks really clamped.
    //  - For printing: a flared mouth and a small snap bump (0.3 mm into the desk) so PETG clicks on; it is
    //    printed lying on its side (the bend along the layers is strong) and Fixer says so.
    public sealed partial class MakerSession
    {
        public const float EdgePullOff = .06f, EdgeSnapReach = .05f;
        public int ClipSnaps { get; private set; }
        public int ClipUnsnaps { get; private set; }
        public bool ClipGhostShown => clipGhost != null && clipGhost.gameObject.activeSelf;

        private readonly Dictionary<Transform, CoasterDesign> madeDesigns = new Dictionary<Transform, CoasterDesign>();
        private readonly Dictionary<Transform, Vector2> railSpan = new Dictionary<Transform, Vector2>();
        private Transform clipGhost;
        private bool snapReady;
        private Vector3 snapPoint, snapAlong;
        private Quaternion snapTurn;
        private Vector2 snapSpan;

        private bool IsClip(Transform thing) => thing != null && madeDesigns.TryGetValue(thing, out var d) && d != null && d.kind == "clip";
        public bool OnEdge(Transform thing) => OnRail(thing);

        // The held thing on its edge: slides along it, or comes off when pulled away. A free clip shows where
        // it would click on. Returns true when it placed the thing itself.
        private bool HoldOnEdge(Vector3 target, Quaternion rot)
        {
            if (held == null) return false;
            if (rails.TryGetValue(held, out var rail))
            {
                Vector3 along = rail.along.normalized;
                float s = Vector3.Dot(target - rail.point, along);
                if (Vector3.Distance(target, rail.point + along * s) > EdgePullOff)
                {
                    Unclip(held);
                    MoveHeld(target, rot);
                    return true;
                }
                Vector2 span = railSpan.TryGetValue(held, out var sp) ? sp : new Vector2(-.6f, .6f);
                held.SetPositionAndRotation(rail.point + along * Mathf.Clamp(s, span.x, span.y), rail.turn);
                if (heldBody != null) { heldBody.position = held.position; heldBody.rotation = held.rotation; }
                return true;
            }
            if (!IsClip(held)) return false;
            MoveHeld(target, rot);
            PreviewClipSnap(held);
            return true;
        }

        private void Unclip(Transform thing)
        {
            rails.Remove(thing);
            railSpan.Remove(thing);
            SetOccluder(thing, false);
            ClipUnsnaps++;
            ToySounds.Play(ToySounds.Cue.Peel, thing.position, .45f, 1.3f);
            Talk.Add("Person pulled the clip off the desk edge");
        }

        // The nearest desk edge to a point: an edge of a MRUK top (not the floor), within reach across and in height.
        public bool NearestEdge(Vector3 pos, float reach, out Vector3 point, out Vector3 outward, out Vector3 along, out Vector2 ends)
        {
            point = outward = along = Vector3.zero;
            ends = Vector2.zero;
            var room = arrival != null ? arrival.handoff.meeting.environment : null;
            if (room == null) return false;
            float best = float.MaxValue;
            var q = new Vector2(pos.x, pos.z);
            foreach (var surface in room.Surfaces)
            {
                var poly = surface.Polygon;
                if (surface.IsFloor || poly == null || poly.Length < 3) continue;
                Vector2 centroid = poly.Aggregate(Vector2.zero, (s, p) => s + p) / poly.Length;
                for (int i = 0; i < poly.Length; i++)
                {
                    Vector2 a = poly[i], b = poly[(i + 1) % poly.Length], ab = b - a;
                    float len = ab.magnitude;
                    if (len < .05f) continue;
                    float t = Mathf.Clamp01(Vector2.Dot(q - a, ab) / (len * len));
                    Vector2 c = a + ab * t;
                    float across = (q - c).magnitude;
                    float y = surface.HeightAt(c);
                    float dy = Mathf.Abs(pos.y - y);
                    float score = across + dy * .5f;
                    if (across > reach || dy > .08f || score >= best) continue;
                    best = score;
                    Vector2 n = new Vector2(ab.y, -ab.x) / len;
                    if (Vector2.Dot(n, c - centroid) < 0f) n = -n;
                    point = new Vector3(c.x, y, c.y);
                    outward = new Vector3(n.x, 0f, n.y);
                    along = Vector3.Cross(outward, Vector3.up).normalized;
                    float sa = Vector3.Dot(new Vector3(a.x, y, a.y) - point, along), sb = Vector3.Dot(new Vector3(b.x, y, b.y) - point, along);
                    ends = new Vector2(Mathf.Min(sa, sb), Mathf.Max(sa, sb));
                }
            }
            return best < float.MaxValue;
        }

        private void PreviewClipSnap(Transform clip)
        {
            snapReady = false;
            var design = madeDesigns.TryGetValue(clip, out var d) ? d : Design;
            // The clip's mouth is at its pivot (the desk's top front edge): look for an edge near it.
            if (design != null && NearestEdge(clip.position, EdgeSnapReach, out var p, out var outward, out var along, out var ends))
            {
                float margin = Mathf.Max(10f, design.width * .5f + 5f) * .001f;
                snapReady = ends.y - ends.x > margin * 2f;
                if (snapReady)
                {
                    snapPoint = p;
                    snapAlong = along;
                    snapTurn = Quaternion.LookRotation(along, Vector3.up); // its right is the edge's outward side
                    snapSpan = new Vector2(ends.x + margin, ends.y - margin);
                }
            }
            if (!snapReady) { if (clipGhost != null) clipGhost.gameObject.SetActive(false); return; }
            var filter = clip.GetComponentInChildren<MeshFilter>();
            if (clipGhost == null)
            {
                var go = new GameObject("Clip Ghost", typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(transform, false);
                go.GetComponent<MeshRenderer>().sharedMaterial = Hologram();
                go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                clipGhost = go.transform;
            }
            clipGhost.GetComponent<MeshFilter>().sharedMesh = filter != null ? filter.sharedMesh : null;
            clipGhost.gameObject.SetActive(true);
            clipGhost.SetPositionAndRotation(snapPoint, snapTurn);
        }

        // Let go near an edge: it slides onto it with a click and stays clamped there.
        private bool SnapOnEdgeAtRelease(Transform thing)
        {
            bool ready = snapReady && IsClip(thing);
            snapReady = false;
            if (clipGhost != null) clipGhost.gameObject.SetActive(false);
            if (!ready) return false;
            rails[thing] = (snapPoint, snapAlong, snapTurn);
            railSpan[thing] = snapSpan;
            StartCoroutine(SlideOn(thing, snapPoint, snapTurn));
            SetOccluder(thing, true);
            ClipSnaps++;
            Talk.Add("Person clipped it onto the desk edge");
            Ack(Crew.Fixer, "Click! It's on.");
            return true;
        }

        private IEnumerator SlideOn(Transform thing, Vector3 to, Quaternion turn)
        {
            Vector3 from = thing.position;
            Quaternion fromTurn = thing.rotation;
            for (float t = 0f; t < 1f && thing != null && held != thing; t += Time.deltaTime / .2f)
            {
                thing.SetPositionAndRotation(Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t)), Quaternion.Slerp(fromTurn, turn, t));
                yield return null;
            }
            if (thing == null || held == thing) yield break;
            thing.SetPositionAndRotation(to, turn);
            ToySounds.Play(ToySounds.Cue.Press, to, .7f, .85f);
            ToySounds.Play(ToySounds.Cue.Pop, to, .35f, 2f);
        }

        // The invisible desk slab under a clip (only while it is on an edge).
        private void SetOccluder(Transform clip, bool on)
        {
            if (clip == null) return;
            var existing = clip.Find("Desk Occluder");
            if (!on) { if (existing != null) existing.gameObject.SetActive(false); return; }
            var d = madeDesigns.TryGetValue(clip, out var known) ? known : null;
            var mat = Resources.Load<Material>("MakerDepthOnly");
            if (d == null || d.kind != "clip" || mat == null) return;
            Transform slab = existing;
            if (slab == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                DestroyImmediate(go.GetComponent<Collider>()); // (now: the held clip's own collider must stay its only one)
                go.name = "Desk Occluder";
                go.GetComponent<MeshRenderer>().sharedMaterial = mat;
                go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                slab = go.transform;
                slab.SetParent(clip, false);
            }
            // In the clip's frame (mm): the desk is u < 0 (x), -thickness < v < 0 (y); it spans the clip's width (z).
            float desk = d.cupDiameter, reach = d.grip + 12f, width = d.width + 40f;
            slab.localPosition = new Vector3(-(reach * .5f + .3f), -desk * .5f, 0f) * .001f;
            slab.localRotation = Quaternion.identity;
            slab.localScale = new Vector3(reach, Mathf.Max(1f, desk - .6f), width) * .001f;
            slab.gameObject.SetActive(true);
        }

        // The clip's colour from the colour inputs (a swatch, else a colour word), else the palette, else natural.
        private string ClipColour()
        {
            var swatch = InputSwatches.FirstOrDefault(s => s.kind != "texture") ?? InputSwatches.FirstOrDefault();
            if (swatch != null && !string.IsNullOrEmpty(swatch.hex)) return swatch.hex;
            string w = ((ColourWords ?? "") + " " + (Style ?? "")).ToLowerInvariant();
            var words = new (string word, string hex)[]
            {
                ("navy", "#25356b"), ("blue", "#3f74d6"), ("mint", "#9fe0c4"), ("green", "#4fae66"), ("yellow", "#f2c94c"), ("orange", "#f08a3c"),
                ("pink", "#f19bb8"), ("purple", "#8e6bd1"), ("red", "#d9534f"), ("black", "#2b2b30"), ("white", "#f4f2ee"), ("grey", "#9a9ca3"),
                ("gray", "#9a9ca3"), ("brown", "#8a5a3b"), ("beige", "#e2cfa8"), ("wood", "#c89a64"),
                ("남색", "#25356b"), ("파란", "#3f74d6"), ("파랑", "#3f74d6"), ("민트", "#9fe0c4"), ("초록", "#4fae66"), ("노란", "#f2c94c"), ("노랑", "#f2c94c"),
                ("주황", "#f08a3c"), ("분홍", "#f19bb8"), ("보라", "#8e6bd1"), ("빨간", "#d9534f"), ("빨강", "#d9534f"), ("검정", "#2b2b30"), ("검은", "#2b2b30"),
                ("흰", "#f4f2ee"), ("하얀", "#f4f2ee"), ("회색", "#9a9ca3"), ("갈색", "#8a5a3b"), ("베이지", "#e2cfa8"), ("나무", "#c89a64")
            };
            foreach (var (word, hex) in words) if (w.Contains(word)) return hex;
            return Palette.LastOrDefault(s => s.kind == "colour")?.hex ?? "#e8d2a8";
        }

        // Printed lying on its side: the clip's width (z) becomes the printer's up, so the bend is along the layers.
        private static Mesh PrintOriented(Mesh mesh, CoasterDesign d)
        {
            if (mesh == null || d == null || d.kind != "clip") return mesh;
            var copy = Object.Instantiate(mesh);
            var v = copy.vertices;
            float minY = float.MaxValue;
            for (int i = 0; i < v.Length; i++) { v[i] = new Vector3(v[i].x, v[i].z, -v[i].y); minY = Mathf.Min(minY, v[i].y); }
            for (int i = 0; i < v.Length; i++) v[i].y -= minY;
            copy.vertices = v;
            copy.RecalculateNormals();
            copy.RecalculateBounds();
            return copy;
        }

        // Tests: set the clip at an edge point as if carried and let go there.
        public void SimulateClipRelease(Transform clip, Vector3 at)
        {
            Grab(null, clip.position, clip);
            mouseHolding = true;
            for (int i = 1; i <= 10 && held == clip; i++) Hold(Vector3.Lerp(clip.position, at, i / 10f), Quaternion.identity);
            LetGo();
        }
    }
}
