using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Windups.Handoff
{
    // 2026-11-15 (Quest: "when I'm not doing anything with the thing, you could show its part tags - I couldn't tell
    // what to do to see them"): while no hand has been near the made thing (IdleTagsReach) for IdleTagsAfter, each of
    // its named parts (not the rest, "body") shows a small paper tag just in front of it, toward the person. A hand
    // coming near puts them away: then the touched part lights with its own tag and knob (F83), as before. Nothing
    // shows while it is held, sculpted, carved or rebuilt. Where each part sits is worked out once per mesh in a Task.
    public sealed partial class MakerSession
    {
        public static bool IdlePartTags = true;
        public const float IdleTagsReach = .15f, IdleTagsAfter = .8f, IdleTagScale = 1f;

        // F102 (Quest 2026-11-16, the user: "the part name tags don't show - not white, another colour; and sometimes
        // they are stuck inside the parts"): the tags were white 3.6 cm paper placed 2.5 cm in front of the part's middle,
        // which for a thick part is still inside the thing. Now they are the part outline's coral with white bold words,
        // bigger, and stand PartTagOut in front of the first surface of the thing between the eyes and the part (its
        // convex collider, else its bounds), so nothing of the thing is ever in front of them.
        public const float PartTagW = .05f, PartTagH = .017f, PartTagWords = 10f, PartTagOut = .012f;
        public static readonly Color PartTagColour = new Color(1f, .45f, .36f, 1f); // Parts90Coral
        private readonly List<Collider> tagColliders = new List<Collider>();

        private Transform MakePartTag(string name, out TextMeshProUGUI text)
        {
            var root = PaperTag(name, PartTagW, PartTagH, PartTagColour, out text, PartTagWords, false);
            if (text != null) { text.color = Color.white; text.fontStyle = FontStyles.Bold; }
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = ShadowCastingMode.Off;
            return root;
        }

        // The first surface of `thing` on the line from the eyes to `point` (the point itself when nothing is in front).
        private Vector3 FrontOf(Component thing, Bounds bounds, Vector3 point, Vector3 eyes)
        {
            Vector3 d = point - eyes;
            float far = d.magnitude;
            if (thing == null || far < 1e-3f) return point;
            var ray = new Ray(eyes, d / far);
            float best = float.MaxValue;
            tagColliders.Clear();
            thing.GetComponentsInChildren(false, tagColliders);
            for (int i = 0; i < tagColliders.Count; i++)
            {
                var c = tagColliders[i];
                if (c != null && c.enabled && c.Raycast(ray, out RaycastHit hit, far + .01f) && hit.distance < best) best = hit.distance;
            }
            if (best == float.MaxValue && bounds.IntersectRay(ray, out float enter) && enter > 0f && enter < far) best = enter;
            return best < far ? ray.GetPoint(best) : point;
        }
        public const int IdleTagsMost = 6;
        public int IdleTagsShown { get; private set; }
        // Checks (F102): the tags on show now.
        public List<Transform> IdleTagViews()
        {
            var shown = new List<Transform>();
            foreach (var (root, _) in idleTags) if (root != null && root.gameObject.activeInHierarchy) shown.Add(root);
            return shown;
        }
        public string IdleTagNames => string.Join(", ", idleNames);

        private readonly List<(Transform root, TextMeshProUGUI text)> idleTags = new List<(Transform, TextMeshProUGUI)>();
        private readonly List<string> idleNames = new List<string>();
        private readonly List<Vector3> idlePoints = new List<Vector3>(); // the parts' middles, the thing's own space
        private readonly List<float> idleGrow = new List<float>();
        private Mesh idleMesh, idleMeasuringMesh;
        private PaintableThing idleThing;
        private float handsAwaySince = -1f, idleHandsCheckAt;

        // From PartsFrame, every frame.
        private void IdlePartTagsFrame()
        {
            var thing = IdleTagThing();
            bool want = thing != null;
            if (want && Time.time >= idleHandsCheckAt)
            {
                idleHandsCheckAt = Time.time + .1f;
                if (HandNear(thing.Renderer.bounds, IdleTagsReach)) handsAwaySince = -1f;
                else if (handsAwaySince < 0f) handsAwaySince = Time.time;
            }
            want &= handsAwaySince >= 0f && Time.time - handsAwaySince >= IdleTagsAfter;
            if (!want) { HideIdleTags(); return; }
            if (thing != idleThing || thing.Mesh != idleMesh) { MeasureIdleParts(thing); HideIdleTags(); return; }
            ShowIdleTags(thing);
        }

        // The current made thing, when it has parts of its own and is just standing there.
        private PaintableThing IdleTagThing()
        {
            if (!IdlePartTags || Product == null || !Product.gameObject.activeInHierarchy || HiddenOnPlate(Product) || (Sculpting && !KnobOnlyWhenSculpting) || partKnobHand != null || HoverLive) return null; // (2026-11-15: also in Sculpt it's mode)
            if (Modelling || Sculpt != SculptStage.None || Drawing || holdHand != null) return null;
            var thing = Product.GetComponentInChildren<PaintableThing>();
            if (thing == null || thing.Renderer == null || !thing.Renderer.enabled || thing.Mesh == null || thing.Parts == null || thing.Parts.Length < 2) return null;
            return HasOwnParts(thing) ? thing : null;
        }

        private bool HandNear(Bounds bounds, float reach)
        {
            if (PartTipOverride.HasValue) return bounds.SqrDistance(PartTipOverride.Value) < reach * reach;
            foreach (var hand in HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !HandIO.Connected(hand)) continue;
                if (HandIO.Joint(hand, HandJointId.HandIndexTip, out Pose tip) && bounds.SqrDistance(tip.position) < reach * reach) return true;
                if (HandIO.Joint(hand, HandJointId.HandPalm, out Pose palm) && bounds.SqrDistance(palm.position) < reach * reach) return true;
            }
            return false;
        }

        // Each named part's middle (area-weighted), off the main thread; the mesh's arrays are copied here once.
        private void MeasureIdleParts(PaintableThing thing)
        {
            var mesh = thing.Mesh;
            if (mesh == null || idleMeasuringMesh == mesh) return;
            idleMeasuringMesh = mesh;
            var v = mesh.vertices;
            int count = Mathf.Min(mesh.subMeshCount, thing.Parts.Length);
            var parts = new int[count][];
            for (int k = 0; k < count; k++) parts[k] = mesh.GetTriangles(k);
            var names = (string[])thing.Parts.Clone();
            StartCoroutine(MeasureIdlePartsLater(thing, mesh, v, parts, names));
        }

        private IEnumerator MeasureIdlePartsLater(PaintableThing thing, Mesh mesh, Vector3[] v, int[][] parts, string[] names)
        {
            var work = Task.Run(() =>
            {
                // one tag per name ("wing", "wing 2": one Wing), on its biggest piece
                var best = new Dictionary<string, (Vector3 at, double area, int order)>();
                for (int k = 0; k < parts.Length; k++)
                {
                    string name = PartField.BaseName(names[k]);
                    if (string.IsNullOrEmpty(name) || name == PartField.Rest) continue;
                    var t = parts[k];
                    Vector3 sum = Vector3.zero;
                    double area = 0.0;
                    for (int i = 0; i + 2 < t.Length; i += 3)
                    {
                        Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                        float w = Vector3.Cross(b - a, c - a).magnitude;
                        sum += (a + b + c) * (w / 3f);
                        area += w;
                    }
                    if (area <= 1e-12) continue;
                    if (!best.TryGetValue(name, out var had)) best[name] = (sum / (float)area, area, k);
                    else if (area > had.area) best[name] = (sum / (float)area, area, had.order);
                }
                var found = new List<(string name, Vector3 at)>();
                foreach (var pair in best) found.Add((pair.Key, pair.Value.at));
                found.Sort((a, b) => best[a.name].order.CompareTo(best[b.name].order));
                return found;
            });
            while (!work.IsCompleted) yield return null;
            if (idleMeasuringMesh == mesh) idleMeasuringMesh = null;
            if (work.IsFaulted || thing == null || thing.Mesh != mesh) yield break;
            idleThing = thing;
            idleMesh = mesh;
            idleNames.Clear();
            idlePoints.Clear();
            foreach (var (name, at) in work.Result)
            {
                if (idleNames.Count >= IdleTagsMost) break;
                idleNames.Add(Capital(name));
                idlePoints.Add(at);
            }
        }

        private void ShowIdleTags(PaintableThing thing)
        {
            var t = thing.transform;
            Vector3 middle = thing.Renderer.bounds.center;
            Vector3 eyes = Head != null ? Head.position : middle + Vector3.back;
            IdleTagsShown = 0;
            for (int i = 0; i < idleNames.Count; i++)
            {
                if (i >= idleTags.Count)
                {
                    var root = MakePartTag("Idle Part Tag", out var text);
                    idleTags.Add((root, text));
                    idleGrow.Add(0f);
                }
                var (tag, label) = idleTags[i];
                if (label != null && label.text != idleNames[i]) label.text = idleNames[i];
                Vector3 part = t.TransformPoint(idlePoints[i]);
                Vector3 toEyes = (eyes - part).normalized;
                Vector3 outward = Vector3.ProjectOnPlane(part - middle, toEyes);
                // F102: in front of the first surface between the eyes and the part, nudged out from the middle a little
                Vector3 front = FrontOf(thing, thing.Renderer.bounds, part, eyes);
                Vector3 at = front + (eyes - front).normalized * PartTagOut + (outward.sqrMagnitude > 1e-6f ? outward.normalized * .01f : Vector3.zero) + Vector3.up * .006f;
                // not over a tag placed before it (as the person sees them): a step up until it is clear
                // (F102: across and up as the eyes see them - looking down at a desk, world-up is mostly depth)
                Vector3 look = -toEyes;
                Vector3 side = Vector3.Cross(Vector3.up, look).normalized;
                Vector3 viewUp = Vector3.Cross(look, side).normalized;
                for (int tries = 0; tries < 6; tries++)
                {
                    bool clear = true;
                    for (int j = 0; j < i; j++)
                    {
                        // (F102: as the eyes see it - tags stand at different depths now)
                        Vector3 other = idleTags[j].root.position;
                        float far = Vector3.Distance(eyes, other);
                        Vector3 seen = far > 1e-3f ? eyes + (other - eyes) * (Vector3.Distance(eyes, at) / far) : other;
                        Vector3 d = at - seen;
                        if (Mathf.Abs(Vector3.Dot(d, side)) < PartTagW * IdleTagScale && Mathf.Abs(Vector3.Dot(d, viewUp)) < (PartTagH + .001f) * IdleTagScale) { clear = false; break; }
                    }
                    if (clear) break;
                    at += viewUp * (PartTagH + .002f) * IdleTagScale;
                }
                idleGrow[i] = Mathf.MoveTowards(idleGrow[i], 1f, Time.deltaTime * 5f);
                tag.SetPositionAndRotation(at, SpeechBubbleDock.FacingAt(at));
                tag.localScale = Vector3.one * IdleTagScale * Mathf.SmoothStep(0f, 1f, idleGrow[i]);
                if (!tag.gameObject.activeSelf) tag.gameObject.SetActive(true);
                IdleTagsShown++;
            }
            for (int i = idleNames.Count; i < idleTags.Count; i++) if (idleTags[i].root != null && idleTags[i].root.gameObject.activeSelf) idleTags[i].root.gameObject.SetActive(false);
        }

        private void HideIdleTags()
        {
            IdleTagsShown = 0;
            for (int i = 0; i < idleTags.Count; i++)
            {
                idleGrow[i] = 0f;
                var root = idleTags[i].root;
                if (root != null && root.gameObject.activeSelf) root.gameObject.SetActive(false);
            }
        }
    }
}
